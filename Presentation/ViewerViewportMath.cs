namespace HdrImageViewer.Presentation;

internal static class ViewerViewportMath
{
    public static (uint X, uint Y, uint Width, uint Height) CalculateCropPixels(
        double left, double top, double right, double bottom, uint pixelWidth, uint pixelHeight)
    {
        if (pixelWidth == 0 || pixelHeight == 0)
        {
            return default;
        }

        var x = (uint)Math.Clamp(Math.Round(left * pixelWidth), 0.0, pixelWidth - 1.0);
        var y = (uint)Math.Clamp(Math.Round(top * pixelHeight), 0.0, pixelHeight - 1.0);
        var endX = (uint)Math.Clamp(Math.Round(right * pixelWidth), x + 1.0, pixelWidth);
        var endY = (uint)Math.Clamp(Math.Round(bottom * pixelHeight), y + 1.0, pixelHeight);
        return (x, y, endX - x, endY - y);
    }

    public static (double Width, double Height) CalculateFitSize(
        double availableWidth,
        double availableHeight,
        double aspectRatio)
    {
        var availableAspectRatio = availableWidth / availableHeight;
        if (availableAspectRatio > aspectRatio)
        {
            var height = availableHeight;
            return (height * aspectRatio, height);
        }

        var width = availableWidth;
        return (width, width / aspectRatio);
    }

    public static (double Width, double Height) CalculateFillSize(
        double availableWidth,
        double availableHeight,
        double aspectRatio)
    {
        var availableAspectRatio = availableWidth / availableHeight;
        if (availableAspectRatio > aspectRatio)
        {
            var width = availableWidth;
            return (width, width / aspectRatio);
        }

        var height = availableHeight;
        return (height * aspectRatio, height);
    }

    public static double CalculatePointerAnchorBlend(double targetSize, double viewportSize)
    {
        if (targetSize <= 0.0 || viewportSize <= 1.0)
        {
            return 0.0;
        }

        var transition = Math.Clamp(viewportSize * 0.18, 96.0, 220.0);
        var amount = Math.Clamp((targetSize - viewportSize) / transition, 0.0, 1.0);
        return amount * amount * (3.0 - (2.0 * amount));
    }

    public static double CalculateActualSizeZoomScale(
        double availableWidth,
        double availableHeight,
        double aspectRatio,
        int contentPixelWidth,
        int contentPixelHeight,
        double compositionScaleX,
        double compositionScaleY,
        bool orientationSwapsDimensions)
    {
        if (!IsPositiveFinite(availableWidth)
            || !IsPositiveFinite(availableHeight)
            || !IsPositiveFinite(aspectRatio)
            || contentPixelWidth <= 0
            || contentPixelHeight <= 0
            || !IsPositiveFinite(compositionScaleX)
            || !IsPositiveFinite(compositionScaleY))
        {
            return 1.0;
        }

        var (fitWidth, fitHeight) = CalculateFitSize(availableWidth, availableHeight, aspectRatio);
        if (orientationSwapsDimensions)
        {
            (contentPixelWidth, contentPixelHeight) = (contentPixelHeight, contentPixelWidth);
        }

        var physicalFitWidth = fitWidth * Math.Max(compositionScaleX, double.Epsilon);
        var physicalFitHeight = fitHeight * Math.Max(compositionScaleY, double.Epsilon);
        var scaleX = contentPixelWidth / Math.Max(physicalFitWidth, 1.0);
        var scaleY = contentPixelHeight / Math.Max(physicalFitHeight, 1.0);
        var actualSizeScale = Math.Min(scaleX, scaleY);
        return IsPositiveFinite(actualSizeScale) ? actualSizeScale : 1.0;
    }

    public static double ClampUserZoomScale(double targetScale, double actualSizeScale)
    {
        if (!IsPositiveFinite(actualSizeScale)) return 1.0;
        if (double.IsNaN(targetScale)) return actualSizeScale;

        // The interaction limits are relative to source pixels, not to fit mode.
        return Math.Clamp(targetScale, actualSizeScale * 0.01, actualSizeScale * 8.0);
    }

    public static double CalculateZoomPercentage(double displayedWidth, double fitWidth, double actualSizeScale)
    {
        if (!IsPositiveFinite(displayedWidth)
            || !IsPositiveFinite(fitWidth)
            || !IsPositiveFinite(actualSizeScale)) return 100.0;

        var percentage = displayedWidth / fitWidth / actualSizeScale * 100.0;
        return IsPositiveFinite(percentage) ? percentage : 100.0;
    }

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0.0;

    public static double Lerp(double from, double to, double amount)
    {
        return from + ((to - from) * amount);
    }

    public static (int PixelWidth, int PixelHeight) CalculateSwapChainPixelSize(
        double previewWidth,
        double previewHeight,
        double compositionScaleX,
        double compositionScaleY)
    {
        if (previewWidth <= 0.0 || previewHeight <= 0.0)
        {
            return (0, 0);
        }

        var pixelWidth = (int)Math.Round(previewWidth * Math.Max(compositionScaleX, 0.0));
        var pixelHeight = (int)Math.Round(previewHeight * Math.Max(compositionScaleY, 0.0));
        return (Math.Max(0, pixelWidth), Math.Max(0, pixelHeight));
    }

    /// <summary>
    /// Maps the visible preview panel onto the logical image rectangle using the
    /// shader ImageLayout convention: fittedUv = (panelUv - offset) / scale.
    /// </summary>
    public static bool ImageFitsInPreview(
        double imageWidth,
        double imageHeight,
        double previewWidth,
        double previewHeight)
    {
        return imageWidth > 0.0
            && imageHeight > 0.0
            && previewWidth > 0.0
            && previewHeight > 0.0
            && imageWidth <= previewWidth + 0.5
            && imageHeight <= previewHeight + 0.5;
    }

    public static double CalculateZoomPreviewScale(double presentedImageWidth, double targetImageWidth)
    {
        if (presentedImageWidth <= 0.0 || targetImageWidth <= 0.0)
        {
            return 1.0;
        }

        return Math.Clamp(targetImageWidth / presentedImageWidth, 0.2, 8.0);
    }

    public static (double X, double Y) CalculateZoomPreviewOrigin(
        double anchorViewportX,
        double anchorViewportY,
        double previewWidth,
        double previewHeight)
    {
        if (previewWidth <= 0.0 || previewHeight <= 0.0)
        {
            return (0.5, 0.5);
        }

        return (
            Math.Clamp(anchorViewportX / previewWidth, 0.0, 1.0),
            Math.Clamp(anchorViewportY / previewHeight, 0.0, 1.0));
    }

    public static ViewerSwapChainHostLayout CalculateSwapChainHostLayout(
        double previewWidth,
        double previewHeight,
        double imageWidth,
        double imageHeight,
        double contentWidth,
        double contentHeight,
        double scrollX,
        double scrollY)
    {
        if (previewWidth <= 0.0
            || previewHeight <= 0.0
            || imageWidth <= 0.0
            || imageHeight <= 0.0)
        {
            return ViewerSwapChainHostLayout.Invalid;
        }

        var imageLeft = Math.Max(0.0, (contentWidth - imageWidth) / 2.0);
        var imageTop = Math.Max(0.0, (contentHeight - imageHeight) / 2.0);
        var visibleLeft = Math.Max(scrollX, imageLeft);
        var visibleTop = Math.Max(scrollY, imageTop);
        var visibleRight = Math.Min(scrollX + previewWidth, imageLeft + imageWidth);
        var visibleBottom = Math.Min(scrollY + previewHeight, imageTop + imageHeight);
        var hostWidth = visibleRight - visibleLeft;
        var hostHeight = visibleBottom - visibleTop;
        if (hostWidth < 2.0 || hostHeight < 2.0)
        {
            hostWidth = Math.Min(imageWidth, previewWidth);
            hostHeight = Math.Min(imageHeight, previewHeight);
            if (hostWidth < 2.0 || hostHeight < 2.0)
            {
                return ViewerSwapChainHostLayout.Invalid;
            }

            return new ViewerSwapChainHostLayout(hostWidth, hostHeight, 1.0f, 1.0f, 0.0f, 0.0f);
        }

        return new ViewerSwapChainHostLayout(
            hostWidth,
            hostHeight,
            (float)(imageWidth / hostWidth),
            (float)(imageHeight / hostHeight),
            (float)((imageLeft - visibleLeft) / hostWidth),
            (float)((imageTop - visibleTop) / hostHeight));
    }

    public static ViewerVisibleImageLayout CalculateVisibleImageLayout(
        double previewWidth,
        double previewHeight,
        double imageWidth,
        double imageHeight,
        double contentWidth,
        double contentHeight,
        double scrollX,
        double scrollY)
    {
        if (previewWidth <= 0.0
            || previewHeight <= 0.0
            || imageWidth <= 0.0
            || imageHeight <= 0.0)
        {
            return ViewerVisibleImageLayout.Identity;
        }

        var imageLeft = Math.Max(0.0, (contentWidth - imageWidth) / 2.0);
        var imageTop = Math.Max(0.0, (contentHeight - imageHeight) / 2.0);
        return new ViewerVisibleImageLayout(
            (float)(imageWidth / previewWidth),
            (float)(imageHeight / previewHeight),
            (float)((imageLeft - scrollX) / previewWidth),
            (float)((imageTop - scrollY) / previewHeight));
    }
}

internal readonly record struct ViewerSwapChainHostLayout(
    double HostWidth,
    double HostHeight,
    float ScaleX,
    float ScaleY,
    float OffsetX,
    float OffsetY)
{
    public static ViewerSwapChainHostLayout Invalid { get; } = new(0.0, 0.0, 1.0f, 1.0f, 0.0f, 0.0f);

    public bool IsValid =>
        HostWidth >= 2.0
        && HostHeight >= 2.0
        && float.IsFinite(ScaleX)
        && float.IsFinite(ScaleY)
        && ScaleX > 0.0f
        && ScaleY > 0.0f
        && float.IsFinite(OffsetX)
        && float.IsFinite(OffsetY);

    public bool CoversPreview(double previewWidth, double previewHeight) =>
        HostWidth >= previewWidth - 0.5
        && HostHeight >= previewHeight - 0.5;
}

internal readonly record struct ViewerVisibleImageLayout(
    float ScaleX,
    float ScaleY,
    float OffsetX,
    float OffsetY)
{
    public static ViewerVisibleImageLayout Identity { get; } = new(1.0f, 1.0f, 0.0f, 0.0f);

    public bool IsValid =>
        float.IsFinite(ScaleX)
        && float.IsFinite(ScaleY)
        && ScaleX > 0.0f
        && ScaleY > 0.0f
        && float.IsFinite(OffsetX)
        && float.IsFinite(OffsetY);
}
