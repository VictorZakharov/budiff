using System;
using System.ComponentModel;
using System.Linq;
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
    // Left + right padding of the bird's-eye toolbar.
    private const double ToolbarPadding = 20;

    // Horizontal padding around the header/row grids (6 left, 18 right).
    private const double TreeRowPadding = 24;

    // The filter box wraps to its own row rather than shrink below this.
    private const double FilterComfortWidth = 140;

    // Left margin before the period controls and the filter group.
    private const double ToolbarGap = 10;

    private readonly MainWindowViewModel _viewModel;
    private DiffWindow? _floatWindow;
    private GridLength _diffWidth = new(640);

    // The selected tree row's path while Roots is being replaced.
    private string? _reselectPath;

    public MainWindow()
        : this(Environment.CurrentDirectory, gitRoot: false)
    {
    }

    public MainWindow(string startPath, bool gitRoot)
        : this(startPath, gitRoot, settings: null)
    {
    }

    // settings: where the GUI's preferences live (null = the user's own);
    // commit / openPath: see MainWindowViewModel.
    internal MainWindow(string startPath, bool gitRoot, AppSettings? settings, CommitDetails? commit = null, string? openPath = null)
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel(startPath, gitRoot, settings, commit, openPath);
        _viewModel.ShowCommit = (scope, shown) => CommitWindow.Open(this, scope, shown);
        _viewModel.RowRevealed += row =>
        {
            AnalysisTree.SelectedItem = row;
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() => AnalysisTree.TreeContainerFromItem(row)?.BringIntoView(), global::Avalonia.Threading.DispatcherPriority.Loaded);
        };
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.Analysis.PropertyChanging += OnAnalysisPropertyChanging;
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
        AddColumnMenuItems();

        AnalysisTree.ContextRequested += OnAnalysisContextRequested;
        AnalysisTree.SelectionChanged += OnAnalysisSelectionChanged;
        AnalysisTree.AddHandler(KeyDownEvent, OnAnalysisTreeKeyDown, RoutingStrategies.Bubble, handledEventsToo: false);

        CodeCity.Gpu = CityGl;
        if (Environment.GetEnvironmentVariable("BUDIFF_CITY_BENCH") is { Length: > 0 } benchReport)
        {
            StartCityBench(benchReport);
        }

        // In the city, Enter in the search box flies to the best match.
        FilterBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && _viewModel.Analysis.IsCityView)
            {
                CodeCity.FlyToBestMatch();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    // A developer benchmark of the code city (BUDIFF_CITY_BENCH=<report file>,
    // optional BUDIFF_CITY_BENCH_SIZE=<width>x<height> in pixels): the window
    // shows the city of the start folder, orbits it for 8 s after a 2 s
    // warm-up, writes the performance report, and closes. It asks to open
    // off-screen without focus, but Windows can still show and activate it,
    // so run it only when nobody is using the desktop.
    private void StartCityBench(string reportPath)
    {
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(-9000, 50);
        Opened += (_, _) =>
        {
            if (Environment.GetEnvironmentVariable("BUDIFF_CITY_BENCH_SIZE") is { } size
                && size.Split('x') is [var w, var h]
                && int.TryParse(w, out var width) && int.TryParse(h, out var height))
            {
                var scale = RenderScaling;
                Width = width / scale;
                Height = height / scale;
            }
        };

        var started = false;
        _viewModel.Analysis.PropertyChanged += (_, _) =>
        {
            if (started || _viewModel.Analysis.IsScanning || _viewModel.Analysis.Roots.Count == 0)
            {
                return;
            }

            started = true;
            _viewModel.Analysis.IsCityView = true;
            _viewModel.Analysis.ShowCityFps = true;
            CodeCity.RunBench(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8), report =>
            {
                System.IO.File.WriteAllText(reportPath, report + Environment.NewLine
                    + $"Window {ClientSize.Width * RenderScaling:0}x{ClientSize.Height * RenderScaling:0} px" + Environment.NewLine);
                Close();
            });
        };
    }

    // A row's name tip shows only when the name is cut short by the ellipsis.
    private void OnNameTipOpening(object? sender, CancelRoutedEventArgs e)
    {
        if (sender is TextBlock name && !name.TextLayout.TextLines.Any(line => line.HasCollapsed))
        {
            e.Cancel = true;
        }
    }

    // One check item per column, between the column menu's two separators.
    // Built here, wired to each option directly: indexer bindings
    // (ColumnOptions[i].IsOn) came up empty in the NativeAOT build.
    private void AddColumnMenuItems()
    {
        var menu = (MenuFlyout)Resources["ColumnMenu"]!;
        var at = 2;
        foreach (var option in _viewModel.ColumnOptions)
        {
            var item = new MenuItem { Header = option.Name, ToggleType = MenuItemToggleType.CheckBox, IsChecked = option.IsOn, StaysOpenOnClick = true };
            item.PropertyChanged += (_, e) =>
            {
                if (e.Property == MenuItem.IsCheckedProperty)
                {
                    option.IsOn = item.IsChecked;
                }
            };
            option.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(BirdsEyeColumnOption.IsOn))
                {
                    item.IsChecked = option.IsOn;
                }
            };
            menu.Items.Insert(at++, item);
        }
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

    // A narrow bird's-eye moves the filter box under the grouping / period
    // controls instead of squeezing it to nothing.
    private void UpdateToolbarWrap()
    {
        var available = ToolbarGrid.Bounds.Width;
        if (available <= 0)
        {
            return;
        }

        if (GroupingSwitch.IsVisible && GroupingSwitch.DesiredSize.Width > 0)
        {
            _groupingSwitchWidth = GroupingSwitch.DesiredSize.Width;
        }

        // The period controls at the picker's full width, without the label
        // and the wrap-dependent left margin.
        var periodExtra = PeriodWithoutLabel() - PeriodBox.Width;
        var period = periodExtra + PeriodBoxWidth;
        var label = _periodLabelWidth + PeriodBar.Spacing;
        var oneRow = _groupingSwitchWidth + ToolbarGap + label + period + ToolbarGap + FilterComfortWidth;

        // Compact once the filter no longer fits on the first row: the filter
        // wraps under it, the Folders / Projects switch becomes a drop-down and
        // the "Period" label goes (the picker says it). The drop-down and the
        // picker share a row, the picker narrowing down to its minimum; below
        // that, the picker wraps too.
        var compact = available < oneRow;
        var beside = available - GroupingBox.Width - ToolbarGap - periodExtra;
        var periodWraps = compact ? beside < PeriodBoxMinWidth : available < _groupingSwitchWidth + ToolbarGap + label + period;
        PeriodBox.Width = compact && !periodWraps ? Math.Min(PeriodBoxWidth, Math.Floor(beside)) : PeriodBoxWidth;

        var filterRow = !compact ? 0 : periodWraps ? 2 : 1;
        var periodRow = periodWraps ? 1 : 0;
        PeriodLabel.IsVisible = !compact;
        GroupingSwitch.IsVisible = !compact;
        GroupingBox.IsVisible = compact;
        Grid.SetRow(PeriodBar, periodRow);
        Grid.SetColumn(PeriodBar, periodWraps ? 0 : 1);
        Grid.SetColumnSpan(PeriodBar, periodWraps ? 4 : 1);
        PeriodBar.Margin = new Thickness(periodWraps ? 0 : ToolbarGap, 0, 0, 0);

        Grid.SetRow(FilterRow, filterRow);
        Grid.SetColumn(FilterRow, compact ? 0 : 2);
        Grid.SetColumnSpan(FilterRow, compact ? 4 : 1);
        FilterRow.Margin = new Thickness(compact ? 0 : ToolbarGap, 0, 0, 0);

        // Only as many rows as are used: RowSpacing counts empty rows too.
        var rows = Math.Max(filterRow, periodRow) + 1;
        if (ToolbarGrid.RowDefinitions.Count != rows)
        {
            ToolbarGrid.RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", rows)));
        }

        ToolbarGrid.RowSpacing = rows > 1 ? 8 : 0;
    }

    // The period picker's width, and the narrowest it gets beside the
    // compact toolbar's grouping drop-down.
    private const double PeriodBoxWidth = 132;
    private const double PeriodBoxMinWidth = 112;

    // The "Period" label's and the Folders / Projects switch's widths,
    // remembered from when they last showed.
    private double _periodLabelWidth = 40;
    private double _groupingSwitchWidth = 143;

    // The period controls' width without the label and the left margin.
    private double PeriodWithoutLabel()
    {
        var width = PeriodBar.DesiredSize.Width - PeriodBar.Margin.Left;
        if (PeriodLabel.IsVisible && PeriodLabel.DesiredSize.Width > 0)
        {
            _periodLabelWidth = PeriodLabel.DesiredSize.Width;
            width -= _periodLabelWidth + PeriodBar.Spacing;
        }

        return width;
    }

    // Narrowest docked bird's-eye: Name's minimum, the Added / Removed columns
    // when on and the row padding (the optional columns drop out before this
    // is reached), or the fully wrapped toolbar's widest row if that is wider.
    private double BirdsEyeMinWidth()
    {
        var tree = MainWindowViewModel.NameMinWidth + TreeRowPadding
            + (_viewModel.ShowAddedColumn ? MainWindowViewModel.ChurnColumnWidth : 0)
            + (_viewModel.ShowRemovedColumn ? MainWindowViewModel.ChurnColumnWidth : 0);
        var toolbar = ToolbarPadding + Math.Max(GroupingBox.Width, PeriodWithoutLabel() - PeriodBox.Width + PeriodBoxWidth);
        return Math.Ceiling(Math.Max(tree, toolbar));
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

        // Fewer always-on columns let the docked bird's-eye go narrower.
        if (e.PropertyName is nameof(MainWindowViewModel.ShowAddedColumn)
            or nameof(MainWindowViewModel.ShowRemovedColumn))
        {
            ClampDockedDiff();
        }
    }

    private void OnAnalysisPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CodeAnalysisPanelViewModel.Extensions)
            or nameof(CodeAnalysisPanelViewModel.Authors))
        {
            ApplyFilterSplit();
        }
        else if (e.PropertyName is nameof(CodeAnalysisPanelViewModel.Roots) && _reselectPath is { } path
            && _viewModel.Analysis.Roots.Count > 0)
        {
            // (An empty tree - a refresh still scanning - keeps the path for
            // the rows that follow.)
            // A rebuild (sort, filter, grouping, period) makes new rows: select
            // the same path again and scroll it into view once the tree has
            // taken the new items.
            _reselectPath = null;
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_viewModel.Analysis.FindShown(path) is not { } row)
                {
                    return;
                }

                AnalysisTree.SelectedItem = row;
                CenterTreeRow(row);
            }, global::Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    // Scrolls the tree so the row sits in the middle of the viewport (as far
    // as the list allows at its ends). An expanded folder's container spans
    // its children too, so the row's own header line is what gets centred.
    private void CenterTreeRow(CodeAnalysisRow row)
    {
        AnalysisTree.UpdateLayout();
        if (AnalysisTree.TreeContainerFromItem(row) is not Control item)
        {
            return;
        }

        var line = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(item)
            .OfType<Control>().FirstOrDefault(c => c.Name == "PART_LayoutRoot") ?? item;
        var viewer = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(AnalysisTree).OfType<ScrollViewer>().FirstOrDefault();
        if (viewer is null || line.TranslatePoint(default, viewer) is not { } at)
        {
            item.BringIntoView();
            return;
        }

        var y = viewer.Offset.Y + at.Y + line.Bounds.Height / 2 - viewer.Viewport.Height / 2;
        var max = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
        viewer.Offset = new Vector(viewer.Offset.X, Math.Clamp(y, 0, max));
    }

    // The tree is about to get new rows: remember which one was selected.
    private void OnAnalysisPropertyChanging(object? sender, PropertyChangingEventArgs e)
    {
        if (e.PropertyName is nameof(CodeAnalysisPanelViewModel.Roots) && _viewModel.Analysis.Roots.Count > 0)
        {
            _reselectPath = (AnalysisTree.SelectedItem as CodeAnalysisRow)?.RelativePath;
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
                cols[0].MinWidth = BirdsEyeMinWidth();
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
        var treeMin = Math.Clamp(available - 360 - splitter, 0, BirdsEyeMinWidth());
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

    // While the diff pane is open it follows the bird's-eye selection (keys,
    // type-to-search, a click). A lone move shows at once; a burst (a held
    // arrow key) shows its first row, then only the row it ends on, once the
    // keys have paused for FollowDelayMs - so a key repeat never queues a
    // load per row it passes. Focus stays in the tree. (The pause is a
    // delayed post, not a DispatcherTimer: the headless test dispatcher never
    // ticks those.)
    private const int FollowDelayMs = 120;
    private CodeAnalysisRow? _followRow;
    private bool _followPending;
    private int _followGen;
    private long _lastFollow = long.MinValue / 2;

    private void OnAnalysisSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_viewModel.IsDiffOpen || AnalysisTree.SelectedItem is not CodeAnalysisRow row)
        {
            CancelFollow();
            return;
        }

        _followRow = row;
        if (!_followPending && Environment.TickCount64 - _lastFollow >= FollowDelayMs)
        {
            FollowPendingSelection();
            return;
        }

        // Each move restarts the pause.
        _followPending = true;
        var gen = ++_followGen;
        var ui = global::Avalonia.Threading.Dispatcher.UIThread;
        _ = Task.Delay(FollowDelayMs).ContinueWith(
            _ => ui.Post(() =>
            {
                if (gen == _followGen)
                {
                    FollowPendingSelection();
                }
            }),
            TaskScheduler.Default);
    }

    private void FollowPendingSelection()
    {
        var row = _followRow;
        CancelFollow();
        _lastFollow = Environment.TickCount64;
        if (row is not null && ReferenceEquals(AnalysisTree.SelectedItem, row))
        {
            _viewModel.FollowSelection(row);
        }
    }

    private void CancelFollow()
    {
        _followGen++;
        _followPending = false;
        _followRow = null;
    }

    // Enter on a file row opens it (pane open or not), like a double-click.
    // async void: a throw here is an unhandled process crash, so guard.
    private async void OnAnalysisTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || AnalysisTree.SelectedItem is not CodeAnalysisRow { IsFile: true } row)
        {
            return;
        }

        e.Handled = true;
        try
        {
            CancelFollow();
            await _viewModel.Analysis.OpenAsync(row);
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = "Open file failed: " + ex.Message;
        }
    }

    // The last row menu opened (tests).
    internal ContextMenu? RowMenu { get; private set; }

    // Right-click a row (or the menu key): Show in Explorer. The row is
    // selected first, so it is clear which one the menu is for.
    private void OnAnalysisContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (FindAnalysisRow(e.Source) is not { } row)
        {
            return;
        }

        e.Handled = true;
        AnalysisTree.SelectedItem = row;
        var analysis = _viewModel.Analysis;
        RowMenu = row.IsFile
            ? FileManager.Menu(() => analysis.RevealRowCommand.Execute(row))
            : FileManager.Menu(
                () => analysis.RevealRowCommand.Execute(row),
                () => analysis.SetSubtreeExpanded(row, expanded: true),
                () => analysis.SetSubtreeExpanded(row, expanded: false));
        RowMenu.Open(e.Source as Control ?? AnalysisTree);
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
