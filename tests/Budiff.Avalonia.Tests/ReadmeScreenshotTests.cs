using System;
using System.Linq;
using System.Threading;
using Avalonia.Headless.XUnit;
using Budiff.App;
using Xunit;

namespace Budiff.Avalonia.Tests;

// Renders the REAL Budiff.App.MainWindow (not a mockup) over a seeded git fixture
// and captures the README screenshots: the default bird's-eye state (filters visible,
// no diff) and the drill-in state (compressed columns + docked diff).
public sealed class ReadmeScreenshotTests
{
    [AvaloniaFact]
    public void ReadmeScreenshots_RenderRealMainWindow()
    {
        var root = ReadmeFixture.BuildFixtureRepo();
        var window = new MainWindow(root, gitRoot: true);
        window.Width = 1440;
        window.Height = 820;
        window.Show();

        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");

        // Default state: full bird's-eye, filter panes visible, no diff pane.
        Assert.True(vm.ShowWideColumns);
        Assert.False(vm.IsDiffOpen);
        Assert.NotEmpty(vm.Analysis.Extensions);
        Assert.NotEmpty(vm.Analysis.Authors);

        foreach (var top in vm.Analysis.Roots)
        {
            top.IsExpanded = true;
        }

        UiSnapshot.Pump();
        var birdsEye = UiSnapshot.Capture(window, "gui-screenshot");
        Assert.Equal(1440, birdsEye.Width);
        Assert.Equal(820, birdsEye.Height);

        // Drill-in state: open the modified file, docked diff + compressed columns.
        var row = vm.Analysis.Roots
            .SelectMany(r => r.SelfAndDescendants())
            .FirstOrDefault(r => r.IsFile && r.Name == "Program.cs");
        Assert.NotNull(row);
        _ = vm.Analysis.OpenAsync(row!);

        WaitUntil(() => vm.IsDiffOpen && !vm.Diff.IsPlaceholder && vm.Diff.Rows.Count > 0, "diff load");
        WaitUntil(() => !vm.Diff.IsHighlighting, "syntax highlighting", throwOnTimeout: false);

        Assert.True(vm.ShowDiffPane);
        Assert.False(vm.ShowWideColumns);

        var diffing = UiSnapshot.Capture(window, "gui-diff");
        Assert.Equal(1440, diffing.Width);
        Assert.Equal(820, diffing.Height);
    }

    private static void WaitUntil(Func<bool> condition, string what, bool throwOnTimeout = true)
    {
        var deadline = Environment.TickCount64 + 20_000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                if (throwOnTimeout)
                {
                    throw new TimeoutException($"Timed out waiting for {what}.");
                }

                return;
            }

            Thread.Sleep(25);
            UiSnapshot.Pump();
        }

        UiSnapshot.Pump();
    }
}
