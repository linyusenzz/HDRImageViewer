using System.Collections.Concurrent;

namespace HdrImageViewer.Services;

public static class ImagePreloadCache
{
    private const long MaxDecodedPixelCacheBytes = 320L * 1024L * 1024L;

    private static readonly ConcurrentDictionary<string, CacheEntry> s_cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, long> s_lastAccessTicks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SharedAsyncOperation<ImageLoadResult>> s_inFlightLoads = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SharedAsyncOperation<object?>> s_inFlightPreloads = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<ImageLoadResult> GetLoadResultAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lastWriteTimeUtc = File.GetLastWriteTimeUtc(path);
            if (s_cache.TryGetValue(path, out var cached)
                && cached.LastWriteTimeUtc == lastWriteTimeUtc
                && cached.LoadResult is not null)
            {
                TouchLastAccess(path);
                return await RefreshCompanionMediaAsync(cached.LoadResult, allowCloudDownload: false, cancellationToken);
            }

            var candidate = new SharedAsyncOperation<ImageLoadResult>(
                token => LoadAndStoreAsync(path, token));
            var operation = s_inFlightLoads.GetOrAdd(path, candidate);
            var ownsOperation = ReferenceEquals(candidate, operation);
            if (!ownsOperation)
            {
                candidate.Dispose();
            }

            if (operation.IsAbandoned)
            {
                s_inFlightLoads.TryRemove(
                    new KeyValuePair<string, SharedAsyncOperation<ImageLoadResult>>(
                        path,
                        operation));
                continue;
            }

            Task<ImageLoadResult> waitTask;
            try
            {
                waitTask = operation.WaitAsync(cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                continue;
            }

            if (ownsOperation)
            {
                var completion = operation.Completion;
                _ = completion.ContinueWith(
                    _ =>
                    {
                        s_inFlightLoads.TryRemove(
                            new KeyValuePair<string, SharedAsyncOperation<ImageLoadResult>>(
                                path,
                                operation));
                        operation.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            try
            {
                return await waitTask;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                s_inFlightLoads.TryRemove(
                    new KeyValuePair<string, SharedAsyncOperation<ImageLoadResult>>(
                        path,
                        operation));
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static async Task<ImageLoadResult> LoadAndStoreAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var result = await ImageDocumentLoader.LoadAsync(path, cancellationToken);
        var entry = s_cache.AddOrUpdate(
            path,
            _ => new CacheEntry(result.LastWriteTimeUtc) { LoadResult = result },
            (_, existing) => existing.LastWriteTimeUtc == result.LastWriteTimeUtc
                ? existing with { LoadResult = result }
                : new CacheEntry(result.LastWriteTimeUtc) { LoadResult = result });
        TouchLastAccess(path);
        return entry.LoadResult ?? result;
    }

    internal static async Task<ImageLoadResult> RefreshCompanionMediaAsync(
        string path, bool allowCloudDownload, CancellationToken cancellationToken)
    {
        var result = await GetLoadResultAsync(path, cancellationToken);
        return await RefreshCompanionMediaAsync(result, allowCloudDownload, cancellationToken);
    }

    private static async Task<ImageLoadResult> RefreshCompanionMediaAsync(
        ImageLoadResult result, bool allowCloudDownload, CancellationToken cancellationToken)
    {
        if (result.Document.CompanionMedia is not { VideoProbe: null, VideoProbeCompleted: false, IsEmbedded: false } previous)
            return result;
        var refreshed = await LivePhotoProbe.CompleteVideoProbeAsync(previous, allowCloudDownload, cancellationToken);
        if (refreshed == previous) return result;
        var updated = result with { Document = result.Document with { CompanionMedia = refreshed } };
        var path = result.Document.Path;
        while (s_cache.TryGetValue(path, out var cached)
            && cached.LastWriteTimeUtc == result.LastWriteTimeUtc
            && cached.LoadResult?.Document.CompanionMedia == previous)
        {
            if (s_cache.TryUpdate(path, cached with { LoadResult = updated }, cached)) break;
        }
        await DirectoryMetadataCache.UpdateCompanionMediaAsync(
            path, result.LastWriteTimeUtc, previous, refreshed, cancellationToken);
        return updated;
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
        var key = $"{path}|{maxPixelSize?.ToString() ?? "full"}";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = new SharedAsyncOperation<object?>(
                token => PreloadCoreAsSharedAsync(path, maxPixelSize, token));
            var operation = s_inFlightPreloads.GetOrAdd(key, candidate);
            var ownsOperation = ReferenceEquals(candidate, operation);
            if (!ownsOperation)
            {
                candidate.Dispose();
            }

            if (operation.IsAbandoned)
            {
                s_inFlightPreloads.TryRemove(
                    new KeyValuePair<string, SharedAsyncOperation<object?>>(key, operation));
                continue;
            }

            Task<object?> waitTask;
            try
            {
                waitTask = operation.WaitAsync(cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                continue;
            }

            if (ownsOperation)
            {
                _ = operation.Completion.ContinueWith(
                    _ =>
                    {
                        s_inFlightPreloads.TryRemove(
                            new KeyValuePair<string, SharedAsyncOperation<object?>>(key, operation));
                        operation.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            try
            {
                await waitTask;
                return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                s_inFlightPreloads.TryRemove(
                    new KeyValuePair<string, SharedAsyncOperation<object?>>(key, operation));
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static async Task<object?> PreloadCoreAsSharedAsync(
        string path,
        int? maxPixelSize,
        CancellationToken cancellationToken)
    {
        await PreloadCoreAsync(path, maxPixelSize, cancellationToken);
        return null;
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

    public static long KeepOnly(
        IReadOnlySet<string> pathsToKeep,
        IReadOnlySet<string>? decodedPriorityPaths = null)
    {
        var evictedBytes = 0L;
        foreach (var path in s_cache.Keys)
        {
            if (!pathsToKeep.Contains(path)
                && s_cache.TryRemove(path, out var removed))
            {
                evictedBytes += removed.DecodedByteCount;
                s_lastAccessTicks.TryRemove(path, out _);
            }
        }

        if (decodedPriorityPaths is not null)
        {
            evictedBytes += DropDecodedPayloadsOutsidePriority(
                decodedPriorityPaths);
        }

        evictedBytes += TrimDecodedPayloadsToBudget(
            decodedPriorityPaths ?? pathsToKeep);
        ImageMemoryPressureService.ReportEvictedBytes(evictedBytes);
        return evictedBytes;
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

    private static long TrimDecodedPayloadsToBudget(
        IReadOnlySet<string> priorityPaths)
    {
        var totalBytes = s_cache.Values.Sum(entry => entry.DecodedByteCount);
        if (totalBytes <= MaxDecodedPixelCacheBytes)
        {
            return 0L;
        }

        // Evict non-priority entries first, least-recently-used within each
        // group, instead of whatever order the dictionary happens to yield.
        var candidates = s_cache
            .Where(pair => pair.Value.DecodedByteCount > 0)
            .OrderBy(pair => priorityPaths.Contains(pair.Key) ? 1 : 0)
            .ThenBy(pair => s_lastAccessTicks.TryGetValue(pair.Key, out var ticks) ? ticks : 0L)
            .ToList();
        var evictedBytes = 0L;

        foreach (var (path, entry) in candidates)
        {
            if (totalBytes <= MaxDecodedPixelCacheBytes)
            {
                break;
            }

            if (!s_cache.TryUpdate(path, entry.WithoutDecodedPayloads(), entry))
            {
                continue;
            }

            totalBytes -= entry.DecodedByteCount;
            evictedBytes += entry.DecodedByteCount;
        }

        return evictedBytes;
    }

    private static long DropDecodedPayloadsOutsidePriority(
        IReadOnlySet<string> priorityPaths)
    {
        var evictedBytes = 0L;
        foreach (var (path, entry) in s_cache)
        {
            if (entry.DecodedByteCount == 0 || priorityPaths.Contains(path))
            {
                continue;
            }

            if (s_cache.TryUpdate(path, entry.WithoutDecodedPayloads(), entry))
            {
                evictedBytes += entry.DecodedByteCount;
            }
        }

        return evictedBytes;
    }
}
