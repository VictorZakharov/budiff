using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Budiff;
using Budiff.Avalonia;
using Budiff.Avalonia.Markdown;
using Budiff.Avalonia.TextMate;
using Budiff.Git;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Budiff.App;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private bool _suppressRefresh;
    private string _startPath;
    private bool _gitRoot;
    private GitRepositoryScope? _scope;
    private ChangedFile? _openDiffFile;
    private DiffTarget? _openTarget;
    private int _blameGen;
    private string? _blamePath;

    // The open file (as the bird's-eye named it) and the files a preview
    // link was followed from - with where each was scrolled to - for Back.
    private (string FullPath, string RelativePath)? _openFile;
    private readonly Stack<(string FullPath, string RelativePath, DiffScrollSpot? Scroll)> _linkTrail = new();

    // Where each file was last scrolled to: opening it again comes back there.
    private readonly Dictionary<string, DiffScrollSpot> _scrollByFile = new();

    // Sentinel base option: resolves to the same boundary commit the windowed
    // churn diffs from, so the drill-in shows what the Added/Removed columns
    // counted. Default while an analysis window is active.
    internal const string PeriodStartId = "(period-start)";

    public MainWindowViewModel(string startPath, bool gitRoot)
        : this(startPath, gitRoot, settings: null)
    {
    }

    internal MainWindowViewModel(string startPath, bool gitRoot, AppSettings? settings)
    {
        _startPath = startPath;
        _gitRoot = gitRoot;
        _settings = settings ?? AppSettings.Load(AppSettings.DefaultPath);
        ColumnOptions = BuildColumnOptions();
        FitColumns(_rowWidth);
        var highlighter = new TextMateSyntaxHighlighter();
        Diff.SyntaxHighlighter = highlighter;
        Diff.Previewer = new MarkdownPreviewer(highlighter);
        Diff.GoBackHandler = GoBack;
        Analysis = new CodeAnalysisPanelViewModel(OpenAnalysisFileAsync);
        Analysis.HistoryApplied += OnHistoryApplied;
        Diff.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DiffViewModel.ShowDiffOptions))
            {
                OnPropertyChanged(nameof(ShowCompareOptions));
            }
        };
        _ = LoadAsync(startPath, gitRoot);
    }

    // Period changed: an open diff follows when its comparison moved - a
    // period-aligned base, or entering / leaving / switching a pull request.
    private void OnHistoryApplied()
    {
        OnPropertyChanged(nameof(ShowCompareOptions));
        if (_scope is not null)
        {
            StatusText = DescribeStatus();
        }

        if (_openTarget is not null && CurrentTarget() != _openTarget)
        {
            ReloadOpenDiff();
        }
    }

    // Base + Working tree: meaningless for a plain file view, and a pull
    // request fixes both ends (its merge-base and its head).
    public bool ShowCompareOptions => Diff.ShowDiffOptions && Analysis.AppliedPullRequest is null;

    public CodeAnalysisPanelViewModel Analysis { get; }

    public ObservableCollection<GitReferenceOption> BaseRefs { get; } = new();

    public DiffViewModel Diff { get; } = new();

    [ObservableProperty]
    private string _windowTitle = "budiff";

    [ObservableProperty]
    private string _repoTitle = "budiff";

    [ObservableProperty]
    private string _scopeTitle = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowScopeOffer))]
    private bool _isGitRootScope = true;

    [ObservableProperty]
    private GitReferenceOption? _selectedBaseRef;

    // OFF = compare against HEAD only, i.e. "as if my uncommitted changes were
    // not made yet". ON (default) = compare against what's on disk.
    [ObservableProperty]
    private bool _includeWorkingTree = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDiffPane))]
    [NotifyPropertyChangedFor(nameof(ShowWideColumns))]
    private bool _isDiffOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDiffPane))]
    [NotifyPropertyChangedFor(nameof(ShowWideColumns))]
    private bool _isDiffFloating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TreeToggleIcon))]
    [NotifyPropertyChangedFor(nameof(TreeToggleTip))]
    private bool _isTreeCollapsed;

    // The docked diff pane: open and not torn off into its own window.
    public bool ShowDiffPane => IsDiffOpen && !IsDiffFloating;

    // Full bird's-eye (all columns + filter panes) whenever the docked diff is away.
    public bool ShowWideColumns => !ShowDiffPane;

    // Bird's-eye column widths (header cell = row cell + its margins). Name
    // always shows, and Added / Removed unless turned off in the chooser; the
    // optional ones drop as the pane narrows, from the end of this keep order:
    // Lines, Code, Commits, Authors, then the wide-only Blank, Comment, Files,
    // Size.
    internal const double NameMinWidth = 150;
    internal const double ChurnColumnWidth = 78;
    internal const double AddedRemovedWidth = ChurnColumnWidth * 2;

    // Chooser indexes of Added / Removed, after the optional columns.
    private const int AddedIndex = 8;
    private const int RemovedIndex = 9;
    internal static readonly double[] OptionalColumnWidths = { 72, 72, 64, 60, 64, 76, 54, 90 };
    private const int DockedColumnCount = 4;

    // The optional columns showing (bit i = OptionalColumnWidths[i]): the ones
    // the user keeps on that also fit, counted along the keep order.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FittingColumns))]
    [NotifyPropertyChangedFor(nameof(VisibleCells))]
    [NotifyPropertyChangedFor(nameof(ShowLinesColumn))]
    [NotifyPropertyChangedFor(nameof(ShowCodeColumn))]
    [NotifyPropertyChangedFor(nameof(ShowCommitsColumn))]
    [NotifyPropertyChangedFor(nameof(ShowAuthorsColumn))]
    [NotifyPropertyChangedFor(nameof(ShowBlankColumn))]
    [NotifyPropertyChangedFor(nameof(ShowCommentColumn))]
    [NotifyPropertyChangedFor(nameof(ShowFilesColumn))]
    [NotifyPropertyChangedFor(nameof(ShowSizeColumn))]
    private int _shownColumns = (1 << 8) - 1;

    // How many optional columns show.
    public int FittingColumns => System.Numerics.BitOperations.PopCount((uint)ShownColumns);

    public bool ShowLinesColumn => IsShown(0);

    public bool ShowCodeColumn => IsShown(1);

    public bool ShowCommitsColumn => IsShown(2);

    public bool ShowAuthorsColumn => IsShown(3);

    public bool ShowBlankColumn => IsShown(4);

    public bool ShowCommentColumn => IsShown(5);

    public bool ShowFilesColumn => IsShown(6);

    public bool ShowSizeColumn => IsShown(7);

    private bool IsShown(int column) => (ShownColumns & (1 << column)) != 0;

    public bool ShowAddedColumn => IsOn(AddedIndex);

    public bool ShowRemovedColumn => IsOn(RemovedIndex);

    // The bird's-eye grid columns showing, bit i = grid column i (1 = Lines
    // ... 10 = Size); a row's name runs on over the empty ones.
    public int VisibleCells =>
        (ShowLinesColumn ? 1 << 1 : 0) | (ShowCodeColumn ? 1 << 2 : 0) | (ShowBlankColumn ? 1 << 3 : 0)
        | (ShowCommentColumn ? 1 << 4 : 0) | (ShowFilesColumn ? 1 << 5 : 0) | (ShowAddedColumn ? 1 << 6 : 0)
        | (ShowRemovedColumn ? 1 << 7 : 0) | (ShowCommitsColumn ? 1 << 8 : 0) | (ShowAuthorsColumn ? 1 << 9 : 0)
        | (ShowSizeColumn ? 1 << 10 : 0);

    // The column chooser (right-click the bird's-eye header), in on-screen
    // order: every column but Name. Turned-off columns are remembered across runs (AppSettings).
    public IReadOnlyList<BirdsEyeColumnOption> ColumnOptions { get; }

    private readonly AppSettings _settings;
    private double _rowWidth = double.PositiveInfinity;

    private IReadOnlyList<BirdsEyeColumnOption> BuildColumnOptions()
    {
        // (setting name, keep-order index), as the header lays them out.
        (string Name, int Index)[] columns =
        {
            ("Lines", 0), ("Code", 1), ("Blank", 4), ("Comment", 5), ("Files", 6),
            ("Added", AddedIndex), ("Removed", RemovedIndex),
            ("Commits", 2), ("Authors", 3), ("Size", 7),
        };
        return columns
            .Select(c => new BirdsEyeColumnOption(c.Name, c.Index, !_settings.HiddenColumns.Contains(c.Name), OnColumnOptionChanged))
            .ToList();
    }

    private void OnColumnOptionChanged(BirdsEyeColumnOption option)
    {
        if (option.IsOn)
        {
            _settings.HiddenColumns.Remove(option.Name);
        }
        else
        {
            _settings.HiddenColumns.Add(option.Name);
        }

        if (_settingAllColumns)
        {
            return;
        }

        ColumnChoicesChanged();
    }

    private void ColumnChoicesChanged()
    {
        _settings.Save();
        FitColumns(_rowWidth);
        OnPropertyChanged(nameof(ShowAddedColumn));
        OnPropertyChanged(nameof(ShowRemovedColumn));
        OnPropertyChanged(nameof(VisibleCells));
        OnPropertyChanged(nameof(AllColumnsOn));
        ResetColumnsCommand.NotifyCanExecuteChanged();
    }

    private bool _settingAllColumns;

    // The chooser's top check: ticked while every column is on; clicking it
    // turns them all on, or - when they already are - all off.
    public bool AllColumnsOn
    {
        get => ColumnOptions.All(o => o.IsOn);
        set => SetAllColumns(value);
    }

    // Back to the defaults (every column on), forgetting the saved choices.
    [RelayCommand(CanExecute = nameof(CanResetColumns))]
    private void ResetColumns() => SetAllColumns(true);

    private bool CanResetColumns() => ColumnOptions.Any(o => !o.IsOn);

    // One save and one refit for the lot, not one per column.
    private void SetAllColumns(bool on)
    {
        _settingAllColumns = true;
        try
        {
            foreach (var option in ColumnOptions)
            {
                option.IsOn = on;
            }
        }
        finally
        {
            _settingAllColumns = false;
        }

        ColumnChoicesChanged();
    }

    // rowWidth = the header/row grid's width (pane minus its padding). The
    // wide-only columns never show next to the docked diff; columns the user
    // turned off take no room, so the next ones in the keep order move up.
    public void FitColumns(double rowWidth)
    {
        _rowWidth = rowWidth;
        var candidates = ShowWideColumns ? OptionalColumnWidths.Length : DockedColumnCount;
        var spare = rowWidth - NameMinWidth
            - (ShowAddedColumn ? ChurnColumnWidth : 0) - (ShowRemovedColumn ? ChurnColumnWidth : 0);
        var shown = 0;
        for (var column = 0; column < candidates; column++)
        {
            if (!IsOn(column))
            {
                continue;
            }

            if (spare < OptionalColumnWidths[column])
            {
                break;
            }

            spare -= OptionalColumnWidths[column];
            shown |= 1 << column;
        }

        ShownColumns = shown;
    }

    private bool IsOn(int column)
    {
        foreach (var option in ColumnOptions)
        {
            if (option.Index == column)
            {
                return option.IsOn;
            }
        }

        return true;
    }

    public AppIconKind TreeToggleIcon => IsTreeCollapsed ? AppIconKind.ChevronDoubleRight : AppIconKind.ChevronDoubleLeft;

    public string TreeToggleTip => IsTreeCollapsed ? "Show bird's-eye" : "Hide bird's-eye";

    [RelayCommand]
    private void CloseDiff()
    {
        IsDiffOpen = false;
        IsTreeCollapsed = false;
        _openDiffFile = null;
        _openTarget = null;
        RememberScroll();
        _openFile = null;
        ClearLinkTrail();
    }

    private void RememberScroll()
    {
        // Mid-load the spot still describes the file shown before.
        if (_openFile is { } open && Diff is { IsLoading: false, IsPlaceholder: false, ScrollSpot: { } spot })
        {
            _scrollByFile[open.FullPath] = spot;
        }
    }

    // Re-runs the open diff against the current base / working-tree choice.
    private void ReloadOpenDiff()
    {
        if (_scope is null || _openDiffFile is null || !IsDiffOpen)
        {
            return;
        }

        var scope = _scope;
        var file = _openDiffFile;
        var target = CurrentTarget();
        _openTarget = target;
        Diff.Subtitle = DescribeTarget(target);
        Diff.PreviewContext = PreviewContext(target);
        Diff.BeginLoad(() => GitRepositoryReader.LoadDiff(scope, file, target));
        BeginLoadBlame(scope, file, target);
    }

    // Blame describes the diff's BASE side; when the base has nothing to blame
    // (empty-tree base, or the file is absent there - a wholly new file), fall
    // back to blaming the NEW side: those lines were still written by someone.
    // Loaded off-thread after the diff so the rows never wait on it; the
    // generation guard drops a stale result when the user has already moved on.
    // Reloading the SAME file keeps the old values on screen until the fresh
    // ones land, so a working-tree/base toggle does not blank the column.
    private void BeginLoadBlame(GitRepositoryScope scope, ChangedFile file, DiffTarget target)
    {
        var gen = ++_blameGen;
        if (!string.Equals(_blamePath, file.Path, StringComparison.OrdinalIgnoreCase))
        {
            Diff.SetBlame(null);
        }

        _blamePath = file.Path;
        var ui = global::Avalonia.Threading.Dispatcher.UIThread;
        _ = Task.Run(() =>
        {
            var forNewSide = false;
            IReadOnlyList<BlameLineInfo?> blame;
            try
            {
                blame = GitRepositoryReader.LoadBlame(scope, file.OldPath ?? file.Path, target.BaseRef);
                if (blame.Count == 0)
                {
                    blame = GitHistory.BlameFile(
                        scope.RepositoryRoot,
                        file.Path,
                        target.HeadIsWorkingTree ? null : target.HeadRef ?? "HEAD");
                    forNewSide = true;
                }
            }
            catch
            {
                // Blame is decoration: on any git failure leave the column
                // empty instead of dying as an unobserved task exception.
                blame = Array.Empty<BlameLineInfo?>();
            }

            var entries = new BlameEntry?[blame.Count];
            for (var i = 0; i < blame.Count; i++)
            {
                if (blame[i] is { } line)
                {
                    var uncommitted = line.ShortSha == "0000000";
                    entries[i] = new BlameEntry(
                        uncommitted ? "uncommitted" : $"{line.Author} {Age(line.When)}",
                        line.ShortSha,
                        line.Author,
                        line.Email,
                        uncommitted ? string.Empty : line.When.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                        line.Message);
                }
            }

            ui.Post(() =>
            {
                if (gen == _blameGen)
                {
                    Diff.SetBlame(entries, forNewSide);
                }
            });
        });
    }

    private static string Age(DateTimeOffset when)
    {
        var span = DateTimeOffset.Now - when;
        return span.TotalDays >= 365 ? $"{(int)(span.TotalDays / 365)}y"
            : span.TotalDays >= 30 ? $"{(int)(span.TotalDays / 30)}mo"
            : span.TotalDays >= 1 ? $"{(int)span.TotalDays}d"
            : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h"
            : "now";
    }

    [RelayCommand]
    private void ToggleTree() => IsTreeCollapsed = !IsTreeCollapsed;

    public bool ShowScopeOffer => !IsGitRootScope;

    // Base and the working-tree toggle affect the drill-in diff only; the
    // bird's-eye is governed by the period filter.
    partial void OnSelectedBaseRefChanged(GitReferenceOption? value)
    {
        if (!_suppressRefresh)
        {
            ReloadOpenDiff();
        }
    }

    partial void OnIncludeWorkingTreeChanged(bool value)
    {
        if (!_suppressRefresh)
        {
            ReloadOpenDiff();
        }
    }

    public async Task LoadAsync(string startPath, bool gitRoot)
    {
        _startPath = startPath;
        _gitRoot = gitRoot;
        try
        {
            _scope = GitRepositoryReader.Open(startPath, gitRoot);
        }
        catch (Exception ex)
        {
            // No usable git repository: fall back to the plain folder analysis.
            // The analysis panel already handles GitAvailable=false (no churn
            // columns), and a drill-in shows the file as-is. Surface git's own
            // reason - "not owned by current user" is not "not a repository".
            _scope = null;
            Analysis.PullRequests = null;
            BaseRefs.Clear();
            var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(startPath));
            RepoTitle = string.IsNullOrEmpty(folderName) ? startPath : folderName;
            WindowTitle = "budiff - " + RepoTitle;
            ScopeTitle = "folder - no git";
            IsGitRootScope = true;
            CloseDiff();
            StatusText = "Folder analysis only: " + ex.Message;
            Diff.SetPlaceholder("Select a file in the bird's-eye to view it.");
            await Analysis.LoadAsync(startPath);
            return;
        }

        WindowTitle = "budiff - " + Path.GetFileName(_scope.RepositoryRoot);
        RepoTitle = Path.GetFileName(_scope.RepositoryRoot);
        ScopeTitle = _scope.ScopePrefix.Length == 0 ? "repo root" : _scope.ScopePrefix;
        IsGitRootScope = _scope.IsGitRootScope;
        LoadRefs(_scope.RepositoryRoot);
        // Opened on a project folder: the PR picker lists only PRs touching it.
        Analysis.PullRequests = new GitHubCliPullRequestSource(_scope.ScopeRoot, _scope.ScopePrefix);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_scope is null)
        {
            // Folder mode: re-run the whole load, which also re-detects git.
            await LoadAsync(_startPath, _gitRoot);
            return;
        }

        CloseDiff();
        StatusText = "Loading bird's-eye...";
        try
        {
            await Analysis.LoadAsync(_scope.ScopeRoot);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            return;
        }

        StatusText = DescribeStatus();
        Diff.SetPlaceholder("Select a file in the bird's-eye to open its diff.");
    }

    [RelayCommand]
    private async Task UseRepoRootAsync()
    {
        if (_scope is not null)
        {
            await LoadAsync(_scope.RepositoryRoot, gitRoot: true);
        }
    }

    private void LoadRefs(string repositoryRoot)
    {
        _suppressRefresh = true;
        try
        {
            BaseRefs.Clear();
            BaseRefs.Add(new GitReferenceOption(PeriodStartId, "Period start"));
            foreach (var item in GitRepositoryReader.GetBranchOptions(repositoryRoot))
            {
                BaseRefs.Add(item);
            }

            // All time is a snapshot, not a window - HEAD is the honest default there.
            SelectedBaseRef = Analysis.SelectedSinceDays is > 0
                ? BaseRefs[0]
                : BaseRefs.FirstOrDefault(r => r.Id == "HEAD") ?? BaseRefs.FirstOrDefault();
        }
        finally
        {
            _suppressRefresh = false;
        }
    }

    private DiffTarget CurrentTarget()
    {
        if (Analysis.AppliedPullRequest is { } pr)
        {
            return new DiffTarget(pr.BaseSha, pr.HeadSha, HeadIsWorkingTree: false);
        }

        var baseRef = SelectedBaseRef?.Id ?? "HEAD";
        if (baseRef == PeriodStartId)
        {
            baseRef = ResolvePeriodStart();
        }

        return IncludeWorkingTree
            ? new DiffTarget(baseRef, null, HeadIsWorkingTree: true)
            : new DiffTarget(baseRef, "HEAD", HeadIsWorkingTree: false);
    }

    private string ResolvePeriodStart()
    {
        var days = Analysis.AppliedSinceDays;
        return _scope is null || days is not > 0
            ? "HEAD"
            : GitHistory.FindWindowBaseCommit(_scope.ScopeRoot, days.Value);
    }

    // The header line: the PR being shown, else what a drill-in compares.
    private string DescribeStatus()
        => Analysis.AppliedPullRequest is { } pr
            ? $"PR #{pr.PullRequest.Number} {pr.PullRequest.Title}  ({pr.PullRequest.BaseRefName} ← {pr.PullRequest.HeadRefName})"
            : DescribeTarget(CurrentTarget());

    // "Period start" resolves to a raw sha; describe it instead of printing it.
    private string DescribeTarget(DiffTarget target)
    {
        if (Analysis.AppliedPullRequest is { } pr)
        {
            return $"PR #{pr.PullRequest.Number}: merge-base {Short(pr.BaseSha)} vs head {Short(pr.HeadSha)}";
        }

        if (SelectedBaseRef?.Id != PeriodStartId || target.BaseRef == "HEAD")
        {
            return target.Display;
        }

        var baseLabel = target.BaseRef == GitHistory.EmptyTreeSha
            ? "start of history"
            : "period start (" + target.BaseRef[..7] + ")";
        return target.HeadIsWorkingTree ? baseLabel + " vs working tree" : baseLabel + " vs HEAD";
    }

    // A file picked in the bird's-eye starts a fresh trail of links.
    internal Task OpenAnalysisFileAsync(string fullPath, string relativePath)
    {
        ClearLinkTrail();
        ShowFile(fullPath, relativePath);
        return Task.CompletedTask;
    }

    // A link in the markdown preview: open that project file (Back returns).
    internal void OpenLinkedFile(string path)
    {
        if (_openFile is not { } current)
        {
            return;
        }

        var root = _scope?.RepositoryRoot ?? DocumentFiles.Split(current.FullPath, current.RelativePath).Root;
        var full = DocumentFiles.FullPath(root, path);
        if (full is null)
        {
            return;
        }

        if (Directory.Exists(full))
        {
            StatusText = "Folders do not open in the diff: " + path;
            return;
        }

        if (!LinkedFileExists(full, path))
        {
            StatusText = "Linked file not found: " + path;
            return;
        }

        _linkTrail.Push((current.FullPath, current.RelativePath, Diff.ScrollSpot));
        Diff.CanGoBack = true;
        ShowFile(full, path);
    }

    private bool LinkedFileExists(string fullPath, string path)
        => File.Exists(fullPath)
           || (_scope is { } scope && _openTarget is { } target
               && (GitRepositoryReader.HasFileAtRevision(scope, path, target.HeadRef ?? "HEAD")
                   || GitRepositoryReader.HasFileAtRevision(scope, path, target.BaseRef)));

    private void GoBack()
    {
        if (_linkTrail.Count == 0)
        {
            return;
        }

        var (fullPath, relativePath, scroll) = _linkTrail.Pop();
        Diff.CanGoBack = _linkTrail.Count > 0;
        ShowFile(fullPath, relativePath, scroll);
    }

    private void ClearLinkTrail()
    {
        _linkTrail.Clear();
        Diff.CanGoBack = false;
    }

    // What the preview of the open file can reach: the project's files as
    // each side of this comparison has them, and links to open.
    private DocumentPreviewContext? PreviewContext(DiffTarget? target)
    {
        if (_openFile is not { } open)
        {
            return null;
        }

        var scope = _scope;
        var (root, documentPath) = scope is null
            ? DocumentFiles.Split(open.FullPath, open.RelativePath)
            : (scope.RepositoryRoot, ToRepoPath(scope.RepositoryRoot, open.FullPath));
        return new DocumentPreviewContext(
            documentPath,
            (path, side) => DocumentFiles.Read(root, path, side, scope, target),
            OpenLinkedFile);
    }

    // restore: where to scroll once it shows (Back); else where this file was
    // left, or - opened for the first time - its start. The file already open
    // stays where it is.
    private void ShowFile(string fullPath, string relativePath, DiffScrollSpot? restore = null)
    {
        var reopened = _openFile?.FullPath == fullPath;
        RememberScroll();
        _openFile = (fullPath, relativePath);
        Diff.RestoreScroll = restore ?? (reopened ? null : _scrollByFile.GetValueOrDefault(fullPath) ?? DiffScrollSpot.Start);
        if (_scope is null)
        {
            // No git: nothing to compare against - show the file as it is now.
            _openDiffFile = null;
            _openTarget = null;
            _blameGen++;
            _blamePath = null;
            Diff.SetBlame(null);
            Diff.Title = relativePath.Replace('\\', '/');
            Diff.Subtitle = "current file - no git";
            Diff.PreviewContext = PreviewContext(null);
            IsDiffOpen = true;
            Diff.BeginLoad(() => File.Exists(fullPath)
                ? DiffLoader.BuildView(DiffLoader.ReadWorkingFile(fullPath))
                : DiffResult.Message("File not found."));
        }
        else
        {
            var repoPath = ToRepoPath(_scope.RepositoryRoot, fullPath);
            var target = CurrentTarget();
            var file = new ChangedFile(repoPath, null, fullPath, Budiff.GitFileState.Modified, new DiffChurn(0, 0), 0, 0, IsBinary: false);
            Diff.Title = repoPath;
            Diff.Subtitle = DescribeTarget(target);
            _openDiffFile = file;
            _openTarget = target;
            Diff.PreviewContext = PreviewContext(target);
            IsDiffOpen = true;
            Diff.BeginLoad(() => GitRepositoryReader.LoadDiff(_scope, file, target));
            BeginLoadBlame(_scope, file, target);
        }

        // Only once the load is under way (its result lands later, on this
        // thread): the view then waits for the file instead of rendering the
        // previous one's rows under this name.
        Diff.Extension = Path.GetExtension(relativePath);
        Diff.FileName = Path.GetFileName(relativePath);
        Diff.IsFileView = _scope is null;
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    private static string ToRepoPath(string repositoryRoot, string fullPath)
    {
        var rel = Path.GetRelativePath(repositoryRoot, fullPath);
        return rel == "." ? string.Empty : rel.Replace('\\', '/');
    }
}

// One entry of the bird's-eye column chooser.
public sealed partial class BirdsEyeColumnOption : ObservableObject
{
    private readonly Action<BirdsEyeColumnOption> _changed;

    internal BirdsEyeColumnOption(string name, int index, bool isOn, Action<BirdsEyeColumnOption> changed)
    {
        Name = name;
        Index = index;
        _isOn = isOn;
        _changed = changed;
    }

    public string Name { get; }

    // Its place in MainWindowViewModel.OptionalColumnWidths (the keep order),
    // or AddedIndex / RemovedIndex.
    internal int Index { get; }

    [ObservableProperty]
    private bool _isOn;

    partial void OnIsOnChanged(bool value) => _changed(this);
}
