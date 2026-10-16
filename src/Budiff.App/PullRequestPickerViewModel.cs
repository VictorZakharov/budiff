using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Budiff.Avalonia;
using Budiff.Git;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Budiff.App;

// One author choice: Login "@me" = the signed-in user, null = anyone. A note
// ("Loading people...") is a list row that cannot be picked.
public sealed record AuthorOption(string Display, string? Login, bool IsNote = false)
{
    public override string ToString() => Display;
}

public sealed record StateOption(string Label, PullRequestStateFilter Value)
{
    public override string ToString() => Label;
}

// One list row. Its counts arrive after the row (see FillCountsAsync); until
// then the files column says "..." (blank if they could not be fetched).
public sealed partial class PullRequestRowVm(PullRequestInfo pullRequest, DateTimeOffset now) : ObservableObject
{
    private static readonly IBrush OpenBrush = HexBrush.Parse("#2EA043");
    private static readonly IBrush DraftBrush = HexBrush.Parse("#6E7681");
    private static readonly IBrush MergedBrush = HexBrush.Parse("#8957E5");
    private static readonly IBrush ClosedBrush = HexBrush.Parse("#DA3633");

    public PullRequestInfo PullRequest { get; } = pullRequest;

    public string NumberText => "#" + PullRequest.Number;

    public string Title => PullRequest.Title;

    public string StateLabel => PullRequest.StateLabel;

    public IBrush StateBrush => PullRequest.StateLabel switch
    {
        "Open" => OpenBrush,
        "Draft" => DraftBrush,
        "Merged" => MergedBrush,
        _ => ClosedBrush,
    };

    public string Meta => $"{PullRequest.Author}  ·  updated {Age(now - PullRequest.UpdatedAt)}  ·  {PullRequest.BaseRefName} ← {PullRequest.HeadRefName}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Added), nameof(Removed), nameof(Files))]
    private PullRequestCounts? _counts = pullRequest.Counts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Files))]
    private bool _countsUnavailable;

    public string Added => Counts is { } counts ? "+" + counts.Additions.ToString("N0") : string.Empty;

    public string Removed => Counts is { } counts ? "−" + counts.Deletions.ToString("N0") : string.Empty;

    public string Files => Counts switch
    {
        { ChangedFiles: 1 } => "1 file",
        { } counts => counts.ChangedFiles.ToString("N0") + " files",
        _ => CountsUnavailable ? string.Empty : "…",
    };

    private static string Age(TimeSpan span)
        => span.TotalDays >= 365 ? $"{(int)(span.TotalDays / 365)}y ago"
            : span.TotalDays >= 30 ? $"{(int)(span.TotalDays / 30)}mo ago"
            : span.TotalDays >= 1 ? $"{(int)span.TotalDays}d ago"
            : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h ago"
            : "just now";
}

// The "find a pull request" popup: your PRs first, 10 at a time, more as the
// list scrolls; narrow by author (anyone in the repo), state, and free text.
// Typing narrows the PRs already seen at once; GitHub's answer follows once
// typing settles, and a later query stops an earlier one. The repo's people
// are fetched the first time the author list drops down, then kept per
// source - for its repository, until restart.
public sealed partial class PullRequestPickerViewModel : ObservableObject
{
    public const int PageSize = 10;
    private const int DebounceMs = 300;

    // How many recent PRs are fetched for typing to filter (see Seed).
    private const int SeedSize = 100;

    public static readonly AuthorOption Mine = new("Mine", "@me");
    public static readonly AuthorOption Anyone = new("Anyone", null);
    public static readonly AuthorOption LoadingPeople = new("Loading people…", null, IsNote: true);
    public static readonly AuthorOption NoPeople = new("Could not list people - type a login", null, IsNote: true);

    private static readonly ConditionalWeakTable<IPullRequestSource, Task<IReadOnlyList<RepoPerson>>> PeopleCache = new();
    private static readonly ConditionalWeakTable<IPullRequestSource, Task<string?>> LoginCache = new();

    // First pages per source and query (see FirstPage): searched ahead when
    // the Period list opens, shown at once when a picker opens again.
    private static readonly ConditionalWeakTable<IPullRequestSource, Dictionary<PullRequestQuery, FirstPage>> FirstPages = new();

    // The PRs lists have shown, per source: typing filters them at once.
    private static readonly ConditionalWeakTable<IPullRequestSource, SeenPullRequests> Seen = new();

    private readonly IPullRequestSource _source;
    private readonly Func<DateTimeOffset> _now;
    private PullRequestQuery _query;
    private string? _cursor;
    private int _searchGen;
    private int _debounceGen;
    private CancellationTokenSource _searchCts = new();

    // The list shows a stand-in for _query (see StandIn) and GitHub has not
    // been asked yet - typing has not settled.
    private bool _provisional;
    private bool _initialized;
    private Task? _peopleLoad;
    private bool _peopleListed;

    // What a picker opens on: Mine, any state, no text.
    public static PullRequestQuery DefaultQuery { get; } = new(Mine.Login, PullRequestStateFilter.All, string.Empty);

    // The Period list opened, so "Pull request..." may be next: start the
    // list the picker opens on, so its rows are there (or nearly) by then.
    public static void SearchAhead(IPullRequestSource source)
    {
        if (source.CanSearchAhead)
        {
            FirstPageOf(source, DefaultQuery).Fetch(source, DefaultQuery, () => DateTimeOffset.Now);
            Seed(source, DateTimeOffset.Now);
        }
    }

    // The repository's most recently updated PRs, fetched in the background
    // (again after a while) when it is on GitHub and listed whole: what
    // typing filters before GitHub answers. A folder's list keeps to what it
    // showed - these may not touch the folder.
    private static void Seed(IPullRequestSource source, DateTimeOffset now)
    {
        if (source.CanSearchAhead && source.Scope.Length == 0 && SeenOf(source).StartSeed(now))
        {
            _ = SeedAsync(source);
        }
    }

    private static async Task SeedAsync(IPullRequestSource source)
    {
        var anyone = new PullRequestQuery(null, PullRequestStateFilter.All, string.Empty);
        try
        {
            SeenOf(source).Add((await Ask(() => source.SearchAsync(anyone, null, SeedSize, CancellationToken.None))).Items);
        }
        catch (Exception)
        {
            // Only a head start; the next picker tries again.
            SeenOf(source).SeedFailed();
        }
    }

    private static SeenPullRequests SeenOf(IPullRequestSource source) => Seen.GetValue(source, _ => new SeenPullRequests());

    public PullRequestPickerViewModel(IPullRequestSource source, Func<DateTimeOffset>? now = null)
    {
        _source = source;
        _now = now ?? (() => DateTimeOffset.Now);
        _selectedState = States[0];
        _query = CurrentQuery();
        AuthorOptions.Add(Mine);
        AuthorOptions.Add(Anyone);
    }

    public ObservableCollection<PullRequestRowVm> Items { get; } = new();

    public ObservableCollection<AuthorOption> AuthorOptions { get; } = new();

    public IReadOnlyList<StateOption> States { get; } = new[]
    {
        new StateOption("All", PullRequestStateFilter.All),
        new StateOption("Open", PullRequestStateFilter.Open),
        new StateOption("Merged", PullRequestStateFilter.Merged),
        new StateOption("Closed", PullRequestStateFilter.Closed),
    };

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _authorText = Mine.Display;

    [ObservableProperty]
    private StateOption _selectedState;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanShow))]
    private PullRequestRowVm? _selectedItem;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasMore;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    [ObservableProperty]
    private string _mineHint = "Mine";

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool CanShow => SelectedItem is not null;

    // First search, and who "Mine" is. The people list waits for the author
    // list to drop down - unless an earlier picker already fetched it.
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _ = LoadMineAsync();
        if (PeopleCache.TryGetValue(_source, out var known) && known.IsCompletedSuccessfully)
        {
            _ = LoadPeopleAsync();
        }

        var first = SearchNowAsync();
        Seed(_source, _now());
        await first;
    }

    // The author list dropped down: list the repo's people (once; a failure
    // is retried on the next drop-down).
    public Task LoadPeopleAsync()
    {
        if (_peopleListed)
        {
            return Task.CompletedTask;
        }

        return _peopleLoad is { IsCompleted: false } running ? running : _peopleLoad = ShowPeopleAsync();
    }

    public Task SearchNowAsync()
    {
        _debounceGen++;
        return RunSearchAsync();
    }

    [RelayCommand]
    public async Task LoadMoreAsync()
    {
        if (IsLoading || !HasMore || _cursor is null)
        {
            return;
        }

        var query = _query;
        var after = _cursor;
        var ct = _searchCts.Token;
        await LoadPageAsync(_searchGen, query, Ask(() => _source.SearchAsync(query, after, PageSize, ct)));
    }

    // The picker closed: what is still out is no longer wanted.
    public void StopSearching()
    {
        _debounceGen++;
        _searchGen++;
        _ = _searchCts.CancelAsync();
    }

    partial void OnSearchTextChanged(string value) => ScheduleSearch();

    partial void OnAuthorTextChanged(string value) => ScheduleSearch();

    partial void OnSelectedStateChanged(StateOption value)
    {
        if (_initialized)
        {
            _ = SearchNowAsync();
        }
    }

    // Typing settles before a query goes out; a later keystroke supersedes
    // it. Meanwhile the PRs already seen that match show at once.
    private void ScheduleSearch()
    {
        if (!_initialized)
        {
            return;
        }

        var query = CurrentQuery();
        if (query != _query && StandIn(query) is { } standIn)
        {
            Supersede(query);
            _provisional = true;
            Show(standIn);
        }

        _ = DebouncedSearchAsync(++_debounceGen);
    }

    private async Task DebouncedSearchAsync(int gen)
    {
        await Task.Delay(DebounceMs);
        if (gen == _debounceGen && (_provisional || CurrentQuery() != _query))
        {
            await RunSearchAsync();
        }
    }

    private async Task RunSearchAsync()
    {
        var query = CurrentQuery();
        var shown = _provisional && query == _query;
        var gen = Supersede(query);
        _provisional = false;

        // A list (no text) shares its first page: a search ahead still out
        // or just back is used as is. A text search is this picker's own,
        // stopped when a later one supersedes it.
        Task<PullRequestPage> request;
        if (query.Text.Length == 0)
        {
            request = FirstPageOf(_source, query).Fetch(_source, query, _now);
        }
        else
        {
            var ct = _searchCts.Token;
            request = Ask(() => _source.SearchAsync(query, null, PageSize, ct));
        }

        // Meanwhile the stand-in shows, unless typing already put it up.
        if (!shown && !request.IsCompleted && StandIn(query) is { } standIn)
        {
            Show(standIn);
            shown = true;
        }
        else if (!shown)
        {
            SelectedItem = null;
            Items.Clear();
        }

        await LoadPageAsync(gen, query, request, shown);
    }

    // A new query: what the last one still has out is no longer wanted.
    private int Supersede(PullRequestQuery query)
    {
        _ = _searchCts.CancelAsync();
        _searchCts = new CancellationTokenSource();
        _query = query;
        _cursor = null;
        HasMore = false;
        ErrorText = null;
        return ++_searchGen;
    }

    // What to show while a query is out: its last answer, else the PRs seen
    // so far that match it. Null when neither applies.
    private (IReadOnlyList<PullRequestInfo> Rows, PullRequestPage? Last)? StandIn(PullRequestQuery query)
    {
        if (query.Text.Length == 0 && FirstPageOf(_source, query).Last is { } last)
        {
            return (last.Items, last);
        }

        return LocalMatches(query) is { } matches ? (matches, null) : null;
    }

    private void Show((IReadOnlyList<PullRequestInfo> Rows, PullRequestPage? Last) standIn)
    {
        ShowRows(standIn.Rows);
        StatusText = standIn.Last is { } last ? Status(last) : "Searching...";
        IsLoading = true;
    }

    // The seen PRs a query would likely list, newest first. GitHub matches
    // words in bodies and comments too, so its answer replaces these. Null
    // when the query is beyond a local filter: search qualifiers, an author
    // not known yet, or a folder's default list (open, then merged history).
    private IReadOnlyList<PullRequestInfo>? LocalMatches(PullRequestQuery query)
    {
        var seen = SeenOf(_source);
        if (PullRequestSearch.ParseNumber(query.Text) is { } number)
        {
            return seen.Find(number) is { } pr ? new[] { pr } : Array.Empty<PullRequestInfo>();
        }

        var words = query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim('"')).Where(w => w.Length > 0).ToList();
        if (words.Any(w => w.Contains(':') || w.StartsWith('-')))
        {
            return null;
        }

        var author = query.Author;
        if (author == Mine.Login)
        {
            if (!LoginCache.TryGetValue(_source, out var login) || !login.IsCompletedSuccessfully || login.Result is not { } me)
            {
                return null;
            }

            author = me;
        }

        if (author is null && words.Count == 0 && query.State == PullRequestStateFilter.All && _source.Scope.Length > 0)
        {
            return null;
        }

        var all = seen.All();
        if (author is not null
            && !all.Any(p => p.Author.Equals(author, StringComparison.OrdinalIgnoreCase))
            && !AuthorOptions.Any(o => author.Equals(o.Login, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return all
            .Where(p => author is null || p.Author.Equals(author, StringComparison.OrdinalIgnoreCase))
            .Where(p => query.State switch
            {
                PullRequestStateFilter.Open => p.State == "OPEN",
                PullRequestStateFilter.Merged => p.State == "MERGED",
                PullRequestStateFilter.Closed => p.State == "CLOSED",
                _ => true,
            })
            .Where(p => words.All(w => p.Title.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(p => p.UpdatedAt)
            .Take(PageSize)
            .ToList();
    }

    // shown: the rows on screen stand in for this first page (see StandIn);
    // they stay if it lists the same PRs, else it replaces them.
    private async Task LoadPageAsync(int gen, PullRequestQuery query, Task<PullRequestPage> request, bool shown = false)
    {
        IsLoading = true;
        if (!shown)
        {
            StatusText = Items.Count == 0 ? "Searching..." : "Loading more...";
        }

        try
        {
            var page = await request;

            // A #number lookup is not limited to the folder - keep it out of
            // what a scoped list filters.
            if (PullRequestSearch.ParseNumber(query.Text) is null)
            {
                SeenOf(_source).Add(page.Items);
            }

            if (gen != _searchGen)
            {
                return;
            }

            if (!shown)
            {
                AddRows(page);
            }
            else if (!SameRows(page))
            {
                ShowRows(page.Items);
            }

            _cursor = page.EndCursor;
            HasMore = page.HasNextPage && page.EndCursor is not null;
            SelectedItem ??= Items.FirstOrDefault();
            StatusText = Status(page);
        }
        catch (Exception ex)
        {
            if (gen == _searchGen)
            {
                ErrorText = ex.Message;
                StatusText = string.Empty;
                HasMore = false;
            }
        }
        finally
        {
            if (gen == _searchGen)
            {
                IsLoading = false;
            }
        }
    }

    private void AddRows(PullRequestPage page)
    {
        var now = _now();
        var known = Items.Select(i => i.PullRequest.Number).ToHashSet();
        var added = new List<PullRequestRowVm>();
        foreach (var pr in page.Items.Where(pr => known.Add(pr.Number)))
        {
            var row = new PullRequestRowVm(pr, now);
            Items.Add(row);
            added.Add(row);
        }

        _ = FillCountsAsync(added);
    }

    // These PRs replace the rows, reusing the rows still listed (counts and
    // all) and keeping the selected PR when it is among them.
    private void ShowRows(IReadOnlyList<PullRequestInfo> prs)
    {
        var selected = SelectedItem?.PullRequest.Number;
        var rows = Items.ToDictionary(r => r.PullRequest.Number);
        var now = _now();
        var added = new List<PullRequestRowVm>();
        Items.Clear();
        foreach (var pr in prs.DistinctBy(p => p.Number))
        {
            if (!rows.TryGetValue(pr.Number, out var row) || row.PullRequest.UpdatedAt != pr.UpdatedAt || row.PullRequest.State != pr.State)
            {
                row = new PullRequestRowVm(pr, now);
                added.Add(row);
            }

            Items.Add(row);
        }

        SelectedItem = Items.FirstOrDefault(r => r.PullRequest.Number == selected) ?? Items.FirstOrDefault();
        _ = FillCountsAsync(added);
    }

    private bool SameRows(PullRequestPage page)
        => Items.Select(r => (r.PullRequest.Number, r.PullRequest.State, r.PullRequest.UpdatedAt))
            .SequenceEqual(page.Items.DistinctBy(p => p.Number).Select(p => (p.Number, p.State, p.UpdatedAt)));

    private static FirstPage FirstPageOf(IPullRequestSource source, PullRequestQuery query)
    {
        var pages = FirstPages.GetValue(source, _ => new Dictionary<PullRequestQuery, FirstPage>());
        lock (pages)
        {
            if (!pages.TryGetValue(query, out var page))
            {
                if (pages.Count >= 32)
                {
                    pages.Clear();
                }

                pages[query] = page = new FirstPage();
            }

            return page;
        }
    }

    // A source that throws instead of returning a failed task still fails
    // the task.
    private static Task<PullRequestPage> Ask(Func<Task<PullRequestPage>> ask)
    {
        try
        {
            return ask();
        }
        catch (Exception ex)
        {
            return Task.FromException<PullRequestPage>(ex);
        }
    }

    // One query's first page: the request out (shared while it runs, and for
    // a few seconds after it answered) and the last answer.
    private sealed class FirstPage
    {
        private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(15);

        private readonly object _gate = new();
        private Task<PullRequestPage>? _request;
        private DateTimeOffset _answeredAt;

        public PullRequestPage? Last { get; private set; }

        public Task<PullRequestPage> Fetch(IPullRequestSource source, PullRequestQuery query, Func<DateTimeOffset> now)
        {
            lock (_gate)
            {
                if (_request is { } request
                    && (!request.IsCompleted || (request.IsCompletedSuccessfully && now() - _answeredAt < Fresh)))
                {
                    return request;
                }

                var asked = Ask(() => source.SearchAsync(query, null, PageSize, CancellationToken.None));
                _request = asked;
                _ = asked.ContinueWith(
                    done =>
                    {
                        lock (_gate)
                        {
                            if (done.IsCompletedSuccessfully)
                            {
                                Last = done.Result;
                                _answeredAt = now();
                            }
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return asked;
            }
        }
    }

    // The PRs answers listed, by number, as last seen.
    private sealed class SeenPullRequests
    {
        private const int Limit = 2000;
        private static readonly TimeSpan Reseed = TimeSpan.FromMinutes(10);

        private readonly Dictionary<int, PullRequestInfo> _byNumber = new();
        private DateTimeOffset? _seededAt;

        // Whether to fetch the recent PRs (see Seed): not yet, or not lately.
        public bool StartSeed(DateTimeOffset now)
        {
            lock (_byNumber)
            {
                if (_seededAt is { } at && now - at < Reseed)
                {
                    return false;
                }

                _seededAt = now;
                return true;
            }
        }

        public void SeedFailed()
        {
            lock (_byNumber)
            {
                _seededAt = null;
            }
        }

        public void Add(IEnumerable<PullRequestInfo> prs)
        {
            lock (_byNumber)
            {
                foreach (var pr in prs)
                {
                    if (!_byNumber.TryGetValue(pr.Number, out var known) || known.UpdatedAt <= pr.UpdatedAt)
                    {
                        _byNumber[pr.Number] = pr;
                    }
                }

                if (_byNumber.Count > Limit)
                {
                    foreach (var old in _byNumber.Values.OrderBy(p => p.UpdatedAt).Take(_byNumber.Count - Limit).ToList())
                    {
                        _byNumber.Remove(old.Number);
                    }
                }
            }
        }

        public PullRequestInfo? Find(int number)
        {
            lock (_byNumber)
            {
                return _byNumber.GetValueOrDefault(number);
            }
        }

        public List<PullRequestInfo> All()
        {
            lock (_byNumber)
            {
                return _byNumber.Values.ToList();
            }
        }
    }

    // The rows are up; their +/- and file counts follow in one request.
    private async Task FillCountsAsync(IReadOnlyList<PullRequestRowVm> rows)
    {
        var missing = rows.Where(r => r.Counts is null).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        try
        {
            var counts = await _source.GetCountsAsync(missing.Select(r => r.PullRequest).ToList(), CancellationToken.None);
            foreach (var row in missing)
            {
                if (counts.TryGetValue(row.PullRequest.Number, out var found))
                {
                    row.Counts = found;
                }
                else
                {
                    row.CountsUnavailable = true;
                }
            }
        }
        catch (Exception)
        {
            // Counts are extras; the rows stand without them.
            foreach (var row in missing)
            {
                row.CountsUnavailable = true;
            }
        }
    }

    // A scoped list has no total: it says what it is limited to instead (and
    // keeps looking while nothing has turned up yet - the window asks for
    // more while the list is short).
    private string Status(PullRequestPage page)
    {
        var scope = _source.Scope;
        if (scope.Length == 0 || page.TotalCount >= 0)
        {
            return Items.Count == 0
                ? "No pull requests match."
                : $"{Items.Count:N0} of {Math.Max(page.TotalCount, Items.Count):N0}";
        }

        return Items.Count == 0
            ? HasMore ? $"Looking for pull requests touching {scope}..." : $"No pull requests touch {scope}."
            : $"{Items.Count:N0} touching {scope}" + (HasMore ? " - more as you scroll" : string.Empty);
    }

    private async Task LoadMineAsync()
    {
        var login = LoginCache.GetValue(_source, s => s.GetCurrentLoginAsync(CancellationToken.None));
        try
        {
            if (await login is { } me)
            {
                MineHint = $"Mine ({me})";
            }
        }
        catch
        {
            // Only the hint; forget the failure so a later picker asks again.
            LoginCache.Remove(_source);
        }
    }

    // Convenience only - typing a login works without the list, so a failure
    // just says so in the list.
    private async Task ShowPeopleAsync()
    {
        AuthorOptions.Remove(NoPeople);
        var people = PeopleCache.GetValue(_source, s => s.GetPeopleAsync(CancellationToken.None));
        if (!people.IsCompleted)
        {
            AuthorOptions.Add(LoadingPeople);
        }

        try
        {
            var list = await people;
            AuthorOptions.Remove(LoadingPeople);
            foreach (var person in list)
            {
                AuthorOptions.Add(new AuthorOption(person.Display, person.Login));
            }

            _peopleListed = true;
        }
        catch
        {
            PeopleCache.Remove(_source);
            AuthorOptions.Remove(LoadingPeople);
            AuthorOptions.Add(NoPeople);
        }
    }

    private PullRequestQuery CurrentQuery() => new(ResolveAuthor(AuthorText), SelectedState.Value, SearchText.Trim());

    // The author box takes a listed option, "me"/"anyone", a login (with or
    // without @), or part of a listed person's name.
    internal string? ResolveAuthor(string text)
    {
        var typed = text.Trim();
        if (typed.Length == 0
            || typed.Equals(Anyone.Display, StringComparison.OrdinalIgnoreCase)
            || typed.Equals("everyone", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (typed.Equals(Mine.Display, StringComparison.OrdinalIgnoreCase)
            || typed.Equals("me", StringComparison.OrdinalIgnoreCase)
            || typed.Equals("@me", StringComparison.OrdinalIgnoreCase)
            || typed.Equals(MineHint, StringComparison.OrdinalIgnoreCase))
        {
            return Mine.Login;
        }

        var exact = AuthorOptions.FirstOrDefault(o => o.Login is not null && o != Mine
            && (o.Display.Equals(typed, StringComparison.OrdinalIgnoreCase) || o.Login.Equals(typed.TrimStart('@'), StringComparison.OrdinalIgnoreCase)));
        if (exact is not null)
        {
            return exact.Login;
        }

        var byName = AuthorOptions.Where(o => o.Login is not null && o != Mine && o.Display.Contains(typed, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count == 1 ? byName[0].Login : typed.TrimStart('@');
    }
}
