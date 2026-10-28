using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Budiff.App;
using LibGit2Sharp;
using Xunit;

namespace Budiff.Avalonia.Tests;

// With the diff pane open, walking the bird's-eye tree with the keys shows
// what is selected - a file's diff, a folder's summary - without Enter or a
// double-click; with the pane closed the keys only move the selection. On
// the real window.
public sealed class DiffFollowsSelectionTests
{
    // Root order (Lines, descending): lib, then f01 ... f08.
    private const int RootFiles = 8;

    [AvaloniaFact]
    public void PaneOpen_DownArrow_LoadsTheNextFile()
    {
        var (window, tree) = OpenOn("budiff-follow-down", "f01.cs");
        var vm = window.ViewModel;

        Press(window, Key.Down, PhysicalKey.ArrowDown);

        WaitUntil(() => ShowsFile(vm, "f02.cs"), "f02.cs's diff");
        Assert.Equal("f02.cs", ((CodeAnalysisRow)tree.SelectedItem!).Name);
        Assert.True(vm.IsDiffOpen);
        Assert.False(vm.IsFolderSummary);
        UiSnapshot.Pump();
        Assert.False(window.FindControl<ScrollViewer>("FolderSummaryPanel")!.IsEffectivelyVisible);
        Assert.True(window.GetVisualDescendants().OfType<DiffView>().Single().IsEffectivelyVisible);
        AssertFocusInTree(window, tree);

        // And back up.
        Press(window, Key.Up, PhysicalKey.ArrowUp);
        WaitUntil(() => ShowsFile(vm, "f01.cs"), "f01.cs's diff");
        window.Close();
    }

    [AvaloniaFact]
    public void PaneOpen_FolderSelected_ShowsItsSummary()
    {
        var (window, tree) = OpenOn("budiff-follow-folder", "f01.cs");
        var vm = window.ViewModel;

        // Up from the first file: the lib folder above it.
        Press(window, Key.Up, PhysicalKey.ArrowUp);
        WaitUntil(() => vm.IsFolderSummary, "the folder summary");

        var summary = vm.FolderSummary!;
        Assert.Equal("lib", summary.Path);
        Assert.Equal("Last month", summary.Period);
        Assert.Equal(3, summary.Files);
        Assert.Equal(3, summary.Changed);
        Assert.Equal(1, summary.New);
        Assert.Equal(1, summary.Modified);
        Assert.Equal(1, summary.Deleted);
        Assert.Equal(31, summary.LinesAdded);
        Assert.Equal(51, summary.LinesRemoved);
        Assert.Equal(
            new[] { ("gone.cs", "D"), ("new.cs", "N"), ("edit.cs", "M") },
            summary.ChangedFiles.Select(f => (f.Path, f.Letter)).ToArray());

        // On screen: the panel in place of the diff, no line-diff toggles.
        UiSnapshot.Pump();
        var panel = window.FindControl<ScrollViewer>("FolderSummaryPanel")!;
        Assert.True(panel.IsEffectivelyVisible);
        Assert.False(window.GetVisualDescendants().OfType<DiffView>().Single().IsEffectivelyVisible);
        Assert.Equal("lib", vm.Diff.Title);
        Assert.False(vm.ShowCompareOptions);
        Assert.False(vm.ShowWrapToggle);
        var texts = panel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("3 files changed", texts);
        Assert.Contains("1 deleted", texts);
        Assert.Contains("gone.cs", texts);
        AssertFocusInTree(window, tree);
        UiSnapshot.Capture(window, "diff-follows-folder-summary");

        // Down again: back to a file diff.
        Press(window, Key.Down, PhysicalKey.ArrowDown);
        WaitUntil(() => ShowsFile(vm, "f01.cs"), "f01.cs's diff", 20, () => $"sel={((CodeAnalysisRow?)tree.SelectedItem)?.Name} title={vm.Diff.Title} loading={vm.Diff.IsLoading} ph={vm.Diff.IsPlaceholder} '{vm.Diff.Placeholder}' rows={vm.Diff.Rows.Count} summary={vm.IsFolderSummary}");
        Assert.False(vm.IsFolderSummary);
        UiSnapshot.Pump();
        Assert.True(window.GetVisualDescendants().OfType<DiffView>().Single().IsEffectivelyVisible);
        Assert.False(window.FindControl<ScrollViewer>("FolderSummaryPanel")!.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void PaneClosed_ArrowsOnlyMoveTheSelection_EnterOpens()
    {
        var root = BuildRepo("budiff-follow-closed");
        var window = new MainWindow(root, gitRoot: true) { Width = 1400, Height = 820 };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");
        UiSnapshot.Pump();
        var tree = window.FindControl<TreeView>("AnalysisTree")!;
        Select(tree, Row(vm, "f01.cs"));

        for (var i = 0; i < 3; i++)
        {
            Press(window, Key.Down, PhysicalKey.ArrowDown);
        }

        Press(window, Key.Up, PhysicalKey.ArrowUp);
        Settle(400);
        Assert.Equal("f03.cs", ((CodeAnalysisRow)tree.SelectedItem!).Name);
        Assert.False(vm.IsDiffOpen);
        Assert.False(vm.IsFolderSummary);

        // Enter opens the selected file, like a double-click.
        Press(window, Key.Enter, PhysicalKey.Enter);
        WaitUntil(() => ShowsFile(vm, "f03.cs"), "f03.cs's diff");
        Assert.True(vm.IsDiffOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void PaneOpen_KeyBurst_EndsOnTheLastFile()
    {
        var (window, tree) = OpenOn("budiff-follow-burst", "f01.cs");
        var vm = window.ViewModel;

        // A pause long enough that no runner is slow enough to split the burst.
        window.FollowDelayMs = 1500;
        var titles = new List<string>();
        vm.Diff.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.Diff.Title))
            {
                titles.Add(vm.Diff.Title);
            }
        };

        // A held arrow key: one press after another, no pause between.
        for (var i = 0; i < RootFiles - 2; i++)
        {
            Press(window, Key.Down, PhysicalKey.ArrowDown);
        }

        Assert.Equal("f07.cs", ((CodeAnalysisRow)tree.SelectedItem!).Name);
        WaitUntil(() => ShowsFile(vm, "f07.cs"), "f07.cs's diff");

        // Nothing stale lands afterwards.
        Settle(500);
        Assert.True(ShowsFile(vm, "f07.cs"), $"ended on {vm.Diff.Title}");
        AssertFocusInTree(window, tree);

        // Only the burst's first row and its last were loaded, not each one passed.
        Assert.Equal(new[] { "f02.cs", "f07.cs" }, titles.ToArray());
        window.Close();
    }

    // A window on the fixture with the pane open on that root file, its row
    // selected and focused.
    private static (MainWindow Window, TreeView Tree) OpenOn(string name, string file)
    {
        var root = BuildRepo(name);
        var window = new MainWindow(root, gitRoot: true) { Width = 1400, Height = 820 };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");
        UiSnapshot.Pump();
        Assert.Equal(new[] { "lib" }.Concat(Enumerable.Range(1, RootFiles).Select(i => $"f{i:00}.cs")), vm.Analysis.Roots.Select(r => r.Name));

        var tree = window.FindControl<TreeView>("AnalysisTree")!;
        var row = Row(vm, file);
        Select(tree, row);
        _ = vm.Analysis.OpenAsync(row);
        WaitUntil(() => ShowsFile(vm, file), file + "'s diff");
        Settle(200);
        return (window, tree);
    }

    private static CodeAnalysisRow Row(MainWindowViewModel vm, string name)
        => vm.Analysis.Roots.First(r => r.Name == name);

    private static void Select(TreeView tree, CodeAnalysisRow row)
    {
        tree.SelectedItem = row;
        UiSnapshot.Pump();
        ((Control)tree.TreeContainerFromItem(row)!).Focus(NavigationMethod.Tab);
        UiSnapshot.Pump();
    }

    // The diff of that root file has landed (its marker line is in the rows).
    private static bool ShowsFile(MainWindowViewModel vm, string file)
        => vm.Diff.Title == file && !vm.Diff.IsLoading && !vm.IsFolderSummary
           && vm.Diff.Rows.Any(r => r.Source?.NewText?.Contains("marker " + file, StringComparison.Ordinal) == true);

    private static void AssertFocusInTree(MainWindow window, TreeView tree)
    {
        var focused = window.FocusManager?.GetFocusedElement() as Visual;
        Assert.True(focused is not null && (focused == tree || tree.IsVisualAncestorOf(focused)), $"focus is on {focused?.GetType().Name ?? "nothing"}, not in the tree");
    }

    private static void Press(MainWindow window, Key key, PhysicalKey physical)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        window.KeyRelease(key, RawInputModifiers.None, physical, null);
        Dispatcher.UIThread.RunJobs();
    }

    // Lets timers and loads run for a while.
    private static void Settle(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Thread.Sleep(20);
            UiSnapshot.Pump();
        }
    }

    // Month-old baseline, then (5 days ago, inside "Last month") every root
    // file gains a marker line and lib/ gets one file edited, one added and
    // one deleted. lib/ is the biggest, so it heads the tree.
    private static string BuildRepo(string name)
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
        var files = new Dictionary<string, string>
        {
            ["lib/keep.cs"] = Lines("keep", 200),
            ["lib/edit.cs"] = Lines("edit", 100),
            ["lib/gone.cs"] = Lines("gone", 50),
        };
        for (var i = 1; i <= RootFiles; i++)
        {
            files[$"f{i:00}.cs"] = Lines($"f{i:00}", (RootFiles + 2 - i) * 5);
        }

        ReadmeFixture.Commit(repo, ReadmeFixture.Sig("Ada Lovelace", "ada@example.com", daysAgo: 60), "Baseline", files);

        var changes = new Dictionary<string, string>
        {
            ["lib/edit.cs"] = Lines("edit", 100).Replace("// edit line 7\n", "// edit line seven\n", StringComparison.Ordinal),
            ["lib/new.cs"] = Lines("new", 30),
        };
        for (var i = 1; i <= RootFiles; i++)
        {
            changes[$"f{i:00}.cs"] = Lines($"f{i:00}", (RootFiles + 2 - i) * 5) + $"// marker f{i:00}.cs\n";
        }

        File.Delete(Path.Combine(root, "lib", "gone.cs"));
        Commands.Remove(repo, "lib/gone.cs");
        ReadmeFixture.Commit(repo, ReadmeFixture.Sig("Grace Hopper", "grace@example.com", daysAgo: 5), "Changes", changes);
        return root;
    }

    private static string Lines(string name, int count)
        => string.Concat(Enumerable.Range(1, count).Select(i => $"// {name} line {i}\n"));

    private static void WaitUntil(Func<bool> condition, string what, int seconds = 20, Func<string>? state = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}. {state?.Invoke()}");
            }

            Thread.Sleep(25);
            UiSnapshot.Pump();
        }
    }
}
