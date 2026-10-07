using System.Numerics;
using HdrImageViewer.Rendering;
using Xunit;

namespace HdrImageViewer.Tests;

public sealed class GainMapChromaticityComparisonTests
{
    [Fact]
    public void RgbGainMovesChromaticityWhileUniformGainOverlapsBase()
    {
        var constants = new GainMapShaderConstants { Gamma = Vector4.One, GainMapMax = new Vector4(2) };
        var sdr = new Vector3(.25f);
        var rgbHdr = HdrColorMath.ReconstructAdobeHdrSample(sdr, Vector3.UnitX, constants, 1);
        Assert.Equal(new Vector3(1, .25f, .25f), rgbHdr);
        var monoHdr = HdrColorMath.ReconstructAdobeHdrSample(sdr, Vector3.One, constants, 1);
        var pair = new GainMapChromaticityComparison(Snapshot([sdr, sdr]), Snapshot([rgbHdr, monoHdr]));
        var whiteBin = ChromaticityDiagram.Bin(new Vector2(.3127f, .329f));
        // Independent XYZ result for linear RGB (1, .25, .25).
        var redBin = ChromaticityDiagram.Bin(new Vector2(.439937f, .329389f));
        Assert.Equal(2, pair.SdrBins[whiteBin]);
        Assert.Equal(1, pair.AlternateBins[whiteBin]);
        Assert.Equal(1, pair.AlternateBins[redBin]);
        Assert.NotEqual(whiteBin, redBin);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DensityDistinguishesSourcesAndOverlapWithoutOrderBias(bool dark)
    {
        var pair = new GainMapChromaticityComparison(
            Snapshot([Vector3.UnitX, Vector3.One, Vector3.Zero]),
            Snapshot([Vector3.UnitY, Vector3.One * 4, Vector3.Zero]));
        Assert.Equal(2, pair.SdrCount);
        Assert.Equal(2, pair.AlternateCount);
        var pixels = pair.CreateDensityPixels(dark);
        AssertPixel(pixels, new Vector2(.64f, .33f), GainMapChromaticityComparison.SdrColor(dark));
        AssertPixel(pixels, new Vector2(.30f, .60f), GainMapChromaticityComparison.AlternateColor(dark));
        AssertPixel(pixels, new Vector2(.3127f, .329f), GainMapChromaticityComparison.OverlapColor(dark));
        for (var i = 0; i < pixels.Length; i += 4)
        {
            Assert.True(pixels[i] <= pixels[i + 3]);
            Assert.True(pixels[i + 1] <= pixels[i + 3]);
            Assert.True(pixels[i + 2] <= pixels[i + 3]);
        }
    }

    [Fact]
    public void WideGamutSurvivesAndBlackDoesNotCreateFalseOverlap()
    {
        // Just inside Display P3 red, with negative scRGB channels. Avoid a zero
        // XYZ component at the gamut corner becoming negative after FP16 rounding.
        var p3Red = new Vector3(1.22494f, -.04206f, -.01964f) + new Vector3(.001f);
        var pair = new GainMapChromaticityComparison(Snapshot([Vector3.Zero]), Snapshot([p3Red]));
        Assert.Equal(0, pair.SdrCount);
        // Allow either adjacent cell at the exact P3-red grid boundary after FP16 rounding.
        var occupied = Assert.Single(pair.AlternateBins.Select((count, bin) => (count, bin)), item => item.count > 0);
        var center = new Vector2((occupied.bin % ChromaticityDiagram.BinWidth + .5f) * ChromaticityDiagram.MaxX / ChromaticityDiagram.BinWidth,
            (occupied.bin / ChromaticityDiagram.BinWidth + .5f) * ChromaticityDiagram.MaxY / ChromaticityDiagram.BinHeight);
        Assert.InRange(center.X, .675f, .685f);
        Assert.InRange(center.Y, .315f, .325f);
        AssertPixel(pair.CreateDensityPixels(true), center, GainMapChromaticityComparison.AlternateColor(true));
        var empty = new GainMapChromaticityComparison(Snapshot([Vector3.Zero]), Snapshot([Vector3.Zero]));
        Assert.All(empty.CreateDensityPixels(true), value => Assert.Equal(0, value));
    }

    [Fact]
    public void LayerSwitchesRevealOverlappingSourcesWithoutMutatingCapturedBins()
    {
        var pair = new GainMapChromaticityComparison(Snapshot([Vector3.One]), Snapshot([Vector3.One * 4]));
        var white = new Vector2(.3127f, .329f);
        AssertPixel(pair.CreateDensityPixels(true), white, GainMapChromaticityComparison.OverlapColor(true));
        AssertPixel(pair.CreateDensityPixels(true, showAlternate: false), white, GainMapChromaticityComparison.SdrColor(true));
        AssertPixel(pair.CreateDensityPixels(true, showSdr: false), white, GainMapChromaticityComparison.AlternateColor(true));
        Assert.All(pair.CreateDensityPixels(true, false, false), value => Assert.Equal(0, value));
        Assert.Equal(1, pair.SdrBins.Sum());
        Assert.Equal(1, pair.AlternateBins.Sum());
    }

    [Fact]
    public void RejectsFramesFromDifferentPreviews()
    {
        Assert.Throws<ArgumentException>(() => new GainMapChromaticityComparison(
            Snapshot([Vector3.One], 1), Snapshot([Vector3.One], 2)));
        Assert.Throws<ArgumentException>(() => new GainMapChromaticityComparison(
            Snapshot([Vector3.One]), Snapshot([Vector3.One, Vector3.One])));
    }

    private static LuminanceSnapshot Snapshot(Vector3[] colors, long version = 1)
    {
        var bytes = new byte[colors.Length * 8];
        for (var i = 0; i < colors.Length; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 8), (Half)colors[i].X);
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 8 + 2), (Half)colors[i].Y);
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 8 + 4), (Half)colors[i].Z);
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 8 + 6), (Half)1);
        }
        return new LuminanceSnapshot(colors.Length, 1, bytes, new Vector4(1, 1, 0, 0), version);
    }

    private static void AssertPixel(byte[] pixels, Vector2 xy, Vector3 color)
    {
        var bin = ChromaticityDiagram.Bin(xy);
        var x = bin % ChromaticityDiagram.BinWidth;
        var y = bin / ChromaticityDiagram.BinWidth;
        var offset = ((ChromaticityDiagram.BinHeight - 1 - y) * ChromaticityDiagram.BinWidth + x) * 4;
        Assert.Equal((byte)(color.Z * 240 / 255), pixels[offset]);
        Assert.Equal((byte)(color.Y * 240 / 255), pixels[offset + 1]);
        Assert.Equal((byte)(color.X * 240 / 255), pixels[offset + 2]);
        Assert.Equal(240, pixels[offset + 3]);
    }
}
