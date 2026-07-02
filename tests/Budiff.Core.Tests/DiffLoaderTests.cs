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
