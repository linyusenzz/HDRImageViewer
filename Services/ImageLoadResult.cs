using HdrImageViewer.Models;

namespace HdrImageViewer.Services;

public sealed record ImageLoadResult(
    HdrImageDocument Document,
    string ExifSummary,
    DateTime LastWriteTimeUtc);
