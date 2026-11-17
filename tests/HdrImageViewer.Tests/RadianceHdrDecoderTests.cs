using System.Buffers.Binary;
using System.Text;
using HdrImageViewer.Models;
using HdrImageViewer.Services;
using Xunit;

namespace HdrImageViewer.Tests;

public sealed class RadianceHdrDecoderTests
{
    [Fact]
    public void Decode_FlatRgbeScanlines_ProducesExpectedSceneLinearFloat16()
    {
        var header = Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 1 +X 3\n");
        byte[] pixels =
        [
            0, 0, 0, 0,             // black (exponent 0)
            128, 64, 0, 129,        // ~1.004, ~0.504, 0.0 (zero mantissa -> 0)
            200, 100, 50, 132,      // HDR highlight: (200.5)*2^-4 = 12.53125
        ];

        var bitmap = RadianceHdrDecoder.Decode([.. header, .. pixels]);

        Assert.Equal(3, bitmap.PixelWidth);
        Assert.Equal(1, bitmap.PixelHeight);
        Assert.Equal(DecodedBitmapPixelFormat.Rgba16Float, bitmap.PixelFormat);
        Assert.Equal(DecodedBitmapTransfer.LinearSceneScRgb, bitmap.Transfer);
        Assert.Equal(GainMapColorGamut.Bt709, bitmap.EffectiveColorGamut);

        AssertPixel(bitmap, 0, 0, 0.0f, 0.0f, 0.0f);
        AssertPixel(bitmap, 1, 0, 128.5f / 128.0f, 64.5f / 128.0f, 0.0f);
        AssertPixel(bitmap, 2, 0, 200.5f / 16.0f, 100.5f / 16.0f, 50.5f / 16.0f);
    }

    [Fact]
    public void Decode_AdaptiveRleScanline_DecodesLiteralAndRepeatRuns()
    {
        var header = Encoding.ASCII.GetBytes("#?RGBE\nFORMAT=32-bit_rle_rgbe\n\n-Y 1 +X 8\n");
        byte[] rleScanline =
        [
            0x02, 0x02, 0x00, 0x08,         // Adaptive RLE header for width = 8
            128 + 6, 160, 2, 80, 40,        // R: 6x 160, then literals 80, 40
            128 + 8, 100,                   // G: 8x 100
            128 + 8, 50,                    // B: 8x 50
            128 + 6, 131, 2, 129, 0,        // E: 6x 131 (HDR ~5.0), 1x 129, 1x 0 (black)
        ];

        var bitmap = RadianceHdrDecoder.Decode([.. header, .. rleScanline]);

        Assert.Equal(8, bitmap.PixelWidth);
        Assert.Equal(1, bitmap.PixelHeight);
        AssertPixel(bitmap, 0, 0, 160.5f / 32.0f, 100.5f / 32.0f, 50.5f / 32.0f);
        AssertPixel(bitmap, 5, 0, 160.5f / 32.0f, 100.5f / 32.0f, 50.5f / 32.0f);
        AssertPixel(bitmap, 6, 0, 80.5f / 128.0f, 100.5f / 128.0f, 50.5f / 128.0f);
        AssertPixel(bitmap, 7, 0, 0.0f, 0.0f, 0.0f);
    }

    [Fact]
    public void Decode_ExposureHeaderAndOrientationFlip_AppliesInverseExposureAndAxisMapping()
    {
        var header = Encoding.ASCII.GetBytes("#?RADIANCE\nEXPOSURE=2.0\nEXPOSURE=2.0\nFORMAT=32-bit_rle_rgbe\n\n+Y 2 -X 2\n");
        byte[] pixels =
        [
            // Scanline 0 (bottom row because +Y, right-to-left because -X):
            128, 0, 0, 131,     // (x=1, y=1)
            0, 128, 0, 131,     // (x=0, y=1)
            // Scanline 1 (top row y=0):
            0, 0, 128, 131,     // (x=1, y=0)
            128, 128, 128, 131, // (x=0, y=0)
        ];

        // Cumulative exposure = 4.0, scale for E=131 is 2^-5 / 4 = 1/128.
        var expected = 128.5f / 128.0f;
        var bitmap = RadianceHdrDecoder.Decode([.. header, .. pixels]);

        Assert.Equal(2, bitmap.PixelWidth);
        Assert.Equal(2, bitmap.PixelHeight);
        AssertPixel(bitmap, 0, 0, expected, expected, expected);
        AssertPixel(bitmap, 1, 0, 0.0f, 0.0f, expected);
        AssertPixel(bitmap, 0, 1, 0.0f, expected, 0.0f);
        AssertPixel(bitmap, 1, 1, expected, 0.0f, 0.0f);
    }

    [Fact]
    public void Decode_DownscalesWhenMaxPixelSizeIsSmallerThanImage()
    {
        var header = Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 4 +X 8\n");
        var data = new List<byte>(header);
        for (var y = 0; y < 4; y++)
        {
            data.AddRange([0x02, 0x02, 0x00, 0x08]);
            data.AddRange([128 + 8, 128]);
            data.AddRange([128 + 8, 128]);
            data.AddRange([128 + 8, 128]);
            data.AddRange([128 + 8, 130]);
        }

        var downscaled = RadianceHdrDecoder.Decode(data.ToArray(), maxPixelSize: 4);

        Assert.Equal(4, downscaled.PixelWidth);
        Assert.Equal(2, downscaled.PixelHeight);
        AssertPixel(downscaled, 0, 0, 128.5f / 64.0f, 128.5f / 64.0f, 128.5f / 64.0f);
    }

    [Theory]
    [InlineData("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 2 +X 2\n")]
    [InlineData("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 1 +X 8\n\u0002\u0002\u0000\u0008\u0085")]
    [InlineData("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 1 +X 8\n\u0002\u0002\u0000\u0009")]
    [InlineData("not a radiance header\n\n-Y 1 +X 1\n\0\0\0\0")]
    public void Decode_CorruptOrTruncatedData_ThrowsInvalidDataException(string asciiPayload)
    {
        var bytes = Encoding.Latin1.GetBytes(asciiPayload);
        Assert.Throws<InvalidDataException>(() => RadianceHdrDecoder.Decode(bytes));
    }

    [Fact]
    public void FileSignatureProbe_DetectsRadianceHdrAndJxlSignatures()
    {
        Assert.Equal(
            FileContainerKind.RadianceHdr,
            FileSignatureProbe.Detect("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n"u8));
        Assert.Equal(
            FileContainerKind.RadianceHdr,
            FileSignatureProbe.Detect("#?RGBE\n"u8));
        Assert.Equal(
            FileContainerKind.Jxl,
            FileSignatureProbe.Detect([0xFF, 0x0A, 0x00, 0x00]));
        Assert.Equal(
            FileContainerKind.Jxl,
            FileSignatureProbe.Detect([0x00, 0x00, 0x00, 0x0C, 0x4A, 0x58, 0x4C, 0x20, 0x0D, 0x0A, 0x87, 0x0A]));
    }

    [Fact]
    public async Task DirectoryMetadataCache_PreservesXyzeRadianceHdrDescriptorOnCacheHit()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"hdriv-xyze-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var hdrPath = Path.Combine(tempDir, "sample-xyze.hdr");
        try
        {
            var header = Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_xyze\n\n-Y 2 +X 4\n");
            var payload = new List<byte>(header);
            for (var y = 0; y < 2; y++)
            {
                payload.AddRange([0x02, 0x02, 0x00, 0x04]);
                payload.AddRange([128 + 4, 96]);
                payload.AddRange([128 + 4, 100]);
                payload.AddRange([128 + 4, 108]);
                payload.AddRange([128 + 4, 129]);
            }

            await File.WriteAllBytesAsync(hdrPath, payload.ToArray());
            var probe = RadianceHdrDecoder.TryProbeFile(hdrPath);
            Assert.NotNull(probe);
            Assert.True(probe!.IsXyze);

            var initialDescriptor = DecoderCatalog.Describe(
                hdrPath,
                gainMapProbe: null,
                heifAvifProbe: null,
                jxlProbe: null,
                wicImageProbe: null,
                exrProbe: null,
                FileContainerKind.RadianceHdr,
                probe);
            Assert.Contains("XYZE", initialDescriptor.DisplayName, StringComparison.Ordinal);

            var document = new HdrImageDocument(
                hdrPath,
                Path.GetFileName(hdrPath),
                initialDescriptor,
                RadianceHdrProbe: probe);
            var loadResult = new ImageLoadResult(document, "EXIF none", File.GetLastWriteTimeUtc(hdrPath));
            await DirectoryMetadataCache.StoreAsync(loadResult, FileContainerKind.RadianceHdr);

            var cached = await DirectoryMetadataCache.TryLoadAsync(hdrPath);
            Assert.NotNull(cached);
            Assert.NotNull(cached!.Document.RadianceHdrProbe);
            Assert.True(cached.Document.RadianceHdrProbe!.IsXyze);
            Assert.Contains("XYZE", cached.Document.Format.DisplayName, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static void AssertPixel(DecodedBitmap bitmap, int x, int y, float expectedR, float expectedG, float expectedB)
    {
        var offset = ((y * bitmap.PixelWidth) + x) * 8;
        var r = (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bitmap.RgbaPixels.AsSpan(offset, 2)));
        var g = (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bitmap.RgbaPixels.AsSpan(offset + 2, 2)));
        var b = (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bitmap.RgbaPixels.AsSpan(offset + 4, 2)));
        var a = (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bitmap.RgbaPixels.AsSpan(offset + 6, 2)));

        Assert.InRange(Math.Abs(r - expectedR), 0.0f, 0.01f);
        Assert.InRange(Math.Abs(g - expectedG), 0.0f, 0.01f);
        Assert.InRange(Math.Abs(b - expectedB), 0.0f, 0.01f);
        Assert.Equal(1.0f, a);
    }
}
