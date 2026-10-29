using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Budiff.App;
using Budiff.Git;
using LibGit2Sharp;
using Xunit;

namespace Budiff.Avalonia.Tests;

// The Period list works while the first (default-period) load still runs: a
// period or a pull request picked meanwhile cancels that load and wins, and a
// late result of the old load is dropped.
public sealed partial class PullRequestModeTests
{
    [AvaloniaFact]
    public void PeriodPickedDuringTheFirstLoad_CancelsIt_AndLoadsThatPeriod()
    {
        var root = ReadmeFixture.BuildFixtureRepo("period-mid-load");
        var held = new ManualResetEventSlim();
        var loads = new ConcurrentQueue<(int? Days, CancellationToken Token)>();
        CodeAnalysisPanelViewModel.FolderLoadHook = (path, days, ct) =>
        {
            if (!SamePath(path, root))
            {
                return;
            }

            loads.Enqueue((days, ct));
            if (loads.Count == 1)
            {
                held.Set();
                ct.WaitHandle.WaitOne(20_000);
            }
        };
        try
        {
            var window = new MainWindow(root, gitRoot: true) { Width = 1400, Height = 560 };
            window.Show();
            var vm = window.ViewModel;
            WaitUntil(() => held.IsSet, "the first load to start");
            UiSnapshot.Pump();
            Assert.True(vm.Analysis.IsScanning);
            Assert.True(vm.Analysis.CanPickPeriod);
            Assert.True(window.FindControl<ComboBox>("PeriodBox")!.IsEffectivelyEnabled, "the period list is usable mid-load");

            vm.Analysis.SelectedPeriod = vm.Analysis.Periods.Single(p => p.Label == "All time");
            WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "the All time load");

            var seen = loads.ToList();
            Assert.Equal(new int?[] { 30, null }, seen.Select(l => l.Days));
            Assert.True(seen[0].Token.IsCancellationRequested, "the first load was cancelled");
            Assert.Null(vm.Analysis.AppliedSinceDays);
            Assert.Equal("All time", vm.Analysis.SelectedPeriod.Label);
            Assert.True(vm.Analysis.GitAvailable);
            window.Close();
        }
        finally
        {
            CodeAnalysisPanelViewModel.FolderLoadHook = null;
        }
    }

    [AvaloniaFact]
    public void PullRequestPickedDuringTheFirstLoad_ShowsThePr_AndTheLateLoadIsDropped()
    {
        var root = ReadmeFixture.BuildFixtureRepo("pr-mid-load");
        string baseSha, headSha;
        using (var repo = new Repository(root))
        {
            headSha = repo.Head.Tip.Sha;
            baseSha = repo.Head.Tip.Parents.First().Sha;
        }

        var pr = new PullRequestInfo(7, "Simplify the line diff", "OPEN", false, "ada", FakePullRequestSource.Now, "main", "feature", baseSha, headSha, 3, 2, 2);

        // The first load ignores the cancel and finishes only when released:
        // its (complete, folder-wide) result arrives after the PR's.
        var held = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var calls = 0;
        CodeAnalysisPanelViewModel.FolderLoadHook = (path, days, ct) =>
        {
            if (SamePath(path, root) && Interlocked.Increment(ref calls) == 1)
            {
                held.Set();
                release.Wait(20_000);
            }
        };
        try
        {
            var window = new MainWindow(root, gitRoot: true) { Width = 1400, Height = 560 };
            window.Show();
            var vm = window.ViewModel;
            WaitUntil(() => held.IsSet, "the first load to start");
            vm.Analysis.PullRequests = new FakePullRequestSource(new[] { pr }) { Root = root };
            vm.Analysis.PickPullRequest = () => Task.FromResult<PullRequestInfo?>(pr);

            vm.Analysis.SelectedPeriod = vm.Analysis.Periods.Single(p => p.IsPullRequest);
            WaitUntil(() => vm.Analysis.AppliedPullRequest is not null && !vm.Analysis.IsScanning, "PR load");
            Assert.Equal(new[] { "LineDiff.cs", "README.md" }, AllFiles(vm).Select(r => r.Name).Order(StringComparer.Ordinal));

            release.Set();
            var until = Environment.TickCount64 + 600;
            while (Environment.TickCount64 < until)
            {
                Thread.Sleep(25);
                UiSnapshot.Pump();
            }

            Assert.True(vm.Analysis.IsPullRequestPeriod);
            Assert.NotNull(vm.Analysis.AppliedPullRequest);
            Assert.False(vm.Analysis.IsScanning);
            Assert.Equal(new[] { "LineDiff.cs", "README.md" }, AllFiles(vm).Select(r => r.Name).Order(StringComparer.Ordinal));

            // Back to a time period: the folder is scanned now (it never was).
            vm.Analysis.SelectedPeriod = vm.Analysis.Periods.Single(p => p.Label == "Last month");
            WaitUntil(() => vm.Analysis.AppliedPullRequest is null && !vm.Analysis.IsScanning && AllFiles(vm).Count > 2, "folder load");
            Assert.Equal(30, vm.Analysis.AppliedSinceDays);
            window.Close();
        }
        finally
        {
            release.Set();
            CodeAnalysisPanelViewModel.FolderLoadHook = null;
        }
    }

    private static bool SamePath(string a, string b)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);
}
