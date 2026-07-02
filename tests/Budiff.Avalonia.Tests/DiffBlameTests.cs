using System.Linq;
using Budiff;
using Xunit;

namespace Budiff.Avalonia.Tests;

public sealed class DiffBlameTests
{
    [Fact]
    public void SetBlame_MapsOldLineNumbersToRows_AndClears()
    {
        var model = new DiffViewModel();
        model.SetDiff(LineDiff.Compute("one\ntwo\nthree\n", "one\ntwo CHANGED\nthree\n"));

        var ada = new BlameEntry("Ada 10d", "abc1234", "Ada", "ada@example.com", "2026-06-22 10:00", "add");
        var grace = new BlameEntry("Grace 2d", "def5678", "Grace", "grace@example.com", "2026-06-30 10:00", "change");
        model.SetBlame(new[] { ada, grace, ada });

        var byOldNo = model.Rows.Where(r => r.OldNo is not null).ToDictionary(r => r.OldNo!);
        Assert.Equal("Ada", byOldNo["1"].Blame!.Author);
        Assert.Equal("Grace", byOldNo["2"].Blame!.Author);
        Assert.Equal("Ada", byOldNo["3"].Blame!.Author);

        // A collapse/expand rebuild keeps blame (Content() reads the stored lines).
        model.CollapseUnchanged = false;
        Assert.Equal("Grace", model.Rows.Where(r => r.OldNo == "2").Single().Blame!.Author);

        model.SetBlame(null);
        Assert.All(model.Rows, r => Assert.Null(r.Blame));
    }

    [Fact]
    public void SetBlame_ForNewSide_MapsByNewLineNumbers()
    {
        var model = new DiffViewModel();
        // Wholly new file: rows only have NEW line numbers.
        model.SetDiff(LineDiff.Compute(string.Empty, "alpha\nbeta\n"));

        var ada = new BlameEntry("Ada 1d", "abc1234", "Ada", "ada@example.com", "2026-07-01 10:00", "add");
        var uncommitted = new BlameEntry("uncommitted", "0000000", "Not committed yet", "", "", "uncommitted changes");
        model.SetBlame(new[] { ada, uncommitted }, forNewSide: true);

        var byNewNo = model.Rows.Where(r => r.NewNo is not null).ToDictionary(r => r.NewNo!);
        Assert.Equal("Ada", byNewNo["1"].Blame!.Author);
        Assert.Equal("uncommitted", byNewNo["2"].Blame!.Label);
    }

    [Fact]
    public void SetDiff_IdenticalReload_KeepsExistingRows()
    {
        var model = new DiffViewModel();
        model.SetDiff(LineDiff.Compute("a\nb\n", "a\nc\n"));
        var before = model.Rows[0];

        // Same content again (e.g. working-tree toggle with a clean tree):
        // nothing rebuilds, so nothing can flicker.
        model.SetDiff(LineDiff.Compute("a\nb\n", "a\nc\n"));

        Assert.Same(before, model.Rows[0]);
    }

    [Fact]
    public void BlameCheckbox_TracksAutoHide_AndUserCanOverride()
    {
        var model = new DiffViewModel();
        Assert.True(model.IsBlameVisible);

        // Crossing into cramped auto-hides AND unchecks the box.
        model.BlameCramped = true;
        Assert.False(model.ShowBlame);
        Assert.False(model.IsBlameVisible);

        // The user can override while cramped; layout re-checks that do not
        // cross the boundary must not undo the override.
        model.ShowBlame = true;
        Assert.True(model.IsBlameVisible);
        model.BlameCramped = true;
        Assert.True(model.IsBlameVisible);

        // Crossing back to roomy auto-shows (and re-checks the box).
        model.ShowBlame = false;
        model.BlameCramped = false;
        Assert.True(model.ShowBlame);
        Assert.True(model.IsBlameVisible);
    }
}
