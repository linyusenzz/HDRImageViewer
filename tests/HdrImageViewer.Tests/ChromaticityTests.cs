using System.Numerics;
using HdrImageViewer.Rendering;
using Xunit;

namespace HdrImageViewer.Tests;

public sealed class ChromaticityTests
{
    [Theory]
    [InlineData(1, 0, 0, .64, .33)]
    [InlineData(0, 1, 0, .30, .60)]
    [InlineData(0, 0, 1, .15, .06)]
    [InlineData(1, 1, 1, .3127, .329)]
    [InlineData(12.5, 12.5, 12.5, .3127, .329)]
    public void ScRgbMapsToKnownChromaticities(float r, float g, float b, double x, double y)
    {
        var xy = ChromaticityDiagram.FromScRgb(new Vector3(r, g, b));
        Assert.NotNull(xy);
        Assert.Equal(x, xy.Value.X, 5); Assert.Equal(y, xy.Value.Y, 5);
    }

    [Fact]
    public void ExtendedRgbPreservesWideGamutPrimary()
    {
        var rgb = HdrColorMath.P3ToBt709(Vector3.UnitX);
        Assert.True(rgb.Y < 0);
        var xy = ChromaticityDiagram.FromScRgb(rgb)!.Value;
        Assert.Equal(.68, xy.X, 5); Assert.Equal(.32, xy.Y, 5);
        Assert.False(ChromaticityDiagram.Contains(ChromaticityDiagram.Srgb, xy));
        Assert.True(ChromaticityDiagram.Contains(ChromaticityDiagram.DisplayP3, xy));
        // The P3 red corner lies just outside the BT.2020 red-green edge.
        Assert.False(ChromaticityDiagram.Contains(ChromaticityDiagram.Bt2020, xy));
        var green = ChromaticityDiagram.FromScRgb(HdrColorMath.Bt2020ToBt709(Vector3.UnitY))!.Value;
        Assert.Equal(.17, green.X, 5); Assert.Equal(.797, green.Y, 5);
        Assert.False(ChromaticityDiagram.Contains(ChromaticityDiagram.DisplayP3, green));
        Assert.True(ChromaticityDiagram.Contains(ChromaticityDiagram.Bt2020, green));
    }

    [Fact]
    public void BlackInvalidAndOutsidePlotValuesAreNotPlotted()
    {
        Assert.Null(ChromaticityDiagram.FromScRgb(Vector3.Zero));
        Assert.Null(ChromaticityDiagram.FromScRgb(new Vector3(float.NaN)));
        Assert.Null(ChromaticityDiagram.FromScRgb(-Vector3.One));
        Assert.Equal(-1, ChromaticityDiagram.Bin(new Vector2(-.1f, .3f)));
        Assert.Equal(-1, ChromaticityDiagram.Bin(new Vector2(.3f, float.NaN)));
        var snap = new LuminanceSnapshot(1, 1, new byte[8], new Vector4(1, 1, 0, 0), 1);
        Assert.Equal(1, snap.Count);
        Assert.Equal(0, snap.ChromaticityCount);
        Assert.Equal(0, snap.ChromaticityBins.Sum());
    }

    [Fact]
    public void CieDatasetProducesExpectedHorseshoeAndPremultipliedBackground()
    {
        Assert.Equal(321, ChromaticityDiagram.SpectralLocus.Count);
        Assert.InRange(ChromaticityDiagram.SpectralLocus.Max(p => p.Y), .83f, .84f);
        var pixels = ChromaticityDiagram.CreateBackground();
        Assert.Equal(ChromaticityDiagram.BinWidth * ChromaticityDiagram.BinHeight * 4, pixels.Length);
        Assert.Contains((byte)48, pixels);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            Assert.True(pixels[i] <= pixels[i + 3]);
            Assert.True(pixels[i + 1] <= pixels[i + 3]);
            Assert.True(pixels[i + 2] <= pixels[i + 3]);
        }
    }
}
