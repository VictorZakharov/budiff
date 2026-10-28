using Avalonia.Headless.XUnit;
using Budiff;
using Xunit;

namespace Budiff.Avalonia.Tests;

// Moving to another file: the old file's rows keep their blame until the new
// rows land, and those land with their own blame - the column never flashes
// empty in between. A blame slower than the wait fills in afterwards, the
// column keeping its width meanwhile.
public sealed class BlameSwapTests
{
    [AvaloniaFact]
    public void NextFile_RowsAndBlameChangeTogether()
    {
        var model = OnFileA();
        var blankMoments = 0;
        var landedWithBlame = false;
        model.Rows.CollectionChanged += (_, _) =>
        {
            if (model.Rows.Count > 0 && model.Rows.All(r => r.Blame is null))
            {
                blankMoments++;
            }

            landedWithBlame |= model.Rows.Any(r => r.Source?.NewText == "b2") && model.Rows.Any(r => r.Blame?.Author == "Grace");
        };

        var blame = Task.Run(async () =>
        {
            await Task.Delay(50);
            return new BlameSides(new[] { Grace, Grace }, null);
        });
        model.BlameWaitMs = 5000;
        model.BeginLoad(() => DiffResult.FromRows(LineDiff.Compute("b1\nb\n", "b1\nb2\n")), blame);
        PumpUntil(() => model.Rows.Any(r => r.Source?.NewText == "b2"));

        Assert.True(landedWithBlame);
        Assert.Equal(0, blankMoments);
    }

    [AvaloniaFact]
    public void NextFile_SlowBlame_FillsInAfter_KeepingTheColumnWidth()
    {
        var model = OnFileA();
        var width = model.BlameLabelWidth;
        var slow = new TaskCompletionSource<BlameSides>();
        model.BlameWaitMs = 0;
        model.BeginLoad(() => DiffResult.FromRows(LineDiff.Compute("b1\nb\n", "b1\nb2\n")), slow.Task);
        PumpUntil(() => model.Rows.Any(r => r.Source?.NewText == "b2"));

        // The old file's blame is not shown against the new file's lines.
        Assert.All(model.Rows, r => Assert.Null(r.Blame));
        Assert.Equal(width, model.BlameLabelWidth);

        slow.SetResult(new BlameSides(new[] { Grace, Grace }, null));
        PumpUntil(() => model.Rows.Any(r => r.Blame?.Author == "Grace"));
    }

    private static readonly BlameEntry Ada = new("Ada 10d", "abc1234", "Ada", "ada@example.com", "2026-06-22 10:00", "add");
    private static readonly BlameEntry Grace = new("Grace 2d", "def5678", "Grace", "grace@example.com", "2026-06-30 10:00", "change");

    private static DiffViewModel OnFileA()
    {
        var model = new DiffViewModel();
        model.SetDiff(LineDiff.Compute("a1\na\n", "a1\na2\n"));
        model.SetBlame(new[] { Ada, Ada });
        Assert.Contains(model.Rows, r => r.Blame?.Author == "Ada");
        return model;
    }

    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            Thread.Sleep(10);
            UiSnapshot.Pump();
        }
    }
}
