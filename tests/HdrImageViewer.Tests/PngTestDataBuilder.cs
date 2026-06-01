using System.Buffers.Binary;
using System.IO.Compression;

namespace HdrImageViewer.Tests;

/// <summary>
/// Builds minimal, structurally-valid PNG byte streams (correct signature,
/// chunk lengths and CRC-32) for exercising <c>PngColorMetadataReader</c>.
/// Only the chunks relevant to colour-metadata parsing are emitted.
/// </summary>
internal sealed class PngTestDataBuilder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    private readonly List<(string Type, byte[] Data)> _chunks = new();

    public PngTestDataBuilder Ihdr(uint width, uint height, byte bitDepth, byte colorType)
    {
        var data = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), height);
        data[8] = bitDepth;
        data[9] = colorType;
        // bytes 10-12: compression / filter / interlace, all zero.
        return AddChunk("IHDR", data);
    }

    public PngTestDataBuilder Cicp(byte primaries, byte transfer, byte matrix, bool fullRange)
    {
        return AddChunk("cICP", [primaries, transfer, matrix, (byte)(fullRange ? 1 : 0)]);
    }

    /// <summary>Adds an iCCP chunk with the given profile name and raw ICC profile bytes (zlib-compressed per spec).</summary>
    public PngTestDataBuilder Iccp(string profileName, byte[] profileBytes)
    {
        var name = System.Text.Encoding.ASCII.GetBytes(profileName);
        using var compressedStream = new MemoryStream();
        using (var zlib = new ZLibStream(compressedStream, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(profileBytes, 0, profileBytes.Length);
        }

        var compressed = compressedStream.ToArray();
        var data = new byte[name.Length + 1 + 1 + compressed.Length];
        name.CopyTo(data, 0);
        data[name.Length] = 0;       // null separator
        data[name.Length + 1] = 0;   // compression method = deflate
        compressed.CopyTo(data, name.Length + 2);
        return AddChunk("iCCP", data);
    }

    public PngTestDataBuilder Idat(byte[]? data = null) => AddChunk("IDAT", data ?? new byte[] { 0x78, 0x9c, 0x03, 0x00 });

    public PngTestDataBuilder Iend() => AddChunk("IEND", Array.Empty<byte>());

    /// <summary>Adds an arbitrary chunk (e.g. an ancillary chunk that should be skipped).</summary>
    public PngTestDataBuilder Chunk(string type, byte[] data) => AddChunk(type, data);

    private PngTestDataBuilder AddChunk(string type, byte[] data)
    {
        _chunks.Add((type, data));
        return this;
    }

    public byte[] Build()
    {
        using var stream = new MemoryStream();
        stream.Write(Signature, 0, Signature.Length);
        foreach (var (type, data) in _chunks)
        {
            var lengthBuffer = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(lengthBuffer, (uint)data.Length);
            stream.Write(lengthBuffer, 0, 4);

            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            stream.Write(typeBytes, 0, 4);
            stream.Write(data, 0, data.Length);

            var crc = Crc32(typeBytes, data);
            var crcBuffer = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBuffer, crc);
            stream.Write(crcBuffer, 0, 4);
        }

        return stream.ToArray();
    }

    /// <summary>Builds a fake ICC profile body containing a "desc" tag with the given description text.</summary>
    public static byte[] IccProfileWithDescription(string description)
    {
        var text = System.Text.Encoding.ASCII.GetBytes(description);
        // Layout expected by ExtractIccProfileName's "desc" fallback:
        // "desc" + 4 bytes reserved + 4 bytes length(BE) + 4 bytes (skipped) + text.
        // descIndex points at "desc"; lengthOffset = descIndex + 12; textOffset = descIndex + 16.
        var profile = new List<byte>();
        profile.AddRange(System.Text.Encoding.ASCII.GetBytes("desc"));
        profile.AddRange(new byte[8]); // 4 reserved + first half before length offset
        var lengthField = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lengthField, (uint)(text.Length + 1)); // +1: stored length includes null terminator
        profile.AddRange(lengthField);
        profile.AddRange(text);
        profile.Add(0);
        // Pad so textOffset + length <= profile.Length comfortably.
        profile.AddRange(new byte[8]);
        return profile.ToArray();
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        foreach (var b in data)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return c ^ 0xFFFFFFFFu;
    }
}
