using HdrImageViewer.Services;
using Xunit;

namespace HdrImageViewer.Tests;

public sealed class JxlProbeTests
{
    [Fact]
    public void Parse_LegacySingleLineMetadata_DetectsHlgAndNumericFields()
    {
        const string output = """
            JPEG XL image, 4096x3072, lossy, 16-bit RGB
            intensity_target: 1000.000000 nits
            min_nits: 0.000000
            Color space: RGB, D65, Rec.2100 primaries, HLG transfer function, rendering intent: Relative
            """;

        var result = JxlProbe.Parse(output);

        Assert.True(result.IsJxl);
        Assert.Equal(4096, result.PixelWidth);
        Assert.Equal(3072, result.PixelHeight);
        Assert.Equal(16, result.BitDepth);
        Assert.Equal("HLG", result.TransferFunction);
        Assert.Equal("Rec.2100 / BT.2020", result.ColorPrimaries);
        Assert.Equal(1000.0, result.IntensityTargetNits);
        Assert.Equal(0.0, result.MinNits);
        Assert.True(result.IsHdrTransfer);
    }

    [Fact]
    public void Parse_MultilineMetadata_DetectsPqAndNumericFields()
    {
        const string output = """
            JPEG XL image, 3840x2160, lossless, 12-bit RGB
            Intensity target: 4000.000000 nits
            Min nits: 0.005000
            Color space: RGB
            White point: D65
            Primaries: Rec.2100
            Transfer function: PQ
            Rendering intent: Relative
            Box: Exif
            Transfer function: sRGB
            """;

        var result = JxlProbe.Parse(output);

        Assert.Equal("PQ / SMPTE ST 2084", result.TransferFunction);
        Assert.Equal("Rec.2100 / BT.2020", result.ColorPrimaries);
        Assert.Equal(4000.0, result.IntensityTargetNits);
        Assert.Equal(0.005, result.MinNits);
        Assert.True(result.IsHdrTransfer);
        Assert.True(result.UsesBt2020Primaries);
    }

    [Fact]
    public void Parse_MultilineSrgbMetadata_RemainsSdr()
    {
        const string output = """
            JPEG XL image, 1920x1080, lossless, 8-bit RGB
            Color space: RGB
            White point: D65
            Primaries: sRGB
            Transfer function: sRGB
            """;

        var result = JxlProbe.Parse(output);

        Assert.Equal("sRGB", result.TransferFunction);
        Assert.Equal("sRGB / BT.709", result.ColorPrimaries);
        Assert.False(result.IsHdrTransfer);
    }
}
