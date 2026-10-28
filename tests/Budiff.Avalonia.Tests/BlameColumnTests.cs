using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Xunit;

namespace Budiff.Avalonia.Tests;

// The blame column is as wide as the file's longest label, measured in the
// font the cells draw with, so "author age" always shows in full.
public sealed class BlameColumnTests
{
    [AvaloniaTheory]
    [InlineData("Victor Zakharov 17min")]
    [InlineData("Victor Zakharov 38min")]
    [InlineData("Ada Lovelace 3d")]
    public void BlameLabels_ShowInFull(string label)
    {
        var model = new DiffViewModel { Title = "a.cs", Extension = ".cs" };
        model.SetDiff(LineDiff.Compute("a\nb\n", "a\nc\n"));
        var entry = new BlameEntry(label, "abc1234", "Author", "a@example.com", "2026-10-27 12:00", "m");

        // Blame that arrived before the view (fitted again once it attaches)...
        model.SetBlame(new[] { entry, entry });
        var window = new Window { Width = 1200, Height = 300, Content = new DiffView { DataContext = model } };
        window.Show();
        UiSnapshot.Pump();
        AssertNotTrimmed(window);

        // ... and blame that arrives after.
        model.SetBlame(null);
        model.SetBlame(new[] { entry, entry });
        UiSnapshot.Pump();
        AssertNotTrimmed(window);
        window.Close();
    }

    private static void AssertNotTrimmed(Window window)
    {
        var cells = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Classes.Contains("blameCell") && t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)).ToList();
        Assert.NotEmpty(cells);
        Assert.All(cells, c => Assert.False(c.TextLayout.TextLines.Any(l => l.HasCollapsed), "trimmed: " + c.Text));
    }
}
