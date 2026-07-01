using System.IO;
using System.Linq;
using Budiff;
using Xunit;

namespace Budiff.Cli.Tests;

public sealed class UnifiedDiffParserTests
{
    [Fact]
    public void Parse_ConvertsUnifiedHunkToSideBySideRows()
    {
        var files = UnifiedDiffParser.Parse(string.Join('\n', new[]
        {
            "diff --git a/src/App.cs b/src/App.cs",
            "index 1111111..2222222 100644",
            "--- a/src/App.cs",
            "+++ b/src/App.cs",
            "@@ -1,3 +1,4 @@",
            " line one",
            "-old value",
            "+new value",
            "+extra value",
            " line three",
        }));

        var file = Assert.Single(files);
        Assert.Equal("src/App.cs", file.File.Path);
        Assert.Equal(new DiffChurn(2, 1), file.File.Churn);
        Assert.Collection(
            file.Diff.Rows!,
            row => Assert.Equal(DiffKind.Unchanged, row.Kind),
            row =>
            {
                Assert.Equal(DiffKind.Modified, row.Kind);
                Assert.Equal("old value", row.OldText);
                Assert.Equal("new value", row.NewText);
            },
            row =>
            {
                Assert.Equal(DiffKind.Added, row.Kind);
                Assert.Equal("extra value", row.NewText);
            },
            row => Assert.Equal(DiffKind.Unchanged, row.Kind));
    }

    [Fact]
    public void Parse_DetectsAddedDeletedAndRenamedFiles()
    {
        var files = UnifiedDiffParser.Parse(string.Join('\n', new[]
        {
            "diff --git a/added.txt b/added.txt",
            "new file mode 100644",
            "--- /dev/null",
            "+++ b/added.txt",
            "@@ -0,0 +1 @@",
            "+added",
            "diff --git a/deleted.txt b/deleted.txt",
            "deleted file mode 100644",
            "--- a/deleted.txt",
            "+++ /dev/null",
            "@@ -1 +0,0 @@",
            "-deleted",
            "diff --git a/old.txt b/new.txt",
            "similarity index 80%",
            "rename from old.txt",
            "rename to new.txt",
            "--- a/old.txt",
            "+++ b/new.txt",
            "@@ -1 +1 @@",
            "-old",
            "+new",
        }));

        Assert.Equal(GitFileState.Added, files[0].File.State);
        Assert.Equal(GitFileState.Deleted, files[1].File.State);
        Assert.Equal(GitFileState.Renamed, files[2].File.State);
        Assert.Equal("old.txt", files[2].File.OldPath);
        Assert.Equal("new.txt", files[2].File.Path);
    }

    [Fact]
    public void Parse_KeepsBinaryAndMetadataOnlyFiles()
    {
        var files = UnifiedDiffParser.Parse(string.Join('\n', new[]
        {
            "diff --git a/logo.png b/logo.png",
            "Binary files a/logo.png and b/logo.png differ",
            "diff --git a/old.txt b/new.txt",
            "similarity index 100%",
            "rename from old.txt",
            "rename to new.txt",
        }));

        Assert.Equal(2, files.Count);
        Assert.True(files[0].File.IsBinary);
        Assert.Equal("Binary file not shown.", files[0].Diff.Placeholder);
        Assert.Equal(GitFileState.Renamed, files[1].File.State);
        Assert.Equal("No text changes in unified diff.", files[1].Diff.Placeholder);
    }

    [Fact]
    public void Run_RendersRedirectedUnifiedDiffWithoutRepositoryAccess()
    {
        var input = new StringReader(string.Join('\n', new[]
        {
            "diff --git a/file.txt b/file.txt",
            "--- a/file.txt",
            "+++ b/file.txt",
            "@@ -1 +1 @@",
            "-before",
            "+after",
        }));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = BudiffCli.Run(Array.Empty<string>(), input, output, error, inputRedirected: true);

        Assert.Equal(0, exitCode);
        Assert.Contains("file.txt", output.ToString());
        Assert.Contains("before", output.ToString());
        Assert.Contains("after", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_RejectsJsonForStdinDiffs()
    {
        var input = new StringReader(string.Join('\n', new[]
        {
            "diff --git a/file.txt b/file.txt",
            "--- a/file.txt",
            "+++ b/file.txt",
            "@@ -1 +1 @@",
            "-before",
            "+after",
        }));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = BudiffCli.Run(new[] { "--json" }, input, output, error, inputRedirected: true);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("--json is not supported for stdin diffs.", error.ToString());
    }
}
