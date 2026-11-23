using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HdrImageViewer.Services;

internal static class BitmapPreviewResampler
{
    private const int ParallelPixelThreshold = 4096;

    internal static DecodedBitmap Downscale(
        DecodedBitmap bitmap,
        int? maxPixelSize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryCalculateScaledSize(bitmap.PixelWidth, bitmap.PixelHeight, maxPixelSize, out var scaledWidth, out var scaledHeight))
        {
            return bitmap;
        }

        var timer = Stopwatch.StartNew();
        var sourceWidth = bitmap.PixelWidth;
        var sourceHeight = bitmap.PixelHeight;
        var destinationWidth = checked((int)scaledWidth);
        var destinationHeight = checked((int)scaledHeight);
        var bytesPerPixel = bitmap.BytesPerPixel;
        var sourceBytes = bitmap.RgbaPixels;
        var destination = new byte[checked(destinationWidth * destinationHeight * bytesPerPixel)];

        var sourceXStart = ArrayPool<int>.Shared.Rent(destinationWidth);
        var sourceXEnd = ArrayPool<int>.Shared.Rent(destinationWidth);
        try
        {
            for (var x = 0; x < destinationWidth; x++)
            {
                var x0 = Math.Min(sourceWidth - 1, (int)((long)x * sourceWidth / destinationWidth));
                var x1 = Math.Clamp(Math.Max(x0 + 1, (int)(((long)(x + 1) * sourceWidth) / destinationWidth)), x0 + 1, sourceWidth);
                sourceXStart[x] = x0;
                sourceXEnd[x] = x1;
            }

            var pixelFormat = bitmap.PixelFormat;
            if (destinationWidth * destinationHeight >= ParallelPixelThreshold)
            {
                Parallel.For(0, destinationHeight, new ParallelOptions { CancellationToken = cancellationToken }, y =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ResampleRowBox(sourceBytes, destination, sourceWidth, sourceHeight, destinationWidth, destinationHeight, bytesPerPixel, pixelFormat, sourceXStart, sourceXEnd, y);
                });
            }
            else
            {
                for (var y = 0; y < destinationHeight; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ResampleRowBox(sourceBytes, destination, sourceWidth, sourceHeight, destinationWidth, destinationHeight, bytesPerPixel, pixelFormat, sourceXStart, sourceXEnd, y);
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(sourceXStart);
            ArrayPool<int>.Shared.Return(sourceXEnd);
        }

        var scaleMs = timer.ElapsedMilliseconds;
        return bitmap with
        {
            PixelWidth = destinationWidth,
            PixelHeight = destinationHeight,
            RgbaPixels = destination,
            DecoderName = $"{bitmap.DecoderName} [preview box-downscale {sourceWidth}x{sourceHeight}->{destinationWidth}x{destinationHeight} {scaleMs}ms]",
        };
    }

    private static void ResampleRowBox(
        byte[] sourceBytes,
        byte[] destinationBytes,
        int sourceWidth,
        int sourceHeight,
        int destinationWidth,
        int destinationHeight,
        int bytesPerPixel,
        DecodedBitmapPixelFormat pixelFormat,
        int[] sourceXStart,
        int[] sourceXEnd,
        int y)
    {
        var y0 = Math.Min(sourceHeight - 1, (int)((long)y * sourceHeight / destinationHeight));
        var y1 = Math.Clamp(Math.Max(y0 + 1, (int)(((long)(y + 1) * sourceHeight) / destinationHeight)), y0 + 1, sourceHeight);

        if (bytesPerPixel == 4 && pixelFormat == DecodedBitmapPixelFormat.Rgba8Unorm)
        {
            var dstOffset = y * destinationWidth * 4;
            for (var x = 0; x < destinationWidth; x++)
            {
                var x0 = sourceXStart[x];
                var x1 = sourceXEnd[x];
                long sumR = 0, sumG = 0, sumB = 0, sumA = 0;
                var count = (y1 - y0) * (x1 - x0);
                for (var sy = y0; sy < y1; sy++)
                {
                    var srcOffset = ((sy * sourceWidth) + x0) * 4;
                    for (var sx = x0; sx < x1; sx++)
                    {
                        long alpha = sourceBytes[srcOffset + 3];
                        sumR += sourceBytes[srcOffset] * alpha;
                        sumG += sourceBytes[srcOffset + 1] * alpha;
                        sumB += sourceBytes[srcOffset + 2] * alpha;
                        sumA += alpha;
                        srcOffset += 4;
                    }
                }

                var half = count >> 1;
                destinationBytes[dstOffset] = sumA == 0 ? (byte)0 : (byte)((sumR + sumA / 2) / sumA);
                destinationBytes[dstOffset + 1] = sumA == 0 ? (byte)0 : (byte)((sumG + sumA / 2) / sumA);
                destinationBytes[dstOffset + 2] = sumA == 0 ? (byte)0 : (byte)((sumB + sumA / 2) / sumA);
                destinationBytes[dstOffset + 3] = (byte)((sumA + half) / count);
                dstOffset += 4;
            }

            return;
        }

        if (bytesPerPixel == 8 && pixelFormat == DecodedBitmapPixelFormat.Rgba16Unorm)
        {
            var srcU16 = MemoryMarshal.Cast<byte, ushort>(sourceBytes.AsSpan());
            var dstU16 = MemoryMarshal.Cast<byte, ushort>(destinationBytes.AsSpan(y * destinationWidth * 8, destinationWidth * 8));
            for (var x = 0; x < destinationWidth; x++)
            {
                var x0 = sourceXStart[x];
                var x1 = sourceXEnd[x];
                long sumR = 0, sumG = 0, sumB = 0, sumA = 0;
                var count = (long)(y1 - y0) * (x1 - x0);
                for (var sy = y0; sy < y1; sy++)
                {
                    var srcIdx = ((sy * sourceWidth) + x0) * 4;
                    for (var sx = x0; sx < x1; sx++)
                    {
                        long alpha = srcU16[srcIdx + 3];
                        sumR += srcU16[srcIdx] * alpha;
                        sumG += srcU16[srcIdx + 1] * alpha;
                        sumB += srcU16[srcIdx + 2] * alpha;
                        sumA += alpha;
                        srcIdx += 4;
                    }
                }

                var half = count >> 1;
                var dstIdx = x * 4;
                dstU16[dstIdx] = sumA == 0 ? (ushort)0 : (ushort)((sumR + sumA / 2) / sumA);
                dstU16[dstIdx + 1] = sumA == 0 ? (ushort)0 : (ushort)((sumG + sumA / 2) / sumA);
                dstU16[dstIdx + 2] = sumA == 0 ? (ushort)0 : (ushort)((sumB + sumA / 2) / sumA);
                dstU16[dstIdx + 3] = (ushort)((sumA + half) / count);
            }

            return;
        }

        if (bytesPerPixel == 8 && pixelFormat == DecodedBitmapPixelFormat.Rgba16Float)
        {
            var srcU16 = MemoryMarshal.Cast<byte, ushort>(sourceBytes.AsSpan());
            var dstU16 = MemoryMarshal.Cast<byte, ushort>(destinationBytes.AsSpan(y * destinationWidth * 8, destinationWidth * 8));
            for (var x = 0; x < destinationWidth; x++)
            {
                var x0 = sourceXStart[x];
                var x1 = sourceXEnd[x];
                double sumR = 0, sumG = 0, sumB = 0, sumA = 0;
                var count = (y1 - y0) * (x1 - x0);
                for (var sy = y0; sy < y1; sy++)
                {
                    var srcIdx = ((sy * sourceWidth) + x0) * 4;
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var alpha = Math.Clamp(ReadFiniteHalf(srcU16[srcIdx + 3], 1.0f), 0.0f, 1.0f);
                        sumR += ReadFiniteHalf(srcU16[srcIdx], 0.0f) * alpha;
                        sumG += ReadFiniteHalf(srcU16[srcIdx + 1], 0.0f) * alpha;
                        sumB += ReadFiniteHalf(srcU16[srcIdx + 2], 0.0f) * alpha;
                        sumA += alpha;
                        srcIdx += 4;
                    }
                }

                var dstIdx = x * 4;
                dstU16[dstIdx] = BitConverter.HalfToUInt16Bits((Half)(sumA > 0 ? sumR / sumA : 0));
                dstU16[dstIdx + 1] = BitConverter.HalfToUInt16Bits((Half)(sumA > 0 ? sumG / sumA : 0));
                dstU16[dstIdx + 2] = BitConverter.HalfToUInt16Bits((Half)(sumA > 0 ? sumB / sumA : 0));
                dstU16[dstIdx + 3] = BitConverter.HalfToUInt16Bits((Half)(sumA / count));
            }

            return;
        }

        var sourceRow = checked(y0 * sourceWidth * bytesPerPixel);
        var destinationRow = checked(y * destinationWidth * bytesPerPixel);
        var srcSpan = sourceBytes.AsSpan();
        var dstSpan = destinationBytes.AsSpan();
        for (var x = 0; x < destinationWidth; x++)
        {
            srcSpan.Slice(sourceRow + (sourceXStart[x] * bytesPerPixel), bytesPerPixel)
                .CopyTo(dstSpan.Slice(destinationRow + (x * bytesPerPixel), bytesPerPixel));
        }
    }

    private static float ReadFiniteHalf(ushort bits, float nanFallback)
    {
        if ((bits & 0x7C00) == 0x7C00)
        {
            return (bits & 0x03FF) != 0 ? nanFallback : ((bits & 0x8000) != 0 ? -65504.0f : 65504.0f);
        }

        return (float)BitConverter.UInt16BitsToHalf(bits);
    }

    internal static bool TryCalculateScaledSize(
        int width,
        int height,
        int? maxPixelSize,
        out uint scaledWidth,
        out uint scaledHeight)
    {
        scaledWidth = 0;
        scaledHeight = 0;
        if (maxPixelSize is null || maxPixelSize <= 0 || width <= 0 || height <= 0)
        {
            return false;
        }

        var largerSide = Math.Max(width, height);
        if (largerSide <= maxPixelSize.Value)
        {
            return false;
        }

        var scale = maxPixelSize.Value / (double)largerSide;
        scaledWidth = Math.Max(1u, (uint)Math.Round(width * scale));
        scaledHeight = Math.Max(1u, (uint)Math.Round(height * scale));
        return true;
    }
}
