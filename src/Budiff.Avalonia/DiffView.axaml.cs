using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Media;
using global::Avalonia.Media.Immutable;
using global::Avalonia.Markup.Xaml;

namespace Budiff.Avalonia;

public partial class DiffView : UserControl
{
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
        DataContextChanged += (_, _) =>
        {
            ApplyThemeToViewModel();
            UpdateBlameCramped();
        };

        // LayoutUpdated (not just SizeChanged): a fixed-width docked pane gets
        // NO SizeChanged when only the window resizes, yet "half the window"
        // changes. The check is a couple of comparisons - cheap per pass.
        LayoutUpdated += (_, _) => UpdateBlameCramped();
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
    }

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
}
