namespace HdrImageViewer.Models;

public sealed record HdrImageDocument(
    string Path,
    string FileName,
    ImageFormatDescriptor Format,
    GainMapProbeResult? GainMapProbe = null,
    HeifAvifProbeResult? HeifAvifProbe = null,
    JxlProbeResult? JxlProbe = null,
    WicImageProbeResult? WicImageProbe = null,
    ExrProbeResult? ExrProbe = null,
    CompanionMedia? CompanionMedia = null)
{
    public bool HasCompanionMedia => CompanionMedia is not null;

    public bool HasGainMapSignal =>
        GainMapProbe?.HasUltraHdrSignal == true
        || GainMapProbe?.HasIso21496Signal == true
        || GainMapProbe?.HasAppleHdrGainMapSignal == true
        || HeifAvifProbe?.HasGainMapSignal == true
        || JxlProbe?.HasGainMapBox == true;

    public bool HasRenderableGainMap =>
        GainMapProbe?.IsRenderableUltraHdr == true
        || (HeifAvifProbe?.IsHeifFamily == true && (HeifAvifProbe.HasGainMapAuxiliary || HeifAvifProbe.HasIsoGainMapSignal))
        || JxlProbe?.HasGainMapBox == true;
}
