using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using HdrImageViewer.Models;

namespace HdrImageViewer.Services;

public static partial class JxlProbe
{
    public static async Task<JxlProbeResult?> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!IsJxlExtension(Path.GetExtension(path)))
        {
            return null;
        }

        var jxlinfo = FindNativeTool("jxlinfo.exe", "libjxl");
        if (jxlinfo is null)
        {
            return new JxlProbeResult(
                true,
                null,
                null,
                null,
                "未知",
                "未知",
                null,
                null,
                "未找到 jxlinfo.exe");
        }

        using var process = NativeProcessRunner.Create(jxlinfo);
        process.StartInfo.ArgumentList.Add(path);
        var output = await NativeProcessRunner.RunAsync(process, "libjxl jxlinfo", cancellationToken);
        return Parse(output);
    }

    public static bool IsJxlExtension(string extension)
    {
        return string.Equals(extension, ".jxl", StringComparison.OrdinalIgnoreCase);
    }

    public static JxlProbeResult Parse(string output)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var firstLine = lines.FirstOrDefault() ?? "JPEG XL";
        var imageLine = lines.FirstOrDefault(line => line.StartsWith("JPEG XL image", StringComparison.OrdinalIgnoreCase))
            ?? firstLine;
        var sizeMatch = SizeRegex().Match(imageLine);
        var bitDepthMatch = BitDepthRegex().Match(imageLine);
        var colorLine = lines
            .FirstOrDefault(line => line.Contains("Color space:", StringComparison.OrdinalIgnoreCase));
        var hasGainMap = lines.Any(line =>
            line.Contains("Gain map", StringComparison.OrdinalIgnoreCase)
            || line.Contains("jhgm", StringComparison.OrdinalIgnoreCase));

        return new JxlProbeResult(
            firstLine.Contains("JPEG XL", StringComparison.OrdinalIgnoreCase),
            TryParseInt(sizeMatch, "width"),
            TryParseInt(sizeMatch, "height"),
            TryParseInt(bitDepthMatch, "depth"),
            ParseTransfer(colorLine),
            ParsePrimaries(colorLine),
            ParseDouble(output, "intensity_target"),
            ParseDouble(output, "min_nits"),
            firstLine,
            hasGainMap);
    }

    private static string ParseTransfer(string? colorLine)
    {
        if (string.IsNullOrWhiteSpace(colorLine))
        {
            return "未知";
        }

        if (colorLine.Contains("PQ transfer function", StringComparison.OrdinalIgnoreCase))
        {
            return "PQ / SMPTE ST 2084";
        }

        if (colorLine.Contains("HLG transfer function", StringComparison.OrdinalIgnoreCase))
        {
            return "HLG";
        }

        if (colorLine.Contains("linear", StringComparison.OrdinalIgnoreCase))
        {
            return "linear";
        }

        if (colorLine.Contains("sRGB", StringComparison.OrdinalIgnoreCase))
        {
            return "sRGB";
        }

        return "未知";
    }

    private static string ParsePrimaries(string? colorLine)
    {
        if (string.IsNullOrWhiteSpace(colorLine))
        {
            return "primaries 未知";
        }

        if (colorLine.Contains("Rec.2100 primaries", StringComparison.OrdinalIgnoreCase))
        {
            return "Rec.2100 / BT.2020";
        }

        if (colorLine.Contains("BT.2020", StringComparison.OrdinalIgnoreCase))
        {
            return "BT.2020";
        }

        if (colorLine.Contains("sRGB", StringComparison.OrdinalIgnoreCase))
        {
            return "sRGB / BT.709";
        }

        return colorLine.Trim();
    }

    private static int? TryParseInt(Match match, string groupName)
    {
        return match.Success && int.TryParse(match.Groups[groupName].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static double? ParseDouble(string output, string key)
    {
        var match = Regex.Match(output, $@"{Regex.Escape(key)}:\s*(?<value>[-+]?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        return match.Success && double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string? FindNativeTool(string fileName, string dependencyDirectoryName)
    {
        foreach (var candidate in EnumerateLocalToolCandidates(fileName, dependencyDirectoryName))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return NativeProcessRunner.FindExecutableOnPath(fileName);
    }

    private static IEnumerable<string> EnumerateLocalToolCandidates(string fileName, string dependencyDirectoryName)
    {
        var baseDir = AppContext.BaseDirectory;
        var currentDir = Environment.CurrentDirectory;
        yield return Path.Combine(@"C:\msys64\ucrt64\bin", fileName);
        foreach (var root in new[] { currentDir, baseDir })
        {
            var dependencyRoot = Path.Combine(root, "external", dependencyDirectoryName);
            yield return Path.GetFullPath(Path.Combine(dependencyRoot, fileName));
            yield return Path.GetFullPath(Path.Combine(dependencyRoot, "bin", fileName));
            yield return Path.GetFullPath(Path.Combine(dependencyRoot, "tools", fileName));
            yield return Path.GetFullPath(Path.Combine(dependencyRoot, "build", fileName));
            yield return Path.GetFullPath(Path.Combine(dependencyRoot, "build", "Release", fileName));
            yield return Path.GetFullPath(Path.Combine(dependencyRoot, "build", "tools", fileName));
            yield return Path.GetFullPath(Path.Combine(dependencyRoot, "build", "tools", "Release", fileName));
            yield return Path.GetFullPath(Path.Combine(root, "..", "..", "..", "..", "..", "external", dependencyDirectoryName, "bin", fileName));
            yield return Path.GetFullPath(Path.Combine(root, "..", "..", "..", "..", "..", "external", dependencyDirectoryName, "build", "Release", fileName));
            yield return Path.GetFullPath(Path.Combine(root, "..", "..", "..", "..", "..", "external", dependencyDirectoryName, "build", "tools", "Release", fileName));
        }
    }

    [GeneratedRegex(@"(?<width>\d+)x(?<height>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"(?<depth>\d+)-bit", RegexOptions.IgnoreCase)]
    private static partial Regex BitDepthRegex();
}
