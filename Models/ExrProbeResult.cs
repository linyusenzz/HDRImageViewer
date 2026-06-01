namespace HdrImageViewer.Models;

public sealed record ExrProbeResult(
    bool IsOpenExr,
    int? PixelWidth,
    int? PixelHeight,
    string DecoderName,
    string Status,
    bool UsesBt2020Primaries = false)
{
    public string ColorSummary =>
        PixelWidth is > 0 && PixelHeight is > 0
            ? $"{PixelWidth}x{PixelHeight}; half/float RGBA; {(UsesBt2020Primaries ? "BT.2020" : "source primaries")}"
            : "half/float channels";

    public string DisplayStatus =>
        IsOpenExr
            ? $"OpenEXR 已接入 HdrImageViewer.Native；按 scene-linear {(UsesBt2020Primaries ? "BT.2020 " : string.Empty)}RGBA16F 单层 HDR 预览。"
            : Status;
}
