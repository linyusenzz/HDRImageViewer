using System.Buffers.Binary;
using System.Text;

namespace HdrImageViewer.Services;

internal sealed record HeifTmapDescription(
    int PrimaryItemId,
    int GainMapItemId,
    IsoGainMapMetadata Metadata);

internal static class HeifTmapBoxReader
{
    public static HeifTmapDescription Read(string path)
    {
        var data = File.ReadAllBytes(path);
        var context = new ParseContext(data);
        foreach (var box in ReadBoxes(data, 0, data.Length))
        {
            if (box.Type == "meta")
            {
                ParseMeta(context, box);
            }
        }

        var primaryItemId = context.PrimaryItemId
            ?? throw new InvalidOperationException("HEIF/AVIF tmap container did not declare a primary item.");
        foreach (var tmapItemId in context.ItemTypesById
                     .Where(pair => string.Equals(pair.Value, "tmap", StringComparison.OrdinalIgnoreCase))
                     .Select(pair => pair.Key))
        {
            var references = context.ItemReferences
                .Where(reference => reference.FromItemId == tmapItemId
                    && string.Equals(reference.Type, "dimg", StringComparison.OrdinalIgnoreCase))
                .Select(reference => reference.ToItemId)
                .ToList();
            if (!references.Contains(primaryItemId))
            {
                continue;
            }

            var gainMapItemId = references.FirstOrDefault(itemId => itemId != primaryItemId);
            if (gainMapItemId <= 0)
            {
                continue;
            }

            var metadataBytes = ReadItemBytes(context, tmapItemId);
            var metadata = IsoGainMapMetadataParser.Parse(metadataBytes, IsoGainMapMetadataPayloadKind.HeifTmap);
            return new HeifTmapDescription(primaryItemId, gainMapItemId, metadata);
        }

        throw new InvalidOperationException("HEIF/AVIF container declares tmap support, but no base+gain-map tmap item graph was found.");
    }

    private static void ParseMeta(ParseContext context, Box box)
    {
        var start = box.PayloadOffset + 4;
        if (start > box.EndOffset)
        {
            return;
        }

        foreach (var child in ReadBoxes(context.Data, start, box.EndOffset))
        {
            switch (child.Type)
            {
                case "pitm":
                    ParsePrimaryItem(context, child);
                    break;
                case "iinf":
                    ParseItemInfo(context, child);
                    break;
                case "iref":
                    ParseItemReferences(context, child);
                    break;
                case "iloc":
                    ParseItemLocations(context, child);
                    break;
                case "idat":
                    context.IdatPayloadOffset = child.PayloadOffset;
                    context.IdatPayloadLength = child.PayloadLength;
                    break;
            }
        }
    }

    private static void ParsePrimaryItem(ParseContext context, Box box)
    {
        if (box.PayloadLength < 6)
        {
            return;
        }

        var version = context.Data[box.PayloadOffset];
        context.PrimaryItemId = version == 0
            ? ReadUInt16(context.Data, box.PayloadOffset + 4)
            : box.PayloadLength >= 8 ? checked((int)ReadUInt32(context.Data, box.PayloadOffset + 4)) : null;
    }

    private static void ParseItemInfo(ParseContext context, Box box)
    {
        var data = context.Data;
        if (box.PayloadLength < 6)
        {
            return;
        }

        var version = data[box.PayloadOffset];
        var offset = box.PayloadOffset + 4;
        var entryCount = version == 0
            ? ReadUInt16(data, offset)
            : checked((int)ReadUInt32(data, offset));
        offset += version == 0 ? 2 : 4;

        foreach (var entry in ReadBoxes(data, offset, box.EndOffset))
        {
            if (entryCount-- <= 0 || entry.Type != "infe")
            {
                continue;
            }

            ParseItemInfoEntry(context, entry);
        }
    }

    private static void ParseItemInfoEntry(ParseContext context, Box box)
    {
        var data = context.Data;
        if (box.PayloadLength < 12)
        {
            return;
        }

        var version = data[box.PayloadOffset];
        var offset = box.PayloadOffset + 4;
        int itemId;
        if (version >= 3)
        {
            itemId = checked((int)ReadUInt32(data, offset));
            offset += 4;
        }
        else
        {
            itemId = ReadUInt16(data, offset);
            offset += 2;
        }

        if (version < 2 || offset + 6 > box.EndOffset)
        {
            return;
        }

        offset += 2;
        context.ItemTypesById[itemId] = ReadType(data, offset);
    }

    private static void ParseItemReferences(ParseContext context, Box box)
    {
        var data = context.Data;
        if (box.PayloadLength < 4)
        {
            return;
        }

        var version = data[box.PayloadOffset];
        foreach (var referenceBox in ReadBoxes(data, box.PayloadOffset + 4, box.EndOffset))
        {
            var offset = referenceBox.PayloadOffset;
            while (offset < referenceBox.EndOffset)
            {
                int fromItemId;
                int referenceCount;
                if (version == 0)
                {
                    if (offset + 4 > referenceBox.EndOffset)
                    {
                        break;
                    }

                    fromItemId = ReadUInt16(data, offset);
                    referenceCount = ReadUInt16(data, offset + 2);
                    offset += 4;
                    for (var index = 0; index < referenceCount && offset + 2 <= referenceBox.EndOffset; index++)
                    {
                        context.ItemReferences.Add(new ItemReference(referenceBox.Type, fromItemId, ReadUInt16(data, offset)));
                        offset += 2;
                    }
                }
                else
                {
                    if (offset + 6 > referenceBox.EndOffset)
                    {
                        break;
                    }

                    fromItemId = checked((int)ReadUInt32(data, offset));
                    referenceCount = ReadUInt16(data, offset + 4);
                    offset += 6;
                    for (var index = 0; index < referenceCount && offset + 4 <= referenceBox.EndOffset; index++)
                    {
                        context.ItemReferences.Add(new ItemReference(referenceBox.Type, fromItemId, checked((int)ReadUInt32(data, offset))));
                        offset += 4;
                    }
                }
            }
        }
    }

    private static void ParseItemLocations(ParseContext context, Box box)
    {
        var data = context.Data;
        if (box.PayloadLength < 8)
        {
            return;
        }

        var version = data[box.PayloadOffset];
        var offset = box.PayloadOffset + 4;
        var sizes1 = data[offset++];
        var sizes2 = data[offset++];
        var offsetSize = sizes1 >> 4;
        var lengthSize = sizes1 & 0x0F;
        var baseOffsetSize = sizes2 >> 4;
        var indexSize = version is 1 or 2 ? sizes2 & 0x0F : 0;
        var itemCount = version < 2
            ? ReadUInt16(data, offset)
            : checked((int)ReadUInt32(data, offset));
        offset += version < 2 ? 2 : 4;

        for (var item = 0; item < itemCount && offset < box.EndOffset; item++)
        {
            var itemId = version < 2
                ? ReadUInt16(data, offset)
                : checked((int)ReadUInt32(data, offset));
            offset += version < 2 ? 2 : 4;

            var constructionMethod = 0;
            if (version is 1 or 2)
            {
                constructionMethod = ReadUInt16(data, offset) & 0x0FFF;
                offset += 2;
            }

            offset += 2; // data_reference_index
            var baseOffset = ReadUnsignedBigEndian(data, ref offset, baseOffsetSize);
            var extentCount = ReadUInt16(data, offset);
            offset += 2;
            var extents = new List<ItemExtent>(extentCount);
            for (var extent = 0; extent < extentCount; extent++)
            {
                if ((version is 1 or 2) && indexSize > 0)
                {
                    _ = ReadUnsignedBigEndian(data, ref offset, indexSize);
                }

                var extentOffset = ReadUnsignedBigEndian(data, ref offset, offsetSize);
                var extentLength = ReadUnsignedBigEndian(data, ref offset, lengthSize);
                extents.Add(new ItemExtent(checked(baseOffset + extentOffset), extentLength));
            }

            context.ItemLocations[itemId] = new ItemLocation(constructionMethod, extents);
        }
    }

    private static byte[] ReadItemBytes(ParseContext context, int itemId)
    {
        if (!context.ItemLocations.TryGetValue(itemId, out var location))
        {
            throw new InvalidOperationException($"HEIF tmap item #{itemId} did not have an iloc entry.");
        }

        var source = location.ConstructionMethod switch
        {
            0 => context.Data,
            1 when context.IdatPayloadOffset is { } idatOffset && context.IdatPayloadLength is { } idatLength
                => context.Data.AsSpan(idatOffset, idatLength).ToArray(),
            _ => throw new InvalidOperationException($"HEIF tmap item #{itemId} uses unsupported iloc construction method {location.ConstructionMethod}."),
        };

        using var stream = new MemoryStream();
        foreach (var extent in location.Extents)
        {
            if (extent.Offset < 0 || extent.Length < 0 || extent.Offset + extent.Length > source.Length)
            {
                throw new InvalidOperationException($"HEIF tmap item #{itemId} has an invalid iloc extent.");
            }

            stream.Write(source, extent.Offset, extent.Length);
        }

        return stream.ToArray();
    }

    private static IReadOnlyList<Box> ReadBoxes(byte[] data, int startOffset, int endOffset)
    {
        var boxes = new List<Box>();
        var offset = startOffset;
        endOffset = Math.Min(endOffset, data.Length);
        while (offset + 8 <= endOffset)
        {
            var size32 = ReadUInt32(data, offset);
            var type = ReadType(data, offset + 4);
            long size = size32;
            var headerSize = 8;
            if (size32 == 1)
            {
                if (offset + 16 > endOffset)
                {
                    break;
                }

                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset + 8, 8)));
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

            boxes.Add(new Box(type, offset + headerSize, checked((int)size - headerSize), checked((int)(offset + size))));
            offset += checked((int)size);
        }

        return boxes;
    }

    private static int ReadUInt16(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
    }

    private static uint ReadUInt32(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
    }

    private static int ReadUnsignedBigEndian(byte[] data, ref int offset, int byteCount)
    {
        if (byteCount == 0)
        {
            return 0;
        }

        if (byteCount is < 0 or > 8 || offset + byteCount > data.Length)
        {
            throw new InvalidOperationException("HEIF item location box is truncated.");
        }

        ulong value = 0;
        for (var index = 0; index < byteCount; index++)
        {
            value = (value << 8) | data[offset++];
        }

        return checked((int)value);
    }

    private static string ReadType(byte[] data, int offset)
    {
        return offset + 4 <= data.Length ? Encoding.ASCII.GetString(data, offset, 4) : string.Empty;
    }

    private sealed class ParseContext(byte[] data)
    {
        public byte[] Data { get; } = data;

        public int? PrimaryItemId { get; set; }

        public int? IdatPayloadOffset { get; set; }

        public int? IdatPayloadLength { get; set; }

        public Dictionary<int, string> ItemTypesById { get; } = [];

        public Dictionary<int, ItemLocation> ItemLocations { get; } = [];

        public List<ItemReference> ItemReferences { get; } = [];
    }

    private sealed record Box(string Type, int PayloadOffset, int PayloadLength, int EndOffset);

    private sealed record ItemReference(string Type, int FromItemId, int ToItemId);

    private sealed record ItemLocation(int ConstructionMethod, IReadOnlyList<ItemExtent> Extents);

    private sealed record ItemExtent(int Offset, int Length);
}
