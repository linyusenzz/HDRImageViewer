using System.Collections.ObjectModel;
using HdrImageViewer.Services;
using Microsoft.UI.Xaml.Media;

namespace HdrImageViewer.Presentation;

internal sealed class FilmstripThumbnailController(
    ObservableCollection<FilmstripImageItem> items, CancellationToken lifetimeToken) : IDisposable
{
    private readonly FilmstripThumbnailControllerCore<FilmstripImageItem, ImageSource> _core = new(
        items,
        (path, token) => CreateQuickAsync(items, path, token),
        (path, token) => PhotoThumbnailService.CreateHdrToneMappedAsync(path, 192, token), lifetimeToken)
    {
        RefreshItemState = RefreshCloudState,
    };

    private static bool RefreshCloudState(FilmstripImageItem item)
    {
        var wasCloudPlaceholder = item.IsCloudPlaceholder;
        item.IsCloudPlaceholder = CloudFilePolicy.IsCloudPlaceholder(item.Path);
        var becameLocal = wasCloudPlaceholder && !item.IsCloudPlaceholder;
        if (becameLocal) item.InvalidatePreviewDimensions();
        return becameLocal;
    }

    public bool TryGetCached(string path, out ImageSource? thumbnail) => _core.TryGetCached(path, out thumbnail);
    public void QueueLoads(int focusIndex, int firstVisible = -1, int lastVisible = -1) =>
        _core.QueueLoads(focusIndex, firstVisible, lastVisible, refreshState: firstVisible < 0);
    public void PruneCache(IReadOnlyList<string> folderPaths, int focusIndex) => _core.PruneCache(folderPaths, focusIndex);
    public void Invalidate(string path)
    {
        items.FirstOrDefault(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))?.InvalidatePreviewDimensions();
        _core.Invalidate(path);
    }
    public void Cancel() => _core.Cancel();
    public void Dispose() => _core.Dispose();

    private static async Task<ImageSource?> CreateQuickAsync(ObservableCollection<FilmstripImageItem> items,
        string path, CancellationToken cancellationToken)
    {
        var item = items.FirstOrDefault(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
        var size = item?.HasPreviewDimensions == true ? null
            : await PhotoThumbnailService.TryGetOrientedSizeAsync(path, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (size is { } dimensions)
            items.FirstOrDefault(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))?
                .SetPreviewDimensions(dimensions.Width, dimensions.Height);
        return await PhotoThumbnailService.CreateQuickAsync(path, 192, cancellationToken);
    }
}
