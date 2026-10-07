using System.Numerics;

namespace HdrImageViewer.Rendering;

public sealed class LuminanceSnapshot
{
    private readonly byte[] _pixels;
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
    public int[] ChromaticityBins { get; } = new int[ChromaticityDiagram.BinWidth * ChromaticityDiagram.BinHeight];
    public int ChromaticityCount { get; }
    public GainMapChromaticityComparison? GainMapComparison { get; init; }
    public double AverageNits { get; }
    public float PeakNits { get; }
    public int Count { get; }

    public LuminanceSnapshot(int width, int height, byte[] pixels, Vector4 layout, long version,
        float sdrWhiteNits = 80, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height * 8))
            throw new ArgumentException("Invalid FP16 preview dimensions.");
        Width = width; Height = height; _pixels = pixels; _layout = layout; Version = version;
        SdrWhiteNits = float.IsFinite(sdrWhiteNits) && sdrWhiteNits > 0 ? sdrWhiteNits : 80;
        double sum = 0;
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                if (Sample(x, y) is not { } sample) continue;
                var nits = ToNits(sample);
                Histogram[HistogramBin(nits)]++;
                RedHistogram[ChannelBin(sample.X * 80, SdrWhiteNits)]++;
                GreenHistogram[ChannelBin(sample.Y * 80, SdrWhiteNits)]++;
                BlueHistogram[ChannelBin(sample.Z * 80, SdrWhiteNits)]++;
                if (ChromaticityDiagram.FromScRgb(sample) is { } xy && ChromaticityDiagram.Bin(xy) is var bin && bin >= 0)
                {
                    ChromaticityBins[bin]++;
                    ChromaticityCount++;
                }
                sum += nits;
                PeakNits = Math.Max(PeakNits, nits);
                Count++;
            }
        }
        AverageNits = Count == 0 ? 0 : sum / Count;
    }

    public Vector3? Sample(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height || _layout.X <= 0 || _layout.Y <= 0) return null;
        var u = ((x + 0.5f) / Width - _layout.Z) / _layout.X;
        var v = ((y + 0.5f) / Height - _layout.W) / _layout.Y;
        if (u < 0 || u > 1 || v < 0 || v > 1) return null;
        var offset = (y * Width + x) * 8;
        var rgb = new Vector3(Read(offset), Read(offset + 2), Read(offset + 4));
        return float.IsFinite(rgb.X) && float.IsFinite(rgb.Y) && float.IsFinite(rgb.Z) ? rgb : null;
    }

    private float Read(int offset) => (float)BitConverter.UInt16BitsToHalf(
        (ushort)(_pixels[offset] | (_pixels[offset + 1] << 8)));

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
        ? Math.Clamp((int)(MathF.Log2(1 + Math.Max(nits, 0)) / MathF.Log2(10001) * 64), 0, 63) : 0;
}
