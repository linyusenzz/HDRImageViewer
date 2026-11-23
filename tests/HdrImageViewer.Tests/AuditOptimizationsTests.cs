using System.Buffers.Binary;
using System.Numerics;
using HdrImageViewer.Models;
using HdrImageViewer.Rendering;
using HdrImageViewer.Services;
using Xunit;

namespace HdrImageViewer.Tests;

public sealed class AuditOptimizationsTests
{
    private const float Tolerance = 0.001f;

    [Fact]
    public void ReconstructGainMapHdr_HonorsIso21496AlternateColorSpace()
    {
        // Pure BT.709 green has non-zero Red/Blue components when converted to Display P3.
        // Applying per-channel asymmetric gain in Display P3 vs BT.709 produces a distinct result.
        var sdrBt709 = new Vector3(0.0f, 0.8f, 0.0f);
        var gainSample = new Vector3(1.0f, 0.2f, 0.0f);

        var baseSpaceConstants = new GainMapShaderConstants
        {
            GainMapMin = Vector4.Zero,
            GainMapMax = new Vector4(2.0f, 2.0f, 2.0f, 0.0f),
            Gamma = Vector4.One,
            OffsetSdr = Vector4.Zero,
            OffsetHdr = Vector4.Zero,
            GainMapControl = new Vector4(1.0f, 0.0f, 0.0f, 0.0f),
            SourceEncoding = new Vector4(0.0f, 0.0f, 0.0f, 0.0f),
            HdrCapacity = new Vector4(0.0f, 2.0f, 0.0f, 0.0f),
        };

        var p3AlternateConstants = baseSpaceConstants with
        {
            SourceEncoding = new Vector4(0.0f, 0.0f, 1.0f, HdrColorMath.ToShaderGamut(GainMapColorGamut.DisplayP3)),
        };

        var inBase = HdrColorMath.ReconstructGainMapHdrToBt709(sdrBt709, gainSample, baseSpaceConstants, 1.0f);
        var inP3 = HdrColorMath.ReconstructGainMapHdrToBt709(sdrBt709, gainSample, p3AlternateConstants, 1.0f);

        // Base-space BT.709 reconstruction keeps R == 0 because sdrBt709.X == 0 and Offset == 0.
        Assert.Equal(0.0f, inBase.X, Tolerance);
        // Display P3 alternate-space reconstruction converts BT.709 green to P3 (where R_p3 > 0), boosts R_p3 by 4x,
        // and converts back to BT.709, yielding a non-zero R_bt709 component.
        Assert.True(inP3.X > 0.1f, $"Expected alternate P3 gain to boost red channel, got {inP3.X}");

        var inBt2020 = HdrColorMath.ReconstructGainMapHdrToBt2020(sdrBt709, gainSample, p3AlternateConstants, 1.0f);
        var expectedBt2020 = HdrColorMath.Bt709ToBt2020(inP3);
        Assert.Equal(expectedBt2020.X, inBt2020.X, Tolerance);
        Assert.Equal(expectedBt2020.Y, inBt2020.Y, Tolerance);
        Assert.Equal(expectedBt2020.Z, inBt2020.Z, Tolerance);
    }

    [Fact]
    public void CalculateGainMapWeight_SupportsInverseIsoGainMaps()
    {
        var inverseConstants = new GainMapShaderConstants
        {
            GainMapControl = new Vector4(1.0f, 0.0f, 0.0f, 0.0f),
            HdrCapacity = new Vector4(3.0f, 0.0f, 0.0f, 0.0f),
        };

        Assert.Equal(0.0f, HdrColorMath.CalculateGainMapWeight(inverseConstants, 3.0f), Tolerance);
        Assert.Equal(0.5f, HdrColorMath.CalculateGainMapWeight(inverseConstants, 1.5f), Tolerance);
        Assert.Equal(1.0f, HdrColorMath.CalculateGainMapWeight(inverseConstants, 0.0f), Tolerance);
    }

    [Fact]
    public void SanitizeRgbaHalfBufferInPlace_ReplacesNonFiniteValuesAndPreservesFiniteBits()
    {
        var bytes = new byte[16];
        // Pixel 0: R=NaN, G=+Inf, B=-Inf, A=NaN
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0, 2), 0x7E00);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2, 2), 0x7C00);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), 0xFC00);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), 0xFE01);
        // Pixel 1: finite values (2.5, -0.5, 0.0, 0.75)
        var r1 = BitConverter.HalfToUInt16Bits((Half)2.5f);
        var g1 = BitConverter.HalfToUInt16Bits((Half)(-0.5f));
        var b1 = BitConverter.HalfToUInt16Bits((Half)0.0f);
        var a1 = BitConverter.HalfToUInt16Bits((Half)0.75f);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), r1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10, 2), g1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12, 2), b1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14, 2), a1);

        HdrColorMath.SanitizeRgbaHalfBufferInPlace(bytes);

        Assert.Equal(0.0f, (float)BitConverter.UInt16BitsToHalf(ReadUInt16(bytes, 0)));
        Assert.Equal(65504.0f, (float)BitConverter.UInt16BitsToHalf(ReadUInt16(bytes, 2)));
        Assert.Equal(-65504.0f, (float)BitConverter.UInt16BitsToHalf(ReadUInt16(bytes, 4)));
        Assert.Equal(1.0f, (float)BitConverter.UInt16BitsToHalf(ReadUInt16(bytes, 6)));

        Assert.Equal(r1, ReadUInt16(bytes, 8));
        Assert.Equal(g1, ReadUInt16(bytes, 10));
        Assert.Equal(b1, ReadUInt16(bytes, 12));
        Assert.Equal(a1, ReadUInt16(bytes, 14));
    }

    [Fact]
    public void BitmapPreviewResampler_BoxIntegrationPreservesSubTexelHdrHighlightsAndSanitizesNaN()
    {
        var pixels = new byte[4 * 4 * 8];
        for (var i = 0; i < 16; i++)
        {
            var offset = i * 8;
            var r = i == 15 ? (Half)16.0f : (Half)0.0f;
            var g = i == 0 ? Half.NaN : (Half)2.0f;
            BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(offset, 2), BitConverter.HalfToUInt16Bits(r));
            BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(offset + 2, 2), BitConverter.HalfToUInt16Bits(g));
            BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(offset + 4, 2), BitConverter.HalfToUInt16Bits((Half)0.0f));
            BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(offset + 6, 2), BitConverter.HalfToUInt16Bits((Half)1.0f));
        }

        var source = new DecodedBitmap(
            4,
            4,
            pixels,
            ColorManagedToSrgb: false,
            PixelFormat: DecodedBitmapPixelFormat.Rgba16Float,
            Transfer: DecodedBitmapTransfer.LinearSceneScRgb);

        var downscaled = BitmapPreviewResampler.Downscale(source, 1, CancellationToken.None);
        Assert.Equal(1, downscaled.PixelWidth);
        Assert.Equal(1, downscaled.PixelHeight);

        var outR = (float)BitConverter.UInt16BitsToHalf(ReadUInt16(downscaled.RgbaPixels, 0));
        var outG = (float)BitConverter.UInt16BitsToHalf(ReadUInt16(downscaled.RgbaPixels, 2));
        Assert.Equal(1.0f, outR, 0.01f);
        Assert.True(float.IsFinite(outG));
        Assert.Equal(30.0f / 16.0f, outG, 0.01f);
    }

    [Fact]
    public void ThumbnailPixelConverter_ColorManagedRgba8SwizzlesAccuratelyAcrossSimdAndTail()
    {
        // 37 pixels exercises AVX2 (8 px), SSSE3 (4 px), and scalar remainder (1 px).
        const int width = 37;
        var rgba = new byte[width * 4];
        for (var i = 0; i < width; i++)
        {
            rgba[(i * 4) + 0] = (byte)((i * 7) + 1);
            rgba[(i * 4) + 1] = (byte)((i * 5) + 2);
            rgba[(i * 4) + 2] = (byte)((i * 3) + 3);
            rgba[(i * 4) + 3] = (byte)(i % 251);
        }

        var bitmap = new DecodedBitmap(width, 1, rgba, ColorManagedToSrgb: true, PixelFormat: DecodedBitmapPixelFormat.Rgba8Unorm);
        var bgraOpaque = ThumbnailPixelConverter.ConvertSdrToBgra8(bitmap, CancellationToken.None, premultiplyAlpha: false);

        for (var i = 0; i < width; i++)
        {
            Assert.Equal(rgba[(i * 4) + 2], bgraOpaque[(i * 4) + 0]);
            Assert.Equal(rgba[(i * 4) + 1], bgraOpaque[(i * 4) + 1]);
            Assert.Equal(rgba[(i * 4) + 0], bgraOpaque[(i * 4) + 2]);
            Assert.Equal(255, bgraOpaque[(i * 4) + 3]);
        }
    }

    [Theory]
    [InlineData(GainMapColorGamut.AdobeRgb)]
    [InlineData(GainMapColorGamut.ProPhoto)]
    public void ThumbnailDecodePolicy_RequiresPixelDecodeForAdobeRgbAndProPhotoIcc(GainMapColorGamut iccGamut)
    {
        var wicProbe = new WicImageProbeResult(
            true,
            ".tif",
            64,
            64,
            24,
            "RGB",
            "WIC TIFF",
            IccColorGamut: iccGamut);
        var document = new HdrImageDocument(
            "wide.tif",
            "wide.tif",
            DecoderCatalog.Describe("wide.tif", wicImageProbe: wicProbe),
            WicImageProbe: wicProbe);

        Assert.True(ThumbnailDecodePolicy.RequiresPixelDecode(document));
    }

    [Fact]
    public async Task FileSignatureProbe_DetectsExtendedImageContainers()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "HdrSigTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var exr = Path.Combine(tempDir, "sample.bin");
            await File.WriteAllBytesAsync(exr, [0x76, 0x2F, 0x31, 0x01, 0x02, 0x00, 0x00, 0x00]);
            Assert.Equal(FileContainerKind.OpenExr, await FileSignatureProbe.DetectAsync(exr));

            var png = Path.Combine(tempDir, "png.bin");
            await File.WriteAllBytesAsync(png, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            Assert.Equal(FileContainerKind.Png, await FileSignatureProbe.DetectAsync(png));

            var tiff = Path.Combine(tempDir, "tiff.bin");
            await File.WriteAllBytesAsync(tiff, [0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00]);
            Assert.Equal(FileContainerKind.Tiff, await FileSignatureProbe.DetectAsync(tiff));

            var jxr = Path.Combine(tempDir, "jxr.bin");
            await File.WriteAllBytesAsync(jxr, [0x49, 0x49, 0xBC, 0x01, 0x20, 0x00, 0x00, 0x00]);
            Assert.Equal(FileContainerKind.JpegXr, await FileSignatureProbe.DetectAsync(jxr));

            var webp = Path.Combine(tempDir, "webp.bin");
            await File.WriteAllBytesAsync(webp, [(byte)'R', (byte)'I', (byte)'F', (byte)'F', 0x10, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P']);
            Assert.Equal(FileContainerKind.WebP, await FileSignatureProbe.DetectAsync(webp));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task FolderImageIndexCache_IgnoresExportStagingFilesAndSortsNumerically()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "HdrFolderIndexTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var img2 = Path.Combine(tempDir, "2.jpg");
            var img10 = Path.Combine(tempDir, "10.jpg");
            var staging = Path.Combine(tempDir, ".hdr-export-1234567890abcdef.jpg");
            await File.WriteAllBytesAsync(img10, [0xFF, 0xD8, 0xFF, 0xD9]);
            await File.WriteAllBytesAsync(img2, [0xFF, 0xD8, 0xFF, 0xD9]);
            await File.WriteAllBytesAsync(staging, [0xFF, 0xD8, 0xFF, 0xD9]);

            using var index = new FolderImageIndexCache();
            var (paths, currentIndex) = index.GetFolderImages(img10, CancellationToken.None);

            Assert.Equal(2, paths.Count);
            Assert.Equal(img2, paths[0]);
            Assert.Equal(img10, paths[1]);
            Assert.Equal(1, currentIndex);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task DirectoryMetadataCache_PreservesContainerKindAndSkipsRepeatedLargeFolderPruneWithinCooldown()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "HdrMetaLargeDirTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var files = new string[260];
            for (var i = 0; i < files.Length; i++)
            {
                var file = Path.Combine(tempDir, $"img_{i:D4}.png");
                files[i] = file;
                await File.WriteAllBytesAsync(file, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
                var doc = new HdrImageDocument(
                    file,
                    Path.GetFileName(file),
                    DecoderCatalog.Describe(file, containerKind: FileContainerKind.Png),
                    ContainerKind: FileContainerKind.Png);
                await DirectoryMetadataCache.StoreAsync(
                    new ImageLoadResult(doc, "EXIF", File.GetLastWriteTimeUtc(file)),
                    FileContainerKind.Png);
            }

            await DirectoryMetadataCache.FlushAsync();

            var loaded = await DirectoryMetadataCache.TryLoadAsync(files[0]);
            Assert.NotNull(loaded);
            Assert.Equal(FileContainerKind.Png, loaded.Document.ContainerKind);

            // Delete one file and trigger a second flush immediately; because all 260 entries were
            // verified moments ago, the second flush should not storm-stat all 256 entries again.
            File.Delete(files[259]);
            var docUpdate = new HdrImageDocument(
                files[0],
                Path.GetFileName(files[0]),
                DecoderCatalog.Describe(files[0], containerKind: FileContainerKind.Png),
                ContainerKind: FileContainerKind.Png);
            await DirectoryMetadataCache.StoreAsync(
                new ImageLoadResult(docUpdate, "EXIF-updated", File.GetLastWriteTimeUtc(files[0])),
                FileContainerKind.Png);
            await DirectoryMetadataCache.FlushAsync();

            var cachePath = DirectoryMetadataCache.GetCacheFilePath(tempDir);
            if (File.Exists(cachePath))
            {
                using var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(cachePath));
                Assert.True(json.RootElement.GetProperty("Entries").TryGetProperty("img_0259.png", out _));
            }
        }
        finally
        {
            await DirectoryMetadataCache.FlushAsync();
            var cachePath = DirectoryMetadataCache.GetCacheFilePath(tempDir);
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }

            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(DecodedBitmapPixelFormat.Rgba8Unorm)]
    [InlineData(DecodedBitmapPixelFormat.Rgba16Unorm)]
    [InlineData(DecodedBitmapPixelFormat.Rgba16Float)]
    public void BoxFilterIgnoresHiddenBlue(DecodedBitmapPixelFormat format)
    {
        var one = format == DecodedBitmapPixelFormat.Rgba16Float ? (ushort)0x3c00 : ushort.MaxValue;
        byte[] pixels = [255, 0, 0, 255, 0, 0, 255, 0];
        if (format != DecodedBitmapPixelFormat.Rgba8Unorm)
        {
            pixels = new byte[16];
            foreach (var offset in new[] { 0, 6, 12 })
                BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(offset), one);
        }

        var source = new DecodedBitmap(2, 1, pixels, true, PixelFormat: format);
        var result = BitmapPreviewResampler.Downscale(source, 1, CancellationToken.None).RgbaPixels;
        if (format == DecodedBitmapPixelFormat.Rgba8Unorm)
        {
            Assert.Equal(new byte[] { 255, 0, 0, 128 }, result);
            return;
        }

        Assert.Equal(one, ReadUInt16(result, 0));
        Assert.Equal(0, ReadUInt16(result, 4));
        Assert.Equal(format == DecodedBitmapPixelFormat.Rgba16Float ? 0x3800 : 32768, ReadUInt16(result, 6));
    }

    [Fact]
    public void IsoAlternateBt709SurvivesConstantConstruction()
    {
        var metadata = new IsoGainMapMetadata(Vector3.Zero, Vector3.One, Vector3.One,
            Vector3.Zero, Vector3.Zero, 0, 2, UseBaseColorSpace: false);
        var constants = metadata.CreateConstants(GainMapColorGamut.Bt2100, alternateColorGamut: GainMapColorGamut.Bt709);
        Assert.Equal(1f, constants.SourceEncoding.Z);
        Assert.Equal(HdrColorMath.ToShaderGamut(GainMapColorGamut.Bt709), constants.SourceEncoding.W);
        var green = new Vector3(0, 0.5f, 0);
        var base2020 = HdrColorMath.Bt709ToBt2020(green);
        var rendered = HdrColorMath.ReconstructGainMapHdrToBt709(base2020, Vector3.UnitY, constants, 1);
        Assert.True(Vector3.Distance(rendered, Vector3.UnitY) < Tolerance);
    }

    private static ushort ReadUInt16(byte[] buffer, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset, 2));
}
