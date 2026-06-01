using System.Buffers.Binary;
using System.IO.Compression;

namespace HdrImageViewer.Services;

/// <summary>
/// Colour-relevant metadata recovered from a PNG container by scanning its
/// chunk stream (IHDR / cICP / iCCP). Intentionally free of any WinRT or
/// file-system dependency so the parsing logic can be unit tested directly
/// against an in-memory stream.
/// </summary>
public sealed record PngColorMetadata(
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

/// <summary>
/// Parses the colour-signalling chunks of a PNG (IHDR, cICP, iCCP) straight
/// from a byte stream. This was previously inlined inside
/// <see cref="WicImageProbe"/>; it lives here so it carries no WinRT
/// dependency and can be exercised by unit tests.
/// </summary>
public static class PngColorMetadataReader
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    private const int MaxIccChunkLength = 1024 * 1024;

    /// <summary>
    /// Scans <paramref name="stream"/> for PNG colour metadata. Returns
    /// <c>null</c> if the stream is not a PNG or carries no relevant signal.
    /// The scan stops at IDAT: cICP and iCCP are required to appear before the
    /// first image-data chunk, so there is nothing colour-relevant afterwards,
    /// and stopping there avoids seeking over the (potentially large) pixel
    /// payload. The scan must NOT stop on the first colour chunk encountered:
    /// when iCCP precedes the more authoritative cICP, an early stop would lose
    /// the cICP transfer/primaries signal.
    /// </summary>
    public static async Task<PngColorMetadata?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        try
        {
            var signature = new byte[PngSignature.Length];
            if (!await TryReadExactlyAsync(stream, signature, cancellationToken)
                || !signature.AsSpan().SequenceEqual(PngSignature))
            {
                return null;
            }

            uint? width = null;
            uint? height = null;
            int? bitsPerChannel = null;
            int? colorType = null;
            int? totalBitDepth = null;
            int? cicpPrimaries = null;
            int? cicpTransfer = null;
            int? cicpMatrix = null;
            bool? cicpFullRange = null;
            string? iccProfileName = null;
            var header = new byte[8];

            while (await TryReadExactlyAsync(stream, header, cancellationToken))
            {
                var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
                var isIhdr = IsChunk(header, "IHDR");
                var isCicp = IsChunk(header, "cICP");
                var isIccp = IsChunk(header, "iCCP");
                var isIend = IsChunk(header, "IEND");
                var isIdat = IsChunk(header, "IDAT");
                if (isIhdr && length >= 13)
                {
                    var data = new byte[13];
                    if (!await TryReadExactlyAsync(stream, data, cancellationToken))
                    {
                        break;
                    }

                    width = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4));
                    height = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4));
                    bitsPerChannel = data[8];
                    colorType = data[9];
                    if (GetChannelCount(data[9]) is { } channelCount)
                    {
                        totalBitDepth = checked(channelCount * data[8]);
                    }

                    await SkipBytesAsync(stream, length - 13 + 4, cancellationToken);
                }
                else if (isCicp && length >= 4)
                {
                    var data = new byte[4];
                    if (!await TryReadExactlyAsync(stream, data, cancellationToken))
                    {
                        break;
                    }

                    cicpPrimaries = data[0];
                    cicpTransfer = data[1];
                    cicpMatrix = data[2];
                    cicpFullRange = data[3] != 0;
                    await SkipBytesAsync(stream, length - 4 + 4, cancellationToken);
                }
                else if (isIccp && length > 2 && length <= MaxIccChunkLength)
                {
                    var data = new byte[length];
                    if (!await TryReadExactlyAsync(stream, data, cancellationToken))
                    {
                        break;
                    }

                    iccProfileName = TryReadIccProfileName(data) ?? iccProfileName;
                    await SkipBytesAsync(stream, 4, cancellationToken);
                }
                else
                {
                    await SkipBytesAsync(stream, length + 4, cancellationToken);
                    if (isIend)
                    {
                        break;
                    }
                }

                // cICP and iCCP both precede IDAT in a valid PNG, so once image
                // data starts there is no further colour metadata to read.
                if (isIdat)
                {
                    break;
                }
            }

            return width is null && cicpTransfer is null && iccProfileName is null
                ? null
                : new PngColorMetadata(
                    width,
                    height,
                    bitsPerChannel,
                    colorType,
                    totalBitDepth,
                    cicpPrimaries,
                    cicpTransfer,
                    cicpMatrix,
                    cicpFullRange,
                    iccProfileName);
        }
        catch
        {
            return null;
        }
    }

    internal static string? TryReadIccProfileName(byte[] chunkData)
    {
        var separator = Array.IndexOf(chunkData, (byte)0);
        if (separator <= 0 || separator + 2 >= chunkData.Length)
        {
            return null;
        }

        var compressionMethod = chunkData[separator + 1];
        if (compressionMethod != 0)
        {
            return null;
        }

        try
        {
            using var compressed = new MemoryStream(chunkData, separator + 2, chunkData.Length - separator - 2, writable: false);
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            using var profile = new MemoryStream();
            zlib.CopyTo(profile);
            return ExtractIccProfileName(profile.ToArray());
        }
        catch
        {
            return null;
        }
    }

    internal static string? ExtractIccProfileName(byte[] profile)
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

    private static int? GetChannelCount(byte colorType)
    {
        return colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => null,
        };
    }

    private static bool IsChunk(byte[] header, string expected)
    {
        return header.Length >= 8
            && header[4] == expected[0]
            && header[5] == expected[1]
            && header[6] == expected[2]
            && header[7] == expected[3];
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
}
