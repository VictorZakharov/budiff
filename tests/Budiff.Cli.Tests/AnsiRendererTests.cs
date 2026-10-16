using System.IO;
using Budiff;
using Budiff.CodeAnalysis;
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
    public void Version_PrintsThePackageVersion()
    {
        var output = new StringWriter();
        var exit = BudiffCli.Run(new[] { "--version" }, TextReader.Null, output, new StringWriter(), inputRedirected: false);

        Assert.Equal(0, exit);
        Assert.Matches(@"^budiff \d+\.\d+\.\d+\S*$", output.ToString().Trim());
    }

    [Fact]
    public void Parse_RejectsJsonWithGui()
    {
        var options = CliOptions.Parse(new[] { "-i", "--json" });

        Assert.Equal("--json cannot be combined with -i.", options.Error);
    }

    [Fact]
    public void RenderAnalysis_PrintsBirdsEyeColumns()
    {
        var output = new StringWriter();
        var scan = new ScanResult
        {
            Root = "G:\\Git\\budiff",
            HasGit = true,
            Files = new[]
            {
                new FileStat { RelativePath = "src/App.cs", Extension = ".cs", Lines = new LineCounts(10, 2, 3) },
                new FileStat { RelativePath = "README.md", Extension = ".md", Lines = new LineCounts(5, 1, 0) },
            },
        };
        var history = new HistoryResult();
        var app = history.ForPath("src/App.cs");
        app.Added = 4;
        app.Removed = 1;
        app.Commits.Add(0);
        app.Authors.Add("Ada");
        history.AuthorCommits["Ada"] = 1;

        AnsiRenderer.RenderAnalysis(
            new CodeAnalysisSnapshot(scan, history),
            output,
            new[] { "repo: (test)", "scope: ." },
            sinceDays: 30);

        var text = output.ToString();
        Assert.Contains("bird's-eye", text);
        Assert.Contains("lines", text);
        Assert.Contains("src", text);
        Assert.Contains("+4", text);
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
