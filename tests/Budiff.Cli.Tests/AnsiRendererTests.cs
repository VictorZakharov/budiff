using System.IO;
using Budiff;
using Budiff.Git;
using Xunit;

namespace Budiff.Cli.Tests;

public sealed class AnsiRendererTests
{
    [Fact]
    public void Parse_RejectsUnknownOptionAndSecondFile()
    {
        Assert.Equal("Unknown option '--bad'.", CliOptions.Parse(new[] { "--bad" }).Error);
        Assert.Equal("Only one file path can be provided.", CliOptions.Parse(new[] { "a.cs", "b.cs" }).Error);
    }

    [Fact]
    public void Parse_RejectsJsonWithGui()
    {
        var options = CliOptions.Parse(new[] { "-i", "--json" });

        Assert.Equal("--json cannot be combined with -i.", options.Error);
    }

    [Fact]
    public void RenderList_PrintsChangeRowsFromStructuredModel()
    {
        var output = new StringWriter();

        AnsiRenderer.RenderList(MakeChangeSet(), output);

        var text = output.ToString();
        Assert.Contains("budiff HEAD vs working tree", text);
        Assert.Contains("src/App.cs", text);
        Assert.Contains("Run `budiff <file>`", text);
    }

    [Fact]
    public void RenderDiff_PrintsPlaceholderAndSideBySideRows()
    {
        var placeholder = new StringWriter();
        AnsiRenderer.RenderDiff(MakeFile(), DiffResult.Message("Binary file not shown."), placeholder);
        Assert.Contains("Binary file not shown.", placeholder.ToString());

        var diff = new StringWriter();
        AnsiRenderer.RenderDiff(
            MakeFile(),
            DiffResult.FromRows(new[]
            {
                new DiffRow(1, "count = 1;", 1, "count = 2;", DiffKind.Modified),
            }),
            diff);

        var text = diff.ToString();
        Assert.Contains("count = 1;", text);
        Assert.Contains("count = 2;", text);
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
