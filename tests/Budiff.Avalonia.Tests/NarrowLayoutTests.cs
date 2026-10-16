using System;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
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
        UiSnapshot.Pump();
        UiSnapshot.Capture(window, "narrow-diff");

        Assert.True(vm.ShowDiffPane);
        AssertDiffToolbarFits(window, expectOverflow: true);

        // The overflow menu drives the same settings.
        var overflow = window.FindControl<Button>("DiffOptionsOverflow")!;
        var menu = (MenuFlyout)overflow.Flyout!;
        menu.ShowAt(overflow);
        UiSnapshot.Pump();
        var wrap = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Wrap lines"));
        Assert.True(wrap.IsVisible);
        Assert.True(wrap.IsChecked);
        wrap.IsChecked = false;
        Assert.False(vm.Diff.WrapLines);
        Assert.False(menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Preview")).IsVisible);
        menu.Hide();
        vm.Diff.WrapLines = true;
        UiSnapshot.Pump();

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

    // Squeezing the docked bird's-eye drops optional columns in a fixed order -
    // Authors, Commits, Code, then Lines - leaving Name + Added + Removed; the
    // toolbar's filter box wraps to its own row when the pane gets that tight.
    [AvaloniaFact]
    public void BirdsEyeColumns_DropInOrder_AsThePaneNarrows()
    {
        var root = ReadmeFixture.BuildFixtureRepo("budiff-columns");
        var window = new MainWindow(root, gitRoot: true);
        window.MinWidth = 0;
        window.Width = 1400;
        window.Height = 420;
        window.Show();

        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");
        var filterRow = window.FindControl<global::Avalonia.Controls.Grid>("FilterRow")!;
        Assert.Equal(8, vm.FittingColumns);
        Assert.Equal(0, global::Avalonia.Controls.Grid.GetRow(filterRow));

        var file = vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).First(r => r.IsFile && r.Name == "Program.cs");
        _ = vm.Analysis.OpenAsync(file);
        WaitUntil(() => vm.IsDiffOpen && vm.ShowDiffPane, "diff open");

        // The docked diff keeps its width, so the window width sets the tree's.
        var seen = new System.Collections.Generic.List<int>();
        foreach (var width in new[] { 1400.0, 1220.0, 1150.0, 1080.0, 1000.0 })
        {
            window.Width = width;
            UiSnapshot.Pump();
            UiSnapshot.Pump();
            var context = $"window={width}";
            seen.Add(vm.FittingColumns);

            var headers = HeaderButtons(window).ToDictionary(b => global::Avalonia.Controls.Grid.GetColumn(b));
            Assert.Equal(vm.ShowLinesColumn, headers[1].IsEffectivelyVisible);
            Assert.Equal(vm.ShowCodeColumn, headers[2].IsEffectivelyVisible);
            Assert.Equal(vm.ShowCommitsColumn, headers[8].IsEffectivelyVisible);
            Assert.Equal(vm.ShowAuthorsColumn, headers[9].IsEffectivelyVisible);
            Assert.True(headers[6].IsEffectivelyVisible && headers[7].IsEffectivelyVisible, context);
            Assert.False(headers[3].IsEffectivelyVisible || headers[4].IsEffectivelyVisible || headers[5].IsEffectivelyVisible, context);

            // What is left fits: the last visible header ends inside the pane.
            var treeArea = window.FindControl<global::Avalonia.Controls.DockPanel>("TreeArea")!;
            var paneRight = AvaloniaVisualExtensions.TranslatePoint(treeArea, new global::Avalonia.Point(treeArea.Bounds.Width, 0), window)!.Value.X;
            var lastHeader = headers.Values.Where(b => b.IsEffectivelyVisible).OrderBy(b => global::Avalonia.Controls.Grid.GetColumn(b)).Last();
            var lastRight = AvaloniaVisualExtensions.TranslatePoint(lastHeader, new global::Avalonia.Point(lastHeader.Bounds.Width, 0), window)!.Value.X;
            Assert.True(lastRight <= paneRight + 0.5, $"{context}: last column ends at {lastRight}, pane at {paneRight}");

            AssertColumnsAligned(window, context, column: 6);
            if (vm.ShowLinesColumn)
            {
                AssertColumnsAligned(window, context, column: 1);
            }

            UiSnapshot.Capture(window, $"birdseye-columns-{vm.FittingColumns}");
        }

        Assert.Equal(new[] { 4, 3, 2, 1, 0 }, seen);
        Assert.Equal(1, global::Avalonia.Controls.Grid.GetRow(filterRow));

        // Wrapped, the filter box and Reset both sit inside the pane.
        var reset = window.FindControl<global::Avalonia.Controls.Button>("ResetButton")!;
        var birdsEye = window.FindControl<global::Avalonia.Controls.DockPanel>("BirdsEyePanel")!;
        var resetRight = AvaloniaVisualExtensions.TranslatePoint(reset, new global::Avalonia.Point(reset.Bounds.Width, 0), birdsEye)!.Value.X;
        Assert.True(resetRight <= birdsEye.Bounds.Width, $"Reset ends at {resetRight}, pane is {birdsEye.Bounds.Width}");
        window.Close();
    }

    // Keep order: Lines, Code, Commits, Authors, then (no docked diff only)
    // Blank, Comment, Files, Size - so a narrow window keeps the essentials.
    [AvaloniaTheory]
    [InlineData(true, 2000, 4)]
    [InlineData(true, 574, 4)]
    [InlineData(true, 573, 3)]
    [InlineData(true, 514, 3)]
    [InlineData(true, 513, 2)]
    [InlineData(true, 450, 2)]
    [InlineData(true, 449, 1)]
    [InlineData(true, 378, 1)]
    [InlineData(true, 377, 0)]
    [InlineData(true, 100, 0)]
    [InlineData(false, 858, 8)]
    [InlineData(false, 857, 7)]
    [InlineData(false, 638, 5)]
    [InlineData(false, 637, 4)]
    [InlineData(false, 377, 0)]
    public void FitColumns_DropsColumnsFromTheEndOfTheKeepOrder(bool docked, double rowWidth, int expected)
    {
        var vm = new MainWindowViewModel(System.IO.Directory.CreateTempSubdirectory("budiff-fit-").FullName, gitRoot: false) { IsDiffOpen = docked };
        vm.FitColumns(rowWidth);
        Assert.Equal(expected, vm.FittingColumns);
        Assert.False(docked && vm.ShowBlankColumn);
    }

    private static System.Collections.Generic.List<global::Avalonia.Controls.Button> HeaderButtons(MainWindow window)
        => global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<global::Avalonia.Controls.Button>()
            .Where(b => b.Classes.Contains("colHeader"))
            .ToList();

    private static void AssertColumnsAligned(MainWindow window, string context, int column = 1)
    {
        // The column's header button and each row's TextBlock (num class) in
        // that column must share the same right edge on screen.
        var header = HeaderButtons(window).Single(b => global::Avalonia.Controls.Grid.GetColumn(b) == column);
        var headerRight = AvaloniaVisualExtensions
            .TranslatePoint(header, new global::Avalonia.Point(header.Bounds.Width, 0), window)!.Value.X;

        var rowCells = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<global::Avalonia.Controls.TextBlock>()
            .Where(t => t.Classes.Contains("num") && t.Text is { Length: > 0 } && t.IsEffectivelyVisible)
            .Where(t => ((global::Avalonia.Controls.Grid)t.Parent!).Children.IndexOf(t) >= 0 && global::Avalonia.Controls.Grid.GetColumn(t) == column)
            .ToList();
        Assert.True(rowCells.Count > 2, $"{context}: no row cells found in column {column}");

        foreach (var cell in rowCells)
        {
            // Normalize to the COLUMN edge: the cell carries an 8px right margin
            // (num class); the header button stretches to the column edge.
            var cellColumnRight = AvaloniaVisualExtensions
                .TranslatePoint(cell, new global::Avalonia.Point(cell.Bounds.Width, 0), window)!.Value.X
                + cell.Margin.Right;
            Assert.True(Math.Abs(cellColumnRight - headerRight) <= 2,
                $"{context}: column {column} right {cellColumnRight} vs header right {headerRight} (row text '{cell.Text}')");
        }
    }

    // Docked diff toolbar: no toggle is ever half-cut - one that does not fit
    // is hidden and the overflow menu (holding every toggle) shows instead.
    private static void AssertDiffToolbarFits(MainWindow window, bool expectOverflow)
    {
        var grid = window.FindControl<Grid>("DiffToolbarGrid")!;
        var overflow = window.FindControl<Button>("DiffOptionsOverflow")!;
        var toggles = grid.Children.OfType<CheckBox>().Where(c => c.IsVisible).ToList();
        Assert.NotEmpty(toggles);
        Assert.All(toggles.Where(t => t.Opacity > 0), t => Assert.True(t.Bounds.Right <= grid.Bounds.Width + 0.5, $"{t.Content} is cut"));
        Assert.Equal(toggles.Any(t => t.Opacity == 0), overflow.IsVisible);
        Assert.Equal(expectOverflow, overflow.IsVisible);
        Assert.True(overflow.IsEffectivelyVisible == expectOverflow);
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
