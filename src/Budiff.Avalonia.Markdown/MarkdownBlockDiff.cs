using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Budiff;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Parsers;
using Markdig.Syntax;

namespace Budiff.Avalonia.Markdown;

public enum BlockChange
{
    Unchanged,
    Removed,
    Added,
}

// One rendered unit of a document: a top-level block, or one item of a
// top-level list (so adding a bullet marks just that bullet). Lines are
// 0-based and inclusive. Number is the item's display number in an ordered
// list.
public sealed record PreviewBlock(Block Block, int FirstLine, int LastLine, ListBlock? List, int Number, BlockChange Change)
{
    public bool IsListItem => List is not null;
}

// Diffs two markdown documents BLOCK by block: each block's normalized text
// (list markers dropped, prose whitespace collapsed) is one "line" for the
// line diff, so a renumbered list or a reflowed paragraph still matches. A
// matched block shows once; a changed one shows its old version (removed)
// then its new version (added), where it sits in the document.
public static class MarkdownBlockDiff
{
    private static readonly Regex ListMarker = new(@"^\s*(?:[-*+]|\d+[.)])\s*");
    private static readonly Regex Whitespace = new(@"\s+");

    public static MarkdownPipeline Pipeline { get; } = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseTaskLists()
        .UseAutoLinks()
        .UseYamlFrontMatter()
        .UseAlertBlocks()
        .UsePreciseSourceLocation()
        .Build();

    public static IReadOnlyList<PreviewBlock> Build(DocumentPreviewRequest request)
    {
        var plain = DocumentKind.IsPlainText(request.FileName, request.NewText.Length > 0 ? request.NewText : request.OldText);
        IReadOnlyList<PreviewBlock> Of(string text, BlockChange change) => plain ? PlainUnits(text, change) : Units(text, change);
        switch (request.Mode)
        {
            case DocumentPreviewMode.NewOnly:
                return Of(request.NewText, BlockChange.Unchanged);
            case DocumentPreviewMode.OldOnly:
                return Of(request.OldText, BlockChange.Unchanged);
        }

        var oldUnits = Of(request.OldText, BlockChange.Removed);
        var newUnits = Of(request.NewText, BlockChange.Added);
        var rows = LineDiff.Compute(Keys(oldUnits, request.OldText), Keys(newUnits, request.NewText));

        var result = new List<PreviewBlock>(Math.Max(oldUnits.Count, newUnits.Count));
        foreach (var row in rows)
        {
            var oldUnit = Pick(oldUnits, row.OldNo);
            var newUnit = Pick(newUnits, row.NewNo);
            if (row.Kind == DiffKind.Unchanged)
            {
                if (newUnit is not null)
                {
                    result.Add(newUnit with { Change = BlockChange.Unchanged });
                }

                continue;
            }

            if (oldUnit is not null)
            {
                result.Add(oldUnit);
            }

            if (newUnit is not null)
            {
                result.Add(newUnit);
            }
        }

        return result;
    }

    // LineNo is 1-based; an empty side diffs as one phantom empty line.
    private static PreviewBlock? Pick(IReadOnlyList<PreviewBlock> units, int? lineNo)
        => lineNo is { } n && n >= 1 && n <= units.Count ? units[n - 1] : null;

    private static string Keys(IReadOnlyList<PreviewBlock> units, string text)
    {
        var keys = new StringBuilder();
        foreach (var unit in units)
        {
            if (keys.Length > 0)
            {
                keys.Append('\n');
            }

            keys.Append(Key(unit, text));
        }

        return keys.ToString();
    }

    // The block's identity for matching: its type plus its source, list
    // marker dropped; code keeps its whitespace, prose does not.
    internal static string Key(PreviewBlock unit, string text)
    {
        var block = unit.Block;
        var start = Math.Clamp(block.Span.Start, 0, text.Length);
        var end = Math.Clamp(block.Span.End + 1, start, text.Length);
        var source = text[start..end];
        if (unit.IsListItem)
        {
            source = ListMarker.Replace(source, string.Empty, 1);
        }

        source = block is CodeBlock or PlainTextBlock
            ? source.Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', '\u0001')
            : Whitespace.Replace(source, " ").Trim();
        return block.GetType().Name + ":" + source;
    }

    public static IReadOnlyList<PreviewBlock> Units(string text, BlockChange change)
    {
        var document = Markdig.Markdown.Parse(text, Pipeline);
        var lineStarts = LineStarts(text);
        var units = new List<PreviewBlock>();
        foreach (var block in document)
        {
            if (block is LinkReferenceDefinitionGroup)
            {
                continue;
            }

            if (block is ListBlock list)
            {
                var number = ListStart(list);
                foreach (var item in list)
                {
                    units.Add(new PreviewBlock(item, item.Line, LastLine(item, lineStarts), list, number++, change));
                }

                continue;
            }

            units.Add(new PreviewBlock(block, block.Line, LastLine(block, lineStarts), null, 0, change));
        }

        return units;
    }

    // A plain-text document's units: its paragraphs (runs of non-blank lines).
    public static IReadOnlyList<PreviewBlock> PlainUnits(string text, BlockChange change)
    {
        var units = new List<PreviewBlock>();
        var lines = text.Split('\n');
        var lineStart = 0;
        var first = -1;
        var spanStart = 0;
        for (var i = 0; i <= lines.Length; i++)
        {
            var blank = i == lines.Length || lines[i].Trim().Length == 0;
            if (!blank && first < 0)
            {
                first = i;
                spanStart = lineStart;
            }
            else if (blank && first >= 0)
            {
                var spanEnd = lineStart - 2;
                var body = text[spanStart..(spanEnd + 1)].Replace("\r", string.Empty, StringComparison.Ordinal);
                units.Add(new PreviewBlock(new PlainTextBlock(body, spanStart, spanEnd, first), first, i - 1, null, 0, change));
                first = -1;
            }

            if (i < lines.Length)
            {
                lineStart += lines[i].Length + 1;
            }
        }

        return units;
    }

    private static int ListStart(ListBlock list)
        => list.IsOrdered && int.TryParse(list.OrderedStart ?? list.DefaultOrderedStart, out var start) ? start : 1;

    private static int LastLine(Block block, int[] lineStarts)
    {
        var end = block.Span.End;
        if (end < block.Span.Start)
        {
            return block.Line;
        }

        var index = Array.BinarySearch(lineStarts, end);
        var line = index >= 0 ? index : ~index - 1;
        return Math.Max(block.Line, line);
    }

    private static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts.ToArray();
    }

}
