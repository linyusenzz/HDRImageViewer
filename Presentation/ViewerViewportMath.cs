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

    public static double Lerp(double from, double to, double amount)
    {
        return from + ((to - from) * amount);
    }
}
