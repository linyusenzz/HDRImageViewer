using HdrImageViewer.Services;

namespace HdrImageViewer.Presentation;

/// <summary>
/// Drives background decoding of the images adjacent to the current one and
/// keeps <see cref="ImagePreloadCache"/> trimmed to a window around the focus.
/// Extracted from HomePage; the page passes the current folder paths / focus
/// index / settings in rather than carrying the cache-scope state and the
/// background preload loop itself.
/// </summary>
internal sealed class ImagePreloadController
{
    private readonly CancellationToken _lifetimeToken;
    private readonly object _gate = new();
    private HashSet<string> _keepPaths = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _decodedPriorityPaths = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;

    public ImagePreloadController(CancellationToken lifetimeToken)
    {
        _lifetimeToken = lifetimeToken;
    }

    public void QueueAdjacent(
        IReadOnlyList<string> folderPaths,
        int focusIndex,
        bool preloadEnabled,
        int preloadRadius,
        int maxPixelSize)
    {
        CancelCts();
        if (!preloadEnabled || folderPaths.Count == 0 || focusIndex < 0)
        {
            SetScope(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            Trim();
            return;
        }

        var keepPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            folderPaths[focusIndex],
        };
        var priorityPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var preloadPaths = new List<string>();
        var hotPreloadPaths = new List<string>();
        for (var offset = 1; offset <= preloadRadius; offset++)
        {
            var isHot = offset == 1;
            AddPreloadPath(focusIndex + offset, isHot);
            AddPreloadPath(focusIndex - offset, isHot);
        }

        SetScope(keepPaths, priorityPaths);
        Trim();

        if (preloadPaths.Count == 0)
        {
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            await Task.Delay(250, token);
            if (hotPreloadPaths.Count > 0)
            {
                try
                {
                    foreach (var path in hotPreloadPaths)
                    {
                        await PreloadOneAsync(path);
                    }

                    Trim();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            foreach (var path in preloadPaths)
            {
                if (hotPreloadPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    await ImagePreloadCache.GetLoadResultAsync(path, token);
                    Trim();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                }
            }
        }, token);

        async Task PreloadOneAsync(string path)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await ImagePreloadCache.PreloadAsync(path, maxPixelSize, token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }
        }

        void AddPreloadPath(int index, bool isHot)
        {
            if (index < 0 || index >= folderPaths.Count)
            {
                return;
            }

            var path = folderPaths[index];
            if (string.Equals(Path.GetExtension(path), ".exr", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!keepPaths.Add(path))
            {
                return;
            }

            if (isHot)
            {
                priorityPaths.Add(path);
                hotPreloadPaths.Add(path);
            }

            preloadPaths.Add(path);
        }
    }

    public void Cancel() => CancelCts();

    public void Dispose()
    {
        CancelCts();
    }

    private void CancelCts()
    {
        var source = _cts;
        _cts = null;
        if (source is not null)
        {
            source.Cancel();
            source.Dispose();
        }
    }

    private void SetScope(HashSet<string> keepPaths, HashSet<string> decodedPriorityPaths)
    {
        lock (_gate)
        {
            _keepPaths = new HashSet<string>(keepPaths, StringComparer.OrdinalIgnoreCase);
            _decodedPriorityPaths = new HashSet<string>(decodedPriorityPaths, StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Trim()
    {
        HashSet<string> keepPaths;
        HashSet<string> decodedPriorityPaths;
        lock (_gate)
        {
            keepPaths = new HashSet<string>(_keepPaths, StringComparer.OrdinalIgnoreCase);
            decodedPriorityPaths = new HashSet<string>(_decodedPriorityPaths, StringComparer.OrdinalIgnoreCase);
        }

        ImagePreloadCache.KeepOnly(keepPaths, decodedPriorityPaths);
    }
}
