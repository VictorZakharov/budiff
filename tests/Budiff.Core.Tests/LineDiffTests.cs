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

    // A missing side is no lines: a blank line after the imports must not pair
    // with it into an Unchanged row numbered 1 on the missing side.
    [Fact]
    public void Compute_WhollyAddedOrDeletedFileHasOnlyChangeRows()
    {
        const string text = "using A;\nusing B;\nusing C;\n\nclass X\n{\n\n}\n";

        var added = LineDiff.Compute(string.Empty, text);
        var deleted = LineDiff.Compute(text, string.Empty);

        Assert.Equal(8, added.Count);
        Assert.Equal(8, deleted.Count);
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(new DiffRow(null, null, i + 1, added[i].NewText, DiffKind.Added), added[i]);
            Assert.Equal(new DiffRow(i + 1, deleted[i].OldText, null, null, DiffKind.Removed), deleted[i]);
        }

        Assert.Equal(string.Empty, deleted[3].OldText);
        Assert.Equal("}", added[7].NewText);
        Assert.Equal(new DiffChurn(8, 0), DiffMetrics.Churn(added));
        Assert.Equal(new DiffChurn(0, 8), DiffMetrics.Churn(deleted));

        // No trailing newline keeps the last line; a lone newline is one blank line.
        Assert.Equal(2, LineDiff.Compute("a\nb", string.Empty).Count);
        Assert.Single(LineDiff.Compute(string.Empty, "\n"));

        // Both sides empty stays one blank Unchanged row (no change at all).
        Assert.Equal(DiffKind.Unchanged, Assert.Single(LineDiff.Compute(string.Empty, string.Empty)).Kind);
    }

    [Fact]
    public void Churn_CountsModifiedAsAddedAndRemoved()
    {
        var rows = LineDiff.Compute("a\nb", "a\nB\nc");

        Assert.Equal(new DiffChurn(2, 1), DiffMetrics.Churn(rows));
    }

    [Fact]
    public void InlineDiff_IdenticalTextIsSingleUnchangedRun()
    {
        var pair = InlineDiff.Compute("same", "same");

        Assert.Equal(new[] { new InlineSegment("same", InlineChangeKind.Unchanged) }, pair.Old);
        Assert.Equal(new[] { new InlineSegment("same", InlineChangeKind.Unchanged) }, pair.New);
    }

    [Fact]
    public void InlineDiff_TracksInsertedAndRemovedCharacters()
    {
        var pair = InlineDiff.Compute("abc", "axc!");

        Assert.Contains(pair.Old, segment => segment is { Text: "b", Kind: InlineChangeKind.Removed });
        Assert.Contains(pair.New, segment => segment is { Text: "x", Kind: InlineChangeKind.Added });
        Assert.Contains(pair.New, segment => segment is { Text: "!", Kind: InlineChangeKind.Added });
    }
}
