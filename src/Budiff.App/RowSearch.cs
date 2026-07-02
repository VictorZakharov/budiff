using System;
using System.Collections.Generic;

namespace Budiff.App;

// The in-place bird's-eye search: two passes over already-built rows setting
// match/highlight/visibility/expansion state via observable properties, so
// the TreeView keeps its containers and keystrokes never rebuild the tree.
// Pure logic over CodeAnalysisRow trees - unit-tested directly.
internal static class RowSearch
{
    // A broad query (the first 1-2 typed characters match almost every name) is
    // not a useful filter yet, but hiding/expanding rows for it forces the
    // non-virtualized TreeView through a layout pass over every realized row -
    // the typing lag. So while matches exceed this threshold the search only
    // HIGHLIGHTS in place; filtering and match-path expansion kick in together
    // once the match set is narrow enough to be worth scanning.
    internal const int ExpandThreshold = 64;

    // Pass 1: match state per row (the filter decision needs the TOTAL count
    // first). Returns the match count; an empty query clears all match state.
    public static int ComputeMatches(IEnumerable<CodeAnalysisRow> roots, string query)
    {
        var matches = 0;
        foreach (var row in roots)
        {
            Compute(row, query, ref matches);
        }

        return matches;
    }

    private static bool Compute(CodeAnalysisRow row, string query, ref int matches)
    {
        var index = query.Length > 0 ? row.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) : -1;
        if (index >= 0)
        {
            matches++;
        }

        var descendantMatch = false;
        foreach (var child in row.Children)
        {
            descendantMatch |= Compute(child, query, ref matches);
        }

        row.MatchIndex = index;
        row.HasDescendantMatch = descendantMatch;
        return index >= 0 || descendantMatch;
    }

    // Pass 2: highlight always; hide non-matching rows only in filter (narrow)
    // mode; when applyExpansion (searching, or a search just ended) also set
    // folder expansion - match paths in narrow mode, the captured user
    // expansion otherwise. filter is only ever true while searching.
    public static void Apply(
        IEnumerable<CodeAnalysisRow> roots,
        string query,
        bool filter,
        bool applyExpansion,
        IReadOnlyCollection<string> expandedPaths)
    {
        foreach (var row in roots)
        {
            ApplyToRow(row, query, ancestorMatched: false, filter, applyExpansion, expandedPaths);
        }
    }

    private static void ApplyToRow(
        CodeAnalysisRow row,
        string query,
        bool ancestorMatched,
        bool filter,
        bool applyExpansion,
        IReadOnlyCollection<string> expandedPaths)
    {
        var matched = row.MatchIndex >= 0;
        row.SetNameHighlight(row.MatchIndex, query.Length);
        row.IsSearchVisible = !filter || matched || row.HasDescendantMatch || ancestorMatched;
        if (applyExpansion && !row.IsFile)
        {
            row.IsExpanded = filter ? row.HasDescendantMatch : expandedPaths.Contains(row.RelativePath);
        }

        foreach (var child in row.Children)
        {
            ApplyToRow(child, query, ancestorMatched || matched, filter, applyExpansion, expandedPaths);
        }
    }
}
