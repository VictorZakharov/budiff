using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Budiff.App;

// The icons the app shows. Each is a Material Design icon (Pictogrammers,
// Apache 2.0); only these ship, not the whole set of ~7,000.
public enum AppIconKind
{
    Account,
    AccountGroup,
    ArrowLeft,
    ChevronDoubleLeft,
    ChevronDoubleRight,
    ChevronDown,
    ChevronRight,
    Close,
    DockWindow,
    FileCompare,
    FileOutline,
    FolderOpen,
    FolderOpenOutline,
    FolderOutline,
    FolderQuestionOutline,
    OpenInNew,
    Package,
    Refresh,
    SourcePull,
    Tune,
}

// One icon: its path on Material's 24x24 canvas, scaled to the control's size
// and filled with the inherited text colour. The whole box takes clicks, not
// just the inked part.
public sealed class AppIcon : Control
{
    public static readonly StyledProperty<AppIconKind> KindProperty =
        AvaloniaProperty.Register<AppIcon, AppIconKind>(nameof(Kind));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<AppIcon>();

    private const double CanvasSize = 24;

    private static readonly ConcurrentDictionary<AppIconKind, Geometry> Geometries = new();

    static AppIcon()
    {
        AffectsRender<AppIcon>(KindProperty, ForegroundProperty);
        WidthProperty.OverrideDefaultValue<AppIcon>(16);
        HeightProperty.OverrideDefaultValue<AppIcon>(16);
    }

    public AppIconKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.Transparent, bounds);
        var size = Math.Min(bounds.Width, bounds.Height);
        if (size <= 0 || Foreground is not { } ink)
        {
            return;
        }

        var scale = size / CanvasSize;
        var offset = Matrix.CreateTranslation((bounds.Width - size) / 2, (bounds.Height - size) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * offset))
        {
            context.DrawGeometry(ink, null, GeometryOf(Kind));
        }
    }

    internal static Geometry GeometryOf(AppIconKind kind) => Geometries.GetOrAdd(kind, k => StreamGeometry.Parse(PathOf(k)));

    private static string PathOf(AppIconKind kind) => kind switch
    {
        AppIconKind.Account => "M12,4A4,4 0 0,1 16,8A4,4 0 0,1 12,12A4,4 0 0,1 8,8A4,4 0 0,1 12,4M12,14C16.42,14 20,15.79 20,18V20H4V18C4,15.79 7.58,14 12,14Z",
        AppIconKind.AccountGroup => "M12,5.5A3.5,3.5 0 0,1 15.5,9A3.5,3.5 0 0,1 12,12.5A3.5,3.5 0 0,1 8.5,9A3.5,3.5 0 0,1 12,5.5M5,8C5.56,8 6.08,8.15 6.53,8.42C6.38,9.85 6.8,11.27 7.66,12.38C7.16,13.34 6.16,14 5,14A3,3 0 0,1 2,11A3,3 0 0,1 5,8M19,8A3,3 0 0,1 22,11A3,3 0 0,1 19,14C17.84,14 16.84,13.34 16.34,12.38C17.2,11.27 17.62,9.85 17.47,8.42C17.92,8.15 18.44,8 19,8M5.5,18.25C5.5,16.18 8.41,14.5 12,14.5C15.59,14.5 18.5,16.18 18.5,18.25V20H5.5V18.25M0,20V18.5C0,17.11 1.89,15.94 4.45,15.6C3.86,16.28 3.5,17.22 3.5,18.25V20H0M24,20H20.5V18.25C20.5,17.22 20.14,16.28 19.55,15.6C22.11,15.94 24,17.11 24,18.5V20Z",
        AppIconKind.ArrowLeft => "M20,11V13H8L13.5,18.5L12.08,19.92L4.16,12L12.08,4.08L13.5,5.5L8,11H20Z",
        AppIconKind.ChevronDoubleLeft => "M18.41,7.41L17,6L11,12L17,18L18.41,16.59L13.83,12L18.41,7.41M12.41,7.41L11,6L5,12L11,18L12.41,16.59L7.83,12L12.41,7.41Z",
        AppIconKind.ChevronDoubleRight => "M5.59,7.41L7,6L13,12L7,18L5.59,16.59L10.17,12L5.59,7.41M11.59,7.41L13,6L19,12L13,18L11.59,16.59L16.17,12L11.59,7.41Z",
        AppIconKind.ChevronDown => "M7.41,8.58L12,13.17L16.59,8.58L18,10L12,16L6,10L7.41,8.58Z",
        AppIconKind.ChevronRight => "M8.59,16.58L13.17,12L8.59,7.41L10,6L16,12L10,18L8.59,16.58Z",
        AppIconKind.Close => "M19,6.41L17.59,5L12,10.59L6.41,5L5,6.41L10.59,12L5,17.59L6.41,19L12,13.41L17.59,19L19,17.59L13.41,12L19,6.41Z",
        AppIconKind.DockWindow => "M18 18V20H4A2 2 0 0 1 2 18V8H4V18M22 6V14A2 2 0 0 1 20 16H8A2 2 0 0 1 6 14V6A2 2 0 0 1 8 4H20A2 2 0 0 1 22 6M20 6H8V14H20Z",
        AppIconKind.FileCompare => "M10,18H6V16H10V18M10,14H6V12H10V14M10,1V2H6C4.89,2 4,2.89 4,4V20A2,2 0 0,0 6,22H10V23H12V1H10M20,8V20C20,21.11 19.11,22 18,22H14V20H18V11H14V9H18.5L14,4.5V2L20,8M16,14H14V12H16V14M16,18H14V16H16V18Z",
        AppIconKind.FileOutline => "M14,2H6A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2M18,20H6V4H13V9H18V20Z",
        AppIconKind.FolderOpen => "M19,20H4C2.89,20 2,19.1 2,18V6C2,4.89 2.89,4 4,4H10L12,6H19A2,2 0 0,1 21,8H21L4,8V18L6.14,10H23.21L20.93,18.5C20.7,19.37 19.92,20 19,20Z",
        AppIconKind.FolderOpenOutline => "M6.1,10L4,18V8H21A2,2 0 0,0 19,6H12L10,4H4A2,2 0 0,0 2,6V18A2,2 0 0,0 4,20H19C19.9,20 20.7,19.4 20.9,18.5L23.2,10H6.1M19,18H6L7.6,12H20.6L19,18Z",
        AppIconKind.FolderOutline => "M20,18H4V8H20M20,6H12L10,4H4C2.89,4 2,4.89 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V8C22,6.89 21.1,6 20,6Z",
        AppIconKind.FolderQuestionOutline => "M20 18H4V8H20M20 6H12L10 4H4C2.9 4 2 4.9 2 6V18C2 19.1 2.9 20 4 20H20C21.1 20 22 19.1 22 18V8C22 6.9 21.1 6 20 6M19 11.7C19 13.4 17 13.5 17 15H15.7C15.7 12.8 17.7 13 17.7 11.7C17.7 11 17.1 10.4 16.4 10.4C15.7 10.4 15.1 11 15.1 11.7H13.8C13.8 10.2 15 9 16.5 9S19 10.2 19 11.7M17 15.7V17H15.7V15.7H17Z",
        AppIconKind.OpenInNew => "M14,3V5H17.59L7.76,14.83L9.17,16.24L19,6.41V10H21V3M19,19H5V5H12V3H5C3.89,3 3,3.9 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V12H19V19Z",
        AppIconKind.Package => "M5.12,5H18.87L17.93,4H5.93L5.12,5M20.54,5.23C20.83,5.57 21,6 21,6.5V19A2,2 0 0,1 19,21H5A2,2 0 0,1 3,19V6.5C3,6 3.17,5.57 3.46,5.23L4.84,3.55C5.12,3.21 5.53,3 6,3H18C18.47,3 18.88,3.21 19.15,3.55L20.54,5.23M6,18H12V15H6V18Z",
        AppIconKind.Refresh => "M17.65,6.35C16.2,4.9 14.21,4 12,4A8,8 0 0,0 4,12A8,8 0 0,0 12,20C15.73,20 18.84,17.45 19.73,14H17.65C16.83,16.33 14.61,18 12,18A6,6 0 0,1 6,12A6,6 0 0,1 12,6C13.66,6 15.14,6.69 16.22,7.78L13,11H20V4L17.65,6.35Z",
        AppIconKind.SourcePull => "M6,3A3,3 0 0,1 9,6C9,7.31 8.17,8.42 7,8.83V15.17C8.17,15.58 9,16.69 9,18A3,3 0 0,1 6,21A3,3 0 0,1 3,18C3,16.69 3.83,15.58 5,15.17V8.83C3.83,8.42 3,7.31 3,6A3,3 0 0,1 6,3M6,5A1,1 0 0,0 5,6A1,1 0 0,0 6,7A1,1 0 0,0 7,6A1,1 0 0,0 6,5M6,17A1,1 0 0,0 5,18A1,1 0 0,0 6,19A1,1 0 0,0 7,18A1,1 0 0,0 6,17M21,18A3,3 0 0,1 18,21A3,3 0 0,1 15,18C15,16.69 15.83,15.58 17,15.17V7H15V10.25L10.75,6L15,1.75V5H17A2,2 0 0,1 19,7V15.17C20.17,15.58 21,16.69 21,18M18,17A1,1 0 0,0 17,18A1,1 0 0,0 18,19A1,1 0 0,0 19,18A1,1 0 0,0 18,17Z",
        AppIconKind.Tune => "M3,17V19H9V17H3M3,5V7H13V5H3M13,21V19H21V17H13V15H11V21H13M7,9V11H3V13H7V15H9V9H7M21,13V11H11V13H21M15,9H17V7H21V5H17V3H15V9Z",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
