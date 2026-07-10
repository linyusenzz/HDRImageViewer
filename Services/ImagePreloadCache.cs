using System.Collections.Concurrent;

namespace HdrImageViewer.Services;

public static class ImagePreloadCache
{
    private const long MaxDecodedPixelCacheBytes = 320L * 1024L * 1024L;

    private static readonly ConcurrentDictionary<string, CacheEntry> s_cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, long> s_lastAccessTicks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Lazy<Task<ImageLoadResult>>> s_inFlightLoads = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Lazy<Task>> s_inFlightPreloads = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<ImageLoadResult> GetLoadResultAsync(string path, CancellationToken cancellationToken = default)
    {
        var lastWriteTimeUtc = File.GetLastWriteTimeUtc(path);
        if (s_cache.TryGetValue(path, out var cached)
            && cached.LastWriteTimeUtc == lastWriteTimeUtc
            && cached.LoadResult is not null)
        {
            TouchLastAccess(path);
            return cached.LoadResult;
        }

        // Deduplicate concurrent loads of the same path (UI navigation racing
        // the adjacent-image preloader). The shared probe runs detached from
        // any single caller's token; each caller only stops waiting via its
        // own token, so one canceled waiter cannot fail the others.
        var lazy = s_inFlightLoads.GetOrAdd(path, p => new Lazy<Task<ImageLoadResult>>(() => LoadAndStoreAsync(p)));
        var loadTask = lazy.Value;
        _ = loadTask.ContinueWith(
            _ => s_inFlightLoads.TryRemove(new KeyValuePair<string, Lazy<Task<ImageLoadResult>>>(path, lazy)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        try
        {
            return await loadTask.WaitAsync(cancellationToken);
        }
        finally
        {
            if (loadTask.IsCompleted)
            {
                s_inFlightLoads.TryRemove(new KeyValuePair<string, Lazy<Task<ImageLoadResult>>>(path, lazy));
            }
        }
    }

    private static async Task<ImageLoadResult> LoadAndStoreAsync(string path)
    {
        var result = await ImageDocumentLoader.LoadAsync(path, CancellationToken.None);
        var entry = s_cache.AddOrUpdate(
            path,
            _ => new CacheEntry(result.LastWriteTimeUtc) { LoadResult = result },
            (_, existing) => existing.LastWriteTimeUtc == result.LastWriteTimeUtc
                ? existing with { LoadResult = result }
                : new CacheEntry(result.LastWriteTimeUtc) { LoadResult = result });
        TouchLastAccess(path);
        return entry.LoadResult ?? result;
    }

    public static bool TryGetGainMapInputs(string path, DateTime lastWriteTimeUtc, int? maxPixelSize, out GainMapRenderInputs inputs)
    {
        if (s_cache.TryGetValue(path, out var entry)
            && entry.LastWriteTimeUtc == lastWriteTimeUtc
            && entry.GainMapInputs is not null
            && IsDecodedSizeUsable(entry.DecodedMaxPixelSize, maxPixelSize))
        {
            TouchLastAccess(path);
            inputs = entry.GainMapInputs;
            return true;
        }

        inputs = null!;
        return false;
    }

    public static bool TryGetBaseBitmap(string path, DateTime lastWriteTimeUtc, int? maxPixelSize, out DecodedBitmap bitmap)
    {
        if (s_cache.TryGetValue(path, out var entry)
            && entry.LastWriteTimeUtc == lastWriteTimeUtc
            && entry.BaseBitmap is not null
            && IsDecodedSizeUsable(entry.DecodedMaxPixelSize, maxPixelSize))
        {
            TouchLastAccess(path);
            bitmap = entry.BaseBitmap;
            return true;
        }

        bitmap = null!;
        return false;
    }

    public static async Task PreloadAsync(string path, int? maxPixelSize = null, CancellationToken cancellationToken = default)
    {
        // Deduplicate identical concurrent preloads. The shared decode runs
        // under the first requester's token; if that owner cancels the shared
        // work while we still want it, retry as the new owner.
        var key = $"{path}|{maxPixelSize?.ToString() ?? "full"}";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lazy = s_inFlightPreloads.GetOrAdd(key, _ => new Lazy<Task>(() => PreloadCoreAsync(path, maxPixelSize, cancellationToken)));
            var preloadTask = lazy.Value;
            _ = preloadTask.ContinueWith(
                _ => s_inFlightPreloads.TryRemove(new KeyValuePair<string, Lazy<Task>>(key, lazy)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            try
            {
                await preloadTask.WaitAsync(cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                if (preloadTask.IsCompleted)
                {
                    s_inFlightPreloads.TryRemove(new KeyValuePair<string, Lazy<Task>>(key, lazy));
                }
            }
        }
    }

    private static async Task PreloadCoreAsync(string path, int? maxPixelSize, CancellationToken cancellationToken)
    {
        var loadResult = await GetLoadResultAsync(path, cancellationToken);
        var document = loadResult.Document;
        var lastWriteTimeUtc = loadResult.LastWriteTimeUtc;
        var entry = s_cache.GetOrAdd(path, _ => new CacheEntry(lastWriteTimeUtc));
        if (entry.LastWriteTimeUtc != lastWriteTimeUtc)
        {
            entry = new CacheEntry(lastWriteTimeUtc) { LoadResult = loadResult };
            s_cache[path] = entry;
        }

        if (document.HasRenderableGainMap)
        {
            if (entry.GainMapInputs is not null && IsDecodedSizeUsable(entry.DecodedMaxPixelSize, maxPixelSize))
            {
                return;
            }

            var inputs = await GainMapRenderInputDecoder.DecodeRenderInputsAsync(document, maxPixelSize, cancellationToken);
            StoreGainMapInputs(path, lastWriteTimeUtc, loadResult, inputs, maxPixelSize);
            TouchLastAccess(path);
            return;
        }

        if (entry.BaseBitmap is not null && IsDecodedSizeUsable(entry.DecodedMaxPixelSize, maxPixelSize))
        {
            return;
        }

        var bitmap = await BitmapDecodeService.DecodeDocumentAsync(document, maxPixelSize, cancellationToken);
        StoreBaseBitmap(path, lastWriteTimeUtc, loadResult, bitmap, maxPixelSize);
        TouchLastAccess(path);
    }

    public static void KeepOnly(
        IReadOnlySet<string> pathsToKeep,
        IReadOnlySet<string>? decodedPriorityPaths = null)
    {
        foreach (var path in s_cache.Keys)
        {
            if (!pathsToKeep.Contains(path))
            {
                s_cache.TryRemove(path, out _);
                s_lastAccessTicks.TryRemove(path, out _);
            }
        }

        if (decodedPriorityPaths is not null)
        {
            DropDecodedPayloadsOutsidePriority(decodedPriorityPaths);
        }

        TrimDecodedPayloadsToBudget(decodedPriorityPaths ?? pathsToKeep);
    }

    private static void TouchLastAccess(string path)
    {
        s_lastAccessTicks[path] = Environment.TickCount64;
    }

    private sealed record CacheEntry(DateTime LastWriteTimeUtc)
    {
        public ImageLoadResult? LoadResult { get; init; }

        public DecodedBitmap? BaseBitmap { get; init; }

        public GainMapRenderInputs? GainMapInputs { get; init; }

        public int? DecodedMaxPixelSize { get; init; }

        public long DecodedByteCount =>
            (BaseBitmap?.ApproximateByteCount ?? 0L)
            + (GainMapInputs?.ApproximateByteCount ?? 0L);

        public CacheEntry WithoutDecodedPayloads()
        {
            return this with
            {
                BaseBitmap = null,
                GainMapInputs = null,
                DecodedMaxPixelSize = null,
            };
        }
    }

    private static bool IsDecodedSizeUsable(int? cachedMaxPixelSize, int? requestedMaxPixelSize)
    {
        return DecodedCachePolicy.IsAtLeastAsDetailed(cachedMaxPixelSize, requestedMaxPixelSize);
    }

    private static void StoreGainMapInputs(
        string path,
        DateTime lastWriteTimeUtc,
        ImageLoadResult loadResult,
        GainMapRenderInputs inputs,
        int? maxPixelSize)
    {
        var candidate = new CacheEntry(lastWriteTimeUtc)
        {
            LoadResult = loadResult,
            GainMapInputs = inputs,
            DecodedMaxPixelSize = maxPixelSize,
        };
        s_cache.AddOrUpdate(
            path,
            candidate,
            (_, current) => current.LastWriteTimeUtc == lastWriteTimeUtc
                && current.GainMapInputs is not null
                && DecodedCachePolicy.IsAtLeastAsDetailed(current.DecodedMaxPixelSize, maxPixelSize)
                    ? current with { LoadResult = loadResult }
                    : candidate);
    }

    private static void StoreBaseBitmap(
        string path,
        DateTime lastWriteTimeUtc,
        ImageLoadResult loadResult,
        DecodedBitmap bitmap,
        int? maxPixelSize)
    {
        var candidate = new CacheEntry(lastWriteTimeUtc)
        {
            LoadResult = loadResult,
            BaseBitmap = bitmap,
            DecodedMaxPixelSize = maxPixelSize,
        };
        s_cache.AddOrUpdate(
            path,
            candidate,
            (_, current) => current.LastWriteTimeUtc == lastWriteTimeUtc
                && current.BaseBitmap is not null
                && DecodedCachePolicy.IsAtLeastAsDetailed(current.DecodedMaxPixelSize, maxPixelSize)
                    ? current with { LoadResult = loadResult }
                    : candidate);
    }

    private static void TrimDecodedPayloadsToBudget(IReadOnlySet<string> priorityPaths)
    {
        var totalBytes = s_cache.Values.Sum(entry => entry.DecodedByteCount);
        if (totalBytes <= MaxDecodedPixelCacheBytes)
        {
            return;
        }

        // Evict non-priority entries first, least-recently-used within each
        // group, instead of whatever order the dictionary happens to yield.
        var candidates = s_cache
            .Where(pair => pair.Value.DecodedByteCount > 0)
            .OrderBy(pair => priorityPaths.Contains(pair.Key) ? 1 : 0)
            .ThenBy(pair => s_lastAccessTicks.TryGetValue(pair.Key, out var ticks) ? ticks : 0L)
            .ToList();

        foreach (var (path, entry) in candidates)
        {
            if (totalBytes <= MaxDecodedPixelCacheBytes)
            {
                return;
            }

            if (!s_cache.TryUpdate(path, entry.WithoutDecodedPayloads(), entry))
            {
                continue;
            }

            totalBytes -= entry.DecodedByteCount;
        }
    }

    private static void DropDecodedPayloadsOutsidePriority(IReadOnlySet<string> priorityPaths)
    {
        foreach (var (path, entry) in s_cache)
        {
            if (entry.DecodedByteCount == 0 || priorityPaths.Contains(path))
            {
                continue;
            }

            s_cache.TryUpdate(path, entry.WithoutDecodedPayloads(), entry);
        }
    }
}
