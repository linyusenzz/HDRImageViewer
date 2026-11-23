using System.Buffers.Binary;
using System.Numerics;
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

        // Transparent, just below 50 nits, and exactly on the inclusive 50-nit boundary.
        for (var x = 0; x < 48; x++)
        {
            var offset = x * 8;
            var level = x < 16 ? (Half)1 : x < 32 ? BitConverter.UInt16BitsToHalf(0x38ff) : (Half)0.625f;
            for (var channel = 0; channel < 3; channel++)
                BinaryPrimitives.WriteUInt16LittleEndian(exrBytes.AsSpan(offset + channel * 2), BitConverter.HalfToUInt16Bits(level));
            if (x < 16) BinaryPrimitives.WriteUInt16LittleEndian(exrBytes.AsSpan(offset + 6), 0);
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

        if (!pipeline.TrySampleViewportPixel(peakX, peakY, out _, out var probeNits, out _, out _)
            || Math.Abs(probeNits - snapshot.PeakNits) > 1)
        {
            throw new InvalidDataException($"Pixel probe missed the single-pixel peak: {probeNits} vs {snapshot.PeakNits} nits.");
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

        pipeline.LuminanceHeatmapEnabled = true;
        using var heatmap = await CapturePresentationAsync(pipeline);
        AssertBand(heatmap, peakX, peakY, minimumNits: 2000);
        AssertBand(heatmap, peakX - 1, peakY, minimumNits: 50);
        VerifyCleanProbe(pipeline, peakX, peakY, peakX, peakY, probeNits);
        for (var x = 2; x < 46; x++)
        {
            var expectedBand = x < 16 ? 0 : x < 32 ? 10 : 50;
            Require(pipeline.TrySampleViewportPixel(x, 0, out _, out var nits, out _, out _)
                && BandMinimumFor(nits) == expectedBand, $"Unexpected threshold probe at {x}: {nits} nits.");
            AssertBand(heatmap, x, 0, expectedBand);
        }

        pipeline.ClearAnalysis();
        var withHeatmap = await pipeline.AnalyzeCurrentPreviewAsync();
        Require(withHeatmap is not null
            && snapshot.PeakNits == withHeatmap.PeakNits
            && snapshot.AverageNits == withHeatmap.AverageNits
            && snapshot.Histogram.SequenceEqual(withHeatmap.Histogram),
            "Heatmap changed the clean analysis snapshot.");
        using var capturedWhileOn = await CaptureCleanAsync(pipeline);
        AssertSameFrame(capturedFrame, capturedWhileOn);

        pipeline.LuminanceHeatmapEnabled = false;
        using var heatmapOff = await CapturePresentationAsync(pipeline);
        AssertSameFrame(capturedFrame, heatmapOff);

        pipeline.LuminanceHeatmapEnabled = true;
        pipeline.ComparisonEnabled = true;
        pipeline.ComparisonPosition = 0.75f;
        using var splitClean = await CaptureCleanAsync(pipeline);
        using var splitPresentation = await CapturePresentationAsync(pipeline);
        AssertSameFrame(splitClean, splitPresentation);
        pipeline.ComparisonEnabled = false;
        using var resumed = await CapturePresentationAsync(pipeline);
        AssertBand(resumed, peakX, peakY, minimumNits: 2000);

        pipeline.ViewMode = GainmapViewMode.Sdr;
        using var sdr = await CaptureCleanAsync(pipeline);
        using var sdrHeatmap = await CapturePresentationAsync(pipeline);
        var sdrPeak = LuminanceSnapshot.ToNits(ReadPixel(sdr, peakX, peakY));
        Require(sdrPeak < 203.0f, $"SDR mode did not remap the HDR highlight: {sdrPeak:F2} nits.");
        AssertBand(sdrHeatmap, peakX, peakY, BandMinimumFor(sdrPeak));
        VerifyCleanProbe(pipeline, peakX, peakY, peakX, peakY, sdrPeak);

        pipeline.ViewMode = GainmapViewMode.AlternateImage;
        const int panX = 152, panY = 75, panWidth = 1200, panHeight = 900;
        await pipeline.LoadOffscreenForTestingAsync(loaded.Document, panWidth, panHeight, viewport:
            new HdrRenderViewport(panWidth, panHeight, width / (float)panWidth, height / (float)panHeight,
                -panX / (float)panWidth, -panY / (float)panHeight));
        using var panned = await CapturePresentationAsync(pipeline);
        AssertBand(panned, peakX - panX, peakY - panY, minimumNits: 2000);
        AssertBand(panned, peakX, peakY, minimumNits: 50);
        VerifyCleanProbe(pipeline, peakX - panX, peakY - panY, peakX, peakY, probeNits);

        // Zoom to 50% within the same viewport. The rendered pixel may no longer be
        // 3200 nits because the preview renderer filters source pixels at reduced scale.
        const int zoomLeft = 100, zoomTop = 150;
        await pipeline.LoadOffscreenForTestingAsync(loaded.Document, panWidth, panHeight, viewport:
            new HdrRenderViewport(panWidth, panHeight, width * 0.5f / panWidth, height * 0.5f / panHeight,
                zoomLeft / (float)panWidth, zoomTop / (float)panHeight));
        using var zoomedClean = await CaptureCleanAsync(pipeline);
        using var zoomed = await CapturePresentationAsync(pipeline);
        var zoomX = zoomLeft + (peakX / 2);
        var zoomY = zoomTop + (peakY / 2);
        var zoomedPeak = LuminanceSnapshot.ToNits(ReadPixel(zoomedClean, zoomX, zoomY));
        AssertBand(zoomed, zoomX, zoomY, BandMinimumFor(zoomedPeak));
        Require(ReadPixel(zoomed, 0, 0) == Vector3.Zero, "Heatmap painted outside the image.");

        await VerifyHeatmapPresentationStateAsync(loaded.Document);

        Console.WriteLine($"PASS D3D11 heatmap: 1:1 1px {probeNits:F2} nits vs 80-nit neighbor; SDR highlight {sdrPeak:F2}; 50% highlight {zoomedPeak:F2}; pan alignment, letterbox, split suspension/resume, uncolored analysis/probe/capture, and byte-identical off");

        Console.WriteLine($"PASS D3D11 1:1 strip analysis + full-frame capture: peak={snapshot.PeakNits:F1} nits, pixel=({peakX},{peakY}) scRGB={capturedR:F1}");
    }

    private static async Task VerifyHeatmapPresentationStateAsync(HdrImageDocument document)
    {
        // An invalid DXBC header makes the real driver reject only the first
        // heatmap. The ordinary frame and the device remain usable on retry.
        using var validShader = typeof(D3D11HdrRenderPipeline).Assembly
            .GetManifestResourceStream("HdrImageViewer.Rendering.Shaders.Heatmap.PS.cso")!;
        using var shader = new MemoryStream();
        validShader.CopyTo(shader);
        var firstAttempt = true;
        using var presenter = new D3D11HdrRenderPipeline(() =>
        {
            if (!firstAttempt) return shader.ToArray();
            firstAttempt = false;
            return new byte[32];
        });
        await presenter.LoadOffscreenForTestingAsync(document, 160, 120, createPresentationSurface: true);
        var analysis = await presenter.AnalyzeCurrentPreviewAsync() ?? throw new InvalidDataException("No preview analysis.");
        var states = new List<HeatmapPresentation>();
        presenter.HeatmapPresentationChanged += () => states.Add(presenter.HeatmapState);
        await presenter.SetLuminanceHeatmapAsync(true, CancellationToken.None);
        Require(presenter.HeatmapState == HeatmapPresentation.Unavailable, "A rejected heatmap must hide the legend.");
        await presenter.SetLuminanceHeatmapAsync(false, CancellationToken.None);
        await presenter.SetLuminanceHeatmapAsync(true, CancellationToken.None);
        Require(presenter.HeatmapState == HeatmapPresentation.Visible, $"Heatmap presentation state: {presenter.HeatmapState}.");
        await presenter.SetLuminanceHeatmapAsync(false, CancellationToken.None);
        Require(ReferenceEquals(analysis, presenter.AnalysisSnapshot) && analysis.Version == presenter.PreviewVersion,
            "Toggling the heatmap invalidated the current analysis.");
        Require(states.SequenceEqual(new[] { HeatmapPresentation.Unavailable, HeatmapPresentation.Inactive, HeatmapPresentation.Visible, HeatmapPresentation.Inactive }),
            "The legend did not receive the failure, retry and reset state changes.");
    }

    internal static async Task VerifyGainMapHeatmapAsync(HdrImageDocument document)
    {
        using var pipeline = new D3D11HdrRenderPipeline { ViewMode = GainmapViewMode.AlternateImage };
        await pipeline.LoadOffscreenForTestingAsync(document, 256, 128);
        await pipeline.SetLuminanceHeatmapAsync(true, CancellationToken.None);
        using var heatmap = await CapturePresentationAsync(pipeline);
        var bands = new HashSet<int>();
        for (var y = 8; y < 128; y += 16)
        {
            for (var x = 8; x < 256; x += 16)
            {
                if (!pipeline.TrySampleViewportPixel(x, y, out _, out var nits, out _, out _)) continue;
                var band = BandMinimumFor(nits);
                bands.Add(band);
                AssertBand(heatmap, x, y, band);
            }
        }

        Require(bands.Count >= 2, "The gain-map fixture must cover multiple luminance bands.");
        Console.WriteLine($"PASS Ultra HDR heatmap: {bands.Count} luminance bands match the pixel probe.");
    }

    private static async Task<D3D11HdrRenderPipeline.RenderedFrameCapture> CapturePresentationAsync(D3D11HdrRenderPipeline pipeline) =>
        await pipeline.CapturePresentedHeatmapForTestingAsync() ?? throw new InvalidDataException("Heatmap presentation capture failed.");

    private static async Task<D3D11HdrRenderPipeline.RenderedFrameCapture> CaptureCleanAsync(D3D11HdrRenderPipeline pipeline) =>
        await pipeline.CaptureRenderedFrameAsync() ?? throw new InvalidDataException("Clean preview capture failed.");

    private static Vector3 ReadPixel(D3D11HdrRenderPipeline.RenderedFrameCapture capture, int x, int y)
    {
        var strip = capture.Strips.Single(s => y >= s.StartY && y < s.StartY + s.RowCount);
        var offset = ((((y - strip.StartY) * capture.Width) + x) * 8);
        static float ReadHalf(byte[] bytes, int at) => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at, 2)));
        return new Vector3(ReadHalf(strip.Pixels, offset), ReadHalf(strip.Pixels, offset + 2), ReadHalf(strip.Pixels, offset + 4));
    }

    private static int BandMinimumFor(float nits)
    {
        var minimum = 0;
        foreach (var band in LuminanceHeatmap.Bands)
        {
            if (nits >= band.MinimumNits)
            {
                minimum = band.MinimumNits;
            }
        }
        return minimum;
    }

    private static void AssertBand(D3D11HdrRenderPipeline.RenderedFrameCapture capture, int x, int y, int minimumNits)
    {
        foreach (var band in LuminanceHeatmap.Bands)
        {
            if (band.MinimumNits != minimumNits)
            {
                continue;
            }

            var constants = band.ToShaderBand(1.0f);
            var expected = new Vector3(constants.X, constants.Y, constants.Z);
            var actual = ReadPixel(capture, x, y);
            Require(Vector3.Distance(actual, expected) < 0.001f,
                $"Heatmap color at ({x},{y}) was {actual}; expected {expected} for the {minimumNits}+ band.");
            return;
        }

        throw new InvalidDataException($"Unknown heatmap band: {minimumNits}.");
    }

    private static void VerifyCleanProbe(D3D11HdrRenderPipeline pipeline, int x, int y, int expectedX, int expectedY, float expectedNits)
    {
        Require(pipeline.TrySampleViewportPixel(x, y, out _, out var nits, out var imageX, out var imageY)
            && imageX == expectedX && imageY == expectedY && Math.Abs(nits - expectedNits) <= 1.0f,
            $"Clean pixel probe at ({x},{y}) reported ({imageX},{imageY}) {nits:F2} nits; expected ({expectedX},{expectedY}) {expectedNits:F2} nits.");
    }

    private static void AssertSameFrame(D3D11HdrRenderPipeline.RenderedFrameCapture expected,
        D3D11HdrRenderPipeline.RenderedFrameCapture actual)
    {
        Require(expected.Width == actual.Width && expected.Height == actual.Height
            && expected.Strips.Count == actual.Strips.Count, "Frame dimensions did not match.");
        for (var i = 0; i < expected.Strips.Count; i++)
        {
            var first = expected.Strips[i];
            var second = actual.Strips[i];
            var bytes = first.RowCount * expected.Width * 8;
            Require(first.StartY == second.StartY && first.RowCount == second.RowCount
                && first.Pixels.AsSpan(0, bytes).SequenceEqual(second.Pixels.AsSpan(0, bytes)),
                $"Expected identical preview bytes in strip {i}.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}
