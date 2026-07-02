using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Budiff;
using Budiff.Avalonia;
using Budiff.Avalonia.TextMate;
using Budiff.Git;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Material.Icons;

namespace Budiff.App;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IBrush _addedBrush = Brush("#7EE787");
    private readonly IBrush _removedBrush = Brush("#FF7B72");
    private readonly IBrush _modifiedBrush = Brush("#E2C08D");
    private readonly IBrush _mutedBrush = Brush("#9DA0A8");
    private bool _suppressRefresh;
    private string _startPath;
    private bool _gitRoot;
    private GitRepositoryScope? _scope;
    private ChangedFile? _openDiffFile;
    private int _blameGen;

    // Sentinel base option: resolves to the same boundary commit the windowed
    // churn diffs from, so the drill-in shows what the Added/Removed columns
    // counted. Default while an analysis window is active.
    internal const string PeriodStartId = "(period-start)";

    public MainWindowViewModel(string startPath, bool gitRoot)
    {
        _startPath = startPath;
        _gitRoot = gitRoot;
        Diff.SyntaxHighlighter = new TextMateSyntaxHighlighter();
        Analysis = new CodeAnalysisPanelViewModel(OpenAnalysisFileAsync);
        Analysis.HistoryApplied += OnHistoryApplied;
        _ = LoadAsync(startPath, gitRoot);
    }

    // Period changed: a period-aligned diff must follow the new window.
    private void OnHistoryApplied()
    {
        if (SelectedBaseRef?.Id == PeriodStartId)
        {
            ReloadOpenDiff();
        }
    }

    public ObservableCollection<ChangedFileVm> Files { get; } = new();

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
    [NotifyPropertyChangedFor(nameof(HasNoFiles))]
    [NotifyPropertyChangedFor(nameof(FileCountText))]
    private int _fileCount;

    [ObservableProperty]
    private string _emptyText = "No changes in scope.";

    [ObservableProperty]
    private GitReferenceOption? _selectedBaseRef;

    // OFF = compare against HEAD only, i.e. "as if my uncommitted changes were
    // not made yet". ON (default) = compare against what's on disk.
    [ObservableProperty]
    private bool _includeWorkingTree = true;

    [ObservableProperty]
    private ChangedFileVm? _selectedFile;

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

    public MaterialIconKind TreeToggleIcon => IsTreeCollapsed ? MaterialIconKind.ChevronDoubleRight : MaterialIconKind.ChevronDoubleLeft;

    public string TreeToggleTip => IsTreeCollapsed ? "Show bird's-eye" : "Hide bird's-eye";

    [RelayCommand]
    private void CloseDiff()
    {
        IsDiffOpen = false;
        IsTreeCollapsed = false;
        _openDiffFile = null;
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
        Diff.Subtitle = DescribeTarget(target);
        Diff.BeginLoad(() => GitRepositoryReader.LoadDiff(scope, file, target));
        BeginLoadBlame(scope, file, target);
    }

    // Blame describes the diff's BASE side; loaded off-thread after the diff so
    // the rows never wait on it. The generation guard drops a stale result when
    // the user has already moved to another file or base.
    private void BeginLoadBlame(GitRepositoryScope scope, ChangedFile file, DiffTarget target)
    {
        var gen = ++_blameGen;
        Diff.SetBlame(null);
        _ = Task.Run(() =>
        {
            var blame = GitRepositoryReader.LoadBlame(scope, file.OldPath ?? file.Path, target.BaseRef);
            var entries = new BlameEntry?[blame.Count];
            for (var i = 0; i < blame.Count; i++)
            {
                if (blame[i] is { } line)
                {
                    entries[i] = new BlameEntry(
                        $"{line.Author} {Age(line.When)}",
                        line.ShortSha,
                        line.Author,
                        line.Email,
                        line.When.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                        line.Message);
                }
            }

            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (gen == _blameGen)
                {
                    Diff.SetBlame(entries);
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

    public bool HasNoFiles => FileCount == 0;

    public string FileCountText => FileCount == 1 ? "1 file" : $"{FileCount} files";

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

    partial void OnSelectedFileChanged(ChangedFileVm? value)
    {
        LoadSelectedDiff(value);
    }

    public async Task LoadAsync(string startPath, bool gitRoot)
    {
        _startPath = startPath;
        _gitRoot = gitRoot;
        try
        {
            _scope = GitRepositoryReader.Open(startPath, gitRoot);
        }
        catch
        {
            // Not a git repository: fall back to the plain folder analysis. The
            // analysis panel already handles GitAvailable=false (no churn columns).
            _scope = null;
            Files.Clear();
            FileCount = 0;
            BaseRefs.Clear();
            var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(startPath));
            RepoTitle = string.IsNullOrEmpty(folderName) ? startPath : folderName;
            WindowTitle = "budiff - " + RepoTitle;
            ScopeTitle = "folder - not a git repository";
            IsGitRootScope = true;
            CloseDiff();
            StatusText = "Folder analysis only (no git repository).";
            Diff.SetPlaceholder("Not a git repository - diffs unavailable.");
            await Analysis.LoadAsync(startPath);
            return;
        }

        WindowTitle = "budiff - " + Path.GetFileName(_scope.RepositoryRoot);
        RepoTitle = Path.GetFileName(_scope.RepositoryRoot);
        ScopeTitle = _scope.ScopePrefix.Length == 0 ? "repo root" : _scope.ScopePrefix;
        IsGitRootScope = _scope.IsGitRootScope;
        LoadRefs(_scope.RepositoryRoot);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_scope is null)
        {
            return;
        }

        var target = CurrentTarget();
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

        StatusText = DescribeTarget(target);
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

    // "Period start" resolves to a raw sha; describe it instead of printing it.
    private string DescribeTarget(DiffTarget target)
    {
        if (SelectedBaseRef?.Id != PeriodStartId || target.BaseRef == "HEAD")
        {
            return target.Display;
        }

        var baseLabel = target.BaseRef == GitHistory.EmptyTreeSha
            ? "start of history"
            : "period start (" + target.BaseRef[..7] + ")";
        return target.HeadIsWorkingTree ? baseLabel + " vs working tree" : baseLabel + " vs HEAD";
    }

    private void LoadSelectedDiff(ChangedFileVm? value)
    {
        if (_scope is null || value is null)
        {
            return;
        }

        var target = CurrentTarget();
        Diff.Title = value.Path;
        Diff.Subtitle = DescribeTarget(target);
        Diff.Extension = Path.GetExtension(value.Path);
        var file = value.Model;
        _openDiffFile = file;
        IsDiffOpen = true;
        Diff.BeginLoad(() => GitRepositoryReader.LoadDiff(_scope, file, target));
        BeginLoadBlame(_scope, file, target);
    }

    private Task OpenAnalysisFileAsync(string fullPath, string relativePath)
    {
        if (_scope is null)
        {
            return Task.CompletedTask;
        }

        var repoPath = ToRepoPath(_scope.RepositoryRoot, fullPath);
        var target = CurrentTarget();
        var file = new ChangedFile(repoPath, null, fullPath, Budiff.GitFileState.Modified, new DiffChurn(0, 0), 0, 0, IsBinary: false);
        Diff.Title = repoPath;
        Diff.Subtitle = DescribeTarget(target);
        Diff.Extension = Path.GetExtension(relativePath);
        _openDiffFile = file;
        IsDiffOpen = true;
        Diff.BeginLoad(() => GitRepositoryReader.LoadDiff(_scope, file, target));
        BeginLoadBlame(_scope, file, target);
        return Task.CompletedTask;
    }

    private static string ToRepoPath(string repositoryRoot, string fullPath)
    {
        var rel = Path.GetRelativePath(repositoryRoot, fullPath);
        return rel == "." ? string.Empty : rel.Replace('\\', '/');
    }

    private static IBrush Brush(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
}

public sealed class ChangedFileVm
{
    public ChangedFileVm(ChangedFile model, IBrush addedBrush, IBrush removedBrush, IBrush modifiedBrush, IBrush mutedBrush)
    {
        Model = model;
        StateBrush = model.State switch
        {
            Budiff.GitFileState.Added or Budiff.GitFileState.Untracked => addedBrush,
            Budiff.GitFileState.Deleted => removedBrush,
            Budiff.GitFileState.Renamed => modifiedBrush,
            Budiff.GitFileState.Conflicted => removedBrush,
            Budiff.GitFileState.Modified => modifiedBrush,
            _ => mutedBrush,
        };
    }

    public ChangedFile Model { get; }

    public string Path => Model.Path;

    public string StateText => Model.StateText;

    public IBrush StateBrush { get; }

    public string AddedText => "+" + Model.Churn.Added;

    public string RemovedText => "-" + Model.Churn.Removed;

    public string DetailText
    {
        get
        {
            var authors = Model.AuthorCount == 1 ? "1 author" : $"{Model.AuthorCount} authors";
            var commits = Model.CommitCount == 1 ? "1 commit" : $"{Model.CommitCount} commits";
            return Model.IsBinary ? $"{authors}, {commits}, binary" : $"{authors}, {commits}";
        }
    }
}
