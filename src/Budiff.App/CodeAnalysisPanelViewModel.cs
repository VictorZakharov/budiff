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

namespace Budiff.App;

public sealed record GroupingOption(string Label, AnalysisGrouping Value);

public sealed record PeriodOption(string Label, int? Days, bool IsCustom = false, bool IsPullRequest = false, bool IsCommit = false);

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

    public AppIconKind ExpandIcon => IsExpanded ? AppIconKind.ChevronDown : AppIconKind.ChevronRight;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandIcon));

    public string? FullPath { get; set; }

    // The analysis node it shows (its numbers, for a folder summary).
    internal AnalysisNode? Node { get; set; }

    public bool IsFile { get; init; }

    public AppIconKind Icon { get; set; }

    // A file type's own colour; null keeps the muted text colour.
    public IBrush? IconTint { get; set; }

    public string LinesText { get; set; } = string.Empty;

    public string CodeText { get; set; } = string.Empty;

    public string BlankText { get; set; } = string.Empty;

    public string CommentText { get; set; } = string.Empty;

    public string FilesText { get; set; } = string.Empty;

    public string AddedText { get; set; } = string.Empty;

    public string RemovedText { get; set; } = string.Empty;

    public string CommitsText { get; set; } = string.Empty;

    public string AuthorsText { get; set; } = string.Empty;

    // How long ago it last changed: <1min, 5min, 3hr, 2d, 4mo, 1y.
    public string RecencyText { get; set; } = string.Empty;

    public double BarFraction { get; set; }

    // What this row shows in a bird's-eye number column (1 = Lines ... 9 =
    // Authors): the name runs on over empty ones and up to the first number
    // (NameExtent).
    internal string CellText(int column) => column switch
    {
        1 => LinesText,
        2 => CodeText,
        3 => BlankText,
        4 => CommentText,
        5 => FilesText,
        6 => AddedText,
        7 => RemovedText,
        8 => CommitsText,
        9 => AuthorsText,
        10 => RecencyText,
        _ => string.Empty,
    };

    public ObservableCollection<CodeAnalysisRow> Children { get; } = new();

    // A refresh: whether a freshly built row for the same path shows the
    // same, and taking over what it shows - the row object (and the tree's
    // container for it) stays, so the tree does not move.
    internal bool Shows(CodeAnalysisRow fresh)
        => Name == fresh.Name && IsFile == fresh.IsFile && Icon == fresh.Icon && Equals(IconTint, fresh.IconTint)
           && LinesText == fresh.LinesText && CodeText == fresh.CodeText && BlankText == fresh.BlankText
           && CommentText == fresh.CommentText && FilesText == fresh.FilesText && AddedText == fresh.AddedText
           && RemovedText == fresh.RemovedText && CommitsText == fresh.CommitsText && AuthorsText == fresh.AuthorsText
           && RecencyText == fresh.RecencyText && BarFraction.Equals(fresh.BarFraction);

    internal void Take(CodeAnalysisRow fresh)
    {
        Node = fresh.Node;
        FullPath = fresh.FullPath;
        if (Shows(fresh))
        {
            return;
        }

        Icon = fresh.Icon;
        IconTint = fresh.IconTint;
        LinesText = fresh.LinesText;
        CodeText = fresh.CodeText;
        BlankText = fresh.BlankText;
        CommentText = fresh.CommentText;
        FilesText = fresh.FilesText;
        AddedText = fresh.AddedText;
        RemovedText = fresh.RemovedText;
        CommitsText = fresh.CommitsText;
        AuthorsText = fresh.AuthorsText;
        RecencyText = fresh.RecencyText;
        BarFraction = fresh.BarFraction;
        OnPropertyChanged(string.Empty);
    }

    // Makes rows (one level, then each kept row's children) match fresh ones:
    // a row whose path is still there stays and takes the new numbers, a
    // gone one goes, a new one comes in at its place.
    internal static void Merge(ObservableCollection<CodeAnalysisRow> rows, IList<CodeAnalysisRow> fresh)
    {
        var keep = new HashSet<string>(fresh.Select(r => r.RelativePath), StringComparer.OrdinalIgnoreCase);
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(rows[i].RelativePath))
            {
                rows.RemoveAt(i);
            }
        }

        for (var i = 0; i < fresh.Count; i++)
        {
            var path = fresh[i].RelativePath;
            var at = -1;
            for (var j = i; j < rows.Count; j++)
            {
                if (string.Equals(rows[j].RelativePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    at = j;
                    break;
                }
            }

            if (at < 0 || rows[at].IsFile != fresh[i].IsFile || rows[at].Name != fresh[i].Name)
            {
                if (at >= 0)
                {
                    rows.RemoveAt(at);
                }

                rows.Insert(i, fresh[i]);
                continue;
            }

            if (at != i)
            {
                rows.Move(at, i);
            }

            rows[i].Take(fresh[i]);
            Merge(rows[i].Children, fresh[i].Children);
        }
    }

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

    // The next rebuild is a refresh's: merged into the rows shown, not
    // replacing them. Around the merge, the window keeps the selected row
    // where it was on screen.
    private bool _mergeRows;

    internal event Action? TreeMerging;

    internal event Action? TreeMerged;
    private string _root = string.Empty;
    private ScanResult? _scan;

    // The working-tree scan. PR mode swaps _scan for the PR head's changed
    // files; leaving it restores this one without rescanning the disk.
    private ScanResult? _folderScan;
    private PullRequestRange? _appliedPullRequest;

    // Commit mode (a window opened on one commit): the commit, kept to apply
    // again when its period entry is picked again, and whether it is applied.
    private CommitDetails? _commit;
    private CommitDetails? _appliedCommit;
    private HistoryResult? _history;
    private bool _ready;
    private bool _suspendRebuild;
    private bool _lastWasSearching;
    private PeriodOption _appliedPeriod = null!;
    private int? _appliedDays;
    private double _msPerCommit;
    private int _appliedCommitCount;

    private const int WarnThresholdMs = 3000;

    // One load at a time: a newer one - a period or pull request picked while
    // the first load still runs - cancels the older, whose results are then
    // dropped (git may finish its step before it notices the cancel).
    private CancellationTokenSource? _loadCts;
    private int _loadGen;

    // Tests: runs on the load's thread before a folder load (root, days,
    // token) - holds a load to pick a period while it runs.
    internal static Action<string, int?, CancellationToken>? FolderLoadHook { get; set; }

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

    // The commit the visible analysis was computed for (null = not commit mode).
    public CommitDetails? AppliedCommit => _appliedCommit;

    // Whether the working tree was scanned (a window opened on a commit skips it).
    internal bool HasFolderScan => _folderScan is not null;

    [ObservableProperty]
    private bool _isCommitPeriod;

    public string CommitButtonText => _commit is { } commit ? commit.ShortSha + " " + commit.Subject : string.Empty;

    public string CommitTip => _commit is { } commit
        ? $"{commit.ShortSha} - {commit.Author}, {commit.When.LocalDateTime:yyyy-MM-dd HH:mm}\n\n{commit.Message}"
        : string.Empty;

    [ObservableProperty]
    private ObservableCollection<CodeAnalysisRow> _roots = new();

    [ObservableProperty]
    private ObservableCollection<ExtensionFilterItem> _extensions = new();

    [ObservableProperty]
    private ObservableCollection<AuthorFilterItem> _authors = new();

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _summaryText = "Scanning...";

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

    // By name A-Z by default (folders first): the tree reads like a file
    // manager; a column header sorts by its numbers.
    internal const AnalysisSortKey DefaultSortKey = AnalysisSortKey.Name;

    [ObservableProperty]
    private AnalysisSortKey _sortKey = DefaultSortKey;

    [ObservableProperty]
    private bool _sortAscending = true;

    public IReadOnlyList<GroupingOption> Groupings { get; } = new[]
    {
        new GroupingOption("Folders", AnalysisGrouping.Folders),
        new GroupingOption("Projects", AnalysisGrouping.Projects),
    };

    // The "Commit" entry joins the list once a window is opened on a commit.
    public ObservableCollection<PeriodOption> Periods { get; } = new()
    {
        new PeriodOption("All time", null),
        new PeriodOption("Last week", 7),
        new PeriodOption("Last month", 30),
        new PeriodOption("Last 3 months", 90),
        new PeriodOption("Custom...", null, IsCustom: true),
        new PeriodOption("Pull request...", null, IsPullRequest: true),
    };

    private PeriodOption DefaultPeriod => Periods.First(p => p.Days == 30);

    private static readonly PeriodOption CommitPeriod = new("Commit", null, IsCommit: true);

    // Opens on this commit: what it changed, against its parent. Call before
    // the first LoadAsync, which then scans for it instead of a period.
    public void StartWithCommit(CommitDetails commit)
    {
        _commit = commit;
        if (!Periods.Contains(CommitPeriod))
        {
            Periods.Add(CommitPeriod);
        }

        OnPropertyChanged(nameof(CommitButtonText));
        OnPropertyChanged(nameof(CommitTip));
        SelectPeriodQuietly(CommitPeriod);
        IsCommitPeriod = true;
    }

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

    public string RecencyGlyph => Glyph(AnalysisSortKey.Recency);

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
    private bool Historical => _appliedPullRequest is not null || _appliedCommit is not null || SinceDays.HasValue;

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

        var reapplyCommit = SelectedPeriod.IsCommit ? _commit : null;
        if (reapplyCommit is null)
        {
            ForgetCommitReturn();
        }

        // A refresh of the same folder keeps what the reader set up: the tree
        // stays on screen while it scans, then only rows whose numbers changed
        // change (see CodeAnalysisRow.Merge) - open folders, the selection and
        // the scroll stay; so do the filters and the sort.
        var refresh = string.Equals(root, _root, StringComparison.OrdinalIgnoreCase) && Roots.Count > 0;
        CaptureExpansion();
        ClearPullRequest();
        _root = root;
        _ready = false;
        _mergeRows = refresh;
        if (!refresh)
        {
            Roots = new ObservableCollection<CodeAnalysisRow>();
            _expanded.Clear();
            Extensions.Clear();
            Authors.Clear();
        }

        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            ShowMessage("No folder is open.");
            return;
        }

        // A refreshed PR is read again from GitHub and loaded at its head
        // now; like a commit it needs no working-tree scan (one runs when a
        // time period is picked later).
        if (reapply is not null)
        {
            _folderScan = null;
            await ApplyPullRequestAsync(reapply, refresh: true);
            return;
        }

        // A commit needs only its own files: no working-tree scan and no
        // period history (both load if a time period is picked later).
        if (reapplyCommit is not null)
        {
            _folderScan = null;
            await ApplyCommitAsync(reapplyCommit);
            return;
        }

        var (gen, ct) = StartLoad();
        ShowMessage("Scanning...");
        try
        {
            var days = SinceDays;
            var sw = Stopwatch.StartNew();
            var snapshot = await Task.Run(() =>
            {
                FolderLoadHook?.Invoke(root, days, ct);
                return CodeAnalysisRepositoryReader.Load(root, days, ct);
            });
            if (!IsCurrentLoad(gen))
            {
                return;
            }

            RecordTiming(sw.ElapsedMilliseconds, snapshot.History?.CommitCount ?? 0);
            _appliedPeriod = SelectedPeriod;
            _appliedDays = days;
            LoadFrom(snapshot.Scan, snapshot.History, keepFilters: refresh);
        }
        catch (Exception) when (!IsCurrentLoad(gen))
        {
            return;
        }
        catch (Exception ex)
        {
            ShowMessage("Scan failed: " + ex.Message);
            return;
        }
        finally
        {
            // Never leave the spinner stuck when the scan throws.
            EndLoad(gen);
        }

        HistoryApplied?.Invoke();
    }

    public void LoadFrom(ScanResult scan, HistoryResult? history, bool keepFilters = false)
    {
        _folderScan = scan;
        _scan = scan;
        _history = history;
        GitAvailable = scan.HasGit && history is not null;
        LoadFilters(scan, history, preserveSelection: keepFilters);
        _ready = true;
        Rebuild();
    }

    public Task OpenAsync(CodeAnalysisRow? row)
        => row is { IsFile: true, FullPath: { } path } ? _openFile(path, row.RelativePath) : Task.CompletedTask;

    // A folder's (or project's) changes in the applied period, from the
    // numbers its rows were built from. A file whose every line is added is
    // new. A deleted file is not in the tree: it is a path the period's net
    // diff only removes lines from that the scan did not find - counted only
    // when the period has a start to delete from (not all time) and the scan
    // is complete.
    internal FolderSummary SummarizeFolder(CodeAnalysisRow row)
    {
        var path = row.RelativePath.Replace('\\', '/');
        var files = new List<FolderSummaryFile>();
        var fileCount = 0;
        if (row.Node is { } node)
        {
            foreach (var leaf in Leaves(node))
            {
                fileCount++;
                if (leaf.Added + leaf.Removed == 0)
                {
                    continue;
                }

                var isNew = leaf.Removed == 0 && leaf.Added >= leaf.Lines.Total;
                files.Add(new FolderSummaryFile
                {
                    Path = Under(path, leaf.RelativePath),
                    Change = isNew ? FolderFileChange.New : FolderFileChange.Modified,
                    Added = leaf.Added,
                    Removed = leaf.Removed,
                });
            }
        }

        var windowed = _appliedPullRequest is not null || _appliedCommit is not null || _appliedDays is > 0;
        if (_history is not null && windowed && _scan is { Truncated: false } scan && row.Node?.Kind is AnalysisNodeKind.Folder or AnalysisNodeKind.Project)
        {
            var scanned = new HashSet<string>(scan.Files.Select(f => f.RelativePath), StringComparer.OrdinalIgnoreCase);
            var prefix = path.Length == 0 ? string.Empty : path + "/";
            foreach (var (gone, h) in _history.ByPath)
            {
                if (h is { Added: 0, Removed: > 0 }
                    && gone.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && !scanned.Contains(gone))
                {
                    files.Add(new FolderSummaryFile { Path = Under(path, gone), Change = FolderFileChange.Deleted, Removed = h.Removed });
                }
            }
        }

        // Biggest change first, then by path.
        files.Sort((a, b) =>
        {
            var byChurn = (b.Added + b.Removed).CompareTo(a.Added + a.Removed);
            return byChurn != 0 ? byChurn : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        });

        return new FolderSummary
        {
            Path = path.Length == 0 ? Path.GetFileName(Path.TrimEndingDirectorySeparator(_root)) : path,
            Period = DescribeAppliedPeriod(),
            Files = fileCount,
            Changed = files.Count,
            New = files.Count(f => f.Change == FolderFileChange.New),
            Modified = files.Count(f => f.Change == FolderFileChange.Modified),
            Deleted = files.Count(f => f.Change == FolderFileChange.Deleted),
            LinesAdded = files.Sum(f => f.Added),
            LinesRemoved = files.Sum(f => f.Removed),
            HasHistory = _history is not null,
            ChangedFiles = files.Take(FolderSummary.MaxListed).ToList(),
        };

        static IEnumerable<AnalysisNode> Leaves(AnalysisNode node)
            => node.IsFile ? new[] { node } : node.Children.SelectMany(Leaves);

        static string Under(string folder, string file)
            => folder.Length > 0 && file.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase) ? file[(folder.Length + 1)..] : file;
    }

    // What the applied numbers cover, in words.
    private string DescribeAppliedPeriod()
        => _appliedPullRequest is not null ? PullRequestButtonText
            : _appliedCommit is not null ? "Commit " + CommitButtonText
            : _appliedDays is not > 0 ? "All time"
            : _appliedPeriod.IsCustom ? $"Last {_appliedDays} days"
            : _appliedPeriod.Label;

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

    // Anything ResetView would change: the filter box's X shows while true.
    public bool CanResetView =>
        !string.IsNullOrEmpty(SearchText)
        || SortKey != DefaultSortKey || !SortAscending
        || SelectedGrouping != Groupings[0]
        || SelectedPeriod != DefaultPeriod
        || Extensions.Any(e => !e.Included)
        || Authors.Any(a => !a.Included);

    [RelayCommand]
    private void ResetView()
    {
        _suspendRebuild = true;
        SortKey = DefaultSortKey;
        SortAscending = true;
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
        OnPropertyChanged(nameof(CanResetView));
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

        var fresh = root.Children.Select(n => BuildRow(n, depth: 0)).ToList();
        if (_mergeRows && Roots.Count > 0)
        {
            TreeMerging?.Invoke();
            CodeAnalysisRow.Merge(Roots, fresh);
            TreeMerged?.Invoke();
        }
        else
        {
            Roots = new ObservableCollection<CodeAnalysisRow>(fresh);
        }

        _mergeRows = false;
        SetSummary(root);
        ApplySearch();
        _lastRoot = root;
        RebuildCity();
    }

    // Expands every folder, and remembers it across tree rebuilds.
    internal void ExpandAll()
    {
        foreach (var row in Roots.SelectMany(r => r.SelfAndDescendants()).Where(r => !r.IsFile))
        {
            row.IsExpanded = true;
            _expanded.Add(row.RelativePath);
        }
    }

    // A folder's right-click menu: it and every folder under it expand (or
    // collapse); folders elsewhere stay as they are.
    internal void SetSubtreeExpanded(CodeAnalysisRow folder, bool expanded)
    {
        foreach (var row in folder.SelfAndDescendants().Where(r => !r.IsFile))
        {
            row.IsExpanded = expanded;
            if (expanded)
            {
                _expanded.Add(row.RelativePath);
            }
            else
            {
                _expanded.Remove(row.RelativePath);
            }
        }
    }

    // Expands the folders down to relativePath (a folder expands itself too)
    // and returns its row; null when the tree does not show it. A folder the
    // tree compacts into a longer row ("src" inside "src/app") reveals that row.
    internal CodeAnalysisRow? Reveal(string relativePath)
    {
        var trail = FindTrail(Roots, relativePath.Trim('/'));
        if (trail is null)
        {
            return null;
        }

        foreach (var row in trail.Where(r => !r.IsFile))
        {
            row.IsExpanded = true;
        }

        return trail[^1];
    }

    // The row at exactly relativePath when the tree shows it as is: every
    // folder above it expanded and nothing on the way hidden by search. Unlike
    // Reveal it expands nothing; null otherwise.
    internal CodeAnalysisRow? FindShown(string relativePath)
    {
        var trail = FindTrail(Roots, relativePath);
        if (trail is null
            || !string.Equals(trail[^1].RelativePath, relativePath, StringComparison.OrdinalIgnoreCase)
            || trail.Any(r => !r.IsSearchVisible)
            || trail.SkipLast(1).Any(r => !r.IsExpanded))
        {
            return null;
        }

        return trail[^1];
    }

    private static List<CodeAnalysisRow>? FindTrail(IEnumerable<CodeAnalysisRow> rows, string path)
    {
        if (path.Length == 0)
        {
            return null;
        }

        foreach (var row in rows)
        {
            if (string.Equals(row.RelativePath, path, StringComparison.OrdinalIgnoreCase)
                || row.RelativePath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))
            {
                return new List<CodeAnalysisRow> { row };
            }

            if (!row.IsFile
                && path.StartsWith(row.RelativePath + "/", StringComparison.OrdinalIgnoreCase)
                && FindTrail(row.Children, path) is { } trail)
            {
                trail.Insert(0, row);
                return trail;
            }
        }

        return null;
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
            Node = node,
            FullPath = node.IsFile ? Path.Combine(_root, node.RelativePath.Replace('/', Path.DirectorySeparatorChar)) : null,
            Icon = IconFor(node),
            IconTint = node.IsFile ? FileIcons.For(node.Name).Tint : null,
            LinesText = node.IsBinary ? "binary" : N(node.Lines.Total),
            CodeText = node.IsBinary ? string.Empty : N(node.Lines.Code),
            BlankText = node.IsBinary ? string.Empty : N(node.Lines.Blank),
            CommentText = node.IsBinary ? string.Empty : N(node.Lines.Comment),
            FilesText = node.IsFile ? string.Empty : N(node.FileCount),
            AddedText = showGit && node.Added > 0 ? "+" + N(node.Added) : string.Empty,
            RemovedText = showGit && node.Removed > 0 ? "-" + N(node.Removed) : string.Empty,
            CommitsText = showGit && node.CommitCount > 0 ? N(node.CommitCount) : string.Empty,
            AuthorsText = showGit && node.AuthorCount > 0 ? N(node.AuthorCount) : string.Empty,
            RecencyText = showGit ? Age(node.LastChanged, DateTimeOffset.Now) : string.Empty,
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

    // Just the counts: files, lines, and (with git) commits and authors.
    private void SetSummary(AnalysisNode root)
    {
        var parts = new List<string> { Count(root.FileCount, "file"), Count(root.Lines.Total, "line") };
        var commits = -1;
        if (_history is not null)
        {
            commits = parts.Count;
            parts.Add(Count(root.CommitCount, "commit"));
            parts.Add(Count(root.AuthorCount, "author"));
        }
        else if (!GitAvailable)
        {
            parts.Add("no\u00A0git\u00A0repository");
        }

        if (_scan?.Truncated == true)
        {
            parts.Add("list\u00A0capped");
        }

        SummaryText = string.Join(" - ", parts);

        // "N commits" is a link to the commit picker (when there are any).
        if (commits >= 0 && root.CommitCount > 0)
        {
            SummaryLead = string.Join(" - ", parts.Take(commits)) + " - ";
            SummaryCommits = parts[commits];
            SummaryTail = string.Concat(parts.Skip(commits + 1).Select(p => " - " + p));
        }
        else
        {
            SetSummaryParts(SummaryText);
        }
    }

    // A no-break space keeps a number and its word on one line when the
    // summary wraps.
    private static string Count(long value, string noun) => $"{N(value)}\u00A0{noun}{(value == 1 ? string.Empty : "s")}";

    private void ShowMessage(string message)
    {
        SummaryText = message;
        SetSummaryParts(message);
    }

    private void SetSummaryParts(string text)
    {
        SummaryLead = text;
        SummaryCommits = string.Empty;
        SummaryTail = string.Empty;
    }

    // The summary line as the window shows it: the text around the commit
    // count, which is a link (empty: no link).
    [ObservableProperty]
    private string _summaryLead = "Scanning...";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCommitsLink))]
    private string _summaryCommits = string.Empty;

    [ObservableProperty]
    private string _summaryTail = string.Empty;

    public bool HasCommitsLink => SummaryCommits.Length > 0;

    // The commit picker the commit count opens: the period's commits, newest
    // first, after an "All" entry that stands for the whole period. Picking a
    // commit scopes the view to it (the commit period); picking it again, or
    // All, goes back. Filled as it opens (LoadCommitChoices).
    public Budiff.Avalonia.BulkObservableCollection<CommitChoice> CommitChoices { get; } = new();

    // What the picker lists: the period, in words.
    [ObservableProperty]
    private string _commitChoicesTitle = string.Empty;

    // While a commit picked from the list is applied: the list it was picked
    // from, and the period (or pull request) to go back to.
    private List<HistoryCommit>? _pickedFrom;
    private PeriodOption? _returnPeriod;
    private PullRequestInfo? _returnPullRequest;
    private string _returnLabel = string.Empty;

    // The commits the summary counts: the history's, less those of files the
    // filters hide.
    private List<HistoryCommit> CountedCommits()
    {
        if (_history is null)
        {
            return new List<HistoryCommit>();
        }

        var ids = _lastRoot?.CommitIds;
        return _history.Commits.Where((_, i) => ids is null || ids.Contains(i)).ToList();
    }

    internal void LoadCommitChoices()
    {
        var picked = _appliedCommit is not null ? _pickedFrom : null;
        var commits = picked ?? CountedCommits();
        var now = DateTimeOffset.Now;
        var choices = new List<CommitChoice>(commits.Count + 1);

        // A window opened on a commit has no period of its own to go back to.
        if (_appliedCommit is null || picked is not null)
        {
            choices.Add(new CommitChoice
            {
                IsAll = true,
                Subject = commits.Count == 1 ? "All (1 commit)" : $"All ({N(commits.Count)} commits)",
                IsCurrent = _appliedCommit is null,
            });
        }

        choices.AddRange(commits.Select(c => CommitChoice.From(c, now, c.Sha == _appliedCommit?.Sha)));
        CommitChoices.ReplaceAll(choices);
        CommitChoicesTitle = "Commits - " + (picked is not null ? _returnLabel : DescribeAppliedPeriod());
    }

    [RelayCommand]
    private async Task PickCommitAsync(CommitChoice? choice)
    {
        if (choice is null || string.IsNullOrEmpty(_root))
        {
            return;
        }

        if (choice.IsAll || choice.Sha == _appliedCommit?.Sha)
        {
            await ReturnFromCommitAsync();
            return;
        }

        var root = _root;
        var commit = await Task.Run(() => GitRepositoryReader.ReadCommit(root, choice.Sha));
        if (commit is null)
        {
            ShowMessage("Commit not found: " + choice.ShortSha);
            return;
        }

        // Leaving the period (not another picked commit): remember the way back.
        if (_appliedCommit is null)
        {
            _pickedFrom = CountedCommits();
            _returnPeriod = _appliedPeriod;
            _returnPullRequest = _appliedPullRequest?.PullRequest;
            _returnLabel = DescribeAppliedPeriod();
        }

        _commit = commit;
        if (!Periods.Contains(CommitPeriod))
        {
            Periods.Add(CommitPeriod);
        }

        OnPropertyChanged(nameof(CommitButtonText));
        OnPropertyChanged(nameof(CommitTip));
        await ApplyCommitAsync(commit);
    }

    // Back to the period (or pull request) the picked commit came from.
    private async Task ReturnFromCommitAsync()
    {
        if (_appliedCommit is null || _pickedFrom is null)
        {
            return;
        }

        var pullRequest = _returnPullRequest;
        var period = _returnPeriod is { IsCommit: false, IsPullRequest: false } p ? p : DefaultPeriod;

        // (Forgotten once the period is back: a declined expensive reload
        // stays on the commit, with its way back.)
        if (pullRequest is not null)
        {
            await ApplyPullRequestAsync(pullRequest);
        }
        else
        {
            SelectedPeriod = period;
        }
    }

    private void ForgetCommitReturn()
    {
        _pickedFrom = null;
        _returnPeriod = null;
        _returnPullRequest = null;
        _returnLabel = string.Empty;
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
        OnPropertyChanged(nameof(CanResetView));
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

    // The code city: the same tree (filters, period, grouping) as a 3D city
    // instead of rows. Laid out only while it shows.
    // The Debt view is the city in CityMode.Debt: the same 3D city, its
    // buildings worn by each file's technical-debt score (CodeDebt).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTreeView))]
    [NotifyPropertyChangedFor(nameof(IsHistoryCityView))]
    [NotifyPropertyChangedFor(nameof(IsDebtView))]
    private bool _isCityView;

    public bool IsTreeView => !IsCityView;

    // The City segment: the city coloured by history (Hotspots or Age).
    public bool IsHistoryCityView => IsCityView && CityMode != CityMode.Debt;

    public bool IsDebtView => IsCityView && CityMode == CityMode.Debt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCityHotspots))]
    [NotifyPropertyChangedFor(nameof(IsCityAge))]
    [NotifyPropertyChangedFor(nameof(IsHistoryCityView))]
    [NotifyPropertyChangedFor(nameof(IsDebtView))]
    private CityMode _cityMode = CityMode.Hotspots;

    // What the City segment returns to from Debt.
    private CityMode _historyCityMode = CityMode.Hotspots;

    public bool IsCityHotspots => CityMode == CityMode.Hotspots;

    public bool IsCityAge => CityMode == CityMode.Age;

    [ObservableProperty]
    private CityModel _city = CityModel.Empty(CityMode.Hotspots);

    // The city's performance panel; off unless asked for.
    [ObservableProperty]
    private bool _showCityFps;

    // Whether the city's heights and colours come from git history.
    public bool CityHasHistory => _history is not null;

    private AnalysisNode? _lastRoot;

    [RelayCommand]
    private void ShowTree() => IsCityView = false;

    [RelayCommand]
    private void ShowCity()
    {
        CityMode = _historyCityMode;
        IsCityView = true;
    }

    [RelayCommand]
    private void ShowDebt()
    {
        CityMode = CityMode.Debt;
        IsCityView = true;
    }

    [RelayCommand]
    private void ShowHotspots() => CityMode = _historyCityMode = CityMode.Hotspots;

    [RelayCommand]
    private void ShowAge() => CityMode = _historyCityMode = CityMode.Age;

    [RelayCommand]
    private Task OpenCityFile(CityBlock? block)
        => block is { IsFile: true }
            ? _openFile(Path.Combine(_root, block.RelativePath.Replace('/', Path.DirectorySeparatorChar)), block.RelativePath)
            : Task.CompletedTask;

    // Opens the system file manager on a path (tests replace it).
    internal Action<string> RevealInFileManager { get; set; } = FileManager.Reveal;

    // Right-click -> Show in Explorer, from a tree row or a city building.
    [RelayCommand]
    private void RevealRow(CodeAnalysisRow? row) => ShowInFileManager(row?.RelativePath);

    [RelayCommand]
    private void RevealCityBlock(CityBlock? block) => ShowInFileManager(block?.RelativePath);

    private void ShowInFileManager(string? relativePath)
    {
        if (relativePath is not null && _root.Length > 0)
        {
            RevealInFileManager(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
    }

    partial void OnIsCityViewChanged(bool value) => RebuildCity();

    partial void OnCityModeChanged(CityMode value) => RebuildCity();

    private void RebuildCity()
    {
        if (!IsCityView || _lastRoot is null)
        {
            return;
        }

        City = CityLayout.Build(_lastRoot, CityMode, DateTimeOffset.Now.ToUnixTimeSeconds(), hasHistory: _history is not null);
    }

    partial void OnSelectedGroupingChanged(GroupingOption value)
    {
        OnPropertyChanged(nameof(IsFoldersGrouping));
        OnPropertyChanged(nameof(IsProjectsGrouping));
        Rebuild();
    }

    partial void OnSelectedPeriodChanged(PeriodOption value)
    {
        OnPropertyChanged(nameof(CanResetView));
        IsCustomPeriod = value.IsCustom;
        IsPullRequestPeriod = value.IsPullRequest;
        IsCommitPeriod = value.IsCommit;
        if (value.IsCommit)
        {
            if (!_suspendRebuild && _commit is not null)
            {
                _ = ApplyCommitAsync(_commit);
            }

            return;
        }

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

    // The Period entry already selected, picked again: "Pull request..."
    // opens the picker again, as the PR button beside it does.
    [RelayCommand]
    private void RepickPeriod(PeriodOption? option)
    {
        if (option is { IsPullRequest: true } && SelectedPeriod.IsPullRequest)
        {
            ChoosePullRequestCommand.Execute(null);
        }
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

    // refresh: the PR shown, reloaded - first asked anew where its head and
    // base are now (a force-push rewrites them); the tree, the filters and
    // the selection stay.
    public async Task ApplyPullRequestAsync(PullRequestInfo pullRequest, bool refresh = false)
    {
        // Before the first load is done git is not known to work yet - but
        // with a PR source it is a repository, so a PR picked meanwhile loads.
        if (PullRequests is null || string.IsNullOrEmpty(_root) || (_ready && !GitAvailable))
        {
            return;
        }

        var (gen, ct) = StartLoad();
        ShowMessage($"Loading PR #{pullRequest.Number}...");
        try
        {
            if (refresh)
            {
                var shown = pullRequest;
                pullRequest = await LatestAsync(PullRequests, pullRequest, ct);
                if (pullRequest.HeadRefOid != shown.HeadRefOid || pullRequest.BaseRefOid != shown.BaseRefOid)
                {
                    // Rewritten: the commits may arrive in a fetch below. The
                    // warm cat-file would find them too (git rescans its packs
                    // on a miss), but a fresh one starts from the new state.
                    Budiff.Git.GitRepositoryReader.ReleaseSharedGit();
                }
            }

            var range = await PullRequests.ResolveAsync(pullRequest, ct);
            var root = _root;
            if (!IsCurrentLoad(gen))
            {
                return;
            }

            var snapshot = await Task.Run(() => CodeAnalysisRepositoryReader.LoadPullRequest(root, range, ct));
            if (!IsCurrentLoad(gen))
            {
                return;
            }

            _appliedPullRequest = range;
            _appliedCommit = null;
            _scan = snapshot.Scan;
            _history = snapshot.History;
            SelectPeriodQuietly(Periods.First(p => p.IsPullRequest));
            _appliedPeriod = SelectedPeriod;
            _appliedDays = null;
            PullRequestLabel = $"#{pullRequest.Number} {pullRequest.Title}";
            ForgetCommitReturn();
            GitAvailable = true;
            LoadFilters(_scan, _history, preserveSelection: _ready || refresh);
            _ready = true;
        }
        catch (Exception) when (!IsCurrentLoad(gen))
        {
            return;
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
            EndLoad(gen);
        }

        Rebuild();
        HistoryApplied?.Invoke();
    }

    // GitHub's current view of the PR; the known one when GitHub cannot be
    // asked (offline, signed out) - its commits are local, so it still loads.
    private static async Task<PullRequestInfo> LatestAsync(IPullRequestSource source, PullRequestInfo pullRequest, CancellationToken ct)
    {
        try
        {
            return await source.RefreshAsync(pullRequest, ct);
        }
        catch (PullRequestSourceException)
        {
            return pullRequest;
        }
    }

    public async Task ApplyCommitAsync(CommitDetails commit)
    {
        if (string.IsNullOrEmpty(_root))
        {
            return;
        }

        var (gen, ct) = StartLoad();
        ShowMessage($"Loading commit {commit.ShortSha}...");
        try
        {
            var root = _root;
            var snapshot = await Task.Run(() => CodeAnalysisRepositoryReader.LoadCommit(root, commit, ct));
            if (!IsCurrentLoad(gen))
            {
                return;
            }

            _appliedPullRequest = null;
            PullRequestLabel = string.Empty;
            _appliedCommit = commit;
            _scan = snapshot.Scan;
            _history = snapshot.History;
            SelectPeriodQuietly(CommitPeriod);
            IsCommitPeriod = true;
            _appliedPeriod = SelectedPeriod;
            _appliedDays = null;
            GitAvailable = true;
            LoadFilters(_scan, _history, preserveSelection: _ready);
            _ready = true;
        }
        catch (Exception) when (!IsCurrentLoad(gen))
        {
            return;
        }
        catch (Exception ex)
        {
            Rebuild();
            ShowMessage($"Could not load commit {commit.ShortSha}: {ex.Message}");
            return;
        }
        finally
        {
            EndLoad(gen);
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

    // Back to a time period: leaves PR and commit mode.
    private void ClearPullRequest()
    {
        _appliedPullRequest = null;
        _appliedCommit = null;
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
        // Picked while the folder's load still runs (the first one, or a
        // refresh): load it again for this period, superseding that load.
        if (!_suspendRebuild && !_ready && IsScanning && !string.IsNullOrEmpty(_root))
        {
            await LoadAsync(_root);
            return;
        }

        if (_suspendRebuild || !_ready || !GitAvailable)
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

        var (gen, ct) = StartLoad();
        try
        {
            var sw = Stopwatch.StartNew();
            var root = _root;
            if (_folderScan is null)
            {
                // Opened on a commit or a PR: the working tree was never scanned.
                var snapshot = await Task.Run(() => CodeAnalysisRepositoryReader.Load(root, days, ct));
                if (!IsCurrentLoad(gen))
                {
                    return;
                }

                _folderScan = snapshot.Scan;
                _history = snapshot.History;
            }
            else
            {
                var history = await Task.Run(() => GitHistory.Load(root, days, ct));
                if (!IsCurrentLoad(gen))
                {
                    return;
                }

                _history = history;
            }

            RecordTiming(sw.ElapsedMilliseconds, _history?.CommitCount ?? 0);
            ClearPullRequest();
            ForgetCommitReturn();
            _appliedPeriod = SelectedPeriod;
            _appliedDays = days;
            LoadFilters(_folderScan, _history, preserveSelection: true);
        }
        catch (Exception) when (!IsCurrentLoad(gen))
        {
            return;
        }
        catch (Exception ex)
        {
            ShowMessage("Reload failed: " + ex.Message);
            return;
        }
        finally
        {
            // Never leave the spinner stuck when the reload throws.
            EndLoad(gen);
        }

        Rebuild();
        HistoryApplied?.Invoke();
    }

    private (int Gen, CancellationToken Token) StartLoad()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        IsScanning = true;
        OnPropertyChanged(nameof(CanPickPeriod));
        return (++_loadGen, _loadCts.Token);
    }

    private bool IsCurrentLoad(int gen) => gen == _loadGen;

    private void EndLoad(int gen)
    {
        if (gen == _loadGen)
        {
            IsScanning = false;
            OnPropertyChanged(nameof(CanPickPeriod));
        }
    }

    // The period list works once git is known to be there - and already while
    // the first load runs in a repository (a PR source means one), so a
    // period or pull request can be picked without waiting for the default.
    public bool CanPickPeriod => GitAvailable || (!_ready && IsScanning && PullRequests is not null);

    partial void OnGitAvailableChanged(bool value) => OnPropertyChanged(nameof(CanPickPeriod));

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
        OnPropertyChanged(nameof(RecencyGlyph));
    }

    private string Glyph(AnalysisSortKey key)
        => SortKey != key ? string.Empty : (SortAscending ? "▲" : "▼");

    private static AppIconKind IconFor(AnalysisNode node) => node.Kind switch
    {
        AnalysisNodeKind.Project => AppIconKind.Package,
        AnalysisNodeKind.Ungrouped => AppIconKind.FolderQuestionOutline,
        AnalysisNodeKind.Folder => AppIconKind.FolderOutline,
        AnalysisNodeKind.File => FileIcons.For(node.Name).Kind,
        _ => AppIconKind.FolderOpenOutline,
    };

    private static string N(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    // Minutes spelled "min" so they never read as months ("mo").
    internal static string Age(long unixSeconds, DateTimeOffset now)
    {
        if (unixSeconds <= 0)
        {
            return string.Empty;
        }

        var span = now - DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        return span.TotalDays >= 365 ? $"{(int)(span.TotalDays / 365)}y"
            : span.TotalDays >= 30 ? $"{(int)(span.TotalDays / 30)}mo"
            : span.TotalDays >= 1 ? $"{(int)span.TotalDays}d"
            : span.TotalHours >= 1 ? $"{(int)span.TotalHours}hr"
            : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}min"
            : "<1min";
    }
}
