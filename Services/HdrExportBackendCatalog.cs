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
        return "Native backends: libultrahdr for JPEG/HEIC/AVIF Gain Map, Windows WIC for JPEG XR HDR, HdrImageViewer.Native/OpenEXR for EXR, libjxl/cjxl for JPEG XL HDR, libavif/avifenc for AVIF HDR, libheif/heif-enc for HEIF/HEIC HDR.";
    }

    private static IReadOnlyList<HdrExportFormatChoice> GetGainMapChoices()
    {
        var ultraHdr = GainMapHdrExportService.GetCapability();
        return (HdrExportFormatChoice[])
        [
            new HdrExportFormatChoice(
                "JPEG Ultra HDR / gain-map",
                ".jpg",
                ultraHdr.Backend,
                ultraHdr.CanWriteJpegUltraHdr,
                ultraHdr.Details),
            new HdrExportFormatChoice(
                "AVIF Gain Map",
                ".avif",
                ultraHdr.Backend,
                ultraHdr.CanWriteHeifGainMap,
                "ISO 21496-1 Gain Map"),
            new HdrExportFormatChoice(
                "HEIC Gain Map",
                ".heic",
                ultraHdr.Backend,
                ultraHdr.CanWriteHeifGainMap,
                "ISO 21496-1 Gain Map"),
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

