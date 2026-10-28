using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Budiff.App;
using Budiff.CodeAnalysis;
using LibGit2Sharp;
using Xunit;

namespace Budiff.Avalonia.Tests;

// Sorting the bird's-eye tree by a column header keeps the selected row
// selected and scrolls it into view - on the real window.
public sealed class AnalysisSortTests
{
    [AvaloniaFact]
    public void Sort_KeepsTheSelectedRowSelectedAndInView()
    {
        var root = BuildFolder("budiff-sort-selection");
        var window = new MainWindow(root, gitRoot: true) { Width = 1400, Height = 820 };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");
        UiSnapshot.Pump();
        var tree = window.FindControl<TreeView>("AnalysisTree")!;

        // Biggest file first (Lines, descending): the top row is selected.
        var top = vm.Analysis.Roots[0];
        Assert.Equal("item59.cs", top.Name);
        tree.SelectedItem = top;
        UiSnapshot.Pump();
        Assert.True(IsInView(tree, top));

        // Ascending puts it last, far below the fold.
        vm.Analysis.SortCommand.Execute(AnalysisSortKey.Lines);
        UiSnapshot.Pump();
        UiSnapshot.Pump();

        Assert.Equal("item59.cs", vm.Analysis.Roots[^1].Name);
        var selected = Assert.IsType<CodeAnalysisRow>(tree.SelectedItem);
        Assert.Equal("item59.cs", selected.RelativePath);
        Assert.Same(vm.Analysis.Roots[^1], selected);
        Assert.True(IsInView(tree, selected), "the selected row is scrolled out of view after the sort");
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

    // Sixty files of growing size at the root: far more rows than fit.
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
