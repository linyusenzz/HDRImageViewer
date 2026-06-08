using System.Buffers.Binary;
using System.Text;
using HdrImageViewer.Models;

namespace HdrImageViewer.Services;

public static class CompanionVideoProbe
{
    private const int VisualSampleEntryHeaderLength = 78;
    private const long MaxProbeBytes = 512L * 1024L * 1024L;

    public static async Task<CompanionVideoProbeResult?> ProbeAsync(
        CompanionMedia media,
        CancellationToken cancellationToken = default)
    {
        var data = await ReadMediaBytesAsync(media, cancellationToken);
        return data is null ? null : Probe(data);
    }

    public static CompanionVideoProbeResult? Probe(ReadOnlySpan<byte> data)
    {
        var context = new ProbeContext();
        foreach (var box in ReadBoxes(data, 0, data.Length))
        {
            switch (box.Type)
            {
                case "ftyp":
                    ParseFileType(data.Slice(box.PayloadOffset, box.PayloadLength), context);
                    break;
                case "moov":
                    ParseMovie(data, box.PayloadOffset, box.PayloadOffset + box.PayloadLength, context);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(context.ContainerSummary)
            && string.IsNullOrWhiteSpace(context.SampleEntryType)
            && context.ColorPrimaries is null
            && context.TransferCharacteristics is null
            && context.LumaBitDepth is null)
        {
            return null;
        }

        return new CompanionVideoProbeResult(
            string.IsNullOrWhiteSpace(context.ContainerSummary) ? "ISO BMFF/MOV container" : context.ContainerSummary,
            context.SampleEntryType,
            context.CodecSummary,
            context.Width,
            context.Height,
            context.ColorPrimaries,
            context.TransferCharacteristics,
            context.MatrixCoefficients,
            context.FullRange,
            context.LumaBitDepth,
            context.ChromaBitDepth);
    }

    private static async Task<byte[]?> ReadMediaBytesAsync(
        CompanionMedia media,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(media.Path);
        if (!info.Exists)
        {
            return null;
        }

        var offset = media.EmbeddedOffset ?? 0L;
        var length = media.EmbeddedLength ?? info.Length;
        if (offset < 0 || length <= 0 || offset + length > info.Length || length > MaxProbeBytes || length > int.MaxValue)
        {
            return null;
        }

        var data = new byte[(int)length];
        await using var stream = new FileStream(
            media.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            1024 * 1024,
            useAsync: true);
        stream.Seek(offset, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(data, cancellationToken);
        return data;
    }

    private static void ParseFileType(ReadOnlySpan<byte> payload, ProbeContext context)
    {
        if (payload.Length < 8)
        {
            return;
        }

        var majorBrand = ReadType(payload, 0);
        var brands = new List<string>();
        for (var offset = 8; offset + 4 <= payload.Length; offset += 4)
        {
            brands.Add(ReadType(payload, offset));
        }

        context.ContainerSummary = brands.Count == 0
            ? $"brand {majorBrand}"
            : $"brand {majorBrand}; compatible {string.Join("/", brands.Distinct(StringComparer.OrdinalIgnoreCase))}";
    }

    private static void ParseMovie(ReadOnlySpan<byte> data, int startOffset, int endOffset, ProbeContext context)
    {
        foreach (var box in ReadBoxes(data, startOffset, endOffset))
        {
            if (box.Type == "trak")
            {
                ParseTrack(data, box.PayloadOffset, box.PayloadOffset + box.PayloadLength, context);
            }
        }
    }

    private static void ParseTrack(ReadOnlySpan<byte> data, int startOffset, int endOffset, ProbeContext context)
    {
        foreach (var box in ReadBoxes(data, startOffset, endOffset))
        {
            if (box.Type == "mdia")
            {
                ParseMedia(data, box.PayloadOffset, box.PayloadOffset + box.PayloadLength, context);
            }
        }
    }

    private static void ParseMedia(ReadOnlySpan<byte> data, int startOffset, int endOffset, ProbeContext context)
    {
        foreach (var box in ReadBoxes(data, startOffset, endOffset))
        {
            if (box.Type == "minf")
            {
                ParseMediaInformation(data, box.PayloadOffset, box.PayloadOffset + box.PayloadLength, context);
            }
        }
    }

    private static void ParseMediaInformation(ReadOnlySpan<byte> data, int startOffset, int endOffset, ProbeContext context)
    {
        foreach (var box in ReadBoxes(data, startOffset, endOffset))
        {
            if (box.Type == "stbl")
            {
                ParseSampleTable(data, box.PayloadOffset, box.PayloadOffset + box.PayloadLength, context);
            }
        }
    }

    private static void ParseSampleTable(ReadOnlySpan<byte> data, int startOffset, int endOffset, ProbeContext context)
    {
        foreach (var box in ReadBoxes(data, startOffset, endOffset))
        {
            if (box.Type == "stsd")
            {
                ParseSampleDescription(data.Slice(box.PayloadOffset, box.PayloadLength), context);
            }
        }
    }

    private static void ParseSampleDescription(ReadOnlySpan<byte> payload, ProbeContext context)
    {
        if (payload.Length < 8)
        {
            return;
        }

        foreach (var entry in ReadBoxes(payload, 8, payload.Length))
        {
            if (!IsVideoSampleEntry(entry.Type))
            {
                continue;
            }

            context.SampleEntryType ??= entry.Type;
            var entryPayload = payload.Slice(entry.PayloadOffset, entry.PayloadLength);
            if (entryPayload.Length >= VisualSampleEntryHeaderLength)
            {
                context.Width ??= BinaryPrimitives.ReadUInt16BigEndian(entryPayload.Slice(24, 2));
                context.Height ??= BinaryPrimitives.ReadUInt16BigEndian(entryPayload.Slice(26, 2));

                var childStart = entry.PayloadOffset + VisualSampleEntryHeaderLength;
                var childEnd = entry.PayloadOffset + entry.PayloadLength;
                foreach (var child in ReadBoxes(payload, childStart, childEnd))
                {
                    switch (child.Type)
                    {
                        case "hvcC":
                            ParseHevcConfiguration(payload.Slice(child.PayloadOffset, child.PayloadLength), context);
                            break;
                        case "colr":
                            ParseColorInformation(payload.Slice(child.PayloadOffset, child.PayloadLength), context);
                            break;
                    }
                }
            }
        }
    }

    private static bool IsVideoSampleEntry(string type)
    {
        return type is "hvc1" or "hev1" or "avc1" or "avc3" or "mp4v";
    }

    private static void ParseHevcConfiguration(ReadOnlySpan<byte> payload, ProbeContext context)
    {
        if (payload.Length < 23)
        {
            context.CodecSummary ??= "HEVC";
            return;
        }

        var profileIdc = payload[1] & 0x1F;
        var levelIdc = payload[12];
        var chromaFormat = payload[16] & 0x03;
        var lumaBitDepth = (payload[17] & 0x07) + 8;
        var chromaBitDepth = (payload[18] & 0x07) + 8;

        context.LumaBitDepth ??= lumaBitDepth;
        context.ChromaBitDepth ??= chromaBitDepth;
        context.CodecSummary ??= $"HEVC {DescribeHevcProfile(profileIdc)}, level {levelIdc / 30.0:0.#}, chroma {DescribeChromaFormat(chromaFormat)}";
    }

    private static string DescribeHevcProfile(int profileIdc)
    {
        return profileIdc switch
        {
            1 => "Main",
            2 => "Main 10",
            3 => "Main Still Picture",
            4 => "Range Extensions",
            _ => $"profile {profileIdc}",
        };
    }

    private static string DescribeChromaFormat(int chromaFormat)
    {
        return chromaFormat switch
        {
            0 => "monochrome",
            1 => "4:2:0",
            2 => "4:2:2",
            3 => "4:4:4",
            _ => "unknown",
        };
    }

    private static void ParseColorInformation(ReadOnlySpan<byte> payload, ProbeContext context)
    {
        if (payload.Length < 10)
        {
            return;
        }

        var colorType = ReadType(payload, 0);
        if (colorType is not ("nclx" or "nclc"))
        {
            return;
        }

        context.ColorPrimaries ??= BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(4, 2));
        context.TransferCharacteristics ??= BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(6, 2));
        context.MatrixCoefficients ??= BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(8, 2));
        if (colorType == "nclx" && payload.Length >= 11)
        {
            context.FullRange ??= (payload[10] & 0x80) != 0;
        }
    }

    private static IReadOnlyList<Box> ReadBoxes(ReadOnlySpan<byte> data, int startOffset, int endOffset)
    {
        var boxes = new List<Box>();
        var offset = startOffset;
        endOffset = Math.Min(endOffset, data.Length);
        while (offset + 8 <= endOffset)
        {
            var size32 = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            var type = ReadType(data, offset + 4);
            long size = size32;
            var headerSize = 8;
            if (size32 == 1)
            {
                if (offset + 16 > endOffset)
                {
                    break;
                }

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset + 8, 8));
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

            boxes.Add(new Box(type, offset + headerSize, checked((int)size - headerSize)));
            offset += checked((int)size);
        }

        return boxes;
    }

    private static string ReadType(ReadOnlySpan<byte> data, int offset)
    {
        return offset + 4 <= data.Length ? Encoding.ASCII.GetString(data.Slice(offset, 4)) : string.Empty;
    }

    private sealed class ProbeContext
    {
        public string? ContainerSummary { get; set; }

        public string? SampleEntryType { get; set; }

        public string? CodecSummary { get; set; }

        public int? Width { get; set; }

        public int? Height { get; set; }

        public int? ColorPrimaries { get; set; }

        public int? TransferCharacteristics { get; set; }

        public int? MatrixCoefficients { get; set; }

        public bool? FullRange { get; set; }

        public int? LumaBitDepth { get; set; }

        public int? ChromaBitDepth { get; set; }
    }

    private sealed record Box(string Type, int PayloadOffset, int PayloadLength);
}
