using System.Buffers.Binary;
using System.Text;
using HdrImageViewer.Models;

namespace HdrImageViewer.Services;

internal static class IccColorProfileDetector
{
    private const int HeaderLength = 128;
    private const int TagEntryLength = 12;
    private const int Bt709Primaries = 1;
    private const int Bt2020Primaries = 9;
    private const int DciP3Primaries = 11;
    private const int DisplayP3Primaries = 12;
    private const int Bt709Transfer = 1;
    private const int PqTransfer = 16;
    private const int HlgTransfer = 18;

    private static readonly double[][] SrgbD50 =
    [
        [0.436066, 0.222488, 0.013916],
        [0.385147, 0.716873, 0.097076],
        [0.143066, 0.060608, 0.714096],
    ];

    private static readonly double[][] DisplayP3D50 =
    [
        [0.515102, 0.241182, -0.001049],
        [0.291965, 0.692236, 0.041882],
        [0.157153, 0.066582, 0.784378],
    ];

    private static readonly double[][] Rec2020D50 =
    [
        [0.673459, 0.279033, -0.001931],
        [0.165661, 0.675338, 0.029979],
        [0.125100, 0.045629, 0.797162],
    ];

    private static readonly double[][] AdobeRgb1998D50 =
    [
        [0.609741, 0.311111, 0.019470],
        [0.205276, 0.625671, 0.060867],
        [0.149185, 0.063217, 0.744568],
    ];

    private static readonly double[][] ProPhotoRommD50 =
    [
        [0.797675, 0.288040, 0.000000],
        [0.135192, 0.711874, 0.000000],
        [0.031353, 0.000086, 0.824905],
    ];

    public static GainMapColorGamut DetectColorGamut(ReadOnlySpan<byte> profile)
    {
        var parsed = DetectColorGamutFromTags(profile);
        return parsed == GainMapColorGamut.Unknown
            ? DetectColorGamutFromTextTags(profile)
            : parsed;
    }

    public static int? DetectColorPrimaries(ReadOnlySpan<byte> profile)
    {
        var cicpPrimaries = TryReadCicpPrimaries(profile);
        if (cicpPrimaries is Bt709Primaries or Bt2020Primaries or DisplayP3Primaries)
        {
            return cicpPrimaries;
        }

        if (cicpPrimaries == DciP3Primaries)
        {
            return DisplayP3Primaries;
        }

        return DetectColorGamut(profile) switch
        {
            GainMapColorGamut.Bt709 => Bt709Primaries,
            GainMapColorGamut.Bt2100 => Bt2020Primaries,
            GainMapColorGamut.DisplayP3 => DisplayP3Primaries,
            _ => null,
        };
    }

    public static int? DetectTransferCharacteristics(ReadOnlySpan<byte> profile)
    {
        var cicpTransfer = TryReadCicpTransfer(profile);
        if (cicpTransfer is Bt709Transfer or PqTransfer or HlgTransfer)
        {
            return cicpTransfer;
        }

        var text = ReadMetadataTextTags(profile);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (ContainsAny(text, "HLG", "ARIB STD-B67", "ARIB-STD-B67", "ARIBSTDB67", "Hybrid Log-Gamma"))
        {
            return HlgTransfer;
        }

        if (ContainsAny(text, "SMPTE ST 2084", "ST 2084", "ST2084", "SMPTEST2084", "Perceptual Quantizer", " PQ"))
        {
            return PqTransfer;
        }

        return ContainsAny(text, "BT.709", "BT709", "Rec.709", "Rec. 709", "Rec709")
            ? Bt709Transfer
            : null;
    }

    public static string? ExtractProfileDescription(ReadOnlySpan<byte> profile)
    {
        if (TryFindTag(profile, "desc") is { } descTag)
        {
            var descText = ReadTextTag(profile.Slice(descTag.Offset, descTag.Length), firstRecordOnly: true);
            if (!string.IsNullOrWhiteSpace(descText))
            {
                return descText.Trim();
            }
        }

        if (TryFindTag(profile, "dmdd") is { } dmddTag)
        {
            var dmddText = ReadTextTag(profile.Slice(dmddTag.Offset, dmddTag.Length), firstRecordOnly: true);
            if (!string.IsNullOrWhiteSpace(dmddText))
            {
                return dmddText.Trim();
            }
        }

        if (profile.Length < HeaderLength + 4)
        {
            var descIndex = profile.IndexOf("desc"u8);
            if (descIndex >= 0)
            {
                var slice = profile.Slice(descIndex);
                var direct = ReadTextTag(slice, firstRecordOnly: true);
                if (!string.IsNullOrWhiteSpace(direct))
                {
                    return direct.Trim();
                }

                if (slice.Length >= 16)
                {
                    var altLength = BinaryPrimitives.ReadUInt32BigEndian(slice.Slice(12, 4));
                    if (altLength > 0 && altLength <= 512)
                    {
                        var byteCount = (int)Math.Min(altLength, (uint)(slice.Length - 16));
                        var fallback = CleanTagString(Encoding.ASCII.GetString(slice.Slice(16, byteCount)));
                        if (!string.IsNullOrWhiteSpace(fallback))
                        {
                            return fallback.Trim();
                        }
                    }
                }
            }
        }

        return null;
    }

    private static GainMapColorGamut DetectColorGamutFromTags(ReadOnlySpan<byte> profile)
    {
        if (TryReadCicpPrimaries(profile) is { } primaries)
        {
            var cicpGamut = primaries switch
            {
                Bt709Primaries => GainMapColorGamut.Bt709,
                Bt2020Primaries => GainMapColorGamut.Bt2100,
                DciP3Primaries or DisplayP3Primaries => GainMapColorGamut.DisplayP3,
                _ => GainMapColorGamut.Unknown,
            };
            if (cicpGamut != GainMapColorGamut.Unknown)
            {
                return cicpGamut;
            }
        }

        if (TryReadIccTagMatrix(profile, out var matrix))
        {
            if (MatchesIccMatrix(matrix, SrgbD50))
            {
                return GainMapColorGamut.Bt709;
            }

            if (MatchesIccMatrix(matrix, DisplayP3D50))
            {
                return GainMapColorGamut.DisplayP3;
            }

            if (MatchesIccMatrix(matrix, Rec2020D50))
            {
                return GainMapColorGamut.Bt2100;
            }

            if (MatchesIccMatrix(matrix, AdobeRgb1998D50))
            {
                return GainMapColorGamut.AdobeRgb;
            }

            if (MatchesIccMatrix(matrix, ProPhotoRommD50))
            {
                return GainMapColorGamut.ProPhoto;
            }
        }

        return GainMapColorGamut.Unknown;
    }

    private static GainMapColorGamut DetectColorGamutFromTextTags(ReadOnlySpan<byte> profile)
    {
        var text = ReadMetadataTextTags(profile);
        if (string.IsNullOrWhiteSpace(text))
        {
            return GainMapColorGamut.Unknown;
        }

        if (ContainsAny(text, "Display P3", "Display-P3", "DisplayP3", "DCI-P3", "DCI P3", "P3 D65", "Apple Wide Color"))
        {
            return GainMapColorGamut.DisplayP3;
        }

        if (ContainsAny(text, "Adobe RGB", "AdobeRGB", "ClayRGB"))
        {
            return GainMapColorGamut.AdobeRgb;
        }

        if (ContainsAny(text, "ProPhoto", "ROMM RGB", "ROMM-RGB", "RIMM RGB"))
        {
            return GainMapColorGamut.ProPhoto;
        }

        if (ContainsAny(text, "BT.2020", "BT2020", "Rec.2020", "Rec. 2020", "Rec2020", "BT.2100", "BT2100", "Rec.2100", "Rec. 2100", "Rec2100"))
        {
            return GainMapColorGamut.Bt2100;
        }

        return ContainsAny(text, "sRGB", "IEC 61966-2-1", "BT.709", "BT709", "Rec.709", "Rec. 709", "Rec709")
            ? GainMapColorGamut.Bt709
            : GainMapColorGamut.Unknown;
    }

    private static string ReadMetadataTextTags(ReadOnlySpan<byte> profile)
    {
        StringBuilder? builder = null;
        foreach (var signature in new[] { "desc", "dmdd", "cprt" })
        {
            if (TryFindTag(profile, signature) is not { } tag)
            {
                continue;
            }

            var value = ReadTextTag(profile.Slice(tag.Offset, tag.Length), firstRecordOnly: false);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            builder ??= new StringBuilder();
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(value);
        }

        return builder?.ToString() ?? string.Empty;
    }

    private static string? ReadTextTag(ReadOnlySpan<byte> tagData, bool firstRecordOnly)
    {
        if (tagData.Length < 8)
        {
            return null;
        }

        var typeSignature = ReadAsciiTag(tagData, 0);
        switch (typeSignature)
        {
            case "desc":
                {
                    if (tagData.Length < 12)
                    {
                        return null;
                    }

                    var asciiLength = BinaryPrimitives.ReadUInt32BigEndian(tagData.Slice(8, 4));
                    if (asciiLength == 0)
                    {
                        return null;
                    }

                    var maxAvailable = tagData.Length - 12;
                    var byteCount = (int)Math.Min(asciiLength, (uint)maxAvailable);
                    return CleanTagString(Encoding.ASCII.GetString(tagData.Slice(12, byteCount)));
                }

            case "mluc":
                {
                    if (tagData.Length < 16)
                    {
                        return null;
                    }

                    var recordCount = BinaryPrimitives.ReadUInt32BigEndian(tagData.Slice(8, 4));
                    var recordSize = BinaryPrimitives.ReadUInt32BigEndian(tagData.Slice(12, 4));
                    if (recordCount == 0 || recordCount > 64 || recordSize < 12)
                    {
                        return null;
                    }

                    string? preferredEnglish = null;
                    string? firstText = null;
                    StringBuilder? combined = firstRecordOnly ? null : new StringBuilder();

                    for (uint i = 0; i < recordCount; i++)
                    {
                        var recordOffsetLong = 16L + (i * recordSize);
                        if (recordOffsetLong + 12 > tagData.Length)
                        {
                            break;
                        }

                        var recordOffset = (int)recordOffsetLong;
                        var isEnglish = tagData[recordOffset] == (byte)'e' && tagData[recordOffset + 1] == (byte)'n';
                        var stringByteLength = BinaryPrimitives.ReadUInt32BigEndian(tagData.Slice(recordOffset + 4, 4));
                        var stringByteOffset = BinaryPrimitives.ReadUInt32BigEndian(tagData.Slice(recordOffset + 8, 4));
                        if (stringByteLength == 0
                            || (stringByteLength & 1) != 0
                            || (ulong)stringByteOffset + stringByteLength > (ulong)tagData.Length)
                        {
                            continue;
                        }

                        var decoded = CleanTagString(Encoding.BigEndianUnicode.GetString(
                            tagData.Slice((int)stringByteOffset, (int)stringByteLength)));
                        if (string.IsNullOrWhiteSpace(decoded))
                        {
                            continue;
                        }

                        firstText ??= decoded;
                        if (isEnglish && preferredEnglish is null)
                        {
                            preferredEnglish = decoded;
                        }

                        if (!firstRecordOnly)
                        {
                            if (combined!.Length > 0)
                            {
                                combined.Append(' ');
                            }

                            combined.Append(decoded);
                        }
                    }

                    return firstRecordOnly
                        ? (preferredEnglish ?? firstText)
                        : (combined is { Length: > 0 } ? combined.ToString() : null);
                }

            case "text":
                {
                    return tagData.Length > 8
                        ? CleanTagString(Encoding.ASCII.GetString(tagData[8..]))
                        : null;
                }

            default:
                return null;
        }
    }

    private static string? CleanTagString(string raw)
    {
        var nulIndex = raw.IndexOf('\0');
        var slice = nulIndex >= 0 ? raw[..nulIndex] : raw;
        var trimmed = slice.Trim();
        return trimmed.Length > 0 ? trimmed : null;
    }

    private static int? TryReadCicpPrimaries(ReadOnlySpan<byte> profile)
    {
        return TryFindTag(profile, "cicp") is { Length: >= 12 } cicp && cicp.Offset + 9 <= profile.Length
            ? profile[cicp.Offset + 8]
            : null;
    }

    private static int? TryReadCicpTransfer(ReadOnlySpan<byte> profile)
    {
        return TryFindTag(profile, "cicp") is { Length: >= 12 } cicp && cicp.Offset + 10 <= profile.Length
            ? profile[cicp.Offset + 9]
            : null;
    }

    private static bool TryReadIccTagMatrix(ReadOnlySpan<byte> profile, out double[][] matrix)
    {
        matrix = [];
        var red = TryFindTag(profile, "rXYZ");
        var green = TryFindTag(profile, "gXYZ");
        var blue = TryFindTag(profile, "bXYZ");
        if (red is not { Length: >= 20 } rTag
            || green is not { Length: >= 20 } gTag
            || blue is not { Length: >= 20 } bTag)
        {
            return false;
        }

        if (ReadAsciiTag(profile, rTag.Offset) != "XYZ "
            || ReadAsciiTag(profile, gTag.Offset) != "XYZ "
            || ReadAsciiTag(profile, bTag.Offset) != "XYZ ")
        {
            return false;
        }

        matrix =
        [
            ReadIccXyzTag(profile, rTag.Offset),
            ReadIccXyzTag(profile, gTag.Offset),
            ReadIccXyzTag(profile, bTag.Offset),
        ];
        return true;
    }

    private static IccTag? TryFindTag(ReadOnlySpan<byte> profile, string tagSignature)
    {
        if (profile.Length < HeaderLength + 4)
        {
            return null;
        }

        var tagCount = BinaryPrimitives.ReadInt32BigEndian(profile.Slice(HeaderLength, 4));
        if (tagCount <= 0 || tagCount > 256 || HeaderLength + 4 + (tagCount * TagEntryLength) > profile.Length)
        {
            return null;
        }

        for (var index = 0; index < tagCount; index++)
        {
            var entryOffset = HeaderLength + 4 + (index * TagEntryLength);
            if (ReadAsciiTag(profile, entryOffset) != tagSignature)
            {
                continue;
            }

            var offset = BinaryPrimitives.ReadInt32BigEndian(profile.Slice(entryOffset + 4, 4));
            var length = BinaryPrimitives.ReadInt32BigEndian(profile.Slice(entryOffset + 8, 4));
            if (offset >= 0 && length > 0 && (long)offset + length <= profile.Length)
            {
                return new IccTag(offset, length);
            }
        }

        return null;
    }

    private static double[] ReadIccXyzTag(ReadOnlySpan<byte> profile, int offset)
    {
        return
        [
            ReadS15Fixed16(profile, offset + 8),
            ReadS15Fixed16(profile, offset + 12),
            ReadS15Fixed16(profile, offset + 16),
        ];
    }

    private static bool MatchesIccMatrix(double[][] actual, double[][] expected)
    {
        const double tolerance = 0.003;
        for (var primary = 0; primary < 3; primary++)
        {
            for (var component = 0; component < 3; component++)
            {
                if (Math.Abs(actual[primary][component] - expected[primary][component]) > tolerance)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static double ReadS15Fixed16(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset, 4)) / 65536.0;
    }

    private static string ReadAsciiTag(ReadOnlySpan<byte> data, int offset)
    {
        return Encoding.ASCII.GetString(data.Slice(offset, 4));
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct IccTag(int Offset, int Length);
}
