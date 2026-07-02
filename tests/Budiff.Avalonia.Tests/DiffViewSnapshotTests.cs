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
