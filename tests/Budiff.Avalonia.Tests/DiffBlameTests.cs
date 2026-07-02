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
