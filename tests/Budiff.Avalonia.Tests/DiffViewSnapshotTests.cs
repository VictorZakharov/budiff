using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Budiff;
using Xunit;

namespace Budiff.Avalonia.Tests;

public sealed class DiffViewSnapshotTests
{
    // #48: a wholly-added (new file) diff renders single-pane - only the right
    // side, full width; a mixed diff stays two-pane.
    [AvaloniaFact]
    public void DiffView_NewFile_RendersSinglePane()
    {
        var model = new DiffViewModel { Title = "New.cs", Extension = ".cs" };
        model.SetDiff(LineDiff.Compute(string.Empty, "public class New\n{\n    public int Value => 1;\n}\n"));
        Assert.True(model.IsSinglePane);
        // All change rows are Added; the missing old side leaves one blank
        // Unchanged row (empty line pairing), which single-pane tolerates.
        Assert.All(model.Rows.Where(r => r.Kind != DiffKind.Unchanged), r => Assert.Equal(DiffKind.Added, r.Kind));

        // New-side blame (#51): the single-pane blame cell must carry the
        // commit-details tooltip too (#54 - it used to exist only two-pane).
        var ada = new BlameEntry("Ada Lovelace 10d", "abc1234", "Ada Lovelace", "ada@example.com", "2026-06-22 10:00", "Add New.cs");
        model.SetBlame(new[] { ada, ada, ada, ada }, forNewSide: true);

        var window = new Window
        {
            Width = 920,
            Height = 300,
            Content = new DiffView { DataContext = model },
        };
        var snapshot = UiSnapshot.Capture(window, "diff-view-single");
        Assert.True(new FileInfo(snapshot.Path).Length > 1000);
        AssertBlameCellHasTooltip(window);

        // Mixed diff: back to two panes - tooltip present there as well.
        model.SetDiff(LineDiff.Compute("a\nb\n", "a\nc\n"));
        Assert.False(model.IsSinglePane);
        model.SetBlame(new[] { ada, ada });
        UiSnapshot.Pump();
        AssertBlameCellHasTooltip(window);
        window.Close();
    }

    private static void AssertBlameCellHasTooltip(Window window)
    {
        var cells = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<global::Avalonia.Controls.TextBlock>()
            .Where(t => t.Classes.Contains("blameCell") && t.IsEffectivelyVisible)
            .ToList();
        Assert.NotEmpty(cells);
        Assert.All(cells, cell => Assert.NotNull(ToolTip.GetTip(cell)));

        // Open it for real: the template content must bind the row's commit
        // details, not just exist.
        var cell = cells[0];
        ToolTip.SetIsOpen(cell, true);
        UiSnapshot.Pump();
        var tip = Assert.IsAssignableFrom<global::Avalonia.Controls.Control>(ToolTip.GetTip(cell));
        var texts = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(tip)
            .OfType<global::Avalonia.Controls.TextBlock>()
            .Select(t => t.Text)
            .ToList();
        Assert.Contains("abc1234", texts);
        Assert.Contains("Ada Lovelace", texts);
        Assert.Contains("ada@example.com", texts);
        ToolTip.SetIsOpen(cell, false);
        UiSnapshot.Pump();
    }

    // Regression for the Collapse-unchanged freeze (#45): the rows host is
    // virtualized, so a large diff must realize only the viewport's rows, and
    // toggling collapse must not re-realize one container per line.
    [AvaloniaFact]
    public void DiffView_VirtualizesLargeDiffs()
    {
        var oldText = new StringBuilder();
        var newText = new StringBuilder();
        for (var i = 0; i < 3000; i++)
        {
            oldText.AppendLine($"line {i} value = {i};");
            newText.AppendLine(i == 1500 ? $"line {i} value = {i + 1};" : $"line {i} value = {i};");
        }

        var model = new DiffViewModel { Title = "Big.cs", Extension = ".cs" };
        model.SetDiff(LineDiff.Compute(oldText.ToString(), newText.ToString()));
        model.CollapseUnchanged = false;

        var window = new Window
        {
            Width = 920,
            Height = 520,
            Content = new DiffView { DataContext = model },
        };
        window.Show();
        UiSnapshot.Pump();

        Assert.True(model.Rows.Count > 2900);
        var host = ((DiffView)window.Content!).FindControl<ItemsControl>("RowsHost")!;
        var realized = Enumerable.Range(0, model.Rows.Count).Count(i => host.ContainerFromIndex(i) is not null);
        Assert.InRange(realized, 1, 200);

        // The toggle that used to freeze: still only viewport-sized realization.
        model.CollapseUnchanged = true;
        UiSnapshot.Pump();
        Assert.True(model.Rows.Count < 100);

        // Scrolled capture: with LOGICAL scrolling the funnel gutter must
        // anchor its geometry on realized containers, not the host position.
        model.CollapseUnchanged = false;
        UiSnapshot.Pump();
        var scroller = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<ScrollViewer>().First();
        scroller.Offset = new global::Avalonia.Vector(0, 1490 * 18);
        UiSnapshot.Pump();
        UiSnapshot.Pump();
        UiSnapshot.Capture(window, "diff-view-scrolled");
        window.Close();
    }

    [AvaloniaFact]
    public void DiffView_RendersSideBySideRows()
    {
        var model = new DiffViewModel
        {
            Title = "Sample.cs",
            Subtitle = "HEAD vs working tree",
            Extension = ".cs",
        };
        model.SetDiff(LineDiff.Compute(
            "public class Sample\n{\n    public int Count => 1;\n}\n",
            "public class Sample\n{\n    public int Count => 2;\n    public string Name => \"budiff\";\n}\n"));

        Assert.Equal(6, model.Rows.Count);
        Assert.Contains(model.Rows, row => row.Kind == DiffKind.Modified);
        Assert.Contains(model.Rows, row => row.Kind == DiffKind.Added);

        // Blame visible in this capture: the funnel must sit between the code
        // halves (offset by the blame column), not at the row's center.
        var ada = new BlameEntry("Ada Lovelace 10d", "abc1234", "Ada Lovelace", "ada@example.com", "2026-06-22 10:00", "Scaffold the sample");
        model.SetBlame(new[] { ada, ada, ada, ada });

        var window = new Window
        {
            Width = 920,
            Height = 520,
            Content = new DiffView { DataContext = model },
        };

        var snapshot = UiSnapshot.Capture(window, "diff-view");

        Assert.Equal(920, snapshot.Width);
        Assert.Equal(520, snapshot.Height);
        Assert.True(new FileInfo(snapshot.Path).Length > 1000);
    }
}
