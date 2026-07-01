using Budiff.CodeAnalysis;
using Xunit;

namespace Budiff.Core.Tests;

public sealed class CodeAnalysisTests
{
    [Fact]
    public void Counts_csharp_code_blank_comment()
    {
        var source =
            "// header comment\n" +
            "using System;\n" +
            "\n" +
            "/* a\n" +
            "   block */\n" +
            "var x = 1; // trailing\n" +
            "   \n";

        var counts = LineCounter.Count(source, CommentSyntax.For("Program.cs"));

        Assert.Equal(2, counts.Code);
        Assert.Equal(2, counts.Blank);
        Assert.Equal(3, counts.Comment);
        Assert.Equal(7, counts.Total);
    }

    [Fact]
    public void Block_comment_opened_and_closed_on_one_line_is_a_single_comment()
    {
        var counts = LineCounter.Count("/* one liner */\ncode();\n", CommentSyntax.For("a.cs"));

        Assert.Equal(1, counts.Code);
        Assert.Equal(1, counts.Comment);
        Assert.Equal(0, counts.Blank);
    }

    [Fact]
    public void LooksBinary_flags_a_nul_byte()
    {
        Assert.True(LineCounter.LooksBinary(new byte[] { 1, 2, 0, 3 }));
        Assert.False(LineCounter.LooksBinary(new byte[] { 65, 66, 67 }));
    }

    [Fact]
    public void Folders_grouping_builds_tree_with_correct_aggregates()
    {
        var root = AnalysisTreeBuilder.Build(Sample(), null, AnalysisGrouping.Folders, AnalysisSortKey.Lines, false);

        Assert.Equal(3, root.Children.Count);
        Assert.Equal(6, root.FileCount);
        Assert.Equal(215, root.Lines.Total);
        Assert.Equal("src", root.Children[0].Name);

        var app = Child(Child(root, "src"), "App");
        Assert.Equal(3, app.Children.Count);
        Assert.Equal("Main.cs", app.Children[0].Name);
    }

    [Fact]
    public void Projects_grouping_groups_by_marker_and_buckets_rest()
    {
        var root = AnalysisTreeBuilder.Build(Sample(), null, AnalysisGrouping.Projects, AnalysisSortKey.Lines, false);

        var names = root.Children.Select(c => c.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "(ungrouped)", "App", "web" }, names);
        Assert.Equal(6, CountFiles(root));
    }

    [Fact]
    public void Commits_roll_up_as_distinct_not_summed()
    {
        var history = new HistoryResult();
        Touch(history, 0, "Alice", "src/App/Main.cs", 10, 0);
        Touch(history, 0, "Alice", "src/App/Util.cs", 5, 0);
        Touch(history, 1, "Bob", "src/App/Main.cs", 3, 0);

        var root = AnalysisTreeBuilder.Build(Sample(), history, AnalysisGrouping.Projects, AnalysisSortKey.Commits, false);
        var app = Child(root, "App");

        Assert.Equal(2, app.CommitCount);
        Assert.Equal(2, app.AuthorCount);
        Assert.Equal(18, app.Churn);
    }

    [Fact]
    public void All_time_added_is_snapshot_and_removed_is_zero()
    {
        var history = new HistoryResult();
        Touch(history, 0, "Alice", "src/App/Main.cs", 10, 90);

        var root = AnalysisTreeBuilder.Build(Sample(), history, AnalysisGrouping.Folders, AnalysisSortKey.Lines, false, allTime: true);
        var main = Child(Child(Child(root, "src"), "App"), "Main.cs");

        Assert.Equal(100, main.Added);
        Assert.Equal(0, main.Removed);
        Assert.Equal(1, main.CommitCount);
    }

    [Fact]
    public void Author_filter_keeps_only_files_touched_by_selected_authors()
    {
        var history = new HistoryResult();
        Touch(history, 0, "Alice", "src/App/Main.cs", 10, 0);
        Touch(history, 1, "Bob", "src/App/Util.cs", 5, 0);

        var onlyAlice = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Alice" };
        var root = AnalysisTreeBuilder.Build(Sample(), history, AnalysisGrouping.Folders, AnalysisSortKey.Lines, false, includedAuthors: onlyAlice);

        Assert.Equal(1, CountFiles(root));
        Assert.Equal(new[] { "Main.cs" }, Child(Child(root, "src"), "App").Children.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void Extension_filter_does_not_collapse_project_grouping()
    {
        var only = new HashSet<string> { ".cs" };
        var root = AnalysisTreeBuilder.Build(Sample(), null, AnalysisGrouping.Projects, AnalysisSortKey.Lines, false, only);

        Assert.Equal(new[] { "App" }, root.Children.Select(c => c.Name).ToArray());
        Assert.Equal(2, Child(root, "App").FileCount);
    }

    [Fact]
    public void Sorting_by_added_orders_by_churn_not_snapshot()
    {
        var history = new HistoryResult();
        Touch(history, 0, "Alice", "src/App/Main.cs", 1, 0);
        Touch(history, 0, "Alice", "src/App/Util.cs", 90, 40);

        var byLines = AnalysisTreeBuilder.Build(Sample(), history, AnalysisGrouping.Folders, AnalysisSortKey.Lines, false);
        var byAdded = AnalysisTreeBuilder.Build(Sample(), history, AnalysisGrouping.Folders, AnalysisSortKey.Added, false);

        var appByLines = Child(Child(byLines, "src"), "App");
        var appByAdded = Child(Child(byAdded, "src"), "App");

        Assert.Equal("Main.cs", appByLines.Children[0].Name);
        Assert.Equal("Util.cs", appByAdded.Children[0].Name);
    }

    private static void Touch(HistoryResult history, int commit, string author, string path, long added, long removed)
    {
        history.CommitCount = Math.Max(history.CommitCount, commit + 1);
        history.AuthorCommits[author] = Math.Max(1, history.AuthorCommits.GetValueOrDefault(author));
        var item = history.ForPath(path);
        item.Added += added;
        item.Removed += removed;
        item.Commits.Add(commit);
        item.Authors.Add(author);
    }

    private static FileStat F(string path, int code, int blank = 0, int comment = 0)
    {
        var dot = path.LastIndexOf('.');
        var ext = dot < 0 ? string.Empty : path[dot..].ToLowerInvariant();
        return new FileStat { RelativePath = path, Extension = ext, Lines = new LineCounts(code, blank, comment) };
    }

    private static ScanResult Sample(bool hasGit = true) => new()
    {
        Root = "C:/repo",
        HasGit = hasGit,
        Files = new[]
        {
            F("src/App/Main.cs", 80, 10, 10),
            F("src/App/Util.cs", 45, 3, 2),
            F("src/App/App.csproj", 10),
            F("web/index.js", 28, 2),
            F("web/package.json", 5),
            F("README.md", 20),
        },
    };

    private static AnalysisNode Child(AnalysisNode parent, string name)
        => parent.Children.Single(c => c.Name == name);

    private static int CountFiles(AnalysisNode node)
        => node.IsFile ? 1 : node.Children.Sum(CountFiles);
}
