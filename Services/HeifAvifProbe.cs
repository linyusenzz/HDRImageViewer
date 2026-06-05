using System.Buffers.Binary;
using System.Text;
using HdrImageViewer.Models;

namespace HdrImageViewer.Services;

public static class HeifAvifProbe
{
    private const int FastProbeByteCount = 4 * 1024 * 1024;
    private const string AppleHdrGainMapAuxiliaryType = "urn:com:apple:photo:2020:aux:hdrgainmap";

    private static readonly HashSet<string> HeifBrands = new(StringComparer.OrdinalIgnoreCase)
    {
        "avif", "avis", "heic", "heix", "hevc", "hevx", "heif", "heim", "heis", "mif1", "msf1"
    };

    public static async Task<HeifAvifProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var fastProbeLength = checked((int)Math.Min(stream.Length, FastProbeByteCount));
        var data = new byte[fastProbeLength];
        await stream.ReadExactlyAsync(data, cancellationToken);
        var fastProbe = Probe(data);
        if (stream.Length <= FastProbeByteCount
            || fastProbe.HasHdrTransfer
            || fastProbe.HasGainMapAuxiliary
            || fastProbe.HasIsoGainMapSignal
            || fastProbe.PrimaryItemId is not null)
        {
            return fastProbe;
        }

        stream.Position = 0;
        data = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(data, cancellationToken);
        return Probe(data);
    }

    public static HeifAvifProbeResult Probe(ReadOnlySpan<byte> data)
    {
        var context = new ProbeContext();
        foreach (var box in ReadBoxes(data, 0, data.Length))
        {
            switch (box.Type)
            {
                case "ftyp":
                    ParseFileType(data.Slice(box.PayloadOffset, box.PayloadLength), context);
                    break;
                case "meta":
                    ParseMeta(data.Slice(box.PayloadOffset, box.PayloadLength), context);
                    break;
            }
        }

        var isHeifFamily = context.MajorBrand is not null
            && (HeifBrands.Contains(context.MajorBrand)
                || context.CompatibleBrands.Any(HeifBrands.Contains));
        var primaryItemType = context.PrimaryItemId is { } primaryId && context.ItemTypesById.TryGetValue(primaryId, out var type)
            ? type
            : null;
        var associatedProperties = SelectRepresentativeProperties(context, context.PrimaryItemId, primaryItemType);
        var color = associatedProperties
            .Select(property => property.Color)
            .Where(colorInfo => colorInfo is not null)
            .OrderByDescending(colorInfo => colorInfo!.HasUsableColorSignal)
            .FirstOrDefault();
        var bitDepth = associatedProperties
            .Where(property => property.PixelInformation.Count > 0)
            .SelectMany(property => property.PixelInformation)
            .DefaultIfEmpty()
            .Max();
        int? maxBitDepth = bitDepth > 0 ? bitDepth : null;
        var auxiliaryTypes = context.PropertiesByIndex.Values
            .Select(property => property.AuxiliaryType)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToList();
        var hasAppleGainMap = auxiliaryTypes.Any(value => value.Contains(AppleHdrGainMapAuxiliaryType, StringComparison.OrdinalIgnoreCase));
        var hasGainMapAuxiliary = hasAppleGainMap
            || auxiliaryTypes.Any(value => value.Contains("gain", StringComparison.OrdinalIgnoreCase))
            || context.ItemTypesById.Values.Any(value => value.Contains("gain", StringComparison.OrdinalIgnoreCase));
        var hasIsoGainMapSignal =
            context.CompatibleBrands.Any(value => string.Equals(value, "tmap", StringComparison.OrdinalIgnoreCase))
            || context.ItemTypesById.Values.Any(value => string.Equals(value, "tmap", StringComparison.OrdinalIgnoreCase));
        var transfer = color?.TransferCharacteristics;
        var primaries = color?.ColorPrimaries;

        return new HeifAvifProbeResult(
            isHeifFamily,
            context.MajorBrand ?? "unknown",
            context.CompatibleBrands,
            context.PrimaryItemId,
            primaryItemType,
            context.ItemTypesById.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            color?.ProfileType,
            primaries,
            transfer,
            color?.MatrixCoefficients,
            color?.FullRange,
            maxBitDepth,
            transfer is 16 or 18,
            primaries == 9,
            hasGainMapAuxiliary,
            hasAppleGainMap,
            hasIsoGainMapSignal);
    }

    public static bool IsHeifFamilyExtension(string extension)
    {
        return string.Equals(extension, ".avif", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".heif", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".heic", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ItemProperty> SelectRepresentativeProperties(
        ProbeContext context,
        int? primaryItemId,
        string? primaryItemType)
    {
        var selected = primaryItemId is { } id
            ? GetAssociatedProperties(context, id).ToList()
            : [];

        if (string.Equals(primaryItemType, "grid", StringComparison.OrdinalIgnoreCase)
            || !selected.Any(property => property.Color is not null)
            || !selected.Any(property => property.PixelInformation.Count > 0))
        {
            foreach (var itemId in context.ItemTypesById
                .Where(pair => IsCodedImageItemType(pair.Value))
                .Select(pair => pair.Key))
            {
                selected.AddRange(GetAssociatedProperties(context, itemId));
            }
        }

        if (!selected.Any(property => property.Color is not null))
        {
            selected.AddRange(context.PropertiesByIndex.Values.Where(property => property.Color is not null));
        }

        if (!selected.Any(property => property.PixelInformation.Count > 0))
        {
            selected.AddRange(context.PropertiesByIndex.Values.Where(property => property.PixelInformation.Count > 0));
        }

        return selected
            .DistinctBy(property => property.Index)
            .ToList();
    }

    private static IEnumerable<ItemProperty> GetAssociatedProperties(ProbeContext context, int itemId)
    {
        return context.PropertyIndicesByItemId.TryGetValue(itemId, out var propertyIndices)
            ? propertyIndices
                .Select(index => context.PropertiesByIndex.TryGetValue(index, out var property) ? property : null)
                .Where(property => property is not null)
                .Cast<ItemProperty>()
            : [];
    }

    private static bool IsCodedImageItemType(string itemType)
    {
        return itemType is "hvc1" or "hev1" or "av01" or "jpeg" or "jxl ";
    }

    private static void ParseFileType(ReadOnlySpan<byte> payload, ProbeContext context)
    {
        if (payload.Length < 8)
        {
            return;
        }

        context.MajorBrand = ReadType(payload, 0);
        for (var offset = 8; offset + 4 <= payload.Length; offset += 4)
        {
            context.CompatibleBrands.Add(ReadType(payload, offset));
        }
    }

    private static void ParseMeta(ReadOnlySpan<byte> payload, ProbeContext context)
    {
        if (payload.Length < 4)
        {
            return;
        }

        foreach (var box in ReadBoxes(payload, 4, payload.Length))
        {
            var boxPayload = payload.Slice(box.PayloadOffset, box.PayloadLength);
            switch (box.Type)
            {
                case "pitm":
                    ParsePrimaryItem(boxPayload, context);
                    break;
                case "iinf":
                    ParseItemInfo(boxPayload, context);
                    break;
                case "iprp":
                    ParseItemProperties(boxPayload, context);
                    break;
            }
        }
    }

    private static void ParsePrimaryItem(ReadOnlySpan<byte> payload, ProbeContext context)
    {
        if (payload.Length < 6)
        {
            return;
        }

        var version = payload[0];
        context.PrimaryItemId = version == 0
            ? BinaryPrimitives.ReadUInt16BigEndian(payload[4..6])
            : payload.Length >= 8 ? (int)BinaryPrimitives.ReadUInt32BigEndian(payload[4..8]) : null;
    }

    private static void ParseItemInfo(ReadOnlySpan<byte> payload, ProbeContext context)
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

        foreach (var box in ReadBoxes(payload, offset, payload.Length))
        {
            if (entryCount-- <= 0 || box.Type != "infe")
            {
                continue;
            }

            ParseItemInfoEntry(payload.Slice(box.PayloadOffset, box.PayloadLength), context);
        }
    }

    private static void ParseItemInfoEntry(ReadOnlySpan<byte> payload, ProbeContext context)
    {
        if (payload.Length < 12)
        {
            return;
        }

        var version = payload[0];
        var offset = 4;
        int itemId;
        if (version >= 3)
        {
            if (payload.Length < offset + 4)
            {
                return;
            }

            itemId = (int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset, 4));
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

        if (version >= 2)
        {
            if (payload.Length < offset + 6)
            {
                return;
            }

            offset += 2;
            context.ItemTypesById[itemId] = ReadType(payload, offset);
        }
    }

    private static void ParseItemProperties(ReadOnlySpan<byte> payload, ProbeContext context)
    {
        foreach (var box in ReadBoxes(payload, 0, payload.Length))
        {
            var boxPayload = payload.Slice(box.PayloadOffset, box.PayloadLength);
            switch (box.Type)
            {
                case "ipco":
                    ParseItemPropertyContainer(boxPayload, context);
                    break;
                case "ipma":
                    ParseItemPropertyAssociations(boxPayload, context);
                    break;
            }
        }
    }

    private static void ParseItemPropertyContainer(ReadOnlySpan<byte> payload, ProbeContext context)
    {
        foreach (var box in ReadBoxes(payload, 0, payload.Length))
        {
            var boxPayload = payload.Slice(box.PayloadOffset, box.PayloadLength);
            var property = new ItemProperty(context.PropertiesByIndex.Count + 1, box.Type);
            switch (box.Type)
            {
                case "colr":
                    property = property with { Color = ParseColorInformation(boxPayload) };
                    break;
                case "pixi":
                    property = property with { PixelInformation = ParsePixelInformation(boxPayload) };
                    break;
                case "auxC":
                    property = property with { AuxiliaryType = ParseAuxiliaryType(boxPayload) };
                    break;
            }

            context.PropertiesByIndex[context.PropertiesByIndex.Count + 1] = property;
        }
    }

    private static void ParseItemPropertyAssociations(ReadOnlySpan<byte> payload, ProbeContext context)
    {
        if (payload.Length < 8)
        {
            return;
        }

        var version = payload[0];
        var flags = (payload[1] << 16) | (payload[2] << 8) | payload[3];
        var offset = 4;
        var entryCount = (int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset, 4));
        offset += 4;
        var usesLargePropertyIndex = (flags & 1) == 1;

        for (var entry = 0; entry < entryCount && offset < payload.Length; entry++)
        {
            int itemId;
            if (version < 1)
            {
                if (offset + 2 > payload.Length)
                {
                    return;
                }

                itemId = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2));
                offset += 2;
            }
            else
            {
                if (offset + 4 > payload.Length)
                {
                    return;
                }

                itemId = (int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset, 4));
                offset += 4;
            }

            if (offset >= payload.Length)
            {
                return;
            }

            var associationCount = payload[offset++];
            var indices = new List<int>(associationCount);
            for (var association = 0; association < associationCount; association++)
            {
                if (usesLargePropertyIndex)
                {
                    if (offset + 2 > payload.Length)
                    {
                        return;
                    }

                    indices.Add(BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2)) & 0x7FFF);
                    offset += 2;
                }
                else
                {
                    if (offset >= payload.Length)
                    {
                        return;
                    }

                    indices.Add(payload[offset++] & 0x7F);
                }
            }

            context.PropertyIndicesByItemId[itemId] = indices.Where(index => index > 0).ToList();
        }
    }

    private static ColorInformation? ParseColorInformation(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
        {
            return null;
        }

        var profileType = ReadType(payload, 0);
        if (profileType is not ("nclx" or "nclc"))
        {
            return ParseIccProfileColorInformation(profileType, payload[4..]);
        }

        if (payload.Length < 11)
        {
            return new ColorInformation(profileType, null, null, null, null);
        }

        return new ColorInformation(
            profileType,
            BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(4, 2)),
            BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(6, 2)),
            BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(8, 2)),
            (payload[10] & 0x80) != 0);
    }

    private static ColorInformation ParseIccProfileColorInformation(string profileType, ReadOnlySpan<byte> profile)
    {
        if (profile.IsEmpty)
        {
            return new ColorInformation(profileType, null, null, null, null);
        }

        var text = BuildSearchableIccText(profile);
        var transfer = text.Contains("HLG", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ARIBSTDB67", StringComparison.OrdinalIgnoreCase)
                ? 18
                : text.Contains("PQ", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("ST2084", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("SMPTEST2084", StringComparison.OrdinalIgnoreCase)
                        ? 16
                        : (int?)null;
        var primaries = text.Contains("BT2100", StringComparison.OrdinalIgnoreCase)
            || text.Contains("BT.2100", StringComparison.OrdinalIgnoreCase)
            || text.Contains("REC2100", StringComparison.OrdinalIgnoreCase)
            || text.Contains("REC.2100", StringComparison.OrdinalIgnoreCase)
            || text.Contains("BT2020", StringComparison.OrdinalIgnoreCase)
            || text.Contains("BT.2020", StringComparison.OrdinalIgnoreCase)
            || text.Contains("REC2020", StringComparison.OrdinalIgnoreCase)
            || text.Contains("REC.2020", StringComparison.OrdinalIgnoreCase)
                ? 9
                : text.Contains("DISPLAYP3", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("P3", StringComparison.OrdinalIgnoreCase)
                        ? 12
                        : (int?)null;

        return new ColorInformation(profileType, primaries, transfer, null, null);
    }

    private static string BuildSearchableIccText(ReadOnlySpan<byte> profile)
    {
        var bytes = profile.ToArray();
        var ascii = Encoding.ASCII.GetString(bytes);
        var utf16Be = Encoding.BigEndianUnicode.GetString(bytes);
        var utf16Le = Encoding.Unicode.GetString(bytes);
        var combined = string.Concat(ascii, "\n", utf16Be, "\n", utf16Le);
        var builder = new StringBuilder(combined.Length);
        foreach (var ch in combined)
        {
            if (!char.IsWhiteSpace(ch) && ch != '\0' && !char.IsControl(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static IReadOnlyList<int> ParsePixelInformation(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 5)
        {
            return [];
        }

        var channelCount = payload[4];
        var bits = new List<int>(channelCount);
        for (var index = 0; index < channelCount && 5 + index < payload.Length; index++)
        {
            bits.Add(payload[5 + index]);
        }

        return bits;
    }

    private static string? ParseAuxiliaryType(ReadOnlySpan<byte> payload)
    {
        if (payload.Length <= 4)
        {
            return null;
        }

        var bytes = payload[4..];
        var terminator = bytes.IndexOf((byte)0);
        if (terminator >= 0)
        {
            bytes = bytes[..terminator];
        }

        return bytes.Length > 0 ? Encoding.UTF8.GetString(bytes) : null;
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
        public string? MajorBrand { get; set; }

        public List<string> CompatibleBrands { get; } = [];

        public int? PrimaryItemId { get; set; }

        public Dictionary<int, string> ItemTypesById { get; } = [];

        public Dictionary<int, ItemProperty> PropertiesByIndex { get; } = [];

        public Dictionary<int, IReadOnlyList<int>> PropertyIndicesByItemId { get; } = [];
    }

    private sealed record Box(string Type, int PayloadOffset, int PayloadLength);

    private sealed record ItemProperty(
        int Index,
        string Type,
        ColorInformation? Color = null,
        IReadOnlyList<int>? PixelInformation = null,
        string? AuxiliaryType = null)
    {
        public IReadOnlyList<int> PixelInformation { get; init; } = PixelInformation ?? [];
    }

    private sealed record ColorInformation(
        string ProfileType,
        int? ColorPrimaries,
        int? TransferCharacteristics,
        int? MatrixCoefficients,
        bool? FullRange)
    {
        public bool HasUsableColorSignal =>
            ColorPrimaries is not null
            || TransferCharacteristics is not null
            || MatrixCoefficients is not null
            || FullRange is not null
            || ProfileType is not ("nclx" or "nclc");
    }
}
