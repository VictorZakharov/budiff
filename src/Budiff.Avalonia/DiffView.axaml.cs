using System.Collections.Generic;
using System.ComponentModel;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Media;
using global::Avalonia.Media.Immutable;
using global::Avalonia.Markup.Xaml;

namespace Budiff.Avalonia;

public partial class DiffView : UserControl
{
    // Line-number column plus the code TextBlock's horizontal padding.
    private const double LineNumberWidth = 46;
    private const double TextPadding = 6;

    // Every line diff row is this tall (DiffView.axaml), wrapped lines aside.
    private const double RowHeight = 18;

    // Recently shown previews, by what they show. Going Back re-attaches the
    // very same control - its scroll position, unfolded sections and loaded
    // pictures intact - instead of rendering anew and visibly scrolling.
    private const int PreviewCacheSize = 12;

    private readonly List<(PreviewKey Key, Control View)> _previews = new();
    private DiffViewModel? _model;
    private PreviewKey? _shownPreview;
    private bool _reusedPreview;

    public static readonly StyledProperty<IBrush> AddedLineBackgroundProperty =
        AvaloniaProperty.Register<DiffView, IBrush>(nameof(AddedLineBackground), Brush("#3331C36B"));

    public static readonly StyledProperty<IBrush> RemovedLineBackgroundProperty =
        AvaloniaProperty.Register<DiffView, IBrush>(nameof(RemovedLineBackground), Brush("#33E05252"));

    public static readonly StyledProperty<IBrush> FunnelAddedBrushProperty =
        AvaloniaProperty.Register<DiffView, IBrush>(nameof(FunnelAddedBrush), Brush("#5531C36B"));

    public static readonly StyledProperty<IBrush> FunnelRemovedBrushProperty =
        AvaloniaProperty.Register<DiffView, IBrush>(nameof(FunnelRemovedBrush), Brush("#55E05252"));

    public static readonly StyledProperty<IBrush> FunnelModifiedBrushProperty =
        AvaloniaProperty.Register<DiffView, IBrush>(nameof(FunnelModifiedBrush), Brush("#55E2C08D"));

    public static readonly StyledProperty<IBrush> EditorBackgroundBrushProperty =
        AvaloniaProperty.Register<DiffView, IBrush>(nameof(EditorBackgroundBrush), Brush("#1E1F22"));

    public static readonly StyledProperty<IBrush> ToolbarBrushProperty =
        AvaloniaProperty.Register<DiffView, IBrush>(nameof(ToolbarBrush), Brush("#3C3F41"));

    public static readonly StyledProperty<IBrush> DiffBorderBrushProperty =
        AvaloniaProperty.Register<DiffView, IBrush>(nameof(DiffBorderBrush), Brush("#1E1F22"));

    public static readonly StyledProperty<IBrush> TextBrushProperty =
        AvaloniaProperty.Register<DiffView, IBrush>(nameof(TextBrush), Brush("#DFE1E5"));

    public static readonly StyledProperty<IBrush> MutedTextBrushProperty =
        AvaloniaProperty.Register<DiffView, IBrush>(nameof(MutedTextBrush), Brush("#9DA0A8"));

    public static readonly StyledProperty<IBrush> DefaultSyntaxBrushProperty =
        AvaloniaProperty.Register<DiffView, IBrush>(nameof(DefaultSyntaxBrush), Brush("#D4D4D4"));

    // Hosts that render their own chrome (e.g. the docked pane's compact
    // toolbar) can hide the control's built-in header row.
    public static readonly StyledProperty<bool> ShowHeaderProperty =
        AvaloniaProperty.Register<DiffView, bool>(nameof(ShowHeader), true);

    // Host-provided control appended to the built-in header row (e.g. the
    // floating window's "Dock back" button).
    public static readonly StyledProperty<object?> HeaderExtraProperty =
        AvaloniaProperty.Register<DiffView, object?>(nameof(HeaderExtra));

    public DiffView()
    {
        InitializeComponent();
        Focusable = true;
        InitSelection();
        DataContextChanged += (_, _) =>
        {
            ApplyThemeToViewModel();
            UpdateBlameCramped();
            UpdateVisibleTextWidth();
            WatchModel(DataContext as DiffViewModel);
        };

        // The UI font is inherited only once in a window: fit the blame
        // column again with it.
        AttachedToVisualTree += (_, _) =>
        {
            if (_model is not null)
            {
                _model.MeasureBlameLabel = MeasureBlameLabel;
            }
        };

        RowsScroller.ScrollChanged += (_, _) =>
        {
            ReportScroll(RowsScroller, preview: false);
            KeepTopAnchored();
        };

        // Back after following a preview link: the mouse's back button
        // anywhere over the diff, or Alt+Left.
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsXButton1Pressed && _model is { CanGoBack: true } model)
            {
                model.GoBackCommand.Execute(null);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Left && e.KeyModifiers == KeyModifiers.Alt && _model is { CanGoBack: true } model)
            {
                model.GoBackCommand.Execute(null);
                e.Handled = true;
            }
            else if (ViewModeSwitch.IsToggleGesture(e) && _model is { CanView: true } viewable)
            {
                viewable.ToggleViewModeCommand.Execute(null);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

        // LayoutUpdated (not just SizeChanged): a fixed-width docked pane gets
        // NO SizeChanged when only the window resizes, yet "half the window"
        // changes. The checks are a couple of comparisons - cheap per pass.
        LayoutUpdated += (_, _) =>
        {
            UpdateBlameCramped();
            UpdateVisibleTextWidth();
        };
    }

    // The horizontal scrollbar's range is the longest line minus what the
    // NARROWER pane shows, so a side squeezed by the split still reaches the
    // end of its lines.
    private void UpdateVisibleTextWidth()
    {
        if (DataContext is not DiffViewModel model || RowsHost.Bounds.Width <= 0)
        {
            return;
        }

        var code = RowsHost.Bounds.Width - model.BlameGutterWidth;
        var pane = model.IsSinglePane
            ? code
            : (code - DiffFunnelGutter.GutterWidth) * Math.Min(model.SplitRatio, 1 - model.SplitRatio);
        model.VisibleTextWidth = Math.Max(0, pane - LineNumberWidth - TextPadding);
    }

    // Blame needs real estate: it hides while the pane is under half the
    // window's width (regardless of the toggle) and returns when it grows.
    // NOTE: VisualRoot is a TopLevelHost on Avalonia 12, NOT the Window -
    // resolve the containing top-level explicitly.
    private void UpdateBlameCramped()
    {
        if (DataContext is not DiffViewModel model)
        {
            return;
        }

        var windowWidth = TopLevel.GetTopLevel(this)?.ClientSize.Width ?? 0;
        model.BlameCramped = windowWidth > 0 && Bounds.Width < windowWidth / 2;
    }

    public IBrush AddedLineBackground
    {
        get => GetValue(AddedLineBackgroundProperty);
        set => SetValue(AddedLineBackgroundProperty, value);
    }

    public IBrush RemovedLineBackground
    {
        get => GetValue(RemovedLineBackgroundProperty);
        set => SetValue(RemovedLineBackgroundProperty, value);
    }

    public IBrush FunnelAddedBrush
    {
        get => GetValue(FunnelAddedBrushProperty);
        set => SetValue(FunnelAddedBrushProperty, value);
    }

    public IBrush FunnelRemovedBrush
    {
        get => GetValue(FunnelRemovedBrushProperty);
        set => SetValue(FunnelRemovedBrushProperty, value);
    }

    public IBrush FunnelModifiedBrush
    {
        get => GetValue(FunnelModifiedBrushProperty);
        set => SetValue(FunnelModifiedBrushProperty, value);
    }

    public IBrush EditorBackgroundBrush
    {
        get => GetValue(EditorBackgroundBrushProperty);
        set => SetValue(EditorBackgroundBrushProperty, value);
    }

    public IBrush ToolbarBrush
    {
        get => GetValue(ToolbarBrushProperty);
        set => SetValue(ToolbarBrushProperty, value);
    }

    public IBrush DiffBorderBrush
    {
        get => GetValue(DiffBorderBrushProperty);
        set => SetValue(DiffBorderBrushProperty, value);
    }

    public IBrush TextBrush
    {
        get => GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public IBrush MutedTextBrush
    {
        get => GetValue(MutedTextBrushProperty);
        set => SetValue(MutedTextBrushProperty, value);
    }

    public IBrush DefaultSyntaxBrush
    {
        get => GetValue(DefaultSyntaxBrushProperty);
        set => SetValue(DefaultSyntaxBrushProperty, value);
    }

    public bool ShowHeader
    {
        get => GetValue(ShowHeaderProperty);
        set => SetValue(ShowHeaderProperty, value);
    }

    public object? HeaderExtra
    {
        get => GetValue(HeaderExtraProperty);
        set => SetValue(HeaderExtraProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AddedLineBackgroundProperty
            || change.Property == RemovedLineBackgroundProperty
            || change.Property == DefaultSyntaxBrushProperty)
        {
            ApplyThemeToViewModel();
        }

        if (change.Property == TextBrushProperty
            || change.Property == MutedTextBrushProperty
            || change.Property == DiffBorderBrushProperty
            || change.Property == AddedLineBackgroundProperty
            || change.Property == RemovedLineBackgroundProperty
            || change.Property == FunnelAddedBrushProperty
            || change.Property == FunnelRemovedBrushProperty)
        {
            // Rendered with the old colors: none can be reused.
            _previews.Clear();
            _shownPreview = null;
            RefreshPreview();
        }
    }

    // A blame label's width as its cell draws it: this view's font, at the
    // cell's size (DiffView.axaml, TextBlock.blameCell).
    private double MeasureBlameLabel(string label)
    {
        var probe = new TextBlock { Text = label, FontSize = 11, FontFamily = FontFamily };
        probe.Measure(Size.Infinity);
        return probe.DesiredSize.Width;
    }

    private void WatchModel(DiffViewModel? model)
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelPropertyChanged;
            _model.PropertyChanging -= OnModelPropertyChanging;
        }

        _model = model;
        if (_model is not null)
        {
            _model.PropertyChanged += OnModelPropertyChanged;
            _model.PropertyChanging += OnModelPropertyChanging;
            _model.MeasureBlameLabel = MeasureBlameLabel;
        }

        RefreshPreview();
    }

    // The row at the top of the line diff when View / Diff flips: the rebuilt
    // rows scroll back to it (or the next line still shown).
    private DiffRowVm? _flipAnchor;

    private void OnModelPropertyChanging(object? sender, PropertyChangingEventArgs e)
    {
        if (e.PropertyName == nameof(DiffViewModel.IsViewMode) && IsEffectivelyVisible && _model is { IsPreviewActive: false })
        {
            _flipAnchor = TopRow();
        }
    }

    // The first row whose container reaches into the viewport.
    private DiffRowVm? TopRow()
    {
        if (RowsHost.ItemsPanelRoot is not { } panel)
        {
            return null;
        }

        DiffRowVm? top = null;
        var best = double.MaxValue;
        foreach (var child in panel.Children)
        {
            if (child is Control { IsVisible: true, DataContext: DiffRowVm row } container
                && container.TranslatePoint(default, RowsScroller) is { } at
                && at.Y + container.Bounds.Height > 0.5 && at.Y < best)
            {
                best = at.Y;
                top = row;
            }
        }

        return top;
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiffViewModel.IsViewMode) && _flipAnchor is { } anchor && _model is { } flipped)
        {
            _flipAnchor = null;
            var row = flipped.RowIndexNear(anchor);
            if (row > 0)
            {
                ScrollRestore.Start(RowsScroller, row * RowHeight, () => RowTop(row));
            }
            else
            {
                ScrollRestore.Cancel(RowsScroller);
                RowsScroller.Offset = new Vector(RowsScroller.Offset.X, 0);
            }
        }

        // Wrap toggled: re-measure the rows from their panel down, in one pass.
        // Otherwise the panel measures first and skips the rows (they still
        // read as valid - only the text deep inside changed), the text then
        // grows them on its own, and the panel, finding heights it did not
        // measure, loses which row is at the top: it guesses another from the
        // average height, jumps there and rebuilds every row on screen.
        if (e.PropertyName == nameof(DiffViewModel.WrapLines) && RowsHost.ItemsPanelRoot is { } panel)
        {
            panel.InvalidateMeasure();
            foreach (var row in panel.Children)
            {
                foreach (var layoutable in global::Avalonia.VisualTree.VisualExtensions.GetSelfAndVisualDescendants(row).OfType<global::Avalonia.Layout.Layoutable>())
                {
                    layoutable.InvalidateMeasure();
                }
            }
        }

        if (e.PropertyName is nameof(DiffViewModel.IsPreviewActive) or nameof(DiffViewModel.PreviewVersion))
        {
            RefreshPreview();
        }

        // New content arrived (PreviewVersion bumps with every diff): if the
        // host asked to come back to a spot, scroll there - in the view that
        // is on screen (a docked and a floating view can share one model).
        if (e.PropertyName == nameof(DiffViewModel.PreviewVersion)
            && _model is { IsPlaceholder: false, RestoreScroll: { } spot } model
            && IsEffectivelyVisible)
        {
            model.RestoreScroll = null;
            var scroller = model.IsPreviewActive ? PreviewHost.Content as ScrollViewer : RowsScroller;
            if (spot.FileStart)
            {
                // The top needs no holding; a row further down does while
                // the rows above it are still being measured.
                var row = model.IsPreviewActive ? 0 : model.StartRow();
                if (row > 0)
                {
                    ScrollRestore.Start(scroller, row * RowHeight, () => RowTop(row));
                }
                else if (scroller is not null)
                {
                    ScrollRestore.Cancel(scroller);
                    scroller.Offset = new Vector(scroller.Offset.X, 0);
                }
            }
            else if (spot.Preview && model.IsPreviewActive && _reusedPreview && scroller is not null)
            {
                // The same control came back: it is already there.
                scroller.Offset = new Vector(scroller.Offset.X, spot.Offset);
            }
            else if (spot.Preview == model.IsPreviewActive)
            {
                ScrollRestore.Start(scroller, spot.Offset);
            }

            // No scroll event comes when the spot was already the offset (the
            // line diff kept from an earlier file): report it, or the spot
            // still describes the file shown before.
            if (scroller is not null)
            {
                ReportScroll(scroller, model.IsPreviewActive);
            }
        }
    }

    // Scrolled back to the very top, the virtualizing panel sees its viewport
    // start at exactly 0 and treats it as a jump: it rebinds every row on
    // screen instead of adding the few above (~100ms). Shifted 1px up, it sees
    // 1 and takes its usual path; only the first row's blank top pixel hides.
    // Only while the diff scrolls: a diff shorter than the pane would then
    // fill the panel from its end, by estimated heights, and can drop rows.
    // (The gap between the two thresholds keeps the 1px from flipping it.)
    private void KeepTopAnchored()
    {
        if (RowsHost.ItemsPanelRoot is not { } panel)
        {
            return;
        }

        var spare = RowsScroller.Extent.Height - RowsScroller.Viewport.Height;
        if (spare > RowHeight && panel.Margin.Top == 0)
        {
            panel.Margin = new Thickness(0, -1, 0, 0);
        }
        else if (spare <= 0 && panel.Margin.Top != 0)
        {
            panel.Margin = default;
        }
    }

    // Where a line diff row starts in the scrolled content, once it has a
    // container: wrapped rows above it make that more than index * RowHeight.
    private double? RowTop(int index)
    {
        if (RowsHost.ContainerFromIndex(index) is not { } container)
        {
            RowsHost.ScrollIntoView(index);
            if (RowsHost.ContainerFromIndex(index) is not { } brought)
            {
                return null;
            }

            container = brought;
        }

        return container.TranslatePoint(default, RowsScroller) is { } at ? RowsScroller.Offset.Y + at.Y : null;
    }

    private void OnBlameTapped(object? sender, TappedEventArgs e)
    {
        // Uncommitted lines have no commit to open.
        if (sender is Control { DataContext: DiffRowVm { Blame: { } blame } }
            && _model?.OpenCommitHandler is { } open
            && blame.Sha.Trim('0').Length > 0)
        {
            e.Handled = true;
            open(blame);
        }
    }

    private void ReportScroll(ScrollViewer scroller, bool preview)
    {
        // A cached preview off screen does not speak for the view.
        if (preview && PreviewHost.Content != scroller)
        {
            return;
        }

        if (_model is { IsPlaceholder: false } model && model.IsPreviewActive == preview && IsEffectivelyVisible)
        {
            model.ScrollSpot = new DiffScrollSpot(preview, scroller.Offset.Y);
        }
    }

    // Each view renders its own preview (a control has one visual parent, and
    // the docked pane and a floating window can share one model).
    private void RefreshPreview()
    {
        // Brush properties can change while the XAML is still loading.
        if (PreviewHost is null)
        {
            return;
        }

        if (_model is { IsPreviewActive: true, Previewer: { } previewer })
        {
            // The next file is on its way; it renders when it lands.
            if (_model.IsLoading)
            {
                return;
            }

            var request = _model.CreatePreviewRequest();
            var key = new PreviewKey(request.FileName, request.Context?.DocumentPath, request.Mode, request.CollapseUnchanged, request.OldText, request.NewText);
            if (key == _shownPreview && PreviewHost.Content is not null)
            {
                return;
            }

            var cached = _previews.FindIndex(p => p.Key == key);
            Control preview;
            if (cached >= 0)
            {
                preview = _previews[cached].View;
                _previews.RemoveAt(cached);
                _reusedPreview = true;

                // Another document reached other than by Back starts at the top.
                if (_model.RestoreScroll is null && preview is ScrollViewer reused && !key.SameDocument(_shownPreview))
                {
                    reused.Offset = default;
                }
            }
            else
            {
                preview = previewer.Render(request, PreviewTheme());
                if (preview is ScrollViewer scroller)
                {
                    scroller.ScrollChanged += (_, _) => ReportScroll(scroller, preview: true);
                }

                _reusedPreview = false;
            }

            _previews.Add((key, preview));
            if (_previews.Count > PreviewCacheSize)
            {
                _previews.RemoveAt(0);
            }

            _shownPreview = key;
            PreviewHost.Content = preview;

            // A reused preview comes back already scrolled - no scroll event
            // tells where, and a link followed next must remember this spot.
            if (preview is ScrollViewer shown)
            {
                ReportScroll(shown, preview: true);
            }
        }
        else
        {
            PreviewHost.Content = null;
        }
    }

    private DocumentPreviewTheme PreviewTheme() => new(
        TextBrush,
        MutedTextBrush,
        DiffBorderBrush,
        AddedLineBackground,
        RemovedLineBackground,
        Opaque(FunnelAddedBrush),
        Opaque(FunnelRemovedBrush));

    private static IBrush Opaque(IBrush brush)
        => brush is ISolidColorBrush solid ? new ImmutableSolidColorBrush(Color.FromRgb(solid.Color.R, solid.Color.G, solid.Color.B)) : brush;

    private void OnGapTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is DiffViewModel model
            && sender is Control { DataContext: DiffRowVm { IsGap: true } row })
        {
            model.ExpandGap(row.GapRunStart);
        }
    }

    private void ApplyThemeToViewModel()
    {
        if (DataContext is DiffViewModel model)
        {
            model.SetTheme(AddedLineBackground, RemovedLineBackground, DefaultSyntaxBrush);
        }
    }

    private static IBrush Brush(string hex) => HexBrush.Parse(hex);

    // What a preview shows. Texts compare by value; the file identifies the
    // document across renders.
    private sealed record PreviewKey(
        string? FileName,
        string? DocumentPath,
        DocumentPreviewMode Mode,
        bool CollapseUnchanged,
        string OldText,
        string NewText)
    {
        public bool SameDocument(PreviewKey? other)
            => other is not null && other.FileName == FileName && other.DocumentPath == DocumentPath;
    }
}
