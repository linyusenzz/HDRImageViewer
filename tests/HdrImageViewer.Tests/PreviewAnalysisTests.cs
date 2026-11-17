using System.Numerics;
using HdrImageViewer.Rendering;
using Xunit;

namespace HdrImageViewer.Tests;

public sealed class PreviewAnalysisTests
{
    [Fact]
    public async Task AnalysisPublishesCurrentPreviewAndPreservesPixelStatistics()
    {
        using var controller = new PreviewAnalysisController();
        var result = await controller.AnalyzeAsync(controller.Version,
            token => CreateSnapshot(controller.Version, token), CancellationToken.None);

        Assert.Same(result, controller.Snapshot);
        Assert.NotNull(result);
        Assert.Equal(1, result.Count);
        Assert.Equal(80, result.AverageNits, 3);
        Assert.Equal(1, result.ChromaticityCount);
        Assert.Equal(1, result.RedHistogram[128]);
    }

    [Fact]
    public async Task ChangedPreviewDiscardsResultEvenWhenWorkerFinishesAfterCancellation()
    {
        using var controller = new PreviewAnalysisController();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var version = controller.Version;
        var pending = controller.AnalyzeAsync(version, _ =>
        {
            started.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), CancellationToken.None));
            return CreateSnapshot(version, CancellationToken.None);
        }, CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            controller.Invalidate(clearSnapshot: true);
        }
        finally { release.Set(); }

        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(controller.Snapshot);
    }

    [Fact]
    public async Task NewRequestWinsWhenOlderWorkerIgnoresCancellation()
    {
        using var controller = new PreviewAnalysisController();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var version = controller.Version;
        var pending = controller.AnalyzeAsync(version, _ =>
        {
            started.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), CancellationToken.None));
            return CreateSnapshot(version, CancellationToken.None);
        }, CancellationToken.None);
        LuminanceSnapshot? latest;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            latest = await controller.AnalyzeAsync(version,
                token => CreateSnapshot(version, token), CancellationToken.None);
        }
        finally { release.Set(); }

        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.NotNull(latest);
        Assert.Same(latest, controller.Snapshot);
    }

    [Fact]
    public async Task CallerCancellationPropagatesWithoutPublishingPixels()
    {
        using var controller = new PreviewAnalysisController();
        using var source = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var version = controller.Version;
        var pending = controller.AnalyzeAsync(version, token =>
        {
            started.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), CancellationToken.None));
            return CreateSnapshot(version, token);
        }, source.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            source.Cancel();
        }
        finally { release.Set(); }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        Assert.Null(controller.Snapshot);
    }

    [Fact]
    public void LuminanceCalculationHonorsCancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => CreateSnapshot(0, source.Token));
    }

    [Fact]
    public void AlternatingComparisonModesReusesAnalysisUntilImageOrSettingsChange()
    {
        var cache = new ViewModeAnalysisCache<object>();
        var calculations = 0;
        object Calculate() { calculations++; return new object(); }
        var hdr = cache.GetOrCreate(GainmapViewMode.Adaptive, Calculate);
        var sdr = cache.GetOrCreate(GainmapViewMode.Sdr, Calculate);

        for (var divider = 0; divider <= 100; divider++)
        {
            Assert.Same(hdr, cache.GetOrCreate(GainmapViewMode.Adaptive, Calculate));
            Assert.Same(sdr, cache.GetOrCreate(GainmapViewMode.Sdr, Calculate));
        }
        Assert.Equal(2, calculations);

        cache.Clear();
        Assert.NotSame(hdr, cache.GetOrCreate(GainmapViewMode.Adaptive, Calculate));
        Assert.NotSame(sdr, cache.GetOrCreate(GainmapViewMode.Sdr, Calculate));
        Assert.Equal(4, calculations);
    }

    [Fact]
    public void ParallelLuminanceSnapshot_MatchesSequentialStatisticsAndHandlesLetterbox()
    {
        // 256 x 256 = 65,536 pixels (>= 16,384 parallel threshold; 512 KB > 256 KB retention cap).
        const int size = 256;
        var pixels = new byte[size * size * 8];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var pattern = (x + y) & 3;
                Vector3 rgb = pattern switch
                {
                    0 => new Vector3(1.0f, 1.0f, 1.0f),       // 80 nits D65 white
                    1 => new Vector3(4.0f, 2.0f, 0.5f),       // bright HDR warm highlight
                    2 => new Vector3(-0.08f, 1.15f, -0.02f),  // wide-gamut physical green
                    _ => new Vector3(0.001f, 0.001f, 0.001f), // dark pixel below 1 nit
                };
                WritePixelFp16(pixels, ((y * size) + x) * 8, rgb);
            }
        }

        var layout = new Vector4(0.5f, 0.5f, 0.25f, 0.25f);
        var snapshot = new LuminanceSnapshot(size, size, pixels, layout, version: 7, sdrWhiteNits: 80.0f, minimumChromaticityNits: 1.0f);

        // Exactly the central 128 x 128 = 16,384 pixels lie inside [0.25, 0.75] x [0.25, 0.75].
        Assert.Equal(128 * 128, snapshot.Count);
        Assert.Equal(128 * 128, snapshot.Histogram.Sum());
        Assert.Equal(128 * 128, snapshot.RedHistogram.Sum());
        Assert.Equal(128 * 128, snapshot.GreenHistogram.Sum());
        Assert.Equal(128 * 128, snapshot.BlueHistogram.Sum());
        Assert.Equal((128 * 128) / 4, snapshot.Chromaticity.DarkCount);
        Assert.Equal((128 * 128) * 3 / 4, snapshot.Chromaticity.PhysicalCount);
        Assert.Equal(0, snapshot.Chromaticity.NonPhysicalCount);
        Assert.True(snapshot.PeakNits > 180.0f);

        // Mutating the caller buffer after construction proves the >256 KB array was not retained directly.
        Array.Clear(pixels);
        Assert.Null(snapshot.Sample(0, 0));
        Assert.NotNull(snapshot.Sample(size / 2, size / 2));
    }

    [Fact]
    public void MultiStripLuminanceSnapshot_PreservesSinglePixelPeakAndMatchesSingleBuffer()
    {
        const int width = 1600;
        const int height = 1100;
        var fullPixels = new byte[width * height * 8];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                WritePixelFp16(fullPixels, ((y * width) + x) * 8, new Vector3(1.0f, 1.0f, 1.0f));
            }
        }

        // Single-pixel 4000-nit specular highlight (scRGB 50.0 -> 50 * 80 = 4000 nits) inside the fitted area.
        const int peakX = 913;
        const int peakY = 741;
        WritePixelFp16(fullPixels, ((peakY * width) + peakX) * 8, new Vector3(50.0f, 50.0f, 50.0f));

        const int stripHeight = 327;
        var strips = new List<AnalysisStrip>();
        for (var startY = 0; startY < height; startY += stripHeight)
        {
            var rows = Math.Min(stripHeight, height - startY);
            var stripBytes = new byte[(rows * width * 8) + 64];
            Buffer.BlockCopy(fullPixels, startY * width * 8, stripBytes, 0, rows * width * 8);
            strips.Add(new AnalysisStrip(startY, rows, stripBytes));
        }

        var layout = new Vector4(0.8f, 0.8f, 0.1f, 0.1f);
        var expected = new LuminanceSnapshot(
            width,
            height,
            fullPixels,
            layout,
            version: 11,
            sdrWhiteNits: 200.0f,
            minimumChromaticityNits: 1.0f);
        var stripped = new LuminanceSnapshot(
            width,
            height,
            strips,
            layout,
            version: 11,
            sdrWhiteNits: 200.0f,
            minimumChromaticityNits: 1.0f,
            retainSampleGrid: true,
            CancellationToken.None);

        Assert.Equal(4000.0f, stripped.PeakNits, 1);
        Assert.Equal(expected.Count, stripped.Count);
        Assert.Equal(expected.PeakNits, stripped.PeakNits);
        Assert.Equal(expected.AverageNits, stripped.AverageNits, 6);
        Assert.Equal(expected.Histogram, stripped.Histogram);
        Assert.Equal(expected.RedHistogram, stripped.RedHistogram);
        Assert.Equal(expected.GreenHistogram, stripped.GreenHistogram);
        Assert.Equal(expected.BlueHistogram, stripped.BlueHistogram);
        Assert.Equal(expected.Chromaticity.PhysicalCount, stripped.Chromaticity.PhysicalCount);
        Assert.Equal(expected.ChromaticityBins, stripped.ChromaticityBins);
    }

    [Fact]
    public void ChromaticityDiagram_SubGridClassifiesStandardPrimariesAndOutOfLocusPoints()
    {
        Assert.True(ChromaticityDiagram.IsPhysical(ChromaticityDiagram.WhitePoint));
        foreach (var primary in ChromaticityDiagram.Srgb)
        {
            Assert.True(ChromaticityDiagram.IsPhysical(primary));
        }

        foreach (var primary in ChromaticityDiagram.DisplayP3)
        {
            Assert.True(ChromaticityDiagram.IsPhysical(primary));
        }

        foreach (var primary in ChromaticityDiagram.Bt2020)
        {
            Assert.True(ChromaticityDiagram.IsPhysical(primary));
        }

        foreach (var locusVertex in ChromaticityDiagram.SpectralLocus)
        {
            Assert.True(ChromaticityDiagram.IsPhysical(locusVertex));
        }

        Assert.False(ChromaticityDiagram.IsPhysical(new Vector2(0.78f, 0.78f)));
        Assert.False(ChromaticityDiagram.IsPhysical(new Vector2(0.02f, 0.01f)));
        Assert.False(ChromaticityDiagram.IsPhysical(new Vector2(-0.05f, 0.3f)));
    }

    private static void WritePixelFp16(byte[] buffer, int offset, Vector3 rgb)
    {
        WriteHalf(buffer, offset, (Half)rgb.X);
        WriteHalf(buffer, offset + 2, (Half)rgb.Y);
        WriteHalf(buffer, offset + 4, (Half)rgb.Z);
        WriteHalf(buffer, offset + 6, (Half)1.0f);
    }

    private static void WriteHalf(byte[] buffer, int offset, Half value)
    {
        var bits = BitConverter.HalfToUInt16Bits(value);
        buffer[offset] = (byte)(bits & 0xFF);
        buffer[offset + 1] = (byte)(bits >> 8);
    }

    private static LuminanceSnapshot CreateSnapshot(long version, CancellationToken cancellationToken = default)
    {
        var pixels = new byte[] { 0, 0x3c, 0, 0x3c, 0, 0x3c, 0, 0x3c };
        return new LuminanceSnapshot(1, 1, pixels, new Vector4(1, 1, 0, 0), version,
            cancellationToken: cancellationToken);
    }
}
