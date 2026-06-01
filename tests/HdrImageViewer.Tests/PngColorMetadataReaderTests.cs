using HdrImageViewer.Services;
using Xunit;

namespace HdrImageViewer.Tests;

public class PngColorMetadataReaderTests
{
    private static async Task<PngColorMetadata?> ReadAsync(byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        return await PngColorMetadataReader.ReadAsync(stream);
    }

    [Fact]
    public async Task ReturnsNull_ForNonPngStream()
    {
        var notPng = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
        Assert.Null(await ReadAsync(notPng));
    }

    [Fact]
    public async Task ReturnsNull_ForEmptyStream()
    {
        Assert.Null(await ReadAsync(Array.Empty<byte>()));
    }

    [Fact]
    public async Task ReadsIhdrDimensionsAndBitDepth()
    {
        var png = new PngTestDataBuilder()
            .Ihdr(width: 1920, height: 1080, bitDepth: 16, colorType: 6) // RGBA
            .Idat()
            .Iend()
            .Build();

        var metadata = await ReadAsync(png);

        Assert.NotNull(metadata);
        Assert.Equal(1920u, metadata!.PixelWidth);
        Assert.Equal(1080u, metadata.PixelHeight);
        Assert.Equal(16, metadata.BitsPerChannel);
        Assert.Equal(6, metadata.ColorType);
        Assert.Equal(64, metadata.TotalBitDepth); // 4 channels * 16 bits
    }

    [Fact]
    public async Task ReadsCicp_PqBt2020()
    {
        var png = new PngTestDataBuilder()
            .Ihdr(3840, 2160, 16, 6)
            .Cicp(primaries: 9, transfer: 16, matrix: 0, fullRange: true) // BT.2020 / PQ
            .Idat()
            .Iend()
            .Build();

        var metadata = await ReadAsync(png);

        Assert.NotNull(metadata);
        Assert.Equal(9, metadata!.CicpPrimaries);
        Assert.Equal(16, metadata.CicpTransfer);
        Assert.Equal(0, metadata.CicpMatrix);
        Assert.True(metadata.CicpFullRange);
    }

    [Fact]
    public async Task ReadsIccProfileName_FromKnownToken()
    {
        var profile = System.Text.Encoding.ASCII.GetBytes("some header Display P3 trailing");
        var png = new PngTestDataBuilder()
            .Ihdr(800, 600, 8, 2)
            .Iccp("Display P3", profile)
            .Idat()
            .Iend()
            .Build();

        var metadata = await ReadAsync(png);

        Assert.NotNull(metadata);
        Assert.Equal("Display P3", metadata!.IccProfileName);
    }

    [Fact]
    public async Task ReadsIccProfileName_FromDescTag()
    {
        var profile = PngTestDataBuilder.IccProfileWithDescription("My Custom Profile");
        var png = new PngTestDataBuilder()
            .Ihdr(800, 600, 8, 2)
            .Iccp("custom", profile)
            .Idat()
            .Iend()
            .Build();

        var metadata = await ReadAsync(png);

        Assert.NotNull(metadata);
        Assert.Equal("My Custom Profile", metadata!.IccProfileName);
    }

    /// <summary>
    /// Regression for the early-stop bug: when iCCP precedes the more
    /// authoritative cICP, the scan must keep going and still pick up cICP.
    /// </summary>
    [Fact]
    public async Task DoesNotMissCicp_WhenIccpAppearsFirst()
    {
        var iccProfile = System.Text.Encoding.ASCII.GetBytes("Display P3 profile body");
        var png = new PngTestDataBuilder()
            .Ihdr(3840, 2160, 16, 6)
            .Iccp("Display P3", iccProfile)                                // appears BEFORE cICP
            .Cicp(primaries: 9, transfer: 16, matrix: 0, fullRange: true)  // BT.2020 / PQ
            .Idat()
            .Iend()
            .Build();

        var metadata = await ReadAsync(png);

        Assert.NotNull(metadata);
        // Both signals must survive the scan.
        Assert.Equal("Display P3", metadata!.IccProfileName);
        Assert.Equal(9, metadata.CicpPrimaries);
        Assert.Equal(16, metadata.CicpTransfer);
    }

    [Fact]
    public async Task ReadsBothChunks_WhenCicpAppearsFirst()
    {
        var iccProfile = System.Text.Encoding.ASCII.GetBytes("Display P3 profile body");
        var png = new PngTestDataBuilder()
            .Ihdr(3840, 2160, 16, 6)
            .Cicp(primaries: 9, transfer: 16, matrix: 0, fullRange: true)
            .Iccp("Display P3", iccProfile)
            .Idat()
            .Iend()
            .Build();

        var metadata = await ReadAsync(png);

        Assert.NotNull(metadata);
        Assert.Equal("Display P3", metadata!.IccProfileName);
        Assert.Equal(9, metadata.CicpPrimaries);
    }

    [Fact]
    public async Task SkipsUnrelatedAncillaryChunks()
    {
        var png = new PngTestDataBuilder()
            .Ihdr(1024, 768, 8, 2)
            .Chunk("gAMA", new byte[] { 0x00, 0x00, 0x82, 0x35 })
            .Chunk("pHYs", new byte[9])
            .Cicp(9, 16, 0, true)
            .Idat()
            .Iend()
            .Build();

        var metadata = await ReadAsync(png);

        Assert.NotNull(metadata);
        Assert.Equal(9, metadata!.CicpPrimaries);
        Assert.Equal(1024u, metadata.PixelWidth);
    }

    [Fact]
    public async Task StopsAtIdat_IgnoresTrailingColorChunks()
    {
        // A cICP placed AFTER IDAT is invalid per spec; the reader stops at
        // IDAT and must not pick it up.
        var png = new PngTestDataBuilder()
            .Ihdr(640, 480, 8, 2)
            .Idat()
            .Cicp(9, 16, 0, true)
            .Iend()
            .Build();

        var metadata = await ReadAsync(png);

        Assert.NotNull(metadata);
        Assert.Null(metadata!.CicpTransfer);
    }

    [Fact]
    public async Task ReturnsNull_WhenNoDimensionsOrColorSignal()
    {
        // Valid signature but only an ancillary chunk then IEND.
        var png = new PngTestDataBuilder()
            .Chunk("tEXt", System.Text.Encoding.ASCII.GetBytes("Comment\0hello"))
            .Iend()
            .Build();

        Assert.Null(await ReadAsync(png));
    }

    [Theory]
    [InlineData(0, 8, 8)]    // grayscale, 1 channel
    [InlineData(2, 8, 24)]   // truecolor, 3 channels
    [InlineData(4, 8, 16)]   // grayscale + alpha, 2 channels
    [InlineData(6, 16, 64)]  // truecolor + alpha, 4 channels
    public async Task ComputesTotalBitDepth_PerColorType(byte colorType, byte bitDepth, int expectedTotal)
    {
        var png = new PngTestDataBuilder()
            .Ihdr(100, 100, bitDepth, colorType)
            .Idat()
            .Iend()
            .Build();

        var metadata = await ReadAsync(png);

        Assert.NotNull(metadata);
        Assert.Equal(expectedTotal, metadata!.TotalBitDepth);
    }
}
