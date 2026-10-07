using System.Numerics;

namespace HdrImageViewer.Rendering;

/// <summary>Retains only density bins, not the two extra FP16 readback buffers.</summary>
public sealed class GainMapChromaticityComparison
{
    public int[] SdrBins { get; }
    public int[] AlternateBins { get; }
    public int SdrCount { get; }
    public int AlternateCount { get; }

    public GainMapChromaticityComparison(LuminanceSnapshot sdr, LuminanceSnapshot alternate)
    {
        if (sdr.Version != alternate.Version || sdr.Width != alternate.Width || sdr.Height != alternate.Height)
            throw new ArgumentException("Comparison frames must belong to the same preview.");
        SdrBins = sdr.ChromaticityBins;
        AlternateBins = alternate.ChromaticityBins;
        SdrCount = sdr.ChromaticityCount;
        AlternateCount = alternate.ChromaticityCount;
    }

    public static Vector3 SdrColor(bool dark) => dark ? new(80, 210, 235) : new(0, 105, 135);
    public static Vector3 AlternateColor(bool dark) => dark ? new(255, 165, 75) : new(175, 80, 0);
    public static Vector3 OverlapColor(bool dark) => dark ? new(190, 198, 205) : new(75, 85, 95);

    public byte[] CreateDensityPixels(bool dark, bool showSdr = true, bool showAlternate = true)
    {
        var pixels = new byte[ChromaticityDiagram.BinWidth * ChromaticityDiagram.BinHeight * 4];
        var maximum = Math.Log(1 + Math.Max(SdrBins.Max(), AlternateBins.Max()));
        if (maximum == 0) return pixels;
        for (var i = 0; i < SdrBins.Length; i++)
        {
            var sdr = showSdr ? SdrBins[i] : 0;
            var alternate = showAlternate ? AlternateBins[i] : 0;
            if (sdr == 0 && alternate == 0) continue;
            // Use a neutral color for shared bins, independent of drawing order.
            var color = sdr > 0 && alternate > 0 ? OverlapColor(dark)
                : sdr > 0 ? SdrColor(dark) : AlternateColor(dark);
            var alpha = (byte)(48 + 192 * Math.Pow(Math.Log(1 + Math.Max(sdr, alternate)) / maximum, .65));
            var x = i % ChromaticityDiagram.BinWidth;
            var y = i / ChromaticityDiagram.BinWidth;
            var offset = ((ChromaticityDiagram.BinHeight - 1 - y) * ChromaticityDiagram.BinWidth + x) * 4;
            pixels[offset] = (byte)(color.Z * alpha / 255);
            pixels[offset + 1] = (byte)(color.Y * alpha / 255);
            pixels[offset + 2] = (byte)(color.X * alpha / 255);
            pixels[offset + 3] = alpha;
        }
        return pixels;
    }
}
