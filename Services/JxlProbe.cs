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

        var jxlinfo = NativeToolLocator.FindTool("jxlinfo.exe");
        if (jxlinfo is null)
        {
            return new JxlProbeResult(
                true,
                null,
                null,
                null,
                "HDR 候选（缺少 jxlinfo.exe，无法读取精确 transfer）",
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
        var colorMetadata = ExtractColorMetadata(lines);
        var hasGainMap = lines.Any(line =>
            line.Contains("Gain map", StringComparison.OrdinalIgnoreCase)
            || line.Contains("jhgm", StringComparison.OrdinalIgnoreCase));

        return new JxlProbeResult(
            firstLine.Contains("JPEG XL", StringComparison.OrdinalIgnoreCase),
            TryParseInt(sizeMatch, "width"),
            TryParseInt(sizeMatch, "height"),
            TryParseInt(bitDepthMatch, "depth"),
            ParseTransfer(colorMetadata),
            ParsePrimaries(colorMetadata),
            ParseDouble(output, "intensity target"),
            ParseDouble(output, "min nits"),
            firstLine,
            hasGainMap);
    }

    private static string? ExtractColorMetadata(IReadOnlyList<string> lines)
    {
        var colorSpaceIndex = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].Contains("Color space:", StringComparison.OrdinalIgnoreCase))
            {
                colorSpaceIndex = index;
                break;
            }
        }

        if (colorSpaceIndex < 0)
        {
            return null;
        }

        var colorMetadata = new List<string>();
        for (var index = colorSpaceIndex; index < lines.Count; index++)
        {
            var line = lines[index];
            if (index > colorSpaceIndex
                && line.TrimStart().StartsWith("Box:", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            colorMetadata.Add(line);
        }

        return string.Join('\n', colorMetadata);
    }

    private static string ParseTransfer(string? colorMetadata)
    {
        if (string.IsNullOrWhiteSpace(colorMetadata))
        {
            return "未知";
        }

        if (HasMetadataValue(colorMetadata, "Transfer function", "PQ")
            || colorMetadata.Contains("PQ transfer function", StringComparison.OrdinalIgnoreCase))
        {
            return "PQ / SMPTE ST 2084";
        }

        if (HasMetadataValue(colorMetadata, "Transfer function", "HLG")
            || colorMetadata.Contains("HLG transfer function", StringComparison.OrdinalIgnoreCase))
        {
            return "HLG";
        }

        if (HasMetadataValue(colorMetadata, "Transfer function", "linear")
            || colorMetadata.Contains("linear transfer function", StringComparison.OrdinalIgnoreCase))
        {
            return "linear";
        }

        if (HasMetadataValue(colorMetadata, "Transfer function", "sRGB")
            || colorMetadata.Contains("sRGB transfer function", StringComparison.OrdinalIgnoreCase))
        {
            return "sRGB";
        }

        return "未知";
    }

    private static string ParsePrimaries(string? colorMetadata)
    {
        if (string.IsNullOrWhiteSpace(colorMetadata))
        {
            return "primaries 未知";
        }

        if (HasMetadataValue(colorMetadata, "Primaries", "Rec.2100")
            || colorMetadata.Contains("Rec.2100 primaries", StringComparison.OrdinalIgnoreCase))
        {
            return "Rec.2100 / BT.2020";
        }

        if (HasMetadataValue(colorMetadata, "Primaries", "BT.2020")
            || colorMetadata.Contains("BT.2020 primaries", StringComparison.OrdinalIgnoreCase))
        {
            return "BT.2020";
        }

        if (HasMetadataValue(colorMetadata, "Primaries", "sRGB")
            || colorMetadata.Contains("sRGB primaries", StringComparison.OrdinalIgnoreCase))
        {
            return "sRGB / BT.709";
        }

        return colorMetadata.Trim();
    }

    private static bool HasMetadataValue(string metadata, string label, string value)
    {
        return Regex.IsMatch(
            metadata,
            $@"(?:^|\n)\s*{Regex.Escape(label)}:\s*{Regex.Escape(value)}(?:\s|,|$)",
            RegexOptions.IgnoreCase);
    }

    private static int? TryParseInt(Match match, string groupName)
    {
        return match.Success && int.TryParse(match.Groups[groupName].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static double? ParseDouble(string output, string key)
    {
        var keyPattern = string.Join(
            @"[\s_]+",
            key.Split([' ', '_'], StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
        var match = Regex.Match(
            output,
            $@"{keyPattern}:\s*(?<value>[-+]?\d+(?:\.\d+)?)",
            RegexOptions.IgnoreCase);
        return match.Success && double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    [GeneratedRegex(@"(?<width>\d+)x(?<height>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"(?<depth>\d+)-bit", RegexOptions.IgnoreCase)]
    private static partial Regex BitDepthRegex();
}
