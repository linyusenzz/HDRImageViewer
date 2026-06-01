namespace HdrImageViewer.Models;

public sealed record HdrImageDocument(
    string Path,
    string FileName,
    ImageFormatDescriptor Format,
    GainMapProbeResult? GainMapProbe = null,
    HeifAvifProbeResult? HeifAvifProbe = null,
    JxlProbeResult? JxlProbe = null,
    WicImageProbeResult? WicImageProbe = null,
    ExrProbeResult? ExrProbe = null)
{
    public bool HasRenderableGainMap =>
        GainMapProbe?.IsRenderableUltraHdr == true
        || (HeifAvifProbe?.IsHeifFamily == true && HeifAvifProbe.HasGainMapAuxiliary);
}
