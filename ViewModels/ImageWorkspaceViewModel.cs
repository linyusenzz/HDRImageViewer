using HdrImageViewer.Infrastructure;
using HdrImageViewer.Models;
using HdrImageViewer.Services;

namespace HdrImageViewer.ViewModels;

public sealed class ImageWorkspaceViewModel : ObservableObject
{
    private string _fileName = "未打开图片";
    private string _filePath = string.Empty;
    private string _formatName = "无";
    private string _hdrKind = "无";
    private string _decoder = "等待中";
    private string _transferFunction = "未知";
    private string _colorContainer = "未知";
    private string _supportStatus = "请选择一张图片";
    private string _gainMapStatus = "未探测";
    private string _gainMapLocation = "无";
    private string _gainMapMetadata = "无";
    private string _jpegMetadata = "无";
    private string _companionMediaSummary = "无";
    private string _companionVideoHdrSummary = "无";
    private string _companionVideoStatus = "无";
    private string _exifSummary = "没有 EXIF 元数据";
    private string _renderStatus = "渲染器等待中";
    private string _status = "就绪";
    private bool _hasImage;
    private bool _hasStatus = true;
    private bool _hasCompanionMedia;
    private bool _isCompanionMediaMuted = true;
    private string _companionMediaLabel = "动态";

    public string FileName
    {
        get => _fileName;
        private set => SetProperty(ref _fileName, value);
    }

    public string FilePath
    {
        get => _filePath;
        private set => SetProperty(ref _filePath, value);
    }

    public string FormatName
    {
        get => _formatName;
        private set => SetProperty(ref _formatName, value);
    }

    public string HdrKind
    {
        get => _hdrKind;
        private set => SetProperty(ref _hdrKind, value);
    }

    public string Decoder
    {
        get => _decoder;
        private set => SetProperty(ref _decoder, value);
    }

    public string TransferFunction
    {
        get => _transferFunction;
        private set => SetProperty(ref _transferFunction, value);
    }

    public string ColorContainer
    {
        get => _colorContainer;
        private set => SetProperty(ref _colorContainer, value);
    }

    public string SupportStatus
    {
        get => _supportStatus;
        private set => SetProperty(ref _supportStatus, value);
    }

    public string GainMapStatus
    {
        get => _gainMapStatus;
        private set => SetProperty(ref _gainMapStatus, value);
    }

    public string GainMapLocation
    {
        get => _gainMapLocation;
        private set => SetProperty(ref _gainMapLocation, value);
    }

    public string GainMapMetadata
    {
        get => _gainMapMetadata;
        private set => SetProperty(ref _gainMapMetadata, value);
    }

    public string JpegMetadata
    {
        get => _jpegMetadata;
        private set => SetProperty(ref _jpegMetadata, value);
    }

    public string CompanionMediaSummary
    {
        get => _companionMediaSummary;
        private set => SetProperty(ref _companionMediaSummary, value);
    }

    public string CompanionVideoHdrSummary
    {
        get => _companionVideoHdrSummary;
        private set => SetProperty(ref _companionVideoHdrSummary, value);
    }

    public string CompanionVideoStatus
    {
        get => _companionVideoStatus;
        private set => SetProperty(ref _companionVideoStatus, value);
    }

    public string ExifSummary
    {
        get => _exifSummary;
        private set => SetProperty(ref _exifSummary, value);
    }

    public string RenderStatus
    {
        get => _renderStatus;
        private set => SetProperty(ref _renderStatus, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool HasImage
    {
        get => _hasImage;
        private set
        {
            if (SetProperty(ref _hasImage, value))
            {
                OnPropertyChanged(nameof(PlaceholderOpacity));
            }
        }
    }

    public double PlaceholderOpacity => HasImage ? 0.0 : 1.0;

    public bool HasStatus
    {
        get => _hasStatus;
        private set => SetProperty(ref _hasStatus, value);
    }

    public bool HasCompanionMedia
    {
        get => _hasCompanionMedia;
        private set => SetProperty(ref _hasCompanionMedia, value);
    }

    public string CompanionMediaLabel
    {
        get => _companionMediaLabel;
        private set => SetProperty(ref _companionMediaLabel, value);
    }

    public bool IsCompanionMediaMuted
    {
        get => _isCompanionMediaMuted;
        set => SetProperty(ref _isCompanionMediaMuted, value);
    }

    public async Task<HdrImageDocument> LoadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ExifSummary = "正在读取 EXIF...";
        var loadResult = await ImagePreloadCache.GetLoadResultAsync(path, cancellationToken);
        var document = loadResult.Document;
        var descriptor = document.Format;
        var gainMapProbe = document.GainMapProbe;
        var heifAvifProbe = document.HeifAvifProbe;
        var jxlProbe = document.JxlProbe;
        var wicImageProbe = document.WicImageProbe;
        var exrProbe = document.ExrProbe;
        var companionMedia = document.CompanionMedia;

        FileName = document.FileName;
        FilePath = document.Path;
        FormatName = descriptor.DisplayName;
        HdrKind = descriptor.Kind.ToString();
        Decoder = descriptor.Decoder;
        TransferFunction = descriptor.TransferFunction;
        ColorContainer = descriptor.ColorContainer;
        SupportStatus = descriptor.SupportStatus;
        HasImage = descriptor.Kind is not HdrImageKind.Unknown;
        ApplyContainerProbe(gainMapProbe, heifAvifProbe, jxlProbe, wicImageProbe, exrProbe);
        HasCompanionMedia = companionMedia is not null;
        CompanionMediaLabel = companionMedia?.DisplayLabel ?? "动态";
        CompanionMediaSummary = companionMedia?.DisplaySummary ?? "无";
        CompanionVideoHdrSummary = companionMedia?.VideoProbe?.DisplaySummary
            ?? (companionMedia is null ? "无" : "未在 companion video 中定位到 HDR/色彩 metadata");
        CompanionVideoStatus = companionMedia is null
            ? "无"
            : $"WinUI MediaPlayerElement; ready; muted on; playback none; overlay hidden; {companionMedia.Kind}";
        IsCompanionMediaMuted = true;
        Status = CreateStatus(descriptor, gainMapProbe, heifAvifProbe, jxlProbe, wicImageProbe, exrProbe);
        ExifSummary = loadResult.ExifSummary;
        HasStatus = true;

        return document;
    }

    public void UpdateRenderStatus(string status)
    {
        RenderStatus = status;
    }

    public void UpdateCompanionVideoStatus(string status)
    {
        CompanionVideoStatus = status;
    }

    private void ApplyContainerProbe(GainMapProbeResult? probe, HeifAvifProbeResult? heifProbe, JxlProbeResult? jxlProbe, WicImageProbeResult? wicProbe, ExrProbeResult? exrProbe)
    {
        if (probe is null)
        {
            ApplyHeifAvifJxlWicOrExrProbe(heifProbe, jxlProbe, wicProbe, exrProbe);
            return;
        }

        GainMapStatus = probe.DisplayStatus;
        GainMapLocation = probe.GainMapOffset is { } offset
            ? $"offset {offset}, length {(probe.GainMapLength?.ToString() ?? "unknown")}"
            : "无";
        GainMapMetadata = probe.Metadata?.DisplaySummary ?? "无";
        JpegMetadata = $"EXIF orientation {(probe.ExifOrientation?.ToString() ?? "none")}; ICC {(probe.HasPrimaryIccProfile ? "embedded" : "none")}; ISO 21496-1 {(probe.HasIso21496Signal ? "detected" : "not detected")}; Apple HDRGainMap {(probe.HasAppleHdrGainMapSignal ? "detected" : "not detected")}";
    }

    private void ApplyHeifAvifJxlWicOrExrProbe(HeifAvifProbeResult? probe, JxlProbeResult? jxlProbe, WicImageProbeResult? wicProbe, ExrProbeResult? exrProbe)
    {
        if (jxlProbe is not null)
        {
            GainMapStatus = jxlProbe.DisplayStatus;
            GainMapLocation = "无";
            GainMapMetadata = "无";
            JpegMetadata = jxlProbe.Summary;
            return;
        }

        if (exrProbe is not null)
        {
            GainMapStatus = exrProbe.DisplayStatus;
            GainMapLocation = "无";
            GainMapMetadata = "无";
            JpegMetadata = exrProbe.ColorSummary;
            return;
        }

        if (wicProbe is not null)
        {
            GainMapStatus = wicProbe.DisplayStatus;
            GainMapLocation = "无";
            GainMapMetadata = "无";
            JpegMetadata = wicProbe.ColorSummary;
            return;
        }

        if (probe is null)
        {
            GainMapStatus = "不是 JPEG/HEIF gain-map 候选文件。";
            GainMapLocation = "无";
            GainMapMetadata = "无";
            JpegMetadata = "无";
            return;
        }

        GainMapStatus = probe.DisplayStatus;
        GainMapLocation = probe.PrimaryItemId is { } primaryItemId
            ? $"primary item {primaryItemId} ({probe.PrimaryItemType ?? "unknown"}); gain-map signal {(probe.HasGainMapSignal ? "detected" : "not detected")}; auxiliary {(probe.HasGainMapAuxiliary ? "detected" : "not detected")}"
            : $"gain-map signal {(probe.HasGainMapSignal ? "detected" : "not detected")}; auxiliary {(probe.HasGainMapAuxiliary ? "detected" : "not detected")}";
        GainMapMetadata = probe.HasGainMapAuxiliary
            ? "HEIF-family gain-map auxiliary detected; libheif decoding and D3D11 rendering fully active."
            : probe.HasGainMapSignal
                ? "HEIF/AVIF gain-map metadata detected; no renderable auxiliary image was exposed by the current decoder path."
            : "无";
        JpegMetadata = probe.DisplaySummary;
    }

    private static string CreateStatus(
        ImageFormatDescriptor descriptor,
        GainMapProbeResult? probe,
        HeifAvifProbeResult? heifProbe,
        JxlProbeResult? jxlProbe,
        WicImageProbeResult? wicProbe,
        ExrProbeResult? exrProbe)
    {
        if (probe?.IsRenderableUltraHdr == true)
        {
            return "检测到 Ultra HDR gain map；使用色域感知的 SDR 底图解码、EXIF 方向和 D3D11 像素着色器重建。";
        }

        if (heifProbe?.IsHeifFamily == true && heifProbe.HasGainMapAuxiliary)
        {
            return "检测到 HEIF 增益图辅助图像；使用色域感知的 SDR 底图、libheif 解码和 D3D11 像素着色器重建。";
        }

        if (heifProbe?.IsHeifFamily == true && heifProbe.HasGainMapSignal)
        {
            return heifProbe.DisplayStatus;
        }

        if (probe?.HasIso21496Signal == true)
        {
            return probe.DisplayStatus;
        }

        if (probe?.HasUltraHdrSignal == true || probe?.HasAppleHdrGainMapSignal == true)
        {
            return probe.DisplayStatus;
        }

        if (heifProbe is not null)
        {
            return heifProbe.DisplayStatus;
        }

        if (jxlProbe is not null)
        {
            return jxlProbe.DisplayStatus;
        }

        if (exrProbe is not null)
        {
            return exrProbe.DisplayStatus;
        }

        if (wicProbe is not null)
        {
            return wicProbe.DisplayStatus;
        }

        return descriptor.Kind is not HdrImageKind.Unknown
            ? "文件已识别，渲染管线已准备好使用对应解码器。"
            : "这个文件类型还不在 HDR 解码目录中。";
    }
}
