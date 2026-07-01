using System;
using System.IO;
using Xunit;

namespace Budiff.Cli.Tests;

public sealed class FolderFallbackTests
{
    [Fact]
    public void Run_OutsideGitRepository_FallsBackToFolderAnalysis()
    {
        var dir = CreateFolder();
        try
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var exit = BudiffCli.Run(Array.Empty<string>(), TextReader.Null, output, error, inputRedirected: false, workingDirectory: dir);

            Assert.Equal(0, exit);
            var text = output.ToString();
            Assert.Contains("bird's-eye", text);
            Assert.Contains("folder:", text);
            Assert.Contains("not a git repository", text);
            Assert.Contains("a.cs", text);
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Run_OutsideGitRepository_FileDiffStillErrors()
    {
        var dir = CreateFolder();
        try
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var exit = BudiffCli.Run(new[] { "a.cs" }, TextReader.Null, output, error, inputRedirected: false, workingDirectory: dir);

            Assert.Equal(1, exit);
            Assert.NotEqual(string.Empty, error.ToString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string CreateFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "budiff-nogit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "a.cs"), "class A\n{\n}\n");
        File.WriteAllText(Path.Combine(dir, "sub", "b.md"), "# heading\n\nbody\n");
        return dir;
    }
}
