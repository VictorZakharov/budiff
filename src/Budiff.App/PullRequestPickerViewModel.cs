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
// The repo's people are fetched the first time the author list drops down,
// then kept per source - for its repository, until restart.
public sealed partial class PullRequestPickerViewModel : ObservableObject
{
    public const int PageSize = 10;
    private const int DebounceMs = 300;

    public static readonly AuthorOption Mine = new("Mine", "@me");
    public static readonly AuthorOption Anyone = new("Anyone", null);
    public static readonly AuthorOption LoadingPeople = new("Loading people…", null, IsNote: true);
    public static readonly AuthorOption NoPeople = new("Could not list people - type a login", null, IsNote: true);

    private static readonly ConditionalWeakTable<IPullRequestSource, Task<IReadOnlyList<RepoPerson>>> PeopleCache = new();
    private static readonly ConditionalWeakTable<IPullRequestSource, Task<string?>> LoginCache = new();

    // First pages per source and query (see FirstPage): searched ahead when
    // the Period list opens, shown at once when a picker opens again.
    private static readonly ConditionalWeakTable<IPullRequestSource, Dictionary<PullRequestQuery, FirstPage>> FirstPages = new();

    private readonly IPullRequestSource _source;
    private readonly Func<DateTimeOffset> _now;
    private PullRequestQuery _query;
    private string? _cursor;
    private int _searchGen;
    private int _debounceGen;
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
        }
    }

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

        await SearchNowAsync();
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

        await LoadPageAsync(_searchGen, _query, _cursor);
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

    // Typing settles before a query goes out; a later keystroke supersedes it.
    private void ScheduleSearch()
    {
        if (!_initialized)
        {
            return;
        }

        _ = DebouncedSearchAsync(++_debounceGen);
    }

    private async Task DebouncedSearchAsync(int gen)
    {
        await Task.Delay(DebounceMs);
        if (gen == _debounceGen && CurrentQuery() != _query)
        {
            await RunSearchAsync();
        }
    }

    private async Task RunSearchAsync()
    {
        var gen = ++_searchGen;
        _query = CurrentQuery();
        _cursor = null;
        HasMore = false;
        ErrorText = null;
        SelectedItem = null;
        Items.Clear();

        // The first page: a search ahead still out or just back is used as
        // is; else one goes out, and the last answer shows meanwhile.
        var first = FirstPageOf(_source, _query);
        var request = first.Fetch(_source, _query, _now);
        var showingLast = false;
        if (!request.IsCompleted && first.Last is { } last)
        {
            AddRows(last);
            SelectedItem = Items.FirstOrDefault();
            StatusText = Status(last);
            showingLast = true;
        }

        await LoadPageAsync(gen, request, showingLast);
    }

    private Task LoadPageAsync(int gen, PullRequestQuery query, string? after)
        => LoadPageAsync(gen, Ask(() => _source.SearchAsync(query, after, PageSize, CancellationToken.None)));

    // showingLast: the rows on screen are the last answer to this first
    // page; they stay if this one lists the same PRs, else it replaces them.
    private async Task LoadPageAsync(int gen, Task<PullRequestPage> request, bool showingLast = false)
    {
        IsLoading = true;
        if (!showingLast)
        {
            StatusText = Items.Count == 0 ? "Searching..." : "Loading more...";
        }

        try
        {
            var page = await request;
            if (gen != _searchGen)
            {
                return;
            }

            if (!showingLast)
            {
                AddRows(page);
            }
            else if (!SameRows(page))
            {
                var selected = SelectedItem?.PullRequest.Number;
                Items.Clear();
                AddRows(page);
                SelectedItem = Items.FirstOrDefault(r => r.PullRequest.Number == selected);
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
