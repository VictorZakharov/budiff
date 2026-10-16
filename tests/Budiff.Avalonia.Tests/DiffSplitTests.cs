using System;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Budiff;
using Xunit;

namespace Budiff.Avalonia.Tests;

// The two-pane split is resizable: dragging the funnel gutter moves the
// divider (rows and funnel together), double-click re-centers it, and the
// horizontal scroll range follows the narrower pane.
public sealed class DiffSplitTests
{
    [Fact]
    public void SplitRatio_ClampsToAUsableRange()
    {
        var model = new DiffViewModel();
        Assert.Equal(0.5, model.SplitRatio);

        model.SplitRatio = 0.01;
        Assert.Equal(DiffViewModel.MinSplitRatio, model.SplitRatio);
        model.SplitRatio = 2;
        Assert.Equal(DiffViewModel.MaxSplitRatio, model.SplitRatio);
        model.SplitRatio = double.NaN;
        Assert.Equal(0.5, model.SplitRatio);

        // The split is a view preference: a new file keeps it.
        model.SplitRatio = 0.3;
        model.SetDiff(LineDiff.Compute("a\n", "b\n"));
        Assert.Equal(0.3, model.SplitRatio);
    }

    [Fact]
    public void MaxScroll_FollowsTheNarrowerPanesTextWidth()
    {
        var model = new DiffViewModel { WrapLines = false };
        var longLine = new string('x', 200);
        model.SetDiff(LineDiff.Compute("a\n" + longLine + "\n", "b\n" + longLine + "\n"));

        // Default assumption: ~80 visible columns.
        Assert.Equal((200 - 80) * 7.6, model.MaxScroll, 3);

        model.VisibleTextWidth = 300;
        Assert.Equal((200 * 7.6) - 300, model.MaxScroll, 3);

        // Widening the pane shrinks the range and pulls the offset back in.
        model.HorizontalScroll = model.MaxScroll;
        model.VisibleTextWidth = 1400;
        Assert.Equal((200 * 7.6) - 1400, model.MaxScroll, 3);
        Assert.Equal(model.MaxScroll, model.HorizontalScroll, 3);

        model.VisibleTextWidth = 2000;
        Assert.False(model.HasHorizontalScroll);
        Assert.Equal(0, model.HorizontalScroll);
    }

    [AvaloniaFact]
    public void DraggingTheGutter_ResizesBothPanes_AndDoubleClickRecenters()
    {
        var (window, view, model) = Open();
        var gutter = view.GetVisualDescendants().OfType<DiffFunnelGutter>().Single();
        var host = view.FindControl<ItemsControl>("RowsHost")!;
        var contentRow = model.Rows.ToList().FindIndex(r => !r.IsGap);
        var y = RowCenterY(host, contentRow, window);

        var codeWidth = gutter.Bounds.Width - model.BlameGutterWidth - DiffFunnelGutter.GutterWidth;
        var gutterX = GutterCenterX(gutter, model, window);
        AssertDividerAt(view, model, codeWidth);

        // Grab the gutter and pull it 120px left.
        window.MouseMove(new Point(gutterX, y));
        window.MouseDown(new Point(gutterX, y), MouseButton.Left);
        window.MouseMove(new Point(gutterX - 60, y));
        window.MouseMove(new Point(gutterX - 120, y));
        window.MouseUp(new Point(gutterX - 120, y), MouseButton.Left);
        UiSnapshot.Pump();

        Assert.Equal(0.5 - (120 / codeWidth), model.SplitRatio, 0.002);
        AssertDividerAt(view, model, codeWidth);
        UiSnapshot.Capture(window, "diff-view-split");

        // Dragging far past the edge stops at the minimum, not off-screen.
        gutterX = GutterCenterX(gutter, model, window);
        window.MouseDown(new Point(gutterX, y), MouseButton.Left);
        window.MouseMove(new Point(5, y));
        window.MouseUp(new Point(5, y), MouseButton.Left);
        UiSnapshot.Pump();
        Assert.Equal(DiffViewModel.MinSplitRatio, model.SplitRatio, 3);

        // Double-click re-centers.
        gutterX = GutterCenterX(gutter, model, window);
        window.MouseDown(new Point(gutterX, y), MouseButton.Left);
        window.MouseUp(new Point(gutterX, y), MouseButton.Left);
        window.MouseDown(new Point(gutterX, y), MouseButton.Left);
        window.MouseUp(new Point(gutterX, y), MouseButton.Left);
        UiSnapshot.Pump();
        Assert.Equal(0.5, model.SplitRatio, 3);
        window.Close();
    }

    [AvaloniaFact]
    public void TheGutter_GrabsOnlyItsOwnBand_AndLeavesGapRowsClickable()
    {
        var (window, view, model) = Open();
        var gutter = view.GetVisualDescendants().OfType<DiffFunnelGutter>().Single();
        var host = view.FindControl<ItemsControl>("RowsHost")!;
        var rows = model.Rows.ToList();
        var contentY = RowCenterY(host, rows.FindIndex(r => !r.IsGap), window);
        var gapY = RowCenterY(host, rows.FindIndex(r => r.IsGap), window);
        var gutterX = GutterCenterX(gutter, model, window);

        Assert.Same(gutter, window.InputHitTest(new Point(gutterX, contentY)));
        Assert.NotSame(gutter, window.InputHitTest(new Point(gutterX - 40, contentY)));
        Assert.NotSame(gutter, window.InputHitTest(new Point(gutterX, gapY)));

        // The gap bar still expands when clicked right where the gutter runs.
        var before = model.Rows.Count;
        window.MouseDown(new Point(gutterX, gapY), MouseButton.Left);
        window.MouseUp(new Point(gutterX, gapY), MouseButton.Left);
        UiSnapshot.Pump();
        Assert.True(model.Rows.Count > before);
        Assert.Equal(0.5, model.SplitRatio);
        window.Close();
    }

    [AvaloniaFact]
    public void ASqueezedPane_StillScrollsToTheEndOfItsLongestLine()
    {
        var (window, view, model) = Open();
        var wide = model.MaxScroll;

        model.SplitRatio = 0.2;
        UiSnapshot.Pump();

        var gutter = view.GetVisualDescendants().OfType<DiffFunnelGutter>().Single();
        var codeWidth = gutter.Bounds.Width - model.BlameGutterWidth - DiffFunnelGutter.GutterWidth;
        Assert.Equal((codeWidth * 0.2) - 46 - 6, model.VisibleTextWidth, 1.0);
        Assert.True(model.MaxScroll > wide);
        window.Close();
    }

    // 60 lines with edits at 5 and 50: collapse leaves context, then a gap
    // row, then context again; line 50 is long enough to need scrolling.
    private static (Window Window, DiffView View, DiffViewModel Model) Open()
    {
        var oldText = new StringBuilder();
        var newText = new StringBuilder();
        for (var i = 0; i < 60; i++)
        {
            oldText.AppendLine($"    var value{i} = Compute({i});");
            newText.AppendLine(i switch
            {
                5 => $"    var value{i} = Compute({i} + 1);",
                50 => $"    var value{i} = Compute({i}) + " + string.Join(" + ", Enumerable.Range(0, 30).Select(n => $"Extra({n})")) + ";",
                _ => $"    var value{i} = Compute({i});",
            });
        }

        var model = new DiffViewModel { Title = "Split.cs", Extension = ".cs", WrapLines = false };
        model.SetDiff(LineDiff.Compute(oldText.ToString(), newText.ToString()));
        Assert.Contains(model.Rows, r => r.IsGap);

        var view = new DiffView { DataContext = model };
        var window = new Window { Width = 1100, Height = 520, Content = view };
        window.Show();
        UiSnapshot.Pump();
        UiSnapshot.Pump();
        return (window, view, model);
    }

    private static double RowCenterY(ItemsControl host, int index, Window window)
    {
        var container = host.ContainerFromIndex(index) ?? throw new InvalidOperationException($"row {index} not realized");
        return container.TranslatePoint(new Point(0, 9), window)!.Value.Y;
    }

    private static double GutterCenterX(DiffFunnelGutter gutter, DiffViewModel model, Window window)
    {
        var codeWidth = gutter.Bounds.Width - model.BlameGutterWidth - DiffFunnelGutter.GutterWidth;
        var left = model.BlameGutterWidth + (model.SplitRatio * codeWidth);
        return gutter.TranslatePoint(new Point(left + (DiffFunnelGutter.GutterWidth / 2), 0), window)!.Value.X;
    }

    // Every realized two-pane row puts its divider exactly where the funnel
    // gutter draws.
    private static void AssertDividerAt(DiffView view, DiffViewModel model, double codeWidth)
    {
        var grids = view.GetVisualDescendants().OfType<DiffSplitGrid>().Where(g => g.IsEffectivelyVisible).ToList();
        Assert.NotEmpty(grids);
        foreach (var grid in grids)
        {
            Assert.Equal(model.SplitRatio * codeWidth, grid.ColumnDefinitions[1].ActualWidth, 1.0);
            Assert.Equal((1 - model.SplitRatio) * codeWidth, grid.ColumnDefinitions[3].ActualWidth, 1.0);
        }
    }
}
