using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Budiff.Git;

namespace Budiff.App;

// Modal picker; closes with the chosen PullRequestInfo, or null on cancel.
public sealed partial class PullRequestPickerWindow : Window
{
    // How close to the end of the list (px) counts as "scrolled to the bottom".
    private const double LoadMoreMargin = 48;

    private readonly PullRequestPickerViewModel _viewModel;

    public PullRequestPickerWindow()
        : this(new PullRequestPickerViewModel(new GitHubCliPullRequestSource(Environment.CurrentDirectory)))
    {
    }

    public PullRequestPickerWindow(PullRequestPickerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // ScrollChanged bubbles from the list's own ScrollViewer, whatever the
        // theme's template calls it.
        ResultList.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => LoadMoreIfAtEnd());

        // A page shorter than the viewport cannot scroll: re-check once it lays out.
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PullRequestPickerViewModel.IsLoading) && !_viewModel.IsLoading)
            {
                Dispatcher.UIThread.Post(LoadMoreIfAtEnd, DispatcherPriority.Background);
            }
        };

        // People are listed on demand: the first drop-down fetches them.
        AuthorBox.DropDownOpened += (_, _) => _ = _viewModel.LoadPeopleAsync();

        Opened += async (_, _) =>
        {
            SearchBox.Focus();
            await _viewModel.InitializeAsync();
        };
        Closed += (_, _) => _viewModel.StopSearching();
    }

    internal PullRequestPickerViewModel ViewModel => _viewModel;

    // Infinite scroll: near the bottom (or when everything fits), pull the
    // next page; the view model ignores the call while loading or exhausted.
    private void LoadMoreIfAtEnd()
    {
        var scroll = ResultList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is not null && scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - LoadMoreMargin)
        {
            _viewModel.LoadMoreCommand.Execute(null);
        }
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel.SelectedItem is { } row)
        {
            Close(row.PullRequest);
        }
    }

    private void OnShow(object? sender, RoutedEventArgs e) => Close(_viewModel.SelectedItem?.PullRequest);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
