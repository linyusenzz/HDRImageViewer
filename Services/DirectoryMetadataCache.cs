using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HdrImageViewer.Models;

namespace HdrImageViewer.Services;

public static class DirectoryMetadataCache
{
    private const int CurrentVersion = 19;
    private const int MaxResidentDirectoryFiles = 8;
    private const string LegacyCacheFileName = ".hdrimageviewer.meta.json";
    private static readonly TimeSpan FlushDelay = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MaximumDirtyAge = TimeSpan.FromSeconds(30);

    // Cache files live under LocalAppData instead of the browsed folder: the
    // in-folder file polluted user photo directories and silently never
    // persisted on read-only or network shares, so those folders re-paid the
    // full probe cost on every app run.
    private static readonly string s_cacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HdrImageViewer",
        "metadata-cache");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly ConcurrentDictionary<string, DirectoryState> s_directories = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Timer s_flushTimer = new(_ => _ = FlushDirtyDirectoriesAsync(), null, Timeout.Infinite, Timeout.Infinite);
    private static readonly object s_flushTimerGate = new();
    private static readonly SemaphoreSlim s_flushGate = new(1, 1);

    public static async Task<ImageLoadResult?> TryLoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var state = await LockDirectoryStateAsync(directory, cancellationToken);
        var fileName = Path.GetFileName(path);
        DirectoryMetadataEntry? entry;
        try
        {
            if (state.File is null
                || !state.File.Entries.TryGetValue(fileName, out entry)
                || !entry.Matches(path))
            {
                return null;
            }
        }
        finally
        {
            state.Lock.Release();
            TrimResidentDirectoryFiles(directory);
        }

        var descriptor = DecoderCatalog.Describe(path, entry.GainMapProbe, entry.HeifAvifProbe, entry.JxlProbe, entry.WicImageProbe, entry.ExrProbe, entry.ContainerKind);
        var companionMedia = await ResolveCompanionMediaAsync(path, entry, cancellationToken);
        var document = new HdrImageDocument(path, fileName, descriptor, entry.GainMapProbe, entry.HeifAvifProbe, entry.JxlProbe, entry.WicImageProbe, entry.ExrProbe, companionMedia);
        return new ImageLoadResult(document, entry.ExifSummary ?? Localization.GetString("ExifNoMetadata"), entry.LastWriteTimeUtc);
    }

    private static async Task<CompanionMedia?> ResolveCompanionMediaAsync(
        string path,
        DirectoryMetadataEntry entry,
        CancellationToken cancellationToken)
    {
        // Embedded motion data is fully determined by the image file, which
        // entry.Matches() has already validated by length/mtime, so the cached
        // value is trusted without re-scanning the JPEG XMP packets.
        if (entry.CompanionMedia is { IsEmbedded: true } embedded)
        {
            return embedded;
        }

        // A cached sidecar reference stays valid while the sidecar file is
        // still on disk. Sidecars appear and disappear independently of the
        // image, so a missing file (or no cached companion) falls back to the
        // cheap sidecar-only probe; the embedded scan stays skipped because
        // the unchanged image cannot have gained embedded motion data.
        if (entry.CompanionMedia is { } sidecar && File.Exists(sidecar.Path))
        {
            return sidecar;
        }

        return await LivePhotoProbe.ProbeSidecarOnlyAsync(path, entry.ContainerKind, cancellationToken);
    }

    public static async Task StoreAsync(ImageLoadResult result, FileContainerKind containerKind, CancellationToken cancellationToken = default)
    {
        var path = result.Document.Path;
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var state = await LockDirectoryStateAsync(directory, cancellationToken);
        try
        {
            state.File ??= new DirectoryMetadataCacheFile();
            state.File.Version = CurrentVersion;
            state.File.Entries[Path.GetFileName(path)] = DirectoryMetadataEntry.Create(result, containerKind);
            state.MarkDirty();
        }
        finally
        {
            state.Lock.Release();
            TrimResidentDirectoryFiles(directory);
        }

        ScheduleFlush();
    }

    public static Task FlushAsync(CancellationToken cancellationToken = default)
    {
        return FlushDirtyDirectoriesAsync(cancellationToken);
    }

    private static async Task<DirectoryState> LockDirectoryStateAsync(string directory, CancellationToken cancellationToken)
    {
        var state = s_directories.GetOrAdd(directory, dir => new DirectoryState(dir));
        state.Touch();
        await state.Lock.WaitAsync(cancellationToken);
        try
        {
            if (!state.IsLoaded)
            {
                // Prefer the LocalAppData cache; fall back to the legacy in-folder
                // file so directories cached by older versions keep their entries
                // (they migrate to the new location on the next flush).
                state.File = await TryReadCacheFileAsync(GetCacheFilePath(directory), cancellationToken)
                    ?? await TryReadCacheFileAsync(Path.Combine(directory, LegacyCacheFileName), cancellationToken);
                state.IsLoaded = true;
            }

            return state;
        }
        catch
        {
            state.Lock.Release();
            throw;
        }
    }

    private static void ScheduleFlush()
    {
        var now = Environment.TickCount64;
        var dueTime = FlushDelay;
        foreach (var state in s_directories.Values)
        {
            if (!state.IsDirty || state.FirstDirtyTicks <= 0)
            {
                continue;
            }

            var remainingMilliseconds = Math.Max(
                0.0,
                MaximumDirtyAge.TotalMilliseconds - (now - state.FirstDirtyTicks));
            if (remainingMilliseconds < dueTime.TotalMilliseconds)
            {
                dueTime = TimeSpan.FromMilliseconds(remainingMilliseconds);
            }
        }

        lock (s_flushTimerGate)
        {
            s_flushTimer.Change(dueTime, Timeout.InfiniteTimeSpan);
        }
    }

    private static async Task FlushDirtyDirectoriesAsync(
        CancellationToken cancellationToken = default)
    {
        await s_flushGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var state in s_directories.Values)
            {
                if (!state.IsDirty)
                {
                    continue;
                }

                await state.Lock.WaitAsync(cancellationToken);
                DirectoryMetadataCacheFile? snapshot = null;
                var snapshotVersion = 0L;
                try
                {
                    if (state.IsDirty && state.File is not null)
                    {
                        snapshot = state.File.Clone();
                        snapshotVersion = state.ChangeVersion;
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

                var cachePath = GetCacheFilePath(state.Directory);
                string? temporaryPath = null;
                try
                {
                    Directory.CreateDirectory(s_cacheRoot);
                    temporaryPath = cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
                    await using var writeStream = new FileStream(
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        64 * 1024,
                        FileOptions.Asynchronous);
                    await JsonSerializer.SerializeAsync(
                        writeStream,
                        snapshot,
                        JsonOptions,
                        cancellationToken);
                    await writeStream.FlushAsync(cancellationToken);
                    writeStream.Close();
                    File.Move(temporaryPath, cachePath, overwrite: true);
                    temporaryPath = null;

                    await state.Lock.WaitAsync(CancellationToken.None);
                    try
                    {
                        state.MarkFlushed(snapshotVersion);
                    }
                    finally
                    {
                        state.Lock.Release();
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    await state.Lock.WaitAsync(CancellationToken.None);
                    try
                    {
                        state.MarkFlushFailed();
                    }
                    finally
                    {
                        state.Lock.Release();
                    }
                }
                finally
                {
                    if (temporaryPath is not null)
                    {
                        TryDeleteFile(temporaryPath);
                    }
                }
            }

            TrimResidentDirectoryFiles(activeDirectory: null);
        }
        finally
        {
            s_flushGate.Release();
            if (s_directories.Values.Any(state => state.IsDirty))
            {
                ScheduleFlush();
            }
        }
    }

    private static void TrimResidentDirectoryFiles(string? activeDirectory)
    {
        var candidates = s_directories.Values
            .Where(state => state.IsLoaded && state.File is not null)
            .OrderByDescending(state => string.Equals(state.Directory, activeDirectory, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(state => state.LastAccessTicks)
            .ToList();
        var residentCount = candidates.Count;
        foreach (var state in candidates.AsEnumerable().Reverse())
        {
            if (residentCount <= MaxResidentDirectoryFiles)
            {
                break;
            }

            if (string.Equals(state.Directory, activeDirectory, StringComparison.OrdinalIgnoreCase)
                || !state.Lock.Wait(0))
            {
                continue;
            }

            try
            {
                if (state.IsDirty || !state.IsLoaded || state.File is null)
                {
                    continue;
                }

                state.File = null;
                state.IsLoaded = false;
                residentCount--;
            }
            finally
            {
                state.Lock.Release();
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private static string GetCacheFilePath(string directory)
    {
        // Path comparison on Windows is case-insensitive, so the hash key is
        // normalised the same way the in-memory dictionaries compare paths.
        var normalized = Path.TrimEndingDirectorySeparator(directory).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(s_cacheRoot, hash + ".json");
    }

    private static async Task<DirectoryMetadataCacheFile?> TryReadCacheFileAsync(
        string cachePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(cachePath);
            var loaded = await JsonSerializer.DeserializeAsync<DirectoryMetadataCacheFile>(stream, JsonOptions, cancellationToken);
            return loaded?.Version == CurrentVersion ? loaded : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class DirectoryState
    {
        private long _lastAccessTicks = Environment.TickCount64;
        private long _firstDirtyTicks;
        private long _changeVersion;
        private int _isDirty;

        public DirectoryState(string directory)
        {
            Directory = directory;
        }

        public string Directory { get; }

        public SemaphoreSlim Lock { get; } = new(1, 1);

        public DirectoryMetadataCacheFile? File { get; set; }

        public bool IsLoaded { get; set; }

        public bool IsDirty => Volatile.Read(ref _isDirty) != 0;

        public long FirstDirtyTicks => Interlocked.Read(ref _firstDirtyTicks);

        public long ChangeVersion => Interlocked.Read(ref _changeVersion);

        public long LastAccessTicks => Interlocked.Read(ref _lastAccessTicks);

        public void Touch()
        {
            Interlocked.Exchange(ref _lastAccessTicks, Environment.TickCount64);
        }

        public void MarkDirty()
        {
            if (Interlocked.Exchange(ref _isDirty, 1) == 0)
            {
                Interlocked.Exchange(ref _firstDirtyTicks, Environment.TickCount64);
            }

            Interlocked.Increment(ref _changeVersion);
        }

        public void MarkFlushed(long snapshotVersion)
        {
            if (ChangeVersion != snapshotVersion)
            {
                return;
            }

            Volatile.Write(ref _isDirty, 0);
            Interlocked.Exchange(ref _firstDirtyTicks, 0);
        }

        public void MarkFlushFailed()
        {
            Volatile.Write(ref _isDirty, 1);
            Interlocked.Exchange(ref _firstDirtyTicks, Environment.TickCount64);
        }
    }

    private sealed class DirectoryMetadataCacheFile
    {
        public int Version { get; set; } = CurrentVersion;

        public Dictionary<string, DirectoryMetadataEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public DirectoryMetadataCacheFile Clone()
        {
            return new DirectoryMetadataCacheFile
            {
                Version = Version,
                Entries = new Dictionary<string, DirectoryMetadataEntry>(Entries, StringComparer.OrdinalIgnoreCase),
            };
        }
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
