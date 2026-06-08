namespace HdrImageViewer.Models;

public enum CompanionMediaKind
{
    AppleLivePhoto,
    AndroidMotionPhoto,
    SidecarVideo,
}

public sealed record CompanionMedia(
    CompanionMediaKind Kind,
    string Path,
    string DisplayLabel,
    string SourceDescription,
    long? EmbeddedOffset = null,
    long? EmbeddedLength = null,
    CompanionVideoProbeResult? VideoProbe = null)
{
    public bool IsEmbedded => EmbeddedOffset is not null && EmbeddedLength is not null;

    public string DisplaySummary
    {
        get
        {
            if (IsEmbedded)
            {
                return $"{SourceDescription}; embedded video offset {EmbeddedOffset}, length {EmbeddedLength}";
            }

            return $"{SourceDescription}; {System.IO.Path.GetFileName(Path)}";
        }
    }
}
