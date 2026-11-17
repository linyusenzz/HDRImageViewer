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

        var sourceXMap = ArrayPool<int>.Shared.Rent(destinationWidth);
        try
        {
            for (var x = 0; x < destinationWidth; x++)
            {
                sourceXMap[x] = Math.Min(sourceWidth - 1, (int)((long)x * sourceWidth / destinationWidth));
            }

            if (destinationWidth * destinationHeight >= ParallelPixelThreshold)
            {
                Parallel.For(0, destinationHeight, new ParallelOptions { CancellationToken = cancellationToken }, y =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ResampleRow(sourceBytes, destination, sourceWidth, sourceHeight, destinationWidth, destinationHeight, bytesPerPixel, sourceXMap, y);
                });
            }
            else
            {
                for (var y = 0; y < destinationHeight; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ResampleRow(sourceBytes, destination, sourceWidth, sourceHeight, destinationWidth, destinationHeight, bytesPerPixel, sourceXMap, y);
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(sourceXMap);
        }

        var scaleMs = timer.ElapsedMilliseconds;
        return bitmap with
        {
            PixelWidth = destinationWidth,
            PixelHeight = destinationHeight,
            RgbaPixels = destination,
            DecoderName = $"{bitmap.DecoderName} [preview downscale {sourceWidth}x{sourceHeight}->{destinationWidth}x{destinationHeight} {scaleMs}ms]",
        };
    }

    private static void ResampleRow(
        byte[] sourceBytes,
        byte[] destinationBytes,
        int sourceWidth,
        int sourceHeight,
        int destinationWidth,
        int destinationHeight,
        int bytesPerPixel,
        int[] sourceXMap,
        int y)
    {
        var sourceY = Math.Min(sourceHeight - 1, (int)((long)y * sourceHeight / destinationHeight));
        if (bytesPerPixel == 4)
        {
            var srcPixels = MemoryMarshal.Cast<byte, uint>(sourceBytes.AsSpan(sourceY * sourceWidth * 4, sourceWidth * 4));
            var dstPixels = MemoryMarshal.Cast<byte, uint>(destinationBytes.AsSpan(y * destinationWidth * 4, destinationWidth * 4));
            for (var x = 0; x < destinationWidth; x++)
            {
                dstPixels[x] = srcPixels[sourceXMap[x]];
            }

            return;
        }

        if (bytesPerPixel == 8)
        {
            var srcPixels = MemoryMarshal.Cast<byte, ulong>(sourceBytes.AsSpan(sourceY * sourceWidth * 8, sourceWidth * 8));
            var dstPixels = MemoryMarshal.Cast<byte, ulong>(destinationBytes.AsSpan(y * destinationWidth * 8, destinationWidth * 8));
            for (var x = 0; x < destinationWidth; x++)
            {
                dstPixels[x] = srcPixels[sourceXMap[x]];
            }

            return;
        }

        var sourceRow = checked(sourceY * sourceWidth * bytesPerPixel);
        var destinationRow = checked(y * destinationWidth * bytesPerPixel);
        var srcSpan = sourceBytes.AsSpan();
        var dstSpan = destinationBytes.AsSpan();
        for (var x = 0; x < destinationWidth; x++)
        {
            srcSpan.Slice(sourceRow + (sourceXMap[x] * bytesPerPixel), bytesPerPixel)
                .CopyTo(dstSpan.Slice(destinationRow + (x * bytesPerPixel), bytesPerPixel));
        }
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
