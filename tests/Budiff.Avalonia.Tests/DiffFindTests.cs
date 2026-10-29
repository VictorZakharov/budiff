using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Budiff;
using Xunit;

namespace Budiff.Avalonia.Tests;

// Ctrl+F finds in the diff: on the side the pointer is over when it is
// pressed (else the selection's, else the new side), every match marked,
// the current one selected and scrolled to - a collapsed gap opening for it.
public sealed class DiffFindTests
{
    [AvaloniaFact]
    public void CtrlF_SearchesTheSideUnderThePointer_AndStepsThroughMatches()
    {
        var oldText = new StringBuilder();
        var newText = new StringBuilder();
        for (var i = 0; i < 120; i++)
        {
            oldText.AppendLine(i % 30 == 0 ? $"    var legacy{i} = Load({i});" : $"    var value{i} = Step({i});");
            newText.AppendLine(i % 30 == 0 ? $"    var fresh{i} = Load({i});" : i == 60 ? "    var value60 = Step(60); // fresh tail" : $"    var value{i} = Step({i});");
        }

        var model = new DiffViewModel { Title = "Find.cs", Extension = ".cs" };
        model.SetDiff(LineDiff.Compute(oldText.ToString(), newText.ToString()));
        var view = new DiffView { DataContext = model };
        var window = new Window { Width = 1000, Height = 420, Content = view };
        window.Show();
        UiSnapshot.Pump();
        UiSnapshot.Pump();
        var host = view.FindControl<ItemsControl>("RowsHost")!;

        // Pointer over the OLD side's code, then Ctrl+F: the old side.
        var oldCode = WrapIndentTests.Code(host, 0, window, 0);
        window.MouseMove(oldCode.TranslatePoint(new Point(20, 5), window)!.Value);
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
        UiSnapshot.Pump();
        Assert.True(view.IsFindOpen);
        Assert.False(view.FindNewSide);

        var box = view.FindControl<TextBox>("FindBox")!;
        box.Text = "legacy";
        UiSnapshot.Pump();
        Assert.Equal(4, view.FindMatchCount);
        Assert.Equal("legacy", view.SelectedText);
        Assert.False(view.Selection!.Value.NewSide);
        Assert.Equal("1 of 4", view.FindControl<TextBlock>("FindCount")!.Text);

        view.FindNext(forward: true);
        view.FindNext(forward: true);
        Assert.Equal(2, view.FindCurrent);
        Assert.Equal(61, view.Selection!.Value.Start.Line);

        // A match in unchanged lines folded into a gap: going to it opens the gap.
        Assert.DoesNotContain(model.Rows, r => r.Source?.OldNo == 46);
        box.Text = "Step(45)";
        UiSnapshot.Pump();
        Assert.Equal(1, view.FindMatchCount);
        var row = model.Rows.ToList().FindIndex(r => r.Source?.OldNo == 46);
        Assert.True(row >= 0, "the match's row is shown");
        UiSnapshot.Pump();
        var text = (TextBlock)WrapIndentTests.Code(host, row, window, 0);
        Assert.NotNull(DiffInlines.GetMarks(text));
        UiSnapshot.Capture(window, "diff-find");

        // Typed again, the search goes on from where it was (line 46).
        box.Text = "legacy";
        UiSnapshot.Pump();
        Assert.Equal(2, view.FindCurrent);

        // Shift+Enter goes back; it wraps around past the first.
        box.Focus();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Shift);
        Assert.Equal(1, view.FindCurrent);
        view.FindNext(forward: false);
        view.FindNext(forward: false);
        Assert.Equal(3, view.FindCurrent);

        // Esc in the box closes the bar and clears the marks.
        box.Focus();
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.False(view.IsFindOpen);
        Assert.Null(DiffInlines.GetMarks(text));

        // Pointer over the new side: Ctrl+F searches it.
        var onScreen = model.Rows.ToList().FindIndex(r => r.Source?.OldNo == view.Selection!.Value.Start.Line);
        var newCode = WrapIndentTests.Code(host, onScreen, window, 1);
        window.MouseMove(newCode.TranslatePoint(new Point(20, 5), window)!.Value);
        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
        UiSnapshot.Pump();
        Assert.True(view.FindNewSide);
        box.Text = "fresh";
        UiSnapshot.Pump();
        Assert.Equal(4, view.FindMatchCount);
        Assert.True(view.Selection!.Value.NewSide);
        window.Close();
    }
}
