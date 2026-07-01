using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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

public sealed record PeriodOption(string Label, int? Days, bool IsCustom = false);

public sealed partial class ExtensionFilterItem : ObservableObject
{
    [ObservableProperty]
    private bool _included = true;

    public required string Extension { get; init; }

    public required string Display { get; init; }

    public required string Summary { get; init; }
}

public sealed partial class AuthorFilterItem : ObservableObject
{
    [ObservableProperty]
    private bool _included = true;

    public required string Author { get; init; }

    public required string Summary { get; init; }
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

    public required string Name { get; init; }

    public required string RelativePath { get; init; }

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
    private HistoryResult? _history;
    private bool _ready;
    private bool _suspendRebuild;
    private bool _searching;
    private bool _lastWasSearching;
    private string _query = string.Empty;
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

    private bool Historical => SinceDays.HasValue;

    public async Task LoadAsync(string root)
    {
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
        var days = SinceDays;
        var sw = Stopwatch.StartNew();
        var snapshot = await Task.Run(() => CodeAnalysisRepositoryReader.Load(root, days));
        RecordTiming(sw.ElapsedMilliseconds, snapshot.History?.CommitCount ?? 0);
        _appliedPeriod = SelectedPeriod;
        _appliedDays = days;
        LoadFrom(snapshot.Scan, snapshot.History);
        IsScanning = false;
    }

    public void LoadFrom(ScanResult scan, HistoryResult? history)
    {
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

    private void LoadFilters(ScanResult scan, HistoryResult? history)
    {
        var extensionItems = AnalysisTreeBuilder.ExtensionStats(scan).Select(s => new ExtensionFilterItem
        {
            Extension = s.Extension,
            Display = s.Display,
            Summary = s.Percent >= 0.05
                ? $"{s.FileCount:N0} files - {s.Lines:N0} lines - {s.Percent:0.0}%"
                : $"{s.FileCount:N0} files - {s.Lines:N0} lines",
        }).ToList();
        foreach (var item in extensionItems)
        {
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ExtensionFilterItem.Included))
                {
                    UpdateExtensionToggleState();
                    Rebuild();
                }
            };
        }

        Extensions = new ObservableCollection<ExtensionFilterItem>(extensionItems);
        UpdateExtensionToggleState();

        var authorItems = AnalysisTreeBuilder.AuthorStats(history).Select(a => new AuthorFilterItem
        {
            Author = a.Author,
            Summary = a.Percent >= 0.05 ? $"{a.Commits:N0} commits - {a.Percent:0.0}%" : $"{a.Commits:N0} commits",
        }).ToList();
        foreach (var item in authorItems)
        {
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AuthorFilterItem.Included))
                {
                    UpdateAuthorToggleState();
                    Rebuild();
                }
            };
        }

        Authors = new ObservableCollection<AuthorFilterItem>(authorItems);
        UpdateAuthorToggleState();
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

        Roots = new ObservableCollection<CodeAnalysisRow>(root.Children.Select(BuildRow));
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

    // In-place search: walks the existing rows setting visibility, the substring
    // highlight, and match-path expansion via observable properties - the TreeView
    // keeps its containers, so this is instant even on large trees.
    private void ApplySearch()
    {
        var query = SearchText.Trim();
        var searching = query.Length > 0;
        if (searching && !_lastWasSearching)
        {
            CaptureExpansion();
        }

        _query = query;
        _searching = searching;

        foreach (var row in Roots)
        {
            ApplySearchToRow(row, ancestorMatched: false);
        }

        if (!searching && _lastWasSearching)
        {
            foreach (var row in Roots.SelectMany(r => r.SelfAndDescendants()))
            {
                if (!row.IsFile)
                {
                    row.IsExpanded = _expanded.Contains(row.RelativePath);
                }
            }
        }

        _lastWasSearching = searching;
    }

    // Returns true when the row or any descendant matches the query.
    private bool ApplySearchToRow(CodeAnalysisRow row, bool ancestorMatched)
    {
        var index = _searching ? row.Name.IndexOf(_query, StringComparison.OrdinalIgnoreCase) : -1;
        var matched = index >= 0;
        var descendantMatch = false;
        foreach (var child in row.Children)
        {
            descendantMatch |= ApplySearchToRow(child, ancestorMatched || matched);
        }

        row.NameRuns = BuildNameRuns(row.Name, index, _query.Length);
        row.IsSearchVisible = !_searching || matched || descendantMatch || ancestorMatched;
        if (_searching && !row.IsFile)
        {
            row.IsExpanded = descendantMatch;
        }

        return matched || descendantMatch;
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

    private CodeAnalysisRow BuildRow(AnalysisNode node)
    {
        var showGit = _history is not null;
        var row = new CodeAnalysisRow
        {
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
            row.Children.Add(BuildRow(child));
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
            if (Historical)
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

    private void SetAllExtensions(bool included)
    {
        _suspendRebuild = true;
        foreach (var item in Extensions)
        {
            item.Included = included;
        }

        _suspendRebuild = false;
        UpdateExtensionToggleState();
        Rebuild();
    }

    private void SetAllAuthors(bool included)
    {
        _suspendRebuild = true;
        foreach (var item in Authors)
        {
            item.Included = included;
        }

        _suspendRebuild = false;
        UpdateAuthorToggleState();
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

    partial void OnSelectedGroupingChanged(GroupingOption value) => Rebuild();

    partial void OnSelectedPeriodChanged(PeriodOption value)
    {
        IsCustomPeriod = value.IsCustom;
        _ = ReloadHistoryAsync();
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
        if (_suspendRebuild || !_ready || _scan is null || !GitAvailable)
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
        var sw = Stopwatch.StartNew();
        _history = await Task.Run(() => GitHistory.Load(_root, days));
        RecordTiming(sw.ElapsedMilliseconds, _history?.CommitCount ?? 0);
        _appliedPeriod = SelectedPeriod;
        _appliedDays = days;
        IsScanning = false;
        Rebuild();
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
