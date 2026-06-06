using System.Buffers.Binary;
using System.Text;
using HdrImageViewer.Models;

namespace HdrImageViewer.Services;

internal static class IccColorProfileDetector
{
    private const int Bt709Primaries = 1;
    private const int Bt2020Primaries = 9;
    private const int DisplayP3Primaries = 12;
    private const int Bt709Transfer = 1;
    private const int PqTransfer = 16;
    private const int HlgTransfer = 18;

    public static GainMapColorGamut DetectColorGamut(ReadOnlySpan<byte> profile)
    {
        var parsed = DetectColorGamutFromTags(profile);
        return parsed == GainMapColorGamut.Unknown
            ? DetectColorGamutFromText(profile)
            : parsed;
    }

    public static int? DetectColorPrimaries(ReadOnlySpan<byte> profile)
    {
        var cicpPrimaries = TryReadCicpPrimaries(profile);
        if (cicpPrimaries is Bt709Primaries or Bt2020Primaries or DisplayP3Primaries)
        {
            return cicpPrimaries;
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

        var text = BuildSearchableText(profile);
        if (ContainsAny(text, "HLG", "ARIBSTDB67"))
        {
            return HlgTransfer;
        }

        if (ContainsAny(text, "PQ", "ST2084", "SMPTEST2084"))
        {
            return PqTransfer;
        }

        return ContainsAny(text, "BT709", "BT.709", "REC709", "REC.709")
            ? Bt709Transfer
            : null;
    }

    private static GainMapColorGamut DetectColorGamutFromText(ReadOnlySpan<byte> profile)
    {
        var text = BuildSearchableText(profile);
        if (ContainsAny(text, "DISPLAYP3", "DISPLAY-P3", "DISPLAY P3", "P3"))
        {
            return GainMapColorGamut.DisplayP3;
        }

        if (ContainsAny(text, "BT2020", "BT.2020", "REC2020", "REC.2020", "BT2100", "BT.2100", "REC2100", "REC.2100"))
        {
            return GainMapColorGamut.Bt2100;
        }

        return ContainsAny(text, "SRGB", "BT709", "BT.709", "REC709", "REC.709")
            ? GainMapColorGamut.Bt709
            : GainMapColorGamut.Unknown;
    }

    private static GainMapColorGamut DetectColorGamutFromTags(ReadOnlySpan<byte> profile)
    {
        if (TryReadCicpPrimaries(profile) is { } primaries)
        {
            return primaries switch
            {
                Bt709Primaries => GainMapColorGamut.Bt709,
                Bt2020Primaries => GainMapColorGamut.Bt2100,
                DisplayP3Primaries => GainMapColorGamut.DisplayP3,
                _ => GainMapColorGamut.Unknown,
            };
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
        }

        return GainMapColorGamut.Unknown;
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
        if (red is not { Length: 20 }
            || green is not { Length: 20 }
            || blue is not { Length: 20 })
        {
            return false;
        }

        matrix =
        [
            ReadIccXyzTag(profile, red.Offset),
            ReadIccXyzTag(profile, green.Offset),
            ReadIccXyzTag(profile, blue.Offset),
        ];
        return true;
    }

    private static IccTag? TryFindTag(ReadOnlySpan<byte> profile, string tagSignature)
    {
        const int headerLength = 128;
        const int tagEntryLength = 12;
        if (profile.Length < headerLength + 4)
        {
            return null;
        }

        var tagCount = BinaryPrimitives.ReadInt32BigEndian(profile.Slice(headerLength, 4));
        if (tagCount < 0 || tagCount > 256 || headerLength + 4 + (tagCount * tagEntryLength) > profile.Length)
        {
            return null;
        }

        for (var index = 0; index < tagCount; index++)
        {
            var entryOffset = headerLength + 4 + (index * tagEntryLength);
            if (ReadAsciiTag(profile, entryOffset) != tagSignature)
            {
                continue;
            }

            var offset = BinaryPrimitives.ReadInt32BigEndian(profile.Slice(entryOffset + 4, 4));
            var length = BinaryPrimitives.ReadInt32BigEndian(profile.Slice(entryOffset + 8, 4));
            if (offset >= 0 && length >= 0 && offset + length <= profile.Length)
            {
                return new IccTag(offset, length);
            }
        }

        return null;
    }

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
        const double tolerance = 0.002;
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

    private static string BuildSearchableText(ReadOnlySpan<byte> profile)
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

    private static bool ContainsAny(string text, params string[] needles)
    {
        return needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record IccTag(int Offset, int Length);
}
