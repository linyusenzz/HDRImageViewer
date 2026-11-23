using HdrImageViewer.Models;

namespace HdrImageViewer.Services;

public static class ImageDocumentLoader
{
    private const long MaxPreloadedJpegBytes = 64L * 1024 * 1024;

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
        RadianceHdrHeader? radianceHdrProbe = null;
        byte[]? preloadedContainerBytes = null;

        var extension = Path.GetExtension(path);
        var (containerKind, preloadedJpeg) = await DetectAndPreloadJpegIfApplicableAsync(path, extension, cancellationToken);
        if (containerKind == FileContainerKind.Jpeg || DecoderCatalog.IsJpegExtension(extension))
        {
            if (preloadedJpeg is not null)
            {
                preloadedContainerBytes = preloadedJpeg;
                gainMapProbe = await Task.Run(() => GainMapJpegProbe.Probe(preloadedContainerBytes), cancellationToken);
            }
            else
            {
                (gainMapProbe, preloadedContainerBytes) = await GainMapJpegProbe.ProbeWithBytesAsync(path, cancellationToken);
            }
        }
        else if (containerKind == FileContainerKind.HeifFamily || HeifAvifProbe.IsHeifFamilyExtension(extension))
        {
            heifAvifProbe = await HeifAvifProbe.ProbeAsync(path, cancellationToken);
        }
        else if (containerKind == FileContainerKind.Jxl || JxlProbe.IsJxlExtension(extension))
        {
            jxlProbe = await JxlProbe.ProbeAsync(path, forceSignatureDetected: containerKind == FileContainerKind.Jxl, cancellationToken);
        }
        else if (ExrProbe.IsExrExtension(extension))
        {
            exrProbe = await ExrProbe.ProbeAsync(path, cancellationToken);
        }
        else if (containerKind == FileContainerKind.RadianceHdr || RadianceHdrDecoder.IsRadianceHdrExtension(extension))
        {
            radianceHdrProbe = RadianceHdrDecoder.TryProbeFile(path);
        }
        else if (WicImageProbe.IsWicProbeExtension(extension))
        {
            wicImageProbe = await WicImageProbe.ProbeAsync(path, cancellationToken);
        }

        var descriptor = DecoderCatalog.Describe(
            path,
            gainMapProbe,
            heifAvifProbe,
            jxlProbe,
            wicImageProbe,
            exrProbe,
            containerKind,
            radianceHdrProbe);
        var companionMedia = await LivePhotoProbe.ProbeAsync(path, containerKind, preloadedContainerBytes, cancellationToken);
        var document = new HdrImageDocument(
            path,
            Path.GetFileName(path),
            descriptor,
            gainMapProbe,
            heifAvifProbe,
            jxlProbe,
            wicImageProbe,
            exrProbe,
            companionMedia,
            radianceHdrProbe,
            containerKind);
        var exifSummary = await ExifMetadataReader.ReadSummaryAsync(path, preloadedContainerBytes, cancellationToken);
        var result = new ImageLoadResult(document, exifSummary, File.GetLastWriteTimeUtc(path));
        await DirectoryMetadataCache.StoreAsync(result, containerKind, cancellationToken);
        return result;
    }

    private static async Task<(FileContainerKind Kind, byte[]? PreloadedJpegBytes)> DetectAndPreloadJpegIfApplicableAsync(
        string path,
        string extension,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            useAsync: true);

        var fileLength = stream.Length;
        if (fileLength <= 0)
        {
            return (FileContainerKind.Unknown, null);
        }

        var headerLength = (int)Math.Min(fileLength, 512);
        var header = new byte[headerLength];
        var headerRead = await ReadUpToAsync(stream, header, cancellationToken);
        var kind = FileSignatureProbe.Detect(header.AsSpan(0, headerRead));

        if (kind == FileContainerKind.Jpeg && fileLength <= MaxPreloadedJpegBytes)
        {
            var fullBytes = new byte[(int)fileLength];
            Buffer.BlockCopy(header, 0, fullBytes, 0, headerRead);
            if (headerRead < fullBytes.Length)
            {
                await stream.ReadExactlyAsync(fullBytes.AsMemory(headerRead, fullBytes.Length - headerRead), cancellationToken);
            }

            return (kind, fullBytes);
        }

        if (kind == FileContainerKind.Unknown && DecoderCatalog.IsJpegExtension(extension))
        {
            return (kind, header[..headerRead]);
        }

        return (kind, null);
    }

    private static async Task<int> ReadUpToAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
