using System.Globalization;
using System.Text;
using System.Xml.Linq;
using HdrImageViewer.Models;

namespace HdrImageViewer.Services;

public static class GainMapJpegProbe
{
    private const string XmpHeader = "http://ns.adobe.com/xap/1.0/";
    private const string HdrGainMapNamespace = "http://ns.adobe.com/hdr-gain-map/1.0/";
    private const string AppleHdrGainMapNamespace = "http://ns.apple.com/HDRGainMap/1.0/";
    private const string AppleHdrGainMapAuxiliaryType = "urn:com:apple:photo:2020:aux:hdrgainmap";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

    public static async Task<GainMapProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        var data = await File.ReadAllBytesAsync(path, cancellationToken);
        return Probe(data);
    }

    public static GainMapProbeResult Probe(ReadOnlySpan<byte> data)
    {
        if (!StartsWithJpegSoi(data))
        {
            return new GainMapProbeResult(false, false, false, false, false, false, false, null, false, GainMapColorGamut.Unknown, null, null, null, null);
        }

        var primary = ScanJpeg(data, 0);
        if (!primary.IsValid)
        {
            return new GainMapProbeResult(true, false, false, false, false, false, false, null, false, GainMapColorGamut.Unknown, null, null, null, null);
        }

        var primaryPackets = primary.XmpPackets.Select(ParseXmp).Where(packet => packet is not null).Cast<XDocument>().ToList();
        var hasUltraHdrSignal = primaryPackets.Any(HasUltraHdrVersion);
        var hasAppleHdrGainMapSignal = primary.HasAppleHdrGainMapMetadata || primaryPackets.Any(HasAppleHdrGainMap);
        var hasGContainerDirectory = primaryPackets.Any(HasContainerDirectory);
        var hasGainMapItem = primaryPackets.Any(HasGainMapItem);
        var hasIso21496Signal = primary.HasIso21496Metadata
            || ContainsAscii(data, "21496-1")
            || ContainsAscii(data, "iso:std:iso:ts:21496");
        var appleHeadroom = primary.AppleHdrHeadroom ?? TryReadAppleXmpHeadroom(primaryPackets);

        var gainMapOffset = FindNextJpegStart(data, primary.EndOffset);
        GainMapMetadata? metadata = ExtractMetadata(primaryPackets);
        int? gainMapLength = null;
        var hasGainMapImage = false;
        var primaryImageEndOffset = primary.EndOffset;

        if (gainMapOffset >= 0)
        {
            var gainMap = ScanJpeg(data, gainMapOffset);
            if (gainMap.IsValid)
            {
                hasGainMapImage = true;
                gainMapLength = gainMap.EndOffset - gainMap.StartOffset;
                primaryImageEndOffset = gainMapOffset;
                hasAppleHdrGainMapSignal |= gainMap.HasAppleHdrGainMapMetadata;

                var gainMapPackets = gainMap.XmpPackets.Select(ParseXmp).Where(packet => packet is not null).Cast<XDocument>().ToList();
                hasUltraHdrSignal |= gainMapPackets.Any(HasUltraHdrVersion);
                hasAppleHdrGainMapSignal |= gainMapPackets.Any(HasAppleHdrGainMap);
                appleHeadroom ??= gainMap.AppleHdrHeadroom ?? TryReadAppleXmpHeadroom(gainMapPackets);
                metadata = ExtractMetadata(gainMapPackets) ?? metadata;
            }
        }

        if (metadata is null && hasAppleHdrGainMapSignal && hasGainMapImage)
        {
            metadata = CreateAppleHdrGainMapMetadata(appleHeadroom ?? TryFindAppleMakerNoteHeadroom(data));
        }

        if (metadata is not null && hasIso21496Signal)
        {
            metadata = metadata with { Source = $"{metadata.Source}; ISO 21496-1 present" };
        }

        return new GainMapProbeResult(
            true,
            hasUltraHdrSignal,
            hasGContainerDirectory,
            hasGainMapItem,
            hasGainMapImage,
            hasIso21496Signal,
            hasAppleHdrGainMapSignal,
            primary.ExifOrientation,
            primary.HasIccProfile,
            primary.IccColorGamut,
            primaryImageEndOffset,
            gainMapOffset >= 0 ? gainMapOffset : null,
            gainMapLength,
            metadata);
    }

    private static bool StartsWithJpegSoi(ReadOnlySpan<byte> data)
    {
        return data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8;
    }

    private static JpegScan ScanJpeg(ReadOnlySpan<byte> data, int startOffset)
    {
        if (startOffset < 0 || startOffset + 1 >= data.Length || data[startOffset] != 0xFF || data[startOffset + 1] != 0xD8)
        {
            return JpegScan.Invalid(startOffset);
        }

        var packets = new List<string>();
        int? exifOrientation = null;
        var hasIccProfile = false;
        var hasIso21496Metadata = false;
        var hasAppleHdrGainMapMetadata = false;
        AppleHdrHeadroom? appleHdrHeadroom = null;
        var iccChunks = new List<IccChunk>();
        var offset = startOffset + 2;

        while (offset + 1 < data.Length)
        {
            if (data[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            while (offset < data.Length && data[offset] == 0xFF)
            {
                offset++;
            }

            if (offset >= data.Length)
            {
                break;
            }

            var marker = data[offset++];
            if (marker == 0xD9)
            {
                return new JpegScan(true, startOffset, offset, packets, exifOrientation, hasIccProfile, DetectIccColorGamut(iccChunks), hasIso21496Metadata, hasAppleHdrGainMapMetadata, appleHdrHeadroom);
            }

            if (marker == 0xDA)
            {
                if (offset + 2 > data.Length)
                {
                    break;
                }

                var scanHeaderLength = ReadBigEndianUInt16(data, offset);
                offset += scanHeaderLength;
                return ScanEntropyForEnd(data, startOffset, offset, packets, exifOrientation, hasIccProfile, DetectIccColorGamut(iccChunks), hasIso21496Metadata, hasAppleHdrGainMapMetadata, appleHdrHeadroom);
            }

            if (IsStandaloneMarker(marker))
            {
                continue;
            }

            if (offset + 2 > data.Length)
            {
                break;
            }

            var segmentLength = ReadBigEndianUInt16(data, offset);
            if (segmentLength < 2 || offset + segmentLength > data.Length)
            {
                break;
            }

            var payload = data.Slice(offset + 2, segmentLength - 2);
            if (marker == 0xE1)
            {
                var xmp = TryReadXmpPacket(payload);
                if (!string.IsNullOrWhiteSpace(xmp))
                {
                    packets.Add(xmp);
                    hasAppleHdrGainMapMetadata |= ContainsAscii(payload, AppleHdrGainMapNamespace)
                        || ContainsAscii(payload, AppleHdrGainMapAuxiliaryType);
                }

                exifOrientation ??= TryReadExifOrientation(payload);
                appleHdrHeadroom ??= TryReadAppleHdrHeadroom(payload);
            }
            else if (marker == 0xE2)
            {
                if (TryReadIccChunk(payload) is { } iccChunk)
                {
                    hasIccProfile = true;
                    iccChunks.Add(iccChunk);
                }

                hasIso21496Metadata |= ContainsAscii(payload, "21496-1")
                    || ContainsAscii(payload, "iso:std:iso:ts:21496");
            }
            else if (marker is >= 0xE0 and <= 0xEF)
            {
                hasIso21496Metadata |= ContainsAscii(payload, "21496-1")
                    || ContainsAscii(payload, "iso:std:iso:ts:21496");
            }

            offset += segmentLength;
        }

        return JpegScan.Invalid(startOffset);
    }

    private static JpegScan ScanEntropyForEnd(
        ReadOnlySpan<byte> data,
        int startOffset,
        int entropyOffset,
        List<string> packets,
        int? exifOrientation,
        bool hasIccProfile,
        GainMapColorGamut iccColorGamut,
        bool hasIso21496Metadata,
        bool hasAppleHdrGainMapMetadata,
        AppleHdrHeadroom? appleHdrHeadroom)
    {
        var offset = entropyOffset;
        while (offset + 1 < data.Length)
        {
            if (data[offset++] != 0xFF)
            {
                continue;
            }

            if (data[offset] == 0x00)
            {
                offset++;
                continue;
            }

            while (offset < data.Length && data[offset] == 0xFF)
            {
                offset++;
            }

            if (offset >= data.Length)
            {
                break;
            }

            var marker = data[offset++];
            if (marker == 0xD9)
            {
                return new JpegScan(true, startOffset, offset, packets, exifOrientation, hasIccProfile, iccColorGamut, hasIso21496Metadata, hasAppleHdrGainMapMetadata, appleHdrHeadroom);
            }
        }

        return JpegScan.Invalid(startOffset);
    }

    private static bool IsStandaloneMarker(byte marker)
    {
        return marker is 0x01 or >= 0xD0 and <= 0xD7;
    }

    private static int ReadBigEndianUInt16(ReadOnlySpan<byte> data, int offset)
    {
        return (data[offset] << 8) | data[offset + 1];
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

    private static int? TryReadExifOrientation(ReadOnlySpan<byte> payload)
    {
        if (!StartsWithAscii(payload, "Exif\0\0") || payload.Length < 14)
        {
            return null;
        }

        var tiff = payload[6..];
        var littleEndian = tiff[0] == 0x49 && tiff[1] == 0x49;
        var bigEndian = tiff[0] == 0x4D && tiff[1] == 0x4D;
        if (!littleEndian && !bigEndian)
        {
            return null;
        }

        if (ReadUInt16(tiff, 2, littleEndian) != 42)
        {
            return null;
        }

        var ifdOffset = (int)ReadUInt32(tiff, 4, littleEndian);
        if (ifdOffset < 0 || ifdOffset + 2 > tiff.Length)
        {
            return null;
        }

        var entryCount = ReadUInt16(tiff, ifdOffset, littleEndian);
        var entryOffset = ifdOffset + 2;
        for (var index = 0; index < entryCount; index++)
        {
            if (entryOffset + 12 > tiff.Length)
            {
                return null;
            }

            var tag = ReadUInt16(tiff, entryOffset, littleEndian);
            if (tag == 0x0112)
            {
                var type = ReadUInt16(tiff, entryOffset + 2, littleEndian);
                var count = ReadUInt32(tiff, entryOffset + 4, littleEndian);
                if (type == 3 && count == 1)
                {
                    var orientation = ReadUInt16(tiff, entryOffset + 8, littleEndian);
                    return orientation is >= 1 and <= 8 ? orientation : null;
                }

                return null;
            }

            entryOffset += 12;
        }

        return null;
    }

    private static AppleHdrHeadroom? TryReadAppleHdrHeadroom(ReadOnlySpan<byte> payload)
    {
        var makerNote = TryReadExifUndefinedTag(payload, 0x927C);
        if (makerNote.IsEmpty)
        {
            return null;
        }

        return TryReadAppleMakerNoteHeadroom(makerNote);
    }

    private static ReadOnlySpan<byte> TryReadExifUndefinedTag(ReadOnlySpan<byte> payload, ushort requestedTag)
    {
        if (!StartsWithAscii(payload, "Exif\0\0") || payload.Length < 14)
        {
            return default;
        }

        var tiff = payload[6..];
        var littleEndian = tiff[0] == 0x49 && tiff[1] == 0x49;
        var bigEndian = tiff[0] == 0x4D && tiff[1] == 0x4D;
        if (!littleEndian && !bigEndian || ReadUInt16(tiff, 2, littleEndian) != 42)
        {
            return default;
        }

        var ifdOffset = (int)ReadUInt32(tiff, 4, littleEndian);
        var exifIfdOffset = TryReadIfdOffset(tiff, ifdOffset, 0x8769, littleEndian);
        if (exifIfdOffset is null)
        {
            return default;
        }

        return TryReadIfdByteValue(tiff, exifIfdOffset.Value, requestedTag, littleEndian);
    }

    private static int? TryReadIfdOffset(ReadOnlySpan<byte> tiff, int ifdOffset, ushort requestedTag, bool littleEndian)
    {
        if (ifdOffset < 0 || ifdOffset + 2 > tiff.Length)
        {
            return null;
        }

        var entryCount = ReadUInt16(tiff, ifdOffset, littleEndian);
        var entryOffset = ifdOffset + 2;
        for (var index = 0; index < entryCount; index++)
        {
            if (entryOffset + 12 > tiff.Length)
            {
                return null;
            }

            var tag = ReadUInt16(tiff, entryOffset, littleEndian);
            if (tag == requestedTag)
            {
                return (int)ReadUInt32(tiff, entryOffset + 8, littleEndian);
            }

            entryOffset += 12;
        }

        return null;
    }

    private static ReadOnlySpan<byte> TryReadIfdByteValue(ReadOnlySpan<byte> tiff, int ifdOffset, ushort requestedTag, bool littleEndian)
    {
        if (ifdOffset < 0 || ifdOffset + 2 > tiff.Length)
        {
            return default;
        }

        var entryCount = ReadUInt16(tiff, ifdOffset, littleEndian);
        var entryOffset = ifdOffset + 2;
        for (var index = 0; index < entryCount; index++)
        {
            if (entryOffset + 12 > tiff.Length)
            {
                return default;
            }

            var tag = ReadUInt16(tiff, entryOffset, littleEndian);
            if (tag == requestedTag)
            {
                var type = ReadUInt16(tiff, entryOffset + 2, littleEndian);
                var count = (int)ReadUInt32(tiff, entryOffset + 4, littleEndian);
                var valueOffset = count <= 4 ? entryOffset + 8 : (int)ReadUInt32(tiff, entryOffset + 8, littleEndian);
                if (type == 7 && count > 0 && valueOffset >= 0 && valueOffset + count <= tiff.Length)
                {
                    return tiff.Slice(valueOffset, count);
                }

                return default;
            }

            entryOffset += 12;
        }

        return default;
    }

    private static AppleHdrHeadroom? TryReadAppleMakerNoteHeadroom(ReadOnlySpan<byte> makerNote)
    {
        if (!StartsWithAscii(makerNote, "Apple iOS\0\0\u0001") || makerNote.Length < 16)
        {
            return null;
        }

        var baseOffset = 12;
        var littleEndian = makerNote[baseOffset] == 0x49 && makerNote[baseOffset + 1] == 0x49;
        var bigEndian = makerNote[baseOffset] == 0x4D && makerNote[baseOffset + 1] == 0x4D;
        if (!littleEndian && !bigEndian)
        {
            return null;
        }

        double? maker33 = null;
        double? maker48 = null;
        var entryCount = ReadUInt16(makerNote, baseOffset + 2, littleEndian);
        var entryOffset = baseOffset + 4;
        for (var index = 0; index < entryCount; index++)
        {
            if (entryOffset + 12 > makerNote.Length)
            {
                return null;
            }

            var tag = ReadUInt16(makerNote, entryOffset, littleEndian);
            if (tag is 0x0021 or 0x0030)
            {
                var type = ReadUInt16(makerNote, entryOffset + 2, littleEndian);
                var count = ReadUInt32(makerNote, entryOffset + 4, littleEndian);
                var value = TryReadAppleMakerNoteNumber(makerNote, entryOffset, type, count, littleEndian);
                if (tag == 0x0021)
                {
                    maker33 = value;
                }
                else
                {
                    maker48 = value;
                }
            }

            entryOffset += 12;
        }

        return CreateAppleHeadroomFromMakerNote(maker33, maker48);
    }

    private static AppleHdrHeadroom? TryFindAppleMakerNoteHeadroom(ReadOnlySpan<byte> data)
    {
        var marker = Encoding.ASCII.GetBytes("Apple iOS\0\0\u0001");
        for (var index = 0; index <= data.Length - marker.Length; index++)
        {
            var matches = true;
            for (var markerIndex = 0; markerIndex < marker.Length; markerIndex++)
            {
                if (data[index + markerIndex] != marker[markerIndex])
                {
                    matches = false;
                    break;
                }
            }

            if (!matches)
            {
                continue;
            }

            var available = Math.Min(256, data.Length - index);
            var headroom = TryReadAppleMakerNoteHeadroom(data.Slice(index, available));
            if (headroom?.Headroom is > 1.0)
            {
                return headroom;
            }
        }

        return null;
    }

    private static double? TryReadAppleMakerNoteNumber(
        ReadOnlySpan<byte> makerNote,
        int entryOffset,
        ushort type,
        uint count,
        bool littleEndian)
    {
        if (count == 0)
        {
            return null;
        }

        return type switch
        {
            3 => ReadUInt16(makerNote, entryOffset + 8, littleEndian),
            4 => ReadUInt32(makerNote, entryOffset + 8, littleEndian),
            5 => TryReadRational(makerNote, (int)ReadUInt32(makerNote, entryOffset + 8, littleEndian), littleEndian, signed: false),
            10 => TryReadRational(makerNote, (int)ReadUInt32(makerNote, entryOffset + 8, littleEndian), littleEndian, signed: true),
            11 => ReadSingle(makerNote, entryOffset + 8, littleEndian),
            12 => TryReadDouble(makerNote, (int)ReadUInt32(makerNote, entryOffset + 8, littleEndian), littleEndian),
            _ => null,
        };
    }

    private static double? TryReadRational(ReadOnlySpan<byte> data, int offset, bool littleEndian, bool signed)
    {
        if (offset < 0 || offset + 8 > data.Length)
        {
            return null;
        }

        var numerator = signed
            ? (double)ReadInt32(data, offset, littleEndian)
            : ReadUInt32(data, offset, littleEndian);
        var denominator = signed
            ? (double)ReadInt32(data, offset + 4, littleEndian)
            : ReadUInt32(data, offset + 4, littleEndian);
        return denominator == 0 ? null : Math.Abs((double)numerator / denominator);
    }

    private static double? TryReadDouble(ReadOnlySpan<byte> data, int offset, bool littleEndian)
    {
        if (offset < 0 || offset + 8 > data.Length)
        {
            return null;
        }

        var low = ReadUInt32(data, littleEndian ? offset : offset + 4, littleEndian);
        var high = ReadUInt32(data, littleEndian ? offset + 4 : offset, littleEndian);
        var bits = ((ulong)high << 32) | low;
        var value = BitConverter.UInt64BitsToDouble(bits);
        return double.IsFinite(value) ? value : null;
    }

    private static AppleHdrHeadroom? CreateAppleHeadroomFromMakerNote(double? maker33, double? maker48)
    {
        if (maker33 is not { } value33)
        {
            return null;
        }

        var stops = maker48 is { } value48
            ? value33 >= 1.0 && value48 <= 0.01
                ? (-70.0 * value48) + 3.0
                : (0.8 * value33) + 1.0
            : value33;

        return CreateAppleHeadroom(Math.Pow(2.0, stops), stops, $"MakerNote 33={value33:0.###}, 48={(maker48?.ToString("0.###", CultureInfo.InvariantCulture) ?? "none")}");
    }

    private static AppleHdrHeadroom? CreateAppleHeadroom(double headroom, double stops, string source)
    {
        if (!double.IsFinite(headroom) || headroom <= 1.0)
        {
            return null;
        }

        var normalizedHeadroom = Math.Clamp(headroom, 1.0, 64.0);
        return new AppleHdrHeadroom(normalizedHeadroom, Math.Log2(normalizedHeadroom), source);
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset, bool littleEndian)
    {
        return littleEndian
            ? (ushort)(data[offset] | (data[offset + 1] << 8))
            : (ushort)((data[offset] << 8) | data[offset + 1]);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset, bool littleEndian)
    {
        return littleEndian
            ? (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24))
            : (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, int offset, bool littleEndian)
    {
        return unchecked((int)ReadUInt32(data, offset, littleEndian));
    }

    private static float ReadSingle(ReadOnlySpan<byte> data, int offset, bool littleEndian)
    {
        return BitConverter.Int32BitsToSingle(ReadInt32(data, offset, littleEndian));
    }

    private static int IndexOfXmlStart(string text)
    {
        var xmpStart = text.IndexOf("<x:xmpmeta", StringComparison.Ordinal);
        if (xmpStart >= 0)
        {
            return xmpStart;
        }

        return text.IndexOf("<rdf:RDF", StringComparison.Ordinal);
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

    private static XDocument? ParseXmp(string packet)
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

    private static bool HasUltraHdrVersion(XDocument document)
    {
        return document
            .Descendants()
            .Any(element => GetAttributeValue(element, "Version", HdrGainMapNamespace) == "1.0");
    }

    private static bool HasAppleHdrGainMap(XDocument document)
    {
        return document
            .Descendants()
            .Any(element =>
                element.Name.NamespaceName == AppleHdrGainMapNamespace
                || string.Equals(GetAttributeValue(element, "AuxiliaryImageType"), AppleHdrGainMapAuxiliaryType, StringComparison.OrdinalIgnoreCase));
    }

    private static AppleHdrHeadroom? TryReadAppleXmpHeadroom(IEnumerable<XDocument> documents)
    {
        foreach (var document in documents)
        {
            foreach (var element in document.Descendants())
            {
                var rawHeadroom = GetAttributeValue(element, "HDRGainMapHeadroom", AppleHdrGainMapNamespace)
                    ?? GetAttributeValue(element, "HDRGainMapHeadroom")
                    ?? (element.Name.LocalName == "HDRGainMapHeadroom" ? element.Value : null);
                if (double.TryParse(rawHeadroom, NumberStyles.Float, CultureInfo.InvariantCulture, out var headroom))
                {
                    return CreateAppleHeadroom(headroom, Math.Log2(Math.Max(headroom, 1.0)), "XMP HDRGainMapHeadroom");
                }
            }
        }

        return null;
    }

    private static bool HasContainerDirectory(XDocument document)
    {
        return document.Descendants().Any(element => element.Name.LocalName == "Directory");
    }

    private static bool HasGainMapItem(XDocument document)
    {
        return document
            .Descendants()
            .Where(element => element.Name.LocalName == "Item")
            .Any(element => string.Equals(GetAttributeValue(element, "Semantic"), "GainMap", StringComparison.OrdinalIgnoreCase));
    }

    private static GainMapMetadata? ExtractMetadata(IEnumerable<XDocument> documents)
    {
        foreach (var document in documents)
        {
            foreach (var description in document.Descendants(Rdf + "Description"))
            {
                var version = GetAttributeValue(description, "Version", HdrGainMapNamespace);
                if (string.IsNullOrWhiteSpace(version))
                {
                    continue;
                }

                return new GainMapMetadata(
                    version,
                    GetGainMapValue(description, "GainMapMin"),
                    GetGainMapValue(description, "GainMapMax"),
                    GetGainMapValue(description, "Gamma"),
                    GetGainMapValue(description, "OffsetSDR"),
                    GetGainMapValue(description, "OffsetHDR"),
                    GetGainMapValue(description, "HDRCapacityMin"),
                    GetGainMapValue(description, "HDRCapacityMax"),
                    TryParseBoolean(GetGainMapValue(description, "BaseRenditionIsHDR")));
            }
        }

        return null;
    }

    private static GainMapMetadata CreateAppleHdrGainMapMetadata(AppleHdrHeadroom? headroom)
    {
        var normalizedHeadroom = Math.Clamp(headroom?.Headroom ?? 2.0, 1.0, 64.0);
        var capacity = Math.Log2(normalizedHeadroom);
        var source = headroom is null
            ? $"Apple HDRGainMap (default headroom {normalizedHeadroom:0.###}x)"
            : $"Apple HDRGainMap (headroom {normalizedHeadroom:0.###}x, {capacity:0.###} stops from {headroom.Source})";
        return new GainMapMetadata(
            "1.0",
            "0",
            normalizedHeadroom.ToString("0.###", CultureInfo.InvariantCulture),
            "1",
            "0",
            "0",
            "0",
            capacity.ToString("0.###", CultureInfo.InvariantCulture),
            false,
            source);
    }

    private static string? GetGainMapValue(XElement element, string localName)
    {
        var attributeValue = GetAttributeValue(element, localName, HdrGainMapNamespace);
        if (!string.IsNullOrWhiteSpace(attributeValue))
        {
            return NormalizeNumberList(attributeValue);
        }

        var child = element.Elements().FirstOrDefault(childElement => childElement.Name.LocalName == localName);
        return child is null ? null : NormalizeGainMapElementValue(child);
    }

    private static string? NormalizeGainMapElementValue(XElement element)
    {
        var sequenceValues = element
            .Descendants(Rdf + "li")
            .Select(item => NormalizeNumberList(item.Value))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        return sequenceValues.Length > 0
            ? string.Join(", ", sequenceValues)
            : NormalizeNumberList(element.Value);
    }

    private static string? GetAttributeValue(XElement element, string localName, string? namespaceName = null)
    {
        var attribute = element
            .Attributes()
            .FirstOrDefault(candidate =>
                candidate.Name.LocalName == localName
                && (namespaceName is null || candidate.Name.NamespaceName == namespaceName));

        return attribute?.Value;
    }

    private static bool? TryParseBoolean(string? value)
    {
        return bool.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string? NormalizeNumberList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value
            .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? number.ToString("0.###", CultureInfo.InvariantCulture)
                : part);

        return string.Join(", ", parts);
    }

    private static int FindNextJpegStart(ReadOnlySpan<byte> data, int startOffset)
    {
        for (var index = Math.Max(0, startOffset); index + 1 < data.Length; index++)
        {
            if (data[index] == 0xFF && data[index + 1] == 0xD8)
            {
                return index;
            }
        }

        return -1;
    }

    private static IccChunk? TryReadIccChunk(ReadOnlySpan<byte> payload)
    {
        const int headerLength = 14;
        if (!StartsWithAscii(payload, "ICC_PROFILE\0") || payload.Length <= headerLength)
        {
            return null;
        }

        var sequence = payload[12];
        var count = payload[13];
        if (sequence == 0 || count == 0 || sequence > count)
        {
            return null;
        }

        return new IccChunk(sequence, count, payload[headerLength..].ToArray());
    }

    private static GainMapColorGamut DetectIccColorGamut(IReadOnlyCollection<IccChunk> chunks)
    {
        var icc = AssembleIccProfile(chunks);
        if (icc is null)
        {
            return GainMapColorGamut.Unknown;
        }

        var span = icc.AsSpan();
        var textFallback = DetectIccColorGamutFromText(span);
        var parsed = DetectIccColorGamutFromTags(span);
        return parsed == GainMapColorGamut.Unknown ? textFallback : parsed;
    }

    private static byte[]? AssembleIccProfile(IReadOnlyCollection<IccChunk> chunks)
    {
        if (chunks.Count == 0)
        {
            return null;
        }

        var expectedCount = chunks.First().Count;
        var ordered = chunks
            .Where(chunk => chunk.Count == expectedCount)
            .OrderBy(chunk => chunk.Sequence)
            .ToArray();
        if (ordered.Length != expectedCount)
        {
            return null;
        }

        var totalLength = ordered.Sum(chunk => chunk.Data.Length);
        var profile = new byte[totalLength];
        var offset = 0;
        foreach (var chunk in ordered)
        {
            Buffer.BlockCopy(chunk.Data, 0, profile, offset, chunk.Data.Length);
            offset += chunk.Data.Length;
        }

        return profile;
    }

    private static GainMapColorGamut DetectIccColorGamutFromText(ReadOnlySpan<byte> icc)
    {
        if (ContainsAscii(icc, "Display P3"))
        {
            return GainMapColorGamut.DisplayP3;
        }

        if (ContainsAscii(icc, "Rec2020")
            || ContainsAscii(icc, "Rec.2020")
            || ContainsAscii(icc, "BT.2020")
            || ContainsAscii(icc, "BT2020"))
        {
            return GainMapColorGamut.Bt2100;
        }

        return ContainsAscii(icc, "sRGB") ? GainMapColorGamut.Bt709 : GainMapColorGamut.Unknown;
    }

    private static GainMapColorGamut DetectIccColorGamutFromTags(ReadOnlySpan<byte> icc)
    {
        const int headerLength = 128;
        const int tagEntryLength = 12;
        if (icc.Length < headerLength + 4)
        {
            return GainMapColorGamut.Unknown;
        }

        var tagCount = ReadBigEndianInt32(icc, headerLength);
        if (tagCount < 0 || tagCount > 256 || headerLength + 4 + (tagCount * tagEntryLength) > icc.Length)
        {
            return GainMapColorGamut.Unknown;
        }

        IccTag? cicp = null;
        IccTag? red = null;
        IccTag? green = null;
        IccTag? blue = null;
        for (var index = 0; index < tagCount; index++)
        {
            var entryOffset = headerLength + 4 + (index * tagEntryLength);
            var signature = ReadAsciiTag(icc, entryOffset);
            var offset = ReadBigEndianInt32(icc, entryOffset + 4);
            var length = ReadBigEndianInt32(icc, entryOffset + 8);
            if (offset < 0 || length < 0 || offset + length > icc.Length)
            {
                continue;
            }

            var tag = new IccTag(offset, length);
            switch (signature)
            {
                case "cicp":
                    cicp ??= tag;
                    break;
                case "rXYZ":
                    red ??= tag;
                    break;
                case "gXYZ":
                    green ??= tag;
                    break;
                case "bXYZ":
                    blue ??= tag;
                    break;
            }
        }

        if (cicp is { Length: 12 } cicpTag && cicpTag.Offset + 9 <= icc.Length)
        {
            return icc[cicpTag.Offset + 8] switch
            {
                1 => GainMapColorGamut.Bt709,
                9 => GainMapColorGamut.Bt2100,
                12 => GainMapColorGamut.DisplayP3,
                _ => GainMapColorGamut.Unknown,
            };
        }

        if (red is { Length: 20 } redTag && green is { Length: 20 } greenTag && blue is { Length: 20 } blueTag)
        {
            var matrix = new[]
            {
                ReadIccXyzTag(icc, redTag.Offset),
                ReadIccXyzTag(icc, greenTag.Offset),
                ReadIccXyzTag(icc, blueTag.Offset),
            };
            if (MatchesIccMatrix(matrix, SrgbD50))
            {
                return GainMapColorGamut.Bt709;
            }

            if (MatchesIccMatrix(matrix, DisplayP3D50))
            {
                return GainMapColorGamut.DisplayP3;
            }

            if (MatchesIccMatrix(matrix, Rec2020D50))
            {
                return GainMapColorGamut.Bt2100;
            }
        }

        return GainMapColorGamut.Unknown;
    }

    private static readonly double[][] SrgbD50 =
    [
        [0.436066, 0.222488, 0.013916],
        [0.385147, 0.716873, 0.097076],
        [0.143066, 0.060608, 0.714096],
    ];

    private static readonly double[][] DisplayP3D50 =
    [
        [0.515102, 0.241182, -0.001049],
        [0.291965, 0.692236, 0.041882],
        [0.157153, 0.066582, 0.784378],
    ];

    private static readonly double[][] Rec2020D50 =
    [
        [0.673459, 0.279033, -0.001931],
        [0.165661, 0.675338, 0.029979],
        [0.125100, 0.045629, 0.797162],
    ];

    private static double[] ReadIccXyzTag(ReadOnlySpan<byte> icc, int offset)
    {
        return
        [
            ReadS15Fixed16(icc, offset + 8),
            ReadS15Fixed16(icc, offset + 12),
            ReadS15Fixed16(icc, offset + 16),
        ];
    }

    private static bool MatchesIccMatrix(double[][] actual, double[][] expected)
    {
        const double tolerance = 0.002;
        for (var primary = 0; primary < 3; primary++)
        {
            for (var component = 0; component < 3; component++)
            {
                if (Math.Abs(actual[primary][component] - expected[primary][component]) > tolerance)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static double ReadS15Fixed16(ReadOnlySpan<byte> data, int offset)
    {
        return ReadBigEndianInt32(data, offset) / 65536.0;
    }

    private static int ReadBigEndianInt32(ReadOnlySpan<byte> data, int offset)
    {
        return (data[offset] << 24)
            | (data[offset + 1] << 16)
            | (data[offset + 2] << 8)
            | data[offset + 3];
    }

    private static string ReadAsciiTag(ReadOnlySpan<byte> data, int offset)
    {
        return Encoding.ASCII.GetString(data.Slice(offset, 4));
    }

    private static bool ContainsAscii(ReadOnlySpan<byte> data, string text)
    {
        if (text.Length == 0 || data.Length < text.Length)
        {
            return false;
        }

        for (var index = 0; index <= data.Length - text.Length; index++)
        {
            var matches = true;
            for (var textIndex = 0; textIndex < text.Length; textIndex++)
            {
                if (ToAsciiLower(data[index + textIndex]) != ToAsciiLower((byte)text[textIndex]))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    private static bool StartsWithAscii(ReadOnlySpan<byte> data, string text)
    {
        if (data.Length < text.Length)
        {
            return false;
        }

        for (var index = 0; index < text.Length; index++)
        {
            if (data[index] != (byte)text[index])
            {
                return false;
            }
        }

        return true;
    }

    private static byte ToAsciiLower(byte value)
    {
        return value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;
    }

    private sealed record JpegScan(
        bool IsValid,
        int StartOffset,
        int EndOffset,
        IReadOnlyList<string> XmpPackets,
        int? ExifOrientation,
        bool HasIccProfile,
        GainMapColorGamut IccColorGamut,
        bool HasIso21496Metadata,
        bool HasAppleHdrGainMapMetadata,
        AppleHdrHeadroom? AppleHdrHeadroom)
    {
        public static JpegScan Invalid(int startOffset)
        {
            return new JpegScan(false, startOffset, startOffset, [], null, false, GainMapColorGamut.Unknown, false, false, null);
        }
    }

    private sealed record IccChunk(
        int Sequence,
        int Count,
        byte[] Data);

    private sealed record IccTag(
        int Offset,
        int Length);

    private sealed record AppleHdrHeadroom(
        double Headroom,
        double Stops,
        string Source);
}
