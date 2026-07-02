using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    [Fact]
    public void Parse_ContextRequiresJson()
    {
        Assert.Equal("--context requires --json.", CliOptions.Parse(new[] { "--context", "3", "App.cs" }).Error);
        Assert.NotNull(CliOptions.Parse(new[] { "--context", "-1", "--json", "App.cs" }).Error);
        Assert.NotNull(CliOptions.Parse(new[] { "--json", "App.cs", "--context" }).Error);

        var options = CliOptions.Parse(new[] { "--json", "--context", "0", "App.cs" });
        Assert.Equal(0, options.ContextLines);
        Assert.Null(options.Error);
    }

    [Fact]
    public void RenderDiff_WithoutContext_KeepsEveryRowAndOmitsGapFields()
    {
        var output = new StringWriter();

        JsonRenderer.RenderDiff(MakeChangeSet(), MakeFile(), MakeLongDiff(), output);

        using var doc = JsonDocument.Parse(output.ToString());
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("contextLines", out _));
        var rows = root.GetProperty("rows");
        Assert.Equal(21, rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray())
        {
            Assert.False(row.TryGetProperty("hiddenLines", out _));
        }
    }

    [Fact]
    public void RenderDiff_WithContext_FoldsUnchangedRunsIntoGapRows()
    {
        var output = new StringWriter();

        // Rows: Modified(1) + 20 Unchanged. Trailing run keeps 3 lead context,
        // no trail context -> exactly: modified + 3 unchanged + 1 gap of 17.
        JsonRenderer.RenderDiff(MakeChangeSet(), MakeFile(), MakeLongDiff(), output, contextLines: 3);

        using var doc = JsonDocument.Parse(output.ToString());
        var root = doc.RootElement;
        Assert.Equal(3, root.GetProperty("contextLines").GetInt32());
        var rows = root.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(5, rows.Length);
        Assert.Equal("Modified", rows[0].GetProperty("kind").GetString());
        Assert.Equal(new[] { "Unchanged", "Unchanged", "Unchanged" }, rows[1..4].Select(r => r.GetProperty("kind").GetString()));

        var gap = rows[4];
        Assert.Equal("Gap", gap.GetProperty("kind").GetString());
        Assert.Equal(17, gap.GetProperty("hiddenLines").GetInt32());
        // First hidden line on both sides: 1 modified + 3 visible context -> line 5.
        Assert.Equal(5, gap.GetProperty("oldNo").GetInt32());
        Assert.Equal(5, gap.GetProperty("newNo").GetInt32());
        Assert.Equal(JsonValueKind.Null, gap.GetProperty("oldText").ValueKind);
    }

    private static DiffResult MakeLongDiff()
    {
        var rows = new List<DiffRow> { new(1, "count = 1;", 1, "count = 2;", DiffKind.Modified) };
        for (var i = 2; i <= 21; i++)
        {
            rows.Add(new DiffRow(i, "line" + i, i, "line" + i, DiffKind.Unchanged));
        }

        return DiffResult.FromRows(rows);
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
