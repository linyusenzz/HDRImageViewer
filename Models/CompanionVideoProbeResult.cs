namespace HdrImageViewer.Models;

public sealed record CompanionVideoProbeResult(
    string ContainerSummary,
    string? SampleEntryType = null,
    string? CodecSummary = null,
    int? Width = null,
    int? Height = null,
    int? ColorPrimaries = null,
    int? TransferCharacteristics = null,
    int? MatrixCoefficients = null,
    bool? FullRange = null,
    int? LumaBitDepth = null,
    int? ChromaBitDepth = null)
{
    public bool HasHdrSignal =>
        TransferCharacteristics is 16 or 18
        || (ColorPrimaries == 9 && (LumaBitDepth ?? 8) >= 10);

    public string DisplaySummary
    {
        get
        {
            var size = Width is > 0 && Height is > 0 ? $"{Width}x{Height}; " : string.Empty;
            var codec = CodecSummary ?? SampleEntryType ?? "codec unknown";
            var bitDepth = LumaBitDepth is { } luma
                ? $"; {luma}-bit luma{(ChromaBitDepth is { } chroma ? $"/{chroma}-bit chroma" : string.Empty)}"
                : string.Empty;
            var color = CreateColorSummary();
            var hdr = HasHdrSignal ? "HDR signal detected" : "HDR signal not detected";
            return $"{ContainerSummary}; {size}{codec}{bitDepth}; {color}; {hdr}";
        }
    }

    private string CreateColorSummary()
    {
        var primaries = DescribePrimaries(ColorPrimaries);
        var transfer = DescribeTransfer(TransferCharacteristics);
        var matrix = DescribeMatrix(MatrixCoefficients);
        var range = FullRange is null ? "range unknown" : FullRange.Value ? "full range" : "limited range";
        return $"{primaries}; {transfer}; {matrix}; {range}";
    }

    private static string DescribePrimaries(int? value)
    {
        return value switch
        {
            1 => "BT.709 primaries",
            9 => "BT.2020 primaries",
            12 => "Display P3 primaries",
            null => "primaries unknown",
            _ => $"primaries CICP {value}",
        };
    }

    private static string DescribeTransfer(int? value)
    {
        return value switch
        {
            1 => "BT.709 transfer",
            13 => "sRGB transfer",
            14 => "BT.2020 10-bit transfer",
            15 => "BT.2020 12-bit transfer",
            16 => "PQ / ST 2084 transfer",
            18 => "HLG transfer",
            null => "transfer unknown",
            _ => $"transfer CICP {value}",
        };
    }

    private static string DescribeMatrix(int? value)
    {
        return value switch
        {
            0 => "RGB matrix",
            1 => "BT.709 matrix",
            9 => "BT.2020 non-constant matrix",
            null => "matrix unknown",
            _ => $"matrix CICP {value}",
        };
    }
}
