using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Budiff;
using Xunit;

namespace Budiff.Avalonia.Tests;

// The diff's code is selectable: a drag selects one side's text from line to
// line (the other side never joins in, even when the pointer crosses it) and
// Ctrl+C copies it, the leading indent included.
public sealed class DiffSelectionTests
{
    private const string Old = "alpha one\n    beta two\n    gamma three\ndelta four\n";
    private const string New = "alpha one\n    beta TWO\n    gamma three\ndelta 4\n";

    [AvaloniaFact]
    public void Drag_SelectsOneSide_AndCtrlC_CopiesIt()
    {
        var (window, view, model) = Open();
        var host = view.FindControl<ItemsControl>("RowsHost")!;

        // From "beta" on the old side down to the middle of line 4, ending
        // over the NEW side: still the old side's text.
        var start = CharPoint(host, 1, window, side: 0, index: "    ".Length);
        var endOld = CharPoint(host, 3, window, side: 0, index: "delta".Length);
        var endOverNew = new Point(CharPoint(host, 3, window, side: 1, index: 2).X, endOld.Y);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(endOld);
        window.MouseMove(endOverNew);
        window.MouseMove(endOld);
        window.MouseUp(endOld, MouseButton.Left);
        UiSnapshot.Pump();

        Assert.Equal("beta two\n    gamma three\ndelta", view.SelectedText);
        Assert.False(view.Selection!.Value.NewSide);

        // Shown on the old side's lines only.
        var oldLine = (SelectableTextBlock)WrapIndentTests.Code(host, 2, window, 0);
        var newLine = (SelectableTextBlock)WrapIndentTests.Code(host, 2, window, 1);
        Assert.Equal("gamma three".Length, oldLine.SelectionEnd - oldLine.SelectionStart);
        Assert.Equal(0, newLine.SelectionEnd - newLine.SelectionStart);
        UiSnapshot.Capture(window, "diff-selection");

        window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
        UiSnapshot.Pump();
        var copied = window.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult();
        Assert.Equal("beta two\n    gamma three\ndelta", copied);

        // The new side on its own: a whole line, indent included.
        // (Pressed in the left half of the indent - line start - measured,
        // whatever the font's widths.)
        var lineStart = CharPoint(host, 2, window, side: 1, index: 0);
        var cell = (CodeClip)newLine.Parent!;
        Assert.True(cell.IndentWidth > 4);
        var cellLeft = cell.TranslatePoint(default, window)!.Value.X;
        window.MouseDown(new Point(cellLeft + (cell.IndentWidth / 4), lineStart.Y), MouseButton.Left);
        window.MouseMove(new Point(lineStart.X + 2000, lineStart.Y));
        window.MouseUp(new Point(lineStart.X + 2000, lineStart.Y), MouseButton.Left);
        Assert.True(view.Selection!.Value.NewSide);
        Assert.Equal("    gamma three", view.SelectedText);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.Null(view.Selection);
        Assert.Equal(0, newLine.SelectionEnd - newLine.SelectionStart);
        window.Close();
    }

    [AvaloniaFact]
    public void DoubleClick_SelectsAWord_AndANewDiffClearsTheSelection()
    {
        var (window, view, model) = Open();
        var host = view.FindControl<ItemsControl>("RowsHost")!;
        var word = CharPoint(host, 3, window, side: 1, index: 2);
        window.MouseDown(word, MouseButton.Left);
        window.MouseUp(word, MouseButton.Left);
        window.MouseDown(word, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(word, MouseButton.Left);
        Assert.Equal("delta", view.SelectedText);

        Assert.Equal((5, 10), DiffView.WordAt("for (value) x", 6));
        Assert.Equal((0, 3), DiffView.WordAt("for (value) x", 1));

        model.SetDiff(LineDiff.Compute("x\n", "y\n"));
        UiSnapshot.Pump();
        Assert.Null(view.Selection);
        window.Close();
    }

    private static (Window Window, DiffView View, DiffViewModel Model) Open()
    {
        var model = new DiffViewModel { Title = "Sel.txt", Extension = ".txt", CollapseUnchanged = false };
        model.SetDiff(LineDiff.Compute(Old, New));
        var view = new DiffView { DataContext = model };
        var window = new Window { Width = 900, Height = 300, Content = view };
        window.Show();
        UiSnapshot.Pump();
        UiSnapshot.Pump();
        return (window, view, model);
    }

    // A point inside the given character of a row's old (0) or new (1) code
    // (index in the line as shown - after the indent).
    private static Point CharPoint(ItemsControl host, int row, Window window, int side, int index)
    {
        var text = WrapIndentTests.Code(host, row, window, side);
        var indent = DiffInlines.GetIndent(text).Length;
        var rect = text.TextLayout.HitTestTextPosition(System.Math.Max(0, index - indent));
        return text.TranslatePoint(new Point(text.Padding.Left + rect.X + Math.Min(2, rect.Width / 4), rect.Y + (rect.Height / 2)), window)!.Value;
    }
}
