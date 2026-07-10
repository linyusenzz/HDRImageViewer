namespace HdrImageViewer.Services;

internal static class NativeToolLocator
{
    private static readonly string[] Msys2ToolDirectories =
    [
        @"C:\msys64\ucrt64\bin",
    ];

    public const string PlatformDirectoryName = "x64";

    public static string? FindTool(string fileName)
    {
        return FindFirstTool(fileName);
    }

    public static string? FindFirstTool(params string[] fileNames)
    {
        foreach (var fileName in fileNames)
        {
            foreach (var candidate in EnumerateLocalToolCandidates(fileName))
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        foreach (var fileName in fileNames)
        {
            if (NativeProcessRunner.FindExecutableOnPath(fileName) is { } pathTool)
            {
                return pathTool;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateLocalToolCandidates(string fileName)
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in EnumerateTrustedApplicationRoots())
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(root, "encoders", PlatformDirectoryName, fileName),
                         Path.Combine(root, "encoders", PlatformDirectoryName, Path.GetFileNameWithoutExtension(fileName), fileName),
                     })
            {
                if (TryNormalizePath(candidate) is { } normalized && yielded.Add(normalized))
                {
                    yield return normalized;
                }
            }
        }

        foreach (var directory in Msys2ToolDirectories)
        {
            var candidate = Path.Combine(directory, fileName);
            if (TryNormalizePath(candidate) is { } normalized && yielded.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    private static IEnumerable<string> EnumerateTrustedApplicationRoots()
    {
        if (TryNormalizePath(AppContext.BaseDirectory) is { } applicationDirectory)
        {
            yield return applicationDirectory;
        }
    }

    private static string? TryNormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }
}
