using HdrImageViewer.Services;
using Microsoft.UI.Xaml;

namespace HdrImageViewer.Pages;

// Folder navigation: previous/next stepping, building and refreshing the
// sibling-image list for the current folder, explicit navigation lists from
// multi-file activation, and viewer-session restore. Split from
// HomePage.xaml.cs for readability; shared fields stay in the main partial.
public sealed partial class HomePage
{
    private const long FolderListReuseWindowMilliseconds = 2500;

    private async void PreviousImage_Click(object sender, RoutedEventArgs e)
    {
        await NavigateFolderImageAsync(-1);
    }

    private async void NextImage_Click(object sender, RoutedEventArgs e)
    {
        await NavigateFolderImageAsync(1);
    }

    private async Task NavigateFolderImageAsync(int direction)
    {
        if (_isFolderNavigationLoading || _folderImagePaths.Count == 0)
        {
            return;
        }

        var nextIndex = _currentFolderIndex + direction;
        if (nextIndex < 0 || nextIndex >= _folderImagePaths.Count)
        {
            return;
        }

        _isFolderNavigationLoading = true;
        try
        {
            await LoadImagePathAsync(_folderImagePaths[nextIndex], invalidateRendererCache: false);
        }
        finally
        {
            _isFolderNavigationLoading = false;
        }
    }

    private void SelectCurrentFolderImage(string currentPath)
    {
        var existingIndex = _folderImagePaths.FindIndex(file => string.Equals(file, currentPath, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            _currentFolderIndex = existingIndex;
            RefreshFilmstripItems();
            UpdateFolderNavigationOverlay();
            return;
        }

        _folderImagePaths = [currentPath];
        _currentFolderIndex = 0;
        RefreshFilmstripItems();
        UpdateFolderNavigationOverlay();
    }

    private void QueueFolderImageListRefresh(string currentPath)
    {
        CancelAndDispose(ref _folderRefreshCts);

        // While stepping through a folder, don't re-enumerate and re-sort the
        // whole directory on every image: with tens of thousands of siblings
        // that wastes CPU/disk on each navigation. Reuse the current list for a
        // short window as long as it still contains the image being shown.
        var directory = Path.GetDirectoryName(currentPath);
        if (directory is not null
            && string.Equals(directory, _lastFolderListDirectory, StringComparison.OrdinalIgnoreCase)
            && Environment.TickCount64 - _lastFolderListRefreshTicks < FolderListReuseWindowMilliseconds
            && _currentFolderIndex >= 0
            && _currentFolderIndex < _folderImagePaths.Count
            && string.Equals(_folderImagePaths[_currentFolderIndex], currentPath, StringComparison.OrdinalIgnoreCase))
        {
            QueueAdjacentPreloads();
            return;
        }

        _folderRefreshCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _folderRefreshCts.Token;
        _ = RefreshFolderImageListAsync(currentPath, token);
    }

    private async Task RefreshFolderImageListAsync(string currentPath, CancellationToken cancellationToken)
    {
        try
        {
            var result = await Task.Run(() => BuildFolderImageList(currentPath, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_currentNavigationIsExplicit
                || _currentDocument is null
                || !string.Equals(_currentDocument.Path, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _folderImagePaths = result.Paths;
            _currentFolderIndex = result.CurrentIndex;
            _lastFolderListDirectory = Path.GetDirectoryName(currentPath);
            _lastFolderListRefreshTicks = Environment.TickCount64;
            RefreshFilmstripItems();
            UpdateFolderNavigationOverlay();
            ViewerSessionState.SaveImage(currentPath, _folderImagePaths, _currentNavigationIsExplicit);
            QueueAdjacentPreloads();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static (List<string> Paths, int CurrentIndex) BuildFolderImageList(string currentPath, CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(currentPath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return ([currentPath], 0);
            }

            var paths = Directory
                .EnumerateFiles(directory)
                .Where(IsSupportedImagePath)
                .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            cancellationToken.ThrowIfCancellationRequested();

            var currentIndex = paths.FindIndex(file => string.Equals(file, currentPath, StringComparison.OrdinalIgnoreCase));
            if (currentIndex < 0)
            {
                paths.Add(currentPath);
                paths = paths
                    .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                currentIndex = paths.FindIndex(file => string.Equals(file, currentPath, StringComparison.OrdinalIgnoreCase));
            }

            return (paths, Math.Max(currentIndex, 0));
        }
        catch
        {
            return ([currentPath], 0);
        }
    }

    private async Task RestoreViewerSessionAsync()
    {
        if (!ViewerSessionState.TryGetLastImage(
            out var path,
            out var navigationPaths,
            out var hasExplicitNavigationPaths))
        {
            return;
        }

        try
        {
            await LoadImagePathAsync(
                path,
                invalidateRendererCache: false,
                explicitNavigationPaths: hasExplicitNavigationPaths ? navigationPaths : null);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SetExplicitImageList(IReadOnlyList<string> paths, string currentPath)
    {
        _folderImagePaths = paths
            .Where(IsSupportedImagePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _currentFolderIndex = _folderImagePaths.FindIndex(
            file => string.Equals(file, currentPath, StringComparison.OrdinalIgnoreCase));
        if (_currentFolderIndex < 0)
        {
            _folderImagePaths.Insert(0, currentPath);
            _currentFolderIndex = 0;
        }

        RefreshFilmstripItems();
        UpdateFolderNavigationOverlay();
    }
}
