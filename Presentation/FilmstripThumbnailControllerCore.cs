namespace HdrImageViewer.Presentation;

/// <summary>
/// Owns the filmstrip thumbnail cache and the bounded-concurrency background
/// loading of thumbnails around the current focus index. Independent of WinUI
/// so cache ownership and async publication can be exercised directly in tests.
/// All public methods are expected to be called on the UI thread (the cache and
/// the <see cref="TItem"/> updates are not synchronised).
/// </summary>
internal sealed class FilmstripThumbnailControllerCore<TItem, TThumbnail> : IDisposable
    where TItem : class, IFilmstripThumbnailItem<TThumbnail>
    where TThumbnail : class
{
    private const int ConcurrentLoads = 4;

    private readonly IReadOnlyList<TItem> _items;
    private readonly Dictionary<string, TThumbnail> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TItem> _loadedItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly RetainedThumbnailLoads _loads;
    private readonly Func<string, CancellationToken, Task<TThumbnail?>> _quickLoad;
    private readonly Func<string, CancellationToken, Task<TThumbnail?>> _refinedLoad;
    public Func<TItem, bool>? RefreshItemState { get; init; }
    private IReadOnlyList<TItem> _scope = [];
    private bool _disposed;
    private bool _forceReload;

    public FilmstripThumbnailControllerCore(IReadOnlyList<TItem> items,
        Func<string, CancellationToken, Task<TThumbnail?>> quickLoad,
        Func<string, CancellationToken, Task<TThumbnail?>> refinedLoad,
        CancellationToken lifetimeToken)
    {
        _items = items;
        _quickLoad = quickLoad;
        _refinedLoad = refinedLoad;
        _loads = new RetainedThumbnailLoads(lifetimeToken, ConcurrentLoads);
    }

    public Task WhenIdleAsync() => _loads.WhenIdleAsync();

    public bool TryGetCached(string path, out TThumbnail? thumbnail) => _cache.TryGetValue(path, out thumbnail);

    public void Cancel()
    {
        foreach (var item in _scope)
        {
            item.IsLoading = false;
            item.HasLoadError = false;
        }
        _scope = [];
        _loads.KeepOnly(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        ClearOutsideScope(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loads.Dispose();
        _scope = [];
        ClearOutsideScope(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    public void QueueLoads(int focusIndex, int firstVisible = -1, int lastVisible = -1, bool refreshState = false)
    {
        if (_disposed) return;
        var loadOrder = FilmstripThumbnailWindow.GetLoadOrder(_items.Count, focusIndex, firstVisible, lastVisible)
            .Select(index => _items[index]).ToArray();
        if (!refreshState && !_forceReload && _scope.SequenceEqual(loadOrder)) return;
        // Refresh the bounded visible window even when its thumbnails are cached.
        // A state transition can also make a cached preview/failure obsolete.
        foreach (var item in loadOrder)
        {
            if (RefreshItemState?.Invoke(item) == true)
            {
                Invalidate(item.Path);
                item.Thumbnail = null;
                item.HasLoadError = false;
            }
        }
        if (!_forceReload && _scope.SequenceEqual(loadOrder)) return;
        _forceReload = false;
        _scope = loadOrder;
        var keepPaths = loadOrder.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _loads.KeepOnly(keepPaths);
        ClearOutsideScope(keepPaths);
        foreach (var item in loadOrder)
        {
            if (_cache.TryGetValue(item.Path, out var thumbnail))
                SetThumbnail(item, thumbnail);
            else if (_failedPaths.Contains(item.Path))
            {
                item.IsLoading = false;
                item.HasLoadError = true;
            }
            else
            {
                // Quick previews have not entered the final cache yet. Move
                // their ownership immediately when a folder refresh replaces
                // the item object, while retaining the same pending decode.
                if (_loadedItems.TryGetValue(item.Path, out var previous) && previous.Thumbnail is { } quick)
                    SetThumbnail(item, quick);
                item.IsLoading = true;
                item.HasLoadError = false;
                _loads.Queue(item.Path, token => LoadThumbnailAsync(item.Path, token), () =>
                {
                    item.IsLoading = false;
                    if (FindCurrentItem(item.Path) is { } current) current.IsLoading = false;
                });
            }
        }
    }

    public void PruneCache(IReadOnlyList<string> folderPaths, int focusIndex)
    {
        var keepPaths = FilmstripThumbnailWindow.GetLoadOrder(folderPaths.Count, focusIndex)
            .Select(index => folderPaths[index]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ClearOutsideScope(keepPaths);
        _scope = [];
    }

    public void Invalidate(string path)
    {
        _loads.Invalidate(path);
        _forceReload = true;
        _cache.Remove(path);
        _failedPaths.Remove(path);
        if (_loadedItems.Remove(path, out var item))
        {
            item.Thumbnail = null;
            item.HasLoadError = false;
        }
    }

    private void ClearOutsideScope(HashSet<string> keepPaths)
    {
        _failedPaths.RemoveWhere(path => !keepPaths.Contains(path));
        foreach (var path in _cache.Keys.ToList())
        {
            if (!keepPaths.Contains(path))
            {
                _cache.Remove(path);
            }
        }
        foreach (var (path, item) in _loadedItems.ToArray())
        {
            if (!keepPaths.Contains(path))
            {
                item.Thumbnail = null;
                item.HasLoadError = false;
                _loadedItems.Remove(path);
            }
        }
    }

    private async Task LoadThumbnailAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var item = FindCurrentItem(path);
        if (cancellationToken.IsCancellationRequested || item is null) return;

        if (_cache.TryGetValue(path, out var cachedThumbnail))
        {
            SetThumbnail(item, cachedThumbnail);
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            item.IsLoading = true;
            item.HasLoadError = false;
            TThumbnail? quickThumbnail = null;
            if (item.Thumbnail is null)
            {
                quickThumbnail = await _quickLoad(item.Path, cancellationToken);
                if (quickThumbnail is not null && !cancellationToken.IsCancellationRequested)
                {
                    SetThumbnail(item, quickThumbnail);
                }
            }

            var thumbnail = await _refinedLoad(item.Path, cancellationToken)
                ?? quickThumbnail
                ?? FindCurrentItem(path)?.Thumbnail;
            cancellationToken.ThrowIfCancellationRequested();
            if (thumbnail is null)
            {
                if (FindCurrentItem(path) is { } current)
                {
                    current.HasLoadError = true;
                    _failedPaths.Add(path);
                }
                return;
            }

            _cache[item.Path] = thumbnail;
            SetThumbnail(item, thumbnail);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            if (!cancellationToken.IsCancellationRequested && FindCurrentItem(path) is { } current)
            {
                current.HasLoadError = current.Thumbnail is null;
                if (current.HasLoadError) _failedPaths.Add(path);
            }
        }
        finally
        {
            item.IsLoading = false;
            if (FindCurrentItem(path) is { } current) current.IsLoading = false;
        }
    }

    private TItem? FindCurrentItem(string path) =>
        _scope.FirstOrDefault(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));

    private void SetThumbnail(TItem item, TThumbnail thumbnail)
    {
        // A directory refresh may replace item objects while their shared load
        // is running. Publish to the current scope, never a detached old item.
        var current = FindCurrentItem(item.Path);
        if (current is null) return;
        item = current;
        if (_loadedItems.TryGetValue(item.Path, out var previous) && !ReferenceEquals(previous, item))
        {
            previous.Thumbnail = null;
        }
        item.Thumbnail = thumbnail;
        item.HasLoadError = false;
        _loadedItems[item.Path] = item;
    }
}

internal interface IFilmstripThumbnailItem<TThumbnail> where TThumbnail : class
{
    string Path { get; }
    TThumbnail? Thumbnail { get; set; }
    bool IsLoading { get; set; }
    bool HasLoadError { get; set; }
}
