using System.Buffers.Binary;
using System.IO.Compression;
using HdrImageViewer.Models;
using HdrImageViewer.Services;

internal static class JxlGainMapBoxChecks
{
    public static async Task RunAsync(string directory)
    {
        // Reference encodings serialized with libjxl 0.12 JxlGainMapWriteBundle.
        foreach (var (hex, gamut) in new[] { ("01", GainMapColorGamut.Bt709),
            ("90C70D", GainMapColorGamut.Bt2100), ("90C90D", GainMapColorGamut.DisplayP3) })
        {
            var encoding = Convert.FromHexString(hex);
            byte[] metadata = new byte[5 + 7 * 8];
            for (var i = 0; i < 7; ++i)
                BinaryPrimitives.WriteUInt32BigEndian(metadata.AsSpan(5 + i * 8 + 4), 1);
            var codestream = new byte[65536];
            new Random(42).NextBytes(codestream);
            codestream[0] = 0xff;
            codestream[1] = 0x0a;
            byte[] bundle = [0, 0, (byte)metadata.Length, .. metadata, (byte)encoding.Length, .. encoding, 0, 0, 0, 0, .. codestream];
            using var compressed = new MemoryStream();
            compressed.Write("jhgm"u8);
            using (var writer = new BrotliStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                writer.Write(bundle);
            var path = Path.Combine(directory, $"bundle-{gamut}.jxl");
            await using (var stream = File.Create(path))
            {
                WriteBox(stream, "brob"u8, "ExifUnrelated"u8);
                WriteBox(stream, "brob"u8, compressed.ToArray());
            }

            var parsed = await JxlGainMapDecoder.ReadGainMapBoxAsync(path, CancellationToken.None);
            if (parsed.AlternateColorGamut != gamut || !parsed.GainMapCodestream.AsSpan().SequenceEqual(codestream))
                throw new InvalidDataException($"Incorrect compressed JPEG XL bundle for {gamut}");
        }
        // Truncate the compressed stream and append a complete unrelated box.
        // The decoder must stop at the declared brob boundary.
        var original = await File.ReadAllBytesAsync(Path.Combine(directory, "bundle-Bt2100.jxl"));
        var brobOffset = BinaryPrimitives.ReadInt32BigEndian(original);
        var shortened = original[..^9];
        BinaryPrimitives.WriteInt32BigEndian(shortened.AsSpan(brobOffset), shortened.Length - brobOffset);
        var truncated = Path.Combine(directory, "bundle-truncated.jxl");
        await using (var stream = File.Create(truncated))
        {
            await stream.WriteAsync(shortened);
            WriteBox(stream, "free"u8, original.AsSpan(original.Length - 9));
        }
        try
        {
            await JxlGainMapDecoder.ReadGainMapBoxAsync(truncated, CancellationToken.None);
            throw new Exception("Truncated JPEG XL compressed gain map was accepted.");
        }
        catch (InvalidDataException) { }

        Console.WriteLine("PASS JPEG XL compressed jhgm: sRGB, BT.2020, Display P3 and exact codestream");
    }

    private static void WriteBox(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(size, payload.Length + 8);
        stream.Write(size);
        stream.Write(type);
        stream.Write(payload);
    }
}
