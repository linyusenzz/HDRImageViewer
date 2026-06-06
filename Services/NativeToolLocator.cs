using System.Runtime.InteropServices;

namespace HdrImageViewer.Services;

internal static class NativeToolLocator
{
    private static readonly string[] Msys2ToolDirectories =
    [
        @"C:\msys64\ucrt64\bin",
    ];

    public static string PlatformDirectoryName => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        Architecture.Arm64 => "ARM64",
        _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
    };

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
        foreach (var root in EnumerateRootAndAncestors(Environment.CurrentDirectory)
                     .Concat(EnumerateRootAndAncestors(AppContext.BaseDirectory)))
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(root, "encoders", PlatformDirectoryName, fileName),
                         Path.Combine(root, "encoders", PlatformDirectoryName, Path.GetFileNameWithoutExtension(fileName), fileName),
                         Path.Combine(root, "external", "encoders", PlatformDirectoryName, fileName),
                         Path.Combine(root, "external", "encoders", PlatformDirectoryName, Path.GetFileNameWithoutExtension(fileName), fileName),
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

    private static IEnumerable<string> EnumerateRootAndAncestors(string root)
    {
        DirectoryInfo? directory;
        try
        {
            directory = new DirectoryInfo(root);
        }
        catch
        {
            yield break;
        }

        while (directory is not null)
        {
            yield return directory.FullName;
            directory = directory.Parent;
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
