using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Budiff.App;
using Xunit;

namespace Budiff.Avalonia.Tests;

public sealed class AppIconTests
{
    // Every icon has a path, inside Material's 24x24 canvas. Parsing a
    // geometry needs Avalonia's render platform, hence AvaloniaFact.
    [AvaloniaFact]
    public void EveryIconHasAPathOnTheMaterialCanvas()
    {
        foreach (var kind in Enum.GetValues<AppIconKind>())
        {
            var bounds = AppIcon.GeometryOf(kind).Bounds;
            Assert.True(bounds.Width > 0 && bounds.Height > 0, $"{kind} draws nothing");
            Assert.True(new Rect(0, 0, 24, 24).Contains(bounds), $"{kind} leaves the canvas: {bounds}");
        }
    }

    // Inked in the inherited text colour where the path is, nothing elsewhere;
    // the 24x24 canvas scales to the control's size.
    [AvaloniaFact]
    public void DrawsTheIconInTheInheritedForeground()
    {
        var holder = new Border { Background = Brushes.Black, Child = new AppIcon { Kind = AppIconKind.Close, Width = 48, Height = 48 } };
        TextElement.SetForeground(holder, Brushes.Red);
        var window = new Window { Width = 48, Height = 48, Content = holder };
        window.Show();

        var frame = window.CaptureRenderedFrame()!;
        Assert.Equal(Colors.Red, PixelAt(frame, 24, 24));
        Assert.Equal(Colors.Black, PixelAt(frame, 24, 4));
        window.Close();
    }

    private static Color PixelAt(WriteableBitmap bitmap, int x, int y)
    {
        using var buffer = bitmap.Lock();
        var at = buffer.Address + (y * buffer.RowBytes) + (x * 4);
        var bytes = new byte[4];
        Marshal.Copy(at, bytes, 0, 4);
        return buffer.Format == PixelFormats.Rgba8888
            ? Color.FromArgb(bytes[3], bytes[0], bytes[1], bytes[2])
            : Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
    }
}
