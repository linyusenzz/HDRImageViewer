using HdrImageViewer.Models;

namespace HdrImageViewer.Services;

public static class DecoderCatalog
{
    private static readonly IReadOnlyDictionary<string, ImageFormatDescriptor> KnownFormats =
        new Dictionary<string, ImageFormatDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = GainMapJpeg(),
            [".jpeg"] = GainMapJpeg(),
            [".jpe"] = GainMapJpeg(),
            [".avif"] = HeifFamily("AVIF / AVIF HDR"),
            [".heif"] = HeifFamily("HEIF HDR"),
            [".heic"] = HeifFamily("HEIC HDR"),
            [".jxr"] = new("JPEG XR", HdrImageKind.SingleLayerHdr, "WIC JPEG XR", "扩展范围 / scRGB", "WIC 帧", "Windows 原生 HDR 候选格式"),
            [".wdp"] = new("Windows Media Photo", HdrImageKind.SingleLayerHdr, "WIC JPEG XR", "扩展范围 / scRGB", "WIC 帧", "Windows 原生 HDR 候选格式"),
            [".hdp"] = new("HD Photo", HdrImageKind.SingleLayerHdr, "WIC JPEG XR", "扩展范围 / scRGB", "WIC 帧", "Windows 原生 HDR 候选格式"),
            [".exr"] = new("OpenEXR", HdrImageKind.SingleLayerHdr, "计划接入 OpenEXR", "Scene-linear 浮点", "Half/float 通道", "计划接入原生解码器"),
            [".hdr"] = new("Radiance RGBE", HdrImageKind.SingleLayerHdr, "计划接入 RGBE", "Scene-linear RGBE", "RGBE 扫描线", "计划接入原生解码器"),
            [".jxl"] = new("JPEG XL", HdrImageKind.SingleLayerHdr, "计划接入 libjxl", "ICC、PQ、HLG 或 scene-linear", "JXL 码流/容器", "计划接入原生解码器"),
            [".png"] = new("PNG", HdrImageKind.StandardDynamicRange, "WIC PNG", "sRGB/ICC", "WIC 帧", "SDR 基线支持"),
            [".webp"] = new("WebP", HdrImageKind.StandardDynamicRange, "计划接入 WIC/libwebp", "sRGB/ICC", "WIC 帧或原生适配器", "SDR 基线支持"),
            [".tif"] = new("TIFF", HdrImageKind.StandardDynamicRange, "WIC TIFF", "取决于 ICC/profile", "WIC 帧", "SDR/高位深基线支持"),
            [".tiff"] = new("TIFF", HdrImageKind.StandardDynamicRange, "WIC TIFF", "取决于 ICC/profile", "WIC 帧", "SDR/高位深基线支持"),
        };

    public static ImageFormatDescriptor Describe(
        string path,
        GainMapProbeResult? gainMapProbe = null,
        HeifAvifProbeResult? heifAvifProbe = null,
        FileContainerKind containerKind = FileContainerKind.Unknown)
    {
        var extension = Path.GetExtension(path);
        if (containerKind == FileContainerKind.Jpeg || gainMapProbe?.IsJpeg == true || IsJpegExtension(extension))
        {
            return DescribeJpeg(extension, gainMapProbe, containerKind == FileContainerKind.Jpeg);
        }

        if (containerKind == FileContainerKind.HeifFamily || heifAvifProbe?.IsHeifFamily == true || HeifAvifProbe.IsHeifFamilyExtension(extension))
        {
            return DescribeHeifFamily(extension, heifAvifProbe);
        }

        return KnownFormats.TryGetValue(extension, out var descriptor)
            ? descriptor
            : new ImageFormatDescriptor("未知", HdrImageKind.Unknown, "未选择解码器", "未知", "未知", "暂不支持");
    }

    public static bool IsJpegExtension(string extension)
    {
        return string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".jpe", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> FileTypeFilter { get; } =
    [
        ".jpg",
        ".jpeg",
        ".jpe",
        ".jxr",
        ".wdp",
        ".hdp",
        ".avif",
        ".heif",
        ".heic",
        ".jxl",
        ".exr",
        ".hdr",
        ".png",
        ".webp",
        ".tif",
        ".tiff",
    ];

    private static ImageFormatDescriptor GainMapJpeg()
    {
        return new ImageFormatDescriptor(
            "JPEG / Ultra HDR 候选",
            HdrImageKind.StandardDynamicRange,
            "内置 JPEG/XMP 探测",
            "sRGB/ICC 或 gain map 元数据",
            "JPEG APP 标记 / XMP / 可选附加 gain map",
            "需要探测");
    }

    private static ImageFormatDescriptor HeifFamily(string name)
    {
        return new ImageFormatDescriptor(
            name,
            HdrImageKind.SingleLayerHdr,
            "Windows Imaging HEIF 解码器",
            "根据元数据决定 PQ、HLG、ICC 或 SDR",
            "HEIF/AVIF item graph",
            "安装 Windows HEIF/AV1 编解码器后可预览基础图像；原生 HDR 重建仍在开发中");
    }

    private static ImageFormatDescriptor DescribeHeifFamily(string extension, HeifAvifProbeResult? probe)
    {
        var name = string.Equals(extension, ".avif", StringComparison.OrdinalIgnoreCase)
            ? "AVIF HDR"
            : string.Equals(extension, ".heic", StringComparison.OrdinalIgnoreCase) ? "HEIC HDR" : "HEIF HDR";
        if (probe is null || !probe.IsHeifFamily)
        {
            return HeifFamily(name);
        }

        var kind = probe.HasHdrTransfer || probe.HasGainMapAuxiliary
            ? HdrImageKind.SingleLayerHdr
            : HdrImageKind.StandardDynamicRange;
        var decoder = probe.HasGainMapAuxiliary
            ? "Windows Imaging 基础解码 + HEIF gain-map 探测"
            : "Windows Imaging HEIF 解码器 + HEIF 元数据探测";
        var transfer = probe.HasHdrTransfer
            ? probe.TransferSummary
            : probe.TransferSummary == "Unknown" ? "SDR/ICC 或未指定" : probe.TransferSummary;
        var support = probe.DisplayStatus;
        return new ImageFormatDescriptor(
            probe.HasGainMapAuxiliary ? $"{name} gain map" : name,
            kind,
            decoder,
            transfer,
            $"HEIF items; {probe.ColorSummary}",
            support);
    }

    private static ImageFormatDescriptor DescribeJpeg(
        string extension,
        GainMapProbeResult? gainMapProbe,
        bool jpegSignatureDetected)
    {
        var extensionNote = jpegSignatureDetected && !IsJpegExtension(extension)
            ? $" 文件签名是 JPEG/JFIF；已忽略扩展名 {extension}。"
            : string.Empty;

        if (gainMapProbe?.IsRenderableUltraHdr == true)
        {
            return new ImageFormatDescriptor(
                gainMapProbe.HasAppleHdrGainMapSignal && !gainMapProbe.HasUltraHdrSignal ? "Apple HDRGainMap JPEG" : "Ultra HDR JPEG gain map",
                HdrImageKind.GainMap,
                "内置 JPEG/XMP 探测 + D3D11 shader 重建",
                "基础 SDR + 对数 gain map",
                "JPEG + GContainer/MPF 附加 gain map",
                $"已定位 gain map；GPU 重建已启用。{extensionNote}");
        }

        if (gainMapProbe?.HasUltraHdrSignal == true || gainMapProbe?.HasIso21496Signal == true || gainMapProbe?.HasAppleHdrGainMapSignal == true)
        {
            return new ImageFormatDescriptor(
                gainMapProbe.HasAppleHdrGainMapSignal && !gainMapProbe.HasUltraHdrSignal
                    ? "Apple HDRGainMap JPEG"
                    : gainMapProbe.HasIso21496Signal && !gainMapProbe.HasUltraHdrSignal ? "ISO 21496-1 JPEG gain map" : "Ultra HDR JPEG gain map",
                HdrImageKind.GainMap,
                "内置 JPEG 元数据探测",
                "基础 SDR + gain map 元数据",
                "JPEG + GContainer/MPF 附加 gain map",
                $"{gainMapProbe.DisplayStatus}{extensionNote}");
        }

        return new ImageFormatDescriptor(
            "JPEG SDR",
            HdrImageKind.StandardDynamicRange,
            "WIC JPEG",
            "sRGB/ICC",
            "JPEG 主图",
            $"SDR 回退显示。{extensionNote}");
    }
}
