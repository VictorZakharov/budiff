using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Budiff.Git;

namespace Budiff.App;

// Small and quick: a commit's details and changed files. Expand (or a
// double-clicked file) opens the full budiff window on the commit.
public sealed partial class CommitWindow : Window
{
    private readonly CommitWindowViewModel _viewModel;

    public CommitWindow()
        : this(new CommitWindowViewModel(new GitRepositoryScope(Environment.CurrentDirectory, Environment.CurrentDirectory, string.Empty, true, null), new CommitDetails(string.Empty, null, 0, string.Empty, string.Empty, DateTimeOffset.Now, string.Empty)))
    {
    }

    public CommitWindow(CommitWindowViewModel viewModel)
    {
        InitializeComponent();
        // handledEventsToo: a folder row's double-click also toggles it.
        FileTree.AddHandler(DoubleTappedEvent, OnFileDoubleTapped, handledEventsToo: true);

        // Esc closes this quick look (the full window it expands into stays
        // put). Tunnel: the file tree would take the key first.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            {
                e.Handled = true;
                Close();
            }
        }, RoutingStrategies.Tunnel);
        _viewModel = viewModel;
        DataContext = viewModel;
        Opened += async (_, _) => await _viewModel.LoadAsync();
    }

    internal CommitWindowViewModel ViewModel => _viewModel;

    // Raised instead of opening a window when set (tests).
    internal Func<CommitDetails, string?, Window>? CreateFullWindow { get; set; }

    public static CommitWindow Open(Window owner, GitRepositoryScope scope, CommitDetails commit)
    {
        // Only a clone with a GitHub remote asks gh (a local repo would just fail).
        var source = new GitHubCliPullRequestSource(scope.RepositoryRoot);
        var window = new CommitWindow(new CommitWindowViewModel(scope, commit, source.CanSearchAhead ? source : null));
        window.Show(owner);
        return window;
    }

    // The full window: the same folder this window lists (so it shows the
    // same files), scoped to this commit. openPath (repository-relative) is
    // revealed there: a file opens, a folder expands.
    internal Window Expand(string? openPath = null)
    {
        var scope = _viewModel.Scope;
        var full = CreateFullWindow?.Invoke(_viewModel.Commit, openPath)
                   ?? new MainWindow(scope.ScopeRoot, scope.IsGitRootScope, settings: null, _viewModel.Commit, openPath);

        // Sized like the window the blame was clicked in: maximized when it is.
        if (Owner is Window { WindowState: WindowState.Maximized })
        {
            full.WindowState = WindowState.Maximized;
        }

        full.Show();
        Close();
        return full;
    }

    private void OnExpand(object? sender, RoutedEventArgs e) => Expand();

    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext is CommitFileNode node)
        {
            e.Handled = true;
            Expand(node.Path);
        }
    }

    // Opens a link in the browser (tests replace it).
    internal Func<TopLevel, Uri, Task> OpenExternal { get; set; } = (top, uri) => top.Launcher.LaunchUriAsync(uri);

    private async void OnOpenPullRequest(object? sender, TappedEventArgs e)
    {
        if (_viewModel.PullRequest is { Url.Length: > 0 } pr && Uri.TryCreate(pr.Url, UriKind.Absolute, out var uri))
        {
            await OpenExternal(this, uri);
        }
    }

    private async void OnCopySha(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(_viewModel.Commit.Sha);
        }
    }
}
