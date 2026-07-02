using System.Collections.Generic;
using Budiff;

namespace Budiff.Avalonia;

// The collapse-unchanged display plan, separated from row materialization so
// the context/gap math is directly unit-testable: each item is either the
// index of a visible source row or a gap standing in for Hidden unchanged rows.
public static class DiffRowCollapser
{
    public readonly record struct Item(int Index, int GapRunStart, int Hidden)
    {
        public bool IsGap => Hidden > 0;

        public static Item Content(int index) => new(index, 0, 0);

        public static Item Gap(int runStart, int hidden) => new(-1, runStart, hidden);
    }

    // A run of unchanged rows longer than 2*contextLines+1 folds into
    // lead context + one gap + trail context; the leading run of the file
    // keeps no lead context and the trailing run no trail context. Runs the
    // user expanded (keyed by run start index) stay fully visible.
    public static List<Item> Build(
        IReadOnlyList<DiffRow> all,
        bool collapseUnchanged,
        int contextLines,
        IReadOnlyCollection<int> expandedGaps)
    {
        var items = new List<Item>(all.Count);
        var n = all.Count;
        var i = 0;
        while (i < n)
        {
            if (!collapseUnchanged || all[i].Kind != DiffKind.Unchanged)
            {
                items.Add(Item.Content(i));
                i++;
                continue;
            }

            var j = i;
            while (j < n && all[j].Kind == DiffKind.Unchanged)
            {
                j++;
            }

            var runLen = j - i;
            if (runLen > (2 * contextLines) + 1 && !expandedGaps.Contains(i))
            {
                var lead = i == 0 ? 0 : contextLines;
                var trail = j == n ? 0 : contextLines;
                for (var k = i; k < i + lead; k++)
                {
                    items.Add(Item.Content(k));
                }

                var hidden = runLen - lead - trail;
                if (hidden > 0)
                {
                    items.Add(Item.Gap(i, hidden));
                }

                for (var k = j - trail; k < j; k++)
                {
                    items.Add(Item.Content(k));
                }
            }
            else
            {
                for (var k = i; k < j; k++)
                {
                    items.Add(Item.Content(k));
                }
            }

            i = j;
        }

        return items;
    }

    public static string GapText(int hidden)
        => $"...   {hidden} unchanged line{(hidden == 1 ? string.Empty : "s")}   -   click to expand   ...";
}
