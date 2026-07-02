using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Budiff;
using Budiff.CodeAnalysis;
using Budiff.Git;

return BudiffCli.Run(args);

internal static class BudiffCli
{
    public static int Run(string[] args)
        => Run(args, Console.In, Console.Out, Console.Error, Console.IsInputRedirected);

    internal static int Run(string[] args, TextReader input, TextWriter output, TextWriter errorOutput, bool inputRedirected, string? workingDirectory = null)
    {
        AnsiConsole.TryEnableVirtualTerminal();
        var cwd = workingDirectory ?? Environment.CurrentDirectory;
        var options = CliOptions.Parse(args);
        if (options.ShowHelp)
        {
            AnsiRenderer.RenderHelp(output);
            return 0;
        }

        if (options.Error is { } error)
        {
            errorOutput.WriteLine(error);
            AnsiRenderer.RenderHelp(errorOutput);
            return 2;
        }

        if (ShouldReadStdin(options, input, inputRedirected))
        {
            return RenderStdinDiff(input.ReadToEnd(), output, errorOutput, options.Json);
        }

        if (options.Mcp)
        {
            // MCP stdio must be UTF-8: clients spawn the server with redirected
            // pipes, which on Windows otherwise default to the OEM codepage and
            // mangle non-ASCII tool results. Wrap the raw streams (BOM-less)
            // instead of switching Console.OutputEncoding - no console codepage
            // side effect to restore. Only the real console streams are
            // rewrapped; tests inject in-memory readers/writers.
            if (ReferenceEquals(input, Console.In) && ReferenceEquals(output, Console.Out))
            {
                using var utf8Input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
                using var utf8Output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                return new McpServer(utf8Input, utf8Output, errorOutput, cwd).Run();
            }

            return new McpServer(input, output, errorOutput, cwd).Run();
        }

        if (options.OpenGui)
        {
            return AppLauncher.Launch(options, output);
        }

        // --json feeds machines; when stdout is a pipe hand them UTF-8 bytes
        // (matching the MCP server) instead of the OEM codepage. Interactive
        // terminal output keeps the console's own encoding.
        StreamWriter? utf8Json = null;
        if (options.Json && ReferenceEquals(output, Console.Out) && Console.IsOutputRedirected)
        {
            utf8Json = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            output = utf8Json;
        }

        using var _ = utf8Json;

        // --days 0 = all time; default window is 30 days.
        int? sinceDays = options.Days == 0 ? null : (options.Days ?? 30);

        GitRepositoryScope scope;
        try
        {
            scope = GitRepositoryReader.Open(cwd, options.GitRoot);
        }
        catch (Exception ex)
        {
            // The bird's-eye is a LOC analysis at heart; git only adds churn/commits/
            // authors. Outside a repository, fall back to the plain folder analysis.
            if (options.FilePath is null && !options.Json)
            {
                var folderSnapshot = CodeAnalysisRepositoryReader.Load(cwd, sinceDays);
                AnsiRenderer.RenderAnalysis(
                    folderSnapshot,
                    output,
                    new[] { $"folder: {cwd}", "not a git repository - folder analysis only (no churn / commits / authors)" },
                    sinceDays);
                return 0;
            }

            errorOutput.WriteLine(ex.Message);
            return 1;
        }

        var target = new DiffTarget(options.BaseRef, options.HeadRef, options.HeadRef is null);

        if (options.FilePath is null)
        {
            if (options.Json)
            {
                var jsonChangeSet = GitRepositoryReader.ListChanges(scope, target);
                JsonRenderer.RenderList(jsonChangeSet, output);
            }
            else
            {
                var snapshot = CodeAnalysisRepositoryReader.Load(scope.ScopeRoot, sinceDays);
                AnsiRenderer.RenderAnalysis(
                    snapshot,
                    output,
                    new[] { $"repo: {scope.RepositoryRoot}", $"scope: {(scope.ScopePrefix.Length == 0 ? "." : scope.ScopePrefix)}" },
                    sinceDays);
            }

            return 0;
        }

        var changeSet = GitRepositoryReader.ListChanges(scope, target);
        var repoPath = ToRepoPath(scope.RepositoryRoot, options.FilePath, cwd);
        var file = changeSet.Files.FirstOrDefault(f =>
            f.Path.Equals(repoPath, StringComparison.OrdinalIgnoreCase)
            || f.Path.EndsWith("/" + repoPath, StringComparison.OrdinalIgnoreCase));

        if (file is null)
        {
            errorOutput.WriteLine($"No changed file matched '{options.FilePath}'.");
            return 1;
        }

        var diff = GitRepositoryReader.LoadDiff(scope, file, target);
        if (options.Json)
        {
            JsonRenderer.RenderDiff(changeSet, file, diff, output, options.ContextLines);
        }
        else
        {
            AnsiRenderer.RenderDiff(file, diff, output);
        }

        return 0;
    }

    private static bool ShouldReadStdin(CliOptions options, TextReader input, bool inputRedirected)
    {
        if (options.FilePath is not null || options.OpenGui || options.Mcp)
        {
            return false;
        }

        return options.Stdin || (inputRedirected && input.Peek() >= 0);
    }

    private static int RenderStdinDiff(string text, TextWriter output, TextWriter errorOutput, bool json)
    {
        if (json)
        {
            errorOutput.WriteLine("--json is not supported for stdin diffs.");
            return 2;
        }

        var files = UnifiedDiffParser.Parse(text);
        if (files.Count == 0)
        {
            errorOutput.WriteLine("No unified diff found on stdin.");
            return 1;
        }

        for (var i = 0; i < files.Count; i++)
        {
            if (i > 0)
            {
                output.WriteLine();
            }

            AnsiRenderer.RenderDiff(files[i].File, files[i].Diff, output);
        }

        return 0;
    }

    private static string ToRepoPath(string repositoryRoot, string path, string cwd)
    {
        var full = Path.GetFullPath(path, cwd);
        var rel = Path.GetRelativePath(repositoryRoot, full);
        return rel == "." ? string.Empty : rel.Replace('\\', '/');
    }
}
