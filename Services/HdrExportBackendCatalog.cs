namespace HdrImageViewer.Services;

public enum HdrExportMode
{
    GainMap,
    SingleLayer,
}

public sealed record HdrExportFormatChoice(
    string DisplayName,
    string Extension,
    string Backend,
    bool IsAvailable,
    string Notes);

public static class HdrExportBackendCatalog
{
    public static IReadOnlyList<HdrExportFormatChoice> GetChoices(HdrExportMode mode)
    {
        return mode == HdrExportMode.GainMap
            ? GetGainMapChoices()
            : GetSingleLayerChoices();
    }

    public static string BuildBackendSummary()
    {
        var ffmpeg = HasExecutableOnPath("ffmpeg.exe")
            ? "FFmpeg fallback available for JXL/AVIF single-layer HDR"
            : "FFmpeg fallback unavailable";
        return $"{ffmpeg}; native backends planned: libultrahdr for Ultra HDR JPEG, libavif for AVIF HDR/gain-map, libheif for HEIF/HEIC, libjxl for native JXL.";
    }

    private static IReadOnlyList<HdrExportFormatChoice> GetGainMapChoices()
    {
        var ultraHdr = GainMapHdrExportService.GetCapability();
        return
        [
            new HdrExportFormatChoice(
                "JPEG Ultra HDR / gain-map",
                ".jpg",
                ultraHdr.Backend,
                ultraHdr.CanWriteJpegUltraHdr,
                ultraHdr.Details),
            new HdrExportFormatChoice(
                "AVIF gain-map",
                ".avif",
                "libavif planned",
                false,
                "libavif 已有 gain-map 方向能力，需接 native backend。"),
            new HdrExportFormatChoice(
                "HEIF/HEIC gain-map",
                ".heic",
                "libheif planned",
                false,
                "仅在接入 libheif 并验证 Apple/ISO gain-map item 写入后启用。"),
        ];
    }

    private static IReadOnlyList<HdrExportFormatChoice> GetSingleLayerChoices()
    {
        var ffmpegAvailable = HasExecutableOnPath("ffmpeg.exe");
        return
        [
            new HdrExportFormatChoice(
                "JPEG XL HDR",
                ".jxl",
                ffmpegAvailable ? "FFmpeg libjxl fallback" : "libjxl planned",
                ffmpegAvailable,
                "当前可通过 FFmpeg fallback 写出；后续可切换 native libjxl。"),
            new HdrExportFormatChoice(
                "AVIF HDR",
                ".avif",
                ffmpegAvailable ? "FFmpeg libaom-av1 fallback" : "libavif planned",
                ffmpegAvailable,
                "当前可通过 FFmpeg fallback 写出；后续接 libavif native backend。"),
            new HdrExportFormatChoice(
                "HEIF/HEIC HDR",
                ".heic",
                "libheif planned",
                false,
                "本机 FFmpeg 无 HEIF muxer，先不启用，后续接 libheif。"),
        ];
    }

    private static bool HasExecutableOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(directory, fileName)))
                {
                    return true;
                }
            }
            catch
            {
                continue;
            }
        }

        return false;
    }
}

