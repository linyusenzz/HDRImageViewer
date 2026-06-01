namespace HdrImageViewer.Models;

public sealed record WicImageProbeResult(
    bool IsWicCandidate,
    string Extension,
    uint? PixelWidth,
    uint? PixelHeight,
    int? BitDepth,
    string? ColorSpace,
    string DecoderName,
    int? BitsPerChannel = null,
    int? PngColorType = null,
    int? PngCicpPrimaries = null,
    int? PngCicpTransfer = null,
    int? PngCicpMatrix = null,
    bool? PngCicpFullRange = null,
    string? PngIccProfileName = null,
    int[]? TiffBitsPerSample = null,
    int? TiffSamplesPerPixel = null,
    int? TiffSampleFormat = null,
    int? TiffPhotometricInterpretation = null,
    string? TiffIccProfileName = null)
{
    public bool IsJpegXr =>
        string.Equals(Extension, ".jxr", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Extension, ".wdp", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Extension, ".hdp", StringComparison.OrdinalIgnoreCase);

    public bool IsPng =>
        string.Equals(Extension, ".png", StringComparison.OrdinalIgnoreCase);

    public bool IsTiff =>
        string.Equals(Extension, ".tif", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Extension, ".tiff", StringComparison.OrdinalIgnoreCase);

    public bool IsHighBitDepth => BitDepth is > 32 || BitsPerChannel is > 8 || TiffBitsPerSample?.Any(bitDepth => bitDepth > 8) == true;

    public bool IsTiffFloatingPoint => IsTiff && TiffSampleFormat == 3;

    public bool HasPngCicp => IsPng && PngCicpTransfer is not null;

    public bool IsPngPq => IsPng && PngCicpTransfer == 16;

    public bool IsPngHlg => IsPng && PngCicpTransfer == 18;

    public bool IsPngHdr => IsPngPq || IsPngHlg;

    public bool IsTiffPq => IsTiff && TiffIccProfileName?.Contains("PQ", StringComparison.OrdinalIgnoreCase) == true;

    public bool UsesBt2020Primaries => PngCicpPrimaries == 9 || IsTiffBt2020 || IsPngBt2020Icc;

    public bool UsesDisplayP3Primaries => PngCicpPrimaries == 12 || IsPngDisplayP3Icc;

    public bool IsPngDisplayP3Icc =>
        IsPng && PngIccProfileName?.Contains("Display P3", StringComparison.OrdinalIgnoreCase) == true;

    public bool IsPngBt2020Icc =>
        IsPng
        && (PngIccProfileName?.Contains("Rec. 2020", StringComparison.OrdinalIgnoreCase) == true
            || PngIccProfileName?.Contains("Rec2020", StringComparison.OrdinalIgnoreCase) == true
            || PngIccProfileName?.Contains("BT.2020", StringComparison.OrdinalIgnoreCase) == true
            || PngIccProfileName?.Contains("BT2020", StringComparison.OrdinalIgnoreCase) == true);

    public bool IsTiffBt2020 =>
        IsTiff
        && (TiffIccProfileName?.Contains("Rec. 2020", StringComparison.OrdinalIgnoreCase) == true
            || TiffIccProfileName?.Contains("Rec2020", StringComparison.OrdinalIgnoreCase) == true
            || TiffIccProfileName?.Contains("BT.2020", StringComparison.OrdinalIgnoreCase) == true);

    public bool HasHdrColorSpaceHint =>
        ColorSpace?.Contains("scRGB", StringComparison.OrdinalIgnoreCase) == true
        || ColorSpace?.Contains("HDR", StringComparison.OrdinalIgnoreCase) == true
        || ColorSpace?.Contains("PQ", StringComparison.OrdinalIgnoreCase) == true
        || ColorSpace?.Contains("HLG", StringComparison.OrdinalIgnoreCase) == true
        || IsPngHdr
        || IsTiffPq
        || IsTiffFloatingPoint;

    public HdrImageKind SuggestedKind =>
        IsJpegXr || HasHdrColorSpaceHint || IsPngHdr || IsTiffFloatingPoint
            ? HdrImageKind.SingleLayerHdr
            : HdrImageKind.StandardDynamicRange;

    public string DisplayName
    {
        get
        {
            var baseName = IsJpegXr
                ? "JPEG XR"
                : IsPng ? "PNG" : IsTiff ? "TIFF" : "WIC image";
            if (IsPngHdr)
            {
                return $"{baseName} {PngTransferName}";
            }

            if (IsTiffPq)
            {
                return $"{baseName} PQ";
            }

            if (IsTiffFloatingPoint)
            {
                return $"{baseName} floating point";
            }

            return IsHighBitDepth ? $"{baseName} high bit-depth" : baseName;
        }
    }

    public string TransferSummary =>
        IsTiffPq
            ? $"PQ ({TiffIccProfileName ?? "TIFF ICC"})"
            : IsTiffFloatingPoint
            ? "Scene-linear floating point"
            : IsPngHdr
            ? $"{PngTransferName} (PNG cICP)"
            : IsJpegXr
                ? "Extended range / scRGB"
                : HasHdrColorSpaceHint ? ColorSpace ?? "HDR color space hint" : "sRGB/ICC or unspecified";

    public string PngTransferName =>
        PngCicpTransfer switch
        {
            16 => "PQ",
            18 => "HLG",
            13 => "sRGB",
            1 => "BT.709",
            _ => HasPngCicp ? $"cICP transfer {PngCicpTransfer}" : "unspecified transfer",
        };

    public string PngPrimariesName =>
        PngCicpPrimaries switch
        {
            9 => "BT.2020",
            1 => "BT.709",
            12 => "Display P3",
            _ => HasPngCicp ? $"primaries {PngCicpPrimaries}" : "unspecified primaries",
        };

    public string WicTransferName =>
        IsTiffPq ? "PQ" : PngTransferName;

    public string WicPrimariesName =>
        IsTiffBt2020 ? "BT.2020" : PngPrimariesName;

    public string ColorSummary
    {
        get
        {
            var parts = new List<string>();
            if (PixelWidth is > 0 && PixelHeight is > 0)
            {
                parts.Add($"{PixelWidth}x{PixelHeight}");
            }

            if (BitDepth is > 0)
            {
                parts.Add($"{BitDepth}-bit");
            }

            if (BitsPerChannel is > 0)
            {
                parts.Add($"{BitsPerChannel}-bit/channel");
            }

            if (TiffBitsPerSample is { Length: > 0 })
            {
                parts.Add($"TIFF bits/sample {string.Join(",", TiffBitsPerSample)}");
            }

            if (TiffSampleFormat is not null)
            {
                parts.Add(TiffSampleFormatName);
            }

            if (!string.IsNullOrWhiteSpace(ColorSpace))
            {
                parts.Add(ColorSpace);
            }

            if (!string.IsNullOrWhiteSpace(TiffIccProfileName))
            {
                parts.Add($"TIFF ICC {TiffIccProfileName}");
            }

            if (!string.IsNullOrWhiteSpace(PngIccProfileName))
            {
                parts.Add($"PNG ICC {PngIccProfileName}");
            }

            if (HasPngCicp)
            {
                parts.Add($"PNG cICP {PngPrimariesName} / {PngTransferName}");
                if (PngCicpFullRange is not null)
                {
                    parts.Add(PngCicpFullRange.Value ? "full range" : "limited range");
                }
            }

            return parts.Count > 0 ? string.Join("; ", parts) : "WIC image";
        }
    }

    public string DisplayStatus =>
        IsTiffPq
            ? $"TIFF ICC marks this image as {TiffIccProfileName}; decoding as 16-bit HDR."
            : IsTiffFloatingPoint
            ? "Floating-point TIFF detected; decoding through WIC as linear RGBA16F HDR when supported."
            : IsPngHdr
            ? $"PNG cICP marks this image as {PngTransferName} {PngPrimariesName}; decoding as 16-bit HDR."
            : IsJpegXr
                ? "JPEG XR is decoded through the Windows WIC FP16/scRGB path for single-layer HDR preview."
                : IsHighBitDepth
                    ? "High bit-depth WIC image detected; preserving 16-bit precision on the SDR/ICC path."
                    : "Native WIC decode; previewing as SDR/ICC image.";

    private string TiffSampleFormatName =>
        TiffSampleFormat switch
        {
            1 => "unsigned integer samples",
            2 => "signed integer samples",
            3 => "floating point samples",
            4 => "undefined sample format",
            _ => $"sample format {TiffSampleFormat}",
        };
}
