using Budiff.Git;
using Xunit;

namespace Budiff.Cli.Tests;

public sealed class GitHistoryTests
{
    private const char Sep = (char)0x1f;

    [Fact]
    public void Parse_aggregates_added_removed_commits_and_authors_per_path()
    {
        var log = string.Join("\n", new[]
        {
            Commit("c0", "Alice"),
            "10\t0\tsrc/App/Main.cs",
            "5\t1\tsrc/App/Util.cs",
            Commit("c1", "Bob"),
            "3\t2\tsrc/App/Main.cs",
            Commit("c2", "Alice"),
            "-\t-\tassets/logo.png",
        });

        var result = GitHistory.Parse(log);

        Assert.Equal(3, result.CommitCount);
        var main = result.ByPath["src/App/Main.cs"];
        Assert.Equal(13, main.Added);
        Assert.Equal(2, main.Removed);
        Assert.Equal(2, main.Commits.Count);
        Assert.Equal(2, main.Authors.Count);
        Assert.Equal(0, result.ByPath["assets/logo.png"].Added);
    }

    [Theory]
    [InlineData("old.cs => new.cs", "new.cs")]
    [InlineData("src/{old => new}/file.cs", "src/new/file.cs")]
    [InlineData("src/{ => sub}/file.cs", "src/sub/file.cs")]
    [InlineData("plain/path.cs", "plain/path.cs")]
    public void ResolveRenamePath_returns_current_path(string input, string expected)
    {
        Assert.Equal(expected, GitHistory.ResolveRenamePath(input));
    }

    private static string Commit(string sha, string author) => $"{Sep}{sha}{Sep}{author}";
}
