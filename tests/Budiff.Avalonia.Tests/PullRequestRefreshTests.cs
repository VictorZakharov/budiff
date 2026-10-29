using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Budiff.App;
using Budiff.CodeAnalysis;
using Budiff.Git;
using LibGit2Sharp;
using Xunit;

namespace Budiff.Avalonia.Tests;

// Refresh while reviewing a pull request asks GitHub where the PR is now: a
// force-pushed amend (a new head the clone has never seen, on the remote's
// refs/pull/N/head only) is fetched and shown - the tree, its open folders,
// the selection and the open file stay, and the diff is the new head's.
public sealed partial class PullRequestModeTests
{
    [AvaloniaFact]
    public void Refresh_OfAPullRequest_FollowsAForcePushedAmend()
    {
        var root = ReadmeFixture.BuildFixtureRepo("pr-refresh");
        string baseSha, oldHead;
        using (var repo = new Repository(root))
        {
            oldHead = repo.Head.Tip.Sha;
            baseSha = repo.Head.Tip.Parents.First().Sha;
        }

        // The remote: the PR amended there - same base, a new head that
        // adds a file and leaves the README alone. Only refs/pull/7/head
        // points at it.
        var upstream = Path.Combine(AppContext.BaseDirectory, "fixtures", "pr-refresh-upstream");
        ReadmeFixture.ForceDelete(upstream);
        Repository.Clone(root, upstream);
        string newHead;
        using (var remote = new Repository(upstream))
        {
            remote.Reset(ResetMode.Hard, remote.Lookup<Commit>(baseSha));
            ReadmeFixture.Commit(remote, ReadmeFixture.Sig("Ada Lovelace", "ada@example.com", daysAgo: 1), "Simplify the line diff (amended)", new()
            {
                ["src/Budiff.Core/LineDiff.cs"] = "namespace Budiff;\n\npublic static class LineDiff\n{\n    public static int AmendedMarker => 42;\n}\n",
                ["src/Budiff.Core/Amended.cs"] = "namespace Budiff;\n\npublic static class Amended\n{\n}\n",
            });
            newHead = remote.Head.Tip.Sha;
            remote.Refs.Add("refs/pull/7/head", remote.Head.Tip.Id);
        }

        using (var repo = new Repository(root))
        {
            repo.Network.Remotes.Add("origin", upstream);
            Assert.Null(repo.Lookup<Commit>(newHead));
        }

        var pr = new PullRequestInfo(7, "Simplify the line diff", "OPEN", false, "ada", FakePullRequestSource.Now, "main", "feature", baseSha, oldHead, 3, 2, 2);
        var window = new MainWindow(root, gitRoot: true) { Width = 1400, Height = 560 };
        window.Show();
        var vm = window.ViewModel;
        var tree = window.FindControl<TreeView>("AnalysisTree")!;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");
        var source = new FakePullRequestSource(new[] { pr }) { Root = root };
        vm.Analysis.PullRequests = source;
        vm.Analysis.PickPullRequest = () => Task.FromResult<PullRequestInfo?>(pr);
        vm.Analysis.SelectedPeriod = vm.Analysis.Periods.Single(p => p.IsPullRequest);
        WaitUntil(() => vm.Analysis.AppliedPullRequest is not null && !vm.Analysis.IsScanning, "PR load");
        Assert.Equal(new[] { "LineDiff.cs", "README.md" }, AllFiles(vm).Select(r => r.Name).Order(StringComparer.Ordinal));

        var row = AllFiles(vm).Single(r => r.Name == "LineDiff.cs");
        vm.Analysis.SetSubtreeExpanded(vm.Analysis.Roots.First(r => !r.IsFile), expanded: true);
        tree.SelectedItem = row;
        _ = vm.Analysis.OpenAsync(row);
        WaitUntil(() => vm.IsDiffOpen && !vm.Diff.IsLoading && !vm.Diff.IsPlaceholder, "PR diff");
        Assert.DoesNotContain(vm.Diff.AllRows, r => r.NewText?.Contains("AmendedMarker", StringComparison.Ordinal) == true);
        var expanded = vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).Where(r => !r.IsFile && r.IsExpanded).Select(r => r.RelativePath).Order(StringComparer.Ordinal).ToArray();

        // Force-pushed: GitHub now reports the amended head.
        source.Latest = pr with { HeadRefOid = newHead, UpdatedAt = FakePullRequestSource.Now.AddHours(1) };
        vm.RefreshCommand.Execute(null);
        WaitUntil(
            () => vm.Analysis.AppliedPullRequest?.HeadSha == newHead && !vm.Analysis.IsScanning && !vm.Diff.IsLoading && !vm.Diff.IsPlaceholder
                && vm.Diff.AllRows.Any(r => r.NewText?.Contains("AmendedMarker", StringComparison.Ordinal) == true),
            "the amended PR");

        Assert.Equal(1, source.RefreshCalls);
        Assert.True(vm.Analysis.IsPullRequestPeriod);
        Assert.Equal(new[] { "Amended.cs", "LineDiff.cs" }, AllFiles(vm).Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.Equal($"PR #7: merge-base {baseSha[..7]} vs head {newHead[..7]}", vm.Diff.Subtitle);
        Assert.Equal("LineDiff.cs", (tree.SelectedItem as CodeAnalysisRow)?.Name);
        Assert.True(vm.IsDiffOpen);
        Assert.Equal(expanded, vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).Where(r => !r.IsFile && r.IsExpanded).Select(r => r.RelativePath).Order(StringComparer.Ordinal).ToArray());

        // Still a local-only fetch: no ref was written for the PR.
        using (var repo = new Repository(root))
        {
            Assert.NotNull(repo.Lookup<Commit>(newHead));
            Assert.DoesNotContain(repo.Refs, r => r.CanonicalName.Contains("pull", StringComparison.Ordinal));
        }

        UiSnapshot.Capture(window, "pr-refresh-amended");
        window.Close();
    }
}
