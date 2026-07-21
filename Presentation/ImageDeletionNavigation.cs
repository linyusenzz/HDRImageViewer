namespace HdrImageViewer.Presentation;

public static class ImageDeletionNavigation
{
    public static string? SelectNextPath(IReadOnlyList<string> paths, int currentIndex)
    {
        if (currentIndex < 0 || currentIndex >= paths.Count || paths.Count <= 1)
        {
            return null;
        }

        return currentIndex + 1 < paths.Count
            ? paths[currentIndex + 1]
            : paths[currentIndex - 1];
    }
}
