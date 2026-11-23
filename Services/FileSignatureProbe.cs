using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace HdrImageViewer.Services;

public enum FileContainerKind
{
    Unknown,
    Jpeg,
    HeifFamily,
    Jxl,
    RadianceHdr,
    OpenExr,
    Png,
    Tiff,
    WebP,
    JpegXr,
}

public static class FileSignatureProbe
{
    private const int HeaderByteCount = 512;

    private static readonly byte[] JxlContainerSignature =
    [
        0x00, 0x00, 0x00, 0x0C, 0x4A, 0x58, 0x4C, 0x20, 0x0D, 0x0A, 0x87, 0x0A,
    ];

    private static readonly byte[] PngSignature =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
    ];

    private static readonly byte[] OpenExrSignature =
    [
        0x76, 0x2F, 0x31, 0x01,
    ];

    private static readonly HashSet<string> HeifBrands = new(StringComparer.OrdinalIgnoreCase)
    {
        "avif", "avis", "heic", "heix", "hevc", "hevx", "heif", "heim", "heis", "mif1", "msf1"
    };

    public static async Task<FileContainerKind> DetectAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(HeaderByteCount);
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: HeaderByteCount,
                useAsync: true);
            var bytesRead = await stream.ReadAtLeastAsync(buffer.AsMemory(0, HeaderByteCount), HeaderByteCount, throwOnEndOfStream: false, cancellationToken);
            return Detect(buffer.AsSpan(0, bytesRead));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static FileContainerKind Detect(ReadOnlySpan<byte> header)
    {
        if (IsJpeg(header))
        {
            return FileContainerKind.Jpeg;
        }

        if (IsHeifFamily(header))
        {
            return FileContainerKind.HeifFamily;
        }

        if (IsJxl(header))
        {
            return FileContainerKind.Jxl;
        }

        if (IsOpenExr(header))
        {
            return FileContainerKind.OpenExr;
        }

        if (IsPng(header))
        {
            return FileContainerKind.Png;
        }

        if (IsJpegXr(header))
        {
            return FileContainerKind.JpegXr;
        }

        if (IsTiff(header))
        {
            return FileContainerKind.Tiff;
        }

        if (IsWebP(header))
        {
            return FileContainerKind.WebP;
        }

        if (IsRadianceHdr(header))
        {
            return FileContainerKind.RadianceHdr;
        }

        return FileContainerKind.Unknown;
    }

    public static bool IsOpenExr(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && header[..4].SequenceEqual(OpenExrSignature);

    public static bool IsPng(ReadOnlySpan<byte> header) =>
        header.Length >= 8 && header[..8].SequenceEqual(PngSignature);

    public static bool IsJpegXr(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && header[0] == 0x49 && header[1] == 0x49 && header[2] == 0xBC && (header[3] == 0x01 || header[3] == 0x00);

    public static bool IsTiff(ReadOnlySpan<byte> header) =>
        header.Length >= 4
        && ((header[0] == 0x49 && header[1] == 0x49 && header[2] == 0x2A && header[3] == 0x00)
            || (header[0] == 0x4D && header[1] == 0x4D && header[2] == 0x00 && header[3] == 0x2A));

    public static bool IsWebP(ReadOnlySpan<byte> header) =>
        header.Length >= 12
        && header[..4].SequenceEqual("RIFF"u8)
        && header.Slice(8, 4).SequenceEqual("WEBP"u8);

    public static bool IsJpeg(ReadOnlySpan<byte> header)
    {
        return header.Length >= 2 && header[0] == 0xFF && header[1] == 0xD8;
    }

    public static bool IsJxl(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 2 && header[0] == 0xFF && header[1] == 0x0A)
        {
            return true;
        }

        return header.Length >= JxlContainerSignature.Length
            && header[..JxlContainerSignature.Length].SequenceEqual(JxlContainerSignature);
    }

    public static bool IsRadianceHdr(ReadOnlySpan<byte> header)
    {
        if (StartsWithAsciiIgnoreCase(header, "#?RADIANCE")
            || StartsWithAsciiIgnoreCase(header, "#?RGBE")
            || StartsWithAsciiIgnoreCase(header, "FORMAT=32-bit_rle_rgbe")
            || StartsWithAsciiIgnoreCase(header, "FORMAT=32-bit_rle_xyze"))
        {
            return true;
        }

        if (header.Length >= 2 && header[0] == (byte)'#' && header[1] == (byte)'?')
        {
            return ContainsAsciiIgnoreCase(header, "FORMAT=32-bit_rle_rgbe")
                || ContainsAsciiIgnoreCase(header, "FORMAT=32-bit_rle_xyze");
        }

        return false;
    }

    public static bool IsHeifFamily(ReadOnlySpan<byte> header)
    {
        if (header.Length < 16 || ReadType(header, 4) != "ftyp")
        {
            return false;
        }

        var boxSize = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
        var end = boxSize == 0 ? header.Length : Math.Min(header.Length, checked((int)Math.Min(boxSize, int.MaxValue)));
        if (end < 16)
        {
            return false;
        }

        if (HeifBrands.Contains(ReadType(header, 8)))
        {
            return true;
        }

        for (var offset = 16; offset + 4 <= end; offset += 4)
        {
            if (HeifBrands.Contains(ReadType(header, offset)))
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadType(ReadOnlySpan<byte> data, int offset)
    {
        return offset + 4 <= data.Length ? Encoding.ASCII.GetString(data.Slice(offset, 4)) : string.Empty;
    }

    private static bool StartsWithAsciiIgnoreCase(ReadOnlySpan<byte> data, string ascii)
    {
        if (data.Length < ascii.Length)
        {
            return false;
        }

        for (var i = 0; i < ascii.Length; i++)
        {
            if (ToAsciiLower(data[i]) != ToAsciiLower((byte)ascii[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsAsciiIgnoreCase(ReadOnlySpan<byte> data, string ascii)
    {
        if (ascii.Length == 0 || data.Length < ascii.Length)
        {
            return false;
        }

        for (var i = 0; i <= data.Length - ascii.Length; i++)
        {
            if (StartsWithAsciiIgnoreCase(data[i..], ascii))
            {
                return true;
            }
        }

        return false;
    }

    private static byte ToAsciiLower(byte value)
    {
        return value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;
    }
}
