using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Budiff.App;
using Budiff.Git;
using LibGit2Sharp;
using Xunit;

namespace Budiff.Avalonia.Tests;

public sealed class PullRequestModeTests
{
    [Fact]
    public async Task Picker_DefaultsToMine_TenAtATime_AndPagesUntilExhausted()
    {
        var source = new FakePullRequestSource(Enumerable.Range(1, 25).Select(n => Pr(n, "victor")).Concat(new[] { Pr(90, "ada") }));
        var vm = new PullRequestPickerViewModel(source);

        await vm.InitializeAsync();
        Assert.Equal("@me", source.Queries[0].Author);
        Assert.Equal(10, vm.Items.Count);
        Assert.True(vm.HasMore);
        Assert.Equal("10 of 25", vm.StatusText);
        Assert.Same(vm.Items[0], vm.SelectedItem);

        await vm.LoadMoreAsync();
        await vm.LoadMoreAsync();
        Assert.Equal(25, vm.Items.Count);
        Assert.False(vm.HasMore);
        Assert.Equal(Enumerable.Range(1, 25), vm.Items.Select(i => i.PullRequest.Number));

        var calls = source.Queries.Count;
        await vm.LoadMoreAsync();
        Assert.Equal(calls, source.Queries.Count);
    }

    [Fact]
    public async Task Picker_AuthorAcceptsNamesLoginsAndAnyone_StateAndTextNarrow()
    {
        var source = new FakePullRequestSource(new[] { Pr(1, "victor"), Pr(2, "ada", "MERGED"), Pr(3, "ada"), Pr(4, "grace") });
        var vm = new PullRequestPickerViewModel(source);
        await vm.InitializeAsync();
        await vm.LoadPeopleAsync();

        // A person's name resolves to their login.
        vm.AuthorText = "Ada";
        await vm.SearchNowAsync();
        Assert.Equal("ada", source.Queries[^1].Author);
        Assert.Equal(new[] { 2, 3 }, vm.Items.Select(i => i.PullRequest.Number));

        vm.SelectedState = vm.States.Single(s => s.Value == PullRequestStateFilter.Merged);
        await WaitFor(() => source.Queries[^1].State == PullRequestStateFilter.Merged && !vm.IsLoading);
        Assert.Equal(new[] { 2 }, vm.Items.Select(i => i.PullRequest.Number));

        vm.SelectedState = vm.States[0];
        vm.AuthorText = "anyone";
        vm.SearchText = "#4";
        await vm.SearchNowAsync();
        Assert.Null(source.Queries[^1].Author);
        Assert.Equal("#4", source.Queries[^1].Text);
        Assert.Equal(4, Assert.Single(vm.Items).PullRequest.Number);

        Assert.Equal("someone-else", vm.ResolveAuthor("@someone-else"));
        Assert.Equal("@me", vm.ResolveAuthor("me"));
        Assert.Equal("grace", vm.ResolveAuthor("Grace Hopper (grace)"));
    }

    // The people list waits for the first drop-down, shows that it is
    // loading, and is kept for the source - a later picker starts with it.
    [Fact]
    public async Task Picker_ListsPeopleOnFirstDropDown_AndKeepsThemUntilRestart()
    {
        var gate = new TaskCompletionSource();
        var source = new FakePullRequestSource(new[] { Pr(1, "victor") }) { PeopleGate = gate.Task };
        var vm = new PullRequestPickerViewModel(source);
        await vm.InitializeAsync();
        Assert.Equal(0, source.PeopleCalls);
        Assert.Equal(new[] { "Mine", "Anyone" }, vm.AuthorOptions.Select(o => o.Display));
        Assert.Equal("Mine (victor)", vm.MineHint);

        var load = vm.LoadPeopleAsync();
        Assert.Equal(PullRequestPickerViewModel.LoadingPeople, vm.AuthorOptions[^1]);
        gate.SetResult();
        await load;
        Assert.Equal(new[] { "Mine", "Anyone", "Ada Lovelace (ada)", "Grace Hopper (grace)", "victor" }, vm.AuthorOptions.Select(o => o.Display));
        await vm.LoadPeopleAsync();
        Assert.Equal(5, vm.AuthorOptions.Count);

        var again = new PullRequestPickerViewModel(source);
        await again.InitializeAsync();
        await WaitFor(() => again.AuthorOptions.Count == 5);
        await again.LoadPeopleAsync();
        Assert.Equal(vm.AuthorOptions, again.AuthorOptions);
        Assert.Equal(1, source.PeopleCalls);
        Assert.Equal(1, source.LoginCalls);
    }

    [Fact]
    public async Task Picker_PeopleFailure_SaysSo_AndRetriesOnTheNextDropDown()
    {
        var source = new FakePullRequestSource(new[] { Pr(1, "victor") }) { PeopleFailures = 1 };
        var vm = new PullRequestPickerViewModel(source);
        await vm.InitializeAsync();

        await vm.LoadPeopleAsync();
        Assert.Equal(new[] { "Mine", "Anyone", PullRequestPickerViewModel.NoPeople.Display }, vm.AuthorOptions.Select(o => o.Display));

        await vm.LoadPeopleAsync();
        Assert.Equal(5, vm.AuthorOptions.Count);
        Assert.DoesNotContain(vm.AuthorOptions, o => o.IsNote);
        Assert.Equal(2, source.PeopleCalls);
    }

    // The real window: Author is a drop-down; opening it lists the people
    // (a note while loading, not pickable); picking or typing searches.
    [AvaloniaFact]
    public void PickerWindow_AuthorIsADropDown_ListingPeopleWhenOpened()
    {
        var gate = new TaskCompletionSource();
        var source = new FakePullRequestSource(new[] { Pr(1, "victor"), Pr(2, "ada"), Pr(3, "grace") }) { PeopleGate = gate.Task };
        var window = new PullRequestPickerWindow(new PullRequestPickerViewModel(source, () => FakePullRequestSource.Now)) { Width = 760, Height = 560 };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => !vm.IsLoading && vm.Items.Count == 1, "first search");
        var box = window.FindControl<ComboBox>("AuthorBox")!;
        Assert.True(box.IsEditable);
        Assert.Equal("Mine", box.Text);
        Assert.Equal(0, source.PeopleCalls);

        box.IsDropDownOpen = true;
        WaitUntil(() => vm.AuthorOptions.Contains(PullRequestPickerViewModel.LoadingPeople), "loading note");
        UiSnapshot.Pump();
        var note = box.GetVisualDescendants().OfType<ComboBoxItem>().SingleOrDefault(i => Equals(i.DataContext, PullRequestPickerViewModel.LoadingPeople))
            ?? box.ContainerFromItem(PullRequestPickerViewModel.LoadingPeople) as ComboBoxItem;
        Assert.NotNull(note);
        Assert.False(note.IsEnabled);

        gate.SetResult();
        WaitUntil(() => vm.AuthorOptions.Count == 5, "people");
        Assert.Equal(1, source.PeopleCalls);
        UiSnapshot.Capture(window, "pr-picker-authors");

        box.SelectedItem = vm.AuthorOptions.Single(o => o.Login == "ada");
        box.IsDropDownOpen = false;
        Assert.Equal("Ada Lovelace (ada)", vm.AuthorText);
        WaitUntil(() => source.Queries[^1].Author == "ada" && !vm.IsLoading, "search by the picked person");
        Assert.Equal(2, Assert.Single(vm.Items).PullRequest.Number);

        box.Text = "grace";
        WaitUntil(() => source.Queries[^1].Author == "grace" && !vm.IsLoading, "search by a typed login");
        Assert.Equal(3, Assert.Single(vm.Items).PullRequest.Number);
        window.Close();
    }

    [Fact]
    public async Task Picker_ShowsTheSourceError()
    {
        var vm = new PullRequestPickerViewModel(new FakePullRequestSource(Array.Empty<PullRequestInfo>()) { Failure = "GitHub CLI is not signed in." });
        await vm.InitializeAsync();

        Assert.True(vm.HasError);
        Assert.Equal("GitHub CLI is not signed in.", vm.ErrorText);
        Assert.Empty(vm.Items);
        Assert.False(vm.CanShow);
    }

    // The real window: a short list keeps pulling pages until it can scroll.
    [AvaloniaFact]
    public void PickerWindow_FillsTheViewportByLoadingMorePages()
    {
        var source = new FakePullRequestSource(Enumerable.Range(1, 40).Select(n => Pr(n, "victor")));
        var window = new PullRequestPickerWindow(new PullRequestPickerViewModel(source, () => FakePullRequestSource.Now)) { Width = 760, Height = 860 };
        window.Show();

        WaitUntil(() => !window.ViewModel.IsLoading && window.ViewModel.Items.Count > PullRequestPickerViewModel.PageSize, "second page");
        UiSnapshot.Capture(window, "pr-picker");

        Assert.True(window.ViewModel.Items.Count >= 2 * PullRequestPickerViewModel.PageSize);
        window.Close();
    }

    // Pick a PR from the Period list: the bird's-eye shows only what it
    // changed, the drill-in compares its merge-base with its head, and going
    // back to a time period restores the folder view.
    [AvaloniaFact]
    public void PullRequestPeriod_ShowsOnlyThePrsChanges_AndRestoresTheFolderAfterwards()
    {
        var root = ReadmeFixture.BuildFixtureRepo("pr-mode");
        string baseSha, headSha;
        using (var repo = new Repository(root))
        {
            headSha = repo.Head.Tip.Sha;
            baseSha = repo.Head.Tip.Parents.First().Sha;
        }

        var pr = new PullRequestInfo(7, "Simplify the line diff and make the README mention the gitroot flag for whole-repo scope", "MERGED", false, "ada", FakePullRequestSource.Now, "main", "feature", baseSha, headSha, 3, 2, 2);
        var window = new MainWindow(root, gitRoot: true) { Width = 1400, Height = 560 };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");
        var folderFiles = AllFiles(vm).Count;

        vm.Analysis.PullRequests = new FakePullRequestSource(new[] { pr }) { Root = root };
        vm.Analysis.PickPullRequest = () => Task.FromResult<PullRequestInfo?>(pr);
        vm.Analysis.SelectedPeriod = vm.Analysis.Periods.Single(p => p.IsPullRequest);
        WaitUntil(() => vm.Analysis.AppliedPullRequest is not null && !vm.Analysis.IsScanning, "PR load");

        Assert.Equal(new[] { "LineDiff.cs", "README.md" }, AllFiles(vm).Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.Equal("#7 Simplify the line diff and make the README mention the gitroot flag for whole-repo scope", vm.Analysis.PullRequestLabel);
        Assert.True(vm.Analysis.IsPullRequestPeriod);
        Assert.Contains("in PR #7", vm.Analysis.SummaryTail);
        Assert.Contains("1 commits, 1 authors", vm.Analysis.SummaryTail);

        var row = AllFiles(vm).Single(r => r.Name == "LineDiff.cs");
        Assert.Equal("-7", row.RemovedText);
        _ = vm.Analysis.OpenAsync(row);
        WaitUntil(() => vm.IsDiffOpen && !vm.Diff.IsPlaceholder, "PR diff");
        UiSnapshot.Capture(window, "pr-mode");

        Assert.Equal($"PR #7: merge-base {baseSha[..7]} vs head {headSha[..7]}", vm.Diff.Subtitle);
        Assert.False(vm.ShowCompareOptions);
        Assert.Equal("PR #7 Simplify the line diff and make the README mention the gitroot flag for whole-repo scope  (main ← feature)", vm.StatusText);
        Assert.Contains(vm.Diff.Rows, r => r.Kind == DiffKind.Removed);

        // Back to a time period: folder scan again, and the open diff follows.
        vm.Analysis.SelectedPeriod = vm.Analysis.Periods.Single(p => p.Label == "Last month");
        WaitUntil(() => vm.Analysis.AppliedPullRequest is null && !vm.Analysis.IsScanning && AllFiles(vm).Count == folderFiles, "folder restore");
        Assert.True(vm.ShowCompareOptions);
        Assert.False(vm.Analysis.IsPullRequestPeriod);
        WaitUntil(() => !vm.Diff.Subtitle.StartsWith("PR #", StringComparison.Ordinal), "diff retarget");
        window.Close();
    }

    // Cancelling the first pick must not strand the Period list on "Pull request...".
    [AvaloniaFact]
    public void CancellingTheFirstPick_RestoresThePreviousPeriod()
    {
        var root = ReadmeFixture.BuildFixtureRepo("pr-cancel");
        var window = new MainWindow(root, gitRoot: true) { Width = 1200, Height = 520 };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");
        var before = vm.Analysis.SelectedPeriod;

        vm.Analysis.PickPullRequest = () => Task.FromResult<PullRequestInfo?>(null);
        vm.Analysis.SelectedPeriod = vm.Analysis.Periods.Single(p => p.IsPullRequest);
        WaitUntil(() => vm.Analysis.SelectedPeriod == before, "period restore");

        Assert.False(vm.Analysis.IsPullRequestPeriod);
        Assert.Null(vm.Analysis.AppliedPullRequest);
        window.Close();
    }

    // Lists come without counts: the rows show at once, their +/- and file
    // counts follow in one request for the page ("..." until then).
    [Fact]
    public async Task Picker_ShowsRowsFirst_ThenTheirCounts()
    {
        var gate = new TaskCompletionSource();
        var source = new FakePullRequestSource(Enumerable.Range(1, 12).Select(n => Pr(n, "victor", withCounts: false))) { CountsGate = gate.Task };
        var vm = new PullRequestPickerViewModel(source, () => FakePullRequestSource.Now);

        await vm.InitializeAsync();
        Assert.Equal(10, vm.Items.Count);
        var first = vm.Items[0];
        Assert.Equal((string.Empty, string.Empty, "…"), (first.Added, first.Removed, first.Files));
        Assert.Equal(Enumerable.Range(1, 10), Assert.Single(source.CountsAsked));

        gate.SetResult();
        await WaitFor(() => first.Counts is not null);
        Assert.Equal(("+1,000", "−13", "1 file"), (first.Added, first.Removed, first.Files));
        Assert.Equal("10 files", vm.Items[9].Files);

        // The next page asks for its own rows only.
        await vm.LoadMoreAsync();
        await WaitFor(() => vm.Items.All(r => r.Counts is not null));
        Assert.Equal(new[] { 11, 12 }, source.CountsAsked[1]);
    }

    [Fact]
    public async Task Picker_CountsFailure_LeavesTheRows()
    {
        var source = new FakePullRequestSource(new[] { Pr(1, "victor", withCounts: false) }) { CountsFailure = "rate limited" };
        var vm = new PullRequestPickerViewModel(source, () => FakePullRequestSource.Now);

        await vm.InitializeAsync();
        var row = Assert.Single(vm.Items);
        await WaitFor(() => row.CountsUnavailable);
        Assert.Equal((string.Empty, string.Empty, string.Empty), (row.Added, row.Removed, row.Files));
        Assert.Null(vm.ErrorText);
    }

    // Opening the Period list ("Pull request..." may be next) starts the
    // list the picker opens on - for a GitHub repository only.
    [AvaloniaFact]
    public void OpeningThePeriodList_SearchesAhead()
    {
        var root = ReadmeFixture.BuildFixtureRepo("pr-ahead");
        var window = new MainWindow(root, gitRoot: true) { Width = 1200, Height = 520 };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");
        var box = window.FindControl<RepickComboBox>("PeriodBox")!;

        var elsewhere = new FakePullRequestSource(new[] { Pr(1, "victor") });
        vm.Analysis.PullRequests = elsewhere;
        box.IsDropDownOpen = true;
        UiSnapshot.Pump();
        box.IsDropDownOpen = false;
        Assert.Empty(elsewhere.Queries);

        var github = new FakePullRequestSource(new[] { Pr(1, "victor") }) { CanSearchAhead = true };
        vm.Analysis.PullRequests = github;
        box.IsDropDownOpen = true;
        UiSnapshot.Pump();
        box.IsDropDownOpen = false;
        Assert.Equal(PullRequestPickerViewModel.DefaultQuery, Assert.Single(github.Queries));
        window.Close();
    }

    // The picker opening right after the search ahead uses its answer: no
    // second search, rows at once.
    [Fact]
    public async Task Picker_UsesTheSearchAhead()
    {
        var gate = new TaskCompletionSource();
        var source = new FakePullRequestSource(new[] { Pr(1, "victor"), Pr(2, "victor") }) { CanSearchAhead = true, SearchGate = gate.Task };

        PullRequestPickerViewModel.SearchAhead(source);
        var vm = new PullRequestPickerViewModel(source);
        var opening = vm.InitializeAsync();
        gate.SetResult();
        await opening;

        Assert.Equal(new[] { 1, 2 }, vm.Items.Select(r => r.PullRequest.Number));
        Assert.Single(source.Queries);
    }

    // Opening again shows the last list at once while it asks again; the
    // rows stay when the answer is the same, and are replaced (keeping the
    // selected PR) when it differs.
    [Fact]
    public async Task Picker_OpeningAgain_ShowsTheLastList_ThenTheNewAnswer()
    {
        var clock = FakePullRequestSource.Now;
        var source = new FakePullRequestSource(new[] { Pr(1, "victor"), Pr(2, "victor") });
        var first = new PullRequestPickerViewModel(source, () => clock);
        await first.InitializeAsync();

        // Within seconds: the same answer, no new search.
        var soon = new PullRequestPickerViewModel(source, () => clock);
        await soon.InitializeAsync();
        Assert.Single(source.Queries);

        clock = clock.AddMinutes(1);
        var gate = new TaskCompletionSource();
        source.SearchGate = gate.Task;
        var same = new PullRequestPickerViewModel(source, () => clock);
        var opening = same.InitializeAsync();
        Assert.Equal(new[] { 1, 2 }, same.Items.Select(r => r.PullRequest.Number));
        Assert.True(same.IsLoading);
        var shown = same.Items.ToList();
        gate.SetResult();
        await opening;
        Assert.Equal(shown, same.Items);
        Assert.False(same.IsLoading);
        Assert.Equal(2, source.Queries.Count);

        source.Replace(new[] { Pr(0, "victor"), Pr(2, "victor"), Pr(1, "victor") });
        clock = clock.AddMinutes(1);
        gate = new TaskCompletionSource();
        source.SearchGate = gate.Task;
        var changed = new PullRequestPickerViewModel(source, () => clock);
        opening = changed.InitializeAsync();
        changed.SelectedItem = changed.Items.Single(r => r.PullRequest.Number == 2);
        gate.SetResult();
        await opening;
        Assert.Equal(new[] { 0, 1, 2 }, changed.Items.Select(r => r.PullRequest.Number));
        Assert.Equal(2, changed.SelectedItem!.PullRequest.Number);
    }

    // The real window: picking "Pull request..." again (it is already the
    // period) reopens the picker; just opening the drop-down does not.
    [AvaloniaFact]
    public void PickingPullRequestAgain_ReopensThePicker()
    {
        var root = ReadmeFixture.BuildFixtureRepo("pr-repick");
        string baseSha, headSha;
        using (var repo = new Repository(root))
        {
            headSha = repo.Head.Tip.Sha;
            baseSha = repo.Head.Tip.Parents.First().Sha;
        }

        var pr = new PullRequestInfo(7, "Simplify the line diff", "MERGED", false, "ada", FakePullRequestSource.Now, "main", "feature", baseSha, headSha, 3, 2, 2);
        var window = new MainWindow(root, gitRoot: true) { Width = 1200, Height = 520 };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");

        var picks = 0;
        vm.Analysis.PullRequests = new FakePullRequestSource(new[] { pr }) { Root = root };
        vm.Analysis.PickPullRequest = () => Task.FromResult<PullRequestInfo?>(++picks == 1 ? pr : null);
        var box = window.FindControl<RepickComboBox>("PeriodBox")!;
        var pullRequest = vm.Analysis.Periods.Single(p => p.IsPullRequest);

        ClickDropDownItem(box, pullRequest);
        WaitUntil(() => vm.Analysis.AppliedPullRequest is not null && !vm.Analysis.IsScanning, "PR load");
        Assert.Equal(1, picks);

        box.IsDropDownOpen = true;
        UiSnapshot.Pump();
        box.IsDropDownOpen = false;
        UiSnapshot.Pump();
        Assert.Equal(1, picks);

        // Picked again: the picker opens; cancelling it keeps the PR.
        ClickDropDownItem(box, pullRequest);
        WaitUntil(() => picks == 2, "picker reopened");
        UiSnapshot.Pump();
        Assert.Equal(7, vm.Analysis.AppliedPullRequest!.PullRequest.Number);
        Assert.Same(pullRequest, vm.Analysis.SelectedPeriod);
        window.Close();
    }

    // Opens the drop-down and clicks the item, as a mouse would.
    private static void ClickDropDownItem(ComboBox box, object item)
    {
        box.IsDropDownOpen = true;
        UiSnapshot.Pump();
        var container = (Control)box.ContainerFromItem(item)!;
        var top = TopLevel.GetTopLevel(container)!;
        var at = container.TranslatePoint(new Point(container.Bounds.Width / 2, container.Bounds.Height / 2), top)!.Value;
        top.MouseDown(at, MouseButton.Left);
        top.MouseUp(at, MouseButton.Left);
        UiSnapshot.Pump();
    }

    private static List<CodeAnalysisRow> AllFiles(MainWindowViewModel vm)
        => vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).Where(r => r.IsFile).ToList();

    // Every third title is long (like real ones) so snapshots exercise trimming.
    private static PullRequestInfo Pr(int number, string author, string state = "OPEN", bool withCounts = true)
        => new(
            number,
            number % 3 == 0 ? $"Change {number}: detect git with a lowercase drive letter; show the file outside git or when unchanged" : $"Change {number}",
            state,
            false,
            author,
            FakePullRequestSource.Now.AddHours(-number),
            "main",
            number % 3 == 0 ? "vzakharov/lowercase-drive-git-detect-" + number : "b" + number,
            "base",
            "head",
            withCounts ? number * 1000 : null,
            withCounts ? number * 13 : null,
            withCounts ? number : null);

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static void WaitUntil(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + 20_000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            Thread.Sleep(25);
            UiSnapshot.Pump();
        }

        UiSnapshot.Pump();
    }

    // In-memory GitHub: honors author / state / text / number lookups and
    // cursor paging like the real search, newest update first.
    private sealed class FakePullRequestSource(IEnumerable<PullRequestInfo> all) : IPullRequestSource
    {
        public static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

        private List<PullRequestInfo> _all = all.OrderByDescending(p => p.UpdatedAt).ToList();

        // Searches wait for this, when set.
        public Task? SearchGate { get; set; }

        public bool CanSearchAhead { get; init; }

        public void Replace(IEnumerable<PullRequestInfo> all) => _all = all.OrderByDescending(p => p.UpdatedAt).ToList();

        public List<PullRequestQuery> Queries { get; } = new();

        public string? Failure { get; init; }

        public string? Root { get; init; }

        public Task<PullRequestPage> SearchAsync(PullRequestQuery query, string? after, int pageSize, CancellationToken ct)
        {
            Queries.Add(query);
            return SearchGate is { } gate ? AfterAsync(gate, () => Answer(query, after, pageSize)) : Answer(query, after, pageSize);
        }

        private static async Task<PullRequestPage> AfterAsync(Task gate, Func<Task<PullRequestPage>> answer)
        {
            await gate;
            return await answer();
        }

        private Task<PullRequestPage> Answer(PullRequestQuery query, string? after, int pageSize)
        {
            if (Failure is not null)
            {
                return Task.FromException<PullRequestPage>(new PullRequestSourceException(Failure));
            }

            if (PullRequestSearch.ParseNumber(query.Text) is { } number)
            {
                return Task.FromResult(new PullRequestPage(_all.Where(p => p.Number == number).ToList(), null, false, 1));
            }

            var author = query.Author == "@me" ? "victor" : query.Author;
            var matches = _all
                .Where(p => author is null || p.Author == author)
                .Where(p => query.State switch
                {
                    PullRequestStateFilter.Open => p.State == "OPEN",
                    PullRequestStateFilter.Merged => p.State == "MERGED",
                    PullRequestStateFilter.Closed => p.State == "CLOSED",
                    _ => true,
                })
                .Where(p => query.Text.Length == 0 || p.Title.Contains(query.Text, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var start = after is null ? 0 : int.Parse(after, CultureInfo.InvariantCulture);
            var page = matches.Skip(start).Take(pageSize).ToList();
            var next = start + page.Count;
            return Task.FromResult(new PullRequestPage(page, next.ToString(CultureInfo.InvariantCulture), next < matches.Count, matches.Count));
        }

        // Held until this completes, when set.
        public Task? PeopleGate { get; init; }

        // How many people fetches fail before one succeeds.
        public int PeopleFailures { get; set; }

        public int PeopleCalls { get; private set; }

        public int LoginCalls { get; private set; }

        public async Task<IReadOnlyList<RepoPerson>> GetPeopleAsync(CancellationToken ct)
        {
            PeopleCalls++;
            if (PeopleGate is not null)
            {
                await PeopleGate;
            }

            if (PeopleFailures > 0)
            {
                PeopleFailures--;
                throw new PullRequestSourceException("rate limited");
            }

            return new[] { new RepoPerson("ada", "Ada Lovelace"), new RepoPerson("grace", "Grace Hopper"), new RepoPerson("victor", null) };
        }

        public Task<string?> GetCurrentLoginAsync(CancellationToken ct)
        {
            LoginCalls++;
            return Task.FromResult<string?>("victor");
        }

        public Task<PullRequestRange> ResolveAsync(PullRequestInfo pullRequest, CancellationToken ct)
            => Task.Run(() => PullRequestRanges.Resolve(Root!, pullRequest, () => null, 10_000, ct), ct);

        // Counts are held until this completes, when set.
        public Task? CountsGate { get; init; }

        public string? CountsFailure { get; init; }

        // The PR numbers of each counts request.
        public List<int[]> CountsAsked { get; } = new();

        public async Task<IReadOnlyDictionary<int, PullRequestCounts>> GetCountsAsync(IReadOnlyList<PullRequestInfo> pullRequests, CancellationToken ct)
        {
            CountsAsked.Add(pullRequests.Select(p => p.Number).ToArray());
            if (CountsGate is not null)
            {
                await CountsGate;
            }

            if (CountsFailure is not null)
            {
                throw new PullRequestSourceException(CountsFailure);
            }

            return pullRequests.ToDictionary(p => p.Number, p => new PullRequestCounts(p.Number * 1000, p.Number * 13, p.Number));
        }
    }
}
