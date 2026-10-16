using System;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Budiff;
using Xunit;

namespace Budiff.Avalonia.Tests;

// Wrap lines (on by default): long lines wrap inside their pane and the row
// grows, instead of scrolling sideways; the funnel and the split handle follow
// the taller rows.
public sealed class DiffWrapTests
{
    [Fact]
    public void WrapLines_IsOnByDefault_AndRemovesTheHorizontalScrollRange()
    {
        var model = new DiffViewModel();
        Assert.True(model.WrapLines);

        var longLine = new string('x', 300);
        model.SetDiff(LineDiff.Compute("a\n" + longLine + "\n", "b\n" + longLine + "\n"));
        Assert.Equal(0, model.MaxScroll);
        Assert.False(model.HasHorizontalScroll);

        model.WrapLines = false;
        Assert.True(model.MaxScroll > 0);

        model.HorizontalScroll = model.MaxScroll;
        model.WrapLines = true;
        Assert.Equal(0, model.MaxScroll);
        Assert.Equal(0, model.HorizontalScroll);
    }

    [AvaloniaFact]
    public void WrappedRows_GrowTaller_AndTheGutterTracksThem()
    {
        var (window, view, model) = Open();
        var host = view.FindControl<ItemsControl>("RowsHost")!;
        var rows = model.Rows.ToList();
        var longIndex = rows.FindIndex(r => r.NewText?.Contains("Extra(29)", StringComparison.Ordinal) == true);
        Assert.True(longIndex > 0);

        var tall = host.ContainerFromIndex(longIndex)!;
        Assert.True(tall.Bounds.Height >= 3 * 18, $"wrapped row is {tall.Bounds.Height}px");
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<ScrollBar>(), b => b.Orientation == global::Avalonia.Layout.Orientation.Horizontal && b.IsEffectivelyVisible);
        UiSnapshot.Capture(window, "diff-view-wrapped");

        // Rows past the tall one: the split handle grabs the content row and
        // leaves the gap bar alone - positions come from the real layout.
        var gutter = view.GetVisualDescendants().OfType<DiffFunnelGutter>().Single();
        var gutterX = gutter.TranslatePoint(new Point(model.BlameGutterWidth + (0.5 * (gutter.Bounds.Width - model.BlameGutterWidth - DiffFunnelGutter.GutterWidth)) + 9, 0), window)!.Value.X;
        var after = rows.FindIndex(longIndex + 1, r => !r.IsGap);
        var gap = rows.FindIndex(longIndex + 1, r => r.IsGap);
        Assert.Same(gutter, window.InputHitTest(new Point(gutterX, CenterY(host, after, window))));
        Assert.NotSame(gutter, window.InputHitTest(new Point(gutterX, CenterY(host, gap, window))));

        // Off: every row is one line again, and the long one scrolls.
        model.WrapLines = false;
        UiSnapshot.Pump();
        UiSnapshot.Pump();
        Assert.Equal(18, host.ContainerFromIndex(longIndex)!.Bounds.Height, 0.5);
        Assert.True(model.HasHorizontalScroll);
        window.Close();
    }

    [AvaloniaFact]
    public void SinglePaneFileView_WrapsToo()
    {
        var model = new DiffViewModel { Title = "notes.md", Extension = ".md" };
        var text = "short\n" + string.Join(" ", Enumerable.Repeat("a long sentence that keeps going", 20)) + "\nend\n";
        model.SetDiff(LineDiff.Compute(text, text));
        Assert.True(model.IsSinglePane);

        var view = new DiffView { DataContext = model };
        var window = new Window { Width = 700, Height = 400, Content = view };
        window.Show();
        UiSnapshot.Pump();
        UiSnapshot.Pump();

        var host = view.FindControl<ItemsControl>("RowsHost")!;
        Assert.True(host.ContainerFromIndex(1)!.Bounds.Height > 2 * 18);
        Assert.Equal(18, host.ContainerFromIndex(0)!.Bounds.Height, 0.5);
        window.Close();
    }

    // 60 lines, edits at 5 and 50 (line 50 is very long), collapsed with gaps.
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

        var model = new DiffViewModel { Title = "Wrap.cs", Extension = ".cs" };
        model.SetDiff(LineDiff.Compute(oldText.ToString(), newText.ToString()));
        var view = new DiffView { DataContext = model };
        var window = new Window { Width = 1000, Height = 560, Content = view };
        window.Show();
        UiSnapshot.Pump();
        UiSnapshot.Pump();
        return (window, view, model);
    }

    private static double CenterY(ItemsControl host, int index, Window window)
    {
        var container = host.ContainerFromIndex(index) ?? throw new InvalidOperationException($"row {index} not realized");
        return container.TranslatePoint(new Point(0, container.Bounds.Height / 2), window)!.Value.Y;
    }
}
