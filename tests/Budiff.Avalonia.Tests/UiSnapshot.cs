using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Budiff.Avalonia.Tests;

internal static class UiSnapshot
{
    public static string OutputDir { get; } = ResolveOutputDir();

    public readonly record struct Snapshot(string Path, int Width, int Height);

    public static Snapshot Capture(Window window, string name)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        Pump();
        using WriteableBitmap frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("CaptureRenderedFrame returned null.");

        Directory.CreateDirectory(OutputDir);
        var path = Path.Combine(OutputDir, name + ".png");
        frame.Save(path);
        return new Snapshot(path, frame.PixelSize.Width, frame.PixelSize.Height);
    }

    public static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    private static string ResolveOutputDir()
    {
        var dir = Environment.GetEnvironmentVariable("BUDIFF_UISNAPSHOT_DIR");
        return string.IsNullOrWhiteSpace(dir)
            ? Path.Combine(AppContext.BaseDirectory, "ui-snapshots")
            : dir;
    }
}
