using System.Collections.Concurrent;

namespace HdrImageViewer.Services;

public static class ImagePreloadCache
{
    private const long MaxDecodedPixelCacheBytes = 320L * 1024L * 1024L;

    private static readonly ConcurrentDictionary<string, CacheEntry> s_cache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<ImageLoadResult> GetLoadResultAsync(string path, CancellationToken cancellationToken = default)
    {
        var lastWriteTimeUtc = File.GetLastWriteTimeUtc(path);
        if (s_cache.TryGetValue(path, out var cached)
            && cached.LastWriteTimeUtc == lastWriteTimeUtc
            && cached.LoadResult is not null)
        {
            return cached.LoadResult;
        }

        var result = await ImageDocumentLoader.LoadAsync(path, cancellationToken);
        var entry = s_cache.AddOrUpdate(
            path,
            _ => new CacheEntry(result.LastWriteTimeUtc) { LoadResult = result },
            (_, existing) => existing.LastWriteTimeUtc == result.LastWriteTimeUtc
                ? existing with { LoadResult = result }
                : new CacheEntry(result.LastWriteTimeUtc) { LoadResult = result });
        return entry.LoadResult ?? result;
    }

    public static bool TryGetGainMapInputs(string path, DateTime lastWriteTimeUtc, int? maxPixelSize, out GainMapRenderInputs inputs)
    {
        if (s_cache.TryGetValue(path, out var entry)
            && entry.LastWriteTimeUtc == lastWriteTimeUtc
            && entry.GainMapInputs is not null
            && IsDecodedSizeUsable(entry.DecodedMaxPixelSize, maxPixelSize))
        {
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
            bitmap = entry.BaseBitmap;
            return true;
        }

        bitmap = null!;
        return false;
    }

    public static async Task PreloadAsync(string path, int? maxPixelSize = null, CancellationToken cancellationToken = default)
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
            s_cache[path] = entry with { LoadResult = loadResult, GainMapInputs = inputs, DecodedMaxPixelSize = maxPixelSize };
            return;
        }

        if (entry.BaseBitmap is not null && IsDecodedSizeUsable(entry.DecodedMaxPixelSize, maxPixelSize))
        {
            return;
        }

        var bitmap = await BitmapDecodeService.DecodeFileAsync(document.Path, document.HeifAvifProbe, maxPixelSize, cancellationToken);
        s_cache[path] = entry with { LoadResult = loadResult, BaseBitmap = bitmap, DecodedMaxPixelSize = maxPixelSize };
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
            }
        }

        if (decodedPriorityPaths is not null)
        {
            DropDecodedPayloadsOutsidePriority(decodedPriorityPaths);
        }

        TrimDecodedPayloadsToBudget(decodedPriorityPaths ?? pathsToKeep);
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
        if (cachedMaxPixelSize is null)
        {
            return true;
        }

        return requestedMaxPixelSize is not null && cachedMaxPixelSize.Value >= requestedMaxPixelSize.Value;
    }

    private static void TrimDecodedPayloadsToBudget(IReadOnlySet<string> priorityPaths)
    {
        var totalBytes = s_cache.Values.Sum(entry => entry.DecodedByteCount);
        if (totalBytes <= MaxDecodedPixelCacheBytes)
        {
            return;
        }

        var candidates = s_cache
            .Where(pair => pair.Value.DecodedByteCount > 0)
            .OrderBy(pair => priorityPaths.Contains(pair.Key) ? 1 : 0)
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
