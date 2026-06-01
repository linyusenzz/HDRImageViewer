using System.Buffers.Binary;
using System.Text;

namespace HdrImageViewer.Services;

public enum FileContainerKind
{
    Unknown,
    Jpeg,
    HeifFamily,
}

public static class FileSignatureProbe
{
    private const int HeaderByteCount = 512;

    private static readonly HashSet<string> HeifBrands = new(StringComparer.OrdinalIgnoreCase)
    {
        "avif", "avis", "heic", "heix", "hevc", "hevx", "heif", "heim", "heis", "mif1", "msf1"
    };

    public static async Task<FileContainerKind> DetectAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var buffer = new byte[HeaderByteCount];
        await using var stream = File.OpenRead(path);
        var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
        return Detect(buffer.AsSpan(0, bytesRead));
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

        return FileContainerKind.Unknown;
    }

    public static bool IsJpeg(ReadOnlySpan<byte> header)
    {
        return header.Length >= 2 && header[0] == 0xFF && header[1] == 0xD8;
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
}
