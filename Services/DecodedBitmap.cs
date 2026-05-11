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
}

public sealed record DecodedBitmap(
    int PixelWidth,
    int PixelHeight,
    byte[] RgbaPixels,
    bool ColorManagedToSrgb,
    string DecoderName = "Windows Imaging",
    DecodedBitmapPixelFormat PixelFormat = DecodedBitmapPixelFormat.Rgba8Unorm,
    DecodedBitmapTransfer Transfer = DecodedBitmapTransfer.Sdr,
    bool UsesBt2020Primaries = false)
{
    public int BytesPerPixel => PixelFormat is DecodedBitmapPixelFormat.Rgba16Unorm or DecodedBitmapPixelFormat.Rgba16Float ? 8 : 4;

    public long ApproximateByteCount => RgbaPixels.LongLength;

    public bool IsHdrEncoded => Transfer is DecodedBitmapTransfer.Hlg or DecodedBitmapTransfer.Pq or DecodedBitmapTransfer.LinearScRgb;

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
                _ => "sRGB"
            };
            var primaries = Transfer == DecodedBitmapTransfer.LinearScRgb
                ? "working scRGB/P709 extended"
                : UsesBt2020Primaries ? "BT.2020" : "sRGB/BT.709";
            return $"{DecoderName}, {format}, {transfer}, {primaries}";
        }
    }
}
