using HdrImageViewer.Models;

namespace HdrImageViewer.Services;

public enum DecodedBitmapPixelFormat
{
    Rgba8Unorm,
    Rgba16Unorm,
    Rgba16Float,
}

public enum DecodedBitmapTransfer
{
    Sdr,
    Hlg,
    Pq,
    LinearScRgb,
    LinearSceneScRgb,
}

public sealed record DecodedBitmap(
    int PixelWidth,
    int PixelHeight,
    byte[] RgbaPixels,
    bool ColorManagedToSrgb,
    string DecoderName = "Windows Imaging",
    DecodedBitmapPixelFormat PixelFormat = DecodedBitmapPixelFormat.Rgba8Unorm,
    DecodedBitmapTransfer Transfer = DecodedBitmapTransfer.Sdr,
    bool UsesBt2020Primaries = false,
    GainMapColorGamut ColorGamut = GainMapColorGamut.Unknown)
{
    public GainMapColorGamut EffectiveColorGamut =>
        ColorGamut != GainMapColorGamut.Unknown
            ? ColorGamut
            : UsesBt2020Primaries ? GainMapColorGamut.Bt2100 : GainMapColorGamut.Bt709;

    public int BytesPerPixel => PixelFormat is DecodedBitmapPixelFormat.Rgba16Unorm or DecodedBitmapPixelFormat.Rgba16Float ? 8 : 4;

    public long ApproximateByteCount => RgbaPixels.LongLength;

    public bool IsHdrEncoded => Transfer is DecodedBitmapTransfer.Hlg or DecodedBitmapTransfer.Pq or DecodedBitmapTransfer.LinearScRgb or DecodedBitmapTransfer.LinearSceneScRgb;

    public string RenderEncodingSummary
    {
        get
        {
            var format = PixelFormat switch
            {
                DecodedBitmapPixelFormat.Rgba16Float => "Rgba16F",
                DecodedBitmapPixelFormat.Rgba16Unorm => "Rgba16",
                _ => "Rgba8"
            };
            var transfer = Transfer switch
            {
                DecodedBitmapTransfer.Hlg => "HLG",
                DecodedBitmapTransfer.Pq => "PQ",
                DecodedBitmapTransfer.LinearScRgb => "linear scRGB",
                DecodedBitmapTransfer.LinearSceneScRgb => "scene-linear scRGB",
                _ => "sRGB"
            };
            var primaries = EffectiveColorGamut switch
            {
                GainMapColorGamut.Bt2100 => "BT.2020",
                GainMapColorGamut.DisplayP3 => "Display P3",
                GainMapColorGamut.ProPhoto => "ProPhoto RGB",
                _ => Transfer is DecodedBitmapTransfer.LinearScRgb or DecodedBitmapTransfer.LinearSceneScRgb ? "working scRGB/P709 extended" : "sRGB/BT.709",
            };
            return $"{DecoderName}, {format}, {transfer}, {primaries}";
        }
    }
}
