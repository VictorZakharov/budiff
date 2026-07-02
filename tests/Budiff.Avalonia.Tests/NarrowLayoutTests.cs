using System;
using System.Linq;
using System.Threading;
using Avalonia.Headless.XUnit;
using Budiff.App;
using Xunit;

namespace Budiff.Avalonia.Tests;

// Regression captures for #39/#40: squeezed panes must degrade gracefully
// (shrink/trim/clip) instead of controls painting over each other. Renders the
// real MainWindow at deliberately tight sizes; the PNGs land in ui-snapshots
// for eyeballing alongside the layout-state assertions.
public sealed class NarrowLayoutTests
{
    [AvaloniaFact]
    public void NarrowPanes_RenderWithoutLayoutBlowups()
    {
        var root = ReadmeFixture.BuildFixtureRepo("budiff-narrow");

        // Narrow bird's-eye alone: the toolbar row is tight at this width.
        // (Drop the window minimum so the squeeze actually happens.)
        var window = new MainWindow(root, gitRoot: true);
        window.MinWidth = 0;
        window.Width = 560;
        window.Height = 480;
        window.Show();

        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");
        vm.Analysis.SearchText = "Renderer";
        UiSnapshot.Pump();
        UiSnapshot.Capture(window, "narrow-birdseye");

        // Squeezed split: both panes forced under their comfortable widths.
        window.Width = 900;
        UiSnapshot.Pump();
        var row = vm.Analysis.Roots
            .SelectMany(r => r.SelfAndDescendants())
            .FirstOrDefault(r => r.IsFile && r.Name == "Program.cs");
        Assert.NotNull(row);
        _ = vm.Analysis.OpenAsync(row!);
        WaitUntil(() => vm.IsDiffOpen && !vm.Diff.IsPlaceholder, "diff load");

        UiSnapshot.Pump();
        UiSnapshot.Capture(window, "narrow-diff");

        Assert.True(vm.ShowDiffPane);

        // Blame auto-hide: at 900 the diff (~475px) is at least half the window,
        // so blame shows; growing the window (diff column width stays fixed)
        // drops it under half - blame hides and the checkbox tracks it.
        Assert.True(vm.Diff.ShowBlame);
        Assert.False(vm.Diff.BlameCramped);
        window.Width = 1400;
        UiSnapshot.Pump();
        UiSnapshot.Pump();
        Assert.True(vm.Diff.BlameCramped);
        Assert.False(vm.Diff.IsBlameVisible);
        Assert.False(vm.Diff.ShowBlame);

        // The user can still override the auto-hide by re-checking the box.
        vm.Diff.ShowBlame = true;
        UiSnapshot.Pump();
        Assert.True(vm.Diff.IsBlameVisible);
        window.Close();
    }

    private static void WaitUntil(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + 20_000;
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
