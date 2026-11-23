using System.Runtime.InteropServices;
using HdrImageViewer.Services;
using Xunit;

namespace HdrImageViewer.Tests;

/// <summary>
/// Integration gate for the EXR paths: encode a deterministic half-float
/// gradient with <see cref="NativeExrDecoder.Encode"/>, decode it with
/// <see cref="NativeExrDecoder.Decode"/>, then force the fallback by feeding the
/// same EXR through <c>oiiotool</c>/<c>magick</c> → PFM →
/// <see cref="PortableImageReader.ReadPfmAsLinearScRgb"/> and assert the two
/// produce identical pixels. Reports a skip when the native EXR library or an
/// external EXR→PFM converter is not available (fresh checkouts / CI).
/// </summary>
public sealed class ExrNativeDecoderParityTests
{
    public sealed class ExrFactAttribute : FactAttribute
    {
        public ExrFactAttribute(bool requiresConverter = false)
        {
            if (Environment.GetEnvironmentVariable("HDRVIEWER_REQUIRE_NATIVE_TESTS") != "1")
                Skip = MissingDependency(requiresConverter);
        }
    }

    private static string? MissingDependency(bool requiresConverter) =>
        !RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !NativeExrDecoder.IsAvailable
            ? "Requires Windows and the native OpenEXR library." :
        requiresConverter && FindExrToPfmConverter() is null ? "Requires an EXR to PFM converter." : null;

    [ExrFact]
    public void EncodeThenNativeDecodeRoundTripsRgba16f()
    {
        Assert.Null(MissingDependency(false));

        const int width = 64;
        const int height = 48;
        var pixels = CreateDeterministicRgba16f(width, height);
        var tempDir = Path.Combine(Path.GetTempPath(), "HdrImageViewerTests", "exr-roundtrip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var exrPath = Path.Combine(tempDir, "fixture.exr");
        try
        {
            NativeExrDecoder.Encode(exrPath, width, height, pixels);
            var decoded = NativeExrDecoder.Decode(exrPath);

            Assert.Equal(width, decoded.PixelWidth);
            Assert.Equal(height, decoded.PixelHeight);
            Assert.Equal(DecodedBitmapPixelFormat.Rgba16Float, decoded.PixelFormat);
            Assert.Equal(DecodedBitmapTransfer.LinearSceneScRgb, decoded.Transfer);
            Assert.Equal(pixels, decoded.RgbaPixels);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [ExrFact]
    public void PreviewPreservesBrightScanlineAcrossBlockBoundaries()
    {
        const int size = 195;
        var pixels = new byte[size * size * 8];
        var words = MemoryMarshal.Cast<byte, ushort>(pixels.AsSpan());
        for (int x = 0; x < size; ++x)
            words[(64 * size + x) * 4] = BitConverter.HalfToUInt16Bits((Half)3);
        var path = Path.Combine(Path.GetTempPath(), $"hdriv-scanlines-{Guid.NewGuid():N}.exr");
        try
        {
            NativeExrDecoder.Encode(path, size, size, pixels);
            foreach (var edge in new[] { 65, 1 })
            {
                var preview = NativeExrDecoder.Decode(path, edge);
                var offset = (edge == 65 ? 21 * edge : 0) * 8;
                var red = BitConverter.ToHalf(preview.RgbaPixels, offset);
                Assert.Equal((Half)(3f * edge / size), red);
            }
        }
        finally { File.Delete(path); }
    }

    [ExrFact(true)]
    public async Task ExternalPfmFallbackMatchesNativeDecode()
    {
        Assert.Null(MissingDependency(false));

        Assert.Null(MissingDependency(true));
        var converter = FindExrToPfmConverter()!;

        const int width = 32;
        const int height = 24;
        var pixels = CreateDeterministicRgba16f(width, height);
        var tempDir = Path.Combine(Path.GetTempPath(), "HdrImageViewerTests", "exr-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var exrPath = Path.Combine(tempDir, "fixture.exr");
        var pfmPath = Path.Combine(tempDir, "decoded.pfm");
        try
        {
            NativeExrDecoder.Encode(exrPath, width, height, pixels);
            var native = NativeExrDecoder.Decode(exrPath);

            using (var process = NativeProcessRunner.Create(converter))
            {
                process.StartInfo.ArgumentList.Add(exrPath);
                if (Path.GetFileNameWithoutExtension(converter).Equals("magick", StringComparison.OrdinalIgnoreCase))
                {
                    // Preserve scene-linear EXR samples even when ImageMagick labels them sRGB.
                    process.StartInfo.ArgumentList.Add("-set");
                    process.StartInfo.ArgumentList.Add("colorspace");
                    process.StartInfo.ArgumentList.Add("RGB");
                    process.StartInfo.ArgumentList.Add("-define");
                    process.StartInfo.ArgumentList.Add("quantum:format=floating-point");
                    process.StartInfo.ArgumentList.Add("-depth");
                    process.StartInfo.ArgumentList.Add("32");
                }
                else
                {
                    process.StartInfo.ArgumentList.Add("-o");
                }

                process.StartInfo.ArgumentList.Add(pfmPath);
                await NativeProcessRunner.RunAsync(process, "EXR→PFM parity fixture", CancellationToken.None);
            }

            var fallback = PortableImageReader.ReadPfmAsLinearScRgb(pfmPath, "parity-test", CancellationToken.None);
            Assert.Equal(native.PixelWidth, fallback.PixelWidth);
            Assert.Equal(native.PixelHeight, fallback.PixelHeight);
            Assert.Equal(DecodedBitmapPixelFormat.Rgba16Float, fallback.PixelFormat);
            Assert.Equal(DecodedBitmapTransfer.LinearScRgb, fallback.Transfer);

            // Half-float PFM→EXR round trip is lossless for the values used
            // here, so the two pipelines must agree byte-for-byte.
            Assert.Equal(native.RgbaPixels, fallback.RgbaPixels);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string? FindExrToPfmConverter()
    {
        return NativeToolLocator.FindTool("oiiotool.exe")
            ?? NativeToolLocator.FindTool("magick.exe");
    }

    private static byte[] CreateDeterministicRgba16f(int width, int height)
    {
        var pixels = new byte[width * height * 8];
        var destination = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                WriteHalf(pixels, destination, (float)x / Math.Max(1, width - 1) * 4.0f);
                WriteHalf(pixels, destination + 2, (float)y / Math.Max(1, height - 1) * 3.0f);
                WriteHalf(pixels, destination + 4, ((x + y) & 1) == 0 ? 0.5f : 2.0f);
                WriteHalf(pixels, destination + 6, 1.0f);
                destination += 8;
            }
        }

        return pixels;
    }

    private static void WriteHalf(byte[] buffer, int offset, float value)
    {
        var bits = BitConverter.HalfToUInt16Bits((Half)value);
        buffer[offset] = (byte)(bits & 0xFF);
        buffer[offset + 1] = (byte)(bits >> 8);
    }
}
