using System;
using System.Collections.Generic;

namespace Budiff.CodeAnalysis;

public readonly record struct LineCounts(int Code, int Blank, int Comment)
{
    public int Total => Code + Blank + Comment;

    public static LineCounts operator +(LineCounts a, LineCounts b)
        => new(a.Code + b.Code, a.Blank + b.Blank, a.Comment + b.Comment);
}

public static class LineCounter
{
    public static LineCounts Count(string text, CommentSyntax syntax)
        => Count(SplitLines(text), syntax);

    public static LineCounts Count(IEnumerable<string> lines, CommentSyntax syntax)
    {
        int code = 0, blank = 0, comment = 0;
        var inBlock = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                blank++;
                continue;
            }

            if (!syntax.RecognisesComments)
            {
                code++;
                continue;
            }

            if (inBlock)
            {
                comment++;
                if (syntax.BlockEnd is { } end && line.Contains(end, StringComparison.Ordinal))
                {
                    inBlock = false;
                }

                continue;
            }

            if (StartsWithLineComment(line, syntax))
            {
                comment++;
                continue;
            }

            if (syntax.BlockStart is { } start && line.StartsWith(start, StringComparison.Ordinal))
            {
                comment++;
                var afterStart = line[start.Length..];
                inBlock = syntax.BlockEnd is not { } end || !afterStart.Contains(end, StringComparison.Ordinal);
                continue;
            }

            code++;
        }

        return new LineCounts(code, blank, comment);
    }

    public static IEnumerable<string> SplitLines(string text)
    {
        if (text.Length == 0)
        {
            yield break;
        }

        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            var end = i;
            if (end > start && text[end - 1] == '\r')
            {
                end--;
            }

            yield return text[start..end];
            start = i + 1;
        }

        if (start < text.Length)
        {
            var end = text.Length;
            if (text[end - 1] == '\r')
            {
                end--;
            }

            yield return text[start..end];
        }
    }

    private static bool StartsWithLineComment(string trimmedLine, CommentSyntax syntax)
    {
        foreach (var marker in syntax.LineComments)
        {
            if (trimmedLine.StartsWith(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
