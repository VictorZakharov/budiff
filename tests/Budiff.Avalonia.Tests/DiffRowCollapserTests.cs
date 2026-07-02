using System.Collections.Generic;
using System.Linq;
using Budiff;
using Budiff.App;
using Xunit;

namespace Budiff.Avalonia.Tests;

// Direct tests for the two logic types extracted from the view models:
// the collapse-unchanged display plan and the in-place tree search pass.
public sealed class DiffRowCollapserTests
{
    private static List<DiffRow> Rows(params DiffKind[] kinds)
        => kinds.Select((kind, i) => new DiffRow(i + 1, "old", i + 1, "new", kind)).ToList();

    [Fact]
    public void Build_WithoutCollapse_KeepsEveryRow()
    {
        var all = Rows(DiffKind.Unchanged, DiffKind.Added, DiffKind.Unchanged);

        var plan = DiffRowCollapser.Build(all, collapseUnchanged: false, contextLines: 3, new HashSet<int>());

        Assert.Equal(new[] { 0, 1, 2 }, plan.Select(p => p.Index));
        Assert.All(plan, p => Assert.False(p.IsGap));
    }

    [Fact]
    public void Build_FoldsMiddleRun_WithLeadAndTrailContext()
    {
        var kinds = new List<DiffKind> { DiffKind.Added };
        kinds.AddRange(Enumerable.Repeat(DiffKind.Unchanged, 20));
        kinds.Add(DiffKind.Removed);

        var plan = DiffRowCollapser.Build(Rows(kinds.ToArray()), collapseUnchanged: true, contextLines: 3, new HashSet<int>());

        // changed + 3 lead + gap(14) + 3 trail + changed.
        Assert.Equal(9, plan.Count);
        var gap = Assert.Single(plan, p => p.IsGap);
        Assert.Equal(14, gap.Hidden);
        Assert.Equal(1, gap.GapRunStart);
        Assert.Equal("...   14 unchanged lines   -   click to expand   ...", DiffRowCollapser.GapText(gap.Hidden));
    }

    [Fact]
    public void Build_LeadingAndTrailingRuns_DropOuterContext()
    {
        var kinds = new List<DiffKind>(Enumerable.Repeat(DiffKind.Unchanged, 10)) { DiffKind.Modified };
        kinds.AddRange(Enumerable.Repeat(DiffKind.Unchanged, 10));

        var plan = DiffRowCollapser.Build(Rows(kinds.ToArray()), collapseUnchanged: true, contextLines: 3, new HashSet<int>());

        // File start: gap(7) + 3 context; file end: 3 context + gap(7).
        Assert.True(plan[0].IsGap);
        Assert.Equal(7, plan[0].Hidden);
        Assert.True(plan[^1].IsGap);
        Assert.Equal(7, plan[^1].Hidden);
        Assert.Equal(3 + 1 + 3, plan.Count(p => !p.IsGap));
    }

    [Fact]
    public void Build_ExpandedGap_StaysFullyVisible()
    {
        var kinds = new List<DiffKind> { DiffKind.Added };
        kinds.AddRange(Enumerable.Repeat(DiffKind.Unchanged, 20));

        var plan = DiffRowCollapser.Build(Rows(kinds.ToArray()), collapseUnchanged: true, contextLines: 3, new HashSet<int> { 1 });

        Assert.Equal(21, plan.Count);
        Assert.All(plan, p => Assert.False(p.IsGap));
    }

    [Fact]
    public void GapText_SingularForOneLine()
        => Assert.Contains("1 unchanged line ", DiffRowCollapser.GapText(1) + " ");
}

public sealed class RowSearchTests
{
    private static CodeAnalysisRow Folder(string name, params CodeAnalysisRow[] children)
    {
        var row = new CodeAnalysisRow { Name = name, RelativePath = name };
        foreach (var child in children)
        {
            row.Children.Add(child);
        }

        return row;
    }

    private static CodeAnalysisRow File(string name, string path)
        => new() { Name = name, RelativePath = path, IsFile = true };

    [Fact]
    public void ComputeMatches_CountsAndMarksMatchPaths()
    {
        var roots = new[]
        {
            Folder("src", File("Program.cs", "src/Program.cs"), File("readme.md", "src/readme.md")),
            Folder("docs", File("intro.md", "docs/intro.md")),
        };

        var matches = RowSearch.ComputeMatches(roots, "program");

        Assert.Equal(1, matches);
        Assert.True(roots[0].HasDescendantMatch);
        Assert.False(roots[1].HasDescendantMatch);
        Assert.True(roots[0].Children[0].MatchIndex >= 0);
        Assert.Equal(-1, roots[1].Children[0].MatchIndex);
    }

    [Fact]
    public void Apply_NarrowFilter_HidesNonMatches_AndExpandsMatchPaths()
    {
        var roots = new[]
        {
            Folder("src", File("Program.cs", "src/Program.cs")),
            Folder("docs", File("intro.md", "docs/intro.md")),
        };
        RowSearch.ComputeMatches(roots, "program");

        RowSearch.Apply(roots, "program", filter: true, applyExpansion: true, new HashSet<string>());

        Assert.True(roots[0].IsSearchVisible);
        Assert.True(roots[0].IsExpanded);
        Assert.True(roots[0].Children[0].IsSearchVisible);
        Assert.False(roots[1].IsSearchVisible);
        Assert.False(roots[1].IsExpanded);
    }

    [Fact]
    public void Apply_AfterSearchEnds_RestoresCapturedExpansion()
    {
        var roots = new[] { Folder("src", File("a.cs", "src/a.cs")), Folder("docs", File("b.md", "docs/b.md")) };
        RowSearch.ComputeMatches(roots, string.Empty);

        RowSearch.Apply(roots, string.Empty, filter: false, applyExpansion: true, new HashSet<string> { "docs" });

        Assert.False(roots[0].IsExpanded);
        Assert.True(roots[1].IsExpanded);
        Assert.All(roots, r => Assert.True(r.IsSearchVisible));
    }
}
