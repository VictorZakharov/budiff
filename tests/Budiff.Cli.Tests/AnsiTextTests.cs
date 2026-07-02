using System.Collections.Generic;
using System.Linq;
using Budiff;
using Xunit;

namespace Budiff.Cli.Tests;

// Direct tests for the pure ANSI text primitives and the diff layout
// functions that were previously only reachable through rendered output.
public sealed class AnsiTextTests
{
    private static readonly string Esc = ((char)27).ToString();

    [Fact]
    public void VisibleLength_IgnoresEscapeSequences()
    {
        Assert.Equal(3, AnsiText.VisibleLength(Esc + "[38;2;100;200;50mabc" + Esc + "[0m"));
        Assert.Equal(0, AnsiText.VisibleLength(string.Empty));
        Assert.Equal(5, AnsiText.VisibleLength("plain"));
    }

    [Fact]
    public void PadAnsi_PadsToVisibleWidth()
    {
        var colored = Esc + "[31mab" + Esc + "[0m";
        Assert.Equal(colored + "   ", AnsiText.PadAnsi(colored, 5));
        Assert.Equal(colored, AnsiText.PadAnsi(colored, 2));
    }

    [Fact]
    public void Truncate_MarksOverflowWithArrow()
    {
        Assert.Equal("abc  ", AnsiText.Truncate("abc", 5));
        Assert.Equal("abcd>", AnsiText.Truncate("abcdef", 5));
        Assert.Equal("a", AnsiText.Truncate("abcdef", 1));
        Assert.Equal("abcdef", AnsiText.TruncatePlain("abcdef", 10));
        Assert.Equal("abcd>", AnsiText.TruncatePlain("abcdef", 5));
    }

    [Fact]
    public void ExpandedLength_CountsTabsWithoutAllocating()
    {
        Assert.Equal(AnsiText.ExpandTabs("\ta\tb").Length, AnsiText.ExpandedLength("\ta\tb"));
        Assert.Equal(3, AnsiText.ExpandedLength("abc"));
    }

    [Fact]
    public void CollapseRows_FoldsLongUnchangedRuns_NoLeadAtStart_NoTrailAtEnd()
    {
        var rows = new List<DiffRow>();
        for (var i = 1; i <= 30; i++)
        {
            rows.Add(new DiffRow(i, "x", i, "x", DiffKind.Unchanged));
        }

        rows.Add(new DiffRow(null, null, 31, "new", DiffKind.Added));
        for (var i = 31; i <= 60; i++)
        {
            rows.Add(new DiffRow(i, "x", i + 1, "x", DiffKind.Unchanged));
        }

        var items = AnsiRenderer.CollapseRows(rows).ToList();

        // Leading run: no lead context (file start) -> gap(27) + 3 context.
        // Trailing run: 3 context + gap(27), no trail context (file end).
        Assert.Null(items[0].Row);
        Assert.Contains("27", items[0].GapText);
        Assert.Equal(3 + 1 + 3, items.Count(r => r.Row is not null));
        Assert.Null(items[^1].Row);
        Assert.Contains("27", items[^1].GapText!);
        Assert.Equal(DiffKind.Added, items[4].Row!.Kind);
    }

    [Fact]
    public void CollapseRows_KeepsShortRunsVisible()
    {
        var rows = new List<DiffRow>
        {
            new(1, "a", 1, "a", DiffKind.Unchanged),
            new(2, "b", 2, "c", DiffKind.Modified),
            new(3, "d", 3, "d", DiffKind.Unchanged),
        };

        var items = AnsiRenderer.CollapseRows(rows).ToList();

        Assert.Equal(3, items.Count);
        Assert.All(items, item => Assert.NotNull(item.Row));
    }

    [Fact]
    public void MergeChunks_SplitsSyntaxRunsAtInlineBoundaries()
    {
        var runs = new List<SyntaxChunk> { new("abcdef", "FG1") };
        var inline = new[]
        {
            new InlineSegment("abc", InlineChangeKind.Unchanged),
            new InlineSegment("def", InlineChangeKind.Added),
        };

        var chunks = AnsiRenderer.MergeChunks(runs, inline).ToList();

        Assert.Equal(2, chunks.Count);
        Assert.Equal(("abc", "FG1", false), chunks[0]);
        Assert.Equal(("def", "FG1", true), chunks[1]);
    }

    [Fact]
    public void MergeChunks_WithoutInline_PassesRunsThrough()
    {
        var runs = new List<SyntaxChunk> { new("one", "A"), new("two", null) };

        var chunks = AnsiRenderer.MergeChunks(runs, inline: null).ToList();

        Assert.Equal(2, chunks.Count);
        Assert.Equal(("one", "A", false), chunks[0]);
        Assert.Equal(("two", (string?)null, false), chunks[1]);
    }
}
