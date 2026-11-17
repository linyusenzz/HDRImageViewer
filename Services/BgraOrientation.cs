using System.Runtime.InteropServices;

namespace HdrImageViewer.Services;

internal static class BgraOrientation
{
    private const int ParallelPixelThreshold = 16_384;

    public static (byte[] Pixels, int Width, int Height) Apply(
        byte[] source,
        int width,
        int height,
        int orientation,
        CancellationToken cancellationToken)
    {
        if (source.Length != checked(width * height * 4))
        {
            throw new ArgumentException("Invalid BGRA dimensions.");
        }

        if (orientation is < 2 or > 8)
        {
            return (source, width, height);
        }

        var transform = new ExifOrientationTransform(width, height, orientation);
        var outputWidth = transform.Width;
        var outputHeight = transform.Height;
        var output = new byte[source.Length];

        if ((long)outputWidth * outputHeight >= ParallelPixelThreshold)
        {
            Parallel.For(0, outputHeight, new ParallelOptions { CancellationToken = cancellationToken }, y =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var srcPixels = MemoryMarshal.Cast<byte, uint>(source.AsSpan());
                var dstRow = MemoryMarshal.Cast<byte, uint>(output.AsSpan(y * outputWidth * 4, outputWidth * 4));
                for (var x = 0; x < outputWidth; x++)
                {
                    var (sx, sy) = transform.MapToSource(x, y);
                    dstRow[x] = srcPixels[(sy * width) + sx];
                }
            });
        }
        else
        {
            var srcPixels = MemoryMarshal.Cast<byte, uint>(source.AsSpan());
            var dstPixels = MemoryMarshal.Cast<byte, uint>(output.AsSpan());
            for (var y = 0; y < outputHeight; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rowOffset = y * outputWidth;
                for (var x = 0; x < outputWidth; x++)
                {
                    var (sx, sy) = transform.MapToSource(x, y);
                    dstPixels[rowOffset + x] = srcPixels[(sy * width) + sx];
                }
            }
        }

        return (output, outputWidth, outputHeight);
    }
}
