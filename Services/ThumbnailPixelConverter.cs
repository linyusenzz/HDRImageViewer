using System.Buffers;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
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
    private static readonly float HlgThumbnailSystemGamma = HdrColorMath.CalculateHlgSystemGamma(HlgThumbnailTargetScenePeak);
    private static readonly float[] SrgbByteToLinearLut = CreateByteTransferLut(HdrColorMath.SrgbToLinear);
    private static readonly float[] Rec709ByteToLinearLut = CreateByteTransferLut(HdrColorMath.Rec709ToLinear);
    private static readonly Lazy<float[]> PqUInt16ToSceneLinearLut = new(CreatePqUInt16Lut);
    private static readonly Lazy<float[]> HlgUInt16ToSceneLinearLut = new(CreateHlgUInt16Lut);

    private static float[] CreateByteTransferLut(Func<float, float> transfer)
    {
        var table = new float[256];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = transfer(i / 255.0f);
        }

        return table;
    }

    private static float[] CreatePqUInt16Lut()
    {
        var table = new float[65536];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = HdrColorMath.PqToSceneLinear(new Vector3(i / 65535.0f)).X;
        }

        return table;
    }

    private static float[] CreateHlgUInt16Lut()
    {
        const float a = 0.17883277f;
        const float b = 0.28466892f;
        const float c = 0.55991073f;
        var table = new float[65536];
        for (var i = 0; i < table.Length; i++)
        {
            var v = i / 65535.0f;
            table[i] = v <= 0.5f ? (v * v) / 3.0f : (MathF.Exp((v - c) / a) + b) / 12.0f;
        }

        return table;
    }

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
        var orientationCode = (int)constants.Orientation.X;
        var orientation = new ExifOrientationTransform(
            primary.PixelWidth, primary.PixelHeight, orientationCode);
        var outWidth = orientation.Width;
        var outHeight = orientation.Height;
        var pixelCount = checked(outWidth * outHeight);
        var result = new byte[checked(pixelCount * 4)];

        if (primary.ColorManagedToSrgb && primary.PixelFormat == DecodedBitmapPixelFormat.Rgba8Unorm)
        {
            if (orientationCode is <= 1 or > 8)
            {
                SwizzleRgba8ToBgra8Opaque(primary.RgbaPixels, result, pixelCount);
                return result;
            }

            void CopyOrientedSrgbRow(int y)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var srcBytes = primary.RgbaPixels;
                var srcWidth = primary.PixelWidth;
                var destination = y * outWidth * 4;
                for (var x = 0; x < outWidth; x++)
                {
                    var source = orientation.MapToSource(x, y);
                    var srcOffset = ((source.Y * srcWidth) + source.X) * 4;
                    result[destination] = srcBytes[srcOffset + 2];
                    result[destination + 1] = srcBytes[srcOffset + 1];
                    result[destination + 2] = srcBytes[srcOffset];
                    result[destination + 3] = 255;
                    destination += 4;
                }
            }

            if (pixelCount >= ParallelPixelThreshold)
            {
                Parallel.For(0, outHeight, new ParallelOptions { CancellationToken = cancellationToken }, CopyOrientedSrgbRow);
            }
            else
            {
                for (var y = 0; y < outHeight; y++)
                {
                    CopyOrientedSrgbRow(y);
                }
            }

            return result;
        }

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

        if (bitmap.ColorManagedToSrgb && bitmap.PixelFormat == DecodedBitmapPixelFormat.Rgba8Unorm)
        {
            if (!premultiplyAlpha)
            {
                SwizzleRgba8ToBgra8Opaque(bitmap.RgbaPixels, result, pixelCount);
                return result;
            }

            void SwizzlePremultipliedRow(int y)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var src = bitmap.RgbaPixels;
                var offset = y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    var r = src[offset];
                    var g = src[offset + 1];
                    var b = src[offset + 2];
                    var a = src[offset + 3];
                    if (a == 255)
                    {
                        result[offset] = b;
                        result[offset + 1] = g;
                        result[offset + 2] = r;
                        result[offset + 3] = 255;
                    }
                    else if (a == 0)
                    {
                        result[offset] = 0;
                        result[offset + 1] = 0;
                        result[offset + 2] = 0;
                        result[offset + 3] = 0;
                    }
                    else
                    {
                        result[offset] = (byte)MathF.Round((b * a) / 255.0f);
                        result[offset + 1] = (byte)MathF.Round((g * a) / 255.0f);
                        result[offset + 2] = (byte)MathF.Round((r * a) / 255.0f);
                        result[offset + 3] = a;
                    }

                    offset += 4;
                }
            }

            if (pixelCount >= ParallelPixelThreshold)
            {
                Parallel.For(0, height, new ParallelOptions { CancellationToken = cancellationToken }, SwizzlePremultipliedRow);
            }
            else
            {
                for (var y = 0; y < height; y++)
                {
                    SwizzlePremultipliedRow(y);
                }
            }

            return result;
        }

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

    private static void SwizzleRgba8ToBgra8Opaque(byte[] source, byte[] destination, int pixelCount)
    {
        var byteCount = pixelCount * 4;
        var srcSpan = source.AsSpan(0, byteCount);
        var dstSpan = destination.AsSpan(0, byteCount);
        var index = 0;

        if (Avx2.IsSupported && byteCount >= 32)
        {
            var mask = Vector256.Create(
                (byte)2, 1, 0, 0x80, 6, 5, 4, 0x80, 10, 9, 8, 0x80, 14, 13, 12, 0x80,
                2, 1, 0, 0x80, 6, 5, 4, 0x80, 10, 9, 8, 0x80, 14, 13, 12, 0x80);
            var alphaMask = Vector256.Create(0xFF000000u).AsByte();
            for (; index <= byteCount - 32; index += 32)
            {
                var v = Vector256.Create<byte>(srcSpan.Slice(index, 32));
                var shuffled = Avx2.Or(Avx2.Shuffle(v, mask), alphaMask);
                shuffled.CopyTo(dstSpan.Slice(index, 32));
            }
        }
        else if (Ssse3.IsSupported && byteCount >= 16)
        {
            var mask = Vector128.Create((byte)2, 1, 0, 0x80, 6, 5, 4, 0x80, 10, 9, 8, 0x80, 14, 13, 12, 0x80);
            var alphaMask = Vector128.Create(0xFF000000u).AsByte();
            for (; index <= byteCount - 16; index += 16)
            {
                var v = Vector128.Create<byte>(srcSpan.Slice(index, 16));
                var shuffled = Sse2.Or(Ssse3.Shuffle(v, mask), alphaMask);
                shuffled.CopyTo(dstSpan.Slice(index, 16));
            }
        }

        for (; index < byteCount; index += 4)
        {
            dstSpan[index] = srcSpan[index + 2];
            dstSpan[index + 1] = srcSpan[index + 1];
            dstSpan[index + 2] = srcSpan[index];
            dstSpan[index + 3] = 255;
        }
    }

    internal static Vector3 DecodeSceneLinearBt709(DecodedBitmap bitmap, int x, int y)
    {
        if (bitmap.PixelFormat == DecodedBitmapPixelFormat.Rgba16Unorm)
        {
            var idx = checked(((y * bitmap.PixelWidth) + x) * 8);
            var r16 = ReadUInt16LittleEndian(bitmap.RgbaPixels, idx);
            var g16 = ReadUInt16LittleEndian(bitmap.RgbaPixels, idx + 2);
            var b16 = ReadUInt16LittleEndian(bitmap.RgbaPixels, idx + 4);
            if (bitmap.Transfer == DecodedBitmapTransfer.Pq)
            {
                var pqLut = PqUInt16ToSceneLinearLut.Value;
                return ConvertLinearToBt709(new Vector3(pqLut[r16], pqLut[g16], pqLut[b16]), bitmap.EffectiveColorGamut);
            }

            if (bitmap.Transfer == DecodedBitmapTransfer.Hlg)
            {
                var hlgLut = HlgUInt16ToSceneLinearLut.Value;
                var hlgScene = new Vector3(hlgLut[r16], hlgLut[g16], hlgLut[b16]);
                var gamut = bitmap.EffectiveColorGamut;
                var lumaWeights = gamut switch
                {
                    GainMapColorGamut.DisplayP3 => new Vector3(0.228975f, 0.691739f, 0.079287f),
                    GainMapColorGamut.Bt709 or GainMapColorGamut.Unknown => new Vector3(0.2126f, 0.7152f, 0.0722f),
                    _ => new Vector3(0.2627f, 0.6780f, 0.0593f),
                };
                var hlgLuma = Math.Max(Vector3.Dot(hlgScene, lumaWeights), 0.000001f);
                var sceneLinear = hlgScene * MathF.Pow(hlgLuma, HlgThumbnailSystemGamma - 1.0f) * HlgThumbnailTargetScenePeak;
                return ConvertLinearToBt709(sceneLinear, gamut);
            }
        }

        if (bitmap.PixelFormat == DecodedBitmapPixelFormat.Rgba8Unorm
            && bitmap.Transfer is DecodedBitmapTransfer.Sdr or DecodedBitmapTransfer.Bt709
            && bitmap.EffectiveColorGamut is not (GainMapColorGamut.AdobeRgb or GainMapColorGamut.ProPhoto))
        {
            var idx = checked(((y * bitmap.PixelWidth) + x) * 4);
            var lut = !bitmap.ColorManagedToSrgb && bitmap.Transfer == DecodedBitmapTransfer.Bt709
                ? Rec709ByteToLinearLut
                : SrgbByteToLinearLut;
            var sdrLinear = new Vector3(lut[bitmap.RgbaPixels[idx]], lut[bitmap.RgbaPixels[idx + 1]], lut[bitmap.RgbaPixels[idx + 2]]);
            return bitmap.ColorManagedToSrgb ? sdrLinear : ConvertLinearToBt709(sdrLinear, bitmap.EffectiveColorGamut);
        }

        var encoded = ReadEncodedRgb(bitmap, x, y);
        var linear = bitmap.Transfer switch
        {
            DecodedBitmapTransfer.Pq => HdrColorMath.PqToSceneLinear(encoded),
            DecodedBitmapTransfer.Hlg => HdrColorMath.HlgToSceneLinearWithGamma(encoded, HlgThumbnailTargetScenePeak, HlgThumbnailSystemGamma, bitmap.EffectiveColorGamut),
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
        var p709 = HdrColorMath.ReconstructGainMapHdrToBt709(sdr, gain, inputs.Constants, weight: 1.0f);
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
