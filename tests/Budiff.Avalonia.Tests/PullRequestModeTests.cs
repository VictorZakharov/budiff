using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
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
        await WaitFor(() => vm.AuthorOptions.Count > 2);

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

    private static List<CodeAnalysisRow> AllFiles(MainWindowViewModel vm)
        => vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).Where(r => r.IsFile).ToList();

    // Every third title is long (like real ones) so snapshots exercise trimming.
    private static PullRequestInfo Pr(int number, string author, string state = "OPEN")
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
            number * 1000,
            number * 13,
            number);

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

        private readonly List<PullRequestInfo> _all = all.OrderByDescending(p => p.UpdatedAt).ToList();

        public List<PullRequestQuery> Queries { get; } = new();

        public string? Failure { get; init; }

        public string? Root { get; init; }

        public Task<PullRequestPage> SearchAsync(PullRequestQuery query, string? after, int pageSize, CancellationToken ct)
        {
            Queries.Add(query);
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

        public Task<IReadOnlyList<RepoPerson>> GetPeopleAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RepoPerson>>(new[] { new RepoPerson("ada", "Ada Lovelace"), new RepoPerson("grace", "Grace Hopper"), new RepoPerson("victor", null) });

        public Task<string?> GetCurrentLoginAsync(CancellationToken ct) => Task.FromResult<string?>("victor");

        public Task<PullRequestRange> ResolveAsync(PullRequestInfo pullRequest, CancellationToken ct)
            => Task.Run(() => PullRequestRanges.Resolve(Root!, pullRequest, () => null, 10_000, ct), ct);
    }
}
