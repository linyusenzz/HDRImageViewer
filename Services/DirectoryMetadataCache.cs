using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HdrImageViewer.Models;

namespace HdrImageViewer.Services;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DirectoryMetadataCacheFile))]
internal sealed partial class DirectoryMetadataJsonContext : JsonSerializerContext
{
}

public static class DirectoryMetadataCache
{
    internal const int CurrentVersion = 24;
    internal const string CacheRootEnvironmentVariable = "HDRIV_METADATA_CACHE_DIR";
    private const int MaxResidentDirectoryFiles = 8;
    private const int MaxDirectoryStates = 32;
    private const int MaxUnprunedEntryCount = 256;
    private const int MaxPruneChecksPerFlush = 256;
    private const int MaxDiskCacheFiles = 200;
    private const int DiskCleanupFlushInterval = 64;
    private const string LegacyCacheFileName = ".hdrimageviewer.meta.json";
    private static readonly TimeSpan FlushDelay = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MaximumDirtyAge = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StaleEntryVerificationAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan LargeDirectoryVerificationAge = TimeSpan.FromHours(1);

    // Cache files live under LocalAppData instead of the browsed folder: the
    // in-folder file polluted user photo directories and silently never
    // persisted on read-only or network shares, so those folders re-paid the
    // full probe cost on every app run.
    private static readonly string s_defaultCacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HdrImageViewer",
        "metadata-cache");

    private static readonly ConcurrentDictionary<string, DirectoryState> s_directories = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Timer s_flushTimer = new(_ => _ = FlushDirtyDirectoriesAsync(), null, Timeout.Infinite, Timeout.Infinite);
    private static readonly object s_flushTimerGate = new();
    private static readonly SemaphoreSlim s_flushGate = new(1, 1);
    private static int s_residentCount;
    private static int s_directoryCount;
    private static int s_diskCleanupRunning;
    private static int s_flushesSinceDiskCleanup = DiskCleanupFlushInterval;

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

        var descriptor = DecoderCatalog.Describe(path, entry.GainMapProbe, entry.HeifAvifProbe, entry.JxlProbe, entry.WicImageProbe, entry.ExrProbe, entry.ContainerKind, entry.RadianceHdrProbe);
        var companionMedia = await ResolveCompanionMediaAsync(path, entry, cancellationToken);
        if (entry.CompanionMedia is { } previous && companionMedia is { } refreshed && previous != refreshed)
        {
            await UpdateCompanionMediaAsync(path, entry.LastWriteTimeUtc, previous, refreshed, cancellationToken);
        }
        var document = new HdrImageDocument(path, fileName, descriptor, entry.GainMapProbe, entry.HeifAvifProbe, entry.JxlProbe, entry.WicImageProbe, entry.ExrProbe, companionMedia, entry.RadianceHdrProbe, entry.ContainerKind);
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
            return await LivePhotoProbe.CompleteVideoProbeAsync(sidecar, allowCloudDownload: false, cancellationToken);
        }

        return await LivePhotoProbe.ProbeSidecarOnlyAsync(path, entry.ContainerKind, cancellationToken);
    }

    internal static async Task UpdateCompanionMediaAsync(
        string path, DateTime lastWriteTimeUtc, CompanionMedia previous, CompanionMedia refreshed,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory)) return;
        var state = await LockDirectoryStateAsync(directory, cancellationToken);
        var changed = false;
        try
        {
            var name = Path.GetFileName(path);
            if (state.File?.Entries.TryGetValue(name, out var entry) == true
                && entry.LastWriteTimeUtc == lastWriteTimeUtc && entry.Matches(path)
                && entry.CompanionMedia == previous)
            {
                // Replace the entry: a concurrent flush may hold the old snapshot.
                state.File.Entries[name] = entry.WithCompanionMedia(refreshed);
                state.MarkDirty();
                changed = true;
            }
        }
        finally
        {
            state.Lock.Release();
            TrimResidentDirectoryFiles(directory);
        }
        if (changed) ScheduleFlush();
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
            if (state.File is null)
            {
                state.SetResidentFile(new DirectoryMetadataCacheFile(), loaded: true, ref s_residentCount);
            }

            state.File!.Version = CurrentVersion;
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

    internal static string GetCacheRootDirectory()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable(CacheRootEnvironmentVariable);
        return !string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.GetFullPath(overrideDirectory)
            : s_defaultCacheRoot;
    }

    private static async Task<DirectoryState> LockDirectoryStateAsync(string directory, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (!s_directories.TryGetValue(directory, out var state))
            {
                var candidate = new DirectoryState(directory);
                if (s_directories.TryAdd(directory, candidate))
                {
                    Interlocked.Increment(ref s_directoryCount);
                    state = candidate;
                }
                else if (!s_directories.TryGetValue(directory, out state))
                {
                    continue;
                }
            }

            state.Touch();
            await state.Lock.WaitAsync(cancellationToken);
            if (state.IsEvicted)
            {
                state.Lock.Release();
                continue;
            }

            try
            {
                if (!state.IsLoaded)
                {
                    // Prefer the LocalAppData cache; fall back to the legacy in-folder
                    // file so directories cached by older versions keep their entries
                    // (they migrate to the new location on the next flush).
                    var loaded = await TryReadCacheFileAsync(Path.Combine(GetCacheRootDirectory(), state.CacheFileName), cancellationToken)
                        ?? await TryReadCacheFileAsync(Path.Combine(directory, LegacyCacheFileName), cancellationToken);
                    state.SetResidentFile(loaded, loaded: true, ref s_residentCount);
                }

                return state;
            }
            catch
            {
                state.Lock.Release();
                throw;
            }
        }
    }

    private static void ScheduleFlush()
    {
        var now = Environment.TickCount64;
        var dueTime = FlushDelay;
        foreach (var pair in s_directories)
        {
            var state = pair.Value;
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
            var flushedAny = false;
            foreach (var pair in s_directories)
            {
                var state = pair.Value;
                if (!state.IsDirty)
                {
                    continue;
                }

                await state.Lock.WaitAsync(cancellationToken);
                DirectoryMetadataCacheFile? snapshot = null;
                var snapshotVersion = 0L;
                try
                {
                    if (!state.IsEvicted && state.IsDirty && state.File is not null)
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

                var pruneResult = PruneStaleEntries(state.Directory, snapshot);
                var cacheRoot = GetCacheRootDirectory();
                var cachePath = Path.Combine(cacheRoot, state.CacheFileName);
                string? temporaryPath = null;
                try
                {
                    Directory.CreateDirectory(cacheRoot);
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
                        DirectoryMetadataJsonContext.Default.DirectoryMetadataCacheFile,
                        cancellationToken);
                    await writeStream.FlushAsync(cancellationToken);
                    writeStream.Close();
                    File.Move(temporaryPath, cachePath, overwrite: true);
                    temporaryPath = null;
                    flushedAny = true;

                    await state.Lock.WaitAsync(CancellationToken.None);
                    try
                    {
                        ApplyPruneResult(state.File, pruneResult);
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
            if (flushedAny)
            {
                TriggerDiskCacheCleanupIfNeeded();
            }
        }
        finally
        {
            s_flushGate.Release();
            if (HasDirtyDirectories())
            {
                ScheduleFlush();
            }
        }
    }

    private static bool HasDirtyDirectories()
    {
        foreach (var pair in s_directories)
        {
            if (pair.Value.IsDirty)
            {
                return true;
            }
        }

        return false;
    }

    private static PruneResult PruneStaleEntries(string directory, DirectoryMetadataCacheFile snapshot)
    {
        if (snapshot.Entries.Count == 0 || !Directory.Exists(directory))
        {
            return PruneResult.Empty;
        }

        var utcNow = DateTime.UtcNow;
        var verificationAge = snapshot.Entries.Count > MaxUnprunedEntryCount
            ? LargeDirectoryVerificationAge
            : StaleEntryVerificationAge;
        List<(string Name, DirectoryMetadataEntry Entry)>? candidates = null;

        foreach (var (name, entry) in snapshot.Entries)
        {
            var needsVerification = entry.LastVerifiedUtc is null
                || utcNow - entry.LastVerifiedUtc.Value >= verificationAge;
            if (!needsVerification)
            {
                continue;
            }

            candidates ??= [];
            candidates.Add((name, entry));
        }

        if (candidates is null || candidates.Count == 0)
        {
            return PruneResult.Empty;
        }

        if (candidates.Count > MaxPruneChecksPerFlush)
        {
            candidates.Sort(static (left, right) =>
                (left.Entry.LastVerifiedUtc ?? DateTime.MinValue)
                    .CompareTo(right.Entry.LastVerifiedUtc ?? DateTime.MinValue));
        }

        List<(string Name, DirectoryMetadataEntry Original)>? removed = null;
        List<(string Name, DirectoryMetadataEntry Original, DirectoryMetadataEntry Refreshed)>? verified = null;
        var checkLimit = Math.Min(candidates.Count, MaxPruneChecksPerFlush);

        for (var i = 0; i < checkLimit; i++)
        {
            var (name, entry) = candidates[i];
            string fullPath;
            try
            {
                fullPath = Path.Combine(directory, name);
            }
            catch
            {
                continue;
            }

            if (!File.Exists(fullPath))
            {
                snapshot.Entries.Remove(name);
                removed ??= [];
                removed.Add((name, entry));
            }
            else
            {
                var refreshed = entry.WithLastVerifiedUtc(utcNow);
                snapshot.Entries[name] = refreshed;
                verified ??= [];
                verified.Add((name, entry, refreshed));
            }
        }

        return new PruneResult(removed, verified);
    }

    private static void ApplyPruneResult(DirectoryMetadataCacheFile? liveFile, PruneResult pruneResult)
    {
        if (liveFile is null)
        {
            return;
        }

        if (pruneResult.Removed is not null)
        {
            foreach (var (name, original) in pruneResult.Removed)
            {
                if (liveFile.Entries.TryGetValue(name, out var current)
                    && ReferenceEquals(current, original))
                {
                    liveFile.Entries.Remove(name);
                }
            }
        }

        if (pruneResult.Verified is not null)
        {
            foreach (var (name, original, refreshed) in pruneResult.Verified)
            {
                if (liveFile.Entries.TryGetValue(name, out var current)
                    && ReferenceEquals(current, original))
                {
                    liveFile.Entries[name] = refreshed;
                }
            }
        }
    }

    private static void TriggerDiskCacheCleanupIfNeeded()
    {
        if (Interlocked.Increment(ref s_flushesSinceDiskCleanup) < DiskCleanupFlushInterval)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref s_diskCleanupRunning, 1, 0) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref s_flushesSinceDiskCleanup, 0);
        _ = Task.Run(TrimDiskCacheFiles);
    }

    private static void TrimDiskCacheFiles()
    {
        try
        {
            var cacheRoot = GetCacheRootDirectory();
            if (!Directory.Exists(cacheRoot))
            {
                return;
            }

            var files = new DirectoryInfo(cacheRoot).GetFiles("*.json");
            if (files.Length <= MaxDiskCacheFiles)
            {
                return;
            }

            Array.Sort(
                files,
                static (left, right) => GetMostRecentActivityUtc(left).CompareTo(GetMostRecentActivityUtc(right)));

            var removeCount = files.Length - MaxDiskCacheFiles;
            for (var i = 0; i < removeCount; i++)
            {
                TryDeleteFile(files[i].FullName);
            }
        }
        catch
        {
        }
        finally
        {
            Volatile.Write(ref s_diskCleanupRunning, 0);
        }
    }

    private static DateTime GetMostRecentActivityUtc(FileInfo file)
    {
        var lastWrite = file.LastWriteTimeUtc;
        var lastAccess = file.LastAccessTimeUtc;
        return lastAccess > lastWrite ? lastAccess : lastWrite;
    }

    private static void TrimResidentDirectoryFiles(string? activeDirectory)
    {
        if (Volatile.Read(ref s_residentCount) <= MaxResidentDirectoryFiles
            && Volatile.Read(ref s_directoryCount) <= MaxDirectoryStates)
        {
            return;
        }

        if (Volatile.Read(ref s_residentCount) > MaxResidentDirectoryFiles)
        {
            var residentCandidates = new List<DirectoryState>();
            foreach (var pair in s_directories)
            {
                var state = pair.Value;
                if (state.IsLoaded && state.File is not null)
                {
                    residentCandidates.Add(state);
                }
            }

            if (residentCandidates.Count > MaxResidentDirectoryFiles)
            {
                residentCandidates.Sort((left, right) =>
                {
                    var leftActive = string.Equals(left.Directory, activeDirectory, StringComparison.OrdinalIgnoreCase);
                    var rightActive = string.Equals(right.Directory, activeDirectory, StringComparison.OrdinalIgnoreCase);
                    var activeCompare = rightActive.CompareTo(leftActive);
                    return activeCompare != 0
                        ? activeCompare
                        : right.LastAccessTicks.CompareTo(left.LastAccessTicks);
                });

                var residentCount = residentCandidates.Count;
                for (var i = residentCandidates.Count - 1; i >= 0 && residentCount > MaxResidentDirectoryFiles; i--)
                {
                    var state = residentCandidates[i];
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

                        state.SetResidentFile(null, loaded: false, ref s_residentCount);
                        residentCount--;
                    }
                    finally
                    {
                        state.Lock.Release();
                    }
                }
            }
        }

        if (Volatile.Read(ref s_directoryCount) > MaxDirectoryStates)
        {
            var stateCandidates = new List<DirectoryState>();
            foreach (var pair in s_directories)
            {
                stateCandidates.Add(pair.Value);
            }

            if (stateCandidates.Count > MaxDirectoryStates)
            {
                stateCandidates.Sort(static (left, right) => left.LastAccessTicks.CompareTo(right.LastAccessTicks));
                var totalStates = stateCandidates.Count;
                foreach (var state in stateCandidates)
                {
                    if (totalStates <= MaxDirectoryStates)
                    {
                        break;
                    }

                    if (string.Equals(state.Directory, activeDirectory, StringComparison.OrdinalIgnoreCase)
                        || state.IsDirty
                        || !state.Lock.Wait(0))
                    {
                        continue;
                    }

                    try
                    {
                        if (state.IsDirty || state.IsEvicted)
                        {
                            continue;
                        }

                        if (s_directories.TryRemove(new KeyValuePair<string, DirectoryState>(state.Directory, state)))
                        {
                            state.IsEvicted = true;
                            state.SetResidentFile(null, loaded: false, ref s_residentCount);
                            Interlocked.Decrement(ref s_directoryCount);
                            totalStates--;
                        }
                    }
                    finally
                    {
                        // Never Dispose state.Lock: a concurrent waiter in LockDirectoryStateAsync
                        // may have fetched this instance right before TryRemove and will observe IsEvicted.
                        state.Lock.Release();
                    }
                }
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

    internal static string GetCacheFilePath(string directory)
    {
        return Path.Combine(GetCacheRootDirectory(), ComputeCacheFileName(directory));
    }

    private static string ComputeCacheFileName(string directory)
    {
        // Path comparison on Windows is case-insensitive, so the hash key is
        // normalised the same way the in-memory dictionaries compare paths.
        var normalized = Path.TrimEndingDirectorySeparator(directory).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return hash + ".json";
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
            var loaded = await JsonSerializer.DeserializeAsync(
                stream,
                DirectoryMetadataJsonContext.Default.DirectoryMetadataCacheFile,
                cancellationToken);
            if (loaded?.Version != CurrentVersion)
            {
                return null;
            }

            if (!ReferenceEquals(loaded.Entries.Comparer, StringComparer.OrdinalIgnoreCase))
            {
                loaded.Entries = new Dictionary<string, DirectoryMetadataEntry>(loaded.Entries, StringComparer.OrdinalIgnoreCase);
            }

            return loaded;
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct PruneResult(
        List<(string Name, DirectoryMetadataEntry Original)>? Removed,
        List<(string Name, DirectoryMetadataEntry Original, DirectoryMetadataEntry Refreshed)>? Verified)
    {
        public static PruneResult Empty => default;
    }

    private sealed class DirectoryState
    {
        private long _lastAccessTicks = Environment.TickCount64;
        private long _firstDirtyTicks;
        private long _changeVersion;
        private int _isDirty;
        private int _isResident;

        public DirectoryState(string directory)
        {
            Directory = directory;
            CacheFileName = ComputeCacheFileName(directory);
        }

        public string Directory { get; }

        public string CacheFileName { get; }

        public SemaphoreSlim Lock { get; } = new(1, 1);

        public DirectoryMetadataCacheFile? File { get; private set; }

        public bool IsLoaded { get; private set; }

        public bool IsEvicted { get; set; }

        public bool IsDirty => Volatile.Read(ref _isDirty) != 0;

        public long FirstDirtyTicks => Interlocked.Read(ref _firstDirtyTicks);

        public long ChangeVersion => Interlocked.Read(ref _changeVersion);

        public long LastAccessTicks => Interlocked.Read(ref _lastAccessTicks);

        public void SetResidentFile(DirectoryMetadataCacheFile? file, bool loaded, ref int residentCounter)
        {
            File = file;
            IsLoaded = loaded;
            var shouldCountResident = loaded && file is not null ? 1 : 0;
            var previous = Interlocked.Exchange(ref _isResident, shouldCountResident);
            if (previous != shouldCountResident)
            {
                if (shouldCountResident == 1)
                {
                    Interlocked.Increment(ref residentCounter);
                }
                else
                {
                    Interlocked.Decrement(ref residentCounter);
                }
            }
        }

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
}

internal sealed class DirectoryMetadataCacheFile
{
    public int Version { get; set; } = DirectoryMetadataCache.CurrentVersion;

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

internal sealed class DirectoryMetadataEntry
{
    public long Length { get; set; }

    public DateTime LastWriteTimeUtc { get; set; }

    public DateTime? LastVerifiedUtc { get; set; }

    public FileContainerKind ContainerKind { get; set; }

    public string? ExifSummary { get; set; }

    public GainMapProbeResult? GainMapProbe { get; set; }

    public HeifAvifProbeResult? HeifAvifProbe { get; set; }

    public JxlProbeResult? JxlProbe { get; set; }

    public WicImageProbeResult? WicImageProbe { get; set; }

    public ExrProbeResult? ExrProbe { get; set; }

    public RadianceHdrHeader? RadianceHdrProbe { get; set; }

    public CompanionMedia? CompanionMedia { get; set; }

    public DirectoryMetadataEntry WithCompanionMedia(CompanionMedia media)
    {
        var copy = (DirectoryMetadataEntry)MemberwiseClone();
        copy.CompanionMedia = media;
        return copy;
    }

    public DirectoryMetadataEntry WithLastVerifiedUtc(DateTime verifiedUtc)
    {
        var copy = (DirectoryMetadataEntry)MemberwiseClone();
        copy.LastVerifiedUtc = verifiedUtc;
        return copy;
    }

    public static DirectoryMetadataEntry Create(ImageLoadResult result, FileContainerKind containerKind)
    {
        var info = new FileInfo(result.Document.Path);
        return new DirectoryMetadataEntry
        {
            Length = info.Length,
            LastWriteTimeUtc = result.LastWriteTimeUtc,
            LastVerifiedUtc = DateTime.UtcNow,
            ContainerKind = containerKind,
            ExifSummary = result.ExifSummary,
            GainMapProbe = result.Document.GainMapProbe,
            HeifAvifProbe = result.Document.HeifAvifProbe,
            JxlProbe = result.Document.JxlProbe,
            WicImageProbe = result.Document.WicImageProbe,
            ExrProbe = result.Document.ExrProbe?.PixelWidth is > 0 && result.Document.ExrProbe.PixelHeight is > 0
                ? result.Document.ExrProbe
                : null,
            RadianceHdrProbe = result.Document.RadianceHdrProbe,
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
