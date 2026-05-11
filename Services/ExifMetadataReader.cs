using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Storage;

namespace HdrImageViewer.Services;

public static class ExifMetadataReader
{
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
        var extendedProperties = new Dictionary<string, object>(StringComparer.Ordinal);
        BasicPropertiesSnapshot? basicSnapshot = null;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var imageProperties = await file.Properties.GetImagePropertiesAsync();
                AddRow(rows, "相机", JoinNonEmpty(imageProperties.CameraManufacturer, imageProperties.CameraModel));
                if (imageProperties.DateTaken.Year > 1900)
                {
                    AddRow(rows, "拍摄时间", imageProperties.DateTaken.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture));
                }

                if (imageProperties.Width > 0 && imageProperties.Height > 0)
                {
                    AddRow(rows, "尺寸", $"{imageProperties.Width} x {imageProperties.Height}");
                }

                if (imageProperties.Latitude is { } latitude && imageProperties.Longitude is { } longitude)
                {
                    AddRow(rows, "GPS", $"{latitude:0.######}, {longitude:0.######}");
                }
            }
            catch (Exception ex) when (IsMetadataReadException(ex))
            {
                AddRow(rows, "Windows 元数据", $"图片属性不可用 ({ex.GetType().Name})");
            }

            basicSnapshot = await TryReadBasicPropertiesAsync(file);
            extendedProperties = await TryReadExtendedPropertiesAsync(file, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (IsMetadataReadException(ex))
        {
            AddRow(rows, "Windows 元数据", $"存储属性不可用 ({ex.GetType().Name})");
        }

        var jpegExif = await TryReadJpegExifAsync(path, cancellationToken);
        AddRow(rows, "相机", JoinNonEmpty(jpegExif.Make, jpegExif.Model));
        AddRow(rows, "镜头", JoinNonEmpty(
            GetString(extendedProperties, "System.Photo.LensManufacturer"),
            GetString(extendedProperties, "System.Photo.LensModel"),
            jpegExif.LensMake,
            jpegExif.LensModel));
        AddRow(rows, "拍摄时间", jpegExif.DateTaken);

        var exposureParts = new[]
        {
            FormatExposureTime(GetDouble(extendedProperties, "System.Photo.ExposureTime") ?? jpegExif.ExposureSeconds),
            FormatAperture(GetDouble(extendedProperties, "System.Photo.FNumber") ?? jpegExif.FNumber),
            FormatIso(GetFirstNumber(extendedProperties, "System.Photo.ISOSpeed", "System.Photo.ISOSpeedRatings") ?? jpegExif.Iso),
            FormatFocalLength(
                GetDouble(extendedProperties, "System.Photo.FocalLength") ?? jpegExif.FocalLength,
                GetDouble(extendedProperties, "System.Photo.FocalLengthInFilm") ?? jpegExif.FocalLengthIn35mm),
        };
        AddRow(rows, "曝光", string.Join(", ", exposureParts.Where(static part => !string.IsNullOrWhiteSpace(part))));

        if (jpegExif.Width is > 0 && jpegExif.Height is > 0)
        {
            AddRow(rows, "尺寸", $"{jpegExif.Width} x {jpegExif.Height}");
        }

        if (jpegExif.Orientation is > 0)
        {
            AddRow(rows, "方向", jpegExif.Orientation.ToString());
        }

        AddRow(rows, "位深", FormatNumber(GetFirstNumber(extendedProperties, "System.Image.BitDepth")));
        AddRow(rows, "色彩空间", FormatColorSpace(GetFirstNumber(extendedProperties, "System.Image.ColorSpace")));

        if (basicSnapshot is not null)
        {
            AddRow(rows, "文件大小", FormatBytes(basicSnapshot.Size));
            if (basicSnapshot.DateModified.Year > 1900)
            {
                AddRow(rows, "修改时间", basicSnapshot.DateModified.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture));
            }
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
        foreach (var propertyName in ExtendedPropertyNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var properties = await file.Properties.RetrievePropertiesAsync([propertyName]);
                if (properties.TryGetValue(propertyName, out var value) && value is not null)
                {
                    result[propertyName] = value;
                }
            }
            catch (Exception ex) when (IsMetadataReadException(ex))
            {
            }
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

    private static async Task<JpegExifSnapshot> TryReadJpegExifAsync(string path, CancellationToken cancellationToken)
    {
        if (!DecoderCatalog.IsJpegExtension(Path.GetExtension(path)))
        {
            return JpegExifSnapshot.Empty;
        }

        try
        {
            var data = await File.ReadAllBytesAsync(path, cancellationToken);
            return TryReadJpegExif(data);
        }
        catch (Exception ex) when (IsMetadataReadException(ex) || ex is IOException)
        {
            return JpegExifSnapshot.Empty;
        }
    }

    private static JpegExifSnapshot TryReadJpegExif(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return JpegExifSnapshot.Empty;
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

        return JpegExifSnapshot.Empty;
    }

    private static JpegExifSnapshot TryReadTiffExif(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
        {
            return JpegExifSnapshot.Empty;
        }

        var littleEndian = tiff[0] == (byte)'I' && tiff[1] == (byte)'I';
        var bigEndian = tiff[0] == (byte)'M' && tiff[1] == (byte)'M';
        if (!littleEndian && !bigEndian)
        {
            return JpegExifSnapshot.Empty;
        }

        if (ReadUInt16(tiff, 2, littleEndian) != 42)
        {
            return JpegExifSnapshot.Empty;
        }

        var ifd0Offset = ReadUInt32(tiff, 4, littleEndian);
        var ifd0 = ReadIfd(tiff, ifd0Offset, littleEndian);
        var exifIfdOffset = ReadIntegerTag(ifd0, 0x8769, littleEndian);
        var exif = exifIfdOffset is > 0 ? ReadIfd(tiff, (uint)exifIfdOffset.Value, littleEndian) : [];

        var dateTaken = ReadAsciiTag(exif, 0x9003) ?? ReadAsciiTag(ifd0, 0x0132);
        return new JpegExifSnapshot(
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

    private sealed record JpegExifSnapshot(
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
        public static JpegExifSnapshot Empty { get; } = new(null, null, null, null, null, null, null, null, null, null, null, null, null);
    }
}
