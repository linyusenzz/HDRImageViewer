using HdrImageViewer.Presentation;
using Xunit;

namespace HdrImageViewer.Tests;

public sealed class ViewerViewportMathTests
{
    [Fact]
    public void CalculateFitSize_KeepsImageInsideViewport()
    {
        var size = ViewerViewportMath.CalculateFitSize(1000, 600, 4.0 / 3.0);

        Assert.Equal(800, size.Width, 6);
        Assert.Equal(600, size.Height, 6);
    }

    [Fact]
    public void CalculateFillSize_CoversViewport()
    {
        var size = ViewerViewportMath.CalculateFillSize(1000, 600, 4.0 / 3.0);

        Assert.Equal(1000, size.Width, 6);
        Assert.Equal(750, size.Height, 6);
    }

    [Theory]
    [InlineData(500, 1000, 0)]
    [InlineData(1000, 1000, 0)]
    [InlineData(2000, 1000, 1)]
    public void CalculatePointerAnchorBlend_StaysWithinExpectedRange(
        double targetSize,
        double viewportSize,
        double expected)
    {
        Assert.Equal(expected, ViewerViewportMath.CalculatePointerAnchorBlend(targetSize, viewportSize), 6);
    }
}
