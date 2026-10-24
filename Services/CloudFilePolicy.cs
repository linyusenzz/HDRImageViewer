namespace HdrImageViewer.Services;

/// <summary>
/// Detects files whose content is not present locally because a cloud sync
/// provider (OneDrive, iCloud Drive, or any Windows cloud-files placeholder)
/// keeps them online-only. Reading such a file triggers an automatic download,
/// so background work must query only its attributes, which never recall data.
/// </summary>
public static class CloudFilePolicy
{
    private const int FileAttributeOffline = 0x0000_1000;
    private const int FileAttributeRecallOnOpen = 0x0004_0000;
    private const int FileAttributeRecallOnDataAccess = 0x0040_0000;
    private const int RecallOnAccessAttributes =
        FileAttributeOffline | FileAttributeRecallOnOpen | FileAttributeRecallOnDataAccess;

    public static bool IsCloudPlaceholder(FileAttributes attributes)
    {
        return ((int)attributes & RecallOnAccessAttributes) != 0;
    }

    public static bool IsCloudPlaceholder(string path)
    {
        try
        {
            return IsCloudPlaceholder(File.GetAttributes(path));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether background work (preload, thumbnails, sidecar probes) must not
    /// read this file now because that would download it from the cloud.
    /// Explicit user actions such as opening an image still read on demand.
    /// </summary>
    public static bool ShouldSkipBackgroundRead(string path)
    {
        return AppSettingsService.Current.SkipCloudPlaceholders && IsCloudPlaceholder(path);
    }
}
