using System.Buffers;
using System.Numerics;

namespace HdrImageViewer.Rendering;

internal readonly record struct AnalysisStrip(int StartY, int RowCount, byte[] Pixels);

public sealed class LuminanceSnapshot
{
    private const int MaxRetainedPixelBytes = 256 * 1024;
    private const int DownsampledSampleGridMaxDimension = 96;
    private const int ParallelPixelThreshold = 16_384;
    private static readonly float HistogramLogScale = 64.0f / MathF.Log2(10001.0f);
    private static readonly float[] HalfBitsToFloat = CreateHalfBitsToFloatTable();
    private static volatile CachedChannelLut? s_cachedChannelLut;

    private sealed record CachedChannelLut(float Scale, byte[] Bins);

    private static float[] CreateHalfBitsToFloatTable()
    {
        var table = new float[65536];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = (float)BitConverter.UInt16BitsToHalf((ushort)i);
        }

        return table;
    }

    private static byte[] GetOrCreateChannelBinLut(float scaleToWhite)
    {
        var cached = s_cachedChannelLut;
        if (cached is not null && MathF.Abs(cached.Scale - scaleToWhite) <= 1e-6f)
        {
            return cached.Bins;
        }

        var bins = new byte[65536];
        for (var i = 0; i < bins.Length; i++)
        {
            var v = HalfBitsToFloat[i];
            bins[i] = float.IsFinite(v) ? (byte)ChannelBinFast(v, scaleToWhite) : (byte)0;
        }

        var next = new CachedChannelLut(scaleToWhite, bins);
        s_cachedChannelLut = next;
        return bins;
    }

    private readonly byte[]? _samplePixels;
    private readonly int _sampleWidth;
    private readonly int _sampleHeight;
    private readonly Vector4 _layout;

    public int Width { get; }
    public int Height { get; }
    public long Version { get; }
    public int[] Histogram { get; } = new int[64];
    public const int ChannelBinCount = 256;
    public int[] RedHistogram { get; } = new int[ChannelBinCount];
    public int[] GreenHistogram { get; } = new int[ChannelBinCount];
    public int[] BlueHistogram { get; } = new int[ChannelBinCount];
    public float SdrWhiteNits { get; }
    public ChromaticityDistribution Chromaticity { get; }
    public int[] ChromaticityBins => Chromaticity.PhysicalBins;
    public int ChromaticityCount => Chromaticity.PhysicalCount;
    public GainMapChromaticityComparison? GainMapComparison { get; init; }
    public double AverageNits { get; }
    public float PeakNits { get; }
    public int Count { get; }

    public LuminanceSnapshot(int width, int height, byte[] pixels, Vector4 layout, long version,
        float sdrWhiteNits = 80, float minimumChromaticityNits = 0, CancellationToken cancellationToken = default)
        : this(
            width,
            height,
            pixels ?? throw new ArgumentNullException(nameof(pixels)),
            pixels.Length,
            exactArrayLengthRequired: true,
            allowDirectBufferRetention: true,
            retainSampleGrid: true,
            layout,
            version,
            sdrWhiteNits,
            minimumChromaticityNits,
            cancellationToken)
    {
    }

    internal LuminanceSnapshot(
        int width,
        int height,
        byte[] pixels,
        int pixelByteLength,
        Vector4 layout,
        long version,
        float sdrWhiteNits,
        float minimumChromaticityNits,
        bool retainSampleGrid,
        CancellationToken cancellationToken)
        : this(
            width,
            height,
            pixels ?? throw new ArgumentNullException(nameof(pixels)),
            pixelByteLength,
            exactArrayLengthRequired: false,
            allowDirectBufferRetention: false,
            retainSampleGrid,
            layout,
            version,
            sdrWhiteNits,
            minimumChromaticityNits,
            cancellationToken)
    {
    }

    internal LuminanceSnapshot(
        int width,
        int height,
        IReadOnlyList<AnalysisStrip> strips,
        Vector4 layout,
        long version,
        float sdrWhiteNits,
        float minimumChromaticityNits,
        bool retainSampleGrid,
        CancellationToken cancellationToken)
        : this(
            ValidateStrips(width, height, strips),
            width,
            height,
            allowDirectBufferRetention: false,
            retainSampleGrid,
            layout,
            version,
            sdrWhiteNits,
            minimumChromaticityNits,
            cancellationToken)
    {
    }

    private LuminanceSnapshot(
        int width,
        int height,
        byte[] pixels,
        int pixelByteLength,
        bool exactArrayLengthRequired,
        bool allowDirectBufferRetention,
        bool retainSampleGrid,
        Vector4 layout,
        long version,
        float sdrWhiteNits,
        float minimumChromaticityNits,
        CancellationToken cancellationToken)
        : this(
            ValidateSingleBuffer(width, height, pixels, pixelByteLength, exactArrayLengthRequired),
            width,
            height,
            allowDirectBufferRetention,
            retainSampleGrid,
            layout,
            version,
            sdrWhiteNits,
            minimumChromaticityNits,
            cancellationToken)
    {
    }

    private LuminanceSnapshot(
        IReadOnlyList<AnalysisStrip> strips,
        int width,
        int height,
        bool allowDirectBufferRetention,
        bool retainSampleGrid,
        Vector4 layout,
        long version,
        float sdrWhiteNits,
        float minimumChromaticityNits,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requiredBytes = checked((long)width * height * 8);

        Width = width;
        Height = height;
        _layout = layout;
        Version = version;
        SdrWhiteNits = float.IsFinite(sdrWhiteNits) && sdrWhiteNits > 0 ? sdrWhiteNits : 80;
        Chromaticity = new ChromaticityDistribution(minimumChromaticityNits);

        if (retainSampleGrid)
        {
            if (allowDirectBufferRetention && strips.Count == 1 && requiredBytes <= MaxRetainedPixelBytes)
            {
                _samplePixels = strips[0].Pixels;
                _sampleWidth = width;
                _sampleHeight = height;
            }
            else if (requiredBytes <= DownsampledSampleGridMaxDimension * DownsampledSampleGridMaxDimension * 8)
            {
                var exactBytes = checked((int)requiredBytes);
                var copy = new byte[exactBytes];
                var rowStride = width * 8;
                foreach (var strip in strips)
                {
                    Buffer.BlockCopy(
                        strip.Pixels,
                        0,
                        copy,
                        strip.StartY * rowStride,
                        strip.RowCount * rowStride);
                }

                _samplePixels = copy;
                _sampleWidth = width;
                _sampleHeight = height;
            }
            else
            {
                (_samplePixels, _sampleWidth, _sampleHeight) = CreateDownsampledSampleGrid(strips, width, height);
            }
        }

        if (_layout.X <= 0.0f || _layout.Y <= 0.0f)
        {
            AverageNits = 0.0;
            PeakNits = 0.0f;
            Count = 0;
            return;
        }

        var channelScaleToWhite = 80.0f / SdrWhiteNits;
        var minChromaticityNits = Chromaticity.MinimumNits;
        var channelLut = GetOrCreateChannelBinLut(channelScaleToWhite);
        var locusLookup = ChromaticityDiagram.GetLocusLookup();
        var (startX, endX) = ComputeVisibleColumnRange(width, _layout);
        if (startX > endX)
        {
            AverageNits = 0.0;
            PeakNits = 0.0f;
            Count = 0;
            return;
        }

        if ((long)width * height >= ParallelPixelThreshold && height >= 4)
        {
            var mergeLock = new object();
            double totalSum = 0.0;
            var totalPeak = 0.0f;
            var totalCount = 0;
            var options = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8),
            };

            Parallel.For(
                0,
                height,
                options,
                static () => new ThreadAccumulator(),
                (y, _, local) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var strip = LocateStrip(strips, y);
                    ProcessRow(
                        strip.Pixels,
                        y - strip.StartY,
                        width,
                        height,
                        y,
                        startX,
                        endX,
                        _layout,
                        channelLut,
                        locusLookup,
                        minChromaticityNits,
                        local.Histogram,
                        local.RedHistogram,
                        local.GreenHistogram,
                        local.BlueHistogram,
                        local.PhysicalBins,
                        local.NonPhysicalBins,
                        ref local.PhysicalCount,
                        ref local.NonPhysicalCount,
                        ref local.NonPhysicalOutsidePlotCount,
                        ref local.DarkCount,
                        ref local.InvalidCount,
                        ref local.Sum,
                        ref local.PeakNits,
                        ref local.Count);
                    return local;
                },
                local =>
                {
                    try
                    {
                        if (local.Count == 0
                            && local.PhysicalCount == 0
                            && local.NonPhysicalCount == 0
                            && local.DarkCount == 0
                            && local.InvalidCount == 0)
                        {
                            return;
                        }

                        lock (mergeLock)
                        {
                            for (var i = 0; i < 64; i++)
                            {
                                Histogram[i] += local.Histogram[i];
                            }

                            for (var i = 0; i < ChannelBinCount; i++)
                            {
                                RedHistogram[i] += local.RedHistogram[i];
                                GreenHistogram[i] += local.GreenHistogram[i];
                                BlueHistogram[i] += local.BlueHistogram[i];
                            }

                            Chromaticity.Merge(
                                local.PhysicalBins.AsSpan(0, ChromaticityDistribution.TotalBinCount),
                                local.NonPhysicalBins.AsSpan(0, ChromaticityDistribution.TotalBinCount),
                                local.PhysicalCount,
                                local.NonPhysicalCount,
                                local.NonPhysicalOutsidePlotCount,
                                local.DarkCount,
                                local.InvalidCount);

                            totalSum += local.Sum;
                            totalPeak = Math.Max(totalPeak, local.PeakNits);
                            totalCount += local.Count;
                        }
                    }
                    finally
                    {
                        local.ReturnBuffers();
                    }
                });

            PeakNits = totalPeak;
            Count = totalCount;
            AverageNits = totalCount == 0 ? 0.0 : totalSum / totalCount;
        }
        else
        {
            double sum = 0.0;
            var peakNits = 0.0f;
            var count = 0;
            var physicalCount = 0;
            var nonPhysicalCount = 0;
            var nonPhysicalOutsidePlotCount = 0;
            var darkCount = 0;
            var invalidCount = 0;

            foreach (var strip in strips)
            {
                for (var localY = 0; localY < strip.RowCount; localY++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ProcessRow(
                        strip.Pixels,
                        localY,
                        width,
                        height,
                        strip.StartY + localY,
                        startX,
                        endX,
                        _layout,
                        channelLut,
                        locusLookup,
                        minChromaticityNits,
                        Histogram,
                        RedHistogram,
                        GreenHistogram,
                        BlueHistogram,
                        Chromaticity.PhysicalBins,
                        Chromaticity.NonPhysicalBins,
                        ref physicalCount,
                        ref nonPhysicalCount,
                        ref nonPhysicalOutsidePlotCount,
                        ref darkCount,
                        ref invalidCount,
                        ref sum,
                        ref peakNits,
                        ref count);
                }
            }

            if (physicalCount != 0 || nonPhysicalCount != 0 || darkCount != 0 || invalidCount != 0)
            {
                Chromaticity.AddCounts(
                    physicalCount,
                    nonPhysicalCount,
                    nonPhysicalOutsidePlotCount,
                    darkCount,
                    invalidCount);
            }

            PeakNits = peakNits;
            Count = count;
            AverageNits = count == 0 ? 0.0 : sum / count;
        }
    }

    private static IReadOnlyList<AnalysisStrip> ValidateSingleBuffer(
        int width,
        int height,
        byte[] pixels,
        int pixelByteLength,
        bool exactArrayLengthRequired)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("Invalid FP16 preview dimensions.");
        }

        var requiredBytes = checked(width * height * 8);
        if (exactArrayLengthRequired
            ? pixelByteLength != requiredBytes
            : pixelByteLength < requiredBytes || pixels.Length < requiredBytes)
        {
            throw new ArgumentException("Invalid FP16 preview dimensions.");
        }

        return new[] { new AnalysisStrip(0, height, pixels) };
    }

    private static IReadOnlyList<AnalysisStrip> ValidateStrips(
        int width,
        int height,
        IReadOnlyList<AnalysisStrip> strips)
    {
        ArgumentNullException.ThrowIfNull(strips);
        if (width <= 0 || height <= 0 || strips.Count == 0)
        {
            throw new ArgumentException("Invalid FP16 preview dimensions.");
        }

        var expectedStartY = 0;
        for (var i = 0; i < strips.Count; i++)
        {
            var strip = strips[i];
            if (strip.Pixels is null
                || strip.StartY != expectedStartY
                || strip.RowCount <= 0
                || strip.Pixels.Length < checked(strip.RowCount * width * 8))
            {
                throw new ArgumentException("Invalid FP16 preview strip dimensions.");
            }

            expectedStartY = checked(expectedStartY + strip.RowCount);
        }

        if (expectedStartY != height)
        {
            throw new ArgumentException("Invalid FP16 preview strip dimensions.");
        }

        return strips;
    }

    private static AnalysisStrip LocateStrip(IReadOnlyList<AnalysisStrip> strips, int globalY)
    {
        if (strips.Count == 1)
        {
            return strips[0];
        }

        var firstRows = strips[0].RowCount;
        var index = Math.Clamp(globalY / firstRows, 0, strips.Count - 1);
        while (index > 0 && globalY < strips[index].StartY)
        {
            index--;
        }

        while (index < strips.Count - 1 && globalY >= strips[index].StartY + strips[index].RowCount)
        {
            index++;
        }

        return strips[index];
    }

    private static (int StartX, int EndX) ComputeVisibleColumnRange(int width, Vector4 layout)
    {
        var invWidth = 1.0f / width;
        var invLayoutX = 1.0f / layout.X;
        var minX = Math.Clamp((int)MathF.Floor((layout.Z * width) - 1.5f), 0, width - 1);
        var maxX = Math.Clamp((int)MathF.Ceiling(((layout.Z + layout.X) * width) + 1.5f), 0, width - 1);

        while (minX <= maxX)
        {
            var u = (((minX + 0.5f) * invWidth) - layout.Z) * invLayoutX;
            if (u >= 0.0f)
            {
                break;
            }

            minX++;
        }

        while (maxX >= minX)
        {
            var u = (((maxX + 0.5f) * invWidth) - layout.Z) * invLayoutX;
            if (u <= 1.0f)
            {
                break;
            }

            maxX--;
        }

        return (minX, maxX);
    }

    private static void ProcessRow(
        byte[] pixels,
        int localY,
        int width,
        int height,
        int globalY,
        int startX,
        int endX,
        Vector4 layout,
        byte[] channelLut,
        ChromaticityDiagram.LocusLookupTable locusLookup,
        float minChromaticityNits,
        int[] histogram,
        int[] redHistogram,
        int[] greenHistogram,
        int[] blueHistogram,
        int[] physicalBins,
        int[] nonPhysicalBins,
        ref int physicalCount,
        ref int nonPhysicalCount,
        ref int nonPhysicalOutsidePlotCount,
        ref int darkCount,
        ref int invalidCount,
        ref double sum,
        ref float peakNits,
        ref int count)
    {
        var v = (((globalY + 0.5f) / height) - layout.W) / layout.Y;
        if (v < 0.0f || v > 1.0f)
        {
            return;
        }

        var rowOffset = localY * width * 8;
        for (var x = startX; x <= endX; x++)
        {
            var offset = rowOffset + (x * 8);
            var rBits = (ushort)(pixels[offset] | (pixels[offset + 1] << 8));
            var gBits = (ushort)(pixels[offset + 2] | (pixels[offset + 3] << 8));
            var bBits = (ushort)(pixels[offset + 4] | (pixels[offset + 5] << 8));
            if ((rBits & 0x7C00) == 0x7C00 || (gBits & 0x7C00) == 0x7C00 || (bBits & 0x7C00) == 0x7C00)
            {
                continue;
            }

            var r = HalfBitsToFloat[rBits];
            var g = HalfBitsToFloat[gBits];
            var b = HalfBitsToFloat[bBits];
            var sample = new Vector3(r, g, b);
            var nits = Math.Max(0.0f, ((0.2126f * r) + (0.7152f * g) + (0.0722f * b)) * 80.0f);
            histogram[HistogramBinFast(nits)]++;
            redHistogram[channelLut[rBits]]++;
            greenHistogram[channelLut[gBits]]++;
            blueHistogram[channelLut[bBits]]++;
            ChromaticityDistribution.AccumulateFiniteSample(
                sample,
                nits,
                minChromaticityNits,
                locusLookup,
                physicalBins,
                nonPhysicalBins,
                ref physicalCount,
                ref nonPhysicalCount,
                ref nonPhysicalOutsidePlotCount,
                ref darkCount,
                ref invalidCount);
            sum += nits;
            if (nits > peakNits)
            {
                peakNits = nits;
            }

            count++;
        }
    }

    private static (byte[] Pixels, int Width, int Height) CreateDownsampledSampleGrid(
        IReadOnlyList<AnalysisStrip> strips,
        int width,
        int height)
    {
        var maxDim = Math.Max(width, height);
        var scale = (double)DownsampledSampleGridMaxDimension / maxDim;
        var gridW = Math.Clamp((int)Math.Round(width * scale), 1, DownsampledSampleGridMaxDimension);
        var gridH = Math.Clamp((int)Math.Round(height * scale), 1, DownsampledSampleGridMaxDimension);
        var grid = new byte[gridW * gridH * 8];

        for (var gy = 0; gy < gridH; gy++)
        {
            var sy = Math.Clamp((int)(((gy + 0.5) * height) / gridH), 0, height - 1);
            var strip = LocateStrip(strips, sy);
            var srcRow = (sy - strip.StartY) * width * 8;
            var dstRow = gy * gridW * 8;
            for (var gx = 0; gx < gridW; gx++)
            {
                var sx = Math.Clamp((int)(((gx + 0.5) * width) / gridW), 0, width - 1);
                Buffer.BlockCopy(strip.Pixels, srcRow + (sx * 8), grid, dstRow + (gx * 8), 8);
            }
        }

        return (grid, gridW, gridH);
    }

    internal bool HasExactPixelSamples => _samplePixels is not null && _sampleWidth == Width && _sampleHeight == Height;

    public Vector3? Sample(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height || _layout.X <= 0 || _layout.Y <= 0 || _samplePixels is null) return null;
        var u = ((x + 0.5f) / Width - _layout.Z) / _layout.X;
        var v = ((y + 0.5f) / Height - _layout.W) / _layout.Y;
        if (u < 0 || u > 1 || v < 0 || v > 1) return null;

        var sx = _sampleWidth == Width ? x : Math.Clamp((int)((long)x * _sampleWidth / Width), 0, _sampleWidth - 1);
        var sy = _sampleHeight == Height ? y : Math.Clamp((int)((long)y * _sampleHeight / Height), 0, _sampleHeight - 1);
        var offset = ((sy * _sampleWidth) + sx) * 8;
        var rgb = new Vector3(
            ReadHalf(_samplePixels, offset),
            ReadHalf(_samplePixels, offset + 2),
            ReadHalf(_samplePixels, offset + 4));
        return float.IsFinite(rgb.X) && float.IsFinite(rgb.Y) && float.IsFinite(rgb.Z) ? rgb : null;
    }

    private static float ReadHalf(byte[] pixels, int offset) => (float)BitConverter.UInt16BitsToHalf(
        (ushort)(pixels[offset] | (pixels[offset + 1] << 8)));

    public static float ToNits(Vector3 rgb) => Math.Max(0, Vector3.Dot(rgb, new Vector3(0.2126f, 0.7152f, 0.0722f)) * 80);

    // Left half: sRGB-encoded values up to SDR white. Right half: six stops above white.
    // Negative wide-gamut components fold into the first bin; values above +6 EV into the last.
    public static double ChannelPosition(float nits, float sdrWhiteNits)
    {
        if (!float.IsFinite(nits) || !float.IsFinite(sdrWhiteNits) || sdrWhiteNits <= 0) return 0;
        var relative = Math.Max(0, (double)nits / sdrWhiteNits);
        if (relative >= 1) return Math.Min(1, 0.5 + Math.Log2(relative) / 12);
        return 0.5 * (relative <= 0.0031308 ? 12.92 * relative : 1.055 * Math.Pow(relative, 1 / 2.4) - 0.055);
    }

    public static int ChannelBin(float nits, float sdrWhiteNits) =>
        Math.Clamp((int)(ChannelPosition(nits, sdrWhiteNits) * ChannelBinCount), 0, ChannelBinCount - 1);

    // Log2(1 + nits), 0..10000 nits; brighter values occupy the last bin.
    public static int HistogramBin(float nits) => float.IsFinite(nits)
        ? Math.Clamp((int)(MathF.Log2(1 + Math.Max(nits, 0)) * HistogramLogScale), 0, 63) : 0;

    private static int HistogramBinFast(float nits) =>
        Math.Clamp((int)(MathF.Log2(1.0f + nits) * HistogramLogScale), 0, 63);

    private static int ChannelBinFast(float channelScRgb, float scaleToWhite)
    {
        if (channelScRgb <= 0.0f)
        {
            return 0;
        }

        var relative = channelScRgb * scaleToWhite;
        float position;
        if (relative >= 1.0f)
        {
            position = MathF.Min(1.0f, 0.5f + (MathF.Log2(relative) * (1.0f / 12.0f)));
        }
        else if (relative <= 0.0031308f)
        {
            position = 0.5f * 12.92f * relative;
        }
        else
        {
            position = 0.5f * ((1.055f * MathF.Pow(relative, 1.0f / 2.4f)) - 0.055f);
        }

        return Math.Clamp((int)(position * ChannelBinCount), 0, ChannelBinCount - 1);
    }

    private sealed class ThreadAccumulator
    {
        public readonly int[] Histogram = new int[64];
        public readonly int[] RedHistogram = new int[ChannelBinCount];
        public readonly int[] GreenHistogram = new int[ChannelBinCount];
        public readonly int[] BlueHistogram = new int[ChannelBinCount];
        public readonly int[] PhysicalBins;
        public readonly int[] NonPhysicalBins;
        private int _returned;
        public int PhysicalCount;
        public int NonPhysicalCount;
        public int NonPhysicalOutsidePlotCount;
        public int DarkCount;
        public int InvalidCount;
        public double Sum;
        public float PeakNits;
        public int Count;

        public ThreadAccumulator()
        {
            PhysicalBins = ArrayPool<int>.Shared.Rent(ChromaticityDistribution.TotalBinCount);
            NonPhysicalBins = ArrayPool<int>.Shared.Rent(ChromaticityDistribution.TotalBinCount);
            Array.Clear(PhysicalBins, 0, ChromaticityDistribution.TotalBinCount);
            Array.Clear(NonPhysicalBins, 0, ChromaticityDistribution.TotalBinCount);
        }

        public void ReturnBuffers()
        {
            if (Interlocked.Exchange(ref _returned, 1) == 0)
            {
                ArrayPool<int>.Shared.Return(PhysicalBins);
                ArrayPool<int>.Shared.Return(NonPhysicalBins);
            }
        }
    }
}
