using System.IO;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Budiff;
using Xunit;

namespace Budiff.Avalonia.Tests;

public sealed class DiffViewSnapshotTests
{
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
