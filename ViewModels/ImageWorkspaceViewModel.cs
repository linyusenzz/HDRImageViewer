using HdrImageViewer.Infrastructure;
using HdrImageViewer.Models;
using HdrImageViewer.Services;

namespace HdrImageViewer.ViewModels;

public sealed record ImageWorkspaceLoadResult(HdrImageDocument Document, string ExifSummary);

public sealed class ImageWorkspaceViewModel : ObservableObject
{
    private string _fileName = Localization.GetString("WorkspaceNoImage");
    private string _filePath = string.Empty;
    private string _formatName = Localization.GetString("WorkspaceNone");
    private string _hdrKind = Localization.GetString("WorkspaceNone");
    private string _decoder = Localization.GetString("WorkspaceWaiting");
    private string _transferFunction = Localization.GetString("WorkspaceUnknown");
    private string _colorContainer = Localization.GetString("WorkspaceUnknown");
    private string _supportStatus = Localization.GetString("WorkspacePleaseSelectImage");
    private string _gainMapStatus = Localization.GetString("WorkspaceGainMapNotProbed");
    private string _gainMapLocation = Localization.GetString("WorkspaceNone");
    private string _gainMapMetadata = Localization.GetString("WorkspaceNone");
    private string _jpegMetadata = Localization.GetString("WorkspaceNone");
    private string _companionMediaSummary = Localization.GetString("WorkspaceNone");
    private string _companionVideoHdrSummary = Localization.GetString("WorkspaceNone");
    private string _companionVideoStatus = Localization.GetString("WorkspaceNone");
    private string _exifSummary = Localization.GetString("ExifNoMetadata");
    private string _renderStatus = Localization.GetString("WorkspaceRendererWaiting");
    private string _status = Localization.GetString("WorkspaceReady");
    private bool _hasImage;
    private bool _hasStatus = true;
    private bool _hasCompanionMedia;
    private bool _isCompanionMediaMuted = true;
    private string _companionMediaLabel = Localization.GetString("WorkspaceLivePhotoLabel");

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

    public void BeginFileLoad()
    {
        ExifSummary = Localization.GetString("ExifReading");
    }

    public static async Task<ImageWorkspaceLoadResult> LoadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var loadResult = await ImagePreloadCache.GetLoadResultAsync(path, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new ImageWorkspaceLoadResult(loadResult.Document, loadResult.ExifSummary);
    }

    public void ApplyLoadResult(ImageWorkspaceLoadResult loadResult)
    {
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
        CompanionMediaLabel = companionMedia?.DisplayLabel ?? Localization.GetString("WorkspaceLivePhotoLabel");
        CompanionMediaSummary = companionMedia?.DisplaySummary ?? Localization.GetString("WorkspaceNone");
        CompanionVideoHdrSummary = companionMedia?.VideoProbe?.DisplaySummary
            ?? (companionMedia is null ? Localization.GetString("WorkspaceNone") : Localization.GetString("WorkspaceCompanionVideoNoMetadata"));
        CompanionVideoStatus = companionMedia is null
            ? Localization.GetString("WorkspaceNone")
            : $"WinUI MediaPlayerElement; ready; muted on; playback none; overlay hidden; {companionMedia.Kind}";
        IsCompanionMediaMuted = true;
        Status = CreateStatus(descriptor, gainMapProbe, heifAvifProbe, jxlProbe, wicImageProbe, exrProbe);
        ExifSummary = loadResult.ExifSummary;
        HasStatus = true;
    }

    public void UpdateRenderStatus(string status)
    {
        RenderStatus = status;
    }

    public void ClearImage(string renderStatus)
    {
        FileName = Localization.GetString("WorkspaceNoImage");
        FilePath = string.Empty;
        FormatName = Localization.GetString("WorkspaceNone");
        HdrKind = Localization.GetString("WorkspaceNone");
        Decoder = Localization.GetString("WorkspaceWaiting");
        TransferFunction = Localization.GetString("WorkspaceUnknown");
        ColorContainer = Localization.GetString("WorkspaceUnknown");
        SupportStatus = Localization.GetString("WorkspacePleaseSelectImage");
        GainMapStatus = Localization.GetString("WorkspaceGainMapNotProbed");
        GainMapLocation = Localization.GetString("WorkspaceNone");
        GainMapMetadata = Localization.GetString("WorkspaceNone");
        JpegMetadata = Localization.GetString("WorkspaceNone");
        CompanionMediaSummary = Localization.GetString("WorkspaceNone");
        CompanionVideoHdrSummary = Localization.GetString("WorkspaceNone");
        CompanionVideoStatus = Localization.GetString("WorkspaceNone");
        ExifSummary = Localization.GetString("ExifNoMetadata");
        HasImage = false;
        HasCompanionMedia = false;
        CompanionMediaLabel = Localization.GetString("WorkspaceLivePhotoLabel");
        IsCompanionMediaMuted = true;
        Status = Localization.GetString("WorkspaceReady");
        RenderStatus = renderStatus;
        HasStatus = true;
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
            : Localization.GetString("WorkspaceNone");
        GainMapMetadata = probe.Metadata?.DisplaySummary ?? Localization.GetString("WorkspaceNone");
        JpegMetadata = $"EXIF orientation {(probe.ExifOrientation?.ToString() ?? "none")}; ICC {(probe.HasPrimaryIccProfile ? "embedded" : "none")}; ISO 21496-1 {(probe.HasIso21496Signal ? "detected" : "not detected")}; Apple HDRGainMap {(probe.HasAppleHdrGainMapSignal ? "detected" : "not detected")}";
    }

    private void ApplyHeifAvifJxlWicOrExrProbe(HeifAvifProbeResult? probe, JxlProbeResult? jxlProbe, WicImageProbeResult? wicProbe, ExrProbeResult? exrProbe)
    {
        if (jxlProbe is not null)
        {
            GainMapStatus = jxlProbe.DisplayStatus;
            GainMapLocation = Localization.GetString("WorkspaceNone");
            GainMapMetadata = Localization.GetString("WorkspaceNone");
            JpegMetadata = jxlProbe.Summary;
            return;
        }

        if (exrProbe is not null)
        {
            GainMapStatus = exrProbe.DisplayStatus;
            GainMapLocation = Localization.GetString("WorkspaceNone");
            GainMapMetadata = Localization.GetString("WorkspaceNone");
            JpegMetadata = exrProbe.ColorSummary;
            return;
        }

        if (wicProbe is not null)
        {
            GainMapStatus = wicProbe.DisplayStatus;
            GainMapLocation = Localization.GetString("WorkspaceNone");
            GainMapMetadata = Localization.GetString("WorkspaceNone");
            JpegMetadata = wicProbe.ColorSummary;
            return;
        }

        if (probe is null)
        {
            GainMapStatus = Localization.GetString("WorkspaceNotGainMapCandidate");
            GainMapLocation = Localization.GetString("WorkspaceNone");
            GainMapMetadata = Localization.GetString("WorkspaceNone");
            JpegMetadata = Localization.GetString("WorkspaceNone");
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
            : Localization.GetString("WorkspaceNone");
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
            return Localization.GetString("WorkspaceUltraHdrDetected");
        }

        if (heifProbe?.IsHeifFamily == true && heifProbe.HasGainMapAuxiliary)
        {
            return Localization.GetString("WorkspaceHeifGainMapDetected");
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
            ? Localization.GetString("WorkspaceFileIdentifiedReady")
            : Localization.GetString("WorkspaceFileTypeNotInCatalog");
    }
}
