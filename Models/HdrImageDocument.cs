namespace HdrImageViewer.Models;

public sealed record HdrImageDocument(
    string Path,
    string FileName,
    ImageFormatDescriptor Format,
    GainMapProbeResult? GainMapProbe = null,
    HeifAvifProbeResult? HeifAvifProbe = null);
