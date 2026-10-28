using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Budiff.App;
using Budiff.Git;
using Xunit;

namespace Budiff.Avalonia.Tests;

// A click on a blame cell opens the commit that last touched the line: first
// a small window (message on the left, changed files on the right), which
// expands into a full budiff window scoped to that commit.
public sealed class CommitWindowTests
{
    [AvaloniaFact]
    public void BlameClick_OpensTheCommit_ThenExpandsToAFullWindow()
    {
        var root = ReadmeFixture.BuildFixtureRepo("budiff-commit-window");
        var window = new MainWindow(root, gitRoot: true) { Width = 1100, Height = 800 };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");

        var readme = vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).Single(r => r.IsFile && r.Name == "README.md");
        _ = vm.Analysis.OpenAsync(readme);
        WaitUntil(() => vm.IsDiffOpen && !vm.Diff.IsPlaceholder && vm.Diff.IsBlameVisible && vm.Diff.Rows.Any(r => r.Blame is not null), "README.md with blame");
        UiSnapshot.Pump();

        // The old side of README is blamed on the first commit (its top row;
        // rows are not in screen order in the tree).
        var cell = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Classes.Contains("blameCell") && t.IsEffectivelyVisible && t.DataContext is DiffRowVm { Blame: not null })
            .OrderBy(t => t.TranslatePoint(default, window)?.Y)
            .First();
        Assert.Equal(StandardCursorType.Hand, cell.Cursor?.ToString() is { } c && c.Contains("Hand") ? StandardCursorType.Hand : StandardCursorType.Arrow);
        var at = cell.TranslatePoint(new Point(10, 8), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);

        WaitUntil(() => window.OwnedWindows.OfType<CommitWindow>().Any(), "the commit window");
        var small = window.OwnedWindows.OfType<CommitWindow>().Single();
        WaitUntil(() => small.ViewModel.Files.Count > 0, "the commit's files");
        UiSnapshot.Pump();
        UiSnapshot.Pump();
        UiSnapshot.Capture(small, "commit-window");

        var commit = small.ViewModel.Commit;
        Assert.Equal("Scaffold the project", commit.Subject);
        Assert.Equal(0, commit.ParentCount);
        Assert.Contains("First commit", small.ViewModel.ParentText, StringComparison.Ordinal);
        Assert.StartsWith("8 files changed", small.ViewModel.FilesSummary, StringComparison.Ordinal);
        Assert.True(small.Width < 900 && small.Height < 600, "the commit window starts small");

        // Folders first ("src" holds two projects, so it stays one level), then files.
        var top = small.ViewModel.Files;
        Assert.Equal(new[] { "src", "budiff.json", "build.ps1", "README.md" }, top.Select(n => n.Name));
        Assert.Equal(new[] { "Budiff.Cli", "Budiff.Core" }, top[0].Children.Select(n => n.Name));
        var program = top[0].Children[0].Children.Single(n => n.Name == "Program.cs");
        Assert.StartsWith("+", program.AddedText, StringComparison.Ordinal);
        Assert.Equal("new", program.StateText);

        // Expand with a file: the same (whole repository) folder, scoped to the commit.
        var full = (MainWindow)small.Expand("src/Budiff.Cli/Program.cs");
        var fvm = full.ViewModel;
        WaitUntil(() => fvm.Analysis.AppliedCommit is not null && !fvm.Analysis.IsScanning, "the commit scope");
        WaitUntil(() => fvm.IsDiffOpen && !fvm.Diff.IsLoading && !fvm.Diff.IsPlaceholder && fvm.Diff.FileName == "Program.cs", "Program.cs in the full window");
        UiSnapshot.Pump();
        UiSnapshot.Pump();
        UiSnapshot.Capture(full, "commit-window-expanded");

        Assert.False(small.IsVisible);
        Assert.Equal(commit.Sha, fvm.Analysis.AppliedCommit!.Sha);
        Assert.True(fvm.Analysis.IsCommitPeriod);
        Assert.StartsWith("Commit " + commit.ShortSha, fvm.StatusText, StringComparison.Ordinal);
        Assert.Contains("first commit", fvm.Diff.Subtitle, StringComparison.Ordinal);
        Assert.Contains(" @ " + commit.ShortSha, fvm.WindowTitle, StringComparison.Ordinal);
        Assert.Equal(8, fvm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).Count(r => r.IsFile));
        Assert.False(fvm.ShowCompareOptions);

        // Only the commit was read: the working tree was never scanned.
        Assert.False(fvm.Analysis.HasFolderScan);
        Assert.Equal("8 files changed   +156", small.ViewModel.FilesSummary);

        // A time period leaves the commit; picking "Commit" again comes back.
        fvm.Analysis.SelectedPeriod = fvm.Analysis.Periods.First(p => p.Days == 30);
        WaitUntil(() => fvm.Analysis.AppliedCommit is null && !fvm.Analysis.IsScanning, "the period");
        Assert.DoesNotContain(" @ ", fvm.WindowTitle, StringComparison.Ordinal);
        Assert.True(fvm.Analysis.HasFolderScan);
        Assert.Contains(fvm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()), r => r.IsFile && r.Name == "design.md");
        Assert.False(fvm.Analysis.IsCommitPeriod);
        fvm.Analysis.SelectedPeriod = fvm.Analysis.Periods.Single(p => p.IsCommit);
        WaitUntil(() => fvm.Analysis.AppliedCommit is not null && !fvm.Analysis.IsScanning, "the commit again");
        Assert.Contains(" @ " + commit.ShortSha, fvm.WindowTitle, StringComparison.Ordinal);

        full.Close();
        window.Close();
    }

    // Blame names who last changed each line as it is NOW, so the newest blame
    // matches the file's Recency; the gutter's tint is hottest there.
    [AvaloniaFact]
    public void Blame_FollowsTheNewSide_AndMatchesRecency()
    {
        var root = ReadmeFixture.BuildFixtureRepo("budiff-blame-recency");
        var window = new MainWindow(root, gitRoot: true) { Width = 1100, Height = 800 };
        window.Show();
        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");

        var readme = vm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).Single(r => r.IsFile && r.Name == "README.md");
        Assert.Equal("3d", readme.RecencyText);
        _ = vm.Analysis.OpenAsync(readme);
        WaitUntil(() => vm.IsDiffOpen && !vm.Diff.IsPlaceholder && vm.Diff.Rows.Any(r => r.Blame is not null), "README.md with blame");

        // The line the last commit added (green, new side only) is blamed on it.
        var added = vm.Diff.Rows.Single(r => r.NewText?.Contains("--gitroot", StringComparison.Ordinal) == true);
        Assert.NotNull(added.Blame);
        Assert.EndsWith(" 3d", added.Blame!.Label, StringComparison.Ordinal);
        Assert.Equal("Simplify the line diff", added.Blame.Message);
        Assert.Same(BlameHeat.Palette[^1], added.Blame.Shade);

        // A line untouched since the first commit: older, and the faintest.
        var title = vm.Diff.Rows.First(r => r.NewText == "# budiff");
        Assert.EndsWith(" 1mo", title.Blame!.Label, StringComparison.Ordinal);
        Assert.Same(BlameHeat.Palette[0], title.Blame.Shade);

        UiSnapshot.Pump();
        UiSnapshot.Capture(window, "blame-heat");

        // Every label shows in full: no cell trims.
        var cells = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Classes.Contains("blameCell") && t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)).ToList();
        Assert.NotEmpty(cells);
        Assert.All(cells, c => Assert.True(c.TextLayout.TextLines.All(l => !l.HasCollapsed), "trimmed: " + c.Text));
        window.Close();
    }

    // The scale is the file's own: each distinct age gets its own shade,
    // newest strongest, however close together the ages are.
    [Fact]
    public void BlameHeat_RanksTheFilesOwnAges()
    {
        var now = DateTimeOffset.Now;
        BlameLineInfo Line(string sha, TimeSpan ago) => new(sha, "Ada", "ada@example.com", now - ago, "m");
        var old = Line("aaaaaaa", TimeSpan.FromDays(90));
        var hours = Line("bbbbbbb", TimeSpan.FromHours(4));
        var fresh = Line("ccccccc", TimeSpan.FromSeconds(20));
        var alsoFresh = Line("ddddddd", TimeSpan.FromSeconds(50)); // reads "<1min" too
        string Age(BlameLineInfo l) => CodeAnalysisPanelViewModel.Age(l.When.ToUnixTimeSeconds(), now);
        var heat = BlameHeat.For(new BlameLineInfo?[] { old, hours, fresh, hours, alsoFresh }, Age);

        Assert.Same(BlameHeat.Palette[0], heat(old));
        Assert.Same(BlameHeat.Palette[^1], heat(fresh));
        Assert.Same(heat(fresh), heat(alsoFresh));
        var middle = heat(hours);
        Assert.NotSame(heat(old), middle);
        Assert.NotSame(heat(fresh), middle);

        // Every line the same age: nothing to tell apart, so no tint.
        var flat = BlameHeat.For(new BlameLineInfo?[] { fresh, alsoFresh }, Age);
        Assert.Null(flat(fresh));
    }

    // Opened on a folder: the small window lists the commit's files in that
    // folder, and the expanded window shows exactly those - not the whole
    // repository's.
    [AvaloniaFact]
    public void Expand_KeepsTheFolderScope()
    {
        var root = ReadmeFixture.BuildFixtureRepo("budiff-commit-folder");
        var scope = GitRepositoryReader.Open(System.IO.Path.Combine(root, "src"), gitRoot: false);
        Assert.False(scope.IsGitRootScope);
        var first = GitRepositoryReader.ReadCommit(root, "HEAD")!;
        while (first.ParentSha is { } parent)
        {
            first = GitRepositoryReader.ReadCommit(root, parent)!;
        }

        Assert.Equal("Scaffold the project", first.Subject);

        var small = new CommitWindow(new CommitWindowViewModel(scope, first));
        small.Show();
        WaitUntil(() => small.ViewModel.Files.Count > 0, "the commit's files");
        var listed = Flatten(small.ViewModel.Files).Count(n => n.File is not null);
        Assert.Equal(5, listed);

        var full = (MainWindow)small.Expand();
        var fvm = full.ViewModel;
        WaitUntil(() => fvm.Analysis.AppliedCommit is not null && !fvm.Analysis.IsScanning && fvm.Analysis.Roots.Count > 0, "the commit scope");
        var files = fvm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).Where(r => r.IsFile).Select(r => r.Name).ToList();
        Assert.Equal(listed, files.Count);
        Assert.DoesNotContain("README.md", files);

        // Every folder of the commit opens expanded.
        WaitUntil(() => fvm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).Where(r => !r.IsFile).All(r => r.IsExpanded), "all folders expanded");
        full.Close();
    }

    // Double-clicking a folder opens the full window with that folder
    // expanded and selected; no file opens.
    [AvaloniaFact]
    public void DoubleClickedFolder_IsRevealedInTheFullWindow()
    {
        var root = ReadmeFixture.BuildFixtureRepo("budiff-commit-reveal");
        var scope = GitRepositoryReader.Open(root, gitRoot: true);
        var first = GitRepositoryReader.ReadCommit(root, "HEAD")!;
        while (first.ParentSha is { } parent)
        {
            first = GitRepositoryReader.ReadCommit(root, parent)!;
        }

        var small = new CommitWindow(new CommitWindowViewModel(scope, first)) { Width = 840, Height = 480 };
        string? opened = null;
        MainWindow? full = null;
        small.CreateFullWindow = (commit, path) =>
        {
            opened = path;
            return full = new MainWindow(root, gitRoot: true, settings: null, commit, path);
        };
        small.Show();
        WaitUntil(() => small.ViewModel.Files.Count > 0, "the commit's files");
        UiSnapshot.Pump();
        UiSnapshot.Pump();

        var cliRow = small.GetVisualDescendants().OfType<TreeViewItem>()
            .First(i => i.DataContext is CommitFileNode { Name: "Budiff.Cli" });
        var at = cliRow.TranslatePoint(new Point(60, 10), small)!.Value;
        small.MouseDown(at, MouseButton.Left);
        small.MouseUp(at, MouseButton.Left);
        small.MouseDown(at, MouseButton.Left); // the second press is the double-click: the window closes
        Assert.Equal("src/Budiff.Cli", opened);

        var fvm = full!.ViewModel;
        WaitUntil(() => fvm.Analysis.AppliedCommit is not null && !fvm.Analysis.IsScanning && fvm.Analysis.Roots.Count > 0, "the commit scope");
        var rows = fvm.Analysis.Roots.SelectMany(r => r.SelfAndDescendants()).ToList();
        var cli = rows.Single(r => r.RelativePath == "src/Budiff.Cli");
        Assert.True(cli.IsExpanded);
        Assert.True(rows.Single(r => r.RelativePath == "src").IsExpanded);
        Assert.Same(cli, full.FindControl<TreeView>("AnalysisTree")!.SelectedItem);
        Assert.False(fvm.IsDiffOpen);
        full.Close();
    }

    // The full window opens maximized when the window it came from is.
    [AvaloniaTheory]
    [InlineData(WindowState.Maximized)]
    [InlineData(WindowState.Normal)]
    public void Expand_FollowsTheOwnersMaximizedState(WindowState ownerState)
    {
        var root = ReadmeFixture.BuildFixtureRepo("budiff-commit-maximized-" + ownerState);
        var owner = new Window { WindowState = ownerState };
        owner.Show();
        var commit = GitRepositoryReader.ReadCommit(root, "HEAD")!;
        var small = CommitWindow.Open(owner, GitRepositoryReader.Open(root, gitRoot: true), commit);
        var full = small.Expand();
        Assert.Equal(ownerState, full.WindowState);
        full.Close();
        owner.Close();
    }

    // Esc closes the small window - also with focus in its file tree - but
    // not the full window it expands into.
    [AvaloniaFact]
    public void Escape_ClosesTheSmallWindowOnly()
    {
        var root = ReadmeFixture.BuildFixtureRepo("budiff-commit-escape");
        var owner = new Window();
        owner.Show();
        var scope = GitRepositoryReader.Open(root, gitRoot: true);
        var commit = GitRepositoryReader.ReadCommit(root, "HEAD")!;

        var small = CommitWindow.Open(owner, scope, commit);
        WaitUntil(() => small.ViewModel.Files.Count > 0, "the commit's files");
        var closed = false;
        small.Closed += (_, _) => closed = true;
        small.FindControl<TreeView>("FileTree")!.Focus();
        PressEscape(small);
        Assert.True(closed);

        var full = CommitWindow.Open(owner, scope, commit).Expand();
        PressEscape(full);
        Assert.True(full.IsVisible);
        full.Close();
        owner.Close();
    }

    private static void PressEscape(Window window)
    {
        var open = true;
        window.Closed += (_, _) => open = false;
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

        // A window closed by the key press takes no key release.
        if (open)
        {
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        }

        UiSnapshot.Pump();
    }

    // "Co-authored-by: Name <email>" lines read as people, not wrapped text.
    [Fact]
    public void Trailers_AreSplitFromTheBody()
    {
        var (body, trailers) = CommitWindowViewModel.SplitTrailers(
            "Why it changed.\n\nMore detail.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>\nSigned-off-by: Ada <ada@example.com>");
        Assert.Equal("Why it changed.\n\nMore detail.", body);
        Assert.Equal(new[] { "Co-authored by", "Signed-off by" }, trailers.Select(t => t.Label));
        Assert.Equal("Claude Opus 5.5", trailers[0].Name);
        Assert.Equal("<noreply@anthropic.com>", trailers[0].EmailText);

        // A last paragraph that is not all trailers stays in the body.
        var plain = CommitWindowViewModel.SplitTrailers("Text.\n\nSee: the docs <here>\nand more");
        Assert.Empty(plain.Trailers);
        Assert.Equal("Text.\n\nSee: the docs <here>\nand more", plain.Body);
    }

    // A hard-wrapped message reflows: paragraph lines join, list items keep
    // their own lines with indented continuations folded in, code stays put.
    [Fact]
    public void MessageBody_Reflows()
    {
        var blocks = CommitWindowViewModel.Reflow(
            "Blame now sits in its own column\nbeside the code.\n\n- Auto-hide never fired: VisualRoot\n  is a TopLevelHost.\n  nested detail\n  - Sub point\n* Star item\n1. Numbered\n\n    var x = 1;\n    var y = 2;");
        Assert.Equal(new[]
        {
            "Blame now sits in its own column beside the code.",
            "Auto-hide never fired: VisualRoot is a TopLevelHost. nested detail",
            "Sub point",
            "Star item",
            "Numbered",
            "    var x = 1;\n    var y = 2;",
        }, blocks.Select(b => b.Text));
        Assert.Equal(new string?[] { null, "•", "•", "•", "1.", null }, blocks.Select(b => b.Bullet));
        Assert.Equal(2, blocks[2].Indent);
        Assert.True(blocks[5].IsCode);
        Assert.Equal(new[] { false, true, false, false, false, true }, blocks.Select(b => b.AfterBlank));
    }

    // A long hard-wrapped message renders as list items with hanging indents.
    [AvaloniaFact]
    public void LongMessage_RendersReflowed()
    {
        var root = ReadmeFixture.BuildFixtureRepo("budiff-commit-message");
        var scope = GitRepositoryReader.Open(root, gitRoot: true);
        var commit = GitRepositoryReader.ReadCommit(root, "HEAD")! with
        {
            Message = "Blame gets its own column\n\nThe blame cells used to tint the code; they now sit in their own\ncolumn with a neutral background. The\n  funnel gutter gained a BlameWidth offset so it still sits exactly\n  between the code halves.\n\n- Auto-hide never fired: on Avalonia 12 VisualRoot is a TopLevelHost,\n  NOT the Window, so the \"half the window\" check always saw width 0.\n  Resolved via TopLevel.GetTopLevel.\n- Tooltip now includes the author's email (BlameLineInfo/BlameEntry).\n  - Nested: the floating window's \"Dock back\" is an icon button.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>",
        };
        var small = new CommitWindow(new CommitWindowViewModel(scope, commit)) { Width = 760, Height = 520 };
        small.Show();
        WaitUntil(() => small.ViewModel.Files.Count > 0, "the commit's files");
        UiSnapshot.Pump();
        UiSnapshot.Capture(small, "commit-window-message");
        Assert.Equal(4, small.ViewModel.Paragraphs.Count);
        small.Close();
    }

    // A commit window always shows what the commit added and removed, and
    // its column choices are its own: never saved over the user's settings.
    [AvaloniaFact]
    public void CommitWindow_ShowsChurn_AndKeepsItsColumnsToItself()
    {
        var root = ReadmeFixture.BuildFixtureRepo("budiff-commit-columns");
        var settingsDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "budiff-commit-columns-settings-" + Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(settingsDir, "settings.json");
        var settings = AppSettings.Load(path);
        settings.HiddenColumns.UnionWith(new[] { "Added", "Removed", "Size" });
        settings.Save();
        var saved = System.IO.File.ReadAllText(path);

        var commit = GitRepositoryReader.ReadCommit(root, "HEAD")!;
        var vm = new MainWindowViewModel(root, gitRoot: true, AppSettings.Load(path), commit);
        Assert.True(vm.ShowAddedColumn);
        Assert.True(vm.ShowRemovedColumn);
        Assert.False(vm.ColumnOptions.Single(o => o.Name == "Size").IsOn);

        // Changing its columns leaves the user's settings as they were.
        vm.ColumnOptions.Single(o => o.Name == "Lines").IsOn = false;
        vm.ColumnOptions.Single(o => o.Name == "Size").IsOn = true;
        Assert.Equal(saved, System.IO.File.ReadAllText(path));

        // A normal window still reads (and keeps) the user's own choices.
        var normal = new MainWindowViewModel(root, gitRoot: true, AppSettings.Load(path));
        Assert.False(normal.ShowAddedColumn);
        Assert.True(normal.ColumnOptions.Single(o => o.Name == "Lines").IsOn);
        System.IO.Directory.Delete(settingsDir, recursive: true);
    }

    private static System.Collections.Generic.IEnumerable<CommitFileNode> Flatten(System.Collections.Generic.IEnumerable<CommitFileNode> nodes)
        => nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));

    // A commit with a parent: shown against it, only its own files.
    [AvaloniaFact]
    public async Task Commit_WithAParent_ListsOnlyItsFiles()
    {
        var root = ReadmeFixture.BuildFixtureRepo("budiff-commit-parent");
        var vm = new MainWindowViewModel(root, gitRoot: true);
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");
        CommitDetails? shown = null;
        vm.ShowCommit = (_, commit) => shown = commit;

        await vm.OpenCommitAsync("HEAD");
        Assert.NotNull(shown);
        Assert.Equal("Simplify the line diff", shown!.Subject);
        Assert.Equal(1, shown.ParentCount);

        var scope = GitRepositoryReader.Open(root, gitRoot: true);
        var model = new CommitWindowViewModel(scope, shown);
        await model.LoadAsync();
        Assert.StartsWith("Parent ", model.ParentText, StringComparison.Ordinal);
        Assert.StartsWith("2 files changed", model.FilesSummary, StringComparison.Ordinal);

        // One folder chain, compacted into one row.
        Assert.Equal(new[] { "src/Budiff.Core", "README.md" }, model.Files.Select(n => n.Name));
        Assert.Equal("LineDiff.cs", model.Files[0].Children.Single().Name);
        Assert.Equal(string.Empty, model.Files[1].StateText);

        // Not a commit: nothing opens, the status says why.
        shown = null;
        await vm.OpenCommitAsync("0123abc");
        Assert.Null(shown);
        Assert.Contains("Commit not found", vm.StatusText, StringComparison.Ordinal);
    }

    // The commit's PR shows in the header, with its state as the PR picker
    // colours it; a click opens it in the browser. Any state counts: an open
    // PR the commit is still waiting in too.
    [AvaloniaTheory]
    [InlineData("MERGED", false, "Merged")]
    [InlineData("OPEN", false, "Open")]
    [InlineData("OPEN", true, "Draft")]
    [InlineData("CLOSED", false, "Closed")]
    public void PullRequest_ShowsInTheHeader_WithItsState(string state, bool draft, string label)
    {
        var root = ReadmeFixture.BuildFixtureRepo("budiff-commit-pr-" + label.ToLowerInvariant());
        var scope = GitRepositoryReader.Open(root, gitRoot: true);
        var commit = GitRepositoryReader.ReadCommit(root, "HEAD")!;
        var pr = Pr(105, state, draft, "Blame click opens the commit (small window, expandable); Recency column");
        var api = new FakeCommitPulls(commit.Sha, pr);
        var small = new CommitWindow(new CommitWindowViewModel(scope, commit, api)) { Width = 840, Height = 480 };
        Uri? opened = null;
        small.OpenExternal = (_, uri) =>
        {
            opened = uri;
            return Task.CompletedTask;
        };
        small.Show();
        WaitUntil(() => small.ViewModel.HasPullRequest && small.ViewModel.Files.Count > 0, "the commit's PR");
        UiSnapshot.Pump();
        UiSnapshot.Capture(small, "commit-window-pr-" + label.ToLowerInvariant());

        // Just the number; the colour says the state, the tooltip spells it out.
        var pill = small.FindControl<Border>("PullRequestPill")!;
        Assert.True(pill.IsEffectivelyVisible);
        Assert.Equal("#105", ((TextBlock)pill.Child!).Text);
        Assert.Same(PullRequestRowVm.BrushFor(label), pill.Background);
        Assert.StartsWith(label + " - " + pr.Title, (string)ToolTip.GetTip(pill)!, StringComparison.Ordinal);

        var at = pill.TranslatePoint(new Point(pill.Bounds.Width / 2, pill.Bounds.Height / 2), small)!.Value;
        small.MouseDown(at, MouseButton.Left);
        small.MouseUp(at, MouseButton.Left);
        UiSnapshot.Pump();
        Assert.Equal(pr.Url, opened?.ToString());
        small.Close();
    }

    // No PR (a direct push), or gh failing: the header shows none.
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void PullRequest_None_ShowsNothing(bool fails)
    {
        var root = ReadmeFixture.BuildFixtureRepo("budiff-commit-nopr-" + fails);
        var scope = GitRepositoryReader.Open(root, gitRoot: true);
        var commit = GitRepositoryReader.ReadCommit(root, "HEAD")!;
        var api = new FakeCommitPulls(commit.Sha, null) { Fails = fails };
        var small = new CommitWindow(new CommitWindowViewModel(scope, commit, api));
        small.Show();
        WaitUntil(() => small.ViewModel.Files.Count > 0 && api.Asked, "the commit's files");
        UiSnapshot.Pump();
        Assert.False(small.ViewModel.HasPullRequest);
        Assert.False(small.FindControl<Border>("PullRequestPill")!.IsEffectivelyVisible);
        small.Close();
    }

    // The PR that landed the commit wins; else an open one; else the latest.
    [Fact]
    public void PullRequest_Pick_PrefersMergedThenOpen()
    {
        var closed = Pr(1, "CLOSED", false, "old attempt") with { UpdatedAt = DateTimeOffset.Now };
        var open = Pr(2, "OPEN", false, "still open") with { UpdatedAt = DateTimeOffset.Now.AddDays(-3) };
        var merged = Pr(3, "MERGED", false, "landed") with { UpdatedAt = DateTimeOffset.Now.AddDays(-9) };
        Assert.Same(merged, CommitWindowViewModel.Pick(new[] { closed, open, merged }));
        Assert.Same(open, CommitWindowViewModel.Pick(new[] { closed, open }));
        Assert.Same(closed, CommitWindowViewModel.Pick(new[] { closed }));
        Assert.Null(CommitWindowViewModel.Pick(Array.Empty<PullRequestInfo>()));
    }

    private static PullRequestInfo Pr(int number, string state, bool draft, string title)
        => new(number, title, state, draft, "ada", DateTimeOffset.Now, "main", "feature", new string('a', 40), new string('b', 40), null, null, null, "https://github.com/octo/repo/pull/" + number);

    // Answers PullRequestsOf for one commit; nothing else is asked.
    private sealed class FakeCommitPulls(string sha, PullRequestInfo? pr) : IPullRequestApi
    {
        public bool Fails { get; init; }

        public volatile bool Asked;

        public IReadOnlyDictionary<string, IReadOnlyList<PullRequestInfo>> PullRequestsOf(IReadOnlyList<string> commits, CancellationToken ct)
        {
            Asked = true;
            if (Fails)
            {
                throw new PullRequestSourceException("gh not found");
            }

            Assert.Equal(new[] { sha }, commits);
            return new Dictionary<string, IReadOnlyList<PullRequestInfo>>
            {
                [sha] = pr is null ? Array.Empty<PullRequestInfo>() : new[] { pr },
            };
        }

        public PullRequestPage Search(PullRequestQuery query, string? after, int pageSize, CancellationToken ct) => throw new NotSupportedException();

        public IReadOnlyDictionary<int, IReadOnlyList<string>?> FilesOf(IReadOnlyList<int> numbers, CancellationToken ct) => throw new NotSupportedException();

        public string HistoryRef(CancellationToken ct) => throw new NotSupportedException();
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
