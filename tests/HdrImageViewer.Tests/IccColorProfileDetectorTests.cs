using System.Buffers.Binary;
using HdrImageViewer.Models;
using HdrImageViewer.Services;
using Xunit;

namespace HdrImageViewer.Tests;

public sealed class IccColorProfileDetectorTests
{
    [Fact]
    public void DetectColorGamut_ReadsDisplayP3MatrixWithoutProfileName()
    {
        var profile = CreateMatrixProfile(
        [
            [0.515102, 0.241182, -0.001049],
            [0.291965, 0.692236, 0.041882],
            [0.157153, 0.066582, 0.784378],
        ]);

        var gamut = IccColorProfileDetector.DetectColorGamut(profile);

        Assert.Equal(GainMapColorGamut.DisplayP3, gamut);
    }

    [Fact]
    public void DetectColorInformation_ReadsCicpPrimariesAndTransfer()
    {
        var profile = CreateCicpProfile(primaries: 9, transfer: 18);

        Assert.Equal(9, IccColorProfileDetector.DetectColorPrimaries(profile));
        Assert.Equal(18, IccColorProfileDetector.DetectTransferCharacteristics(profile));
        Assert.Equal(GainMapColorGamut.Bt2100, IccColorProfileDetector.DetectColorGamut(profile));
    }

    private static byte[] CreateCicpProfile(byte primaries, byte transfer)
    {
        var profile = CreateTagTableProfile(("cicp", 12));
        var offset = ReadTagOffset(profile, 0);
        WriteAscii(profile, offset, "cicp");
        profile[offset + 8] = primaries;
        profile[offset + 9] = transfer;
        profile[offset + 10] = 9;
        profile[offset + 11] = 1;
        return profile;
    }

    private static byte[] CreateMatrixProfile(double[][] matrix)
    {
        var profile = CreateTagTableProfile(("rXYZ", 20), ("gXYZ", 20), ("bXYZ", 20));
        for (var index = 0; index < 3; index++)
        {
            var offset = ReadTagOffset(profile, index);
            WriteAscii(profile, offset, "XYZ ");
            WriteS15Fixed16(profile, offset + 8, matrix[index][0]);
            WriteS15Fixed16(profile, offset + 12, matrix[index][1]);
            WriteS15Fixed16(profile, offset + 16, matrix[index][2]);
        }

        return profile;
    }

    private static byte[] CreateTagTableProfile(params (string Signature, int Length)[] tags)
    {
        const int headerLength = 128;
        const int tagEntryLength = 12;
        var tableLength = 4 + (tags.Length * tagEntryLength);
        var dataOffset = headerLength + tableLength;
        var profile = new byte[dataOffset + tags.Sum(tag => tag.Length)];
        BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(headerLength, 4), tags.Length);

        for (var index = 0; index < tags.Length; index++)
        {
            var entryOffset = headerLength + 4 + (index * tagEntryLength);
            WriteAscii(profile, entryOffset, tags[index].Signature);
            BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(entryOffset + 4, 4), dataOffset);
            BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(entryOffset + 8, 4), tags[index].Length);
            dataOffset += tags[index].Length;
        }

        return profile;
    }

    private static int ReadTagOffset(byte[] profile, int index)
    {
        const int headerLength = 128;
        const int tagEntryLength = 12;
        return BinaryPrimitives.ReadInt32BigEndian(profile.AsSpan(headerLength + 4 + (index * tagEntryLength) + 4, 4));
    }

    private static void WriteS15Fixed16(byte[] data, int offset, double value)
    {
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset, 4), (int)Math.Round(value * 65536.0));
    }

    private static void WriteAscii(byte[] data, int offset, string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            data[offset + i] = (byte)text[i];
        }
    }
}
