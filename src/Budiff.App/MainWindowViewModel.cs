using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Budiff.Avalonia;
using Budiff.Avalonia.TextMate;
using Budiff.Git;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

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
        _ = LoadAsync(startPath, gitRoot);
    }

    public ObservableCollection<ChangedFileVm> Files { get; } = new();

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
        StatusText = "Loading changes...";
        ChangeSet changes;
        try
        {
            changes = await Task.Run(() => GitRepositoryReader.ListChanges(_scope, target));
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            return;
        }

        Files.Clear();
        foreach (var file in changes.Files)
        {
            Files.Add(new ChangedFileVm(file, _addedBrush, _removedBrush, _modifiedBrush, _mutedBrush));
        }

        FileCount = Files.Count;
        EmptyText = FileCount == 0 ? "No changes in scope." : string.Empty;
        StatusText = target.Display;
        SelectedFile = Files.FirstOrDefault();
        if (SelectedFile is null)
        {
            Diff.SetPlaceholder("No changed file selected.");
        }
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
        Diff.BeginLoad(() => GitRepositoryReader.LoadDiff(_scope, file, target));
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
