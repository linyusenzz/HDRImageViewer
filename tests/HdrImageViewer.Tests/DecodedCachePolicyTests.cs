using HdrImageViewer.Services;
using Xunit;

namespace HdrImageViewer.Tests;

public sealed class DecodedCachePolicyTests
{
    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, 2048, true)]
    [InlineData(4096, 2048, true)]
    [InlineData(2048, 4096, false)]
    [InlineData(4096, null, false)]
    public void IsAtLeastAsDetailed_PrefersFullOrLargerDecode(
        int? cachedMaxPixelSize,
        int? requestedMaxPixelSize,
        bool expected)
    {
        Assert.Equal(
            expected,
            DecodedCachePolicy.IsAtLeastAsDetailed(cachedMaxPixelSize, requestedMaxPixelSize));
    }
}
