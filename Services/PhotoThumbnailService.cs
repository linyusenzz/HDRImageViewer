using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace HdrImageViewer.Services;

public static class PhotoThumbnailService
{
    public static async Task<ImageSource?> CreateAsync(
        string path,
        uint maxPixelSize = 256,
        CancellationToken cancellationToken = default)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        cancellationToken.ThrowIfCancellationRequested();

        var decodedThumbnail = await TryCreateColorManagedThumbnailAsync(file, maxPixelSize, cancellationToken);
        if (decodedThumbnail is not null)
        {
            return decodedThumbnail;
        }

        var shellThumbnail = await TryCreateShellThumbnailAsync(file, maxPixelSize, cancellationToken);
        if (shellThumbnail is not null)
        {
            return shellThumbnail;
        }

        return TryCreateUriThumbnail(path, maxPixelSize);
    }

    private static async Task<ImageSource?> TryCreateColorManagedThumbnailAsync(
        StorageFile file,
        uint maxPixelSize,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = await file.OpenReadAsync();
            cancellationToken.ThrowIfCancellationRequested();

            var decoder = await BitmapDecoder.CreateAsync(stream);
            var largerSide = Math.Max(decoder.PixelWidth, decoder.PixelHeight);
            if (largerSide == 0)
            {
                return null;
            }

            var scale = Math.Min(1.0, maxPixelSize / (double)largerSide);
            var transform = new BitmapTransform
            {
                ScaledWidth = Math.Max(1u, (uint)Math.Round(decoder.PixelWidth * scale)),
                ScaledHeight = Math.Max(1u, (uint)Math.Round(decoder.PixelHeight * scale)),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };

            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);
            cancellationToken.ThrowIfCancellationRequested();

            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(bitmap);
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

    private static async Task<ImageSource?> TryCreateShellThumbnailAsync(
        StorageFile file,
        uint maxPixelSize,
        CancellationToken cancellationToken)
    {
        try
        {
            using var thumbnailStream = await file.GetThumbnailAsync(
                ThumbnailMode.PicturesView,
                maxPixelSize,
                ThumbnailOptions.UseCurrentScale);
            cancellationToken.ThrowIfCancellationRequested();

            if (thumbnailStream is null || thumbnailStream.Size == 0)
            {
                return null;
            }

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumbnailStream);
            return bitmap;
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
