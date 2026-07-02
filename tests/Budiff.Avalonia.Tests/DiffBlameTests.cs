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

        var ada = new BlameEntry("Ada 10d", "abc1234", "Ada", "2026-06-22 10:00", "add");
        var grace = new BlameEntry("Grace 2d", "def5678", "Grace", "2026-06-30 10:00", "change");
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
    public void IsBlameVisible_RequiresToggleAndRoom()
    {
        var model = new DiffViewModel();
        Assert.True(model.IsBlameVisible);

        // A cramped pane hides blame even while toggled on...
        model.BlameCramped = true;
        Assert.False(model.IsBlameVisible);

        // ...and the toggle still wins when there is room again.
        model.BlameCramped = false;
        Assert.True(model.IsBlameVisible);
        model.ShowBlame = false;
        Assert.False(model.IsBlameVisible);
    }
}
