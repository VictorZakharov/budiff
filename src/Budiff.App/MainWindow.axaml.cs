using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Budiff.Git;

namespace Budiff.App;

public sealed partial class MainWindow : Window
{
    // Narrowest docked bird's-eye: Name's minimum plus Added / Removed and the
    // row padding (330) - the optional columns drop out before this is reached -
    // rounded up to what the wrapped toolbar's first row needs.
    private const double BirdsEyeMinWidth = 365;

    // Horizontal padding around the header/row grids (6 left, 18 right).
    private const double TreeRowPadding = 24;

    // The filter box wraps to its own row rather than shrink below this.
    private const double FilterComfortWidth = 140;

    // Left margin before the period controls and the filter group.
    private const double ToolbarGap = 10;

    private readonly MainWindowViewModel _viewModel;
    private DiffWindow? _floatWindow;
    private GridLength _diffWidth = new(640);

    public MainWindow()
        : this(Environment.CurrentDirectory, gitRoot: false)
    {
    }

    public MainWindow(string startPath, bool gitRoot)
        : this(startPath, gitRoot, settings: null)
    {
    }

    // settings: where the GUI's preferences live (null = the user's own).
    internal MainWindow(string startPath, bool gitRoot, AppSettings? settings)
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel(startPath, gitRoot, settings);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.Analysis.PropertyChanged += OnAnalysisPropertyChanged;
        _viewModel.Analysis.PickPullRequest = PickPullRequestAsync;
        // "Pull request..." may be next: start the list its picker opens on.
        PeriodBox.DropDownOpened += (_, _) =>
        {
            if (_viewModel.Analysis.PullRequests is { } source)
            {
                PullRequestPickerViewModel.SearchAhead(source);
            }
        };
        BodyGrid.SizeChanged += (_, _) => ClampDockedDiff();
        TreeArea.SizeChanged += (_, _) => FitColumns();
        BirdsEyePanel.SizeChanged += (_, _) => UpdateToolbarWrap();
        PeriodBar.SizeChanged += (_, _) => UpdateToolbarWrap();
        DiffToolbarGrid.LayoutUpdated += (_, _) => UpdateDiffOptionsOverflow();
        ApplyDiffLayout();
    }

    // In a narrow docked pane the toggles run past the edge: a toggle that does
    // not fully fit is hidden and the overflow menu, holding every toggle,
    // appears. Runs on every layout pass, so it must NEVER change layout:
    // opacity and hit-testing only. (Showing the menu button via IsVisible
    // narrowed the row it was decided from and, at the threshold width,
    // crashed the app with "Infinite layout loop detected".)
    private void UpdateDiffOptionsOverflow()
    {
        var width = DiffToolbarGrid.Bounds.Width;
        var anyCut = false;
        foreach (var child in DiffToolbarGrid.Children)
        {
            if (child is not CheckBox { IsVisible: true } toggle)
            {
                continue;
            }

            var cut = toggle.Bounds.Right > width + 0.5;
            anyCut |= cut;
            toggle.Opacity = cut ? 0 : 1;
            toggle.IsHitTestVisible = !cut;
        }

        DiffOptionsOverflow.Opacity = anyCut ? 1 : 0;
        DiffOptionsOverflow.IsHitTestVisible = anyCut;
    }

    // Drops optional bird's-eye columns that no longer fit the pane.
    private void FitColumns()
    {
        if (TreeArea.Bounds.Width > 0)
        {
            _viewModel.FitColumns(TreeArea.Bounds.Width - TreeRowPadding);
        }
    }

    // A narrow bird's-eye moves the filter box and Reset under the grouping /
    // period controls instead of squeezing the filter to nothing.
    private void UpdateToolbarWrap()
    {
        var available = ToolbarGrid.Bounds.Width;
        if (available <= 0)
        {
            return;
        }

        // PeriodBar's desired width already includes its left margin.
        var oneRow = GroupingSwitch.DesiredSize.Width + PeriodBar.DesiredSize.Width + ToolbarGap
            + FilterComfortWidth + FilterRow.ColumnSpacing + ResetButton.DesiredSize.Width;
        var wrap = available < oneRow;
        var row = wrap ? 1 : 0;
        if (Grid.GetRow(FilterRow) == row)
        {
            return;
        }

        Grid.SetRow(FilterRow, row);
        Grid.SetColumn(FilterRow, wrap ? 0 : 2);
        Grid.SetColumnSpan(FilterRow, wrap ? 4 : 1);
        FilterRow.Margin = new Thickness(wrap ? 0 : ToolbarGap, 0, 0, 0);
        ToolbarGrid.RowSpacing = wrap ? 8 : 0;
    }

    internal MainWindowViewModel ViewModel => _viewModel;

    // The Period "Pull request..." entry: a modal picker over this window.
    private async Task<PullRequestInfo?> PickPullRequestAsync()
    {
        if (_viewModel.Analysis.PullRequests is not { } source)
        {
            return null;
        }

        var picker = new PullRequestPickerWindow(new PullRequestPickerViewModel(source));
        return await picker.ShowDialog<PullRequestInfo?>(this);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.ShowDiffPane)
            or nameof(MainWindowViewModel.IsTreeCollapsed)
            or nameof(MainWindowViewModel.IsDiffOpen)
            or nameof(MainWindowViewModel.IsDiffFloating))
        {
            ApplyDiffLayout();
        }

        if (e.PropertyName is nameof(MainWindowViewModel.ShowWideColumns))
        {
            FitColumns();
        }
    }

    private void OnAnalysisPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CodeAnalysisPanelViewModel.Extensions)
            or nameof(CodeAnalysisPanelViewModel.Authors))
        {
            ApplyFilterSplit();
        }
    }

    // Sizes the EXTENSIONS/AUTHORS split proportionally to the data so neither pane
    // scrolls while the other wastes space. RowDefinitions cannot be x:Name'd, so the
    // named FilterGrid is indexed. The GridSplitter still allows manual adjustment;
    // a reload re-applies the data-proportional split.
    private void ApplyFilterSplit()
    {
        var extensions = _viewModel.Analysis.Extensions.Count;
        var authors = _viewModel.Analysis.Authors.Count;
        if (extensions == 0 && authors == 0)
        {
            return;
        }

        // +1 per pane for its header row; clamp so neither pane collapses entirely.
        var share = (extensions + 1.0) / (extensions + authors + 2.0);
        share = Math.Clamp(share, 0.2, 0.85);
        FilterGrid.RowDefinitions[0].Height = new GridLength(share, GridUnitType.Star);
        FilterGrid.RowDefinitions[2].Height = new GridLength(1 - share, GridUnitType.Star);
    }

    // Sizes the body columns for the current diff state. ColumnDefinitions cannot be
    // x:Name'd (no field is generated), so the named BodyGrid is indexed instead.
    private void ApplyDiffLayout()
    {
        var cols = BodyGrid.ColumnDefinitions;

        // Remember the user's docked diff width before leaving the docked state.
        if (DiffHost.IsVisible && BirdsEyePanel.IsVisible && cols[2].Width.IsAbsolute && cols[2].Width.Value >= 100)
        {
            _diffWidth = cols[2].Width;
        }

        if (_viewModel.ShowDiffPane)
        {
            var collapsed = _viewModel.IsTreeCollapsed;
            BirdsEyePanel.IsVisible = !collapsed;
            BodySplitter.IsVisible = !collapsed;
            DiffHost.IsVisible = true;
            if (collapsed)
            {
                cols[0].MinWidth = 0;
                cols[0].Width = new GridLength(0);
                cols[1].Width = new GridLength(0);
                cols[2].MinWidth = 0;
                cols[2].Width = new GridLength(1, GridUnitType.Star);
            }
            else
            {
                cols[0].MinWidth = BirdsEyeMinWidth;
                cols[0].Width = new GridLength(1, GridUnitType.Star);
                cols[1].Width = new GridLength(5);
                cols[2].MinWidth = 360;
                cols[2].Width = _diffWidth;
                ClampDockedDiff();
            }
        }
        else
        {
            BirdsEyePanel.IsVisible = true;
            BodySplitter.IsVisible = false;
            DiffHost.IsVisible = false;
            cols[0].MinWidth = 0;
            cols[0].Width = new GridLength(1, GridUnitType.Star);
            cols[1].Width = new GridLength(0);
            cols[2].MinWidth = 0;
            cols[2].Width = new GridLength(0);
        }

        SyncFloatWindow();
    }

    // A fixed diff width (default or splitter-dragged) can exceed what a small
    // window fits next to the tree's minimum: the grid then runs past the window
    // edge and the diff toolbar's controls get cut off. Shrink the minimums and
    // the diff column to the space that actually exists.
    private void ClampDockedDiff()
    {
        var cols = BodyGrid.ColumnDefinitions;
        if (!DiffHost.IsVisible || !BirdsEyePanel.IsVisible || !cols[2].Width.IsAbsolute)
        {
            return;
        }

        var available = BodyGrid.Bounds.Width;
        if (available <= 0)
        {
            return;
        }

        var splitter = cols[1].Width.Value;
        var treeMin = Math.Clamp(available - 360 - splitter, 0, BirdsEyeMinWidth);
        var diffMin = Math.Clamp(available - treeMin - splitter, 0, 360);
        cols[0].MinWidth = treeMin;
        cols[2].MinWidth = diffMin;

        var diff = Math.Max(diffMin, Math.Min(cols[2].Width.Value, available - treeMin - splitter));
        if (Math.Abs(diff - cols[2].Width.Value) > 0.5)
        {
            cols[2].Width = new GridLength(diff);
        }
    }

    private void SyncFloatWindow()
    {
        if (_viewModel.IsDiffFloating && _viewModel.IsDiffOpen)
        {
            if (_floatWindow is null)
            {
                _floatWindow = new DiffWindow(_viewModel.Diff, dockBack: () => _viewModel.IsDiffFloating = false);
                _floatWindow.Closed += OnFloatWindowClosed;
                _floatWindow.Show(this);
            }
        }
        else if (_floatWindow is not null)
        {
            CloseFloatWindow();
        }
    }

    private void OnFloatWindowClosed(object? sender, EventArgs e)
    {
        // User closed the floating window: re-dock the diff.
        _floatWindow = null;
        _viewModel.IsDiffFloating = false;
    }

    private void CloseFloatWindow()
    {
        var window = _floatWindow;
        _floatWindow = null;
        if (window is not null)
        {
            window.Closed -= OnFloatWindowClosed;
            window.Close();
        }
    }

    private void OnFloatDiff(object? sender, RoutedEventArgs e)
    {
        _viewModel.IsDiffFloating = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        CloseFloatWindow();
        base.OnClosed(e);
    }

    // async void: a throw here is an unhandled process crash, so guard.
    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Open repository or folder",
                AllowMultiple = false,
            });

            if (folders.Count == 0)
            {
                return;
            }

            if (folders[0].TryGetLocalPath() is { } path)
            {
                await _viewModel.LoadAsync(path, gitRoot: false);
            }
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = "Open folder failed: " + ex.Message;
        }
    }

    private void OnToggleExpand(object? sender, RoutedEventArgs e)
    {
        if (FindAnalysisRow(sender) is { IsFile: false } row)
        {
            row.IsExpanded = !row.IsExpanded;
        }
    }

    // async void: a throw here is an unhandled process crash, so guard.
    private async void OnAnalysisRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            if (FindAnalysisRow(e.Source) is { IsFile: true } row)
            {
                await _viewModel.Analysis.OpenAsync(row);
            }
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = "Open file failed: " + ex.Message;
        }
    }

    private static CodeAnalysisRow? FindAnalysisRow(object? source)
    {
        var node = source as StyledElement;
        while (node is not null)
        {
            if (node.DataContext is CodeAnalysisRow row)
            {
                return row;
            }

            node = node.Parent;
        }

        return null;
    }
}
