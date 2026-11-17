using System.Buffers;
using System.Numerics;
using HdrImageViewer.Models;
using HdrImageViewer.Rendering;

namespace HdrImageViewer.Services;

// Pure pixel conversion shared by thumbnail presentation, SDR export, and regression tests.
internal static class ThumbnailPixelConverter
{
    private const float HlgThumbnailTargetScenePeak = 1000.0f / 80.0f;
    private const float ThumbnailHdrExposure = 0.60f;
    private const int ParallelPixelThreshold = 16_384;
    private const int MaxPooledSampleCount = 262_144;

    internal static byte[] ConvertGainMapBaseToBgra8(GainMapRenderInputs inputs, CancellationToken cancellationToken)
    {
        return ConvertGainMapBaseThumbnailToBgra8(
            new GainMapBaseThumbnailInput(inputs.Primary, inputs.Constants),
            cancellationToken);
    }

    internal static byte[] ConvertGainMapBaseThumbnailToBgra8(GainMapBaseThumbnailInput input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var primary = input.Primary;
        var constants = input.Constants;
        var orientation = new ExifOrientationTransform(
            primary.PixelWidth, primary.PixelHeight, (int)constants.Orientation.X);
        var outWidth = orientation.Width;
        var outHeight = orientation.Height;
        var pixelCount = checked(outWidth * outHeight);
        var result = new byte[checked(pixelCount * 4)];

        void ConvertRow(int y)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = y * outWidth * 4;
            for (var x = 0; x < outWidth; x++)
            {
                var source = orientation.MapToSource(x, y);
                var encoded = ReadEncodedRgb(primary, source.X, source.Y);
                var linear = primary.ColorManagedToSrgb
                    ? HdrColorMath.SrgbToLinear(encoded)
                    : HdrColorMath.ConvertGainMapBaseToBt709(
                        HdrColorMath.DecodeGainMapBaseToLinear(encoded, constants), constants);
                result[destination] = ToByte(LinearToSrgb(linear.Z));
                result[destination + 1] = ToByte(LinearToSrgb(linear.Y));
                result[destination + 2] = ToByte(LinearToSrgb(linear.X));
                result[destination + 3] = 255;
                destination += 4;
            }
        }

        if (pixelCount >= ParallelPixelThreshold)
        {
            Parallel.For(0, outHeight, new ParallelOptions { CancellationToken = cancellationToken }, ConvertRow);
        }
        else
        {
            for (var y = 0; y < outHeight; y++)
            {
                ConvertRow(y);
            }
        }

        return result;
    }

    internal static byte[] ConvertHdrPreviewToBgra8(DecodedBitmap bitmap, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var pixelCount = checked(width * height);
        var whiteScale = bitmap.Transfer is DecodedBitmapTransfer.Pq or DecodedBitmapTransfer.Hlg
            or DecodedBitmapTransfer.LinearSceneScRgb
            ? HdrColorMath.UltraHdrReferenceWhiteNits / HdrColorMath.ReferenceWhiteNits
            : 1.0f;

        const float knee = 0.92f;
        const float targetRange = 1.0f - knee;
        var result = new byte[checked(pixelCount * 4)];

        if (pixelCount <= MaxPooledSampleCount)
        {
            var rented = ArrayPool<Vector3>.Shared.Rent(pixelCount);
            try
            {
                var peak = 1.0f;
                for (var y = 0; y < height; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var rowOffset = y * width;
                    for (var x = 0; x < width; x++)
                    {
                        var sample = DecodeSceneLinearBt709(bitmap, x, y) / whiteScale;
                        sample = new Vector3(FinitePositive(sample.X), FinitePositive(sample.Y), FinitePositive(sample.Z));
                        rented[rowOffset + x] = sample;
                        if (ReadAlpha(bitmap, x, y) > 0.0f)
                        {
                            peak = Math.Max(peak, Math.Max(sample.X, Math.Max(sample.Y, sample.Z)));
                        }
                    }
                }

                var denominator = 1.0f - MathF.Exp(-(peak - knee) / targetRange);

                void MapPooledRow(int y)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var rowOffset = y * width;
                    var destination = rowOffset * 4;
                    for (var x = 0; x < width; x++)
                    {
                        var sample = rented[rowOffset + x];
                        var samplePeak = Math.Max(sample.X, Math.Max(sample.Y, sample.Z));
                        if (samplePeak > knee && denominator > 0.0f)
                        {
                            var mappedPeak = Math.Min(
                                1.0f,
                                knee + (targetRange * (1.0f - MathF.Exp(-(samplePeak - knee) / targetRange)) / denominator));
                            sample *= mappedPeak / samplePeak;
                        }

                        var alpha = ReadAlpha(bitmap, x, y);
                        result[destination] = ToByte(LinearToSrgb(sample.Z) * alpha);
                        result[destination + 1] = ToByte(LinearToSrgb(sample.Y) * alpha);
                        result[destination + 2] = ToByte(LinearToSrgb(sample.X) * alpha);
                        result[destination + 3] = ToByte(alpha);
                        destination += 4;
                    }
                }

                if (pixelCount >= ParallelPixelThreshold)
                {
                    Parallel.For(0, height, new ParallelOptions { CancellationToken = cancellationToken }, MapPooledRow);
                }
                else
                {
                    for (var y = 0; y < height; y++)
                    {
                        MapPooledRow(y);
                    }
                }

                return result;
            }
            finally
            {
                ArrayPool<Vector3>.Shared.Return(rented);
            }
        }

        // Large image two-pass path with zero intermediate sample array allocation.
        var globalPeak = 1.0f;
        var peakLock = new object();
        Parallel.For(
            0,
            height,
            new ParallelOptions { CancellationToken = cancellationToken },
            () => 1.0f,
            (y, _, localPeak) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = 0; x < width; x++)
                {
                    if (ReadAlpha(bitmap, x, y) <= 0.0f)
                    {
                        continue;
                    }

                    var sample = DecodeSceneLinearBt709(bitmap, x, y) / whiteScale;
                    var sPeak = Math.Max(FinitePositive(sample.X), Math.Max(FinitePositive(sample.Y), FinitePositive(sample.Z)));
                    if (sPeak > localPeak)
                    {
                        localPeak = sPeak;
                    }
                }

                return localPeak;
            },
            localPeak =>
            {
                lock (peakLock)
                {
                    if (localPeak > globalPeak)
                    {
                        globalPeak = localPeak;
                    }
                }
            });

        var largeDenominator = 1.0f - MathF.Exp(-(globalPeak - knee) / targetRange);
        Parallel.For(0, height, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var sample = DecodeSceneLinearBt709(bitmap, x, y) / whiteScale;
                sample = new Vector3(FinitePositive(sample.X), FinitePositive(sample.Y), FinitePositive(sample.Z));
                var samplePeak = Math.Max(sample.X, Math.Max(sample.Y, sample.Z));
                if (samplePeak > knee && largeDenominator > 0.0f)
                {
                    var mappedPeak = Math.Min(
                        1.0f,
                        knee + (targetRange * (1.0f - MathF.Exp(-(samplePeak - knee) / targetRange)) / largeDenominator));
                    sample *= mappedPeak / samplePeak;
                }

                var alpha = ReadAlpha(bitmap, x, y);
                result[destination] = ToByte(LinearToSrgb(sample.Z) * alpha);
                result[destination + 1] = ToByte(LinearToSrgb(sample.Y) * alpha);
                result[destination + 2] = ToByte(LinearToSrgb(sample.X) * alpha);
                result[destination + 3] = ToByte(alpha);
                destination += 4;
            }
        });

        return result;
    }

    private static float FinitePositive(float value) => float.IsFinite(value) ? Math.Max(0, value) : 0;

    internal static byte[] ConvertHdrToBgra8(DecodedBitmap bitmap, CancellationToken cancellationToken, bool premultiplyAlpha = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var pixelCount = checked(width * height);
        var result = new byte[checked(pixelCount * 4)];

        void ConvertRow(int y)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var linear = DecodeSceneLinearBt709(bitmap, x, y);
                var mapped = ToneMapToSdr(linear);
                var alpha = premultiplyAlpha ? ReadAlpha(bitmap, x, y) : 1.0f;
                result[destination] = ToByte(LinearToSrgb(mapped.Z) * alpha);
                result[destination + 1] = ToByte(LinearToSrgb(mapped.Y) * alpha);
                result[destination + 2] = ToByte(LinearToSrgb(mapped.X) * alpha);
                result[destination + 3] = ToByte(alpha);
                destination += 4;
            }
        }

        if (pixelCount >= ParallelPixelThreshold)
        {
            Parallel.For(0, height, new ParallelOptions { CancellationToken = cancellationToken }, ConvertRow);
        }
        else
        {
            for (var y = 0; y < height; y++)
            {
                ConvertRow(y);
            }
        }

        return result;
    }

    internal static byte[] ConvertGainMapToBgra8(GainMapRenderInputs inputs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var orientation = new ExifOrientationTransform(
            inputs.Primary.PixelWidth, inputs.Primary.PixelHeight, (int)inputs.Constants.Orientation.X);
        var outWidth = orientation.Width;
        var outHeight = orientation.Height;
        var pixelCount = checked(outWidth * outHeight);
        var result = new byte[checked(pixelCount * 4)];

        void ConvertRow(int y)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = y * outWidth * 4;
            for (var x = 0; x < outWidth; x++)
            {
                var source = orientation.MapToSource(x, y);
                var linear = DecodeGainMapSceneLinearBt709(inputs, source.X, source.Y);
                var mapped = ToneMapToSdr(linear);
                result[destination] = ToByte(LinearToSrgb(mapped.Z));
                result[destination + 1] = ToByte(LinearToSrgb(mapped.Y));
                result[destination + 2] = ToByte(LinearToSrgb(mapped.X));
                result[destination + 3] = 255;
                destination += 4;
            }
        }

        if (pixelCount >= ParallelPixelThreshold)
        {
            Parallel.For(0, outHeight, new ParallelOptions { CancellationToken = cancellationToken }, ConvertRow);
        }
        else
        {
            for (var y = 0; y < outHeight; y++)
            {
                ConvertRow(y);
            }
        }

        return result;
    }

    internal static byte[] ConvertSdrToBgra8(DecodedBitmap bitmap, CancellationToken cancellationToken, bool premultiplyAlpha = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var pixelCount = checked(width * height);
        var result = new byte[checked(pixelCount * 4)];

        void ConvertRow(int y)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var linear = DecodeSceneLinearBt709(bitmap, x, y);
                var alpha = premultiplyAlpha ? ReadAlpha(bitmap, x, y) : 1.0f;
                result[destination] = ToByte(LinearToSrgb(linear.Z) * alpha);
                result[destination + 1] = ToByte(LinearToSrgb(linear.Y) * alpha);
                result[destination + 2] = ToByte(LinearToSrgb(linear.X) * alpha);
                result[destination + 3] = ToByte(alpha);
                destination += 4;
            }
        }

        if (pixelCount >= ParallelPixelThreshold)
        {
            Parallel.For(0, height, new ParallelOptions { CancellationToken = cancellationToken }, ConvertRow);
        }
        else
        {
            for (var y = 0; y < height; y++)
            {
                ConvertRow(y);
            }
        }

        return result;
    }

    internal static Vector3 DecodeSceneLinearBt709(DecodedBitmap bitmap, int x, int y)
    {
        var encoded = ReadEncodedRgb(bitmap, x, y);
        var linear = bitmap.Transfer switch
        {
            DecodedBitmapTransfer.Pq => HdrColorMath.PqToSceneLinear(encoded),
            DecodedBitmapTransfer.Hlg => HdrColorMath.HlgToSceneLinear(encoded, HlgThumbnailTargetScenePeak),
            DecodedBitmapTransfer.LinearScRgb => encoded,
            DecodedBitmapTransfer.LinearSceneScRgb => encoded,
            _ => bitmap.ColorManagedToSrgb
                ? HdrColorMath.SrgbToLinear(encoded)
                : HdrColorMath.DecodeSdrEncodedToLinear(
                    encoded,
                    bitmap.EffectiveColorGamut,
                    bitmap.Transfer == DecodedBitmapTransfer.Bt709
                        ? HdrColorMath.GainMapBaseTransferBt709
                        : HdrColorMath.GainMapBaseTransferSrgb),
        };

        return bitmap.Transfer is DecodedBitmapTransfer.LinearScRgb or DecodedBitmapTransfer.LinearSceneScRgb
            || bitmap.ColorManagedToSrgb
            ? linear
            : ConvertLinearToBt709(linear, bitmap.EffectiveColorGamut);
    }

    internal static Vector3 ConvertLinearToBt709(Vector3 linear, GainMapColorGamut gamut)
    {
        return HdrColorMath.ConvertLinearToBt709(linear, gamut);
    }

    private static Vector3 DecodeGainMapSceneLinearBt709(GainMapRenderInputs inputs, int x, int y)
    {
        var sdr = HdrColorMath.DecodeGainMapBaseToLinear(ReadEncodedRgb(inputs.Primary, x, y), inputs.Constants);
        var gain = ReadGainMapSample(inputs.GainMap, x, y, inputs.Primary.PixelWidth, inputs.Primary.PixelHeight);
        var scene = inputs.Constants.GainMapControl.Y > 0.5f
            ? HdrColorMath.ReconstructAppleHdrSample(sdr, gain, inputs.Constants.GainMapMax.X, 1.0f)
            : HdrColorMath.ReconstructAdobeHdrSample(sdr, gain, inputs.Constants, 1.0f);
        var p709 = HdrColorMath.ConvertGainMapBaseToBt709(scene, inputs.Constants);
        return inputs.Constants.GainMapControl.Y <= 0.5f
            ? p709 * (HdrColorMath.UltraHdrReferenceWhiteNits / HdrColorMath.ReferenceWhiteNits)
            : p709;
    }

    private static Vector3 ToneMapToSdr(Vector3 sceneLinear)
    {
        var exposed = Vector3.Max(Vector3.Zero, sceneLinear) * ThumbnailHdrExposure;
        return new Vector3(
            AcesToneMap(exposed.X),
            AcesToneMap(exposed.Y),
            AcesToneMap(exposed.Z));
    }

    private static float AcesToneMap(float value)
    {
        value = Math.Max(value, 0.0f);
        return Math.Clamp(
            (value * ((2.51f * value) + 0.03f)) / ((value * ((2.43f * value) + 0.59f)) + 0.14f),
            0.0f,
            1.0f);
    }

    private static Vector3 ReadEncodedRgb(DecodedBitmap bitmap, int x, int y)
    {
        var index = checked(((y * bitmap.PixelWidth) + x) * bitmap.BytesPerPixel);
        if (bitmap.PixelFormat == DecodedBitmapPixelFormat.Rgba16Float)
        {
            return new Vector3(
                ReadHalfLittleEndian(bitmap.RgbaPixels, index),
                ReadHalfLittleEndian(bitmap.RgbaPixels, index + 2),
                ReadHalfLittleEndian(bitmap.RgbaPixels, index + 4));
        }

        if (bitmap.PixelFormat == DecodedBitmapPixelFormat.Rgba16Unorm)
        {
            return new Vector3(
                ReadUInt16LittleEndian(bitmap.RgbaPixels, index) / 65535.0f,
                ReadUInt16LittleEndian(bitmap.RgbaPixels, index + 2) / 65535.0f,
                ReadUInt16LittleEndian(bitmap.RgbaPixels, index + 4) / 65535.0f);
        }

        return new Vector3(
            bitmap.RgbaPixels[index] / 255.0f,
            bitmap.RgbaPixels[index + 1] / 255.0f,
            bitmap.RgbaPixels[index + 2] / 255.0f);
    }

    private static Vector3 ReadGainMapSample(DecodedBitmap bitmap, int primaryX, int primaryY, int primaryWidth, int primaryHeight)
    {
        var x = Math.Clamp((int)((primaryX + 0.5f) * bitmap.PixelWidth / Math.Max(primaryWidth, 1)), 0, bitmap.PixelWidth - 1);
        var y = Math.Clamp((int)((primaryY + 0.5f) * bitmap.PixelHeight / Math.Max(primaryHeight, 1)), 0, bitmap.PixelHeight - 1);
        return ReadEncodedRgb(bitmap, x, y);
    }

    private static float ReadAlpha(DecodedBitmap bitmap, int x, int y)
    {
        var offset = checked(((y * bitmap.PixelWidth) + x) * bitmap.BytesPerPixel);
        var alpha = bitmap.PixelFormat switch
        {
            DecodedBitmapPixelFormat.Rgba16Float => ReadHalfLittleEndian(bitmap.RgbaPixels, offset + 6),
            DecodedBitmapPixelFormat.Rgba16Unorm => ReadUInt16LittleEndian(bitmap.RgbaPixels, offset + 6) / 65535.0f,
            _ => bitmap.RgbaPixels[offset + 3] / 255.0f,
        };
        return float.IsFinite(alpha) ? Math.Clamp(alpha, 0.0f, 1.0f) : 0.0f;
    }

    private static float LinearToSrgb(float value)
    {
        value = Math.Clamp(value, 0.0f, 1.0f);
        if (value == 1.0f)
        {
            return 1.0f;
        }

        return value <= 0.0031308f
            ? value * 12.92f
            : (1.055f * MathF.Pow(value, 1.0f / 2.4f)) - 0.055f;
    }

    private static byte ToByte(float value)
    {
        return (byte)Math.Clamp(MathF.Round(Math.Clamp(value, 0.0f, 1.0f) * 255.0f), 0.0f, 255.0f);
    }

    private static float ReadHalfLittleEndian(byte[] data, int offset)
    {
        return (float)BitConverter.UInt16BitsToHalf((ushort)(data[offset] | (data[offset + 1] << 8)));
    }

    private static ushort ReadUInt16LittleEndian(byte[] data, int offset)
    {
        return (ushort)(data[offset] | (data[offset + 1] << 8));
    }
}
