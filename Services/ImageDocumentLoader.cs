using HdrImageViewer.Models;

namespace HdrImageViewer.Services;

public static class ImageDocumentLoader
{
    public static async Task<ImageLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (await DirectoryMetadataCache.TryLoadAsync(path, cancellationToken) is { } cached)
        {
            return cached;
        }

        GainMapProbeResult? gainMapProbe = null;
        HeifAvifProbeResult? heifAvifProbe = null;
        JxlProbeResult? jxlProbe = null;
        WicImageProbeResult? wicImageProbe = null;
        ExrProbeResult? exrProbe = null;
        var containerKind = await FileSignatureProbe.DetectAsync(path, cancellationToken);
        if (containerKind == FileContainerKind.Jpeg || DecoderCatalog.IsJpegExtension(Path.GetExtension(path)))
        {
            gainMapProbe = await GainMapJpegProbe.ProbeAsync(path, cancellationToken);
        }
        else if (containerKind == FileContainerKind.HeifFamily || HeifAvifProbe.IsHeifFamilyExtension(Path.GetExtension(path)))
        {
            heifAvifProbe = await HeifAvifProbe.ProbeAsync(path, cancellationToken);
        }
        else if (JxlProbe.IsJxlExtension(Path.GetExtension(path)))
        {
            jxlProbe = await JxlProbe.ProbeAsync(path, cancellationToken);
        }
        else if (ExrProbe.IsExrExtension(Path.GetExtension(path)))
        {
            exrProbe = await ExrProbe.ProbeAsync(path, cancellationToken);
        }
        else if (WicImageProbe.IsWicProbeExtension(Path.GetExtension(path)))
        {
            wicImageProbe = await WicImageProbe.ProbeAsync(path, cancellationToken);
        }

        var descriptor = DecoderCatalog.Describe(path, gainMapProbe, heifAvifProbe, jxlProbe, wicImageProbe, exrProbe, containerKind);
        var document = new HdrImageDocument(path, Path.GetFileName(path), descriptor, gainMapProbe, heifAvifProbe, jxlProbe, wicImageProbe, exrProbe);
        var exifSummary = await ExifMetadataReader.ReadSummaryAsync(path, cancellationToken);
        var result = new ImageLoadResult(document, exifSummary, File.GetLastWriteTimeUtc(path));
        await DirectoryMetadataCache.StoreAsync(result, containerKind, cancellationToken);
        return result;
    }
}
