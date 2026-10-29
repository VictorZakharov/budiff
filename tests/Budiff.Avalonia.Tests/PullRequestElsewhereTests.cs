using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Budiff.App;
using Budiff.Git;
using LibGit2Sharp;
using Xunit;

namespace Budiff.Avalonia.Tests;

// A pull request that works elsewhere in the repository, opened on a folder
// it does not touch: the bird's-eye says so instead of an unexplained empty
// tree, and a file of the working branch kept open says it is not part of
// the PR instead of showing one blank line.
public sealed partial class PullRequestModeTests
{
    [AvaloniaFact]
    public void PullRequestElsewhere_SaysItChangesNothingHere()
    {
        var root = ReadmeFixture.BuildFixtureRepo("pr-elsewhere");
        string baseSha, headSha;
        using (var repo = new Repository(root))
        {
            headSha = repo.Head.Tip.Sha;
            baseSha = repo.Head.Tip.Parents.First().Sha;
        }

        // Only on the working branch: in neither end of the PR.
        var docs = Path.Combine(root, "docs");
        File.WriteAllText(Path.Combine(docs, "notes.md"), "# Notes\n\nWork in progress.\n");

        var pr = new PullRequestInfo(9, "Simplify the line diff", "OPEN", false, "grace", FakePullRequestSource.Now, "main", "feature", baseSha, headSha, 3, 2, 2);
        var window = new MainWindow(docs, gitRoot: false) { Width = 1400, Height = 560 };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");
        var notes = AllFiles(vm).Single(r => r.Name == "notes.md");
        _ = vm.Analysis.OpenAsync(notes);
        WaitUntil(() => vm.IsDiffOpen && !vm.Diff.IsLoading && !vm.Diff.IsPlaceholder, "the working file");

        vm.Analysis.PullRequests = new FakePullRequestSource(new[] { pr }) { Root = root };
        vm.Analysis.PickPullRequest = () => Task.FromResult<PullRequestInfo?>(pr);
        vm.Analysis.SelectedPeriod = vm.Analysis.Periods.Single(p => p.IsPullRequest);
        WaitUntil(() => vm.Analysis.AppliedPullRequest is not null && !vm.Analysis.IsScanning, "PR load");

        Assert.Empty(AllFiles(vm));
        Assert.StartsWith("PR #9 changes no files in this folder", vm.Analysis.SummaryText, StringComparison.Ordinal);
        WaitUntil(() => vm.Diff.IsPlaceholder, "the open file's placeholder");
        Assert.Contains("neither end of this comparison", vm.Diff.Placeholder, StringComparison.Ordinal);
        UiSnapshot.Capture(window, "pr-elsewhere");
        window.Close();
    }
}
