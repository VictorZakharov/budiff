using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Headless.XUnit;
using Budiff.App;
using Xunit;

namespace Budiff.Avalonia.Tests;

// A drill-in whose left and right sides are identical must show the file, not
// one "N unchanged lines" gap. The common way in: All time, where the default
// "Period start" base is HEAD, on a file with no uncommitted changes.
public sealed class UnchangedDiffTests
{
    [AvaloniaFact]
    public void AllTime_UnchangedFile_ShowsTheWholeFile()
    {
        var root = ReadmeFixture.BuildFixtureRepo("unchanged-diff");
        var window = new MainWindow(root, gitRoot: true) { Width = 1200, Height = 520 };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");

        vm.Analysis.SelectedPeriod = vm.Analysis.Periods.Single(p => p.Label == "All time");
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.AppliedSinceDays is null && vm.Analysis.Roots.Count > 0, "all-time rescan");

        var row = vm.Analysis.Roots
            .SelectMany(r => r.SelfAndDescendants())
            .Single(r => r.IsFile && r.Name == "LineDiff.cs");
        _ = vm.Analysis.OpenAsync(row);
        WaitUntil(() => vm.IsDiffOpen && !vm.Diff.IsPlaceholder, "diff load");
        UiSnapshot.Capture(window, "unchanged-diff");

        Assert.Equal("HEAD vs working tree", vm.Diff.Subtitle);
        Assert.True(vm.Diff.CollapseUnchanged);
        Assert.True(vm.Diff.IsSinglePane);
        Assert.DoesNotContain(vm.Diff.Rows, r => r.IsGap);
        Assert.All(vm.Diff.Rows, r => Assert.Equal(DiffKind.Unchanged, r.Kind));
        Assert.Equal(LineDiff.SplitLines(File.ReadAllText(row.FullPath!)).Length, vm.Diff.Rows.Count);
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
