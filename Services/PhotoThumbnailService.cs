using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using HdrImageViewer.Models;
using HdrImageViewer.Rendering;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace HdrImageViewer.Services;

public static class PhotoThumbnailService
{
    private const float HlgThumbnailTargetScenePeak = 1000.0f / 80.0f;
    private const float ThumbnailHdrExposure = 0.60f;

    public static async Task<ImageSource?> CreateAsync(
        string path,
        uint maxPixelSize = 256,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ImageLoadResult? loadResult = null;
        try
        {
            loadResult = await ImageDocumentLoader.LoadAsync(path, cancellationToken);
            if (loadResult.Document.HasRenderableGainMap)
            {
                var inputs = await DecodeGainMapThumbnailInputsAsync(
                    loadResult.Document,
                    checked((int)Math.Min(maxPixelSize, int.MaxValue)),
                    cancellationToken);
                return await CreateToneMappedThumbnailAsync(inputs, cancellationToken);
            }

            if (IsHdrThumbnailSource(loadResult.Document))
            {
                var bitmap = await BitmapDecodeService.DecodeDocumentForThumbnailAsync(
                    loadResult.Document,
                    checked((int)Math.Min(maxPixelSize, int.MaxValue)),
                    cancellationToken);
                if (bitmap.IsHdrEncoded)
                {
                    return await CreateToneMappedThumbnailAsync(bitmap, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }

        // SDR images (and any failure above) use the Windows shell thumbnail:
        // it comes from the OS thumbnail cache, so repeat visits avoid decoding
        // the original file at all. BitmapImage with DecodePixelWidth stays as
        // the fallback when the shell cannot produce a thumbnail.
        return await TryCreateShellThumbnailAsync(path, maxPixelSize, cancellationToken)
            ?? TryCreateUriThumbnail(path, maxPixelSize);
    }

    private static async Task<ImageSource?> TryCreateShellThumbnailAsync(
        string path,
        uint maxPixelSize,
        CancellationToken cancellationToken)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            cancellationToken.ThrowIfCancellationRequested();
            using var thumbnail = await file.GetThumbnailAsync(
                ThumbnailMode.PicturesView,
                maxPixelSize,
                ThumbnailOptions.UseCurrentScale);
            cancellationToken.ThrowIfCancellationRequested();
            if (thumbnail is null || thumbnail.Size == 0)
            {
                return null;
            }

            var source = new BitmapImage();
            await source.SetSourceAsync(thumbnail);
            return source;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsHdrThumbnailSource(HdrImageDocument document)
    {
        return document.Format.Kind == HdrImageKind.SingleLayerHdr
            || document.HeifAvifProbe?.HasHdrTransfer == true
            || document.JxlProbe?.IsHdrTransfer == true
            || document.ExrProbe?.IsOpenExr == true;
    }

    private static Task<GainMapRenderInputs> DecodeGainMapThumbnailInputsAsync(
        HdrImageDocument document,
        int maxPixelSize,
        CancellationToken cancellationToken)
    {
        return GainMapRenderInputDecoder.DecodeRenderInputsAsync(document, maxPixelSize, cancellationToken);
    }

    private static async Task<ImageSource?> CreateToneMappedThumbnailAsync(
        GainMapRenderInputs inputs,
        CancellationToken cancellationToken)
    {
        var pixels = await Task.Run(() => ConvertGainMapToBgra8(inputs, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var source = new WriteableBitmap(inputs.Primary.PixelWidth, inputs.Primary.PixelHeight);
        using (var stream = source.PixelBuffer.AsStream())
        {
            await stream.WriteAsync(pixels.AsMemory(0, pixels.Length), cancellationToken);
        }

        source.Invalidate();
        return source;
    }

    private static async Task<ImageSource?> CreateToneMappedThumbnailAsync(
        DecodedBitmap bitmap,
        CancellationToken cancellationToken)
    {
        var pixels = await Task.Run(() => ConvertHdrToBgra8(bitmap, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var source = new WriteableBitmap(bitmap.PixelWidth, bitmap.PixelHeight);
        using (var stream = source.PixelBuffer.AsStream())
        {
            await stream.WriteAsync(pixels.AsMemory(0, pixels.Length), cancellationToken);
        }

        source.Invalidate();
        return source;
    }

    private static async Task<ImageSource?> CreateSdrThumbnailAsync(
        DecodedBitmap bitmap,
        CancellationToken cancellationToken)
    {
        var pixels = await Task.Run(() => ConvertSdrToBgra8(bitmap, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var source = new WriteableBitmap(bitmap.PixelWidth, bitmap.PixelHeight);
        using (var stream = source.PixelBuffer.AsStream())
        {
            await stream.WriteAsync(pixels.AsMemory(0, pixels.Length), cancellationToken);
        }

        source.Invalidate();
        return source;
    }

    private static byte[] ConvertHdrToBgra8(DecodedBitmap bitmap, CancellationToken cancellationToken)
    {
        var result = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
        var destination = 0;
        for (var y = 0; y < bitmap.PixelHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < bitmap.PixelWidth; x++)
            {
                var linear = DecodeSceneLinearBt709(bitmap, x, y);
                var mapped = ToneMapToSdr(linear);
                result[destination++] = ToByte(LinearToSrgb(mapped.Z));
                result[destination++] = ToByte(LinearToSrgb(mapped.Y));
                result[destination++] = ToByte(LinearToSrgb(mapped.X));
                result[destination++] = 255;
            }
        }

        return result;
    }

    private static byte[] ConvertGainMapToBgra8(GainMapRenderInputs inputs, CancellationToken cancellationToken)
    {
        var result = new byte[checked(inputs.Primary.PixelWidth * inputs.Primary.PixelHeight * 4)];
        var destination = 0;
        for (var y = 0; y < inputs.Primary.PixelHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < inputs.Primary.PixelWidth; x++)
            {
                var linear = DecodeGainMapSceneLinearBt709(inputs, x, y);
                var mapped = ToneMapToSdr(linear);
                result[destination++] = ToByte(LinearToSrgb(mapped.Z));
                result[destination++] = ToByte(LinearToSrgb(mapped.Y));
                result[destination++] = ToByte(LinearToSrgb(mapped.X));
                result[destination++] = 255;
            }
        }

        return result;
    }

    private static byte[] ConvertSdrToBgra8(DecodedBitmap bitmap, CancellationToken cancellationToken)
    {
        var result = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
        var destination = 0;
        for (var y = 0; y < bitmap.PixelHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < bitmap.PixelWidth; x++)
            {
                var encoded = Vector3.Clamp(ReadEncodedRgb(bitmap, x, y), Vector3.Zero, Vector3.One);
                result[destination++] = ToByte(encoded.Z);
                result[destination++] = ToByte(encoded.Y);
                result[destination++] = ToByte(encoded.X);
                result[destination++] = 255;
            }
        }

        return result;
    }

    private static Vector3 DecodeSceneLinearBt709(DecodedBitmap bitmap, int x, int y)
    {
        var encoded = ReadEncodedRgb(bitmap, x, y);
        var linear = bitmap.Transfer switch
        {
            DecodedBitmapTransfer.Pq => HdrColorMath.PqToSceneLinear(encoded),
            DecodedBitmapTransfer.Hlg => HdrColorMath.HlgToSceneLinear(encoded, HlgThumbnailTargetScenePeak),
            DecodedBitmapTransfer.LinearScRgb => encoded,
            DecodedBitmapTransfer.LinearSceneScRgb => encoded,
            _ => HdrColorMath.SrgbToLinear(encoded),
        };

        return bitmap.UsesBt2020Primaries && bitmap.Transfer is not (DecodedBitmapTransfer.LinearScRgb or DecodedBitmapTransfer.LinearSceneScRgb)
            ? HdrColorMath.Bt2020ToBt709(linear)
            : linear;
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

    private static Vector3 ReadLinearSrgb(DecodedBitmap bitmap, int x, int y)
    {
        return HdrColorMath.SrgbToLinear(ReadEncodedRgb(bitmap, x, y));
    }

    private static Vector3 ReadGainMapSample(DecodedBitmap bitmap, int primaryX, int primaryY, int primaryWidth, int primaryHeight)
    {
        var x = Math.Clamp((int)((primaryX + 0.5f) * bitmap.PixelWidth / Math.Max(primaryWidth, 1)), 0, bitmap.PixelWidth - 1);
        var y = Math.Clamp((int)((primaryY + 0.5f) * bitmap.PixelHeight / Math.Max(primaryHeight, 1)), 0, bitmap.PixelHeight - 1);
        return ReadEncodedRgb(bitmap, x, y);
    }

    private static float LinearToSrgb(float value)
    {
        value = Math.Clamp(value, 0.0f, 1.0f);
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

    private static ImageSource? TryCreateUriThumbnail(string path, uint maxPixelSize)
    {
        try
        {
            return new BitmapImage
            {
                DecodePixelWidth = (int)Math.Min(maxPixelSize, (uint)int.MaxValue),
                UriSource = new Uri(path),
            };
        }
        catch
        {
            return null;
        }
    }
}
