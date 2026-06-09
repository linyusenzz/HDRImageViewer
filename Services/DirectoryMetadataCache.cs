using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using HdrImageViewer.Models;

namespace HdrImageViewer.Services;

public static class DirectoryMetadataCache
{
    private const int CurrentVersion = 18;
    private const string CacheFileName = ".hdrimageviewer.meta.json";
    private static readonly TimeSpan FlushDelay = TimeSpan.FromSeconds(1.5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly ConcurrentDictionary<string, DirectoryState> s_directories = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Timer s_flushTimer = new(_ => _ = FlushDirtyDirectoriesAsync(), null, Timeout.Infinite, Timeout.Infinite);
    private static readonly object s_flushTimerGate = new();

    public static async Task<ImageLoadResult?> TryLoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var state = await EnsureDirectoryLoadedAsync(directory, cancellationToken);
        if (state.File is null)
        {
            return null;
        }

        var fileName = Path.GetFileName(path);
        DirectoryMetadataEntry? entry;
        await state.Lock.WaitAsync(cancellationToken);
        try
        {
            if (!state.File.Entries.TryGetValue(fileName, out entry) || !entry.Matches(path))
            {
                return null;
            }
        }
        finally
        {
            state.Lock.Release();
        }

        var descriptor = DecoderCatalog.Describe(path, entry.GainMapProbe, entry.HeifAvifProbe, entry.JxlProbe, entry.WicImageProbe, entry.ExrProbe, entry.ContainerKind);
        var companionMedia = await LivePhotoProbe.ProbeAsync(path, entry.ContainerKind, cancellationToken);
        var document = new HdrImageDocument(path, fileName, descriptor, entry.GainMapProbe, entry.HeifAvifProbe, entry.JxlProbe, entry.WicImageProbe, entry.ExrProbe, companionMedia);
        return new ImageLoadResult(document, entry.ExifSummary ?? "没有 EXIF 元数据", entry.LastWriteTimeUtc);
    }

    public static async Task StoreAsync(ImageLoadResult result, FileContainerKind containerKind, CancellationToken cancellationToken = default)
    {
        var path = result.Document.Path;
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var state = await EnsureDirectoryLoadedAsync(directory, cancellationToken);
        await state.Lock.WaitAsync(cancellationToken);
        try
        {
            state.File ??= new DirectoryMetadataCacheFile();
            state.File.Version = CurrentVersion;
            state.File.Entries[Path.GetFileName(path)] = DirectoryMetadataEntry.Create(result, containerKind);
            state.IsDirty = true;
        }
        finally
        {
            state.Lock.Release();
        }

        ScheduleFlush();
    }

    public static Task FlushAsync(CancellationToken cancellationToken = default)
    {
        return FlushDirtyDirectoriesAsync(cancellationToken);
    }

    private static async Task<DirectoryState> EnsureDirectoryLoadedAsync(string directory, CancellationToken cancellationToken)
    {
        var state = s_directories.GetOrAdd(directory, dir => new DirectoryState(dir));
        if (state.IsLoaded)
        {
            return state;
        }

        await state.Lock.WaitAsync(cancellationToken);
        try
        {
            if (state.IsLoaded)
            {
                return state;
            }

            var cachePath = Path.Combine(directory, CacheFileName);
            if (File.Exists(cachePath))
            {
                try
                {
                    await using var stream = File.OpenRead(cachePath);
                    var loaded = await JsonSerializer.DeserializeAsync<DirectoryMetadataCacheFile>(stream, JsonOptions, cancellationToken);
                    state.File = loaded?.Version == CurrentVersion ? loaded : null;
                }
                catch
                {
                    state.File = null;
                }
            }

            state.IsLoaded = true;
        }
        finally
        {
            state.Lock.Release();
        }

        return state;
    }

    private static void ScheduleFlush()
    {
        lock (s_flushTimerGate)
        {
            s_flushTimer.Change(FlushDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private static async Task FlushDirtyDirectoriesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var state in s_directories.Values)
        {
            if (!state.IsDirty)
            {
                continue;
            }

            await state.Lock.WaitAsync(cancellationToken);
            DirectoryMetadataCacheFile? snapshot = null;
            try
            {
                if (state.IsDirty && state.File is not null)
                {
                    snapshot = state.File;
                    state.IsDirty = false;
                }
            }
            finally
            {
                state.Lock.Release();
            }

            if (snapshot is null)
            {
                continue;
            }

            var cachePath = Path.Combine(state.Directory, CacheFileName);
            try
            {
                await using var writeStream = File.Create(cachePath);
                await JsonSerializer.SerializeAsync(writeStream, snapshot, JsonOptions, cancellationToken);
                try
                {
                    File.SetAttributes(cachePath, File.GetAttributes(cachePath) | FileAttributes.Hidden);
                }
                catch
                {
                }
            }
            catch
            {
                state.IsDirty = true;
            }
        }
    }

    private sealed class DirectoryState
    {
        public DirectoryState(string directory)
        {
            Directory = directory;
        }

        public string Directory { get; }

        public SemaphoreSlim Lock { get; } = new(1, 1);

        public DirectoryMetadataCacheFile? File { get; set; }

        public bool IsLoaded { get; set; }

        public bool IsDirty { get; set; }
    }

    private sealed class DirectoryMetadataCacheFile
    {
        public int Version { get; set; } = CurrentVersion;

        public Dictionary<string, DirectoryMetadataEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class DirectoryMetadataEntry
    {
        public long Length { get; set; }

        public DateTime LastWriteTimeUtc { get; set; }

        public FileContainerKind ContainerKind { get; set; }

        public string? ExifSummary { get; set; }

        public GainMapProbeResult? GainMapProbe { get; set; }

        public HeifAvifProbeResult? HeifAvifProbe { get; set; }

        public JxlProbeResult? JxlProbe { get; set; }

        public WicImageProbeResult? WicImageProbe { get; set; }

        public ExrProbeResult? ExrProbe { get; set; }

        public CompanionMedia? CompanionMedia { get; set; }

        public static DirectoryMetadataEntry Create(ImageLoadResult result, FileContainerKind containerKind)
        {
            var info = new FileInfo(result.Document.Path);
            return new DirectoryMetadataEntry
            {
                Length = info.Length,
                LastWriteTimeUtc = result.LastWriteTimeUtc,
                ContainerKind = containerKind,
                ExifSummary = result.ExifSummary,
                GainMapProbe = result.Document.GainMapProbe,
                HeifAvifProbe = result.Document.HeifAvifProbe,
                JxlProbe = result.Document.JxlProbe,
                WicImageProbe = result.Document.WicImageProbe,
                ExrProbe = result.Document.ExrProbe?.PixelWidth is > 0 && result.Document.ExrProbe.PixelHeight is > 0
                    ? result.Document.ExrProbe
                    : null,
                CompanionMedia = result.Document.CompanionMedia,
            };
        }

        public bool Matches(string path)
        {
            var info = new FileInfo(path);
            return info.Exists
                && info.Length == Length
                && info.LastWriteTimeUtc == LastWriteTimeUtc;
        }
    }
}
