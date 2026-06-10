using System.Numerics;
using HdrImageViewer.Rendering;
using Xunit;

namespace HdrImageViewer.Tests;

public class HdrColorMathTests
{
    private const float Tolerance = 0.001f;

    [Theory]
    [InlineData(0.0f, 0.0f)]
    [InlineData(1.0f, 1.0f)]
    [InlineData(0.5f, 0.21404114f)]
    public void SrgbToLinear_MatchesReferenceValues(float encoded, float expected)
    {
        Assert.Equal(expected, HdrColorMath.SrgbToLinear(encoded), Tolerance);
    }

    [Fact]
    public void SrgbToLinear_IsContinuousAtSegmentBoundary()
    {
        var below = HdrColorMath.SrgbToLinear(0.04045f - 0.0001f);
        var above = HdrColorMath.SrgbToLinear(0.04045f + 0.0001f);
        Assert.True(Math.Abs(above - below) < 0.001f);
    }

    [Theory]
    [InlineData(0.0f, 0.0f)]
    [InlineData(1.0f, 1.0f)]
    public void Rec709ToLinear_MatchesEndpoints(float encoded, float expected)
    {
        Assert.Equal(expected, HdrColorMath.Rec709ToLinear(encoded), Tolerance);
    }

    [Fact]
    public void PqToSceneLinear_MapsReferenceLevels()
    {
        // PQ-encoded 100 nits is 0.508078; the scene value is nits / 80.
        var hundredNits = HdrColorMath.PqToSceneLinear(new Vector3(0.508078f));
        Assert.Equal(1.25f, hundredNits.X, 0.01f);

        // PQ 1.0 is 10000 nits by definition.
        var peak = HdrColorMath.PqToSceneLinear(Vector3.One);
        Assert.Equal(125.0f, peak.X, 0.1f);

        Assert.Equal(0.0f, HdrColorMath.PqToSceneLinear(Vector3.Zero).X, Tolerance);
    }

    [Fact]
    public void HlgToSceneLinear_MapsNominalPeakWhiteToTargetPeak()
    {
        const float targetScenePeak = 1000.0f / 80.0f;
        var peak = HdrColorMath.HlgToSceneLinear(Vector3.One, targetScenePeak);
        Assert.Equal(targetScenePeak, peak.X, 0.01f);
        Assert.Equal(0.0f, HdrColorMath.HlgToSceneLinear(Vector3.Zero, targetScenePeak).X, Tolerance);
    }

    [Theory]
    [InlineData(1000.0f / 80.0f, 1.2f)]
    [InlineData(100.0f / 80.0f, 1.0f)]
    [InlineData(4000.0f / 80.0f, 1.35f)]
    public void CalculateHlgSystemGamma_FollowsBt2100AndClamps(float targetScenePeak, float expected)
    {
        Assert.Equal(expected, HdrColorMath.CalculateHlgSystemGamma(targetScenePeak), Tolerance);
    }

    [Fact]
    public void GamutMatrices_PreserveWhite()
    {
        AssertVectorEqual(Vector3.One, HdrColorMath.Bt2020ToBt709(Vector3.One));
        AssertVectorEqual(Vector3.One, HdrColorMath.Bt709ToBt2020(Vector3.One));
        AssertVectorEqual(Vector3.One, HdrColorMath.P3ToBt709(Vector3.One));
        AssertVectorEqual(Vector3.One, HdrColorMath.P3ToBt2020(Vector3.One));
        AssertVectorEqual(Vector3.One, HdrColorMath.ProPhotoToBt709(Vector3.One));
    }

    [Fact]
    public void Bt709Bt2020Conversion_RoundTrips()
    {
        var value = new Vector3(0.2f, 0.5f, 0.8f);
        var roundTripped = HdrColorMath.Bt2020ToBt709(HdrColorMath.Bt709ToBt2020(value));
        AssertVectorEqual(value, roundTripped);
    }

    [Fact]
    public void ConvertBt2020ToBt709_ClipModeRemovesNegatives()
    {
        var bt2020Red = new Vector3(1.0f, 0.0f, 0.0f);

        var managed = HdrColorMath.ConvertBt2020ToBt709(bt2020Red, ColorGamutMappingMode.Managed);
        Assert.True(managed.Y < 0.0f, "scRGB mode should preserve out-of-gamut negatives");

        var clipped = HdrColorMath.ConvertBt2020ToBt709(bt2020Red, ColorGamutMappingMode.Clip);
        Assert.True(clipped.Y >= 0.0f && clipped.Z >= 0.0f, "clip mode should clamp negatives to zero");
    }

    [Fact]
    public void ReconstructAppleHdrSample_WeightInterpolatesHeadroom()
    {
        var sdr = new Vector3(0.25f, 0.5f, 0.75f);
        var fullGain = Vector3.One;
        const float headroom = 4.0f;

        AssertVectorEqual(sdr, HdrColorMath.ReconstructAppleHdrSample(sdr, fullGain, headroom, weight: 0.0f));
        AssertVectorEqual(sdr * headroom, HdrColorMath.ReconstructAppleHdrSample(sdr, fullGain, headroom, weight: 1.0f));
    }

    [Fact]
    public void ReconstructAdobeHdrSample_AppliesLogBoost()
    {
        var constants = new GainMapShaderConstants
        {
            GainMapMin = Vector4.Zero,
            GainMapMax = new Vector4(2.0f, 2.0f, 2.0f, 0.0f),
            Gamma = Vector4.One,
            OffsetSdr = Vector4.Zero,
            OffsetHdr = Vector4.Zero,
        };
        var sdr = new Vector3(0.25f, 0.5f, 0.75f);

        // Full gain at weight 1 applies the max boost of 2 stops (x4).
        AssertVectorEqual(sdr * 4.0f, HdrColorMath.ReconstructAdobeHdrSample(sdr, Vector3.One, constants, weight: 1.0f));

        // Weight 0 leaves the SDR rendition untouched.
        AssertVectorEqual(sdr, HdrColorMath.ReconstructAdobeHdrSample(sdr, Vector3.One, constants, weight: 0.0f));

        // Zero gain stays at the gain-map minimum (0 stops).
        AssertVectorEqual(sdr, HdrColorMath.ReconstructAdobeHdrSample(sdr, Vector3.Zero, constants, weight: 1.0f));
    }

    private static void AssertVectorEqual(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, Tolerance);
        Assert.Equal(expected.Y, actual.Y, Tolerance);
        Assert.Equal(expected.Z, actual.Z, Tolerance);
    }
}
