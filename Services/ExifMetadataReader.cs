using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Storage;

namespace HdrImageViewer.Services;

public static class ExifMetadataReader
{
    private const int MaxJpegExifProbeBytes = 1024 * 1024;
    private const int MaxIsoBmffMetaBoxBytes = 16 * 1024 * 1024;
    private const int MaxIsoBmffExifBytes = 4 * 1024 * 1024;

    private static readonly string[] ExtendedPropertyNames =
    [
        "System.Photo.LensModel",
        "System.Photo.LensManufacturer",
        "System.Photo.FNumber",
        "System.Photo.ExposureTime",
        "System.Photo.ISOSpeed",
        "System.Photo.ISOSpeedRatings",
        "System.Photo.FocalLength",
        "System.Photo.FocalLengthInFilm",
        "System.Photo.Flash",
        "System.Image.BitDepth",
        "System.Image.ColorSpace",
    ];

    public static async Task<string> ReadSummaryAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return "没有 EXIF 元数据";
        }

        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        var exif = await TryReadExifAsync(path, cancellationToken);
        AddRow(rows, "相机", JoinNonEmpty(exif.Make, exif.Model));
        AddRow(rows, "镜头", JoinNonEmpty(exif.LensMake, exif.LensModel));
        AddRow(rows, "拍摄时间", exif.DateTaken);

        var exposureParts = new[]
        {
            FormatExposureTime(exif.ExposureSeconds),
            FormatAperture(exif.FNumber),
            FormatIso(exif.Iso),
            FormatFocalLength(
                exif.FocalLength,
                exif.FocalLengthIn35mm),
        };
        AddRow(rows, "曝光", string.Join(", ", exposureParts.Where(static part => !string.IsNullOrWhiteSpace(part))));

        if (exif.Width is > 0 && exif.Height is > 0)
        {
            AddRow(rows, "尺寸", $"{exif.Width} x {exif.Height}");
        }

        if (exif.Orientation is > 0)
        {
            AddRow(rows, "方向", exif.Orientation.ToString());
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                AddRow(rows, "文件大小", FormatBytes(info.Length));
                if (info.LastWriteTime.Year > 1900)
                {
                    AddRow(rows, "修改时间", info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture));
                }
            }
        }
        catch (Exception ex) when (IsMetadataReadException(ex) || ex is IOException)
        {
        }

        return rows.Count > 0 ? BuildSummary(rows) : "没有 EXIF 元数据";
    }

    private static async Task<BasicPropertiesSnapshot?> TryReadBasicPropertiesAsync(StorageFile file)
    {
        try
        {
            var basicProperties = await file.GetBasicPropertiesAsync();
            return new BasicPropertiesSnapshot((long)basicProperties.Size, basicProperties.DateModified);
        }
        catch (Exception ex) when (IsMetadataReadException(ex))
        {
            return null;
        }
    }

    private static async Task<Dictionary<string, object>> TryReadExtendedPropertiesAsync(StorageFile file, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var properties = await file.Properties.RetrievePropertiesAsync(ExtendedPropertyNames);
            foreach (var propertyName in ExtendedPropertyNames)
            {
                if (properties.TryGetValue(propertyName, out var value) && value is not null)
                {
                    result[propertyName] = value;
                }
            }
        }
        catch (Exception ex) when (IsMetadataReadException(ex))
        {
        }

        return result;
    }

    private static bool IsMetadataReadException(Exception ex)
    {
        return ex is FileNotFoundException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or COMException;
    }

    private static void AddRow(Dictionary<string, string> rows, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || rows.ContainsKey(label))
        {
            return;
        }

        rows[label] = value.Trim();
    }

    private static string BuildSummary(Dictionary<string, string> rows)
    {
        var summary = new StringBuilder();
        foreach (var (label, value) in rows)
        {
            summary.Append(label);
            summary.Append(": ");
            summary.AppendLine(value);
        }

        return summary.ToString().TrimEnd();
    }

    private static string JoinNonEmpty(params string?[] values)
    {
        return string.Join(" ", values.Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value!.Trim()));
    }

    private static string? GetString(IDictionary<string, object> properties, string key)
    {
        return properties.TryGetValue(key, out var value) ? value?.ToString() : null;
    }

    private static double? GetDouble(IDictionary<string, object> properties, string key)
    {
        if (!properties.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return ConvertToDouble(value);
    }

    private static double? GetFirstNumber(IDictionary<string, object> properties, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!properties.TryGetValue(key, out var value) || value is null)
            {
                continue;
            }

            if (value is System.Collections.IEnumerable values and not string)
            {
                foreach (var item in values)
                {
                    var number = ConvertToDouble(item);
                    if (number is not null)
                    {
                        return number;
                    }
                }
            }
            else
            {
                var number = ConvertToDouble(value);
                if (number is not null)
                {
                    return number;
                }
            }
        }

        return null;
    }

    private static double? ConvertToDouble(object? value)
    {
        return value switch
        {
            null => null,
            byte number => number,
            short number => number,
            ushort number => number,
            int number => number,
            uint number => number,
            long number => number,
            ulong number => number,
            float number => number,
            double number => number,
            decimal number => (double)number,
            IConvertible convertible => convertible.ToDouble(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    private static string? FormatExposureTime(double? seconds)
    {
        if (seconds is null or <= 0.0)
        {
            return null;
        }

        if (seconds < 1.0)
        {
            var denominator = Math.Round(1.0 / seconds.Value);
            return denominator > 0.0 ? $"1/{denominator:0} s" : null;
        }

        return $"{seconds.Value:0.###} s";
    }

    private static string? FormatAperture(double? fNumber)
    {
        return fNumber is > 0.0 ? $"f/{fNumber.Value:0.#}" : null;
    }

    private static string? FormatIso(double? iso)
    {
        return iso is > 0.0 ? $"ISO {iso.Value:0}" : null;
    }

    private static string? FormatFocalLength(double? focalLength, double? equivalentFocalLength)
    {
        if (focalLength is null or <= 0.0)
        {
            return equivalentFocalLength is > 0.0 ? $"{equivalentFocalLength.Value:0.#} mm eq" : null;
        }

        return equivalentFocalLength is > 0.0
            ? $"{focalLength.Value:0.#} mm ({equivalentFocalLength.Value:0.#} mm eq)"
            : $"{focalLength.Value:0.#} mm";
    }

    private static string? FormatColorSpace(double? colorSpace)
    {
        return colorSpace switch
        {
            null => null,
            1.0 => "sRGB",
            2.0 => "Uncalibrated",
            _ => colorSpace.Value.ToString("0", CultureInfo.InvariantCulture),
        };
    }

    private static string? FormatNumber(double? value)
    {
        return value is null ? null : value.Value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static async Task<ExifSnapshot> TryReadExifAsync(string path, CancellationToken cancellationToken)
    {
        if (DecoderCatalog.IsJpegExtension(Path.GetExtension(path)))
        {
            return await TryReadJpegExifAsync(path, cancellationToken);
        }

        if (HeifAvifProbe.IsHeifFamilyExtension(Path.GetExtension(path)))
        {
            return await TryReadIsoBmffExifAsync(path, cancellationToken);
        }

        return ExifSnapshot.Empty;
    }

    private static async Task<ExifSnapshot> TryReadJpegExifAsync(string path, CancellationToken cancellationToken)
    {
        if (!DecoderCatalog.IsJpegExtension(Path.GetExtension(path)))
        {
            return ExifSnapshot.Empty;
        }

        try
        {
            var info = new FileInfo(path);
            var length = checked((int)Math.Min(info.Length, MaxJpegExifProbeBytes));
            var data = new byte[length];
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
            var bytesRead = await stream.ReadAsync(data.AsMemory(0, length), cancellationToken);
            return TryReadJpegExif(data.AsSpan(0, bytesRead));
        }
        catch (Exception ex) when (IsMetadataReadException(ex) || ex is IOException)
        {
            return ExifSnapshot.Empty;
        }
    }

    private static ExifSnapshot TryReadJpegExif(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return ExifSnapshot.Empty;
        }

        var offset = 2;
        while (offset + 4 <= data.Length)
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
            if (marker is 0xD9 or 0xDA)
            {
                break;
            }

            if (IsStandaloneJpegMarker(marker))
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
            if (marker == 0xE1 && StartsWithAscii(payload, "Exif\0\0"))
            {
                return TryReadTiffExif(payload[6..]);
            }

            offset += segmentLength;
        }

        return ExifSnapshot.Empty;
    }

    private static async Task<ExifSnapshot> TryReadIsoBmffExifAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
            var metaPayload = await ReadTopLevelMetaBoxAsync(stream, cancellationToken);
            if (metaPayload is null)
            {
                return ExifSnapshot.Empty;
            }

            var exifExtents = FindIsoBmffExifExtents(metaPayload);
            if (exifExtents.Count == 0)
            {
                return ExifSnapshot.Empty;
            }

            foreach (var extents in exifExtents)
            {
                var byteCount = extents.Sum(static extent => extent.Length);
                if (byteCount <= 0 || byteCount > MaxIsoBmffExifBytes)
                {
                    continue;
                }

                var payload = new byte[checked((int)byteCount)];
                var destinationOffset = 0;
                foreach (var extent in extents)
                {
                    if (extent.Offset < 0 || extent.Length < 0 || extent.Offset + extent.Length > stream.Length)
                    {
                        destinationOffset = -1;
                        break;
                    }

                    stream.Position = extent.Offset;
                    await stream.ReadExactlyAsync(payload.AsMemory(destinationOffset, checked((int)extent.Length)), cancellationToken);
                    destinationOffset += checked((int)extent.Length);
                }

                if (destinationOffset < 0)
                {
                    continue;
                }

                var tiff = FindTiffHeaderInIsoBmffExifItem(payload);
                if (!tiff.IsEmpty)
                {
                    var snapshot = TryReadTiffExif(tiff);
                    if (snapshot.HasValues)
                    {
                        return snapshot;
                    }
                }
            }
        }
        catch (Exception ex) when (IsMetadataReadException(ex) || ex is IOException || ex is EndOfStreamException || ex is OverflowException)
        {
        }

        return ExifSnapshot.Empty;
    }

    private static async Task<byte[]?> ReadTopLevelMetaBoxAsync(FileStream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var header = new byte[16];
        while (stream.Position + 8 <= stream.Length)
        {
            var boxOffset = stream.Position;
            await stream.ReadExactlyAsync(header.AsMemory(0, 8), cancellationToken);
            var size32 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            var type = Encoding.ASCII.GetString(header, 4, 4);
            long size = size32;
            var headerSize = 8;
            if (size32 == 1)
            {
                await stream.ReadExactlyAsync(header.AsMemory(8, 8), cancellationToken);
                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8)));
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                size = stream.Length - boxOffset;
            }

            if (size < headerSize || boxOffset + size > stream.Length)
            {
                return null;
            }

            var payloadLength = size - headerSize;
            if (type == "meta")
            {
                if (payloadLength <= 0 || payloadLength > MaxIsoBmffMetaBoxBytes)
                {
                    return null;
                }

                var payload = new byte[checked((int)payloadLength)];
                await stream.ReadExactlyAsync(payload, cancellationToken);
                return payload;
            }

            stream.Position = boxOffset + size;
        }

        return null;
    }

    private static List<IReadOnlyList<IsoBmffExtent>> FindIsoBmffExifExtents(ReadOnlySpan<byte> metaPayload)
    {
        var result = new List<IReadOnlyList<IsoBmffExtent>>();
        if (metaPayload.Length < 4)
        {
            return result;
        }

        var itemTypes = new Dictionary<int, string>();
        var itemLocations = new Dictionary<int, IReadOnlyList<IsoBmffExtent>>();
        foreach (var box in ReadIsoBmffBoxes(metaPayload, 4, metaPayload.Length))
        {
            var payload = metaPayload.Slice(box.PayloadOffset, box.PayloadLength);
            switch (box.Type)
            {
                case "iinf":
                    ParseIsoBmffItemInfo(payload, itemTypes);
                    break;
                case "iloc":
                    ParseIsoBmffItemLocations(payload, itemLocations);
                    break;
            }
        }

        foreach (var (itemId, itemType) in itemTypes)
        {
            if (string.Equals(itemType, "Exif", StringComparison.Ordinal) && itemLocations.TryGetValue(itemId, out var extents))
            {
                result.Add(extents);
            }
        }

        return result;
    }

    private static void ParseIsoBmffItemInfo(ReadOnlySpan<byte> payload, Dictionary<int, string> itemTypes)
    {
        if (payload.Length < 6)
        {
            return;
        }

        var version = payload[0];
        var offset = 4;
        var entryCount = version == 0
            ? BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2))
            : payload.Length >= offset + 4 ? (int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset, 4)) : 0;
        offset += version == 0 ? 2 : 4;

        foreach (var box in ReadIsoBmffBoxes(payload, offset, payload.Length))
        {
            if (entryCount-- <= 0 || box.Type != "infe")
            {
                continue;
            }

            ParseIsoBmffItemInfoEntry(payload.Slice(box.PayloadOffset, box.PayloadLength), itemTypes);
        }
    }

    private static void ParseIsoBmffItemInfoEntry(ReadOnlySpan<byte> payload, Dictionary<int, string> itemTypes)
    {
        if (payload.Length < 12)
        {
            return;
        }

        var version = payload[0];
        if (version < 2)
        {
            return;
        }

        var offset = 4;
        int itemId;
        if (version >= 3)
        {
            if (payload.Length < offset + 4)
            {
                return;
            }

            itemId = checked((int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset, 4)));
            offset += 4;
        }
        else
        {
            if (payload.Length < offset + 2)
            {
                return;
            }

            itemId = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2));
            offset += 2;
        }

        if (payload.Length < offset + 6)
        {
            return;
        }

        offset += 2;
        itemTypes[itemId] = ReadIsoBmffType(payload, offset);
    }

    private static void ParseIsoBmffItemLocations(ReadOnlySpan<byte> payload, Dictionary<int, IReadOnlyList<IsoBmffExtent>> itemLocations)
    {
        if (payload.Length < 8)
        {
            return;
        }

        var version = payload[0];
        var offset = 4;
        var offsetSize = payload[offset] >> 4;
        var lengthSize = payload[offset] & 0x0F;
        var baseOffsetSize = payload[offset + 1] >> 4;
        var indexSize = version is 1 or 2 ? payload[offset + 1] & 0x0F : 0;
        offset += 2;

        if (!IsValidIsoBmffFieldSize(offsetSize)
            || !IsValidIsoBmffFieldSize(lengthSize)
            || !IsValidIsoBmffFieldSize(baseOffsetSize)
            || !IsValidIsoBmffFieldSize(indexSize))
        {
            return;
        }

        if (payload.Length < offset + (version < 2 ? 2 : 4))
        {
            return;
        }

        var itemCount = version < 2
            ? BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2))
            : checked((int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset, 4)));
        offset += version < 2 ? 2 : 4;

        for (var item = 0; item < itemCount && offset < payload.Length; item++)
        {
            var itemIdSize = version < 2 ? 2 : 4;
            if (payload.Length < offset + itemIdSize)
            {
                return;
            }

            var itemId = version < 2
                ? BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2))
                : checked((int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset, 4)));
            offset += itemIdSize;

            var constructionMethod = 0;
            if (version is 1 or 2)
            {
                if (payload.Length < offset + 2)
                {
                    return;
                }

                constructionMethod = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2)) & 0x0F;
                offset += 2;
            }

            if (payload.Length < offset + 2 + baseOffsetSize + 2)
            {
                return;
            }

            offset += 2;
            var baseOffset = ReadIsoBmffVariableUnsigned(payload, offset, baseOffsetSize);
            offset += baseOffsetSize;
            var extentCount = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2));
            offset += 2;

            var extents = new List<IsoBmffExtent>(extentCount);
            for (var extent = 0; extent < extentCount; extent++)
            {
                if (version is 1 or 2 && indexSize > 0)
                {
                    if (payload.Length < offset + indexSize)
                    {
                        return;
                    }

                    offset += indexSize;
                }

                if (payload.Length < offset + offsetSize + lengthSize)
                {
                    return;
                }

                var extentOffset = ReadIsoBmffVariableUnsigned(payload, offset, offsetSize);
                offset += offsetSize;
                var extentLength = ReadIsoBmffVariableUnsigned(payload, offset, lengthSize);
                offset += lengthSize;

                if (constructionMethod == 0)
                {
                    extents.Add(new IsoBmffExtent(checked((long)(baseOffset + extentOffset)), checked((long)extentLength)));
                }
            }

            if (extents.Count > 0)
            {
                itemLocations[itemId] = extents;
            }
        }
    }

    private static ReadOnlySpan<byte> FindTiffHeaderInIsoBmffExifItem(ReadOnlySpan<byte> payload)
    {
        if (LooksLikeTiffHeader(payload))
        {
            return payload;
        }

        if (StartsWithAscii(payload, "Exif\0\0"))
        {
            return payload[6..];
        }

        if (payload.Length >= 8)
        {
            var tiffHeaderOffset = BinaryPrimitives.ReadUInt32BigEndian(payload[..4]);
            if (tiffHeaderOffset <= int.MaxValue)
            {
                var offsetFromItemStart = (int)tiffHeaderOffset;
                if (offsetFromItemStart < payload.Length && LooksLikeTiffHeader(payload[offsetFromItemStart..]))
                {
                    return payload[offsetFromItemStart..];
                }

                var offsetAfterHeaderField = 4 + offsetFromItemStart;
                if (offsetAfterHeaderField < payload.Length && LooksLikeTiffHeader(payload[offsetAfterHeaderField..]))
                {
                    return payload[offsetAfterHeaderField..];
                }
            }
        }

        return [];
    }

    private static bool LooksLikeTiffHeader(ReadOnlySpan<byte> data)
    {
        return data.Length >= 4
            && ((data[0] == (byte)'I' && data[1] == (byte)'I' && data[2] == 42 && data[3] == 0)
                || (data[0] == (byte)'M' && data[1] == (byte)'M' && data[2] == 0 && data[3] == 42));
    }

    private static IReadOnlyList<IsoBmffBox> ReadIsoBmffBoxes(ReadOnlySpan<byte> data, int startOffset, int endOffset)
    {
        var boxes = new List<IsoBmffBox>();
        var offset = startOffset;
        endOffset = Math.Min(endOffset, data.Length);
        while (offset + 8 <= endOffset)
        {
            var size32 = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            var type = ReadIsoBmffType(data, offset + 4);
            long size = size32;
            var headerSize = 8;
            if (size32 == 1)
            {
                if (offset + 16 > endOffset)
                {
                    break;
                }

                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset + 8, 8)));
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                size = endOffset - offset;
            }

            if (size < headerSize || offset + size > endOffset)
            {
                break;
            }

            boxes.Add(new IsoBmffBox(type, offset + headerSize, checked((int)size - headerSize)));
            offset += checked((int)size);
        }

        return boxes;
    }

    private static string ReadIsoBmffType(ReadOnlySpan<byte> data, int offset)
    {
        return offset + 4 <= data.Length ? Encoding.ASCII.GetString(data.Slice(offset, 4)) : string.Empty;
    }

    private static bool IsValidIsoBmffFieldSize(int size)
    {
        return size is 0 or 4 or 8;
    }

    private static ulong ReadIsoBmffVariableUnsigned(ReadOnlySpan<byte> data, int offset, int byteCount)
    {
        return byteCount switch
        {
            0 => 0,
            4 => BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4)),
            8 => BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset, 8)),
            _ => 0,
        };
    }

    private static ExifSnapshot TryReadTiffExif(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
        {
            return ExifSnapshot.Empty;
        }

        var littleEndian = tiff[0] == (byte)'I' && tiff[1] == (byte)'I';
        var bigEndian = tiff[0] == (byte)'M' && tiff[1] == (byte)'M';
        if (!littleEndian && !bigEndian)
        {
            return ExifSnapshot.Empty;
        }

        if (ReadUInt16(tiff, 2, littleEndian) != 42)
        {
            return ExifSnapshot.Empty;
        }

        var ifd0Offset = ReadUInt32(tiff, 4, littleEndian);
        var ifd0 = ReadIfd(tiff, ifd0Offset, littleEndian);
        var exifIfdOffset = ReadIntegerTag(ifd0, 0x8769, littleEndian);
        var exif = exifIfdOffset is > 0 ? ReadIfd(tiff, (uint)exifIfdOffset.Value, littleEndian) : [];

        var dateTaken = ReadAsciiTag(exif, 0x9003) ?? ReadAsciiTag(ifd0, 0x0132);
        return new ExifSnapshot(
            ReadAsciiTag(ifd0, 0x010F),
            ReadAsciiTag(ifd0, 0x0110),
            ReadAsciiTag(exif, 0xA433),
            ReadAsciiTag(exif, 0xA434),
            FormatExifDate(dateTaken),
            ReadRationalTag(exif, 0x829A, littleEndian),
            ReadRationalTag(exif, 0x829D, littleEndian),
            ReadIntegerTag(exif, 0x8827, littleEndian),
            ReadRationalTag(exif, 0x920A, littleEndian),
            ReadIntegerTag(exif, 0xA405, littleEndian),
            ReadIntegerTag(exif, 0xA002, littleEndian),
            ReadIntegerTag(exif, 0xA003, littleEndian),
            ReadIntegerTag(ifd0, 0x0112, littleEndian));
    }

    private static Dictionary<ushort, ExifTagValue> ReadIfd(ReadOnlySpan<byte> tiff, uint ifdOffset, bool littleEndian)
    {
        var result = new Dictionary<ushort, ExifTagValue>();
        if (ifdOffset > int.MaxValue || ifdOffset + 2 > tiff.Length)
        {
            return result;
        }

        var offset = (int)ifdOffset;
        var count = ReadUInt16(tiff, offset, littleEndian);
        offset += 2;
        for (var i = 0; i < count; i++)
        {
            var entryOffset = offset + (i * 12);
            if (entryOffset + 12 > tiff.Length)
            {
                break;
            }

            var tag = ReadUInt16(tiff, entryOffset, littleEndian);
            var type = ReadUInt16(tiff, entryOffset + 2, littleEndian);
            var valueCount = ReadUInt32(tiff, entryOffset + 4, littleEndian);
            var elementSize = GetExifTypeSize(type);
            var byteCount = valueCount * elementSize;
            if (elementSize == 0 || byteCount == 0 || byteCount > int.MaxValue)
            {
                continue;
            }

            byte[] valueBytes;
            if (byteCount <= 4)
            {
                valueBytes = tiff.Slice(entryOffset + 8, (int)byteCount).ToArray();
            }
            else
            {
                var valueOffset = ReadUInt32(tiff, entryOffset + 8, littleEndian);
                if (valueOffset > int.MaxValue || valueOffset + byteCount > tiff.Length)
                {
                    continue;
                }

                valueBytes = tiff.Slice((int)valueOffset, (int)byteCount).ToArray();
            }

            result[tag] = new ExifTagValue(type, valueCount, valueBytes);
        }

        return result;
    }

    private static uint GetExifTypeSize(ushort type)
    {
        return type switch
        {
            1 or 2 or 7 => 1,
            3 => 2,
            4 or 9 => 4,
            5 or 10 => 8,
            _ => 0,
        };
    }

    private static string? ReadAsciiTag(Dictionary<ushort, ExifTagValue> ifd, ushort tag)
    {
        if (!ifd.TryGetValue(tag, out var value) || value.Type != 2 || value.Data.Length == 0)
        {
            return null;
        }

        var text = Encoding.ASCII.GetString(value.Data).TrimEnd('\0', ' ');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static int? ReadIntegerTag(Dictionary<ushort, ExifTagValue> ifd, ushort tag, bool littleEndian)
    {
        if (!ifd.TryGetValue(tag, out var value) || value.Data.Length == 0)
        {
            return null;
        }

        return value.Type switch
        {
            3 when value.Data.Length >= 2 => ReadUInt16(value.Data, 0, littleEndian),
            4 when value.Data.Length >= 4 => (int)Math.Min(int.MaxValue, ReadUInt32(value.Data, 0, littleEndian)),
            9 when value.Data.Length >= 4 => ReadInt32(value.Data, 0, littleEndian),
            _ => null,
        };
    }

    private static double? ReadRationalTag(Dictionary<ushort, ExifTagValue> ifd, ushort tag, bool littleEndian)
    {
        if (!ifd.TryGetValue(tag, out var value) || value.Data.Length < 8)
        {
            return null;
        }

        if (value.Type == 5)
        {
            var numerator = ReadUInt32(value.Data, 0, littleEndian);
            var denominator = ReadUInt32(value.Data, 4, littleEndian);
            return denominator == 0 ? null : (double)numerator / denominator;
        }

        if (value.Type == 10)
        {
            var numerator = ReadInt32(value.Data, 0, littleEndian);
            var denominator = ReadInt32(value.Data, 4, littleEndian);
            return denominator == 0 ? null : (double)numerator / denominator;
        }

        return null;
    }

    private static string? FormatExifDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.TryParseExact(
            value.Trim(),
            "yyyy:MM:dd HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var dateTime)
            ? dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
            : value.Trim();
    }

    private static bool StartsWithAscii(ReadOnlySpan<byte> data, string text)
    {
        if (data.Length < text.Length)
        {
            return false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (data[i] != (byte)text[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsStandaloneJpegMarker(byte marker)
    {
        return marker == 0x01 || marker is >= 0xD0 and <= 0xD7;
    }

    private static ushort ReadBigEndianUInt16(ReadOnlySpan<byte> data, int offset)
    {
        return (ushort)((data[offset] << 8) | data[offset + 1]);
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

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)Math.Max(bytes, 0);
        var unit = 0;
        while (value >= 1024.0 && unit < units.Length - 1)
        {
            value /= 1024.0;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    private sealed record ExifTagValue(ushort Type, uint Count, byte[] Data);

    private sealed record BasicPropertiesSnapshot(long Size, DateTimeOffset DateModified);

    private sealed record IsoBmffBox(string Type, int PayloadOffset, int PayloadLength);

    private sealed record IsoBmffExtent(long Offset, long Length);

    private sealed record ExifSnapshot(
        string? Make,
        string? Model,
        string? LensMake,
        string? LensModel,
        string? DateTaken,
        double? ExposureSeconds,
        double? FNumber,
        int? Iso,
        double? FocalLength,
        int? FocalLengthIn35mm,
        int? Width,
        int? Height,
        int? Orientation)
    {
        public static ExifSnapshot Empty { get; } = new(null, null, null, null, null, null, null, null, null, null, null, null, null);

        public bool HasValues =>
            !string.IsNullOrWhiteSpace(Make)
            || !string.IsNullOrWhiteSpace(Model)
            || !string.IsNullOrWhiteSpace(LensMake)
            || !string.IsNullOrWhiteSpace(LensModel)
            || !string.IsNullOrWhiteSpace(DateTaken)
            || ExposureSeconds is not null
            || FNumber is not null
            || Iso is not null
            || FocalLength is not null
            || FocalLengthIn35mm is not null
            || Width is not null
            || Height is not null
            || Orientation is not null;
    }
}
