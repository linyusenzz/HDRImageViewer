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
    public void AdobeGainMapAfterPreviews_UsesMarkedImageAndKeepsPrimaryBoundary()
    {
        // Camera layout: version-only primary XMP plus ISO metadata, followed
        // by previews and then the full-resolution gain-map auxiliary.
        var primary = CreateJpeg(CreateAdobeXmpSegment(false), CreateApp2Segment(CreateIsoMetadataPayload()));
        var preview = CreateJpeg();
        var gainMap = CreateJpeg(CreateAdobeXmpSegment(true));
        var padding = new byte[11];
        var data = CreateJpegContainer(primary, padding, preview, preview, gainMap, CreateJpeg());

        var probe = GainMapJpegProbe.Probe(data);

        Assert.True(probe.IsRenderableUltraHdr);
        Assert.Equal(primary.Length, probe.PrimaryImageEndOffset);
        Assert.Equal(primary.Length + padding.Length + (2 * preview.Length), probe.GainMapOffset);
        Assert.Equal(gainMap.Length, probe.GainMapLength);
        Assert.Equal("2.246, 2.246, 2.246", probe.Metadata!.GainMapMax);
        Assert.Equal("2.246", probe.Metadata.HdrCapacityMax);
    }

    [Fact]
    public void IsoGainMapAfterPreview_VersionOnlyXmpDoesNotHideIsoParameters()
    {
        var primary = CreateJpeg(CreateAdobeXmpSegment(false));
        var preview = CreateJpeg();
        var gainMap = CreateJpeg(CreateAdobeXmpSegment(false), CreateApp2Segment(CreateIsoMetadataPayload()));

        var probe = GainMapJpegProbe.Probe(CreateJpegContainer(primary, preview, gainMap));

        Assert.True(probe.IsRenderableUltraHdr);
        Assert.Equal(primary.Length + preview.Length, probe.GainMapOffset);
        Assert.Equal(gainMap.Length, probe.GainMapLength);
        Assert.Equal("2.148445", probe.Metadata!.HdrCapacityMax);
        Assert.Equal("2.169925, 2.169925, 2.169925", probe.Metadata.GainMapMax);
    }

    [Fact]
    public void IncompleteMarkedPreview_DoesNotHideLaterCompleteGainMap()
    {
        var primary = CreateJpeg();
        var preview = CreateJpeg(CreateAdobeXmpSegment(false));
        var gainMap = CreateJpeg(CreateApp2Segment(CreateIsoMetadataPayload()));

        var probe = GainMapJpegProbe.Probe(CreateJpegContainer(primary, preview, gainMap));

        Assert.True(probe.IsRenderableUltraHdr);
        Assert.Equal(primary.Length + preview.Length, probe.GainMapOffset);
    }

    [Fact]
    public void AppleGainMapAfterPreview_UsesAuxiliaryMarker()
    {
        var primary = CreateJpeg();
        var preview = CreateJpeg();
        var gainMap = CreateJpeg(CreateXmpSegment("""
            <rdf:Description xmlns:apple="http://ns.apple.com/HDRGainMap/1.0/"
              apple:HDRGainMapHeadroom="4"
              AuxiliaryImageType="urn:com:apple:photo:2020:aux:hdrgainmap" />
            """));

        var probe = GainMapJpegProbe.Probe(CreateJpegContainer(primary, preview, gainMap));

        Assert.True(probe.IsRenderableUltraHdr);
        Assert.True(probe.HasAppleHdrGainMapSignal);
        Assert.Equal(primary.Length + preview.Length, probe.GainMapOffset);
        Assert.StartsWith("Apple HDRGainMap", probe.Metadata!.Source);
        Assert.Equal("2", probe.Metadata.HdrCapacityMax);
    }

    [Fact]
    public void SingleUnmarkedAuxiliary_WithCompletePrimaryMetadata_RemainsSupported()
    {
        var primary = CreateJpeg(CreateAdobeXmpSegment(true));
        var gainMap = CreateJpeg();

        var probe = GainMapJpegProbe.Probe(CreateJpegContainer(primary, gainMap));

        Assert.True(probe.IsRenderableUltraHdr);
        Assert.Equal(primary.Length, probe.GainMapOffset);
        Assert.Equal(gainMap.Length, probe.GainMapLength);
    }

    [Fact]
    public void MultipleUnmarkedAuxiliaries_DoNotGuessWhichIsGainMap()
    {
        var primary = CreateJpeg(CreateAdobeXmpSegment(true));

        var probe = GainMapJpegProbe.Probe(CreateJpegContainer(primary, CreateJpeg(), CreateJpeg()));

        Assert.False(probe.IsRenderableUltraHdr);
        Assert.False(probe.HasGainMapImage);
        Assert.Null(probe.GainMapOffset);
        Assert.Null(probe.GainMapLength);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnmarkedPreview_WithoutReconstructionMetadata_IsNotGainMap(bool hasHdrVersion)
    {
        var primary = hasHdrVersion ? CreateJpeg(CreateAdobeXmpSegment(false)) : CreateJpeg();

        var probe = GainMapJpegProbe.Probe(CreateJpegContainer(primary, CreateJpeg()));

        Assert.False(probe.IsRenderableUltraHdr);
        Assert.False(probe.HasGainMapImage);
        Assert.Null(probe.GainMapOffset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TruncatedAuxiliary_DoesNotConsumeFollowingJpeg(bool inEntropy)
    {
        var primary = CreateJpeg(CreateAdobeXmpSegment(true));
        byte[] truncated = inEntropy ? [0xFF, 0xD8, 0xFF, 0xDA, 0, 2, 1, 2] : [0xFF, 0xD8];
        var later = CreateJpeg(CreateAdobeXmpSegment(true));

        var probe = GainMapJpegProbe.Probe(CreateJpegContainer(primary, truncated, later));

        Assert.False(probe.HasGainMapImage);
        Assert.False(probe.IsRenderableUltraHdr);
        Assert.Equal(primary.Length, probe.PrimaryImageEndOffset);
    }

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
        return CreateSegment(0xE2, payload);
    }

    private static byte[] CreateAdobeXmpSegment(bool complete)
    {
        var values = complete
            ? "hdrgm:GainMapMax=\"2.24578, 2.24578, 2.24578\" hdrgm:HDRCapacityMax=\"2.24578\" hdrgm:BaseRenditionIsHDR=\"False\""
            : string.Empty;
        return CreateXmpSegment($"<rdf:Description xmlns:hdrgm=\"http://ns.adobe.com/hdr-gain-map/1.0/\" hdrgm:Version=\"1.0\" {values} />");
    }

    private static byte[] CreateXmpSegment(string description)
    {
        var packet = $"http://ns.adobe.com/xap/1.0/\0<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">{description}</rdf:RDF>";
        return CreateSegment(0xE1, Encoding.UTF8.GetBytes(packet));
    }

    private static byte[] CreateSegment(byte marker, byte[] payload)
    {
        var segment = new byte[payload.Length + 4];
        segment[0] = 0xFF;
        segment[1] = marker;
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
