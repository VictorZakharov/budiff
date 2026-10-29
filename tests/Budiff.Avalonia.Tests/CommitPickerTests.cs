using System;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Budiff.App;
using Xunit;

namespace Budiff.Avalonia.Tests;

// The bird's-eye summary line's "N commits" is a link: it drops down the
// period's commits (subject, sha, author, age, lines); picking one scopes the
// whole view - tree and diff - to that commit, and picking it again (or All)
// goes back to the period. On the real window.
public sealed class CommitPickerTests
{
    [AvaloniaFact]
    public void CommitsLink_ListsThePeriod_AndPickingOneScopesTheView()
    {
        var root = ReadmeFixture.BuildFixtureRepo("budiff-commit-picker");
        var window = new MainWindow(root, gitRoot: true) { Width = 1180, Height = 800 };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");

        // Last month: four commits (the scaffold is older).
        var link = window.FindControl<Button>("CommitsLink")!;
        UiSnapshot.Pump();
        Assert.True(link.IsEffectivelyVisible);
        Assert.Equal("4\u00A0commits", vm.Analysis.SummaryCommits);
        Assert.Equal(vm.Analysis.SummaryText, vm.Analysis.SummaryLead + vm.Analysis.SummaryCommits + vm.Analysis.SummaryTail);

        // The diff pane on a file the "Trim the renderer" commit changed.
        Open(window, "CliOptions.cs");
        Assert.StartsWith("period start", vm.Diff.Subtitle, StringComparison.Ordinal);

        Click(window, link);
        WaitUntil(() => window.CommitsFlyout.IsOpen, "the commit list");
        var choices = vm.Analysis.CommitChoices;
        Assert.True(choices[0].IsAll && choices[0].IsCurrent);
        Assert.Equal("All (4 commits)", choices[0].Subject);
        Assert.Equal("Commits - Last month", vm.Analysis.CommitChoicesTitle);
        var commits = choices.Skip(1).ToList();
        Assert.Equal(new[] { "Simplify the line diff", "Add the diff loader", "Trim the renderer", "Add tests and docs" }, commits.Select(c => c.Subject));
        Assert.Equal(new[] { "Ada Lovelace", "Grace Hopper", "Grace Hopper", "Ada Lovelace" }, commits.Select(c => c.Author));
        Assert.Equal(new[] { "3d", "6d", "9d", "16d" }, commits.Select(c => c.AgeText));
        Assert.All(commits, c => Assert.Equal(7, c.ShortSha.Length));
        Assert.All(commits, c => Assert.False(c.IsCurrent));
        Assert.Equal(("+25", "\u22120"), (commits[1].AddedText, commits[1].RemovedText));
        Assert.StartsWith("+", commits[2].AddedText, StringComparison.Ordinal);
        Assert.NotEqual("\u22120", commits[2].RemovedText);

        // On screen, under the link.
        var entries = ChoiceButtons(window);
        Assert.Equal(5, entries.Count);
        var popup = TopLevel.GetTopLevel(entries[0])!;
        Assert.True(entries[0].TranslatePoint(default, popup)!.Value.Y > link.TranslatePoint(default, popup)!.Value.Y);
        Assert.Contains(entries[3].GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Trim the renderer");
        UiSnapshot.CaptureTopLevel(popup, "commit-picker");

        // Pick "Trim the renderer": the tree and the diff show only that commit.
        Click(popup, entries[3]);
        var picked = commits[2];
        WaitUntil(() => vm.Analysis.AppliedCommit?.Sha == picked.Sha && !vm.Analysis.IsScanning, "the commit scope");
        Assert.False(window.CommitsFlyout.IsOpen);
        Assert.True(vm.Analysis.IsCommitPeriod);
        Assert.Equal(new[] { "AnsiRenderer.cs", "CliOptions.cs" }, Files(vm).OrderBy(n => n));
        WaitUntil(() => vm.Diff.Subtitle.StartsWith("commit " + picked.ShortSha, StringComparison.Ordinal) && !vm.Diff.IsLoading, "the diff of the commit");
        Assert.Equal("CliOptions.cs", vm.Diff.FileName);
        Assert.Contains(" @ " + picked.ShortSha, vm.WindowTitle, StringComparison.Ordinal);
        Assert.Equal("1\u00A0commit", vm.Analysis.SummaryCommits);
        UiSnapshot.Capture(window, "commit-picker-picked");

        // The list still shows the period, the picked commit marked.
        Click(window, link);
        WaitUntil(() => window.CommitsFlyout.IsOpen, "the commit list again");
        Assert.Equal(5, choices.Count);
        Assert.False(choices[0].IsCurrent);
        Assert.Equal(picked.Sha, choices.Single(c => c.IsCurrent).Sha);
        Assert.Equal("Commits - Last month", vm.Analysis.CommitChoicesTitle);

        // Another commit, then All: back to the period.
        var loader = ChoiceButtons(window)[2];
        Click(TopLevel.GetTopLevel(loader)!, loader);
        WaitUntil(() => vm.Analysis.AppliedCommit?.Sha == commits[1].Sha && !vm.Analysis.IsScanning, "the second commit");
        Assert.Equal(new[] { "DiffLoader.cs" }, Files(vm));

        Click(window, link);
        WaitUntil(() => window.CommitsFlyout.IsOpen, "the commit list a third time");
        var all = ChoiceButtons(window)[0];
        Click(TopLevel.GetTopLevel(all)!, all);
        WaitUntil(() => vm.Analysis.AppliedCommit is null && !vm.Analysis.IsScanning && vm.Analysis.SummaryCommits == "4\u00A0commits", "the period again");
        Assert.Contains("design.md", Files(vm));
        Assert.Equal("Last month", vm.Analysis.SelectedPeriod.Label);
        WaitUntil(() => vm.Diff.Subtitle.StartsWith("period start", StringComparison.Ordinal) && !vm.Diff.IsLoading, "the period's diff");

        // Picking the commit shown again also goes back.
        Click(window, link);
        WaitUntil(() => window.CommitsFlyout.IsOpen, "the commit list a fourth time");
        var trim = ChoiceButtons(window)[3];
        Click(TopLevel.GetTopLevel(trim)!, trim);
        WaitUntil(() => vm.Analysis.AppliedCommit?.Sha == picked.Sha && !vm.Analysis.IsScanning, "the commit once more");
        Click(window, link);
        WaitUntil(() => window.CommitsFlyout.IsOpen, "the commit list a fifth time");
        var again = ChoiceButtons(window).Single(b => b.DataContext is CommitChoice { IsCurrent: true, IsAll: false });
        Click(TopLevel.GetTopLevel(again)!, again);
        WaitUntil(() => vm.Analysis.AppliedCommit is null && !vm.Analysis.IsScanning && vm.Analysis.SummaryCommits == "4\u00A0commits", "the period after re-picking");

        window.Close();
    }

    private static System.Collections.Generic.List<Button> ChoiceButtons(MainWindow window)
    {
        UiSnapshot.Pump();
        var list = (ItemsControl)((Control)window.CommitsFlyout.Content!).GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "CommitChoicesList");
        return list.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("commitChoice"))
            .OrderBy(b => list.Items.IndexOf(b.DataContext)).ToList();
    }

    private static string[] Files(MainWindowViewModel vm)
        => vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).Where(r => r.IsFile).Select(r => r.Name).ToArray();

    private static void Open(MainWindow window, string name)
    {
        var vm = window.ViewModel;
        var row = vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).Single(r => r.IsFile && r.Name == name);
        _ = vm.Analysis.OpenAsync(row);
        WaitUntil(() => vm.IsDiffOpen && !vm.IsFolderSummary && !vm.Diff.IsLoading && !vm.Diff.IsPlaceholder && vm.Diff.FileName == name, name);
    }

    private static void Click(TopLevel top, Control control)
    {
        UiSnapshot.Pump();
        var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
        top.MouseDown(at, MouseButton.Left);
        top.MouseUp(at, MouseButton.Left);
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
