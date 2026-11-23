using HdrImageViewer.Models;
using HdrImageViewer.Services;
using Xunit;

namespace HdrImageViewer.WindowsTests;

public sealed class AuditWindowsOptimizationsTests
{
    [Fact]
    public void ResolveSdrBaseColorGamut_HonorsExplicitChoicesAndAutoDocumentSignals()
    {
        var srgbProbe = new GainMapProbeResult(
            IsJpeg: true,
            HasUltraHdrSignal: true,
            HasGContainerDirectory: true,
            HasGainMapItem: true,
            HasGainMapImage: true,
            HasIso21496Signal: false,
            HasAppleHdrGainMapSignal: false,
            ExifOrientation: 1,
            HasPrimaryIccProfile: true,
            PrimaryColorGamut: GainMapColorGamut.Bt709,
            PrimaryImageEndOffset: 100,
            GainMapOffset: 100,
            GainMapLength: 50,
            Metadata: new GainMapMetadata("1.0", "0", "2", "1", "0", "0", "0", "2", false));
        var srgbDoc = new HdrImageDocument(
            "srgb.jpg",
            "srgb.jpg",
            DecoderCatalog.Describe("srgb.jpg", gainMapProbe: srgbProbe),
            GainMapProbe: srgbProbe);

        Assert.Equal(
            UltraHdrSdrBaseColorGamut.Bt709,
            GainMapHdrExportService.ResolveSdrBaseColorGamut(UltraHdrSdrBaseColorGamut.Bt709, srgbDoc));
        Assert.Equal(
            UltraHdrSdrBaseColorGamut.DisplayP3,
            GainMapHdrExportService.ResolveSdrBaseColorGamut(UltraHdrSdrBaseColorGamut.DisplayP3, srgbDoc));
        Assert.Equal(
            UltraHdrSdrBaseColorGamut.Bt2100,
            GainMapHdrExportService.ResolveSdrBaseColorGamut(UltraHdrSdrBaseColorGamut.Bt2100, srgbDoc));
        Assert.Equal(
            UltraHdrSdrBaseColorGamut.Bt709,
            GainMapHdrExportService.ResolveSdrBaseColorGamut(UltraHdrSdrBaseColorGamut.Auto, srgbDoc));

        var exrProbe = new ExrProbeResult(
            IsOpenExr: true,
            PixelWidth: 64,
            PixelHeight: 64,
            DecoderName: "OpenEXR",
            Status: "OK",
            UsesBt2020Primaries: true);
        var hdrExrDoc = new HdrImageDocument(
            "scene.exr",
            "scene.exr",
            DecoderCatalog.Describe("scene.exr", exrProbe: exrProbe),
            ExrProbe: exrProbe);
        Assert.Equal(
            UltraHdrSdrBaseColorGamut.DisplayP3,
            GainMapHdrExportService.ResolveSdrBaseColorGamut(UltraHdrSdrBaseColorGamut.Auto, hdrExrDoc));
    }

    [Fact]
    public async Task BatchImageExportService_ValidatesExtendedFormatRange()
    {
        var item = new BatchExportItem("sample.exr");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => BatchImageExportService.ExportAsync(item, Path.GetTempPath(), -1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => BatchImageExportService.ExportAsync(item, Path.GetTempPath(), 14, CancellationToken.None));
    }
}
