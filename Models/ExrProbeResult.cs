namespace HdrImageViewer.Models;

public sealed record ExrProbeResult(
    bool IsOpenExr,
    int? PixelWidth,
    int? PixelHeight,
    string DecoderName,
    string Status,
    bool UsesBt2020Primaries = false,
    bool UsesProPhotoPrimaries = false)
{
    private string PrimariesSummary =>
        UsesBt2020Primaries ? "BT.2020" : UsesProPhotoPrimaries ? "ProPhoto RGB" : "source primaries";

    public string ColorSummary =>
        PixelWidth is > 0 && PixelHeight is > 0
            ? $"{PixelWidth}x{PixelHeight}; half/float RGBA; {PrimariesSummary}"
            : "half/float channels";

    public string DisplayStatus =>
        IsOpenExr && PixelWidth is > 0 && PixelHeight is > 0
            ? $"OpenEXR 已接入 {DecoderName}；按 scene-linear {PrimariesSummary} RGBA16F 单层 HDR 预览。"
            : Status;
}
