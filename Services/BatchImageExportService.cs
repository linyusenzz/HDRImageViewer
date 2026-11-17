namespace HdrImageViewer.Services;

public static class BatchImageExportService
{
    public static async Task<string> ExportAsync(BatchExportItem item, string directory, int format, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(format);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(format, 9);

        var extension = format switch
        {
            1 or 2 => ".jpg",
            5 => ".tif",
            6 => ".exr",
            7 => Path.GetExtension(item.SourcePath),
            8 => ".heic",
            9 => ".avif",
            _ => ".png",
        };
        var output = BatchExportController.ChooseOutputPath(directory, item.SourcePath, extension);
        if (format == 7)
        {
            await ExportFileTransaction.CopyAsync(item.SourcePath, output, token, overwrite: false);
            return output;
        }

        var load = await ImagePreloadCache.GetLoadResultAsync(item.SourcePath, token);
        if (format <= 1)
        {
            await SdrImageExportService.ExportAsync(load.Document, output, format == 1, token);
        }
        else if (format is 2 or 8 or 9)
        {
            await GainMapHdrExportService.ExportAsync(load.Document, output, cancellationToken: token);
        }
        else
        {
            await SingleLayerHdrExportService.ExportAsync(
                load.Document,
                output,
                format == 4 ? SingleLayerHdrExportTransfer.Hlg : SingleLayerHdrExportTransfer.Pq,
                token);
        }

        return output;
    }
}
