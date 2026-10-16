using System;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Budiff.App;
using Budiff.Avalonia.Markdown;
using Budiff.Git;
using Xunit;

namespace Budiff.Avalonia.Tests;

// The README's 2x2 screenshot grid, rendered by the real MainWindow over a
// real repository: a clone of the public VictorZakharov/last-stand at
// BUDIFF_README_REPO (the test skips without it). The pull request shot asks
// GitHub through gh, so gh must be signed in. Captures go where UiSnapshot
// writes (BUDIFF_UISNAPSHOT_DIR); copy them to docs/ by hand.
public sealed class ReadmeGridScreenshotTests
{
    private const int Width = 1440;
    private const int Height = 810;

    [AvaloniaFact]
    public void ReadmeGrid_FromARealRepository()
    {
        var root = Environment.GetEnvironmentVariable("BUDIFF_README_REPO");
        Assert.SkipWhen(string.IsNullOrEmpty(root), "Set BUDIFF_README_REPO to a clone of VictorZakharov/last-stand.");
        var file = Environment.GetEnvironmentVariable("BUDIFF_README_DIFF") ?? "src/entities/viewModel.ts";
        var prFile = Environment.GetEnvironmentVariable("BUDIFF_README_PR_DIFF");
        var prNumber = int.Parse(Environment.GetEnvironmentVariable("BUDIFF_README_PR") ?? "254");

        var window = new MainWindow(root!, gitRoot: true) { Width = Width, Height = Height };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");

        // 1. The bird's-eye over the last week, src opened.
        vm.Analysis.SelectedPeriod = vm.Analysis.Periods.Single(p => p.Label == "Last week");
        WaitUntil(() => vm.Analysis.AppliedSinceDays == 7 && !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "last week");
        foreach (var top in vm.Analysis.Roots.Where(r => r.Name == "src"))
        {
            top.IsExpanded = true;
        }

        UiSnapshot.Pump();
        UiSnapshot.Capture(window, "readme-birdseye");

        // 2. A file's side-by-side diff, docked wide.
        Open(window, file);
        UiSnapshot.Capture(window, "readme-diff");

        // 3. The README's formatted preview: its pictures, changed blocks marked.
        Open(window, "README.md");
        vm.Diff.ShowPreview = true;
        WaitUntil(() => vm.Diff.IsPreviewActive, "preview");
        Thread.Sleep(500);
        WaitUntil(() => PicturesSettled(window), "preview pictures", throwOnTimeout: false);
        UiSnapshot.Capture(window, "readme-markdown");
        vm.Diff.ShowPreview = false;

        // 4. A pull request: only its changes, the drill-in from merge-base to head.
        var source = vm.Analysis.PullRequests!;
        var pr = source.SearchAsync(new PullRequestQuery(null, PullRequestStateFilter.All, $"#{prNumber}"), null, 5, CancellationToken.None)
            .GetAwaiter().GetResult().Items.Single(p => p.Number == prNumber);
        vm.Analysis.PickPullRequest = () => System.Threading.Tasks.Task.FromResult<PullRequestInfo?>(pr);
        vm.Analysis.SelectedPeriod = vm.Analysis.Periods.Single(p => p.IsPullRequest);
        WaitUntil(() => vm.Analysis.AppliedPullRequest is not null && !vm.Analysis.IsScanning, "pull request", timeoutMs: 120_000);
        var prRow = prFile is null
            ? vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).Where(r => r.IsFile && !r.Name.EndsWith(".md", StringComparison.Ordinal)).OrderByDescending(r => r.RemovedText.Length).First()
            : Row(vm, prFile);
        foreach (var folder in vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).Where(r => !r.IsFile))
        {
            folder.IsExpanded = true;
        }

        Open(window, prRow.RelativePath);
        UiSnapshot.Capture(window, "readme-pr");
        window.Close();
    }

    private static CodeAnalysisRow Row(MainWindowViewModel vm, string path)
        => vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).First(r => r.IsFile && r.RelativePath.Replace('\\', '/') == path);

    private static void Open(MainWindow window, string path)
    {
        var vm = window.ViewModel;
        var row = Row(vm, path);
        _ = vm.Analysis.OpenAsync(row);
        WaitUntil(() => vm.IsDiffOpen && !vm.Diff.IsLoading && !vm.Diff.IsPlaceholder && vm.Diff.Rows.Count > 0, "diff of " + path);
        WaitUntil(() => !vm.Diff.IsHighlighting, "highlighting", throwOnTimeout: false);

        // As if the splitter were dragged: the diff gets most of the width.
        window.BodyGrid.ColumnDefinitions[2].Width = new GridLength(Width - 520);
        UiSnapshot.Pump();
        Thread.Sleep(200);
        UiSnapshot.Pump();
    }

    private static bool PicturesSettled(MainWindow window)
        => window.GetVisualDescendants().OfType<MarkdownRenderer.PictureSlot>()
            .All(s => s.LoadState is null or "shown" or "no picture" || s.LoadState.StartsWith("failed", StringComparison.Ordinal));

    private static void WaitUntil(Func<bool> condition, string what, bool throwOnTimeout = true, int timeoutMs = 30_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
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
