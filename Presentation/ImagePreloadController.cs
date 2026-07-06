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
        CancelCurrentPreload();
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

        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        lock (_gate)
        {
            _cts = source;
        }

        var hotPreloadPathSet = new HashSet<string>(hotPreloadPaths, StringComparer.OrdinalIgnoreCase);
        _ = Task.Run(() => RunPreloadLoopAsync(preloadPaths, hotPreloadPaths, hotPreloadPathSet, maxPixelSize, source));

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

    public void Cancel() => CancelCurrentPreload();

    public void Dispose()
    {
        CancelCurrentPreload();
    }

    private async Task RunPreloadLoopAsync(
        IReadOnlyList<string> preloadPaths,
        IReadOnlyList<string> hotPreloadPaths,
        IReadOnlySet<string> hotPreloadPathSet,
        int maxPixelSize,
        CancellationTokenSource source)
    {
        var token = source.Token;
        try
        {
            await Task.Delay(250, token);
            if (hotPreloadPaths.Count > 0)
            {
                foreach (var path in hotPreloadPaths)
                {
                    await PreloadOneAsync(path, maxPixelSize, token);
                }

                Trim();
            }

            foreach (var path in preloadPaths)
            {
                if (hotPreloadPathSet.Contains(path))
                {
                    continue;
                }

                token.ThrowIfCancellationRequested();
                try
                {
                    await ImagePreloadCache.GetLoadResultAsync(path, token);
                    Trim();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                }
            }
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

    private static async Task PreloadOneAsync(string path, int maxPixelSize, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // JPEG XR is decoded at full size by the renderer (its decode
            // request is null), so a capped preload would never satisfy the
            // cache's size check.
            var preloadMaxPixelSize = DecoderCatalog.IsJpegXrExtension(Path.GetExtension(path))
                ? (int?)null
                : maxPixelSize;
            await ImagePreloadCache.PreloadAsync(path, preloadMaxPixelSize, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }
    }

    private void CancelCurrentPreload()
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
