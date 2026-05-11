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
        lock (Gate)
        {
            path = s_currentPath ?? string.Empty;
            navigationPaths = s_navigationPaths;
            hasExplicitNavigationPaths = s_hasExplicitNavigationPaths;
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
    }

    public static void SaveImage(string path, IReadOnlyList<string> navigationPaths, bool hasExplicitNavigationPaths)
    {
        lock (Gate)
        {
            s_currentPath = path;
            s_navigationPaths = navigationPaths
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            s_hasExplicitNavigationPaths = hasExplicitNavigationPaths;
        }
    }
}
