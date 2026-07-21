namespace HdrImageViewer.Services;

public static class ViewerSessionState
{
    private static readonly object Gate = new();
    private static string? s_currentPath;
    private static IReadOnlyList<string>? s_navigationPaths;
    private static bool s_hasExplicitNavigationPaths;

    public static bool TryGetLastImage(
        out string path,
        out IReadOnlyList<string>? navigationPaths,
        out bool hasExplicitNavigationPaths)
    {
        IReadOnlyList<string>? storedPaths;
        lock (Gate)
        {
            path = s_currentPath ?? string.Empty;
            storedPaths = s_navigationPaths;
            hasExplicitNavigationPaths = s_hasExplicitNavigationPaths;
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            navigationPaths = null;
            return false;
        }

        // Stale entries are filtered and duplicates dropped here, on the rare
        // restore path, instead of in SaveImage: saving happens on every image
        // navigation, and a File.Exists stat or a Distinct copy per folder
        // entry made each step pay O(sibling count) on the UI thread.
        navigationPaths = storedPaths?
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return true;
    }

    public static void SaveImage(string path, IReadOnlyList<string> navigationPaths, bool hasExplicitNavigationPaths)
    {
        // The list reference is stored without copying: callers hand in lists
        // that are never mutated in place after this call (folder lists are
        // rebuilt as fresh instances on every refresh, explicit lists are
        // finalised before saving), so the defensive O(n) snapshot only cost
        // time per navigation in folders with tens of thousands of images.
        // TryGetLastImage sanitises on read.
        lock (Gate)
        {
            s_currentPath = path;
            s_navigationPaths = navigationPaths;
            s_hasExplicitNavigationPaths = hasExplicitNavigationPaths;
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            s_currentPath = null;
            s_navigationPaths = null;
            s_hasExplicitNavigationPaths = false;
        }
    }
}
