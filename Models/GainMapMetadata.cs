namespace HdrImageViewer.Models;

public sealed record GainMapMetadata(
    string? Version,
    string? GainMapMin,
    string? GainMapMax,
    string? Gamma,
    string? OffsetSdr,
    string? OffsetHdr,
    string? HdrCapacityMin,
    string? HdrCapacityMax,
    bool? BaseRenditionIsHdr,
    string Source = "Ultra HDR / Adobe XMP")
{
    public bool HasRequiredRenderingFields =>
        !string.IsNullOrWhiteSpace(Version)
        && !string.IsNullOrWhiteSpace(GainMapMax)
        && !string.IsNullOrWhiteSpace(HdrCapacityMax)
        && BaseRenditionIsHdr != true;

    public string DisplaySummary =>
        HasRequiredRenderingFields
            ? $"{Source}: gain [{GainMapMin ?? "0"}, {GainMapMax}], gamma {Gamma ?? "1"}, HDR capacity [{HdrCapacityMin ?? "0"}, {HdrCapacityMax}]"
            : "元数据不完整";
}
