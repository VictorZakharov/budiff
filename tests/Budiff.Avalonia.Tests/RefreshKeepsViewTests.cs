using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Budiff.App;
using Budiff.CodeAnalysis;
using Xunit;

namespace Budiff.Avalonia.Tests;

// The Refresh button reloads the bird's-eye and keeps what the reader had
// set up: the sort, the open and closed folders, the filters, the selected
// row, and what the diff pane shows. On the real window.
public sealed class RefreshKeepsViewTests
{
    [AvaloniaFact]
    public void Refresh_KeepsSortExpansionFiltersSelectionAndTheOpenFile()
    {
        var (window, vm, tree) = Open("budiff-refresh-file");
        var analysis = vm.Analysis;
        analysis.SortCommand.Execute(AnalysisSortKey.Name);
        UiSnapshot.Pump();

        // One folder opened all the way down, another one closed.
        var folders = analysis.Roots.Where(r => !r.IsFile).ToList();
        Assert.True(folders.Count >= 2);
        analysis.SetSubtreeExpanded(folders[0], expanded: true);
        analysis.SetSubtreeExpanded(folders[1], expanded: false);
        var picked = folders[0].SelfAndDescendants().Last(r => r.IsFile).RelativePath;
        var excluded = analysis.Extensions.FirstOrDefault(e => !picked.EndsWith(e.Extension, StringComparison.OrdinalIgnoreCase));
        if (excluded is not null)
        {
            excluded.Included = false;
        }

        UiSnapshot.Pump();
        var file = analysis.FindShown(picked)!;
        tree.SelectedItem = file;
        UiSnapshot.Pump();
        _ = analysis.OpenAsync(file);
        WaitUntil(() => vm.IsDiffOpen && !vm.Diff.IsLoading && !vm.Diff.IsPlaceholder, "the file's diff");
        var expanded = ExpandedPaths(vm);
        var title = vm.Diff.Title;

        vm.RefreshCommand.Execute(null);
        WaitUntil(() => !analysis.IsScanning && analysis.Roots.Count > 0 && !vm.Diff.IsLoading && !vm.Diff.IsPlaceholder, "the reload");
        Settle();

        Assert.Equal(AnalysisSortKey.Name, analysis.SortKey);
        Assert.True(analysis.SortAscending);
        Assert.Equal(expanded, ExpandedPaths(vm));
        if (excluded is not null)
        {
            Assert.False(analysis.Extensions.Single(e => e.Extension == excluded.Extension).Included);
        }

        Assert.Equal(file.RelativePath, (tree.SelectedItem as CodeAnalysisRow)?.RelativePath);
        Assert.True(vm.IsDiffOpen);
        Assert.Equal(title, vm.Diff.Title);
        window.Close();
    }

    // The tree stays on screen while it scans and only what changed changes:
    // every row is the same row (the tree keeps its containers and scroll),
    // an edited file's numbers update in place, a new file comes in, and the
    // selected row stays at the same height in the tree.
    [AvaloniaFact]
    public void Refresh_ChangesOnlyWhatChanged()
    {
        var (window, vm, tree) = Open("budiff-refresh-merge");
        var analysis = vm.Analysis;

        // By name, so the order holds; a short window, so the tree scrolls.
        analysis.SortCommand.Execute(AnalysisSortKey.Name);
        UiSnapshot.Pump();
        foreach (var folder in analysis.Roots.Where(r => !r.IsFile))
        {
            analysis.SetSubtreeExpanded(folder, expanded: true);
        }

        window.Height = 360;
        Settle();
        var rows = analysis.Roots.SelectMany(r => r.SelfAndDescendants()).ToList();
        var folderRow = rows.Last(r => !r.IsFile && r.Children.Count(c => c.IsFile && c.FullPath is not null) >= 1);
        var edited = folderRow.Children.Last(c => c.IsFile && c.FullPath is not null);
        tree.SelectedItem = edited;
        ((Control)tree.TreeContainerFromItem(edited)!).BringIntoView();
        Settle();
        var top = RowTop(tree, edited);
        Assert.NotNull(top);
        var roots = analysis.Roots;
        var emptied = false;
        roots.CollectionChanged += (_, _) => emptied |= roots.Count == 0;
        var lines = edited.LinesText;

        // An edit to the selected file, and a new file just above it.
        File.AppendAllText(edited.FullPath!, "\nmore\nlines\n");
        var added = Path.Combine(Path.GetDirectoryName(edited.FullPath!)!, "!added.txt");
        File.WriteAllText(added, "new\n");
        vm.RefreshCommand.Execute(null);
        Assert.True(analysis.Roots.Count > 0);
        WaitUntil(() => !analysis.IsScanning && edited.LinesText != lines, "the reload");
        Settle();

        Assert.Same(roots, analysis.Roots);
        Assert.False(emptied);
        Assert.All(rows, r => Assert.Contains(r, analysis.Roots.SelectMany(x => x.SelfAndDescendants())));
        Assert.Contains(folderRow.Children, r => r.Name == "!added.txt");
        Assert.Same(edited, tree.SelectedItem);
        Assert.Equal(top, RowTop(tree, edited));
        window.Close();
    }

    private static double? RowTop(TreeView tree, CodeAnalysisRow row)
    {
        var item = tree.TreeContainerFromItem(row) as Control;
        var viewer = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(tree).OfType<ScrollViewer>().First();
        return item is null ? null : global::Avalonia.VisualExtensions.TranslatePoint(item, default, viewer)?.Y;
    }

    [AvaloniaFact]
    public void Refresh_KeepsAFolderSummary()
    {
        var (window, vm, tree) = Open("budiff-refresh-folder");
        var analysis = vm.Analysis;
        var file = analysis.Roots.SelectMany(r => r.SelfAndDescendants()).First(r => r.IsFile);
        _ = analysis.OpenAsync(file);
        WaitUntil(() => vm.IsDiffOpen && !vm.Diff.IsLoading, "a file's diff");
        var folder = analysis.Roots.First(r => !r.IsFile);
        tree.SelectedItem = folder;
        WaitUntil(() => vm.IsFolderSummary, "the folder summary");

        vm.RefreshCommand.Execute(null);
        WaitUntil(() => !analysis.IsScanning && analysis.Roots.Count > 0, "the reload");
        Settle();

        Assert.True(vm.IsDiffOpen);
        Assert.True(vm.IsFolderSummary);
        Assert.Equal(folder.RelativePath, (tree.SelectedItem as CodeAnalysisRow)?.RelativePath);
        window.Close();
    }

    private static (MainWindow Window, MainWindowViewModel Vm, TreeView Tree) Open(string name)
    {
        var root = ReadmeFixture.BuildFixtureRepo(name);
        var window = new MainWindow(root, gitRoot: true) { Width = 1400, Height = 820 };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");
        UiSnapshot.Pump();
        return (window, vm, window.FindControl<TreeView>("AnalysisTree")!);
    }

    private static string[] ExpandedPaths(MainWindowViewModel vm)
        => vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants())
            .Where(r => !r.IsFile && r.IsExpanded)
            .Select(r => r.RelativePath)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

    private static void Settle()
    {
        for (var i = 0; i < 10; i++)
        {
            Thread.Sleep(20);
            UiSnapshot.Pump();
        }
    }

    private static void WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
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
