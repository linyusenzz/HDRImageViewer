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

    [Fact]
    public void CalculateActualSizeZoomScale_MapsSourcePixelsToPhysicalPixels()
    {
        var scale = ViewerViewportMath.CalculateActualSizeZoomScale(
            availableWidth: 1600,
            availableHeight: 900,
            aspectRatio: 4.0 / 3.0,
            contentPixelWidth: 4096,
            contentPixelHeight: 3072,
            compositionScaleX: 1.5,
            compositionScaleY: 1.5,
            orientationSwapsDimensions: false);

        var fit = ViewerViewportMath.CalculateFitSize(1600, 900, 4.0 / 3.0);
        Assert.Equal(4096, fit.Width * scale * 1.5, 6);
        Assert.Equal(3072, fit.Height * scale * 1.5, 6);
    }

    [Fact]
    public void CalculateActualSizeZoomScale_UsesOrientedDimensions()
    {
        var scale = ViewerViewportMath.CalculateActualSizeZoomScale(
            availableWidth: 900,
            availableHeight: 1600,
            aspectRatio: 3.0 / 4.0,
            contentPixelWidth: 4096,
            contentPixelHeight: 3072,
            compositionScaleX: 1.5,
            compositionScaleY: 1.5,
            orientationSwapsDimensions: true);

        var fit = ViewerViewportMath.CalculateFitSize(900, 1600, 3.0 / 4.0);
        Assert.Equal(3072, fit.Width * scale * 1.5, 6);
        Assert.Equal(4096, fit.Height * scale * 1.5, 6);
    }

    [Fact]
    public void CalculateActualSizeZoomScale_AllowsMoreThanInteractiveZoomLimit()
    {
        var scale = ViewerViewportMath.CalculateActualSizeZoomScale(
            availableWidth: 672,
            availableHeight: 504,
            aspectRatio: 4.0 / 3.0,
            contentPixelWidth: 4096,
            contentPixelHeight: 3072,
            compositionScaleX: 1.5,
            compositionScaleY: 1.5,
            orientationSwapsDimensions: false);

        Assert.True(scale > 4.0);
        Assert.Equal(4096.0 / 1008.0, scale, 6);
    }
}
