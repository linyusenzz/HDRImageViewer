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

        // Stale entries are filtered here, on the rare restore path, instead of
        // in SaveImage: saving happens on every image navigation and a
        // File.Exists per folder entry made each step pay one stat call per
        // sibling image.
        navigationPaths = storedPaths?
            .Where(File.Exists)
            .ToList();
        return true;
    }

    public static void SaveImage(string path, IReadOnlyList<string> navigationPaths, bool hasExplicitNavigationPaths)
    {
        var snapshot = navigationPaths
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (Gate)
        {
            s_currentPath = path;
            s_navigationPaths = snapshot;
            s_hasExplicitNavigationPaths = hasExplicitNavigationPaths;
        }
    }
}
