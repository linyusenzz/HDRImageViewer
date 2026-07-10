using System.Buffers.Binary;
using System.Text;
using HdrImageViewer.Services;
using Xunit;

namespace HdrImageViewer.Tests;

public class GainMapJpegProbeTests
{
    private const string Iso21496Identifier = "urn:iso:std:iso:ts:21496:-1";
    private const uint FractionDenominator = 1_000_000;

    [Fact]
    public void Iso21496App2MetadataInGainMapImage_IsRenderable()
    {
        var data = CreateJpegContainer(
            CreateJpeg(CreateApp2Segment(CreateIsoIdentifierPayload())),
            CreateJpeg(CreateApp2Segment(CreateIsoMetadataPayload())));

        var probe = GainMapJpegProbe.Probe(data);

        Assert.True(probe.IsJpeg);
        Assert.True(probe.HasIso21496Signal);
        Assert.True(probe.HasGainMapImage);
        Assert.True(probe.IsRenderableUltraHdr);
        Assert.NotNull(probe.Metadata);
        Assert.Contains("ISO 21496-1", probe.Metadata!.Source);
        Assert.Equal("0", probe.Metadata.HdrCapacityMin);
        Assert.Equal("2.148445", probe.Metadata.HdrCapacityMax);
        Assert.Equal("2.169925, 2.169925, 2.169925", probe.Metadata.GainMapMax);
    }

    [Fact]
    public async Task ProbeAsync_LargeJpeg_SkipsWholeContainerAllocation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hdrimageviewer-{Guid.NewGuid():N}.jpg");
        try
        {
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true))
            {
                await stream.WriteAsync(new byte[] { 0xFF, 0xD8 });
                stream.SetLength((64L * 1024L * 1024L) + 1L);
            }

            var probe = await GainMapJpegProbe.ProbeAsync(path);

            Assert.True(probe.IsJpeg);
            Assert.True(probe.IsProbeLimited);
            Assert.False(probe.IsRenderableUltraHdr);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] CreateJpegContainer(params byte[][] images)
    {
        return images.SelectMany(image => image).ToArray();
    }

    private static byte[] CreateJpeg(params byte[][] segments)
    {
        return [0xFF, 0xD8, .. segments.SelectMany(segment => segment), 0xFF, 0xD9];
    }

    private static byte[] CreateApp2Segment(byte[] payload)
    {
        var segment = new byte[payload.Length + 4];
        segment[0] = 0xFF;
        segment[1] = 0xE2;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2, 2), checked((ushort)(payload.Length + 2)));
        payload.CopyTo(segment.AsSpan(4));
        return segment;
    }

    private static byte[] CreateIsoIdentifierPayload()
    {
        return [.. Encoding.ASCII.GetBytes(Iso21496Identifier), 0, 0, 0, 0, 0];
    }

    private static byte[] CreateIsoMetadataPayload()
    {
        var payload = new List<byte>(Encoding.ASCII.GetBytes(Iso21496Identifier));
        payload.Add(0);
        payload.AddRange([0, 0, 0, 0]);
        payload.Add(0x40);
        AddUnsignedFraction(payload, 0);
        AddUnsignedFraction(payload, 2_148_445);
        AddSignedFraction(payload, 0);
        AddSignedFraction(payload, 2_169_925);
        AddUnsignedFraction(payload, FractionDenominator);
        AddSignedFraction(payload, 0);
        AddSignedFraction(payload, 0);
        return payload.ToArray();
    }

    private static void AddUnsignedFraction(List<byte> payload, uint numerator)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(buffer[..4], numerator);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[4..], FractionDenominator);
        payload.AddRange(buffer.ToArray());
    }

    private static void AddSignedFraction(List<byte> payload, int numerator)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(buffer[..4], numerator);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[4..], FractionDenominator);
        payload.AddRange(buffer.ToArray());
    }
}
