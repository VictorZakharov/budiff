using System;
using System.Collections.Generic;

namespace Budiff;

/// <summary>
/// Line-level side-by-side diff. Equal lines align, and a run of removals immediately followed by
/// additions is zipped into modified rows.
/// </summary>
public static class LineDiff
{
    private const int MaxLcsLines = 2000;
    private const int MaxEditDistance = 3000;

    public static IReadOnlyList<DiffRow> Compute(string oldText, string newText)
    {
        // A missing side (an added or deleted file) is no lines at all, not one
        // empty line: left to the LCS, that empty line would pair with the other
        // side's first blank line into an Unchanged row mid-file, numbered and
        // marked as if it existed on both sides.
        if (string.IsNullOrEmpty(oldText) != string.IsNullOrEmpty(newText))
        {
            return WholeSideRows(string.IsNullOrEmpty(oldText) ? newText : oldText, added: string.IsNullOrEmpty(oldText));
        }

        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);

        var ops = oldLines.Length <= MaxLcsLines && newLines.Length <= MaxLcsLines
            ? BuildLcsOps(oldLines, newLines)
            : BuildMyersOps(oldLines, newLines);

        return ops is null ? NaiveRows(oldLines, newLines) : PairRows(ops, oldLines, newLines);
    }

    // Split on \n, \r\n, or lone \r. Note "" yields [""] - a single empty line,
    // like string.Split. Compute handles a missing side before splitting, so
    // that line never pairs with the other side's blanks. Manual scan to avoid the two full
    // normalization copies the old Replace/Replace/Split made per file.
    public static string[] SplitLines(string text)
    {
        text ??= string.Empty;
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch != '\n' && ch != '\r')
            {
                continue;
            }

            lines.Add(text[start..i]);
            if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            start = i + 1;
        }

        lines.Add(text[start..]);
        return lines.ToArray();
    }

    // Every line of the one populated side, all Added or all Removed. The
    // trailing newline gets no blank row of its own (as in DiffLoader.BuildView):
    // there is no other side for it to end alongside.
    private static List<DiffRow> WholeSideRows(string text, bool added)
    {
        var lines = SplitLines(text);
        var count = lines.Length > 1 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        var rows = new List<DiffRow>(count);
        for (var i = 0; i < count; i++)
        {
            rows.Add(added
                ? new DiffRow(null, null, i + 1, lines[i], DiffKind.Added)
                : new DiffRow(i + 1, lines[i], null, null, DiffKind.Removed));
        }

        return rows;
    }

    private readonly record struct Op(int Kind, int OldIdx, int NewIdx);

    private static List<DiffRow> PairRows(List<Op> ops, string[] oldLines, string[] newLines)
    {
        var rows = new List<DiffRow>();
        var deletes = new List<(int Idx, string Text)>();
        var inserts = new List<(int Idx, string Text)>();

        void Flush()
        {
            var max = Math.Max(deletes.Count, inserts.Count);
            for (var k = 0; k < max; k++)
            {
                var hasDel = k < deletes.Count;
                var hasIns = k < inserts.Count;
                if (hasDel && hasIns)
                {
                    rows.Add(new DiffRow(
                        deletes[k].Idx + 1,
                        deletes[k].Text,
                        inserts[k].Idx + 1,
                        inserts[k].Text,
                        DiffKind.Modified));
                }
                else if (hasDel)
                {
                    rows.Add(new DiffRow(deletes[k].Idx + 1, deletes[k].Text, null, null, DiffKind.Removed));
                }
                else
                {
                    rows.Add(new DiffRow(null, null, inserts[k].Idx + 1, inserts[k].Text, DiffKind.Added));
                }
            }

            deletes.Clear();
            inserts.Clear();
        }

        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case 0:
                    Flush();
                    rows.Add(new DiffRow(
                        op.OldIdx + 1,
                        oldLines[op.OldIdx],
                        op.NewIdx + 1,
                        newLines[op.NewIdx],
                        DiffKind.Unchanged));
                    break;
                case 1:
                    deletes.Add((op.OldIdx, oldLines[op.OldIdx]));
                    break;
                default:
                    inserts.Add((op.NewIdx, newLines[op.NewIdx]));
                    break;
            }
        }

        Flush();
        return rows;
    }

    private static List<DiffRow> NaiveRows(string[] oldLines, string[] newLines)
    {
        var rows = new List<DiffRow>();
        var max = Math.Max(oldLines.Length, newLines.Length);
        for (var i = 0; i < max; i++)
        {
            var oldText = i < oldLines.Length ? oldLines[i] : null;
            var newText = i < newLines.Length ? newLines[i] : null;
            var kind = oldText is null
                ? DiffKind.Added
                : newText is null
                    ? DiffKind.Removed
                    : oldText == newText
                        ? DiffKind.Unchanged
                        : DiffKind.Modified;

            rows.Add(new DiffRow(
                oldText is null ? null : i + 1,
                oldText,
                newText is null ? null : i + 1,
                newText,
                kind));
        }

        return rows;
    }

    private static List<Op> BuildLcsOps(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length;
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                dp[i, j] = a[i] == b[j]
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var ops = new List<Op>(n + m);
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y])
            {
                ops.Add(new Op(0, x, y));
                x++;
                y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                ops.Add(new Op(1, x, y));
                x++;
            }
            else
            {
                ops.Add(new Op(2, x, y));
                y++;
            }
        }

        while (x < n)
        {
            ops.Add(new Op(1, x, y));
            x++;
        }

        while (y < m)
        {
            ops.Add(new Op(2, x, y));
            y++;
        }

        return ops;
    }

    private static List<Op>? BuildMyersOps(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length;
        var cap = Math.Min(n + m, MaxEditDistance);
        var offset = cap;
        var v = new int[(2 * cap) + 1];
        var trace = new List<int[]>(cap + 1);

        var reachedD = -1;
        for (var d = 0; d <= cap; d++)
        {
            trace.Add((int[])v.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[k - 1 + offset] < v[k + 1 + offset]))
                {
                    x = v[k + 1 + offset];
                }
                else
                {
                    x = v[k - 1 + offset] + 1;
                }

                var y = x - k;
                while (x < n && y < m && a[x] == b[y])
                {
                    x++;
                    y++;
                }

                v[k + offset] = x;
                if (x >= n && y >= m)
                {
                    reachedD = d;
                    break;
                }
            }

            if (reachedD >= 0)
            {
                break;
            }
        }

        if (reachedD < 0)
        {
            return null;
        }

        var ops = new List<Op>(n + m);
        int px = n, py = m;
        for (var d = reachedD; d > 0; d--)
        {
            var vv = trace[d];
            var k = px - py;
            int prevK;
            if (k == -d || (k != d && vv[k - 1 + offset] < vv[k + 1 + offset]))
            {
                prevK = k + 1;
            }
            else
            {
                prevK = k - 1;
            }

            var prevX = vv[prevK + offset];
            var prevY = prevX - prevK;

            while (px > prevX && py > prevY)
            {
                ops.Add(new Op(0, px - 1, py - 1));
                px--;
                py--;
            }

            if (px == prevX)
            {
                ops.Add(new Op(2, px, py - 1));
            }
            else
            {
                ops.Add(new Op(1, px - 1, py));
            }

            px = prevX;
            py = prevY;
        }

        while (px > 0 && py > 0)
        {
            ops.Add(new Op(0, px - 1, py - 1));
            px--;
            py--;
        }

        ops.Reverse();
        return ops;
    }
}
