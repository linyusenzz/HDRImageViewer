using System.Buffers.Binary;
using HdrImageViewer.Models;
using HdrImageViewer.Rendering;
using HdrImageViewer.Services;

namespace HdrImageViewer.IntegrationTests;

internal static class GpuPipelineRegressionChecks
{
    public static async Task RunAsync(string rootDir)
    {
        var testDir = Path.Combine(rootDir, "gpu-pipeline");
        Directory.CreateDirectory(testDir);

        await VerifyLinearLightMipDownscalingAsync(testDir);
        await VerifySmallAreaPeakAndFullFrameCaptureAsync(testDir);
    }

    private static async Task VerifyLinearLightMipDownscalingAsync(string testDir)
    {
        // 512x512 PQ HDR PNG checkerboard alternating 0 nits and 1000 nits (scRGB 12.5).
        // Non-linear PQ code-value averaging of PQ(0)=0 and PQ(1000)=0.7518 produces PQ^-1(0.3759) ~= 2.2 nits!
        // Linear-light mipmap generation averages 0 and 1000 nits to ~500 nits.
        const int size = 512;
        var exrBytes = new byte[size * size * 8];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var value = ((x + y) & 1) == 0 ? (Half)0.0f : (Half)12.5f;
                var off = ((y * size) + x) * 8;
                BinaryPrimitives.WriteUInt16LittleEndian(exrBytes.AsSpan(off), BitConverter.HalfToUInt16Bits(value));
                BinaryPrimitives.WriteUInt16LittleEndian(exrBytes.AsSpan(off + 2), BitConverter.HalfToUInt16Bits(value));
                BinaryPrimitives.WriteUInt16LittleEndian(exrBytes.AsSpan(off + 4), BitConverter.HalfToUInt16Bits(value));
                BinaryPrimitives.WriteUInt16LittleEndian(exrBytes.AsSpan(off + 6), BitConverter.HalfToUInt16Bits((Half)1.0f));
            }
        }

        var seedExr = Path.Combine(testDir, "pq-checker-seed.exr");
        var pqPng = Path.Combine(testDir, "pq-checker.png");
        NativeExrDecoder.Encode(seedExr, size, size, exrBytes);
        var seedDoc = await ImageDocumentLoader.LoadAsync(seedExr);
        await SingleLayerHdrExportService.ExportAsync(seedDoc.Document, pqPng, SingleLayerHdrExportTransfer.Pq);

        var loaded = await ImageDocumentLoader.LoadAsync(pqPng);
        using var pipeline = new D3D11HdrRenderPipeline
        {
            ViewMode = GainmapViewMode.AlternateImage,
            AdaptiveToneMappingEnabled = false,
            HeadroomMode = HdrHeadroomMode.SystemAdaptive,
            DisplayConfiguration = new HdrDisplayConfiguration(
                AdvancedColorKind: "HighDynamicRange",
                IsHighDynamicRange: true,
                IsHighDynamicRangeAvailable: true,
                SdrWhiteLevelInNits: 80.0,
                MaxLuminanceInNits: 4000.0,
                MaxAverageFullFrameLuminanceInNits: 4000.0,
                Details: "Test HDR 4000"),
        };

        // Render 512x512 PQ checkerboard into 128x128 preview (mip level 2, 4x4 texel box).
        await pipeline.LoadOffscreenForTestingAsync(loaded.Document, 128, 128);
        var snapshot = await pipeline.AnalyzeCurrentPreviewAsync();
        if (snapshot is null)
        {
            throw new InvalidOperationException($"GPU analysis failed: {pipeline.AnalysisError}");
        }

        if (snapshot.AverageNits < 420.0 || snapshot.AverageNits > 580.0)
        {
            throw new InvalidDataException(
                $"Expected linear-light downscaled PQ checkerboard average near 500 nits, but got {snapshot.AverageNits:F2} nits.");
        }

        Console.WriteLine($"PASS D3D11 linear-light mipmaps: 512->128 PQ checkerboard avg={snapshot.AverageNits:F1} nits");
    }

    private static async Task VerifySmallAreaPeakAndFullFrameCaptureAsync(string testDir)
    {
        // 1600x1200 HDR image (>1024 cap) with a single 1-pixel 3200-nit highlight (scRGB 40.0)
        // on an 80-nit background (scRGB 1.0), rendered at 1:1 1600x1200.
        const int width = 1600;
        const int height = 1200;
        const int peakX = 1117;
        const int peakY = 823;
        var exrBytes = new byte[width * height * 8];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = (x == peakX && y == peakY) ? (Half)40.0f : (Half)1.0f;
                var off = ((y * width) + x) * 8;
                BinaryPrimitives.WriteUInt16LittleEndian(exrBytes.AsSpan(off), BitConverter.HalfToUInt16Bits(value));
                BinaryPrimitives.WriteUInt16LittleEndian(exrBytes.AsSpan(off + 2), BitConverter.HalfToUInt16Bits(value));
                BinaryPrimitives.WriteUInt16LittleEndian(exrBytes.AsSpan(off + 4), BitConverter.HalfToUInt16Bits(value));
                BinaryPrimitives.WriteUInt16LittleEndian(exrBytes.AsSpan(off + 6), BitConverter.HalfToUInt16Bits((Half)1.0f));
            }
        }

        var exrPath = Path.Combine(testDir, "single-pixel-peak.exr");
        NativeExrDecoder.Encode(exrPath, width, height, exrBytes);
        var loaded = await ImageDocumentLoader.LoadAsync(exrPath);

        using var pipeline = new D3D11HdrRenderPipeline
        {
            ViewMode = GainmapViewMode.AlternateImage,
            AdaptiveToneMappingEnabled = false,
            HeadroomMode = HdrHeadroomMode.SystemAdaptive,
            DisplayConfiguration = new HdrDisplayConfiguration(
                AdvancedColorKind: "HighDynamicRange",
                IsHighDynamicRange: true,
                IsHighDynamicRangeAvailable: true,
                SdrWhiteLevelInNits: 80.0,
                MaxLuminanceInNits: 4000.0,
                MaxAverageFullFrameLuminanceInNits: 4000.0,
                Details: "Test HDR 4000"),
        };

        await pipeline.LoadOffscreenForTestingAsync(loaded.Document, width, height);
        var snapshot = await pipeline.AnalyzeCurrentPreviewAsync();
        if (snapshot is null)
        {
            throw new InvalidOperationException($"GPU peak analysis failed: {pipeline.AnalysisError}");
        }

        if (snapshot.Width != width || snapshot.Height != height)
        {
            throw new InvalidDataException($"Expected 1:1 analysis dimensions {width}x{height}, got {snapshot.Width}x{snapshot.Height}.");
        }

        if (snapshot.PeakNits < 3100.0f || snapshot.PeakNits > 3300.0f)
        {
            throw new InvalidDataException(
                $"Expected 1-pixel 3200-nit peak to be preserved in 1600x1200 preview, got {snapshot.PeakNits:F1} nits.");
        }

        using var capturedFrame = await pipeline.CaptureRenderedFrameAsync();
        if (capturedFrame is null || capturedFrame.Width != width || capturedFrame.Height != height)
        {
            throw new InvalidDataException("CaptureRenderedFrameAsync did not return the full 1600x1200 rendered frame.");
        }

        var strip = capturedFrame.Strips.Single(s => peakY >= s.StartY && peakY < s.StartY + s.RowCount);
        var localY = peakY - strip.StartY;
        var pixelOffset = ((localY * width) + peakX) * 8;
        var capturedR = (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(strip.Pixels.AsSpan(pixelOffset, 2)));
        var neighborR = (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(strip.Pixels.AsSpan(pixelOffset - 8, 2)));
        if (capturedR < 39.0f || neighborR > 1.1f)
        {
            throw new InvalidDataException(
                $"Full-frame capture did not preserve crisp 1-pixel detail: peakR={capturedR:F2}, neighborR={neighborR:F2}.");
        }

        Console.WriteLine($"PASS D3D11 1:1 strip analysis + full-frame capture: peak={snapshot.PeakNits:F1} nits, pixel=({peakX},{peakY}) scRGB={capturedR:F1}");
    }
}
