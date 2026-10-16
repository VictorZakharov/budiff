using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Budiff;
using Budiff.Avalonia.Markdown;
using Xunit;

namespace Budiff.Avalonia.Tests;

// Clicking a preview picture opens the lightbox over the window: keys and
// side buttons step through the document's pictures, the wheel zooms around
// the pointer, a drag pans the zoomed picture, Esc or a click beside it
// closes. A picture linked elsewhere still follows its link.
public sealed class LightboxTests
{
    private const string Doc =
        "# Shots\n\n" +
        "![First shot](img/a.png)\n\n" +
        "Some text with an inline ![icon](img/icon.png) picture.\n\n" +
        "[![Second shot](img/b.png)](img/b.png)\n\n" +
        "[![badge](img/badge.png)](https://example.test/ci)\n\n" +
        "![](img/wide.png)\n";

    [AvaloniaFact]
    public void Clicking_OpensTheLightbox_AndStepsThroughThePictures()
    {
        var (window, host, _) = Show();
        Click(window, Picture(host, 800));
        var box = Box(window);
        Assert.Equal(0, box.Index);
        Assert.Equal("1 / 4  ·  First shot", box.Caption);
        Assert.Equal(800, box.View.Picture!.PixelSize.Width);
        Assert.True(box.View.IsFitted);
        Assert.True(box.IsFocused);
        UiSnapshot.Capture(window, "markdown-lightbox");

        Press(window, Key.Right, PhysicalKey.ArrowRight);
        Assert.Equal(1, box.Index);
        Assert.Equal("2 / 4  ·  icon", box.Caption);

        Click(window, SideButton(box, HorizontalAlignment.Right));
        Assert.Equal(2, box.Index);
        Assert.Equal(600, box.View.Picture!.PixelSize.Width);

        // The wide one: the preview's copy is scaled down, the lightbox
        // swaps in the full picture; no caption but its file name.
        Press(window, Key.Right, PhysicalKey.ArrowRight);
        Assert.Equal(3, box.Index);
        Assert.Equal("4 / 4  ·  wide.png", box.Caption);
        Assert.False(SideButton(box, HorizontalAlignment.Right).IsVisible);
        PumpUntil(() => box.View.Picture!.PixelSize.Width == 3000);
        Press(window, Key.Right, PhysicalKey.ArrowRight);
        Assert.Equal(3, box.Index);

        Press(window, Key.Home, PhysicalKey.Home);
        Assert.Equal(0, box.Index);
        Assert.False(SideButton(box, HorizontalAlignment.Left).IsVisible);
        Press(window, Key.Left, PhysicalKey.ArrowLeft);
        Assert.Equal(0, box.Index);

        Press(window, Key.Escape, PhysicalKey.Escape);
        Assert.Empty(Boxes(window));
        window.Close();
    }

    [AvaloniaFact]
    public void TheWheel_ZoomsAroundThePointer_AndADragPans()
    {
        var (window, host, _) = Show();
        Click(window, Picture(host, 800));
        var box = Box(window);
        var view = box.View;
        var fit = view.Scale;
        Assert.InRange(fit, 0.5, 0.99);

        // The picture point under the pointer stays under it.
        var at = new Point(500, 380);
        var before = Under(view, at);
        window.MouseMove(at);
        window.MouseWheel(at, new Vector(0, 3));
        UiSnapshot.Pump();
        Assert.Equal(fit * Math.Pow(1.2, 3), view.Scale, 3);
        Assert.False(view.IsFitted);
        Assert.True(view.CanPan);
        var after = Under(view, at);
        Assert.Equal(before.X, after.X, 0.5);
        Assert.Equal(before.Y, after.Y, 0.5);
        Assert.EndsWith($"{Math.Round(view.Scale * 100)}%", box.Caption, StringComparison.Ordinal);

        // A drag pans (and is no click: the lightbox stays).
        var start = view.PictureBounds;
        window.MouseDown(at, MouseButton.Left);
        window.MouseMove(at + new Vector(-60, -10));
        window.MouseUp(at + new Vector(-60, -10), MouseButton.Left);
        UiSnapshot.Pump();
        Assert.Equal(start.X - 60, view.PictureBounds.X, 0.5);
        Assert.Equal(start.Y - 10, view.PictureBounds.Y, 0.5);
        Assert.Single(Boxes(window));

        // ... but never off the screen. (Pressed elsewhere: a second press
        // at the same spot would be a double-click.)
        var from = new Point(300, 250);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Vector(2000, 2000));
        window.MouseUp(from + new Vector(2000, 2000), MouseButton.Left);
        UiSnapshot.Pump();
        Assert.Equal(0, view.PictureBounds.X, 0.5);
        Assert.Equal(0, view.PictureBounds.Y, 0.5);
        UiSnapshot.Capture(window, "markdown-lightbox-zoomed");

        // Out again: it fits, centered.
        window.MouseWheel(at, new Vector(0, -10));
        UiSnapshot.Pump();
        Assert.True(view.IsFitted);
        Assert.Equal(fit, view.Scale, 3);
        Assert.Equal(view.Bounds.Width / 2, view.PictureBounds.Center.X, 0.5);
        Assert.Equal("1 / 4  ·  First shot", box.Caption);

        // Double-click: natural size, and back.
        DoubleClick(window, at);
        Assert.Equal(1, view.Scale, 3);
        DoubleClick(window, at);
        Assert.True(view.IsFitted);

        // Keys: + zooms, 0 fits.
        Press(window, Key.Add, PhysicalKey.NumPadAdd);
        Assert.Equal(fit * 1.25, view.Scale, 3);
        Press(window, Key.D0, PhysicalKey.Digit0);
        Assert.True(view.IsFitted);

        // A click beside the picture closes it.
        Click(window, new Point(30, 120));
        Assert.Empty(Boxes(window));
        window.Close();
    }

    [AvaloniaFact]
    public void APictureLinkedElsewhere_FollowsItsLink()
    {
        var (window, host, launched) = Show();
        Click(window, Picture(host, 80));
        Assert.Empty(Boxes(window));
        Assert.Equal(new[] { "https://example.test/ci" }, launched.Select(u => u.OriginalString));
        window.Close();
    }

    // The preview it belongs to goes away (another file): so does the lightbox.
    [AvaloniaFact]
    public void TheLightbox_ClosesWithItsPreview()
    {
        var (window, host, _) = Show();
        Click(window, Picture(host, 800));
        Assert.Single(Boxes(window));

        var model = (DiffViewModel)((DiffView)window.Content!).DataContext!;
        model.SetDiff(LineDiff.Compute("other\n", "other\n"));
        UiSnapshot.Pump();
        Assert.Empty(Boxes(window));
        window.Close();
    }

    private static (Window Window, ContentControl Host, List<Uri> Launched) Show()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["img/a.png"] = MarkdownLinksTests.Png(800, 500, Colors.IndianRed),
            ["img/icon.png"] = MarkdownLinksTests.Png(16, 16, Colors.Orange),
            ["img/b.png"] = MarkdownLinksTests.Png(600, 400, Colors.SeaGreen),
            ["img/badge.png"] = MarkdownLinksTests.Png(80, 20, Colors.SlateBlue),
            ["img/wide.png"] = MarkdownLinksTests.Png(3000, 1000, Colors.SteelBlue),
        };
        var launched = new List<Uri>();
        var previewer = new MarkdownPreviewer { OpenExternal = (_, uri) => { launched.Add(uri); return System.Threading.Tasks.Task.CompletedTask; } };
        var model = new DiffViewModel { Extension = ".md", Previewer = previewer };
        model.SetDiff(LineDiff.Compute(Doc, Doc));
        model.PreviewContext = new DocumentPreviewContext("README.md", (path, _) => files.GetValueOrDefault(path), _ => { });
        model.ShowPreview = true;
        var view = new DiffView { DataContext = model };
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        UiSnapshot.Pump();
        var host = view.FindControl<ContentControl>("PreviewHost")!;
        PumpUntil(() => host.GetVisualDescendants().OfType<Image>().Count(i => i.Source is not null) == 5, () => PreviewPictures.Describe(host));
        return (window, host, launched);
    }

    private static Image Picture(ContentControl host, int pixelWidth)
        => host.GetVisualDescendants().OfType<Image>().Single(i => i.Source is Bitmap { PixelSize.Width: var w } && w == pixelWidth);

    private static IEnumerable<Lightbox> Boxes(Window window)
        => OverlayLayer.GetOverlayLayer(window)!.Children.OfType<Lightbox>();

    private static Lightbox Box(Window window) => Assert.Single(Boxes(window));

    private static Border SideButton(Lightbox box, HorizontalAlignment side)
        => box.Children.OfType<Border>().Single(b => b.VerticalAlignment == VerticalAlignment.Center && b.HorizontalAlignment == side);

    // The picture's own coordinates under a point of the view.
    private static Point Under(PictureView view, Point at)
        => new((at.X - view.PictureBounds.X) / view.Scale, (at.Y - view.PictureBounds.Y) / view.Scale);

    private static void Click(Window window, Control control)
    {
        control.BringIntoView();
        UiSnapshot.Pump();
        Click(window, control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value);
    }

    private static void Click(Window window, Point at)
    {
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        UiSnapshot.Pump();
    }

    private static void DoubleClick(Window window, Point at)
    {
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        UiSnapshot.Pump();
    }

    private static void Press(Window window, Key key, PhysicalKey physical)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        window.KeyRelease(key, RawInputModifiers.None, physical, null);
        UiSnapshot.Pump();
    }

    private static void PumpUntil(Func<bool> done, Func<string>? describe = null)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.ElapsedMilliseconds < 5000)
        {
            UiSnapshot.Pump();
            Thread.Sleep(10);
        }

        UiSnapshot.Pump();
        Assert.True(done(), describe is null ? "timed out" : "timed out: " + describe());
    }
}
