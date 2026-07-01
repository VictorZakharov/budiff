using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Headless.XUnit;
using Budiff.App;
using LibGit2Sharp;
using Xunit;

namespace Budiff.Avalonia.Tests;

// Renders the REAL Budiff.App.MainWindow (not a mockup) over a seeded git fixture
// and captures the README screenshots: the default bird's-eye state (filters visible,
// no diff) and the drill-in state (compressed columns + docked diff).
public sealed class ReadmeScreenshotTests
{
    private const string ProgramCsBefore =
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

    private const string ProgramCsAfter =
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

    [AvaloniaFact]
    public void ReadmeScreenshots_RenderRealMainWindow()
    {
        var root = BuildFixtureRepo();
        var window = new MainWindow(root, gitRoot: true);
        window.Width = 1440;
        window.Height = 820;
        window.Show();

        var vm = window.ViewModel;
        WaitUntil(() => !vm.Analysis.IsScanning && vm.Analysis.Roots.Count > 0, "bird's-eye scan");

        // Default state: full bird's-eye, filter panes visible, no diff pane.
        Assert.True(vm.ShowWideColumns);
        Assert.False(vm.IsDiffOpen);
        Assert.NotEmpty(vm.Analysis.Extensions);
        Assert.NotEmpty(vm.Analysis.Authors);

        foreach (var top in vm.Analysis.Roots)
        {
            top.IsExpanded = true;
        }

        UiSnapshot.Pump();
        var birdsEye = UiSnapshot.Capture(window, "gui-screenshot");
        Assert.Equal(1440, birdsEye.Width);
        Assert.Equal(820, birdsEye.Height);

        // Drill-in state: open the modified file, docked diff + compressed columns.
        var row = vm.Analysis.Roots
            .SelectMany(r => r.SelfAndDescendants())
            .FirstOrDefault(r => r.IsFile && r.Name == "Program.cs");
        Assert.NotNull(row);
        _ = vm.Analysis.OpenAsync(row!);

        WaitUntil(() => vm.IsDiffOpen && !vm.Diff.IsPlaceholder && vm.Diff.Rows.Count > 0, "diff load");
        WaitUntil(() => !vm.Diff.IsHighlighting, "syntax highlighting", throwOnTimeout: false);

        Assert.True(vm.ShowDiffPane);
        Assert.False(vm.ShowWideColumns);

        var diffing = UiSnapshot.Capture(window, "gui-diff");
        Assert.Equal(1440, diffing.Width);
        Assert.Equal(820, diffing.Height);
    }

    private static void WaitUntil(Func<bool> condition, string what, bool throwOnTimeout = true)
    {
        var deadline = Environment.TickCount64 + 20_000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                if (throwOnTimeout)
                {
                    throw new TimeoutException($"Timed out waiting for {what}.");
                }

                return;
            }

            Thread.Sleep(25);
            UiSnapshot.Pump();
        }

        UiSnapshot.Pump();
    }

    // A small deterministic repo named "budiff": a few folders, mixed extensions,
    // commits by two authors, and one uncommitted change for the diff drill-in.
    private static string BuildFixtureRepo()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "budiff");
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

    private static Signature Sig(string name, string email, int daysAgo)
        => new(name, email, DateTimeOffset.Now.AddDays(-daysAgo));

    private static void Commit(Repository repo, Signature author, string message, Dictionary<string, string> files)
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

    private static void ForceDelete(string path)
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
