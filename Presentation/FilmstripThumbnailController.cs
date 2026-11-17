using System.Collections.ObjectModel;
using System.Collections.Specialized;
using HdrImageViewer.Services;
using Microsoft.UI.Xaml.Media;

namespace HdrImageViewer.Presentation;

internal sealed class FilmstripThumbnailController : IDisposable
{
    private readonly ObservableCollection<FilmstripImageItem> _items;
    private readonly Dictionary<string, FilmstripImageItem> _itemsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly FilmstripThumbnailControllerCore<FilmstripImageItem, ImageSource> _core;
    private bool _disposed;

    public FilmstripThumbnailController(
        ObservableCollection<FilmstripImageItem> items,
        CancellationToken lifetimeToken)
    {
        _items = items;
        RebuildIndex();
        _items.CollectionChanged += Items_CollectionChanged;
        _core = new FilmstripThumbnailControllerCore<FilmstripImageItem, ImageSource>(
            items,
            CreateQuickAsync,
            (path, token) => PhotoThumbnailService.CreateHdrToneMappedAsync(path, 192, token),
            lifetimeToken)
        {
            RefreshItemState = RefreshCloudState,
        };
    }

    private static bool RefreshCloudState(FilmstripImageItem item)
    {
        var wasCloudPlaceholder = item.IsCloudPlaceholder;
        item.IsCloudPlaceholder = CloudFilePolicy.IsCloudPlaceholder(item.Path);
        var becameLocal = wasCloudPlaceholder && !item.IsCloudPlaceholder;
        if (becameLocal)
        {
            item.InvalidatePreviewDimensions();
        }

        return becameLocal;
    }

    public bool TryGetCached(string path, out ImageSource? thumbnail) =>
        _core.TryGetCached(path, out thumbnail);

    public void QueueLoads(int focusIndex, int firstVisible = -1, int lastVisible = -1) =>
        _core.QueueLoads(focusIndex, firstVisible, lastVisible, refreshState: firstVisible < 0);

    public void PruneCache(IReadOnlyList<string> folderPaths, int focusIndex) =>
        _core.PruneCache(folderPaths, focusIndex);

    public void Invalidate(string path)
    {
        if (_itemsByPath.TryGetValue(path, out var item))
        {
            item.InvalidatePreviewDimensions();
        }

        _core.Invalidate(path);
    }

    public void Cancel() => _core.Cancel();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _items.CollectionChanged -= Items_CollectionChanged;
        _itemsByPath.Clear();
        _core.Dispose();
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            RebuildIndex();
            return;
        }

        if (e.OldItems is not null)
        {
            foreach (FilmstripImageItem oldItem in e.OldItems)
            {
                if (_itemsByPath.TryGetValue(oldItem.Path, out var current)
                    && ReferenceEquals(current, oldItem))
                {
                    _itemsByPath.Remove(oldItem.Path);
                }
            }
        }

        if (e.NewItems is not null)
        {
            foreach (FilmstripImageItem newItem in e.NewItems)
            {
                _itemsByPath[newItem.Path] = newItem;
            }
        }
    }

    private void RebuildIndex()
    {
        _itemsByPath.Clear();
        foreach (var item in _items)
        {
            _itemsByPath[item.Path] = item;
        }
    }

    private async Task<ImageSource?> CreateQuickAsync(string path, CancellationToken cancellationToken)
    {
        var (thumbnail, width, height) = await PhotoThumbnailService.CreateQuickWithSizeAsync(path, 192, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (width > 0 && height > 0
            && _itemsByPath.TryGetValue(path, out var currentItem)
            && !currentItem.HasPreviewDimensions)
        {
            currentItem.SetPreviewDimensions((uint)width, (uint)height);
        }

        return thumbnail;
    }
}
