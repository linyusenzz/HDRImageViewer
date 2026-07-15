namespace HdrImageViewer.Presentation;

internal static class ViewerViewportMath
{
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
        if (availableWidth <= 0.0
            || availableHeight <= 0.0
            || aspectRatio <= 0.0
            || contentPixelWidth <= 0
            || contentPixelHeight <= 0)
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
        return Math.Max(Math.Min(scaleX, scaleY), 0.25);
    }

    public static double Lerp(double from, double to, double amount)
    {
        return from + ((to - from) * amount);
    }
}
