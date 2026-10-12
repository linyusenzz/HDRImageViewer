using System.Numerics;
using HdrImageViewer.Rendering;
using Xunit;

namespace HdrImageViewer.Tests;

public sealed class ChromaticityDistributionTests
{
    [Fact]
    public void LocusAndPurpleLineIncludeTheirBoundariesAndRealWideGamutPrimaries()
    {
        foreach (var xy in ChromaticityDiagram.SpectralLocus)
            Assert.True(ChromaticityDiagram.IsPhysical(xy), $"Boundary {xy}");
        var locus = ChromaticityDiagram.SpectralLocus;
        Assert.True(ChromaticityDiagram.IsPhysical((locus[0] + locus[^1]) / 2));
        foreach (var xy in ChromaticityDiagram.DisplayP3.Concat(ChromaticityDiagram.Bt2020))
            Assert.True(ChromaticityDiagram.IsPhysical(xy), $"Primary {xy}");
        Assert.True(ChromaticityDiagram.IsPhysical(ChromaticityDiagram.WhitePoint));
        Assert.False(ChromaticityDiagram.IsPhysical(new Vector2(.1f, .885f)));
        Assert.False(ChromaticityDiagram.IsPhysical(new Vector2(.7f, .1f)));
        Assert.False(ChromaticityDiagram.IsPhysical(new Vector2(float.NaN, .3f)));
    }

    [Fact]
    public void BoundaryToleranceDoesNotQuantizeClassificationToTheBinCenter()
    {
        var locus = ChromaticityDiagram.SpectralLocus;
        var mid = (locus[0] + locus[^1]) / 2;
        var normal = Vector2.Normalize(new Vector2((locus[^1] - locus[0]).Y, -(locus[^1] - locus[0]).X));
        var near = mid + normal * ChromaticityDiagram.LocusTolerance * .9f;
        var far = mid + normal * ChromaticityDiagram.LocusTolerance * 1.1f;
        Assert.Equal(ChromaticityDiagram.Bin(near), ChromaticityDiagram.Bin(far));
        Assert.True(ChromaticityDiagram.IsPhysical(near));
        Assert.False(ChromaticityDiagram.IsPhysical(far));
    }

    [Fact]
    public void RealPhotoDarkSampleIsNonPhysicalButNegativeScRgbAloneIsNot()
    {
        // P1109737.jpg pixel (1637,163): P3 base [1,3,0], gain map [122,123,125].
        // Independent reconstruction using embedded Adobe metadata, before scRGB conversion.
        var hdrP3 = new Vector3(-.00032814858f, .00084409527f, -.000029841219f);
        var sample = HdrColorMath.P3ToBt709(hdrP3);
        var xy = ChromaticityDiagram.FromScRgb(sample)!.Value;
        Assert.InRange(xy.X, .1025f, .1027f);
        Assert.InRange(xy.Y, .8852f, .8854f);
        var distribution = new ChromaticityDistribution();
        distribution.Add(sample);
        distribution.Add(HdrColorMath.P3ToBt709(Vector3.UnitY));
        Assert.Equal(1, distribution.PhysicalCount);
        Assert.Equal(1, distribution.NonPhysicalCount);
        Assert.Equal(1, distribution.PhysicalBins.Sum());
        Assert.Equal(1, distribution.NonPhysicalBins.Sum());
        Assert.Equal(.5, distribution.NonPhysicalFraction);
    }

    [Fact]
    public void OutsidePlotAndInvalidSamplesAreAccountedForWithoutClipping()
    {
        // XYZ = (-.1, .5, .6), sum=1: finite xy with positive luminance, but impossible X.
        var rgb = new Vector3(-1.391954f, 1.059841f, .526632f);
        var xy = ChromaticityDiagram.FromScRgb(rgb)!.Value;
        Assert.InRange(xy.X, -.101f, -.099f);
        var distribution = new ChromaticityDistribution();
        distribution.Add(rgb);
        distribution.Add(Vector3.Zero);
        distribution.Add(-Vector3.One);
        distribution.Add(new Vector3(float.NaN));
        Assert.Equal(1, distribution.NonPhysicalCount);
        Assert.Equal(1, distribution.NonPhysicalOutsidePlotCount);
        Assert.Equal(0, distribution.NonPhysicalBins.Sum());
        Assert.Equal(3, distribution.InvalidCount);
        Assert.Equal(1, distribution.NonPhysicalFraction);
    }

    [Fact]
    public void DarkFilterOnlyChangesChromaticityAndKeepsThresholdBoundary()
    {
        var bytes = Pixels([Vector3.One, new Vector3(.0001f), new Vector3(-.00032814858f, .00084409527f, -.000029841219f)]);
        var originalBytes = bytes.ToArray();
        var full = new LuminanceSnapshot(3, 1, bytes, new Vector4(1, 1, 0, 0), 1);
        var filtered = new LuminanceSnapshot(3, 1, bytes, new Vector4(1, 1, 0, 0), 1, minimumChromaticityNits: .1f);
        Assert.Equal(2, filtered.Chromaticity.DarkCount);
        Assert.Equal(1, filtered.Chromaticity.EligibleCount);
        Assert.Equal(full.Count, filtered.Count);
        Assert.Equal(full.AverageNits, filtered.AverageNits);
        Assert.Equal(full.PeakNits, filtered.PeakNits);
        Assert.Equal(full.Histogram, filtered.Histogram);
        Assert.Equal(full.RedHistogram, filtered.RedHistogram);
        Assert.Equal(full.GreenHistogram, filtered.GreenHistogram);
        Assert.Equal(full.BlueHistogram, filtered.BlueHistogram);
        for (var i = 0; i < 3; i++) Assert.Equal(full.Sample(i, 0), filtered.Sample(i, 0));
        Assert.Equal(originalBytes, bytes);
        var edge = new ChromaticityDistribution(LuminanceSnapshot.ToNits(Vector3.One));
        edge.Add(Vector3.One);
        Assert.Equal(1, edge.PhysicalCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChromaticityDistribution(float.NaN));
    }

    [Fact]
    public void ComparisonRetainsSeparateDiagnosticsAndRejectsDifferentFilters()
    {
        var bytes = Pixels([new Vector3(-.6f, 1, -.1f)]);
        var sdr = new LuminanceSnapshot(1, 1, Pixels([Vector3.One]), new Vector4(1, 1, 0, 0), 1);
        var hdr = new LuminanceSnapshot(1, 1, bytes, new Vector4(1, 1, 0, 0), 1);
        var pair = new GainMapChromaticityComparison(sdr, hdr);
        Assert.Equal(1, pair.Sdr.PhysicalCount);
        Assert.Equal(1, pair.Alternate.NonPhysicalCount);
        Assert.Equal(0, pair.AlternateCount);
        Assert.All(pair.CreateDensityPixels(true, showSdr: false), value => Assert.Equal(0, value));
        Assert.Equal(1, pair.Alternate.NonPhysicalBins.Sum());
        var differentFilter = new LuminanceSnapshot(1, 1, bytes, new Vector4(1, 1, 0, 0), 1, minimumChromaticityNits: 1);
        Assert.Throws<ArgumentException>(() => new GainMapChromaticityComparison(sdr, differentFilter));
    }

    private static byte[] Pixels(Vector3[] colors)
    {
        var bytes = new byte[colors.Length * 8];
        for (var i = 0; i < colors.Length; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 8), (Half)colors[i].X);
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 8 + 2), (Half)colors[i].Y);
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 8 + 4), (Half)colors[i].Z);
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 8 + 6), (Half)1);
        }
        return bytes;
    }
}
