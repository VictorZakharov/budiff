using Budiff;
using Budiff.CodeAnalysis;
using Budiff.Git;

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
        output.WriteLine("  --context <n>   With --json <file>: fold unchanged runs into Gap rows, keep n context lines");
        output.WriteLine("  -h, --help      Show help");
        output.WriteLine();
        output.WriteLine("MCP:");
        output.WriteLine("  budiff mcp      Run a stdio MCP server for AI coding agents");
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
        foreach (var row in AnalysisNode.Flatten(root.Children).Take(200))
        {
            var name = row.Depth == 0 ? row.Node.Name : new string(' ', Math.Min(20, row.Depth * 2)) + row.Node.Name;
            output.Write(AnsiText.TruncatePlain(name, 42).PadRight(42));
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

        var width = Math.Max(96, ConsoleWidthOrDefault());
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
            total += AnsiText.ExpandedLength(chunk.Text);
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

            var expanded = AnsiText.ExpandTabs(chunkText);
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
    internal static IEnumerable<(string Text, string? Fg, bool Strong)> MergeChunks(
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

    // BufferWidth throws when stdout is not redirected yet no real console is
    // attached (PTY-less hosts); fall back to a sane default instead of dying.
    private static int ConsoleWidthOrDefault()
    {
        try
        {
            return Console.IsOutputRedirected ? 120 : Console.BufferWidth;
        }
        catch (IOException)
        {
            return 120;
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
        var safeText = AnsiText.ExpandTabs(text ?? string.Empty);
        var prefix = Dim(no + " ");

        if (kind == DiffKind.Modified && inlineRuns is not null)
        {
            return prefix + AnsiText.PadAnsi(ColorInline(inlineRuns, oldSide), contentWidth);
        }

        var line = AnsiText.Truncate(safeText, contentWidth);
        line = kind switch
        {
            DiffKind.Added when !oldSide => Green(line),
            DiffKind.Removed when oldSide => Red(line),
            DiffKind.Modified when oldSide => Red(line),
            DiffKind.Modified => Green(line),
            _ => line,
        };
        return prefix + AnsiText.PadAnsi(line, contentWidth);
    }

    private static string ColorInline(IReadOnlyList<InlineSegment> runs, bool oldSide)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var run in runs)
        {
            builder.Append(run.Kind switch
            {
                InlineChangeKind.Added => Green(run.Text),
                InlineChangeKind.Removed => Red(run.Text),
                _ => run.Text,
            });
        }

        return builder.Length == 0 ? (oldSide ? Red("-") : Green("+")) : builder.ToString();
    }

    internal static IEnumerable<(DiffRow? Row, string? GapText, int Index)> CollapseRows(IReadOnlyList<DiffRow> rows)
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

    private static string Bold(string text) => Wrap("1", text);

    private static string Dim(string text) => Wrap("2", text);

    private static string Green(string text) => Wrap("32", text);

    private static string Red(string text) => Wrap("31", text);

    private static string Wrap(string code, string text) => UseColor ? $"\u001b[{code}m{text}\u001b[0m" : text;
}
