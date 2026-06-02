namespace HdrImageViewer.Models;

public sealed record JxlProbeResult(
    bool IsJxl,
    int? PixelWidth,
    int? PixelHeight,
    int? BitDepth,
    string TransferFunction,
    string ColorPrimaries,
    double? IntensityTargetNits,
    double? MinNits,
    string Summary,
    bool HasGainMapBox = false)
{
    public bool IsHdrTransfer =>
        TransferFunction.Contains("PQ", StringComparison.OrdinalIgnoreCase)
        || TransferFunction.Contains("HLG", StringComparison.OrdinalIgnoreCase)
        || TransferFunction.Contains("linear", StringComparison.OrdinalIgnoreCase)
        || TransferFunction.Contains("HDR", StringComparison.OrdinalIgnoreCase);

    public bool IsHdrCandidate => IsHdrTransfer || HasGainMapBox;

    public bool UsesBt2020Primaries =>
        ColorPrimaries.Contains("Rec.2100", StringComparison.OrdinalIgnoreCase)
        || ColorPrimaries.Contains("BT.2020", StringComparison.OrdinalIgnoreCase);

    public string TransferSummary
    {
        get
        {
            var target = IntensityTargetNits is { } nits
                ? $", intensity target {nits:0} nits"
                : string.Empty;
            return $"{TransferFunction}{target}";
        }
    }

    public string ColorSummary
    {
        get
        {
            var bitDepth = BitDepth is { } depth ? $"{depth}-bit" : "位深未知";
            var size = PixelWidth is { } width && PixelHeight is { } height
                ? $"{width}x{height}"
                : "尺寸未知";
            var min = MinNits is { } minNits ? $", min {minNits:0.###} nits" : string.Empty;
            return $"{ColorPrimaries}; {bitDepth}; {size}{min}";
        }
    }

    public string DisplayStatus =>
        IsJxl && HasGainMapBox
            ? $"JPEG XL gain-map metadata detected: {TransferSummary}; {ColorSummary}."
            : IsJxl
            ? $"JPEG XL metadata: {TransferSummary}; {ColorSummary}."
            : "不是 JPEG XL 文件。";
}
