using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Budiff.CodeAnalysis;
using Budiff.Git;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace Budiff.App;

public sealed record GroupingOption(string Label, AnalysisGrouping Value);

public sealed record PeriodOption(string Label, int? Days, bool IsCustom = false, bool IsPullRequest = false);

// Common shape of one include/exclude filter entry (extension, author); the
// panel wires all filter lists through the same subscribe/set-all helpers.
public abstract partial class FilterItem : ObservableObject
{
    [ObservableProperty]
    private bool _included = true;

    public required string Summary { get; init; }
}

public sealed class ExtensionFilterItem : FilterItem
{
    public required string Extension { get; init; }

    public required string Display { get; init; }
}

public sealed class AuthorFilterItem : FilterItem
{
    public required string Author { get; init; }
}

public sealed partial class CodeAnalysisRow : ObservableObject
{
    [ObservableProperty]
    private bool _isExpanded;

    // In-place search state: rows are hidden/highlighted without rebuilding the
    // tree, so the TreeView keeps its containers and search stays instant.
    [ObservableProperty]
    private bool _isSearchVisible = true;

    [ObservableProperty]
    private IReadOnlyList<NameRun>? _nameRuns;

    private int _highlightIndex = int.MinValue;
    private int _highlightLength;

    // Search pass scratch: where this row's name matches the query (-1 = no
    // match) and whether any descendant matches.
    public int MatchIndex { get; set; } = -1;

    public bool HasDescendantMatch { get; set; }

    // Rebuilding NameRuns notifies every realized row's inline presenter, so
    // skip it when the highlight span did not actually change.
    public void SetNameHighlight(int index, int length)
    {
        if (index < 0)
        {
            length = 0;
        }

        if (_highlightIndex == index && _highlightLength == length)
        {
            return;
        }

        _highlightIndex = index;
        _highlightLength = length;
        NameRuns = BuildNameRuns(Name, index, length);
    }

    private static IReadOnlyList<NameRun> BuildNameRuns(string name, int index, int length)
    {
        if (index < 0 || length <= 0 || index + length > name.Length)
        {
            return new[] { new NameRun(name, false) };
        }

        var runs = new List<NameRun>(3);
        if (index > 0)
        {
            runs.Add(new NameRun(name[..index], false));
        }

        runs.Add(new NameRun(name.Substring(index, length), true));
        if (index + length < name.Length)
        {
            runs.Add(new NameRun(name[(index + length)..], false));
        }

        return runs;
    }

    public required string Name { get; init; }

    public required string RelativePath { get; init; }

    // Tree level; the row template indents INSIDE the name cell (the
    // TreeViewItem chrome is retemplated away so every row grid spans the
    // full pane width and stays column-aligned with the header).
    public int Depth { get; init; }

    public double IndentWidth => Depth * 16;

    public MaterialIconKind ExpandIcon => IsExpanded ? MaterialIconKind.ChevronDown : MaterialIconKind.ChevronRight;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandIcon));

    public string? FullPath { get; init; }

    public bool IsFile { get; init; }

    public MaterialIconKind Icon { get; init; }

    public string LinesText { get; init; } = string.Empty;

    public string CodeText { get; init; } = string.Empty;

    public string BlankText { get; init; } = string.Empty;

    public string CommentText { get; init; } = string.Empty;

    public string FilesText { get; init; } = string.Empty;

    public string AddedText { get; init; } = string.Empty;

    public string RemovedText { get; init; } = string.Empty;

    public string CommitsText { get; init; } = string.Empty;

    public string AuthorsText { get; init; } = string.Empty;

    public double BarFraction { get; init; }

    public ObservableCollection<CodeAnalysisRow> Children { get; } = new();

    public IEnumerable<CodeAnalysisRow> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var row in child.SelfAndDescendants())
            {
                yield return row;
            }
        }
    }
}

public sealed partial class CodeAnalysisPanelViewModel : ObservableObject
{
    private readonly Func<string, string, Task> _openFile;
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private string _root = string.Empty;
    private ScanResult? _scan;

    // The working-tree scan. PR mode swaps _scan for the PR head's changed
    // files; leaving it restores this one without rescanning the disk.
    private ScanResult? _folderScan;
    private PullRequestRange? _appliedPullRequest;
    private HistoryResult? _history;
    private bool _ready;
    private bool _suspendRebuild;
    private bool _lastWasSearching;
    private PeriodOption _appliedPeriod = null!;
    private int? _appliedDays;
    private double _msPerCommit;
    private int _appliedCommitCount;

    private const int WarnThresholdMs = 3000;

    public CodeAnalysisPanelViewModel(Func<string, string, Task> openFile)
    {
        _openFile = openFile;
        _selectedGrouping = Groupings[0];
        _selectedPeriod = DefaultPeriod;
        _appliedPeriod = DefaultPeriod;
    }

    public Func<string, Task<bool>>? ConfirmExpensive { get; set; }

    // Where pull requests come from (null outside git) and the UI that picks
    // one; both are wired by the shell.
    public IPullRequestSource? PullRequests { get; set; }

    public Func<Task<PullRequestInfo?>>? PickPullRequest { get; set; }

    // The PR the visible analysis was computed for (null = a time period).
    public PullRequestRange? AppliedPullRequest => _appliedPullRequest;

    [ObservableProperty]
    private bool _isPullRequestPeriod;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PullRequestButtonText))]
    [NotifyPropertyChangedFor(nameof(PullRequestTip))]
    private string _pullRequestLabel = string.Empty;

    public string PullRequestButtonText => PullRequestLabel.Length > 0 ? PullRequestLabel : "Choose...";

    public string PullRequestTip => PullRequestLabel.Length > 0 ? PullRequestLabel + " - click to pick another" : "Choose a pull request";

    [ObservableProperty]
    private ObservableCollection<CodeAnalysisRow> _roots = new();

    [ObservableProperty]
    private ObservableCollection<ExtensionFilterItem> _extensions = new();

    [ObservableProperty]
    private ObservableCollection<AuthorFilterItem> _authors = new();

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _summaryHead = "Scanning...";

    [ObservableProperty]
    private string _summaryCode = string.Empty;

    [ObservableProperty]
    private string _summaryBlank = string.Empty;

    [ObservableProperty]
    private string _summaryComment = string.Empty;

    [ObservableProperty]
    private string _summaryTail = string.Empty;

    [ObservableProperty]
    private bool _gitAvailable;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _canSelectAllExtensions;

    [ObservableProperty]
    private bool _canSelectNoneExtensions;

    [ObservableProperty]
    private bool _canSelectAllAuthors;

    [ObservableProperty]
    private bool _canSelectNoneAuthors;

    [ObservableProperty]
    private GroupingOption _selectedGrouping;

    [ObservableProperty]
    private PeriodOption _selectedPeriod;

    [ObservableProperty]
    private AnalysisSortKey _sortKey = AnalysisSortKey.Lines;

    [ObservableProperty]
    private bool _sortAscending;

    public IReadOnlyList<GroupingOption> Groupings { get; } = new[]
    {
        new GroupingOption("Folders", AnalysisGrouping.Folders),
        new GroupingOption("Projects", AnalysisGrouping.Projects),
    };

    public IReadOnlyList<PeriodOption> Periods { get; } = new[]
    {
        new PeriodOption("All time", null),
        new PeriodOption("Last week", 7),
        new PeriodOption("Last month", 30),
        new PeriodOption("Last 3 months", 90),
        new PeriodOption("Custom...", null, IsCustom: true),
        new PeriodOption("Pull request...", null, IsPullRequest: true),
    };

    private PeriodOption DefaultPeriod => Periods.First(p => p.Days == 30);

    public string NameGlyph => Glyph(AnalysisSortKey.Name);

    public string LinesGlyph => Glyph(AnalysisSortKey.Lines);

    public string CodeGlyph => Glyph(AnalysisSortKey.Code);

    public string BlankGlyph => Glyph(AnalysisSortKey.Blank);

    public string CommentGlyph => Glyph(AnalysisSortKey.Comment);

    public string FilesGlyph => Glyph(AnalysisSortKey.Files);

    public string AddedGlyph => Glyph(AnalysisSortKey.Added);

    public string RemovedGlyph => Glyph(AnalysisSortKey.Removed);

    public string CommitsGlyph => Glyph(AnalysisSortKey.Commits);

    public string AuthorsGlyph => Glyph(AnalysisSortKey.Authors);

    [ObservableProperty]
    private bool _isCustomPeriod;

    [ObservableProperty]
    private string _customDaysText = "14";

    private int? SinceDays
    {
        get
        {
            if (!SelectedPeriod.IsCustom)
            {
                return SelectedPeriod.Days;
            }

            // 0 = all time; invalid input falls back to the last applied window.
            if (int.TryParse(CustomDaysText, out var days) && days >= 0)
            {
                return days == 0 ? null : days;
            }

            return _appliedDays;
        }
    }

    // PR mode is period-like too: churn columns show what the PR changed.
    private bool Historical => _appliedPullRequest is not null || SinceDays.HasValue;

    // The window the visible analysis was actually computed with (null = all
    // time). The drill-in diff aligns its default base to this.
    public int? AppliedSinceDays => _appliedDays;

    // The currently SELECTED window (may not be applied yet).
    public int? SelectedSinceDays => SinceDays;

    // Raised after a (re)load applied a new history window - lets the shell
    // re-run an open period-aligned diff.
    public event Action? HistoryApplied;

    public async Task LoadAsync(string root)
    {
        // A refresh of the same folder keeps the PR view; a new folder drops it.
        var reapply = SelectedPeriod.IsPullRequest && string.Equals(root, _root, StringComparison.OrdinalIgnoreCase)
            ? _appliedPullRequest?.PullRequest
            : null;
        if (SelectedPeriod.IsPullRequest && reapply is null)
        {
            SelectPeriodQuietly(DefaultPeriod);
        }

        ClearPullRequest();
        _root = root;
        _ready = false;
        Roots.Clear();
        Extensions.Clear();
        Authors.Clear();

        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            ShowMessage("No folder is open.");
            return;
        }

        IsScanning = true;
        ShowMessage("Scanning...");
        try
        {
            // Re-applying a PR only needs the folder scan; skip an all-time history.
            var days = reapply is not null ? DefaultPeriod.Days : SinceDays;
            var sw = Stopwatch.StartNew();
            var snapshot = await Task.Run(() => CodeAnalysisRepositoryReader.Load(root, days));
            RecordTiming(sw.ElapsedMilliseconds, snapshot.History?.CommitCount ?? 0);
            _appliedPeriod = SelectedPeriod;
            _appliedDays = days;
            LoadFrom(snapshot.Scan, snapshot.History);
        }
        catch (Exception ex)
        {
            ShowMessage("Scan failed: " + ex.Message);
            return;
        }
        finally
        {
            // Never leave the spinner stuck when the scan throws.
            IsScanning = false;
        }

        if (reapply is not null)
        {
            await ApplyPullRequestAsync(reapply);
            return;
        }

        HistoryApplied?.Invoke();
    }

    public void LoadFrom(ScanResult scan, HistoryResult? history)
    {
        _folderScan = scan;
        _scan = scan;
        _history = history;
        GitAvailable = scan.HasGit && history is not null;
        LoadFilters(scan, history);
        _ready = true;
        Rebuild();
    }

    public Task OpenAsync(CodeAnalysisRow? row)
        => row is { IsFile: true, FullPath: { } path } ? _openFile(path, row.RelativePath) : Task.CompletedTask;

    [RelayCommand]
    private void Sort(AnalysisSortKey key)
    {
        if (key == SortKey)
        {
            SortAscending = !SortAscending;
        }
        else
        {
            SortKey = key;
            SortAscending = key == AnalysisSortKey.Name;
        }

        Rebuild();
    }

    [RelayCommand]
    private void ResetView()
    {
        _suspendRebuild = true;
        SortKey = AnalysisSortKey.Lines;
        SortAscending = false;
        SelectedGrouping = Groupings[0];
        SearchText = string.Empty;
        foreach (var item in Extensions)
        {
            item.Included = true;
        }

        foreach (var item in Authors)
        {
            item.Included = true;
        }

        _suspendRebuild = false;
        UpdateExtensionToggleState();
        UpdateAuthorToggleState();
        SelectedPeriod = DefaultPeriod;
        Rebuild();
    }

    [RelayCommand]
    private void SelectAllExtensions() => SetAllExtensions(true);

    [RelayCommand]
    private void SelectNoExtensions() => SetAllExtensions(false);

    [RelayCommand]
    private void SelectAllAuthors() => SetAllAuthors(true);

    [RelayCommand]
    private void SelectNoneAuthors() => SetAllAuthors(false);

    private void LoadFilters(ScanResult scan, HistoryResult? history, bool preserveSelection = false)
    {
        var excludedExtensions = preserveSelection
            ? Extensions.Where(e => !e.Included).Select(e => e.Extension).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var excludedAuthors = preserveSelection
            ? Authors.Where(a => !a.Included).Select(a => a.Author).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var periodAware = history is not null && Historical;
        var lineLabel = periodAware ? "changed lines" : "lines";
        var extensionItems = AnalysisTreeBuilder.ExtensionStats(scan, history, allTime: !periodAware).Select(s => new ExtensionFilterItem
        {
            Extension = s.Extension,
            Display = s.Display,
            Included = !excludedExtensions.Contains(s.Extension),
            Summary = s.Percent >= 0.05
                ? $"{s.FileCount:N0} files - {s.Lines:N0} {lineLabel} - {s.Percent:0.0}%"
                : $"{s.FileCount:N0} files - {s.Lines:N0} {lineLabel}",
        }).ToList();
        WireFilterItems(extensionItems, UpdateExtensionToggleState);
        Extensions = new ObservableCollection<ExtensionFilterItem>(extensionItems);
        UpdateExtensionToggleState();

        var authorItems = AnalysisTreeBuilder.AuthorStats(history).Select(a => new AuthorFilterItem
        {
            Author = a.Author,
            Included = !excludedAuthors.Contains(a.Author),
            Summary = a.Percent >= 0.05 ? $"{a.Commits:N0} commits - {a.Percent:0.0}%" : $"{a.Commits:N0} commits",
        }).ToList();
        WireFilterItems(authorItems, UpdateAuthorToggleState);
        Authors = new ObservableCollection<AuthorFilterItem>(authorItems);
        UpdateAuthorToggleState();
    }

    private void WireFilterItems(IEnumerable<FilterItem> items, Action updateToggle)
    {
        foreach (var item in items)
        {
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(FilterItem.Included))
                {
                    updateToggle();
                    Rebuild();
                }
            };
        }
    }

    private void Rebuild()
    {
        if (_suspendRebuild || !_ready || _scan is null)
        {
            return;
        }

        CaptureExpansion();

        var included = Extensions.Where(x => x.Included).Select(x => x.Extension).ToHashSet(StringComparer.Ordinal);
        ISet<string>? extensionFilter = included.Count == Extensions.Count ? null : included;
        var pickedAuthors = Authors.Where(a => a.Included).Select(a => a.Author).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ISet<string>? authorFilter = Authors.Count > 0 && pickedAuthors.Count < Authors.Count ? pickedAuthors : null;

        // The tree is always built UNFILTERED by search; the search pass below hides
        // rows in place so keystrokes never rebuild the tree (instant search).
        var root = AnalysisTreeBuilder.Build(
            _scan,
            _history,
            SelectedGrouping.Value,
            SortKey,
            SortAscending,
            extensionFilter,
            search: null,
            allTime: !Historical,
            includedAuthors: authorFilter);

        Roots = new ObservableCollection<CodeAnalysisRow>(root.Children.Select(n => BuildRow(n, depth: 0)));
        SetSummary(root);
        ApplySearch();
    }

    private void CaptureExpansion()
    {
        if (_lastWasSearching)
        {
            return;
        }

        foreach (var row in Roots.SelectMany(r => r.SelfAndDescendants()))
        {
            if (row.IsExpanded)
            {
                _expanded.Add(row.RelativePath);
            }
            else
            {
                _expanded.Remove(row.RelativePath);
            }
        }
    }

    // In-place search: RowSearch walks the existing rows setting visibility,
    // the substring highlight, and match-path expansion via observable
    // properties - the TreeView keeps its containers, so this is instant even
    // on large trees. The threshold policy lives on RowSearch.ExpandThreshold.
    private void ApplySearch()
    {
        var query = SearchText.Trim();
        var searching = query.Length > 0;
        if (searching && !_lastWasSearching)
        {
            CaptureExpansion();
        }

        var matches = RowSearch.ComputeMatches(Roots, query);
        var narrow = searching && matches <= RowSearch.ExpandThreshold;
        var applyExpansion = searching || _lastWasSearching;
        RowSearch.Apply(Roots, query, filter: narrow, applyExpansion, _expanded);
        _lastWasSearching = searching;
    }

    private CodeAnalysisRow BuildRow(AnalysisNode node, int depth)
    {
        var showGit = _history is not null;
        var row = new CodeAnalysisRow
        {
            Depth = depth,
            Name = node.Name,
            RelativePath = node.RelativePath,
            IsFile = node.IsFile,
            FullPath = node.IsFile ? Path.Combine(_root, node.RelativePath.Replace('/', Path.DirectorySeparatorChar)) : null,
            Icon = IconFor(node),
            LinesText = node.IsBinary ? "binary" : N(node.Lines.Total),
            CodeText = node.IsBinary ? string.Empty : N(node.Lines.Code),
            BlankText = node.IsBinary ? string.Empty : N(node.Lines.Blank),
            CommentText = node.IsBinary ? string.Empty : N(node.Lines.Comment),
            FilesText = node.IsFile ? string.Empty : N(node.FileCount),
            AddedText = showGit && node.Added > 0 ? "+" + N(node.Added) : string.Empty,
            RemovedText = showGit && node.Removed > 0 ? "-" + N(node.Removed) : string.Empty,
            CommitsText = showGit && node.CommitCount > 0 ? N(node.CommitCount) : string.Empty,
            AuthorsText = showGit && node.AuthorCount > 0 ? N(node.AuthorCount) : string.Empty,
            BarFraction = node.BarFraction,
            IsExpanded = _expanded.Contains(node.RelativePath),
            NameRuns = new[] { new NameRun(node.Name, false) },
        };

        foreach (var child in node.Children)
        {
            row.Children.Add(BuildRow(child, depth + 1));
        }

        return row;
    }

    private void SetSummary(AnalysisNode root)
    {
        SummaryHead = $"{root.FileCount:N0} files - {N(root.Lines.Total)} lines (";
        SummaryCode = $"{N(root.Lines.Code)} code";
        SummaryBlank = $" / {N(root.Lines.Blank)} blank";
        SummaryComment = $" / {N(root.Lines.Comment)} comment";

        var tail = ")";
        if (_history is not null)
        {
            if (_appliedPullRequest is { } pr)
            {
                tail += $"  -  +{N(root.Added)} / -{N(root.Removed)} in PR #{pr.PullRequest.Number}";
            }
            else if (Historical)
            {
                tail += $"  -  +{N(root.Added)} / -{N(root.Removed)} in window";
            }

            tail += $"  -  {root.CommitCount:N0} commits, {root.AuthorCount:N0} authors";
        }
        else if (!GitAvailable)
        {
            tail += "  -  no git repository";
        }

        if (_scan?.Truncated == true)
        {
            tail += "  -  list capped";
        }

        SummaryTail = tail;
    }

    private void ShowMessage(string message)
    {
        SummaryHead = message;
        SummaryCode = SummaryBlank = SummaryComment = SummaryTail = string.Empty;
    }

    private void SetAllExtensions(bool included) => SetAllFilterItems(Extensions, included, UpdateExtensionToggleState);

    private void SetAllAuthors(bool included) => SetAllFilterItems(Authors, included, UpdateAuthorToggleState);

    private void SetAllFilterItems(IEnumerable<FilterItem> items, bool included, Action updateToggle)
    {
        _suspendRebuild = true;
        foreach (var item in items)
        {
            item.Included = included;
        }

        _suspendRebuild = false;
        updateToggle();
        Rebuild();
    }

    private void UpdateExtensionToggleState()
    {
        CanSelectAllExtensions = Extensions.Any(e => !e.Included);
        CanSelectNoneExtensions = Extensions.Any(e => e.Included);
    }

    private void UpdateAuthorToggleState()
    {
        CanSelectAllAuthors = Authors.Any(a => !a.Included);
        CanSelectNoneAuthors = Authors.Any(a => a.Included);
    }

    partial void OnSortKeyChanged(AnalysisSortKey value) => NotifySortGlyphs();

    partial void OnSortAscendingChanged(bool value) => NotifySortGlyphs();

    partial void OnSearchTextChanged(string value)
    {
        if (_ready && !_suspendRebuild)
        {
            ApplySearch();
        }
    }

    // The segmented Folders/Projects toggle highlights the active side.
    public bool IsFoldersGrouping => SelectedGrouping.Value == AnalysisGrouping.Folders;

    public bool IsProjectsGrouping => SelectedGrouping.Value == AnalysisGrouping.Projects;

    [RelayCommand]
    private void SelectGrouping(GroupingOption option) => SelectedGrouping = option;

    partial void OnSelectedGroupingChanged(GroupingOption value)
    {
        OnPropertyChanged(nameof(IsFoldersGrouping));
        OnPropertyChanged(nameof(IsProjectsGrouping));
        Rebuild();
    }

    partial void OnSelectedPeriodChanged(PeriodOption value)
    {
        IsCustomPeriod = value.IsCustom;
        IsPullRequestPeriod = value.IsPullRequest;
        if (value.IsPullRequest)
        {
            if (!_suspendRebuild)
            {
                _ = ChoosePullRequestAsync();
            }

            return;
        }

        _ = ReloadHistoryAsync();
    }

    // Opens the picker (from the Period entry or the PR button beside it).
    // Cancelling the very first pick returns to the previous period.
    [RelayCommand]
    private async Task ChoosePullRequestAsync()
    {
        var picked = PickPullRequest is null ? null : await PickPullRequest();
        if (picked is not null)
        {
            await ApplyPullRequestAsync(picked);
        }
        else if (_appliedPullRequest is null)
        {
            SelectPeriodQuietly(_appliedPeriod.IsPullRequest ? DefaultPeriod : _appliedPeriod);
        }
    }

    public async Task ApplyPullRequestAsync(PullRequestInfo pullRequest)
    {
        if (PullRequests is null || _folderScan is null || !GitAvailable)
        {
            return;
        }

        IsScanning = true;
        ShowMessage($"Loading PR #{pullRequest.Number}...");
        try
        {
            var range = await PullRequests.ResolveAsync(pullRequest, CancellationToken.None);
            var root = _root;
            var snapshot = await Task.Run(() => CodeAnalysisRepositoryReader.LoadPullRequest(root, range));
            _appliedPullRequest = range;
            _scan = snapshot.Scan;
            _history = snapshot.History;
            SelectPeriodQuietly(Periods.First(p => p.IsPullRequest));
            _appliedPeriod = SelectedPeriod;
            _appliedDays = null;
            PullRequestLabel = $"#{pullRequest.Number} {pullRequest.Title}";
            LoadFilters(_scan, _history, preserveSelection: true);
        }
        catch (Exception ex)
        {
            // Keep whatever was showing; the summary line says why.
            if (_appliedPullRequest is null)
            {
                SelectPeriodQuietly(_appliedPeriod.IsPullRequest ? DefaultPeriod : _appliedPeriod);
            }

            Rebuild();
            ShowMessage($"Could not load PR #{pullRequest.Number}: {ex.Message}");
            return;
        }
        finally
        {
            IsScanning = false;
        }

        Rebuild();
        HistoryApplied?.Invoke();
    }

    private void SelectPeriodQuietly(PeriodOption period)
    {
        var suspended = _suspendRebuild;
        _suspendRebuild = true;
        SelectedPeriod = period;
        _suspendRebuild = suspended;
    }

    private void ClearPullRequest()
    {
        _appliedPullRequest = null;
        PullRequestLabel = string.Empty;
        if (_folderScan is not null)
        {
            _scan = _folderScan;
        }
    }

    partial void OnCustomDaysTextChanged(string value)
    {
        if (!IsCustomPeriod || _suspendRebuild)
        {
            return;
        }

        // Reload only for a valid, actually-changed window.
        if (int.TryParse(value, out var days) && days >= 0 && (days == 0 ? null : (int?)days) != _appliedDays)
        {
            _ = ReloadHistoryAsync();
        }
    }

    private async Task ReloadHistoryAsync()
    {
        if (_suspendRebuild || !_ready || _folderScan is null || !GitAvailable)
        {
            Rebuild();
            return;
        }

        var days = SinceDays;
        if (ConfirmExpensive is not null && _msPerCommit > 0)
        {
            var count = await Task.Run(() => GitHistory.CountCommits(_root, days));
            var estimateMs = count * _msPerCommit;
            if (estimateMs > WarnThresholdMs && count > _appliedCommitCount)
            {
                var seconds = Math.Max(1, (int)Math.Round(estimateMs / 1000.0));
                var ok = await ConfirmExpensive($"{SelectedPeriod.Label} covers {count:N0} commits and may take about {seconds}s to analyze. Continue?");
                if (!ok)
                {
                    _suspendRebuild = true;
                    SelectedPeriod = _appliedPeriod;
                    _suspendRebuild = false;
                    return;
                }
            }
        }

        IsScanning = true;
        try
        {
            var sw = Stopwatch.StartNew();
            _history = await Task.Run(() => GitHistory.Load(_root, days));
            RecordTiming(sw.ElapsedMilliseconds, _history?.CommitCount ?? 0);
            ClearPullRequest();
            _appliedPeriod = SelectedPeriod;
            _appliedDays = days;
            LoadFilters(_folderScan, _history, preserveSelection: true);
        }
        catch (Exception ex)
        {
            ShowMessage("Reload failed: " + ex.Message);
            return;
        }
        finally
        {
            // Never leave the spinner stuck when the reload throws.
            IsScanning = false;
        }

        Rebuild();
        HistoryApplied?.Invoke();
    }

    private void RecordTiming(long elapsedMs, int commits)
    {
        _appliedCommitCount = commits;
        if (commits > 0)
        {
            _msPerCommit = (double)elapsedMs / commits;
        }
    }

    private void NotifySortGlyphs()
    {
        OnPropertyChanged(nameof(NameGlyph));
        OnPropertyChanged(nameof(LinesGlyph));
        OnPropertyChanged(nameof(CodeGlyph));
        OnPropertyChanged(nameof(BlankGlyph));
        OnPropertyChanged(nameof(CommentGlyph));
        OnPropertyChanged(nameof(FilesGlyph));
        OnPropertyChanged(nameof(AddedGlyph));
        OnPropertyChanged(nameof(RemovedGlyph));
        OnPropertyChanged(nameof(CommitsGlyph));
        OnPropertyChanged(nameof(AuthorsGlyph));
    }

    private string Glyph(AnalysisSortKey key)
        => SortKey != key ? string.Empty : (SortAscending ? "▲" : "▼");

    private static MaterialIconKind IconFor(AnalysisNode node) => node.Kind switch
    {
        AnalysisNodeKind.Project => MaterialIconKind.Package,
        AnalysisNodeKind.Ungrouped => MaterialIconKind.FolderQuestionOutline,
        AnalysisNodeKind.Folder => MaterialIconKind.FolderOutline,
        AnalysisNodeKind.File => MaterialIconKind.FileOutline,
        _ => MaterialIconKind.FolderOpenOutline,
    };

    private static string N(long value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
