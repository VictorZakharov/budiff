using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Budiff;
using Budiff.Git;

return BudiffCli.Run(args);

internal static class BudiffCli
{
    public static int Run(string[] args)
    {
        var options = CliOptions.Parse(args);
        if (options.ShowHelp)
        {
            AnsiRenderer.RenderHelp(Console.Out);
            return 0;
        }

        if (options.Error is { } error)
        {
            Console.Error.WriteLine(error);
            AnsiRenderer.RenderHelp(Console.Error);
            return 2;
        }

        if (options.OpenGui)
        {
            return AppLauncher.Launch(options);
        }

        GitRepositoryScope scope;
        try
        {
            scope = GitRepositoryReader.Open(Environment.CurrentDirectory, options.GitRoot);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var target = new DiffTarget(options.BaseRef, options.HeadRef, options.HeadRef is null);
        var changeSet = GitRepositoryReader.ListChanges(scope, target);

        if (options.FilePath is null)
        {
            AnsiRenderer.RenderList(changeSet, Console.Out);
            return 0;
        }

        var repoPath = ToRepoPath(scope.RepositoryRoot, options.FilePath);
        var file = changeSet.Files.FirstOrDefault(f =>
            f.Path.Equals(repoPath, StringComparison.OrdinalIgnoreCase)
            || f.Path.EndsWith("/" + repoPath, StringComparison.OrdinalIgnoreCase));

        if (file is null)
        {
            Console.Error.WriteLine($"No changed file matched '{options.FilePath}'.");
            return 1;
        }

        var diff = GitRepositoryReader.LoadDiff(scope, file, target);
        AnsiRenderer.RenderDiff(file, diff, Console.Out);
        return 0;
    }

    private static string ToRepoPath(string repositoryRoot, string path)
    {
        var full = Path.GetFullPath(path, Environment.CurrentDirectory);
        var rel = Path.GetRelativePath(repositoryRoot, full);
        return rel == "." ? string.Empty : rel.Replace('\\', '/');
    }
}

internal sealed record CliOptions(
    bool GitRoot,
    bool OpenGui,
    bool ShowHelp,
    string BaseRef,
    string? HeadRef,
    string? FilePath,
    string? Error)
{
    public static CliOptions Parse(string[] args)
    {
        var gitRoot = false;
        var openGui = false;
        var showHelp = false;
        var baseRef = "HEAD";
        string? headRef = null;
        string? filePath = null;
        string? error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    showHelp = true;
                    break;
                case "-r":
                case "--repo":
                case "--gitroot":
                    gitRoot = true;
                    break;
                case "-i":
                    openGui = true;
                    break;
                case "--base":
                    if (++i >= args.Length)
                    {
                        error = "--base requires a ref.";
                    }
                    else
                    {
                        baseRef = args[i];
                    }

                    break;
                case "--head":
                    if (++i >= args.Length)
                    {
                        error = "--head requires a ref.";
                    }
                    else
                    {
                        headRef = args[i];
                    }

                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        error = $"Unknown option '{arg}'.";
                    }
                    else if (filePath is null)
                    {
                        filePath = arg;
                    }
                    else
                    {
                        error = "Only one file path can be provided.";
                    }

                    break;
            }
        }

        return new CliOptions(gitRoot, openGui, showHelp, baseRef, headRef, filePath, error);
    }
}

internal static class AnsiRenderer
{
    private static bool UseColor => !Console.IsOutputRedirected;

    public static void RenderHelp(TextWriter output)
    {
        output.WriteLine("budiff - ls for diffs");
        output.WriteLine();
        output.WriteLine("Usage:");
        output.WriteLine("  budiff [options] [file]");
        output.WriteLine();
        output.WriteLine("Options:");
        output.WriteLine("  -i              Open the desktop GUI for the same target");
        output.WriteLine("  -r, --gitroot   Widen scope to the repository root");
        output.WriteLine("  --base <ref>    Base ref for comparisons (default: HEAD)");
        output.WriteLine("  --head <ref>    Head ref; omit to compare against the working tree");
        output.WriteLine("  -h, --help      Show help");
    }

    public static void RenderList(ChangeSet changeSet, TextWriter output)
    {
        output.WriteLine($"{Bold("budiff")} {changeSet.Target.Display}");
        output.WriteLine($"repo: {changeSet.Scope.RepositoryRoot}");
        output.WriteLine($"scope: {(changeSet.Scope.ScopePrefix.Length == 0 ? "." : changeSet.Scope.ScopePrefix)}");
        if (changeSet.Scope.Branch is { Length: > 0 } branch)
        {
            output.WriteLine($"branch: {branch}");
        }

        output.WriteLine();

        if (changeSet.Files.Count == 0)
        {
            output.WriteLine("No changes in scope.");
            return;
        }

        output.WriteLine($"{Dim("st"),-3} {Green("+"),5} {Red("-"),5} {"auth",5} {"comm",5}  path");
        foreach (var file in changeSet.Files)
        {
            output.Write(file.StateText.PadRight(3));
            output.Write(" ");
            output.Write(Green(file.Churn.Added.ToString().PadLeft(5)));
            output.Write(" ");
            output.Write(Red(file.Churn.Removed.ToString().PadLeft(5)));
            output.Write(" ");
            output.Write(file.AuthorCount.ToString().PadLeft(5));
            output.Write(" ");
            output.Write(file.CommitCount.ToString().PadLeft(5));
            output.Write("  ");
            output.WriteLine(file.Path);
        }

        output.WriteLine();
        output.WriteLine(Dim("Run `budiff <file>` to print the side-by-side diff."));
    }

    public static void RenderDiff(ChangedFile file, DiffResult diff, TextWriter output)
    {
        output.WriteLine($"{Bold(file.Path)}  {Dim(file.StateText)}  {Green("+" + file.Churn.Added)} {Red("-" + file.Churn.Removed)}");
        output.WriteLine();

        if (diff.Rows is not { } rows)
        {
            output.WriteLine(diff.Placeholder ?? "Nothing to show.");
            return;
        }

        var width = Math.Max(96, Console.IsOutputRedirected ? 120 : Console.BufferWidth);
        var pane = Math.Max(32, (width - 7) / 2);
        foreach (var item in CollapseRows(rows))
        {
            if (item.GapText is { } gap)
            {
                output.WriteLine(Dim(gap));
                continue;
            }

            var row = item.Row!;
            var inline = row.Kind == DiffKind.Modified ? InlineDiff.Compute(row.OldText, row.NewText) : null;
            var oldSide = FormatSide(row.OldNo, row.OldText, pane, row.Kind, oldSide: true, inline?.Old);
            var newSide = FormatSide(row.NewNo, row.NewText, pane, row.Kind, oldSide: false, inline?.New);
            output.Write(oldSide);
            output.Write(Dim(" | "));
            output.WriteLine(newSide);
        }
    }

    private static string FormatSide(
        int? lineNo,
        string? text,
        int paneWidth,
        DiffKind kind,
        bool oldSide,
        IReadOnlyList<InlineSegment>? inlineRuns)
    {
        var no = lineNo?.ToString().PadLeft(5) ?? "     ";
        var contentWidth = Math.Max(8, paneWidth - 7);
        var safeText = (text ?? string.Empty).Replace("\t", "    ", StringComparison.Ordinal);
        var prefix = Dim(no + " ");

        if (kind == DiffKind.Modified && inlineRuns is not null)
        {
            return prefix + PadAnsi(ColorInline(inlineRuns, oldSide), contentWidth);
        }

        var line = Truncate(safeText, contentWidth);
        line = kind switch
        {
            DiffKind.Added when !oldSide => Green(line),
            DiffKind.Removed when oldSide => Red(line),
            DiffKind.Modified when oldSide => Red(line),
            DiffKind.Modified => Green(line),
            _ => line,
        };
        return prefix + PadAnsi(line, contentWidth);
    }

    private static string ColorInline(IReadOnlyList<InlineSegment> runs, bool oldSide)
    {
        var text = string.Empty;
        foreach (var run in runs)
        {
            text += run.Kind switch
            {
                InlineChangeKind.Added => Green(run.Text),
                InlineChangeKind.Removed => Red(run.Text),
                _ => run.Text,
            };
        }

        return text.Length == 0 ? (oldSide ? Red("-") : Green("+")) : text;
    }

    private static IEnumerable<(DiffRow? Row, string? GapText)> CollapseRows(IReadOnlyList<DiffRow> rows)
    {
        const int context = 3;
        var i = 0;
        while (i < rows.Count)
        {
            if (rows[i].Kind != DiffKind.Unchanged)
            {
                yield return (rows[i], null);
                i++;
                continue;
            }

            var j = i;
            while (j < rows.Count && rows[j].Kind == DiffKind.Unchanged)
            {
                j++;
            }

            var len = j - i;
            if (len <= (context * 2) + 1)
            {
                for (var k = i; k < j; k++)
                {
                    yield return (rows[k], null);
                }
            }
            else
            {
                var lead = i == 0 ? 0 : context;
                var trail = j == rows.Count ? 0 : context;
                for (var k = i; k < i + lead; k++)
                {
                    yield return (rows[k], null);
                }

                var hidden = len - lead - trail;
                if (hidden > 0)
                {
                    yield return (null, $"... {hidden} unchanged line{(hidden == 1 ? string.Empty : "s")} ...");
                }

                for (var k = j - trail; k < j; k++)
                {
                    yield return (rows[k], null);
                }
            }

            i = j;
        }
    }

    private static string Truncate(string text, int width)
    {
        if (text.Length <= width)
        {
            return text.PadRight(width);
        }

        return width <= 1 ? text[..width] : text[..(width - 1)] + ">";
    }

    private static string PadAnsi(string text, int width)
    {
        var visible = VisibleLength(text);
        return visible >= width ? text : text + new string(' ', width - visible);
    }

    private static int VisibleLength(string text)
    {
        var count = 0;
        var inEscape = false;
        foreach (var ch in text)
        {
            if (ch == '\x1b')
            {
                inEscape = true;
                continue;
            }

            if (inEscape)
            {
                if (char.IsLetter(ch))
                {
                    inEscape = false;
                }

                continue;
            }

            count++;
        }

        return count;
    }

    private static string Bold(string text) => Wrap("1", text);

    private static string Dim(string text) => Wrap("2", text);

    private static string Green(string text) => Wrap("32", text);

    private static string Red(string text) => Wrap("31", text);

    private static string Wrap(string code, string text) => UseColor ? $"\u001b[{code}m{text}\u001b[0m" : text;
}

internal static class AppLauncher
{
    public static int Launch(CliOptions options)
    {
        var start = Environment.CurrentDirectory;
        var configured = Environment.GetEnvironmentVariable("BUDIFF_APP_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && TryStart(configured, start, options.GitRoot))
        {
            return 0;
        }

        var baseDir = AppContext.BaseDirectory;
        foreach (var exe in new[] { "Budiff.App.exe", "Budiff.App" })
        {
            var path = Path.Combine(baseDir, exe);
            if (File.Exists(path) && TryStart(path, start, options.GitRoot))
            {
                return 0;
            }
        }

        var devProject = FindDevAppProject(Directory.GetCurrentDirectory());
        if (devProject is not null)
        {
            var args = $"run --project \"{devProject}\" -- \"{start}\"" + (options.GitRoot ? " --gitroot" : string.Empty);
            Process.Start(new ProcessStartInfo("dotnet", args) { UseShellExecute = false });
            return 0;
        }

        Console.Error.WriteLine("Could not find Budiff.App. Set BUDIFF_APP_PATH to the GUI executable.");
        return 1;
    }

    private static bool TryStart(string executable, string startPath, bool gitRoot)
    {
        try
        {
            var args = $"\"{startPath}\"" + (gitRoot ? " --gitroot" : string.Empty);
            Process.Start(new ProcessStartInfo(executable, args) { UseShellExecute = false });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindDevAppProject(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Budiff.App", "Budiff.App.csproj");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
