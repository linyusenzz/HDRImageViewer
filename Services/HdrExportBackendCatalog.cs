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
        return "Native backends: libultrahdr for Ultra HDR JPEG, HdrImageViewer.Native/OpenEXR for EXR, libjxl/cjxl for JPEG XL HDR, libavif/avifenc for AVIF HDR, libheif/heif-enc for HEIF/HEIC HDR.";
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
        return SingleLayerHdrExportService.GetCapabilities()
            .Select(capability => new HdrExportFormatChoice(
                capability.DisplayName,
                capability.Extension,
                capability.Backend,
                capability.IsAvailable,
                capability.Details))
            .ToArray();
    }

}

