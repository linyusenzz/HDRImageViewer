using System.Text;
using HdrImageViewer.Models;
using HdrImageViewer.Services;
using Xunit;

namespace HdrImageViewer.Tests;

public sealed class LivePhotoProbeTests
{
    [Fact]
    public async Task SameBasenameMov_ReturnsAppleLivePhotoSidecar()
    {
        var directory = CreateTempDirectory();
        try
        {
            var stillPath = Path.Combine(directory, "IMG_0001.jpg");
            var videoPath = Path.Combine(directory, "IMG_0001.mov");
            await File.WriteAllBytesAsync(stillPath, [0xFF, 0xD8, 0xFF, 0xD9]);
            await File.WriteAllBytesAsync(videoPath, [0x00, 0x00, 0x00, 0x00]);

            var media = await LivePhotoProbe.ProbeAsync(stillPath, FileContainerKind.Jpeg);

            Assert.NotNull(media);
            Assert.Equal(CompanionMediaKind.AppleLivePhoto, media.Kind);
            Assert.Equal(videoPath, media.Path);
            Assert.False(media.IsEmbedded);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task JpegMicroVideoOffset_ReturnsEmbeddedMotionPhoto()
    {
        var directory = CreateTempDirectory();
        try
        {
            var stillPath = Path.Combine(directory, "motion.jpg");
            await File.WriteAllBytesAsync(stillPath, CreateMicroVideoJpeg());

            var media = await LivePhotoProbe.ProbeAsync(stillPath, FileContainerKind.Jpeg);

            Assert.NotNull(media);
            Assert.Equal(CompanionMediaKind.AndroidMotionPhoto, media.Kind);
            Assert.True(media.IsEmbedded);
            Assert.Equal(16, media.EmbeddedLength);
            Assert.Equal(new FileInfo(stillPath).Length - 16, media.EmbeddedOffset);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CompanionVideoProbe_DetectsHevcMain10Bt2020Pq()
    {
        var probe = CompanionVideoProbe.Probe(CreateHdrHevcMovie());

        Assert.NotNull(probe);
        Assert.True(probe.HasHdrSignal);
        Assert.Equal("hvc1", probe.SampleEntryType);
        Assert.Equal(1920, probe.Width);
        Assert.Equal(1080, probe.Height);
        Assert.Equal(9, probe.ColorPrimaries);
        Assert.Equal(16, probe.TransferCharacteristics);
        Assert.Equal(9, probe.MatrixCoefficients);
        Assert.Equal(10, probe.LumaBitDepth);
        Assert.Contains("PQ", probe.DisplaySummary);
        Assert.Contains("BT.2020", probe.DisplaySummary);
        Assert.Contains("Main 10", probe.DisplaySummary);
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HdrImageViewer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static byte[] CreateMicroVideoJpeg()
    {
        const string xmp = """
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
              <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description xmlns:GCamera="http://ns.google.com/photos/1.0/camera/"
                                 GCamera:MicroVideo="1"
                                 GCamera:MicroVideoOffset="16" />
              </rdf:RDF>
            </x:xmpmeta>
            """;
        var payload = Encoding.UTF8.GetBytes("http://ns.adobe.com/xap/1.0/\0" + xmp);
        var segmentLength = payload.Length + 2;
        Assert.InRange(segmentLength, 2, ushort.MaxValue);

        var bytes = new List<byte>
        {
            0xFF,
            0xD8,
            0xFF,
            0xE1,
            (byte)(segmentLength >> 8),
            (byte)(segmentLength & 0xFF),
        };
        bytes.AddRange(payload);
        bytes.AddRange([0xFF, 0xDA]);
        bytes.AddRange([0x00, 0x00, 0x00, 0x10, (byte)'f', (byte)'t', (byte)'y', (byte)'p']);
        bytes.AddRange([(byte)'i', (byte)'s', (byte)'o', (byte)'m', 0x00, 0x00, 0x00, 0x00]);
        return bytes.ToArray();
    }

    private static byte[] CreateHdrHevcMovie()
    {
        var ftyp = Box("ftyp", Concat(
            Ascii("qt  "),
            UInt32(0),
            Ascii("qt  "),
            Ascii("heic")));

        var hvcC = Box("hvcC", CreateHevcConfiguration());
        var colr = Box("colr", Concat(
            Ascii("nclx"),
            UInt16(9),
            UInt16(16),
            UInt16(9),
            [0x00]));
        var hvc1 = Box("hvc1", Concat(CreateVisualSampleEntryHeader(1920, 1080), hvcC, colr));
        var stsd = Box("stsd", Concat([0x00, 0x00, 0x00, 0x00], UInt32(1), hvc1));
        var stbl = Box("stbl", stsd);
        var minf = Box("minf", stbl);
        var mdia = Box("mdia", minf);
        var trak = Box("trak", mdia);
        var moov = Box("moov", trak);
        return Concat(ftyp, moov);
    }

    private static byte[] CreateHevcConfiguration()
    {
        var data = new byte[23];
        data[0] = 1;
        data[1] = 0x02; // profile_idc Main 10
        data[12] = 120; // level 4.0
        data[16] = 0xFC | 1; // chroma 4:2:0
        data[17] = 0xF8 | 2; // 10-bit luma
        data[18] = 0xF8 | 2; // 10-bit chroma
        data[21] = 0x0F;
        data[22] = 0;
        return data;
    }

    private static byte[] CreateVisualSampleEntryHeader(ushort width, ushort height)
    {
        var data = new byte[78];
        data[6] = 0;
        data[7] = 1;
        WriteUInt16(data, 24, width);
        WriteUInt16(data, 26, height);
        data[28] = 0x00;
        data[29] = 0x48;
        data[32] = 0x00;
        data[33] = 0x48;
        data[40] = 0;
        data[41] = 1;
        data[74] = 0;
        data[75] = 0x18;
        data[76] = 0xFF;
        data[77] = 0xFF;
        return data;
    }

    private static byte[] Box(string type, params byte[] payload)
    {
        var bytes = new byte[8 + payload.Length];
        WriteUInt32(bytes, 0, (uint)bytes.Length);
        Encoding.ASCII.GetBytes(type, bytes.AsSpan(4, 4));
        payload.CopyTo(bytes.AsSpan(8));
        return bytes;
    }

    private static byte[] UInt16(ushort value)
    {
        var bytes = new byte[2];
        WriteUInt16(bytes, 0, value);
        return bytes;
    }

    private static byte[] UInt32(uint value)
    {
        var bytes = new byte[4];
        WriteUInt32(bytes, 0, value);
        return bytes;
    }

    private static byte[] Ascii(string value)
    {
        return Encoding.ASCII.GetBytes(value);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var bytes = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(bytes.AsSpan(offset));
            offset += part.Length;
        }

        return bytes;
    }

    private static void WriteUInt16(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)(value & 0xFF);
    }

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)((value >> 16) & 0xFF);
        data[offset + 2] = (byte)((value >> 8) & 0xFF);
        data[offset + 3] = (byte)(value & 0xFF);
    }
}
