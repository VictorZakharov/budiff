using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibGit2Sharp;

namespace Budiff.Avalonia.Tests;

// A small deterministic repo named "budiff": a few folders, mixed extensions,
// commits by two authors, and one uncommitted change for the diff drill-in.
// Shared by the README screenshot and narrow-layout captures; each caller gets
// its own folder so test classes can run in parallel.
internal static class ReadmeFixture
{
    public const string ProgramCsBefore =
        """
        using Budiff;
        using Budiff.Git;

        return BudiffCli.Run(args);

        internal static class BudiffCli
        {
            public static int Run(string[] args)
            {
                var options = CliOptions.Parse(args);
                AnsiRenderer.RenderList(changeSet, Console.Out);
                return 0;
            }
        }
        """;

    public const string ProgramCsAfter =
        """
        using Budiff;
        using Budiff.Git;

        return BudiffCli.Run(args);

        internal static class BudiffCli
        {
            public static int Run(string[] args)
            {
                var options = CliOptions.Parse(args);
                if (options.Json)
                {
                    JsonRenderer.RenderList(changeSet, Console.Out);
                }
                else
                {
                    AnsiRenderer.RenderList(changeSet, Console.Out);
                }

                return 0;
            }
        }
        """;

    public static string BuildFixtureRepo(string name = "budiff")
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        ForceDelete(root);
        Directory.CreateDirectory(root);
        Repository.Init(root);

        using var repo = new Repository(root);

        // A baseline commit OLDER than the default "Last month" window: the windowed
        // Added/Removed metric is a NET diff from the window-start snapshot to HEAD,
        // so removals only show when a file shrinks relative to that baseline.
        Commit(repo, Sig("Ada Lovelace", "ada@example.com", daysAgo: 45), "Scaffold the project", new()
        {
            ["README.md"] = "# budiff\n\nls, but for your diffs.\n\n## Usage\n\nRun `budiff` in any folder.\n",
            ["budiff.json"] = "{\n  \"name\": \"budiff\",\n  \"version\": \"0.1.0\"\n}\n",
            ["build.ps1"] = "# Build script\ndotnet build\ndotnet test\n",
            ["src/Budiff.Core/LineDiff.cs"] = CsFile("LineDiff", 4),
            ["src/Budiff.Core/Models.cs"] = CsFile("Models", 2),
            ["src/Budiff.Cli/Program.cs"] = ProgramCsBefore + "\n",
            ["src/Budiff.Cli/CliOptions.cs"] = CsFile("CliOptions", 2),
            ["src/Budiff.Cli/AnsiRenderer.cs"] = CsFile("AnsiRenderer", 8),
        });

        Commit(repo, Sig("Ada Lovelace", "ada@example.com", daysAgo: 16), "Add tests and docs", new()
        {
            ["tests/CliTests.cs"] = CsFile("CliTests", 2),
            ["tests/LineDiffTests.cs"] = CsFile("LineDiffTests", 3),
            ["docs/design.md"] = "# Design\n\nThe listing is the ls surface; the diff is the drill-in.\n",
        });

        Commit(repo, Sig("Grace Hopper", "grace@example.com", daysAgo: 9), "Trim the renderer", new()
        {
            ["src/Budiff.Cli/AnsiRenderer.cs"] = CsFile("AnsiRenderer", 5),
            ["src/Budiff.Cli/CliOptions.cs"] = CsFile("CliOptions", 3),
        });

        Commit(repo, Sig("Grace Hopper", "grace@example.com", daysAgo: 6), "Add the diff loader", new()
        {
            ["src/Budiff.Core/DiffLoader.cs"] = CsFile("DiffLoader", 3),
        });

        Commit(repo, Sig("Ada Lovelace", "ada@example.com", daysAgo: 3), "Simplify the line diff", new()
        {
            ["src/Budiff.Core/LineDiff.cs"] = CsFile("LineDiff", 3),
            ["README.md"] = "# budiff\n\nls, but for your diffs.\n\n## Usage\n\nRun `budiff` in any folder.\nUse `--gitroot` for the whole repository.\n",
        });

        // Uncommitted working-tree change: the diff the drill-in screenshot shows.
        File.WriteAllText(Path.Combine(root, "src/Budiff.Cli/Program.cs".Replace('/', Path.DirectorySeparatorChar)), ProgramCsAfter + "\n");
        return root;
    }

    internal static Signature Sig(string name, string email, int daysAgo)
        => new(name, email, DateTimeOffset.Now.AddDays(-daysAgo));

    internal static void Commit(Repository repo, Signature author, string message, Dictionary<string, string> files)
    {
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(repo.Info.WorkingDirectory, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            Commands.Stage(repo, path);
        }

        repo.Commit(message, author, author);
    }

    private static string CsFile(string name, int methods)
    {
        var body = string.Join("\n\n", Enumerable.Range(1, methods).Select(i =>
            $"    // Step {i} of the {name} pipeline.\n    public static int Step{i}(int value)\n    {{\n        var result = value + {i};\n        return result;\n    }}"));
        return $"namespace Budiff.Sample;\n\npublic static class {name}\n{{\n{body}\n}}\n";
    }

    internal static void ForceDelete(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        // Git object files are read-only on Windows; clear attributes before deleting.
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }
}
