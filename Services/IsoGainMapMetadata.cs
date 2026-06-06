using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using HdrImageViewer.Models;
using HdrImageViewer.Rendering;

namespace HdrImageViewer.Services;

internal enum IsoGainMapMetadataPayloadKind
{
    JpegXl,
    HeifTmap,
}

internal sealed record IsoGainMapMetadata(
    Vector3 GainMapMin,
    Vector3 GainMapMax,
    Vector3 Gamma,
    Vector3 OffsetSdr,
    Vector3 OffsetHdr,
    float BaseHdrHeadroom,
    float AlternateHdrHeadroom,
    bool UseBaseColorSpace)
{
    public GainMapShaderConstants CreateConstants(
        GainMapColorGamut primaryColorGamut,
        float baseTransfer = HdrColorMath.GainMapBaseTransferSrgb,
        float orientation = 1.0f)
    {
        return new GainMapShaderConstants
        {
            GainMapMin = ToVector4(GainMapMin),
            GainMapMax = ToVector4(GainMapMax),
            Gamma = ToVector4(Gamma),
            OffsetSdr = ToVector4(OffsetSdr),
            OffsetHdr = ToVector4(OffsetHdr),
            GainMapControl = new Vector4(1.0f, 0.0f, ToShaderGamut(primaryColorGamut), 0.0f),
            SourceEncoding = new Vector4(baseTransfer, 0.0f, 0.0f, 0.0f),
            Orientation = new Vector4(orientation, 0.0f, 0.0f, 0.0f),
            HdrCapacity = new Vector4(BaseHdrHeadroom, AlternateHdrHeadroom, 0.0f, 0.0f),
        };
    }

    private static Vector4 ToVector4(Vector3 value)
    {
        return new Vector4(value, 0.0f);
    }

    private static float ToShaderGamut(GainMapColorGamut gamut)
    {
        return gamut switch
        {
            GainMapColorGamut.DisplayP3 => 1.0f,
            GainMapColorGamut.Bt2100 => 2.0f,
            GainMapColorGamut.ProPhoto => 3.0f,
            _ => 0.0f,
        };
    }
}

internal static partial class IsoGainMapMetadataParser
{
    public static IsoGainMapMetadata Parse(ReadOnlySpan<byte> payload, IsoGainMapMetadataPayloadKind payloadKind)
    {
        var offset = payloadKind == IsoGainMapMetadataPayloadKind.HeifTmap ? 5 : 4;
        if (payload.Length <= offset)
        {
            throw new InvalidOperationException("ISO gain-map metadata is truncated.");
        }

        var flags = payload[offset++];
        if ((flags & 0x3F) != 0)
        {
            throw new InvalidOperationException($"ISO gain-map metadata has unsupported reserved flag bits: 0x{flags:X2}.");
        }

        var isMultichannel = (flags & 0x80) != 0;
        var useBaseColorSpace = (flags & 0x40) != 0;
        var baseHeadroom = ReadUnsignedFraction(payload, ref offset);
        var alternateHeadroom = ReadUnsignedFraction(payload, ref offset);
        var channelCount = isMultichannel ? 3 : 1;
        Span<float> min = stackalloc float[3];
        Span<float> max = stackalloc float[3];
        Span<float> gamma = stackalloc float[3];
        Span<float> offsetSdr = stackalloc float[3];
        Span<float> offsetHdr = stackalloc float[3];

        for (var channel = 0; channel < channelCount; channel++)
        {
            min[channel] = ReadSignedFraction(payload, ref offset);
            max[channel] = ReadSignedFraction(payload, ref offset);
            gamma[channel] = ReadUnsignedFraction(payload, ref offset);
            offsetSdr[channel] = ReadSignedFraction(payload, ref offset);
            offsetHdr[channel] = ReadSignedFraction(payload, ref offset);
        }

        if (!isMultichannel)
        {
            for (var channel = 1; channel < 3; channel++)
            {
                min[channel] = min[0];
                max[channel] = max[0];
                gamma[channel] = gamma[0];
                offsetSdr[channel] = offsetSdr[0];
                offsetHdr[channel] = offsetHdr[0];
            }
        }

        return new IsoGainMapMetadata(
            new Vector3(min[0], min[1], min[2]),
            new Vector3(max[0], max[1], max[2]),
            new Vector3(gamma[0], gamma[1], gamma[2]),
            new Vector3(offsetSdr[0], offsetSdr[1], offsetSdr[2]),
            new Vector3(offsetHdr[0], offsetHdr[1], offsetHdr[2]),
            baseHeadroom,
            alternateHeadroom,
            useBaseColorSpace);
    }

    public static IsoGainMapMetadata ParseAvifGainMapUtilOutput(string output)
    {
        return new IsoGainMapMetadata(
            ReadVector(output, "Gain Map Min"),
            ReadVector(output, "Gain Map Max"),
            ReadVector(output, "Gain Map Gamma"),
            ReadVector(output, "Base Offset"),
            ReadVector(output, "Alternate Offset"),
            ReadScalar(output, "Base headroom"),
            ReadScalar(output, "Alternate headroom"),
            ReadBoolean(output, "Use Base Color Space"));
    }

    private static float ReadSignedFraction(ReadOnlySpan<byte> data, ref int offset)
    {
        EnsureAvailable(data, offset, 8);
        var numerator = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset, 4));
        var denominator = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 4, 4));
        offset += 8;
        if (denominator == 0)
        {
            throw new InvalidOperationException("ISO gain-map metadata contains a zero denominator.");
        }

        return numerator / (float)denominator;
    }

    private static float ReadUnsignedFraction(ReadOnlySpan<byte> data, ref int offset)
    {
        EnsureAvailable(data, offset, 8);
        var numerator = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
        var denominator = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 4, 4));
        offset += 8;
        if (denominator == 0)
        {
            throw new InvalidOperationException("ISO gain-map metadata contains a zero denominator.");
        }

        return numerator / (float)denominator;
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> data, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > data.Length)
        {
            throw new InvalidOperationException("ISO gain-map metadata is truncated.");
        }
    }

    private static float ReadScalar(string output, string label)
    {
        var line = FindLine(output, label);
        var match = NumberRegex().Match(line);
        return match.Success && float.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidOperationException($"avifgainmaputil metadata did not include a numeric value for {label}.");
    }

    private static Vector3 ReadVector(string output, string label)
    {
        var line = FindLine(output, label);
        var matches = ChannelValueRegex().Matches(line);
        if (matches.Count >= 3)
        {
            return new Vector3(
                ParseFloat(matches[0].Groups["value"].Value, label),
                ParseFloat(matches[1].Groups["value"].Value, label),
                ParseFloat(matches[2].Groups["value"].Value, label));
        }

        var scalar = ReadScalar(output, label);
        return new Vector3(scalar, scalar, scalar);
    }

    private static bool ReadBoolean(string output, string label)
    {
        var line = FindLine(output, label);
        return line.Contains("true", StringComparison.OrdinalIgnoreCase)
            || line.Contains("yes", StringComparison.OrdinalIgnoreCase)
            || line.Contains("1", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindLine(string output, string label)
    {
        return output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.Contains(label, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"avifgainmaputil metadata did not include {label}.");
    }

    private static float ParseFloat(string value, string label)
    {
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"avifgainmaputil metadata contains an invalid numeric value for {label}: {value}.");
    }

    [GeneratedRegex(@"[-+]?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"\b[RGB]\s+(?<value>[-+]?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelValueRegex();
}
