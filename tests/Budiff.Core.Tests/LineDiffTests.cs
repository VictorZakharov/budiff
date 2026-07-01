using Budiff;
using Xunit;

namespace Budiff.Core.Tests;

public sealed class LineDiffTests
{
    [Fact]
    public void Compute_ZipsDeleteInsertRunIntoModifiedRows()
    {
        var rows = LineDiff.Compute("one\ntwo\nthree", "one\nTWO\nthree");

        Assert.Collection(rows,
            row => Assert.Equal(DiffKind.Unchanged, row.Kind),
            row =>
            {
                Assert.Equal(DiffKind.Modified, row.Kind);
                Assert.Equal("two", row.OldText);
                Assert.Equal("TWO", row.NewText);
            },
            row => Assert.Equal(DiffKind.Unchanged, row.Kind));
    }

    [Fact]
    public void Compute_AlignsAdditionsAndRemovals()
    {
        var rows = LineDiff.Compute("a\nb", "a\nb\nc");

        Assert.Equal(DiffKind.Added, rows[^1].Kind);
        Assert.Null(rows[^1].OldNo);
        Assert.Equal(3, rows[^1].NewNo);
        Assert.Equal("c", rows[^1].NewText);
    }

    [Fact]
    public void Churn_CountsModifiedAsAddedAndRemoved()
    {
        var rows = LineDiff.Compute("a\nb", "a\nB\nc");

        Assert.Equal(new DiffChurn(2, 1), DiffMetrics.Churn(rows));
    }
}
