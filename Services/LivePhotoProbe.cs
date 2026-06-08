using System.Globalization;
using System.Text;
using System.Xml.Linq;
using HdrImageViewer.Models;

namespace HdrImageViewer.Services;

public static class LivePhotoProbe
{
    private const string XmpHeader = "http://ns.adobe.com/xap/1.0/";
    private static readonly string[] SidecarVideoExtensions = [".mov", ".mp4", ".m4v"];

    public static async Task<CompanionMedia?> ProbeAsync(
        string path,
        FileContainerKind containerKind,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path) || !IsStillImageCandidate(path, containerKind))
        {
            return null;
        }

        if (containerKind == FileContainerKind.Jpeg || DecoderCatalog.IsJpegExtension(Path.GetExtension(path)))
        {
            var embedded = await ProbeEmbeddedJpegMotionPhotoAsync(path, cancellationToken);
            if (embedded is not null)
            {
                return embedded;
            }
        }

        return await ProbeSidecarVideoAsync(path, containerKind, cancellationToken);
    }

    private static bool IsStillImageCandidate(string path, FileContainerKind containerKind)
    {
        if (containerKind is FileContainerKind.Jpeg or FileContainerKind.HeifFamily)
        {
            return true;
        }

        var extension = Path.GetExtension(path);
        return DecoderCatalog.FileTypeFilter.Any(
            filter => string.Equals(filter, extension, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<CompanionMedia?> ProbeSidecarVideoAsync(
        string path,
        FileContainerKind containerKind,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var stem = Path.GetFileNameWithoutExtension(path);
        foreach (var extension in SidecarVideoExtensions)
        {
            var candidate = Path.Combine(directory, stem + extension);
            if (!File.Exists(candidate))
            {
                continue;
            }

            var kind = string.Equals(extension, ".mov", StringComparison.OrdinalIgnoreCase)
                && containerKind is FileContainerKind.Jpeg or FileContainerKind.HeifFamily
                    ? CompanionMediaKind.AppleLivePhoto
                    : CompanionMediaKind.SidecarVideo;
            var source = kind == CompanionMediaKind.AppleLivePhoto
                ? "Apple Live Photo sidecar video"
                : "same-name companion video";

            var media = new CompanionMedia(kind, candidate, "动态", source);
            var videoProbe = await CompanionVideoProbe.ProbeAsync(media, cancellationToken);
            return media with { VideoProbe = videoProbe };
        }

        return null;
    }

    private static async Task<CompanionMedia?> ProbeEmbeddedJpegMotionPhotoAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists || fileInfo.Length <= 0)
        {
            return null;
        }

        var packets = await ReadJpegXmpPacketsAsync(path, cancellationToken);
        foreach (var packet in packets)
        {
            if (TryParseXmp(packet) is not { } document)
            {
                continue;
            }

            var embedded = await TryCreateEmbeddedMotionPhotoAsync(path, fileInfo.Length, document, cancellationToken);
            if (embedded is not null)
            {
                return embedded;
            }
        }

        return null;
    }

    private static async Task<CompanionMedia?> TryCreateEmbeddedMotionPhotoAsync(
        string path,
        long fileLength,
        XDocument document,
        CancellationToken cancellationToken)
    {
        if (TryReadContainerMotionPhotoRange(document, fileLength) is { } containerRange
            && await LooksLikeIsoBaseMediaFileAsync(path, containerRange.Offset, cancellationToken))
        {
            var media = new CompanionMedia(
                CompanionMediaKind.AndroidMotionPhoto,
                path,
                "动态",
                "Android Motion Photo embedded video",
                containerRange.Offset,
                containerRange.Length);
            var videoProbe = await CompanionVideoProbe.ProbeAsync(media, cancellationToken);
            return media with { VideoProbe = videoProbe };
        }

        if (TryReadMicroVideoRange(document, fileLength) is { } microVideoRange
            && await LooksLikeIsoBaseMediaFileAsync(path, microVideoRange.Offset, cancellationToken))
        {
            var media = new CompanionMedia(
                CompanionMediaKind.AndroidMotionPhoto,
                path,
                "动态",
                "Google Camera MicroVideo embedded video",
                microVideoRange.Offset,
                microVideoRange.Length);
            var videoProbe = await CompanionVideoProbe.ProbeAsync(media, cancellationToken);
            return media with { VideoProbe = videoProbe };
        }

        return null;
    }

    private static EmbeddedRange? TryReadContainerMotionPhotoRange(XDocument document, long fileLength)
    {
        foreach (var item in document.Descendants().Where(element => element.Name.LocalName == "Item"))
        {
            var semantic = GetAttributeValue(item, "Semantic");
            var mime = GetAttributeValue(item, "Mime");
            var isMotionItem =
                string.Equals(semantic, "MotionPhoto", StringComparison.OrdinalIgnoreCase)
                || string.Equals(semantic, "MotionPhotoVideo", StringComparison.OrdinalIgnoreCase)
                || mime?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true;
            if (!isMotionItem)
            {
                continue;
            }

            var length = TryParseLong(GetAttributeValue(item, "Length"));
            if (length is not > 0)
            {
                continue;
            }

            var padding = TryParseLong(GetAttributeValue(item, "Padding")) ?? 0L;
            var offset = fileLength - padding - length.Value;
            if (IsValidRange(fileLength, offset, length.Value))
            {
                return new EmbeddedRange(offset, length.Value);
            }
        }

        return null;
    }

    private static EmbeddedRange? TryReadMicroVideoRange(XDocument document, long fileLength)
    {
        var hasMotionSignal = false;
        long? offsetFromEnd = null;

        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes())
            {
                var value = attribute.Value;
                switch (attribute.Name.LocalName)
                {
                    case "MotionPhoto":
                    case "MicroVideo":
                        hasMotionSignal |= value is "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "MicroVideoOffset":
                    case "MotionPhotoVideoOffset":
                        offsetFromEnd ??= TryParseLong(value);
                        break;
                }
            }
        }

        if (!hasMotionSignal || offsetFromEnd is not > 0)
        {
            return null;
        }

        var offset = fileLength - offsetFromEnd.Value;
        return IsValidRange(fileLength, offset, offsetFromEnd.Value)
            ? new EmbeddedRange(offset, offsetFromEnd.Value)
            : null;
    }

    private static async Task<IReadOnlyList<string>> ReadJpegXmpPacketsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var packets = new List<string>();
        var lengthBytes = new byte[2];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            useAsync: true);

        if (stream.Length < 4 || await ReadByteAsync(stream, cancellationToken) != 0xFF || await ReadByteAsync(stream, cancellationToken) != 0xD8)
        {
            return packets;
        }

        while (stream.Position + 4 <= stream.Length)
        {
            var markerPrefix = await ReadByteAsync(stream, cancellationToken);
            if (markerPrefix != 0xFF)
            {
                break;
            }

            int marker;
            do
            {
                marker = await ReadByteAsync(stream, cancellationToken);
            }
            while (marker == 0xFF && stream.Position < stream.Length);

            if (marker is 0xD9 or 0xDA)
            {
                break;
            }

            if (IsStandaloneMarker(marker))
            {
                continue;
            }

            if (await stream.ReadAsync(lengthBytes, cancellationToken) != lengthBytes.Length)
            {
                break;
            }

            var segmentLength = (lengthBytes[0] << 8) | lengthBytes[1];
            if (segmentLength < 2 || stream.Position + segmentLength - 2 > stream.Length)
            {
                break;
            }

            var payloadLength = segmentLength - 2;
            if (marker == 0xE1 && payloadLength > 0)
            {
                var payload = new byte[payloadLength];
                await stream.ReadExactlyAsync(payload, cancellationToken);
                if (TryReadXmpPacket(payload) is { Length: > 0 } packet)
                {
                    packets.Add(packet);
                }
            }
            else
            {
                stream.Seek(payloadLength, SeekOrigin.Current);
            }
        }

        return packets;
    }

    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        return await stream.ReadAsync(buffer, cancellationToken) == 1 ? buffer[0] : -1;
    }

    private static bool IsStandaloneMarker(int marker)
    {
        return marker is 0x01 or >= 0xD0 and <= 0xD7;
    }

    private static string? TryReadXmpPacket(ReadOnlySpan<byte> payload)
    {
        var text = Encoding.UTF8.GetString(payload);
        if (!text.Contains(XmpHeader, StringComparison.Ordinal) && !text.Contains("<rdf:RDF", StringComparison.Ordinal))
        {
            return null;
        }

        var start = IndexOfXmlStart(text);
        if (start < 0)
        {
            return null;
        }

        var end = IndexOfXmlEnd(text, start);
        return end > start ? text[start..end] : text[start..].TrimEnd('\0');
    }

    private static XDocument? TryParseXmp(string packet)
    {
        try
        {
            return XDocument.Parse(packet, LoadOptions.None);
        }
        catch
        {
            return null;
        }
    }

    private static int IndexOfXmlStart(string text)
    {
        var xmpStart = text.IndexOf("<x:xmpmeta", StringComparison.Ordinal);
        return xmpStart >= 0 ? xmpStart : text.IndexOf("<rdf:RDF", StringComparison.Ordinal);
    }

    private static int IndexOfXmlEnd(string text, int start)
    {
        var xmpEnd = text.IndexOf("</x:xmpmeta>", start, StringComparison.Ordinal);
        if (xmpEnd >= 0)
        {
            return xmpEnd + "</x:xmpmeta>".Length;
        }

        var rdfEnd = text.IndexOf("</rdf:RDF>", start, StringComparison.Ordinal);
        return rdfEnd >= 0 ? rdfEnd + "</rdf:RDF>".Length : -1;
    }

    private static string? GetAttributeValue(XElement element, string localName)
    {
        return element
            .Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == localName)
            ?.Value;
    }

    private static long? TryParseLong(string? value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool IsValidRange(long fileLength, long offset, long length)
    {
        return offset >= 0 && length > 0 && offset + length <= fileLength;
    }

    private static async Task<bool> LooksLikeIsoBaseMediaFileAsync(
        string path,
        long offset,
        CancellationToken cancellationToken)
    {
        if (offset < 0)
        {
            return false;
        }

        var header = new byte[16];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            useAsync: true);
        if (offset + 12 > stream.Length)
        {
            return false;
        }

        stream.Seek(offset, SeekOrigin.Begin);
        var bytesRead = await stream.ReadAsync(header, cancellationToken);
        return bytesRead >= 12
            && header[4] == (byte)'f'
            && header[5] == (byte)'t'
            && header[6] == (byte)'y'
            && header[7] == (byte)'p';
    }

    private sealed record EmbeddedRange(long Offset, long Length);
}
