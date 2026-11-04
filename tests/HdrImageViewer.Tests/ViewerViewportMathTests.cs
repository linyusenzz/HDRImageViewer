using HdrImageViewer.Presentation;
using Xunit;

namespace HdrImageViewer.Tests;

public sealed class ViewerViewportMathTests
{
    [Theory]
    // A landscape image has vertical letterboxing before a pinch.
    [InlineData(500, 1000, 1000, 3, 900, 150)]
    // A large image has no letterboxing to remove.
    [InlineData(2000, 2000, 1000, 1.5, 700, 700)]
    // Shrinking below fit centers the image and clears scrolling.
    [InlineData(2000, 2000, 1000, 0.4, 200, 0)]
    // At the edge, commit must clamp to the image rather than retain blank space.
    [InlineData(500, 1000, 1000, 3, 1900, 500)]
    [InlineData(500, 1000, 1000, 3, 100, 0)]
    public void NativePinchCommit_PreservesVisibleImagePosition(
        double image, double content, double viewport, double factor, double offset, double expected)
    {
        Assert.Equal(expected, ViewerViewportMath.CalculateNativeZoomCommitOffset(image, content, viewport, factor, offset), 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NativePinchCommit_IgnoresInvalidScale(double factor)
    {
        Assert.Equal(0, ViewerViewportMath.CalculateNativeZoomCommitOffset(500, 1000, 1000, factor, 100));
    }

    [Theory]
    [InlineData(4000, 3000, 1000, 750, 2000, 1500)]
    [InlineData(3000, 4000, 750, 1000, 1500, 2000)]
    public void Crop_UsesFullSourceDimensionsIncludingPortraitOrientation(
        uint width, uint height, uint x, uint y, uint cropWidth, uint cropHeight)
    {
        var crop = ViewerViewportMath.CalculateCropPixels(0.25, 0.25, 0.75, 0.75, width, height);
        Assert.Equal((x, y, cropWidth, cropHeight), crop);
    }

    [Fact]
    public void Crop_ClampsEdgesToSourcePixels()
    {
        Assert.Equal((0u, 0u, 4031u, 3023u),
            ViewerViewportMath.CalculateCropPixels(-0.1, -0.2, 1.1, 1.2, 4031, 3023));
    }

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

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void CalculateActualSizeZoomScale_KeepsSmallImagesAtOnePhysicalPixelPerSourcePixel(double compositionScale)
    {
        var scale = ViewerViewportMath.CalculateActualSizeZoomScale(
            1000, 1000, 1.0, 100, 100, compositionScale, compositionScale, false);

        Assert.True(scale < 0.25);
        Assert.Equal(100, 1000 * scale * compositionScale, 6);
        Assert.Equal(100, ViewerViewportMath.CalculateZoomPercentage(1000 * scale, 1000, scale), 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void CalculateActualSizeZoomScale_FallsBackForInvalidViewport(double availableWidth)
    {
        Assert.Equal(1.0, ViewerViewportMath.CalculateActualSizeZoomScale(
            availableWidth, 1000, 1.0, 100, 100, 1.0, 1.0, false));
    }

    [Fact]
    public void CalculateZoomPercentage_DistinguishesFitAndFillFromDisplayedImageSize()
    {
        var fit = ViewerViewportMath.CalculateFitSize(1000, 600, 4.0 / 3.0);
        var fill = ViewerViewportMath.CalculateFillSize(1000, 600, 4.0 / 3.0);
        var actualSize = ViewerViewportMath.CalculateActualSizeZoomScale(
            1000, 600, 4.0 / 3.0, 4000, 3000, 1.0, 1.0, false);

        Assert.Equal(20, ViewerViewportMath.CalculateZoomPercentage(fit.Width, fit.Width, actualSize), 6);
        Assert.Equal(25, ViewerViewportMath.CalculateZoomPercentage(fill.Width, fit.Width, actualSize), 6);
    }

    [Fact]
    public void ClampUserZoomScale_IncreasesTwoHundredPercentEvenBeyondFourTimesFit()
    {
        var actualSize = ViewerViewportMath.CalculateActualSizeZoomScale(
            1000, 600, 4.0 / 3.0, 4000, 3000, 1.0, 1.0, false);
        var currentScale = actualSize * 2;

        var zoomedScale = ViewerViewportMath.ClampUserZoomScale(currentScale * 1.25, actualSize);

        Assert.True(currentScale > 4);
        Assert.True(zoomedScale > currentScale);
        Assert.Equal(250, ViewerViewportMath.CalculateZoomPercentage(800 * zoomedScale, 800, actualSize), 6);
    }

    [Theory]
    [InlineData(0.1, -1, 1)]
    [InlineData(0.1, 0.001, 1)]
    [InlineData(0.1, 0.2, 200)]
    [InlineData(0.1, 0.8, 800)]
    [InlineData(0.1, 100, 800)]
    [InlineData(5, 0, 1)]
    [InlineData(5, 0.05, 1)]
    [InlineData(5, 10, 200)]
    [InlineData(5, 40, 800)]
    [InlineData(5, 100, 800)]
    public void ClampUserZoomScale_UsesOneToEightHundredPercentOfSourcePixels(
        double actualSizeScale, double targetScale, double expectedPercentage)
    {
        var clamped = ViewerViewportMath.ClampUserZoomScale(targetScale, actualSizeScale);

        Assert.Equal(expectedPercentage,
            ViewerViewportMath.CalculateZoomPercentage(800 * clamped, 800, actualSizeScale), 6);
    }

    [Theory]
    [InlineData(0, 800, 5)]
    [InlineData(800, 0, 5)]
    [InlineData(800, 800, 0)]
    [InlineData(-1, 800, 5)]
    [InlineData(double.NaN, 800, 5)]
    [InlineData(800, double.PositiveInfinity, 5)]
    [InlineData(800, 800, double.NaN)]
    [InlineData(double.MaxValue, double.Epsilon, 1)]
    public void CalculateZoomPercentage_FallsBackForInvalidMeasurements(
        double displayedWidth, double fitWidth, double actualSizeScale)
    {
        Assert.Equal(100.0, ViewerViewportMath.CalculateZoomPercentage(displayedWidth, fitWidth, actualSizeScale));
    }

    [Fact]
    public void CalculateVisibleImageLayout_CentersFitImageInsidePreview()
    {
        var layout = ViewerViewportMath.CalculateVisibleImageLayout(
            previewWidth: 1000,
            previewHeight: 600,
            imageWidth: 800,
            imageHeight: 600,
            contentWidth: 1000,
            contentHeight: 600,
            scrollX: 0,
            scrollY: 0);

        Assert.Equal(0.8f, layout.ScaleX, 5);
        Assert.Equal(1.0f, layout.ScaleY, 5);
        Assert.Equal(0.1f, layout.OffsetX, 5);
        Assert.Equal(0.0f, layout.OffsetY, 5);
    }

    [Fact]
    public void CalculateVisibleImageLayout_MapsZoomedPanToNegativeOffsets()
    {
        var layout = ViewerViewportMath.CalculateVisibleImageLayout(
            previewWidth: 1000,
            previewHeight: 600,
            imageWidth: 1600,
            imageHeight: 1200,
            contentWidth: 1600,
            contentHeight: 1200,
            scrollX: 300,
            scrollY: 200);

        Assert.Equal(1.6f, layout.ScaleX, 5);
        Assert.Equal(2.0f, layout.ScaleY, 5);
        Assert.Equal(-0.3f, layout.OffsetX, 5);
        Assert.Equal(-1.0f / 3.0f, layout.OffsetY, 5);
    }

    [Fact]
    public void CalculateVisibleImageLayout_KeepsBottomRightEdgeInsideImage()
    {
        var layout = ViewerViewportMath.CalculateVisibleImageLayout(
            previewWidth: 1000,
            previewHeight: 600,
            imageWidth: 1600,
            imageHeight: 1200,
            contentWidth: 1600,
            contentHeight: 1200,
            scrollX: 600,
            scrollY: 600);

        var rightUv = (1.0f - layout.OffsetX) / layout.ScaleX;
        var bottomUv = (1.0f - layout.OffsetY) / layout.ScaleY;
        Assert.Equal(1.0f, rightUv, 5);
        Assert.Equal(1.0f, bottomUv, 5);
    }

    [Fact]
    public void CalculateSwapChainPixelSize_UsesPreviewAndCompositionScale()
    {
        var size = ViewerViewportMath.CalculateSwapChainPixelSize(1280, 720, 1.5, 1.5);

        Assert.Equal(1920, size.PixelWidth);
        Assert.Equal(1080, size.PixelHeight);
    }

    [Fact]
    public void CalculateSwapChainHostLayout_KeepsHostAtImageSizeWhenLetterboxed()
    {
        var layout = ViewerViewportMath.CalculateSwapChainHostLayout(
            previewWidth: 1000,
            previewHeight: 600,
            imageWidth: 800,
            imageHeight: 600,
            contentWidth: 1000,
            contentHeight: 600,
            scrollX: 0,
            scrollY: 0);

        Assert.Equal(800, layout.HostWidth, 6);
        Assert.Equal(600, layout.HostHeight, 6);
        Assert.Equal(1.0f, layout.ScaleX, 5);
        Assert.Equal(1.0f, layout.ScaleY, 5);
        Assert.Equal(0.0f, layout.OffsetX, 5);
        Assert.Equal(0.0f, layout.OffsetY, 5);
        Assert.False(layout.CoversPreview(1000, 600));
    }

    [Fact]
    public void CalculateSwapChainHostLayout_DoesNotLetterboxNarrowTallImage()
    {
        var layout = ViewerViewportMath.CalculateSwapChainHostLayout(
            previewWidth: 1000,
            previewHeight: 500,
            imageWidth: 800,
            imageHeight: 700,
            contentWidth: 1000,
            contentHeight: 700,
            scrollX: 0,
            scrollY: 0);

        Assert.Equal(800, layout.HostWidth, 6);
        Assert.Equal(500, layout.HostHeight, 6);
        Assert.Equal(1.0f, layout.ScaleX, 5);
        Assert.Equal(1.4f, layout.ScaleY, 5);
        Assert.Equal(0.0f, layout.OffsetX, 5);
        Assert.Equal(0.0f, layout.OffsetY, 5);
        Assert.False(layout.CoversPreview(1000, 500));
    }

    [Fact]
    public void CalculateSwapChainHostLayout_CoversPreviewWhenImageIsLarger()
    {
        var layout = ViewerViewportMath.CalculateSwapChainHostLayout(
            previewWidth: 1000,
            previewHeight: 600,
            imageWidth: 1600,
            imageHeight: 1200,
            contentWidth: 1600,
            contentHeight: 1200,
            scrollX: 300,
            scrollY: 200);

        Assert.Equal(1000, layout.HostWidth, 6);
        Assert.Equal(600, layout.HostHeight, 6);
        Assert.Equal(1.6f, layout.ScaleX, 5);
        Assert.Equal(2.0f, layout.ScaleY, 5);
        Assert.Equal(-0.3f, layout.OffsetX, 5);
        Assert.Equal(-1.0f / 3.0f, layout.OffsetY, 5);
        Assert.True(layout.CoversPreview(1000, 600));
    }

    [Fact]
    public void ImageFitsInPreview_DetectsLetterboxedAndCoveringStates()
    {
        Assert.True(ViewerViewportMath.ImageFitsInPreview(800, 600, 1000, 600));
        Assert.False(ViewerViewportMath.ImageFitsInPreview(1600, 1200, 1000, 600));
    }

    [Fact]
    public void CalculateZoomPreviewScale_UsesPresentedImageWidth()
    {
        Assert.Equal(1.25, ViewerViewportMath.CalculateZoomPreviewScale(800, 1000), 6);
        Assert.Equal(1.0, ViewerViewportMath.CalculateZoomPreviewScale(0, 1000), 6);
    }

    [Fact]
    public void CalculateZoomPreviewOrigin_MapsPointerIntoHost()
    {
        var origin = ViewerViewportMath.CalculateZoomPreviewOrigin(250, 120, 1000, 600);

        Assert.Equal(0.25, origin.X, 6);
        Assert.Equal(0.2, origin.Y, 6);
    }

    [Fact]
    public void CalculateSwapChainPixelSize_ReturnsZeroForEmptyPreview()
    {
        var size = ViewerViewportMath.CalculateSwapChainPixelSize(0, 720, 1.5, 1.5);

        Assert.Equal(0, size.PixelWidth);
        Assert.Equal(0, size.PixelHeight);
    }
}
