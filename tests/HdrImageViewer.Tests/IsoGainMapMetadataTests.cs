using System.Buffers.Binary;
using HdrImageViewer.Services;
using Xunit;

namespace HdrImageViewer.Tests;

public class IsoGainMapMetadataTests
{
    private const float Tolerance = 0.0001f;

    [Fact]
    public void Parse_SingleChannelPayload_ReplicatesChannelAndReadsFlags()
    {
        var payload = BuildPayload(
            headerLength: 4,
            flags: 0x40, // single channel, use base color space
            fractions:
            [
                (0, 1),      // base headroom = 0
                (3, 1),      // alternate headroom = 3
                (-2, 1),     // min = -2 (signed)
                (4, 2),      // max = 2
                (1, 1),      // gamma = 1
                (1, 64),     // offsetSdr = 0.015625
                (1, 32),     // offsetHdr = 0.03125
            ]);

        var metadata = IsoGainMapMetadataParser.Parse(payload, IsoGainMapMetadataPayloadKind.JpegApp2);

        Assert.Equal(0.0f, metadata.BaseHdrHeadroom, Tolerance);
        Assert.Equal(3.0f, metadata.AlternateHdrHeadroom, Tolerance);
        Assert.True(metadata.UseBaseColorSpace);

        // Single-channel values are replicated to all three channels.
        Assert.Equal(-2.0f, metadata.GainMapMin.X, Tolerance);
        Assert.Equal(metadata.GainMapMin.X, metadata.GainMapMin.Y, Tolerance);
        Assert.Equal(metadata.GainMapMin.X, metadata.GainMapMin.Z, Tolerance);
        Assert.Equal(2.0f, metadata.GainMapMax.X, Tolerance);
        Assert.Equal(1.0f, metadata.Gamma.X, Tolerance);
        Assert.Equal(0.015625f, metadata.OffsetSdr.X, Tolerance);
        Assert.Equal(0.03125f, metadata.OffsetHdr.X, Tolerance);
    }

    [Fact]
    public void Parse_MultichannelPayload_KeepsPerChannelValues()
    {
        var fractions = new List<(long Numerator, uint Denominator)>
        {
            (0, 1), // base headroom
            (2, 1), // alternate headroom
        };
        for (var channel = 0; channel < 3; channel++)
        {
            fractions.Add((channel, 1));     // min
            fractions.Add((channel + 2, 1)); // max
            fractions.Add((1, 1));           // gamma
            fractions.Add((0, 1));           // offsetSdr
            fractions.Add((0, 1));           // offsetHdr
        }

        var payload = BuildPayload(headerLength: 4, flags: 0x80, fractions: [.. fractions]);

        var metadata = IsoGainMapMetadataParser.Parse(payload, IsoGainMapMetadataPayloadKind.JpegApp2);

        Assert.False(metadata.UseBaseColorSpace);
        Assert.Equal(0.0f, metadata.GainMapMin.X, Tolerance);
        Assert.Equal(1.0f, metadata.GainMapMin.Y, Tolerance);
        Assert.Equal(2.0f, metadata.GainMapMin.Z, Tolerance);
        Assert.Equal(2.0f, metadata.GainMapMax.X, Tolerance);
        Assert.Equal(3.0f, metadata.GainMapMax.Y, Tolerance);
        Assert.Equal(4.0f, metadata.GainMapMax.Z, Tolerance);
    }

    [Fact]
    public void Parse_HeifTmapPayload_UsesFiveByteHeader()
    {
        var payload = BuildPayload(
            headerLength: 5,
            flags: 0x00,
            fractions:
            [
                (1, 1), // base headroom
                (2, 1), // alternate headroom
                (0, 1), (1, 1), (1, 1), (0, 1), (0, 1),
            ]);

        var metadata = IsoGainMapMetadataParser.Parse(payload, IsoGainMapMetadataPayloadKind.HeifTmap);

        Assert.Equal(1.0f, metadata.BaseHdrHeadroom, Tolerance);
        Assert.Equal(2.0f, metadata.AlternateHdrHeadroom, Tolerance);
    }

    [Fact]
    public void Parse_RejectsReservedFlagBits()
    {
        var payload = BuildPayload(headerLength: 4, flags: 0x01, fractions: [(1, 1)]);
        Assert.Throws<InvalidOperationException>(
            () => IsoGainMapMetadataParser.Parse(payload, IsoGainMapMetadataPayloadKind.JpegApp2));
    }

    [Fact]
    public void Parse_RejectsTruncatedPayload()
    {
        var payload = BuildPayload(headerLength: 4, flags: 0x00, fractions: [(1, 1)]);
        Assert.Throws<InvalidOperationException>(
            () => IsoGainMapMetadataParser.Parse(payload, IsoGainMapMetadataPayloadKind.JpegApp2));
    }

    [Fact]
    public void Parse_RejectsZeroDenominator()
    {
        var payload = BuildPayload(
            headerLength: 4,
            flags: 0x00,
            fractions: [(1, 0)]);
        Assert.Throws<InvalidOperationException>(
            () => IsoGainMapMetadataParser.Parse(payload, IsoGainMapMetadataPayloadKind.JpegApp2));
    }

    [Fact]
    public void ParseAvifGainMapUtilOutput_ReadsVectorsScalarsAndFlags()
    {
        const string output = """
            Gain Map Min: R 0.000000, G 0.100000, B 0.200000
            Gain Map Max: R 2.300000, G 2.200000, B 2.100000
            Gain Map Gamma: 1.000000
            Base Offset: 0.015625
            Alternate Offset: 0.015625
            Base headroom: 0.000000
            Alternate headroom: 2.300000
            Use Base Color Space: true
            """;

        var metadata = IsoGainMapMetadataParser.ParseAvifGainMapUtilOutput(output);

        Assert.Equal(0.0f, metadata.GainMapMin.X, Tolerance);
        Assert.Equal(0.1f, metadata.GainMapMin.Y, Tolerance);
        Assert.Equal(0.2f, metadata.GainMapMin.Z, Tolerance);
        Assert.Equal(2.3f, metadata.GainMapMax.X, Tolerance);
        Assert.Equal(2.1f, metadata.GainMapMax.Z, Tolerance);

        // Scalar lines are replicated across channels.
        Assert.Equal(1.0f, metadata.Gamma.X, Tolerance);
        Assert.Equal(1.0f, metadata.Gamma.Z, Tolerance);
        Assert.Equal(0.015625f, metadata.OffsetSdr.X, Tolerance);
        Assert.Equal(0.0f, metadata.BaseHdrHeadroom, Tolerance);
        Assert.Equal(2.3f, metadata.AlternateHdrHeadroom, Tolerance);
        Assert.True(metadata.UseBaseColorSpace);
    }

    [Fact]
    public void ParseAvifGainMapUtilOutput_ThrowsWhenLabelMissing()
    {
        Assert.Throws<InvalidOperationException>(
            () => IsoGainMapMetadataParser.ParseAvifGainMapUtilOutput("unrelated text"));
    }

    private static byte[] BuildPayload(int headerLength, byte flags, (long Numerator, uint Denominator)[] fractions)
    {
        var payload = new byte[headerLength + 1 + (fractions.Length * 8)];
        payload[headerLength] = flags;
        var offset = headerLength + 1;
        foreach (var (numerator, denominator) in fractions)
        {
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(offset, 4), checked((int)numerator));
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(offset + 4, 4), denominator);
            offset += 8;
        }

        return payload;
    }
}
