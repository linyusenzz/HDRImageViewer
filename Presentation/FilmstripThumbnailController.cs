using System.Collections.ObjectModel;
using HdrImageViewer.Services;
using Microsoft.UI.Xaml.Media;

namespace HdrImageViewer.Presentation;

/// <summary>
/// Owns the filmstrip thumbnail cache and the bounded-concurrency background
/// loading of thumbnails around the current focus index. Extracted from
/// HomePage so the viewer page no longer carries this state and logic directly.
/// All public methods are expected to be called on the UI thread (the cache and
/// the <see cref="FilmstripImageItem"/> updates are not synchronised).
/// </summary>
internal sealed class FilmstripThumbnailController
{
    private const int CacheRadius = 36;
    private const int ConcurrentLoads = 4;
    private const uint MaxPixelSize = 128;

    private readonly ObservableCollection<FilmstripImageItem> _items;
    private readonly CancellationToken _lifetimeToken;
    private readonly Dictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;

    public FilmstripThumbnailController(ObservableCollection<FilmstripImageItem> items, CancellationToken lifetimeToken)
    {
        _items = items;
        _lifetimeToken = lifetimeToken;
    }

    public bool TryGetCached(string path, out ImageSource? thumbnail) => _cache.TryGetValue(path, out thumbnail);

    public void Cancel() => CancelCurrentLoad();

    public void Dispose()
    {
        CancelCurrentLoad();
    }

    public void QueueLoads(int focusIndex)
    {
        CancelCurrentLoad();
        if (_items.Count == 0)
        {
            return;
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        lock (_gate)
        {
            _cts = source;
        }

        var loadOrder = CreateLoadOrder(focusIndex);
        _ = RunLoadThumbnailsAsync(loadOrder, source);
    }

    public void PruneCache(IReadOnlyList<string> folderPaths, int focusIndex)
    {
        var keepPaths = CreateKeepPaths(folderPaths, focusIndex);
        foreach (var path in _cache.Keys.ToList())
        {
            if (!keepPaths.Contains(path))
            {
                _cache.Remove(path);
            }
        }
    }

    private IReadOnlyList<FilmstripImageItem> CreateLoadOrder(int focusIndex)
    {
        if (focusIndex < 0)
        {
            return _items.Take(CacheRadius * 2).ToList();
        }

        // Walk outward from the focus instead of sorting the whole collection
        // by distance: the full sort allocates and orders one entry per folder
        // image, which is wasteful for folders with tens of thousands of files.
        var capacity = Math.Min(_items.Count, (CacheRadius * 2) + 1);
        var focus = Math.Clamp(focusIndex, 0, _items.Count - 1);
        var loadOrder = new List<FilmstripImageItem>(capacity) { _items[focus] };
        for (var offset = 1; loadOrder.Count < capacity; offset++)
        {
            var before = focus - offset;
            var after = focus + offset;
            if (before >= 0)
            {
                loadOrder.Add(_items[before]);
            }

            if (after < _items.Count && loadOrder.Count < capacity)
            {
                loadOrder.Add(_items[after]);
            }
        }

        return loadOrder;
    }

    private async Task LoadThumbnailsAsync(IReadOnlyList<FilmstripImageItem> items, CancellationToken cancellationToken)
    {
        using var loadGate = new SemaphoreSlim(ConcurrentLoads);
        var tasks = items.Select(item => LoadThumbnailAsync(item, loadGate, cancellationToken)).ToArray();
        await Task.WhenAll(tasks);
    }

    private async Task RunLoadThumbnailsAsync(IReadOnlyList<FilmstripImageItem> items, CancellationTokenSource source)
    {
        try
        {
            await LoadThumbnailsAsync(items, source.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_cts, source))
                {
                    _cts = null;
                }
            }

            source.Dispose();
        }
    }

    private void CancelCurrentLoad()
    {
        CancellationTokenSource? source;
        lock (_gate)
        {
            source = _cts;
            _cts = null;
        }

        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task LoadThumbnailAsync(
        FilmstripImageItem item,
        SemaphoreSlim loadGate,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (_cache.TryGetValue(item.Path, out var cachedThumbnail))
        {
            item.Thumbnail = cachedThumbnail;
            return;
        }

        var acquiredLoadSlot = false;
        try
        {
            await loadGate.WaitAsync(cancellationToken);
            acquiredLoadSlot = true;
            if (_cache.ContainsKey(item.Path))
            {
                if (_cache.TryGetValue(item.Path, out cachedThumbnail))
                {
                    item.Thumbnail = cachedThumbnail;
                }

                return;
            }

            ImageSource? quickThumbnail = null;
            if (item.Thumbnail is null)
            {
                quickThumbnail = await PhotoThumbnailService.CreateQuickAsync(item.Path, MaxPixelSize, cancellationToken);
                if (quickThumbnail is not null && !cancellationToken.IsCancellationRequested)
                {
                    item.Thumbnail = quickThumbnail;
                }
            }

            var thumbnail = await PhotoThumbnailService.CreateHdrToneMappedAsync(item.Path, MaxPixelSize, cancellationToken)
                ?? quickThumbnail
                ?? item.Thumbnail;
            if (thumbnail is null || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _cache[item.Path] = thumbnail;
            item.Thumbnail = thumbnail;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (acquiredLoadSlot)
            {
                loadGate.Release();
            }
        }
    }

    private static HashSet<string> CreateKeepPaths(IReadOnlyList<string> folderPaths, int focusIndex)
    {
        var keepPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (folderPaths.Count == 0)
        {
            return keepPaths;
        }

        if (focusIndex < 0)
        {
            foreach (var path in folderPaths.Take(CacheRadius * 2))
            {
                keepPaths.Add(path);
            }

            return keepPaths;
        }

        var start = Math.Max(0, focusIndex - CacheRadius);
        var end = Math.Min(folderPaths.Count - 1, focusIndex + CacheRadius);
        for (var index = start; index <= end; index++)
        {
            keepPaths.Add(folderPaths[index]);
        }

        return keepPaths;
    }
}
