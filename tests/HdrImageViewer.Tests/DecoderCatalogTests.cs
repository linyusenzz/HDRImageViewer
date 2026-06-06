using HdrImageViewer.Models;
using HdrImageViewer.Services;
using Xunit;

namespace HdrImageViewer.Tests;

public class DecoderCatalogTests
{
    [Fact]
    public void HeifAuxiliaryGainMap_RemainsRenderableGainMap()
    {
        var probe = new HeifAvifProbeResult(
            true,
            "heic",
            ["mif1", "heic"],
            1,
            "grid",
            ["hvc1", "grid", "mime"],
            "prof",
            null,
            null,
            null,
            null,
            10,
            HasHdrTransfer: false,
            HasBt2020: false,
            HasGainMapAuxiliary: true,
            HasAppleHdrGainMapSignal: true);

        var descriptor = DecoderCatalog.Describe("sample.heic", heifAvifProbe: probe, containerKind: FileContainerKind.HeifFamily);
        var document = new HdrImageDocument("sample.heic", "sample.heic", descriptor, HeifAvifProbe: probe);

        Assert.Equal(HdrImageKind.GainMap, descriptor.Kind);
        Assert.True(document.HasGainMapSignal);
        Assert.True(document.HasRenderableGainMap);
        Assert.Contains("重建", descriptor.TransferFunction);
        Assert.DoesNotContain("metadata-only", descriptor.TransferFunction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HeifTmapWithoutAuxiliary_IsRenderableGainMap()
    {
        var probe = new HeifAvifProbeResult(
            true,
            "avif",
            ["avif", "mif1", "tmap"],
            1,
            "av01",
            ["av01", "mime", "tmap"],
            "nclx",
            9,
            1,
            9,
            true,
            10,
            HasHdrTransfer: false,
            HasBt2020: true,
            HasGainMapAuxiliary: false,
            HasAppleHdrGainMapSignal: false,
            HasIsoGainMapSignal: true);

        var descriptor = DecoderCatalog.Describe("sample.avif", heifAvifProbe: probe, containerKind: FileContainerKind.HeifFamily);
        var document = new HdrImageDocument("sample.avif", "sample.avif", descriptor, HeifAvifProbe: probe);

        Assert.Equal(HdrImageKind.GainMap, descriptor.Kind);
        Assert.True(document.HasGainMapSignal);
        Assert.True(document.HasRenderableGainMap);
        Assert.Contains("重建", descriptor.TransferFunction);
        Assert.DoesNotContain("metadata-only", descriptor.TransferFunction, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("未启用", descriptor.SupportStatus);
    }

    [Fact]
    public void JxlGainMapBox_IsRenderableGainMap()
    {
        var probe = new JxlProbeResult(
            true,
            1024,
            1024,
            16,
            "sRGB",
            "sRGB / BT.709",
            null,
            null,
            "JPEG XL file format container",
            HasGainMapBox: true);

        var descriptor = DecoderCatalog.Describe("sample.jxl", jxlProbe: probe);
        var document = new HdrImageDocument("sample.jxl", "sample.jxl", descriptor, JxlProbe: probe);

        Assert.Equal(HdrImageKind.GainMap, descriptor.Kind);
        Assert.True(document.HasGainMapSignal);
        Assert.True(document.HasRenderableGainMap);
        Assert.Contains("重建", descriptor.TransferFunction);
        Assert.DoesNotContain("metadata-only", descriptor.TransferFunction, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not implemented", descriptor.SupportStatus, StringComparison.OrdinalIgnoreCase);
    }
}
