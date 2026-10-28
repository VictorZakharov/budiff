using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Budiff.App;
using Budiff.CodeAnalysis;
using LibGit2Sharp;
using Xunit;

namespace Budiff.Avalonia.Tests;

// Sorting the bird's-eye tree by a column header keeps the selected row
// selected and scrolls it to the middle of the view - on the real window.
public sealed class AnalysisSortTests
{
    // Picked with the mouse and sorted from the header, like the user does:
    // a file, a folder, and a file inside an expanded folder. Each lands well
    // inside the list after the sort, so it can be centred.
    [AvaloniaTheory]
    [InlineData("item50.cs")]
    [InlineData("pack50")]
    [InlineData("pack50/code.cs")]
    public void Sort_KeepsTheClickedRowSelectedAndCentred(string path)
    {
        var name = path.Split('/')[0];
        var root = BuildFolder("budiff-sort-" + path.Replace('/', '-'));
        var window = new MainWindow(root, gitRoot: true) { Width = 1400, Height = 820 };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");
        UiSnapshot.Pump();
        var tree = window.FindControl<TreeView>("AnalysisTree")!;

        // Biggest first (Lines, descending): the row is on screen; click it.
        vm.Analysis.SortCommand.Execute(AnalysisSortKey.Lines);
        UiSnapshot.Pump();
        var row = vm.Analysis.Roots.First(r => r.Name == name);
        if (path != name)
        {
            row.IsExpanded = true;
            UiSnapshot.Pump();
            UiSnapshot.Pump();
            row = row.Children.Single();
        }

        Assert.True(IsInView(tree, row));
        var at = global::Avalonia.VisualExtensions.TranslatePoint((Control)tree.TreeContainerFromItem(row)!, new global::Avalonia.Point(120, 12), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        UiSnapshot.Pump();
        Assert.Same(row, tree.SelectedItem);

        // Ascending from the Lines header puts it far below the fold.
        var header = window.GetVisualDescendants().OfType<Button>()
            .First(b => b.CommandParameter is AnalysisSortKey.Lines && b.IsEffectivelyVisible);
        var headerAt = global::Avalonia.VisualExtensions.TranslatePoint(header, new global::Avalonia.Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;
        window.MouseDown(headerAt, MouseButton.Left);
        window.MouseUp(headerAt, MouseButton.Left);
        UiSnapshot.Pump();
        UiSnapshot.Pump();

        Assert.True(vm.Analysis.SortAscending);
        Assert.True(vm.Analysis.Roots.ToList().FindIndex(r => r.Name == name) > 50);
        var selected = Assert.IsType<CodeAnalysisRow>(tree.SelectedItem);
        Assert.Equal(path, selected.RelativePath);
        Assert.Same(vm.Analysis.FindShown(path), selected);
        Assert.True(IsInView(tree, selected), "the selected row is scrolled out of view after the sort");
        var offCentre = OffCentre(tree, selected);
        Assert.True(Math.Abs(offCentre) <= 24, $"the selected row is {offCentre:0} px off the middle of the view");
    }

    // How far the row's own line sits from the middle of the viewport.
    private static double OffCentre(TreeView tree, CodeAnalysisRow row)
    {
        var item = (Control)tree.TreeContainerFromItem(row)!;
        var line = item.GetVisualDescendants().OfType<Control>().First(c => c.Name == "PART_LayoutRoot");
        var viewer = tree.GetVisualDescendants().OfType<ScrollViewer>().First();
        var at = global::Avalonia.VisualExtensions.TranslatePoint(line, new global::Avalonia.Point(0, line.Bounds.Height / 2), viewer)!.Value;
        return at.Y - viewer.Viewport.Height / 2;
    }

    // Realized, and inside the tree's scroll viewport.
    private static bool IsInView(TreeView tree, CodeAnalysisRow row)
    {
        if (tree.TreeContainerFromItem(row) is not Control item || !item.IsEffectivelyVisible)
        {
            return false;
        }

        var viewer = tree.GetVisualDescendants().OfType<ScrollViewer>().First();
        var at = global::Avalonia.VisualExtensions.TranslatePoint(item, new global::Avalonia.Point(0, 0), viewer);
        return at is { } p && p.Y >= 0 && p.Y + item.Bounds.Height <= viewer.Bounds.Height;
    }

    // Sixty files and sixty one-file folders of growing size at the root: far
    // more rows than fit.
    private static string BuildFolder(string name)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        if (Directory.Exists(root))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
        }

        Directory.CreateDirectory(root);
        Repository.Init(root);
        using var repo = new Repository(root);
        var files = new Dictionary<string, string>();
        for (var i = 0; i < 60; i++)
        {
            files[$"item{i:00}.cs"] = string.Concat(Enumerable.Repeat("var x = 1;\n", (i + 1) * 5));
            files[$"pack{i:00}/code.cs"] = string.Concat(Enumerable.Repeat("var y = 2;\n", (i + 1) * 5 + 2));
        }

        ReadmeFixture.Commit(repo, ReadmeFixture.Sig("Ada Lovelace", "ada@example.com", daysAgo: 5), "Files", files);
        return root;
    }

    private static void WaitUntil(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            Thread.Sleep(25);
            UiSnapshot.Pump();
        }
    }
}
