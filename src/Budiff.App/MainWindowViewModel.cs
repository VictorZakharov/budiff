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

    public MainWindowViewModel(string startPath, bool gitRoot)
    {
        _startPath = startPath;
        _gitRoot = gitRoot;
        Diff.SyntaxHighlighter = new TextMateSyntaxHighlighter();
        Analysis = new CodeAnalysisPanelViewModel(OpenAnalysisFileAsync);
        _ = LoadAsync(startPath, gitRoot);
    }

    public ObservableCollection<ChangedFileVm> Files { get; } = new();

    public CodeAnalysisPanelViewModel Analysis { get; }

    public ObservableCollection<GitReferenceOption> BaseRefs { get; } = new();

    public ObservableCollection<GitReferenceOption> HeadRefs { get; } = new();

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

    [ObservableProperty]
    private GitReferenceOption? _selectedHeadRef;

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
    }

    [RelayCommand]
    private void ToggleTree() => IsTreeCollapsed = !IsTreeCollapsed;

    public bool ShowScopeOffer => !IsGitRootScope;

    public bool HasNoFiles => FileCount == 0;

    public string FileCountText => FileCount == 1 ? "1 file" : $"{FileCount} files";

    partial void OnSelectedBaseRefChanged(GitReferenceOption? value)
    {
        if (!_suppressRefresh)
        {
            _ = RefreshAsync();
        }
    }

    partial void OnSelectedHeadRefChanged(GitReferenceOption? value)
    {
        if (!_suppressRefresh)
        {
            _ = RefreshAsync();
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
        catch (Exception ex)
        {
            Files.Clear();
            FileCount = 0;
            RepoTitle = "Not a git repository";
            ScopeTitle = startPath;
            StatusText = ex.Message;
            Diff.SetPlaceholder(ex.Message);
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

        StatusText = target.Display;
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
            HeadRefs.Clear();
            var refs = GitRepositoryReader.GetReferenceOptions(repositoryRoot);
            foreach (var item in refs.Where(r => !r.IsWorkingTree))
            {
                BaseRefs.Add(item);
            }

            foreach (var item in refs)
            {
                HeadRefs.Add(item);
            }

            SelectedBaseRef = BaseRefs.FirstOrDefault(r => r.Id == "HEAD") ?? BaseRefs.FirstOrDefault();
            SelectedHeadRef = HeadRefs.FirstOrDefault(r => r.IsWorkingTree) ?? HeadRefs.FirstOrDefault();
        }
        finally
        {
            _suppressRefresh = false;
        }
    }

    private DiffTarget CurrentTarget()
    {
        var baseRef = SelectedBaseRef?.Id ?? "HEAD";
        var head = SelectedHeadRef;
        return head is null || head.IsWorkingTree
            ? new DiffTarget(baseRef, null, HeadIsWorkingTree: true)
            : new DiffTarget(baseRef, head.Id, HeadIsWorkingTree: false);
    }

    private void LoadSelectedDiff(ChangedFileVm? value)
    {
        if (_scope is null || value is null)
        {
            return;
        }

        var target = CurrentTarget();
        Diff.Title = value.Path;
        Diff.Subtitle = target.Display;
        Diff.Extension = Path.GetExtension(value.Path);
        var file = value.Model;
        IsDiffOpen = true;
        Diff.BeginLoad(() => GitRepositoryReader.LoadDiff(_scope, file, target));
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
        Diff.Subtitle = target.Display;
        Diff.Extension = Path.GetExtension(relativePath);
        IsDiffOpen = true;
        Diff.BeginLoad(() => GitRepositoryReader.LoadDiff(_scope, file, target));
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
