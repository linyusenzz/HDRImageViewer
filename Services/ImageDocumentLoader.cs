using HdrImageViewer.Models;

namespace HdrImageViewer.Services;

public static class ImageDocumentLoader
{
    public static async Task<ImageLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        GainMapProbeResult? gainMapProbe = null;
        HeifAvifProbeResult? heifAvifProbe = null;
        var containerKind = await FileSignatureProbe.DetectAsync(path, cancellationToken);
        if (containerKind == FileContainerKind.Jpeg || DecoderCatalog.IsJpegExtension(Path.GetExtension(path)))
        {
            gainMapProbe = await GainMapJpegProbe.ProbeAsync(path, cancellationToken);
        }
        else if (containerKind == FileContainerKind.HeifFamily || HeifAvifProbe.IsHeifFamilyExtension(Path.GetExtension(path)))
        {
            heifAvifProbe = await HeifAvifProbe.ProbeAsync(path, cancellationToken);
        }

        var descriptor = DecoderCatalog.Describe(path, gainMapProbe, heifAvifProbe, containerKind);
        var document = new HdrImageDocument(path, Path.GetFileName(path), descriptor, gainMapProbe, heifAvifProbe);
        var exifSummary = await ExifMetadataReader.ReadSummaryAsync(path, cancellationToken);
        return new ImageLoadResult(document, exifSummary, File.GetLastWriteTimeUtc(path));
    }
}
