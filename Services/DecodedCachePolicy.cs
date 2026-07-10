namespace HdrImageViewer.Services;

internal static class DecodedCachePolicy
{
    public static bool IsAtLeastAsDetailed(int? cachedMaxPixelSize, int? requestedMaxPixelSize)
    {
        if (cachedMaxPixelSize is null)
        {
            return true;
        }

        return requestedMaxPixelSize is not null
            && cachedMaxPixelSize.Value >= requestedMaxPixelSize.Value;
    }
}
