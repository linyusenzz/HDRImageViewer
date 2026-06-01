using System.Buffers.Binary;
using HdrImageViewer.Models;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace HdrImageViewer.Services;

public static class WicImageProbe
{
    private static readonly string[] PropertyNames =
    [
        "System.Image.BitDepth",
        "System.Image.ColorSpace",
    ];

    public static bool IsWicProbeExtension(string extension)
    {
        return DecoderCatalog.IsJpegXrExtension(extension)
            || string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".tif", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".tiff", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<WicImageProbeResult?> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(path);
        if (!IsWicProbeExtension(extension))
        {
            return null;
        }

        var pngMetadata = string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
            ? await ReadPngMetadataAsync(path, cancellationToken)
            : null;
        var tiffMetadata = string.Equals(extension, ".tif", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".tiff", StringComparison.OrdinalIgnoreCase)
                ? await ReadTiffMetadataAsync(path, cancellationToken)
                : null;

        uint? width = null;
        uint? height = null;
        try
        {
            await using var fileStream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                useAsync: true);
            using var stream = fileStream.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            width = decoder.PixelWidth;
            height = decoder.PixelHeight;
        }
        catch
        {
        }

        int? bitDepth = null;
        string? colorSpace = null;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var properties = await file.Properties.RetrievePropertiesAsync(PropertyNames);
            if (properties.TryGetValue("System.Image.BitDepth", out var bitDepthValue))
            {
                bitDepth = ConvertToInt32(bitDepthValue);
            }

            if (properties.TryGetValue("System.Image.ColorSpace", out var colorSpaceValue))
            {
                colorSpace = FormatColorSpace(colorSpaceValue);
            }
        }
        catch
        {
        }

        width ??= pngMetadata?.PixelWidth;
        height ??= pngMetadata?.PixelHeight;
        width ??= tiffMetadata?.PixelWidth;
        height ??= tiffMetadata?.PixelHeight;
        bitDepth ??= pngMetadata?.TotalBitDepth ?? tiffMetadata?.TotalBitDepth;

        return new WicImageProbeResult(
            IsWicCandidate: true,
            extension,
            width,
            height,
            bitDepth,
            colorSpace,
            DecoderNameForExtension(extension),
            pngMetadata?.BitsPerChannel,
            pngMetadata?.ColorType,
            pngMetadata?.CicpPrimaries,
            pngMetadata?.CicpTransfer,
            pngMetadata?.CicpMatrix,
            pngMetadata?.CicpFullRange,
            pngMetadata?.IccProfileName,
            tiffMetadata?.BitsPerSample,
            tiffMetadata?.SamplesPerPixel,
            tiffMetadata?.SampleFormat,
            tiffMetadata?.PhotometricInterpretation,
            tiffMetadata?.IccProfileName);
    }

    private static async Task<PngMetadata?> ReadPngMetadataAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);

            var metadata = await PngColorMetadataReader.ReadAsync(stream, cancellationToken);
            if (metadata is null)
            {
                return null;
            }

            return new PngMetadata(
                metadata.PixelWidth,
                metadata.PixelHeight,
                metadata.BitsPerChannel,
                metadata.ColorType,
                metadata.TotalBitDepth,
                metadata.CicpPrimaries,
                metadata.CicpTransfer,
                metadata.CicpMatrix,
                metadata.CicpFullRange,
                metadata.IccProfileName);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<TiffMetadata?> ReadTiffMetadataAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);

            var header = new byte[8];
            if (!await TryReadExactlyAsync(stream, header, cancellationToken))
            {
                return null;
            }

            var littleEndian = header[0] == (byte)'I' && header[1] == (byte)'I';
            var bigEndian = header[0] == (byte)'M' && header[1] == (byte)'M';
            if (!littleEndian && !bigEndian)
            {
                return null;
            }

            if (ReadUInt16(header, 2, littleEndian) != 42)
            {
                return null;
            }

            var ifdOffset = ReadUInt32(header, 4, littleEndian);
            if (ifdOffset == 0 || ifdOffset > stream.Length - 2)
            {
                return null;
            }

            stream.Seek(ifdOffset, SeekOrigin.Begin);
            var countBuffer = new byte[2];
            if (!await TryReadExactlyAsync(stream, countBuffer, cancellationToken))
            {
                return null;
            }

            var entryCount = ReadUInt16(countBuffer, 0, littleEndian);
            if (entryCount > 4096)
            {
                return null;
            }

            uint? width = null;
            uint? height = null;
            int[]? bitsPerSample = null;
            int? samplesPerPixel = null;
            int? sampleFormat = null;
            int? photometricInterpretation = null;
            string? iccProfileName = null;
            var entry = new byte[12];

            for (var i = 0; i < entryCount; i++)
            {
                if (!await TryReadExactlyAsync(stream, entry, cancellationToken))
                {
                    break;
                }

                var tag = ReadUInt16(entry, 0, littleEndian);
                var type = ReadUInt16(entry, 2, littleEndian);
                var count = ReadUInt32(entry, 4, littleEndian);
                var valueOrOffset = ReadUInt32(entry, 8, littleEndian);

                switch (tag)
                {
                    case 256:
                        width = ReadTiffFirstUIntValue(stream, entry, type, count, valueOrOffset, littleEndian);
                        break;
                    case 257:
                        height = ReadTiffFirstUIntValue(stream, entry, type, count, valueOrOffset, littleEndian);
                        break;
                    case 258:
                        bitsPerSample = await ReadTiffShortValuesAsync(stream, entry, type, count, valueOrOffset, littleEndian, cancellationToken);
                        break;
                    case 262:
                        photometricInterpretation = (int?)ReadTiffFirstUIntValue(stream, entry, type, count, valueOrOffset, littleEndian);
                        break;
                    case 277:
                        samplesPerPixel = (int?)ReadTiffFirstUIntValue(stream, entry, type, count, valueOrOffset, littleEndian);
                        break;
                    case 339:
                        var sampleFormats = await ReadTiffShortValuesAsync(stream, entry, type, count, valueOrOffset, littleEndian, cancellationToken);
                        sampleFormat = sampleFormats?.FirstOrDefault();
                        break;
                    case 34675:
                        iccProfileName = await ReadTiffIccProfileNameAsync(stream, type, count, valueOrOffset, cancellationToken);
                        break;
                }
            }

            var totalBitDepth = bitsPerSample?.Sum();
            return width is null && height is null && bitsPerSample is null && sampleFormat is null
                ? null
                : new TiffMetadata(
                    width,
                    height,
                    bitsPerSample,
                    samplesPerPixel,
                    sampleFormat,
                    photometricInterpretation,
                    totalBitDepth,
                    iccProfileName);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> ReadTiffIccProfileNameAsync(
        Stream stream,
        ushort type,
        uint count,
        uint valueOrOffset,
        CancellationToken cancellationToken)
    {
        if (type != 7 || count < 128 || count > 1024 * 1024 || valueOrOffset > stream.Length - count)
        {
            return null;
        }

        var originalPosition = stream.Position;
        try
        {
            stream.Seek(valueOrOffset, SeekOrigin.Begin);
            var data = new byte[count];
            if (!await TryReadExactlyAsync(stream, data, cancellationToken))
            {
                return null;
            }

            return ExtractIccProfileName(data);
        }
        finally
        {
            stream.Seek(originalPosition, SeekOrigin.Begin);
        }
    }

    private static string? ExtractIccProfileName(byte[] profile)
    {
        var ascii = System.Text.Encoding.ASCII.GetString(profile);
        foreach (var known in new[] { "Rec. 2020 PQ", "Rec. 2020", "Rec2020", "BT.2020", "Display P3", "sRGB" })
        {
            if (ascii.Contains(known, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        var descIndex = ascii.IndexOf("desc", StringComparison.Ordinal);
        if (descIndex >= 0 && descIndex + 16 < profile.Length)
        {
            var lengthOffset = descIndex + 12;
            var length = BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(lengthOffset, 4));
            var textOffset = descIndex + 16;
            if (length > 1 && length < 256 && textOffset + length <= profile.Length)
            {
                var description = System.Text.Encoding.ASCII.GetString(profile, textOffset, (int)length - 1);
                return string.IsNullOrWhiteSpace(description) ? null : description.Trim();
            }
        }

        return null;
    }

    private static uint? ReadTiffFirstUIntValue(Stream stream, byte[] entry, ushort type, uint count, uint valueOrOffset, bool littleEndian)
    {
        if (count == 0)
        {
            return null;
        }

        return type switch
        {
            3 => count == 1 ? ReadUInt16(entry, 8, littleEndian) : ReadUInt16AtOffset(stream, valueOrOffset, littleEndian),
            4 => count == 1 ? valueOrOffset : ReadUInt32AtOffset(stream, valueOrOffset, littleEndian),
            _ => null,
        };
    }

    private static async Task<int[]?> ReadTiffShortValuesAsync(
        Stream stream,
        byte[] entry,
        ushort type,
        uint count,
        uint valueOrOffset,
        bool littleEndian,
        CancellationToken cancellationToken)
    {
        if (type != 3 || count == 0 || count > 32)
        {
            return null;
        }

        var values = new int[count];
        if (count <= 2)
        {
            for (var i = 0; i < count; i++)
            {
                values[i] = ReadUInt16(entry, 8 + (i * 2), littleEndian);
            }

            return values;
        }

        if (valueOrOffset > stream.Length - (count * 2))
        {
            return null;
        }

        var originalPosition = stream.Position;
        try
        {
            stream.Seek(valueOrOffset, SeekOrigin.Begin);
            var data = new byte[checked((int)count * 2)];
            if (!await TryReadExactlyAsync(stream, data, cancellationToken))
            {
                return null;
            }

            for (var i = 0; i < count; i++)
            {
                values[i] = ReadUInt16(data, i * 2, littleEndian);
            }

            return values;
        }
        finally
        {
            stream.Seek(originalPosition, SeekOrigin.Begin);
        }
    }

    private static ushort ReadUInt16(byte[] data, int offset, bool littleEndian)
    {
        return littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
    }

    private static uint ReadUInt32(byte[] data, int offset, bool littleEndian)
    {
        return littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
    }

    private static ushort? ReadUInt16AtOffset(Stream stream, uint offset, bool littleEndian)
    {
        if (offset > stream.Length - 2)
        {
            return null;
        }

        var originalPosition = stream.Position;
        try
        {
            stream.Seek(offset, SeekOrigin.Begin);
            var data = new byte[2];
            return stream.Read(data, 0, data.Length) == data.Length
                ? ReadUInt16(data, 0, littleEndian)
                : null;
        }
        finally
        {
            stream.Seek(originalPosition, SeekOrigin.Begin);
        }
    }

    private static uint? ReadUInt32AtOffset(Stream stream, uint offset, bool littleEndian)
    {
        if (offset > stream.Length - 4)
        {
            return null;
        }

        var originalPosition = stream.Position;
        try
        {
            stream.Seek(offset, SeekOrigin.Begin);
            var data = new byte[4];
            return stream.Read(data, 0, data.Length) == data.Length
                ? ReadUInt32(data, 0, littleEndian)
                : null;
        }
        finally
        {
            stream.Seek(originalPosition, SeekOrigin.Begin);
        }
    }

    private static async Task<bool> TryReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private static async Task SkipBytesAsync(Stream stream, long count, CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            return;
        }

        if (stream.CanSeek)
        {
            stream.Seek(count, SeekOrigin.Current);
            return;
        }

        var buffer = new byte[Math.Min(4096, count)];
        while (count > 0)
        {
            var readLength = (int)Math.Min(buffer.Length, count);
            var read = await stream.ReadAsync(buffer.AsMemory(0, readLength), cancellationToken);
            if (read == 0)
            {
                return;
            }

            count -= read;
        }
    }

    private static int? ConvertToInt32(object? value)
    {
        try
        {
            return value switch
            {
                null => null,
                byte number => number,
                short number => number,
                ushort number => number,
                int number => number,
                uint number => checked((int)number),
                long number => checked((int)number),
                ulong number => checked((int)number),
                IConvertible convertible => convertible.ToInt32(System.Globalization.CultureInfo.InvariantCulture),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? FormatColorSpace(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (ConvertToInt32(value) is { } number)
        {
            return number switch
            {
                1 => "sRGB",
                2 => "Uncalibrated",
                _ => $"Color space {number}",
            };
        }

        var text = value.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string DecoderNameForExtension(string extension)
    {
        if (DecoderCatalog.IsJpegXrExtension(extension))
        {
            return "WIC JPEG XR";
        }

        if (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase))
        {
            return "WIC PNG";
        }

        return "WIC TIFF";
    }

    private sealed record PngMetadata(
        uint? PixelWidth,
        uint? PixelHeight,
        int? BitsPerChannel,
        int? ColorType,
        int? TotalBitDepth,
        int? CicpPrimaries,
        int? CicpTransfer,
        int? CicpMatrix,
        bool? CicpFullRange,
        string? IccProfileName);

    private sealed record TiffMetadata(
        uint? PixelWidth,
        uint? PixelHeight,
        int[]? BitsPerSample,
        int? SamplesPerPixel,
        int? SampleFormat,
        int? PhotometricInterpretation,
        int? TotalBitDepth,
        string? IccProfileName);
}
