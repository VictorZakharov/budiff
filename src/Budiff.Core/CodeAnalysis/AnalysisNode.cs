using System;
using System.Collections.Generic;

namespace Budiff.CodeAnalysis;

public enum AnalysisGrouping
{
    Folders,
    Projects,
}

public enum AnalysisSortKey
{
    Name,
    Lines,
    Code,
    Blank,
    Comment,
    Files,
    Added,
    Removed,
    Commits,
    Authors,
    Recency,
}

public enum AnalysisNodeKind
{
    Root,
    Folder,
    Project,
    File,
    Ungrouped,
}

public sealed class AnalysisNode
{
    public AnalysisNode(string name, string relativePath, AnalysisNodeKind kind)
    {
        Name = name;
        RelativePath = relativePath;
        Kind = kind;
    }

    public string Name { get; }

    public string RelativePath { get; }

    public AnalysisNodeKind Kind { get; }

    public List<AnalysisNode> Children { get; } = new();

    public bool IsFile => Kind == AnalysisNodeKind.File;

    public LineCounts Lines { get; set; }

    public int FileCount { get; set; }

    public bool IsBinary { get; set; }

    public long Added { get; set; }

    public long Removed { get; set; }

    public HashSet<int> CommitIds { get; } = new();

    public HashSet<string> Authors { get; } = new(StringComparer.OrdinalIgnoreCase);

    public long Churn => Added + Removed;

    public int CommitCount => CommitIds.Count;

    public int AuthorCount => Authors.Count;

    // The newest commit under it, as Unix seconds (0 = none in the window).
    public long LastChanged { get; set; }

    public double BarFraction { get; private set; }

    public double SortValue(AnalysisSortKey key) => key switch
    {
        AnalysisSortKey.Lines => Lines.Total,
        AnalysisSortKey.Code => Lines.Code,
        AnalysisSortKey.Blank => Lines.Blank,
        AnalysisSortKey.Comment => Lines.Comment,
        AnalysisSortKey.Files => FileCount,
        AnalysisSortKey.Added => Added,
        AnalysisSortKey.Removed => Removed,
        AnalysisSortKey.Commits => CommitCount,
        AnalysisSortKey.Authors => AuthorCount,
        AnalysisSortKey.Recency => LastChanged,
        _ => Lines.Total,
    };

    public void Aggregate()
    {
        if (IsFile)
        {
            return;
        }

        var lines = new LineCounts();
        var files = 0;
        long added = 0;
        long removed = 0;
        foreach (var child in Children)
        {
            child.Aggregate();
            lines += child.Lines;
            files += child.FileCount;
            added += child.Added;
            removed += child.Removed;
            CommitIds.UnionWith(child.CommitIds);
            Authors.UnionWith(child.Authors);
            LastChanged = Math.Max(LastChanged, child.LastChanged);
        }

        Lines = lines;
        FileCount = files;
        Added = added;
        Removed = removed;
    }

    // Depth-first walk with depth, in display order - the shape both the CLI
    // renderer and the MCP overview flatten the tree into.
    public static IEnumerable<(AnalysisNode Node, int Depth)> Flatten(IEnumerable<AnalysisNode> nodes, int depth = 0)
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

    public void SortAndScale(AnalysisSortKey key, bool ascending)
    {
        if (Children.Count == 0)
        {
            return;
        }

        Comparison<AnalysisNode> ascendingCompare = (a, b) =>
        {
            if (key == AnalysisSortKey.Name)
            {
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            }

            var cmp = a.SortValue(key).CompareTo(b.SortValue(key));
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = a.Lines.Total.CompareTo(b.Lines.Total);
            return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        };

        Children.Sort(ascending ? ascendingCompare : (a, b) => ascendingCompare(b, a));

        // A date is no size: by name or recency, the bar shows lines.
        var barKey = key is AnalysisSortKey.Name or AnalysisSortKey.Recency ? AnalysisSortKey.Lines : key;
        var max = 0.0;
        foreach (var child in Children)
        {
            max = Math.Max(max, child.SortValue(barKey));
        }

        foreach (var child in Children)
        {
            child.BarFraction = max > 0 ? child.SortValue(barKey) / max : 0;
            child.SortAndScale(key, ascending);
        }
    }
}
