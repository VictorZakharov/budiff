using System.Text;
using Budiff;
using Xunit;

namespace Budiff.Core.Tests;

public sealed class DiffLoaderTests
{
    [Fact]
    public void Build_ReturnsPlaceholderForBinaryBlob()
    {
        var result = DiffLoader.Build(
            BlobInfo.Empty,
            new BlobInfo(string.Empty, 42, IsBinary: true));

        Assert.Null(result.Rows);
        Assert.Contains("Binary file not shown", result.Placeholder);
    }

    [Fact]
    public void BuildView_NumbersEveryLineAsUnchanged_WithoutATrailingBlankRow()
    {
        var rows = DiffLoader.BuildView(new BlobInfo("one\r\ntwo\n\nfour\n", 16, IsBinary: false)).Rows;

        Assert.NotNull(rows);
        Assert.Equal(new[] { "one", "two", string.Empty, "four" }, rows.Select(r => r.NewText));
        Assert.All(rows, r => Assert.Equal(DiffKind.Unchanged, r.Kind));
        Assert.All(rows, r => Assert.Equal(r.OldNo, r.NewNo));
        Assert.Equal(new int?[] { 1, 2, 3, 4 }, rows.Select(r => r.NewNo));
    }

    [Fact]
    public void BuildView_ReturnsPlaceholdersForEmptyBinaryAndOversizedFiles()
    {
        Assert.Equal("Empty file.", DiffLoader.BuildView(BlobInfo.Empty).Placeholder);
        Assert.Contains("Binary file not shown", DiffLoader.BuildView(new BlobInfo(string.Empty, 42, IsBinary: true)).Placeholder);
        Assert.Contains("too large", DiffLoader.BuildView(new BlobInfo(string.Empty, DiffLoader.MaxInlineDiffBytes + 1, IsBinary: false)).Placeholder);
    }

    [Fact]
    public void LooksBinary_UsesNullByteSniff()
    {
        Assert.True(TextBytes.LooksBinary(new byte[] { 1, 2, 0, 4 }));
        Assert.False(TextBytes.LooksBinary(Encoding.UTF8.GetBytes("plain text")));
    }

    [Fact]
    public void InlineDiff_PreservesUnchangedPrefixAndSuffix()
    {
        var pair = InlineDiff.Compute("count = 1;", "count = 2;");

        Assert.Contains(pair.Old, s => s.Kind == InlineChangeKind.Removed && s.Text == "1");
        Assert.Contains(pair.New, s => s.Kind == InlineChangeKind.Added && s.Text == "2");
    }
}
