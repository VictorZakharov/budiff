using System;
using System.Linq;
using System.Threading;
using Avalonia.Headless.XUnit;
using Budiff.App;
using Xunit;
using AvaloniaVisualExtensions = global::Avalonia.VisualExtensions;

namespace Budiff.Avalonia.Tests;

// Regression captures for #39/#40: squeezed panes must degrade gracefully
// (shrink/trim/clip) instead of controls painting over each other. Renders the
// real MainWindow at deliberately tight sizes; the PNGs land in ui-snapshots
// for eyeballing alongside the layout-state assertions.
public sealed class NarrowLayoutTests
{
    [AvaloniaFact]
    public void NarrowPanes_RenderWithoutLayoutBlowups()
    {
        var root = ReadmeFixture.BuildFixtureRepo("budiff-narrow");

        // Narrow bird's-eye alone: the toolbar row is tight at this width.
        // (Drop the window minimum so the squeeze actually happens.)
        var window = new MainWindow(root, gitRoot: true);
        window.MinWidth = 0;
        window.Width = 560;
        window.Height = 480;
        window.Show();

        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");
        vm.Analysis.SearchText = "Renderer";
        UiSnapshot.Pump();
        UiSnapshot.Capture(window, "narrow-birdseye");

        // Squeezed split: both panes forced under their comfortable widths.
        window.Width = 900;
        UiSnapshot.Pump();
        var row = vm.Analysis.Roots
            .SelectMany(r => r.SelfAndDescendants())
            .FirstOrDefault(r => r.IsFile && r.Name == "Program.cs");
        Assert.NotNull(row);
        _ = vm.Analysis.OpenAsync(row!);
        WaitUntil(() => vm.IsDiffOpen && !vm.Diff.IsPlaceholder, "diff load");

        UiSnapshot.Pump();
        UiSnapshot.Capture(window, "narrow-diff");

        Assert.True(vm.ShowDiffPane);

        // Blame auto-hide: at 900 the diff (~475px) is at least half the window,
        // so blame shows; growing the window (diff column width stays fixed)
        // drops it under half - blame hides and the checkbox tracks it.
        Assert.True(vm.Diff.ShowBlame);
        Assert.False(vm.Diff.BlameCramped);
        window.Width = 1400;
        UiSnapshot.Pump();
        UiSnapshot.Pump();
        Assert.True(vm.Diff.BlameCramped);
        Assert.False(vm.Diff.IsBlameVisible);
        Assert.False(vm.Diff.ShowBlame);

        // The user can still override the auto-hide by re-checking the box.
        vm.Diff.ShowBlame = true;
        UiSnapshot.Pump();
        Assert.True(vm.Diff.IsBlameVisible);
        window.Close();
    }

    // Regression for #43 round 2: header and rows must stay column-aligned at
    // ANY pane width, including squeezed splits with a vertical scrollbar and
    // deep/long rows. Compares actual on-screen x-positions.
    [AvaloniaFact]
    public void HeaderAndRows_ColumnAligned_AcrossWidths()
    {
        var root = ReadmeFixture.BuildFixtureRepo("budiff-align");
        var window = new MainWindow(root, gitRoot: true);
        window.MinWidth = 0;
        window.Width = 1050;
        window.Height = 400;
        window.Show();

        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");
        foreach (var row in vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()))
        {
            row.IsExpanded = true;
        }

        UiSnapshot.Pump();

        // Open a diff so the left pane is squeezed (compact columns).
        var file = vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).First(r => r.IsFile && r.Name == "Program.cs");
        _ = vm.Analysis.OpenAsync(file);
        WaitUntil(() => vm.IsDiffOpen, "diff open");
        UiSnapshot.Pump();

        foreach (var width in new[] { 1050.0, 1150.0, 1300.0 })
        {
            window.Width = width;
            UiSnapshot.Pump();
            UiSnapshot.Pump();
            AssertColumnsAligned(window, $"window={width}");
        }

        window.Close();
    }

    private static void AssertColumnsAligned(MainWindow window, string context)
    {
        // The Lines header button and each row's Lines TextBlock (num class,
        // column 1) must share the same right edge on screen.
        var headerButtons = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<global::Avalonia.Controls.Button>()
            .Where(b => b.Classes.Contains("colHeader"))
            .ToList();
        Assert.True(headerButtons.Count > 1, "no header buttons found");
        var linesHeader = headerButtons[1];
        var headerRight = AvaloniaVisualExtensions
            .TranslatePoint(linesHeader, new global::Avalonia.Point(linesHeader.Bounds.Width, 0), window)!.Value.X;

        var rowCells = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<global::Avalonia.Controls.TextBlock>()
            .Where(t => t.Classes.Contains("num") && t.Text is { Length: > 0 })
            .Where(t => ((global::Avalonia.Controls.Grid)t.Parent!).Children.IndexOf(t) >= 0 && global::Avalonia.Controls.Grid.GetColumn(t) == 1)
            .ToList();
        Assert.True(rowCells.Count > 2, "no Lines row cells found");

        foreach (var cell in rowCells)
        {
            // Normalize to the COLUMN edge: the cell carries an 8px right margin
            // (num class); the header button stretches to the column edge.
            var cellColumnRight = AvaloniaVisualExtensions
                .TranslatePoint(cell, new global::Avalonia.Point(cell.Bounds.Width, 0), window)!.Value.X
                + cell.Margin.Right;
            Assert.True(Math.Abs(cellColumnRight - headerRight) <= 2,
                $"{context}: Lines column right {cellColumnRight} vs header right {headerRight} (row text '{cell.Text}')");
        }
    }

    private static void WaitUntil(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + 20_000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            Thread.Sleep(25);
            UiSnapshot.Pump();
        }

        UiSnapshot.Pump();
    }
}
