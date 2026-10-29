using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Budiff.App;
using Budiff.Avalonia.Markdown;
using Xunit;

namespace Budiff.Avalonia.Tests;

// The README's 2x2 screenshot grid, rendered by the real MainWindow over a
// real repository: a clone of the public VictorZakharov/last-stand at
// BUDIFF_README_REPO (the test skips without it). The pull request shot asks
// GitHub through gh, so gh must be signed in. Captures go where UiSnapshot
// writes (BUDIFF_UISNAPSHOT_DIR); copy them to docs/ by hand.
public sealed class ReadmeGridScreenshotTests
{
    private const int Width = 1440;
    private const int Height = 810;
    private const int PickerWidth = 900;
    private const int PickerHeight = 600;
    private const int TitleBarHeight = 32;

    [AvaloniaFact]
    public void ReadmeGrid_FromARealRepository()
    {
        var root = Environment.GetEnvironmentVariable("BUDIFF_README_REPO");
        Assert.SkipWhen(string.IsNullOrEmpty(root), "Set BUDIFF_README_REPO to a clone of VictorZakharov/last-stand.");
        var file = Environment.GetEnvironmentVariable("BUDIFF_README_DIFF") ?? "src/entities/viewModel.ts";

        var window = new MainWindow(root!, gitRoot: true) { Width = Width, Height = Height };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");

        // 1. The bird's-eye over the last week, src opened.
        vm.Analysis.SelectedPeriod = vm.Analysis.Periods.Single(p => p.Label == "Last week");
        WaitUntil(() => vm.Analysis.AppliedSinceDays == 7 && !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "last week");
        foreach (var top in vm.Analysis.Roots.Where(r => r.Name == "src"))
        {
            top.IsExpanded = true;
        }

        UiSnapshot.Pump();
        UiSnapshot.Capture(window, "readme-birdseye");

        // 2. A file's side-by-side diff, docked wide.
        Open(window, file);
        UiSnapshot.Capture(window, "readme-diff");

        // 3. The README's formatted preview: its pictures, changed blocks marked.
        Open(window, "README.md");
        vm.Diff.ShowPreview = true;
        WaitUntil(() => vm.Diff.IsPreviewActive, "preview");
        Thread.Sleep(500);
        WaitUntil(() => PicturesSettled(window), "preview pictures", throwOnTimeout: false);
        UiSnapshot.Capture(window, "readme-markdown");
        vm.Diff.ShowPreview = false;

        // 4. The pull request picker - yours first, searchable - over the
        // dimmed bird's-eye, the way it opens from the Period list.
        vm.CloseDiffCommand.Execute(null);
        UiSnapshot.Pump();
        var backdrop = UiSnapshot.Capture(window, "readme-pr-backdrop");
        var picker = new PullRequestPickerWindow(new PullRequestPickerViewModel(vm.Analysis.PullRequests!)) { Width = PickerWidth, Height = PickerHeight };
        picker.Show();
        var list = picker.ViewModel;
        WaitUntil(() => !list.IsLoading && list.Items.Count > 0, "pull requests", timeoutMs: 60_000);
        WaitUntil(() => !list.IsLoading && list.Items.All(i => i.Counts is not null || i.CountsUnavailable), "pull request counts", throwOnTimeout: false, timeoutMs: 60_000);
        list.SelectedItem = list.Items[0];
        UiSnapshot.Pump();
        var dialog = UiSnapshot.Capture(picker, "readme-pr-picker");
        picker.Close();
        Composite(backdrop.Path, dialog.Path, picker.Title ?? string.Empty, "readme-pr");
        window.Close();
    }

    private static CodeAnalysisRow Row(MainWindowViewModel vm, string path)
        => vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).First(r => r.IsFile && r.RelativePath.Replace('\\', '/') == path);

    private static void Open(MainWindow window, string path)
    {
        var vm = window.ViewModel;
        var row = Row(vm, path);
        _ = vm.Analysis.OpenAsync(row);
        WaitUntil(() => vm.IsDiffOpen && !vm.Diff.IsLoading && !vm.Diff.IsPlaceholder && vm.Diff.Rows.Count > 0, "diff of " + path);
        WaitUntil(() => !vm.Diff.IsHighlighting, "highlighting", throwOnTimeout: false);

        // As if the splitter were dragged: the diff gets most of the width.
        window.BodyGrid.ColumnDefinitions[2].Width = new GridLength(Width - 520);
        UiSnapshot.Pump();
        Thread.Sleep(200);
        UiSnapshot.Pump();
    }

    // Headless windows have no frame: draw the dialog's title bar, border and
    // shadow over the dimmed main window, centered as CenterOwner would.
    private static void Composite(string backdropPath, string dialogPath, string title, string name)
    {
        using var backdrop = new Bitmap(backdropPath);
        using var dialog = new Bitmap(dialogPath);
        var w = dialog.PixelSize.Width;
        var h = dialog.PixelSize.Height + TitleBarHeight;
        var x = Math.Round((Width - w) / 2.0);
        var y = Math.Round((Height - h) / 2.0);
        using var target = new RenderTargetBitmap(new PixelSize(Width, Height));
        using (var dc = target.CreateDrawingContext())
        {
            dc.DrawImage(backdrop, new Rect(0, 0, Width, Height));
            dc.FillRectangle(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), new Rect(0, 0, Width, Height));
            for (var i = 14; i >= 1; i--)
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(9, 0, 0, 0)), null, new Rect(x - i, y - i + 6, w + 2 * i, h + 2 * i), i, i);
            }

            dc.FillRectangle(new SolidColorBrush(Color.Parse("#2B2D31")), new Rect(x, y, w, TitleBarHeight));
            var caption = new FormattedText(title, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), 13, new SolidColorBrush(Color.Parse("#DFE1E5")));
            dc.DrawText(caption, new Point(x + 14, y + Math.Round((TitleBarHeight - caption.Height) / 2)));
            var close = new FormattedText("✕", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), 12, new SolidColorBrush(Color.Parse("#9DA0A8")));
            dc.DrawText(close, new Point(x + w - 18 - close.Width, y + Math.Round((TitleBarHeight - close.Height) / 2)));
            dc.DrawImage(dialog, new Rect(x, y + TitleBarHeight, w, dialog.PixelSize.Height));
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#4A4D55")), 1), new Rect(x - 0.5, y - 0.5, w + 1, h + 1));
        }

        target.Save(Path.Combine(UiSnapshot.OutputDir, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private static bool PicturesSettled(MainWindow window)
        => window.GetVisualDescendants().OfType<MarkdownRenderer.PictureSlot>()
            .All(s => s.LoadState is null or "shown" or "no picture" || s.LoadState.StartsWith("failed", StringComparison.Ordinal));

    private static void WaitUntil(Func<bool> condition, string what, bool throwOnTimeout = true, int timeoutMs = 30_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                if (throwOnTimeout)
                {
                    throw new TimeoutException($"Timed out waiting for {what}.");
                }

                return;
            }

            Thread.Sleep(25);
            UiSnapshot.Pump();
        }

        UiSnapshot.Pump();
    }
}
