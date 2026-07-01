using System.IO;
using System.Text.Json;
using Budiff;
using Budiff.Git;
using Xunit;

namespace Budiff.Cli.Tests;

public sealed class JsonRendererTests
{
    [Fact]
    public void Parse_AcceptsJsonFlag()
    {
        var options = CliOptions.Parse(new[] { "--gitroot", "--json", "README.md" });

        Assert.True(options.GitRoot);
        Assert.True(options.Json);
        Assert.Equal("README.md", options.FilePath);
        Assert.Null(options.Error);
    }

    [Fact]
    public void RenderList_EmitsStructuredChangeList()
    {
        var output = new StringWriter();

        JsonRenderer.RenderList(MakeChangeSet(), output);

        using var doc = JsonDocument.Parse(output.ToString());
        var root = doc.RootElement;
        Assert.Equal("budiff.changeList", root.GetProperty("format").GetString());
        Assert.Equal("HEAD vs working tree", root.GetProperty("target").GetProperty("display").GetString());
        Assert.Equal("src/App.cs", root.GetProperty("files")[0].GetProperty("path").GetString());
        Assert.Equal(3, root.GetProperty("files")[0].GetProperty("churn").GetProperty("total").GetInt32());
    }

    [Fact]
    public void RenderDiff_EmitsInlineSegmentsForModifiedRows()
    {
        var output = new StringWriter();
        var diff = DiffResult.FromRows(new[]
        {
            new DiffRow(1, "count = 1;", 1, "count = 2;", DiffKind.Modified),
        });

        JsonRenderer.RenderDiff(MakeChangeSet(), MakeFile(), diff, output);

        using var doc = JsonDocument.Parse(output.ToString());
        var row = doc.RootElement.GetProperty("rows")[0];
        Assert.Equal("Modified", row.GetProperty("kind").GetString());
        Assert.Contains(row.GetProperty("oldInline").EnumerateArray(), s =>
            s.GetProperty("kind").GetString() == "Removed" && s.GetProperty("text").GetString() == "1");
        Assert.Contains(row.GetProperty("newInline").EnumerateArray(), s =>
            s.GetProperty("kind").GetString() == "Added" && s.GetProperty("text").GetString() == "2");
    }

    private static ChangeSet MakeChangeSet()
        => new(
            new GitRepositoryScope("G:\\Git\\budiff", "G:\\Git\\budiff", string.Empty, true, "main"),
            DiffTarget.WorkingTree,
            new[] { MakeFile() });

    private static ChangedFile MakeFile()
        => new(
            "src/App.cs",
            null,
            "G:\\Git\\budiff\\src\\App.cs",
            GitFileState.Modified,
            new DiffChurn(2, 1),
            AuthorCount: 1,
            CommitCount: 2,
            IsBinary: false);
}
