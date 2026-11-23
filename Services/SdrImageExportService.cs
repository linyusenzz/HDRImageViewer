using HdrImageViewer.Models;
using Windows.Graphics.Imaging;

namespace HdrImageViewer.Services;

internal static class SdrImageExportService
{
    public static async Task ExportAsync(HdrImageDocument document, string destination, bool jpeg, CancellationToken cancellationToken)
    {
        byte[] pixels;
        int width;
        int height;
        var preserveAlpha = !jpeg;
        var alphaMode = BitmapAlphaMode.Ignore;
        if (document.HasRenderableGainMap)
        {
            var inputs = await GainMapRenderInputDecoder.DecodeRenderInputsAsync(document, null, cancellationToken);
            var orientation = new ExifOrientationTransform(
                inputs.Primary.PixelWidth, inputs.Primary.PixelHeight, (int)inputs.Constants.Orientation.X);
            width = orientation.Width;
            height = orientation.Height;
            pixels = await Task.Run(() => ThumbnailPixelConverter.ConvertGainMapToBgra8(inputs, cancellationToken), cancellationToken);
        }
        else
        {
            var bitmap = preserveAlpha
                ? await BitmapDecodeService.DecodeDocumentAsync(document, maxPixelSize: null, cancellationToken)
                : await BitmapDecodeService.DecodeDocumentForHdrExportAsync(document, cancellationToken);
            width = bitmap.PixelWidth;
            height = bitmap.PixelHeight;
            alphaMode = preserveAlpha ? BitmapAlphaMode.Premultiplied : BitmapAlphaMode.Ignore;
            pixels = await Task.Run(() => bitmap.IsHdrEncoded
                ? ThumbnailPixelConverter.ConvertHdrToBgra8(bitmap, cancellationToken, premultiplyAlpha: preserveAlpha)
                : ThumbnailPixelConverter.ConvertSdrToBgra8(bitmap, cancellationToken, premultiplyAlpha: preserveAlpha), cancellationToken);
        }

        using var transaction = new ExportFileTransaction(destination);
        await using (var stream = new FileStream(transaction.TemporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, bufferSize: 1024 * 1024, useAsync: true))
        {
            using var random = stream.AsRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(jpeg ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId, random);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, alphaMode, (uint)width, (uint)height, 96, 96, pixels);
            await encoder.FlushAsync();
        }

        transaction.Commit(cancellationToken);
    }
}
