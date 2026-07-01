using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
            return new McpServer(input, output, errorOutput, cwd).Run();
        }

        if (options.OpenGui)
        {
            return AppLauncher.Launch(options);
        }

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
            JsonRenderer.RenderDiff(changeSet, file, diff, output);
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

internal sealed record CliOptions(
    bool GitRoot,
    bool OpenGui,
    bool Mcp,
    bool Stdin,
    bool ShowHelp,
    string BaseRef,
    string? HeadRef,
    string? FilePath,
    bool Json,
    int? Days,
    string? Error)
{
    public static CliOptions Parse(string[] args)
    {
        var gitRoot = false;
        var openGui = false;
        var mcp = false;
        var stdin = false;
        var showHelp = false;
        var baseRef = "HEAD";
        string? headRef = null;
        string? filePath = null;
        string? error = null;
        var json = false;
        int? daysOption = null;

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
                case "mcp":
                    if (filePath is null)
                    {
                        mcp = true;
                    }
                    else
                    {
                        error = "`mcp` must be the first positional argument.";
                    }

                    break;
                case "--stdin":
                    stdin = true;
                    break;
                case "--json":
                    json = true;
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
                case "--days":
                    if (++i >= args.Length || !int.TryParse(args[i], out var days) || days < 0)
                    {
                        error = "--days requires a non-negative number (0 = all time).";
                    }
                    else
                    {
                        daysOption = days;
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

        if (openGui && json)
        {
            error = "--json cannot be combined with -i.";
        }

        if (openGui && stdin)
        {
            error = "--stdin cannot be combined with -i.";
        }

        if (mcp && openGui)
        {
            error = "mcp cannot be combined with -i.";
        }

        if (mcp && json)
        {
            error = "mcp cannot be combined with --json.";
        }

        if (mcp && stdin)
        {
            error = "mcp cannot be combined with --stdin.";
        }

        if (mcp && filePath is not null)
        {
            error = "mcp cannot be combined with a file path.";
        }

        if (stdin && filePath is not null)
        {
            error = "--stdin cannot be combined with a file path.";
        }

        return new CliOptions(gitRoot, openGui, mcp, stdin, showHelp, baseRef, headRef, filePath, json, daysOption, error);
    }
}

internal static class AnsiRenderer
{
    private static bool UseColor => AnsiConsole.Enabled;

    private static readonly string Esc = ((char)27).ToString();

    private static string Reset => Esc + "[0m";

    // DarkPlus default token color, readable on the row background tints.
    private static string DefaultFg => Esc + "[38;2;212;212;212m";

    // Row background tints approximating the GUI's translucent added/removed
    // backgrounds over the editor background; "strong" marks intra-line segments.
    private static string BgAdded => Esc + "[48;2;34;64;49m";

    private static string BgRemoved => Esc + "[48;2;69;47;50m";

    private static string BgAddedStrong => Esc + "[48;2;46;96;64m";

    private static string BgRemovedStrong => Esc + "[48;2;104;58;62m";

    private static string Rgb(string text, int r, int g, int b) => UseColor ? Esc + $"[38;2;{r};{g};{b}m" + text + Reset : text;

    private static string Blue(string text) => Rgb(text, 97, 175, 239);

    private static string Muted(string text) => Rgb(text, 157, 160, 168);

    private static string CommentTint(string text) => Rgb(text, 94, 140, 94);

    private static string BlankTint(string text) => Rgb(text, 110, 116, 126);

    private static string AddTint(string text) => Rgb(text, 107, 179, 107);

    private static string RemTint(string text) => Rgb(text, 209, 105, 105);

    public static void RenderHelp(TextWriter output)
    {
        output.WriteLine("budiff - ls for diffs");
        output.WriteLine();
        output.WriteLine("Usage:");
        output.WriteLine("  budiff [options] [file]");
        output.WriteLine("  budiff mcp");
        output.WriteLine();
        output.WriteLine("Options:");
        output.WriteLine("  -i              Open the desktop GUI for the same target");
        output.WriteLine("  -r, --gitroot   Widen scope to the repository root");
        output.WriteLine("  --base <ref>    Base ref for comparisons (default: HEAD)");
        output.WriteLine("  --head <ref>    Head ref; omit to compare against the working tree");
        output.WriteLine("  --days <n>      Bird's-eye analysis window in days (default 30; 0 = all time)");
        output.WriteLine("  --stdin         Read a unified diff from stdin and render it side-by-side");
        output.WriteLine("  --json          Write structured JSON instead of ANSI text");
        output.WriteLine("  -h, --help      Show help");
        output.WriteLine();
        output.WriteLine("MCP:");
        output.WriteLine("  budiff mcp      Run a stdio MCP server for AI coding agents");
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

    public static void RenderAnalysis(CodeAnalysisSnapshot snapshot, TextWriter output, IReadOnlyList<string> headerLines, int? sinceDays)
    {
        var historical = sinceDays.HasValue;
        var periodText = sinceDays is { } days ? "last " + days + " days" : "all time";
        var root = AnalysisTreeBuilder.Build(
            snapshot.Scan,
            snapshot.History,
            AnalysisGrouping.Folders,
            AnalysisSortKey.Lines,
            ascending: false,
            allTime: !historical);

        output.WriteLine($"{Bold("budiff")} bird's-eye");
        foreach (var line in headerLines)
        {
            output.WriteLine(Muted(line));
        }

        output.WriteLine(Muted($"period: {periodText}"));

        var summary = $"{root.FileCount:N0} files - {root.Lines.Total:N0} lines ({root.Lines.Code:N0} code / {root.Lines.Blank:N0} blank / {root.Lines.Comment:N0} comment)";
        if (snapshot.History is not null)
        {
            var commitsWord = root.CommitCount == 1 ? "commit" : "commits";
            var authorsWord = root.AuthorCount == 1 ? "author" : "authors";
            summary += $" - {AddTint("+" + root.Added.ToString("N0"))} / {RemTint("-" + root.Removed.ToString("N0"))} - {root.CommitCount:N0} {commitsWord}, {root.AuthorCount:N0} {authorsWord}";
        }

        output.WriteLine(summary);
        if (snapshot.Scan.Truncated)
        {
            output.WriteLine(Muted("file list capped"));
        }

        output.WriteLine();
        output.WriteLine(string.Join(" ",
            Blue("name".PadRight(42)),
            Blue("lines".PadLeft(8)),
            Blue("code".PadLeft(8)),
            Blue("blank".PadLeft(7)),
            Blue("comment".PadLeft(8)),
            Blue("files".PadLeft(6)),
            Blue("added".PadLeft(8)),
            Blue("removed".PadLeft(8)),
            Blue("commits".PadLeft(8)),
            Blue("authors".PadLeft(8))));
        foreach (var row in Flatten(root.Children, depth: 0).Take(200))
        {
            var name = row.Depth == 0 ? row.Node.Name : new string(' ', Math.Min(20, row.Depth * 2)) + row.Node.Name;
            output.Write(TruncatePlain(name, 42).PadRight(42));
            output.Write(" ");
            output.Write((row.Node.IsBinary ? "binary" : row.Node.Lines.Total.ToString("N0")).PadLeft(8));
            output.Write(" ");
            output.Write((row.Node.IsBinary ? string.Empty : row.Node.Lines.Code.ToString("N0")).PadLeft(8));
            output.Write(" ");
            output.Write(BlankTint((row.Node.IsBinary ? string.Empty : row.Node.Lines.Blank.ToString("N0")).PadLeft(7)));
            output.Write(" ");
            output.Write(CommentTint((row.Node.IsBinary ? string.Empty : row.Node.Lines.Comment.ToString("N0")).PadLeft(8)));
            output.Write(" ");
            output.Write(Muted((row.Node.IsFile ? string.Empty : row.Node.FileCount.ToString("N0")).PadLeft(6)));
            output.Write(" ");
            output.Write(AddTint((snapshot.History is not null && row.Node.Added > 0 ? "+" + row.Node.Added.ToString("N0") : string.Empty).PadLeft(8)));
            output.Write(" ");
            output.Write(RemTint((snapshot.History is not null && row.Node.Removed > 0 ? "-" + row.Node.Removed.ToString("N0") : string.Empty).PadLeft(8)));
            output.Write(" ");
            output.Write(Muted((snapshot.History is not null && row.Node.CommitCount > 0 ? row.Node.CommitCount.ToString("N0") : string.Empty).PadLeft(8)));
            output.Write(" ");
            output.WriteLine(Muted((snapshot.History is not null && row.Node.AuthorCount > 0 ? row.Node.AuthorCount.ToString("N0") : string.Empty).PadLeft(8)));
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

        // Pre-tokenize every row per side so the grammar state threads correctly
        // through rows the collapse below hides.
        var session = UseColor ? TerminalSyntax.CreateSession(Path.GetExtension(file.Path), rows.Count) : null;
        List<SyntaxChunk>?[]? oldRuns = null;
        List<SyntaxChunk>?[]? newRuns = null;
        if (session is not null)
        {
            oldRuns = new List<SyntaxChunk>?[rows.Count];
            newRuns = new List<SyntaxChunk>?[rows.Count];
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i].OldText is { } oldText)
                {
                    oldRuns[i] = session.Highlight(oldText, oldSide: true);
                }

                if (rows[i].NewText is { } newText)
                {
                    newRuns[i] = session.Highlight(newText, oldSide: false);
                }
            }
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
            string oldSide;
            string newSide;
            if (session is not null)
            {
                oldSide = ComposeSide(row.OldNo, row.OldText, oldRuns![item.Index], inline?.Old, pane, row.Kind, oldSide: true);
                newSide = ComposeSide(row.NewNo, row.NewText, newRuns![item.Index], inline?.New, pane, row.Kind, oldSide: false);
            }
            else
            {
                oldSide = FormatSide(row.OldNo, row.OldText, pane, row.Kind, oldSide: true, inline?.Old);
                newSide = FormatSide(row.NewNo, row.NewText, pane, row.Kind, oldSide: false, inline?.New);
            }

            output.Write(oldSide);
            output.Write(Dim(" | "));
            output.WriteLine(newSide);
        }
    }

    // Renders one diff pane with syntax-colored foregrounds, a background tint for
    // changed lines, and a stronger tint on intra-line changed segments.
    private static string ComposeSide(
        int? lineNo,
        string? text,
        List<SyntaxChunk>? runs,
        IReadOnlyList<InlineSegment>? inline,
        int paneWidth,
        DiffKind kind,
        bool oldSide)
    {
        var no = lineNo?.ToString().PadLeft(5) ?? "     ";
        var contentWidth = Math.Max(8, paneWidth - 7);
        var prefix = Dim(no + " ");

        if (text is null || runs is null)
        {
            return prefix + new string(' ', contentWidth);
        }

        var bg = kind switch
        {
            DiffKind.Added when !oldSide => BgAdded,
            DiffKind.Removed when oldSide => BgRemoved,
            DiffKind.Modified => oldSide ? BgRemoved : BgAdded,
            _ => null,
        };
        var strongBg = kind == DiffKind.Modified ? (oldSide ? BgRemovedStrong : BgAddedStrong) : null;

        var total = 0;
        foreach (var chunk in runs)
        {
            total += ExpandTabs(chunk.Text).Length;
        }

        var budget = total <= contentWidth ? int.MaxValue : contentWidth - 1;
        var builder = new System.Text.StringBuilder();
        builder.Append(prefix);
        if (bg is not null)
        {
            builder.Append(bg);
        }

        var visible = 0;
        foreach (var (chunkText, fg, strong) in MergeChunks(runs, inline))
        {
            if (visible >= budget)
            {
                break;
            }

            var expanded = ExpandTabs(chunkText);
            if (expanded.Length == 0)
            {
                continue;
            }

            if (visible + expanded.Length > budget)
            {
                expanded = expanded[..(budget - visible)];
            }

            var useStrong = strong && strongBg is not null && bg is not null;
            if (useStrong)
            {
                builder.Append(strongBg);
            }

            builder.Append(fg ?? DefaultFg);
            builder.Append(expanded);
            if (useStrong)
            {
                builder.Append(bg);
            }

            visible += expanded.Length;
        }

        if (total > contentWidth)
        {
            builder.Append(DefaultFg).Append('>');
            visible += 1;
        }

        if (visible < contentWidth)
        {
            builder.Append(new string(' ', contentWidth - visible));
        }

        builder.Append(Reset);
        return builder.ToString();
    }

    // Splits the syntax runs at the inline-diff segment boundaries so intra-line
    // changed spans can carry the stronger background while keeping token colors.
    private static IEnumerable<(string Text, string? Fg, bool Strong)> MergeChunks(
        List<SyntaxChunk> runs,
        IReadOnlyList<InlineSegment>? inline)
    {
        if (inline is null || inline.Count == 0)
        {
            foreach (var chunk in runs)
            {
                yield return (chunk.Text, chunk.Fg, false);
            }

            yield break;
        }

        var runIndex = 0;
        var runOffset = 0;
        foreach (var segment in inline)
        {
            var strong = segment.Kind != InlineChangeKind.Unchanged;
            var remaining = segment.Text.Length;
            while (remaining > 0 && runIndex < runs.Count)
            {
                var run = runs[runIndex];
                var take = Math.Min(remaining, run.Text.Length - runOffset);
                if (take > 0)
                {
                    yield return (run.Text.Substring(runOffset, take), run.Fg, strong);
                    runOffset += take;
                    remaining -= take;
                }

                if (runOffset >= run.Text.Length)
                {
                    runIndex++;
                    runOffset = 0;
                }
            }

            if (remaining > 0)
            {
                // Length mismatch safety: emit the tail uncolored.
                yield return (segment.Text[^remaining..], null, strong);
            }
        }

        while (runIndex < runs.Count)
        {
            var run = runs[runIndex];
            yield return (runOffset == 0 ? run.Text : run.Text[runOffset..], run.Fg, false);
            runIndex++;
            runOffset = 0;
        }
    }

    private static string ExpandTabs(string text) => text.Replace("\t", "    ", StringComparison.Ordinal);

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

    private static IEnumerable<(DiffRow? Row, string? GapText, int Index)> CollapseRows(IReadOnlyList<DiffRow> rows)
    {
        const int context = 3;
        var i = 0;
        while (i < rows.Count)
        {
            if (rows[i].Kind != DiffKind.Unchanged)
            {
                yield return (rows[i], null, i);
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
                    yield return (rows[k], null, k);
                }
            }
            else
            {
                var lead = i == 0 ? 0 : context;
                var trail = j == rows.Count ? 0 : context;
                for (var k = i; k < i + lead; k++)
                {
                    yield return (rows[k], null, k);
                }

                var hidden = len - lead - trail;
                if (hidden > 0)
                {
                    yield return (null, $"... {hidden} unchanged line{(hidden == 1 ? string.Empty : "s")} ...", -1);
                }

                for (var k = j - trail; k < j; k++)
                {
                    yield return (rows[k], null, k);
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

    private static string TruncatePlain(string text, int width)
    {
        if (text.Length <= width)
        {
            return text;
        }

        return width <= 1 ? text[..width] : text[..(width - 1)] + ">";
    }

    private static IEnumerable<(AnalysisNode Node, int Depth)> Flatten(IEnumerable<AnalysisNode> nodes, int depth)
    {
        foreach (var node in nodes)
        {
            yield return (node, depth);
            foreach (var child in Flatten(node.Children, depth + 1))
            {
                yield return child;
            }
        }
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

        // The GUI ships inside the tool package under gui/ (see Budiff.Cli.csproj);
        // a bare exe next to the CLI covers hand-assembled layouts.
        var baseDir = AppContext.BaseDirectory;
        foreach (var name in new[] { "Budiff.App.exe", "Budiff.App" })
        {
            foreach (var path in new[] { Path.Combine(baseDir, "gui", name), Path.Combine(baseDir, name) })
            {
                if (File.Exists(path) && TryStart(path, start, options.GitRoot))
                {
                    return 0;
                }
            }
        }

        // No apphost for this OS in the package (it is produced on the packing
        // machine) - run the assembly through the dotnet host instead.
        var bundledDll = Path.Combine(baseDir, "gui", "Budiff.App.dll");
        if (File.Exists(bundledDll) && TryStartDll(bundledDll, start, options.GitRoot))
        {
            return 0;
        }

        // Dev fallback: inside the budiff repo, launch the exe a normal build
        // already produced. Never build here - `dotnet run` was slow and opened
        // an extra console window.
        var appDir = FindDevAppDirectory(Directory.GetCurrentDirectory());
        if (appDir is not null)
        {
            var built = FindNewestBuiltApp(appDir);
            if (built is not null && TryStart(built, start, options.GitRoot))
            {
                return 0;
            }

            Console.Error.WriteLine($"Budiff.App at {appDir} is not built. Run: dotnet build");
            return 1;
        }

        Console.Error.WriteLine("Could not find Budiff.App. Set BUDIFF_APP_PATH to the GUI executable.");
        return 1;
    }

    private static bool TryStart(string executable, string startPath, bool gitRoot)
    {
        try
        {
            // UseShellExecute detaches the GUI from this console entirely - the
            // terminal gets its prompt back and never waits on the GUI's lifetime.
            var args = $"\"{startPath}\"" + (gitRoot ? " --gitroot" : string.Empty);
            Process.Start(new ProcessStartInfo(executable, args) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryStartDll(string assemblyPath, string startPath, bool gitRoot)
    {
        try
        {
            var args = $"\"{assemblyPath}\" \"{startPath}\"" + (gitRoot ? " --gitroot" : string.Empty);
            Process.Start(new ProcessStartInfo("dotnet", args) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindDevAppDirectory(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Budiff.App");
            if (File.Exists(Path.Combine(candidate, "Budiff.App.csproj")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    internal static string? FindNewestBuiltApp(string appDirectory)
    {
        var bin = Path.Combine(appDirectory, "bin");
        if (!Directory.Exists(bin))
        {
            return null;
        }

        return Directory.EnumerateFiles(bin, "Budiff.App*", SearchOption.AllDirectories)
            .Where(p => Path.GetFileName(p) is "Budiff.App.exe" or "Budiff.App")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
