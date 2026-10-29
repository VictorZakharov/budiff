using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Budiff.App;
using Budiff.Git;
using LibGit2Sharp;
using Xunit;

namespace Budiff.Avalonia.Tests;

// The PR checked out here with commits on top that are not pushed yet: the
// commit picker lists them first, marked local, and picking one shows it.
public sealed partial class PullRequestModeTests
{
    [AvaloniaFact]
    public void PullRequestCommits_ListLocalOnesFirst_MarkedLocal()
    {
        var root = ReadmeFixture.BuildFixtureRepo("pr-local-commits");
        string baseSha, headSha, branch, localSha;
        using (var repo = new Repository(root))
        {
            headSha = repo.Head.Tip.Sha;
            baseSha = repo.Head.Tip.Parents.First().Sha;
            branch = repo.Head.FriendlyName;
            ReadmeFixture.Commit(repo, ReadmeFixture.Sig("Grace Hopper", "grace@example.com", daysAgo: 0), "Tidy the loader (not pushed)", new()
            {
                ["src/Budiff.Core/Loader.cs"] = "namespace Budiff;\n\npublic static class Loader\n{\n    public static string Load() => \"tidy\";\n}\n",
            });
            localSha = repo.Head.Tip.Sha;
        }

        var pr = new PullRequestInfo(12, "Simplify the line diff", "OPEN", false, "grace", FakePullRequestSource.Now, "main", branch, baseSha, headSha, 3, 2, 1);
        var window = new MainWindow(root, gitRoot: true) { Width = 1400, Height = 760 };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");

        vm.Analysis.PullRequests = new FakePullRequestSource(new[] { pr }) { Root = root };
        vm.Analysis.PickPullRequest = () => Task.FromResult<PullRequestInfo?>(pr);
        vm.Analysis.SelectedPeriod = vm.Analysis.Periods.Single(p => p.IsPullRequest);
        WaitUntil(() => vm.Analysis.AppliedPullRequest is not null && !vm.Analysis.IsScanning, "PR load");

        Assert.Equal("1 commit +1 local", vm.Analysis.SummaryCommits);
        var link = window.FindControl<Button>("CommitsLink")!;
        window.CommitsFlyout.ShowAt(link);
        WaitUntil(() => window.CommitsFlyout.IsOpen && vm.Analysis.CommitChoices.Count > 0, "the commit list");
        var choices = vm.Analysis.CommitChoices;
        Assert.True(choices[0].IsAll);
        Assert.Equal("Tidy the loader (not pushed)", choices[1].Subject);
        Assert.True(choices[1].IsLocal);
        Assert.Equal(localSha, choices[1].Sha);
        Assert.All(choices.Skip(2), c => Assert.False(c.IsLocal));
        Assert.Contains(choices.Skip(2), c => c.Sha == headSha);

        // The tag shows on the entry.
        var popup = TopLevel.GetTopLevel(window.FindControl<ItemsControl>("CommitChoicesList"))!;
        UiSnapshot.Pump();
        Assert.Contains(
            global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(popup).OfType<TextBlock>(),
            t => t.Text == "local" && t.IsEffectivelyVisible);
        UiSnapshot.CaptureTopLevel(popup, "pr-local-commits");
        window.CommitsFlyout.Hide();

        // Picking it shows that commit, and the list still has it, current.
        _ = vm.Analysis.PickCommitCommand.ExecuteAsync(choices[1]);
        WaitUntil(() => vm.Analysis.AppliedCommit?.Sha == localSha && !vm.Analysis.IsScanning, "the local commit");
        vm.Analysis.LoadCommitChoices();
        Assert.Contains(vm.Analysis.CommitChoices, c => c.IsLocal && c.IsCurrent);
        window.Close();
    }
}
