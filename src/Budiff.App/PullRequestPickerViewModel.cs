using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Budiff.Avalonia;
using Budiff.Git;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Budiff.App;

// One author choice: Login "@me" = the signed-in user, null = anyone.
public sealed record AuthorOption(string Display, string? Login)
{
    public override string ToString() => Display;
}

public sealed record StateOption(string Label, PullRequestStateFilter Value)
{
    public override string ToString() => Label;
}

public sealed class PullRequestRowVm(PullRequestInfo pullRequest, DateTimeOffset now)
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

    public string Added => "+" + PullRequest.Additions.ToString("N0");

    public string Removed => "−" + PullRequest.Deletions.ToString("N0");

    public string Files => PullRequest.ChangedFiles == 1 ? "1 file" : PullRequest.ChangedFiles.ToString("N0") + " files";

    private static string Age(TimeSpan span)
        => span.TotalDays >= 365 ? $"{(int)(span.TotalDays / 365)}y ago"
            : span.TotalDays >= 30 ? $"{(int)(span.TotalDays / 30)}mo ago"
            : span.TotalDays >= 1 ? $"{(int)span.TotalDays}d ago"
            : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h ago"
            : "just now";
}

// The "find a pull request" popup: your PRs first, 10 at a time, more as the
// list scrolls; narrow by author (anyone in the repo), state, and free text.
public sealed partial class PullRequestPickerViewModel : ObservableObject
{
    public const int PageSize = 10;
    private const int DebounceMs = 300;

    public static readonly AuthorOption Mine = new("Mine", "@me");
    public static readonly AuthorOption Anyone = new("Anyone", null);

    private readonly IPullRequestSource _source;
    private readonly Func<DateTimeOffset> _now;
    private PullRequestQuery _query;
    private string? _cursor;
    private int _searchGen;
    private int _debounceGen;
    private bool _initialized;

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

    // First search plus the people list (a convenience: typing a login works
    // without it, so its failure is silent).
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _ = LoadPeopleAsync();
        await SearchNowAsync();
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
        await LoadPageAsync(gen, _query, after: null);
    }

    private async Task LoadPageAsync(int gen, PullRequestQuery query, string? after)
    {
        IsLoading = true;
        StatusText = Items.Count == 0 ? "Searching..." : "Loading more...";
        try
        {
            var page = await _source.SearchAsync(query, after, PageSize, CancellationToken.None);
            if (gen != _searchGen)
            {
                return;
            }

            var now = _now();
            var known = Items.Select(i => i.PullRequest.Number).ToHashSet();
            foreach (var pr in page.Items.Where(pr => known.Add(pr.Number)))
            {
                Items.Add(new PullRequestRowVm(pr, now));
            }

            _cursor = page.EndCursor;
            HasMore = page.HasNextPage && page.EndCursor is not null;
            SelectedItem ??= Items.FirstOrDefault();
            StatusText = Items.Count == 0
                ? "No pull requests match."
                : $"{Items.Count:N0} of {Math.Max(page.TotalCount, Items.Count):N0}";
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

    private async Task LoadPeopleAsync()
    {
        try
        {
            if (await _source.GetCurrentLoginAsync(CancellationToken.None) is { } me)
            {
                MineHint = $"Mine ({me})";
            }

            foreach (var person in await _source.GetPeopleAsync(CancellationToken.None))
            {
                AuthorOptions.Add(new AuthorOption(person.Display, person.Login));
            }
        }
        catch
        {
            // Convenience only - a typed login still searches.
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
