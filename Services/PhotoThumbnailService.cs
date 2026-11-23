using System.Runtime.InteropServices.WindowsRuntime;
using HdrImageViewer.Models;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace HdrImageViewer.Services;

public static class PhotoThumbnailService
{
    internal static async Task<(uint Width, uint Height)?> TryGetOrientedSizeAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CloudFilePolicy.ShouldSkipBackgroundRead(path))
            {
                return null;
            }

            await using var fileStream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16 * 1024,
                useAsync: true);
            using var stream = fileStream.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            cancellationToken.ThrowIfCancellationRequested();
            return (decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
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

    public static async Task<ImageSource?> CreateAsync(
        string path,
        uint maxPixelSize = 256,
        CancellationToken cancellationToken = default)
    {
        return await CreateHdrToneMappedAsync(path, maxPixelSize, cancellationToken)
            ?? await CreateQuickAsync(path, maxPixelSize, cancellationToken);
    }

    public static async Task<ImageSource?> CreateHdrToneMappedAsync(
        string path,
        uint maxPixelSize = 256,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CloudFilePolicy.ShouldSkipBackgroundRead(path))
        {
            return null;
        }

        // Fast-skip cold ordinary JPEG/WebP files when we already ran _quickLoad:
        // only probe if metadata is already cached or the file could be HDR / wide-gamut / JXL / EXR / HEIF.
        var ext = Path.GetExtension(path);
        if (string.Equals(ext, ".webp", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var loadResult = await ImagePreloadCache.GetLoadResultAsync(
                path,
                cancellationToken);
            return await CreateHdrToneMappedAsync(
                loadResult,
                maxPixelSize,
                cancellationToken);
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

    internal static async Task<ImageSource?> CreateHdrToneMappedAsync(
        ImageLoadResult loadResult,
        uint maxPixelSize,
        CancellationToken cancellationToken)
    {
        var document = loadResult.Document;
        var clampedMax = checked((int)Math.Clamp(maxPixelSize, 1u, int.MaxValue));
        if (document.HasRenderableGainMap)
        {
            if (ImagePreloadCache.TryGetGainMapInputs(document.Path, loadResult.LastWriteTimeUtc, clampedMax, out var cachedInputs))
            {
                var scaledPrimary = BitmapPreviewResampler.Downscale(cachedInputs.Primary, clampedMax, cancellationToken);
                return await CreateToneMappedThumbnailAsync(
                    new GainMapBaseThumbnailInput(scaledPrimary, cachedInputs.Constants),
                    cancellationToken);
            }

            var baseInput = await GainMapRenderInputDecoder.DecodeBaseForThumbnailAsync(
                document,
                clampedMax,
                cancellationToken);
            return await CreateToneMappedThumbnailAsync(baseInput, cancellationToken);
        }

        var bitmap = await DecodeDocumentThumbnailAsync(document, maxPixelSize, cancellationToken, loadResult.LastWriteTimeUtc);
        if (bitmap is not null)
        {
            if (bitmap.IsHdrEncoded)
            {
                return await CreateToneMappedThumbnailAsync(bitmap, cancellationToken);
            }

            return await CreateSdrThumbnailAsync(bitmap, cancellationToken);
        }

        return null;
    }

    internal static async Task<DecodedBitmap?> DecodeDocumentThumbnailAsync(
        HdrImageDocument document,
        uint maxPixelSize,
        CancellationToken cancellationToken,
        DateTime? knownWriteTimeUtc = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ThumbnailDecodePolicy.RequiresPixelDecode(document))
        {
            return null;
        }

        var clampedMax = checked((int)Math.Clamp(maxPixelSize, 1u, int.MaxValue));
        if (knownWriteTimeUtc is { } writeTimeUtc
            && ImagePreloadCache.TryGetBaseBitmap(document.Path, writeTimeUtc, clampedMax, out var preloaded))
        {
            return BitmapPreviewResampler.Downscale(preloaded, clampedMax, cancellationToken);
        }

        return await BitmapDecodeService.DecodeDocumentForThumbnailAsync(document, clampedMax, cancellationToken);
    }

    public static async Task<ImageSource?> CreateQuickAsync(
        string path,
        uint maxPixelSize = 256,
        CancellationToken cancellationToken = default)
    {
        var (thumbnail, _, _) = await CreateQuickWithSizeAsync(path, maxPixelSize, cancellationToken);
        return thumbnail;
    }

    public static async Task<(ImageSource? Thumbnail, int Width, int Height)> CreateQuickWithSizeAsync(
        string path,
        uint requestedSize = 256,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CloudFilePolicy.ShouldSkipBackgroundRead(path))
        {
            var cloudThumb = await TryCreateShellThumbnailAsync(path, requestedSize, cancellationToken);
            return (cloudThumb, 0, 0);
        }

        var streamResult = await TryCreateStreamThumbnailWithSizeAsync(path, requestedSize, cancellationToken);
        if (streamResult.Thumbnail is not null)
        {
            return streamResult;
        }

        var shellThumb = await TryCreateShellThumbnailAsync(path, requestedSize, cancellationToken);
        return (shellThumb, streamResult.Width, streamResult.Height);
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
            if (thumbnail is null || thumbnail.Size == 0 || thumbnail.Type != ThumbnailType.Image)
            {
                return null;
            }

            var source = new BitmapImage();
            await source.SetSourceAsync(thumbnail);
            cancellationToken.ThrowIfCancellationRequested();
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

    private static async Task<ImageSource?> CreateToneMappedThumbnailAsync(
        GainMapBaseThumbnailInput input,
        CancellationToken cancellationToken)
    {
        var pixels = await Task.Run(
            () => ThumbnailPixelConverter.ConvertGainMapBaseThumbnailToBgra8(input, cancellationToken),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var orientation = new ExifOrientationTransform(
            input.Primary.PixelWidth, input.Primary.PixelHeight, (int)input.Constants.Orientation.X);
        var source = new WriteableBitmap(orientation.Width, orientation.Height);
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
        var pixels = await Task.Run(() => ThumbnailPixelConverter.ConvertHdrPreviewToBgra8(bitmap, cancellationToken), cancellationToken);
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
        var pixels = await Task.Run(() => ThumbnailPixelConverter.ConvertSdrToBgra8(bitmap, cancellationToken, premultiplyAlpha: true), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var source = new WriteableBitmap(bitmap.PixelWidth, bitmap.PixelHeight);
        using (var stream = source.PixelBuffer.AsStream())
        {
            await stream.WriteAsync(pixels.AsMemory(0, pixels.Length), cancellationToken);
        }

        source.Invalidate();
        return source;
    }

    private static async Task<(ImageSource? Thumbnail, int Width, int Height)> TryCreateStreamThumbnailWithSizeAsync(
        string path, uint maxPixelSize, CancellationToken cancellationToken)
    {
        var orientedWidth = 0;
        var orientedHeight = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var fileStream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                useAsync: true);
            using var stream = fileStream.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            cancellationToken.ThrowIfCancellationRequested();
            var width = decoder.OrientedPixelWidth;
            var height = decoder.OrientedPixelHeight;
            orientedWidth = checked((int)width);
            orientedHeight = checked((int)height);
            var scale = Math.Min(1.0, Math.Max(1u, maxPixelSize) / (double)Math.Max(width, height));
            var source = new BitmapImage
            {
                DecodePixelWidth = Math.Max(1, checked((int)Math.Round(width * scale))),
                DecodePixelHeight = Math.Max(1, checked((int)Math.Round(height * scale))),
            };
            stream.Seek(0);
            await source.SetSourceAsync(stream);
            cancellationToken.ThrowIfCancellationRequested();
            return (source, orientedWidth, orientedHeight);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (null, orientedWidth, orientedHeight);
        }
    }
}
