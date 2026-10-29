using System;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Budiff.App;
using Xunit;

namespace Budiff.Avalonia.Tests;

// The diff pane's View / Diff switch: View shows the file as it is now (the
// new side only, plain, no change marks), Diff the comparison. Diff is the
// default; Ctrl+D flips; the choice carries to the next file. On the real
// window.
public sealed class ViewModeTests
{
    [AvaloniaFact]
    public void ViewMode_ShowsOnlyTheNewFile_AndFlipsBack()
    {
        var root = ReadmeFixture.BuildFixtureRepo("budiff-view-mode");
        var window = new MainWindow(root, gitRoot: true) { Width = 1180, Height = 800 };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");

        // Program.cs: lines added and one removed in the working tree.
        Open(window, "Program.cs");
        var diff = vm.Diff;
        var newLines = FileLines(root, "src/Budiff.Cli/Program.cs");

        // Diff is the default: the comparison, with its removed line and tints.
        Assert.False(diff.IsViewMode);
        Assert.True(diff.CanView);
        var toggle = window.FindControl<ViewModeSwitch>("DiffModeSwitch")!;
        Assert.True(toggle.IsEffectivelyVisible);
        Assert.Contains("active", Segment(toggle, "DiffModeButton").Classes);
        Assert.DoesNotContain("active", Segment(toggle, "ViewModeButton").Classes);
        Assert.False(diff.IsSinglePane);
        Assert.Contains(diff.Rows, IsChange);
        Assert.Contains(diff.Rows, r => r.IsGap);
        Assert.True(Funnel(window).IsEffectivelyVisible);
        var view = window.GetVisualDescendants().OfType<DiffView>().Single();
        Assert.Contains(view.GetVisualDescendants().OfType<DiffSplitGrid>(), g => g.IsEffectivelyVisible);

        // A click on View: the new file only - every line once, in order,
        // untinted, no bars, no funnel; blame stays (it is per line).
        Click(window, Segment(toggle, "ViewModeButton"));
        WaitUntil(() => diff.IsViewActive, "View mode");
        AssertViewMode(window, newLines);
        Assert.Contains("active", Segment(toggle, "ViewModeButton").Classes);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<CheckBox>(), c => c.Content as string == "Collapse unchanged" && c.IsEffectivelyVisible);
        WaitUntil(() => diff.Rows.Any(r => r.Blame is not null), "blame in View mode");
        Assert.True(diff.IsBlameVisible);
        UiSnapshot.Pump();
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Classes.Contains("blameCell") && t.IsEffectivelyVisible && t.Text?.Length > 0);
        UiSnapshot.Capture(window, "view-mode");

        // Ctrl+D (focus in the tree) flips back to the comparison...
        PressCtrlD(window);
        WaitUntil(() => !diff.IsViewActive, "Diff mode again");
        UiSnapshot.Pump();
        Assert.Contains(diff.Rows, IsChange);
        Assert.True(Funnel(window).IsEffectivelyVisible);
        Assert.Contains("active", Segment(toggle, "DiffModeButton").Classes);

        // ... and to View again.
        PressCtrlD(window);
        WaitUntil(() => diff.IsViewActive, "View mode by Ctrl+D");
        AssertViewMode(window, newLines);

        // The choice carries to the next file.
        Open(window, "CliOptions.cs");
        Assert.True(diff.IsViewActive);
        AssertViewMode(window, diff.Rows.Count);
        Assert.All(diff.Rows, r => Assert.NotNull(r.NewNo));

        // A folder summary has nothing to view: no switch, Ctrl+D does nothing.
        var folder = vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).First(r => !r.IsFile);
        vm.FollowSelection(folder);
        WaitUntil(() => vm.IsFolderSummary, "the folder summary");
        Assert.False(diff.CanView);
        Assert.False(toggle.IsEffectivelyVisible);
        PressCtrlD(window);
        Assert.True(diff.IsViewMode);

        window.Close();
    }

    [AvaloniaFact]
    public void FloatingWindow_HasTheSwitch_AndCtrlD()
    {
        var root = ReadmeFixture.BuildFixtureRepo("budiff-view-mode-float");
        var window = new MainWindow(root, gitRoot: true) { Width = 1180, Height = 800 };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");
        Open(window, "Program.cs");

        vm.IsDiffFloating = true;
        WaitUntil(() => window.OwnedWindows.OfType<DiffWindow>().Any(w => w.IsVisible), "the floating window");
        var floating = window.OwnedWindows.OfType<DiffWindow>().Single();
        UiSnapshot.Pump();
        var toggle = floating.GetVisualDescendants().OfType<ViewModeSwitch>().Single();
        Assert.True(toggle.IsEffectivelyVisible);
        Assert.False(vm.Diff.IsViewMode);

        PressCtrlD(floating);
        WaitUntil(() => vm.Diff.IsViewActive, "View mode in the floating window");
        AssertViewMode(floating, FileLines(root, "src/Budiff.Cli/Program.cs"));
        UiSnapshot.Capture(floating, "view-mode-floating");

        Click(floating, Segment(toggle, "DiffModeButton"));
        WaitUntil(() => !vm.Diff.IsViewActive, "Diff mode in the floating window");
        floating.Close();
        window.Close();
    }

    private static void AssertViewMode(Window window, int lines)
    {
        UiSnapshot.Pump();
        var diff = ((MainWindow)(window as MainWindow ?? (Window)window.Owner!)).ViewModel.Diff;
        Assert.True(diff.IsSinglePane);
        Assert.Equal(lines, diff.Rows.Count);
        Assert.Equal(Enumerable.Range(1, lines).Select(n => n.ToString()), diff.Rows.Select(r => r.SingleNo));
        Assert.All(diff.Rows, r =>
        {
            Assert.False(r.IsGap);
            Assert.Equal(Budiff.DiffKind.Unchanged, r.Kind);
            Assert.Same(Brushes.Transparent, r.SingleMarker);
            Assert.Same(Brushes.Transparent, r.NewBackground);
        });

        var view = window.GetVisualDescendants().OfType<DiffView>().Single(v => v.IsEffectivelyVisible);
        Assert.False(view.GetVisualDescendants().OfType<DiffFunnelGutter>().Single().IsEffectivelyVisible);
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<DiffSplitGrid>(), g => g.IsEffectivelyVisible);
    }

    // A row per line of the file on disk (the last one empty after its final newline).
    private static int FileLines(string root, string path)
        => System.IO.File.ReadAllText(System.IO.Path.Combine(root, path)).Split('\n').Length;

    // The old side's line is gone or replaced.
    private static bool IsChange(DiffRowVm row) => row.Kind is Budiff.DiffKind.Removed or Budiff.DiffKind.Modified;

    private static void Open(MainWindow window, string name)
    {
        var vm = window.ViewModel;
        var row = vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).Single(r => r.IsFile && r.Name == name);
        _ = vm.Analysis.OpenAsync(row);
        WaitUntil(() => vm.IsDiffOpen && !vm.IsFolderSummary && !vm.Diff.IsLoading && !vm.Diff.IsPlaceholder && vm.Diff.FileName == name, name);
    }

    private static DiffFunnelGutter Funnel(MainWindow window)
        => window.GetVisualDescendants().OfType<DiffView>().Single().GetVisualDescendants().OfType<DiffFunnelGutter>().Single();

    private static Button Segment(ViewModeSwitch toggle, string name)
        => toggle.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);

    private static void Click(Window window, Control control)
    {
        UiSnapshot.Pump();
        var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void PressCtrlD(Window window)
    {
        window.KeyPress(Key.D, RawInputModifiers.Control, PhysicalKey.D, null);
        window.KeyRelease(Key.D, RawInputModifiers.Control, PhysicalKey.D, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void WaitUntil(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + 30_000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            Thread.Sleep(25);
            UiSnapshot.Pump();
        }

        UiSnapshot.Pump();
    }
}
