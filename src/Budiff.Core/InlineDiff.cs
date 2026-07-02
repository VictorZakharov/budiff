using System;
using System.Collections.Generic;

namespace Budiff;

public static class InlineDiff
{
    private const int MaxExactChars = 1200;

    public static InlineDiffPair Compute(string? oldText, string? newText)
    {
        oldText ??= string.Empty;
        newText ??= string.Empty;
        if (oldText == newText)
        {
            return new InlineDiffPair(
                new[] { new InlineSegment(oldText, InlineChangeKind.Unchanged) },
                new[] { new InlineSegment(newText, InlineChangeKind.Unchanged) });
        }

        if (oldText.Length * newText.Length > MaxExactChars * MaxExactChars)
        {
            return PrefixSuffix(oldText, newText);
        }

        return Lcs(oldText, newText);
    }

    private static InlineDiffPair PrefixSuffix(string oldText, string newText)
    {
        var prefix = 0;
        var maxPrefix = Math.Min(oldText.Length, newText.Length);
        while (prefix < maxPrefix && oldText[prefix] == newText[prefix])
        {
            prefix++;
        }

        var oldSuffix = oldText.Length - 1;
        var newSuffix = newText.Length - 1;
        while (oldSuffix >= prefix && newSuffix >= prefix && oldText[oldSuffix] == newText[newSuffix])
        {
            oldSuffix--;
            newSuffix--;
        }

        var oldRuns = new List<InlineSegment>();
        var newRuns = new List<InlineSegment>();
        Add(oldRuns, oldText[..prefix], InlineChangeKind.Unchanged);
        Add(newRuns, newText[..prefix], InlineChangeKind.Unchanged);
        Add(oldRuns, oldText[prefix..(oldSuffix + 1)], InlineChangeKind.Removed);
        Add(newRuns, newText[prefix..(newSuffix + 1)], InlineChangeKind.Added);
        Add(oldRuns, oldText[(oldSuffix + 1)..], InlineChangeKind.Unchanged);
        Add(newRuns, newText[(newSuffix + 1)..], InlineChangeKind.Unchanged);
        return new InlineDiffPair(oldRuns, newRuns);
    }

    private static InlineDiffPair Lcs(string oldText, string newText)
    {
        var n = oldText.Length;
        var m = newText.Length;
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                dp[i, j] = oldText[i] == newText[j]
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        // The backtrack visits each side's characters in order, so runs are
        // contiguous index ranges - collect (kind, start, end) and slice each
        // run once instead of concatenating char-by-char (O(len^2) copies).
        var oldRuns = new RunBuilder(oldText);
        var newRuns = new RunBuilder(newText);
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (oldText[x] == newText[y])
            {
                oldRuns.Append(x++, InlineChangeKind.Unchanged);
                newRuns.Append(y++, InlineChangeKind.Unchanged);
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                oldRuns.Append(x++, InlineChangeKind.Removed);
            }
            else
            {
                newRuns.Append(y++, InlineChangeKind.Added);
            }
        }

        while (x < n)
        {
            oldRuns.Append(x++, InlineChangeKind.Removed);
        }

        while (y < m)
        {
            newRuns.Append(y++, InlineChangeKind.Added);
        }

        return new InlineDiffPair(oldRuns.Build(), newRuns.Build());
    }

    private sealed class RunBuilder
    {
        private readonly string _source;
        private readonly List<InlineSegment> _runs = new();
        private InlineChangeKind _kind;
        private int _start;
        private int _end;

        public RunBuilder(string source) => _source = source;

        public void Append(int index, InlineChangeKind kind)
        {
            if (_end > _start && kind == _kind && index == _end)
            {
                _end++;
                return;
            }

            Flush();
            _kind = kind;
            _start = index;
            _end = index + 1;
        }

        public IReadOnlyList<InlineSegment> Build()
        {
            Flush();
            return _runs;
        }

        private void Flush()
        {
            if (_end > _start)
            {
                _runs.Add(new InlineSegment(_source[_start.._end], _kind));
                _start = _end;
            }
        }
    }

    private static void Add(List<InlineSegment> runs, string text, InlineChangeKind kind)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (runs.Count > 0 && runs[^1].Kind == kind)
        {
            runs[^1] = runs[^1] with { Text = runs[^1].Text + text };
        }
        else
        {
            runs.Add(new InlineSegment(text, kind));
        }
    }
}
