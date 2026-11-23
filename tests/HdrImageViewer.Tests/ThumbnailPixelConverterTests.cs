using System.Numerics;
using HdrImageViewer.Models;
using HdrImageViewer.Rendering;
using HdrImageViewer.Services;
using Xunit;

namespace HdrImageViewer.Tests;

public sealed class ThumbnailPixelConverterTests
{
    [Theory]
    [InlineData(1, "123456")]
    [InlineData(2, "321654")]
    [InlineData(3, "654321")]
    [InlineData(4, "456123")]
    [InlineData(5, "142536")]
    [InlineData(6, "415263")]
    [InlineData(7, "635241")]
    [InlineData(8, "362514")]
    public void GainMapThumbnail_OrientsPrimaryAndGainTogether(int orientation, string expectedOrder)
    {
        // Both grids are asymmetric: rotating just one of them must fail.
        var primaryPixels = new byte[24];
        var gainPixels = new byte[24];
        for (var index = 0; index < 6; index++)
        {
            primaryPixels[index * 4] = primaryPixels[(index * 4) + 1] = primaryPixels[(index * 4) + 2] = (byte)(40 + (index * 10));
            gainPixels[index * 4] = gainPixels[(index * 4) + 1] = gainPixels[(index * 4) + 2] = (byte)(index * 40);
        }
        var primary = new DecodedBitmap(3, 2, primaryPixels, false);
        var gain = new DecodedBitmap(3, 2, gainPixels, false);
        var constants = new GainMapShaderConstants
        {
            GainMapMax = new Vector4(2),
            Gamma = Vector4.One,
            Orientation = new Vector4(1, 0, 0, 0),
        };
        var baseline = ThumbnailPixelConverter.ConvertGainMapToBgra8(new(primary, gain, constants), CancellationToken.None);
        constants.Orientation.X = orientation;

        var actual = ThumbnailPixelConverter.ConvertGainMapToBgra8(new(primary, gain, constants), CancellationToken.None);

        for (var pixel = 0; pixel < 6; pixel++)
        {
            var sourcePixel = expectedOrder[pixel] - '1';
            Assert.Equal(baseline.AsSpan(sourcePixel * 4, 4).ToArray(), actual.AsSpan(pixel * 4, 4).ToArray());
        }
    }

    [Theory]
    [InlineData(DecodedBitmapTransfer.Pq)]
    [InlineData(DecodedBitmapTransfer.Hlg)]
    public void DisplayP3Hdr_IsConvertedToWorkingPrimaries(DecodedBitmapTransfer transfer)
    {
        var bitmap = CreatePixel(0.6f, 0.4f, 0.2f, transfer, GainMapColorGamut.DisplayP3);
        var encoded = new Vector3(Quantize(0.6f), Quantize(0.4f), Quantize(0.2f));
        var linear = transfer == DecodedBitmapTransfer.Pq
            ? HdrColorMath.PqToSceneLinear(encoded)
            : HdrColorMath.HlgToSceneLinear(encoded, 1000.0f / 80.0f, gamut: GainMapColorGamut.DisplayP3);
        var expected = HdrColorMath.P3ToBt709(linear);

        AssertVector(expected, ThumbnailPixelConverter.DecodeSceneLinearBt709(bitmap, 0, 0));
    }

    [Fact]
    public void ExplicitBt2020Tag_IsUsedWithoutLegacyBoolean()
    {
        var bitmap = CreatePixel(0.6f, 0.4f, 0.2f, DecodedBitmapTransfer.Pq, GainMapColorGamut.Bt2100);
        var expected = HdrColorMath.Bt2020ToBt709(HdrColorMath.PqToSceneLinear(
            new Vector3(Quantize(0.6f), Quantize(0.4f), Quantize(0.2f))));

        AssertVector(expected, ThumbnailPixelConverter.DecodeSceneLinearBt709(bitmap, 0, 0));
    }

    [Fact]
    public void LinearScRgb_IsNotConvertedTwice()
    {
        var pixels = new byte[8];
        BitConverter.GetBytes((Half)0.5f).CopyTo(pixels, 0);
        BitConverter.GetBytes((Half)0.25f).CopyTo(pixels, 2);
        BitConverter.GetBytes((Half)0.125f).CopyTo(pixels, 4);
        var bitmap = new DecodedBitmap(1, 1, pixels, false, PixelFormat: DecodedBitmapPixelFormat.Rgba16Float,
            Transfer: DecodedBitmapTransfer.LinearScRgb, UsesBt2020Primaries: true);

        AssertVector(new Vector3(0.5f, 0.25f, 0.125f), ThumbnailPixelConverter.DecodeSceneLinearBt709(bitmap, 0, 0));
    }

    [Fact]
    public void WideGamutSdr_UsesGamutConversionWithoutHdrToneMapping()
    {
        var bitmap = CreatePixel(0.6f, 0.4f, 0.2f, DecodedBitmapTransfer.Sdr, GainMapColorGamut.DisplayP3);
        var result = ThumbnailPixelConverter.ConvertSdrToBgra8(bitmap, CancellationToken.None);

        // Independent P3 -> sRGB reference for encoded (0.6, 0.4, 0.2),
        // quantized at 16 bits on input and at 8 bits on output.
        Assert.Equal(new byte[] { 38, 99, 162, 255 }, result);
    }

    private static DecodedBitmap CreatePixel(float red, float green, float blue,
        DecodedBitmapTransfer transfer, GainMapColorGamut gamut)
    {
        var pixels = new byte[8];
        BitConverter.GetBytes((ushort)MathF.Round(red * 65535)).CopyTo(pixels, 0);
        BitConverter.GetBytes((ushort)MathF.Round(green * 65535)).CopyTo(pixels, 2);
        BitConverter.GetBytes((ushort)MathF.Round(blue * 65535)).CopyTo(pixels, 4);
        return new DecodedBitmap(1, 1, pixels, false,
            PixelFormat: DecodedBitmapPixelFormat.Rgba16Unorm, Transfer: transfer, ColorGamut: gamut);
    }

    private static float Quantize(float value) => MathF.Round(value * 65535) / 65535;

    private static void AssertVector(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 0.0001f);
        Assert.Equal(expected.Y, actual.Y, 0.0001f);
        Assert.Equal(expected.Z, actual.Z, 0.0001f);
    }
}
