using HdrImageViewer.Services;
using HdrImageViewer.Presentation;
using Microsoft.UI.Xaml;

namespace HdrImageViewer.Pages;

// Folder navigation: previous/next stepping, building and refreshing the
// sibling-image list for the current folder, explicit navigation lists from
// multi-file activation, and viewer-session restore. Split from
// HomePage.xaml.cs for readability; shared fields stay in the main partial.
public sealed partial class HomePage
{
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
            var failed = await ImageNavigationContext.NavigateAsync(_folderImagePaths.ToArray(), _currentFolderIndex, direction,
                path => LoadImagePathAsync(path, invalidateRendererCache: false, preserveNavigationList: true), _lifetime.Token);
            if (failed.Count > 0)
            {
                var fileList = string.Join(", ", failed.Select(Path.GetFileName));
                ViewModel.UpdateRenderStatus($"{ViewModel.RenderStatus}; {Localization.GetString("StatusSkippedUnopenableFilesFormat", fileList)}");
            }
        }
        catch (OperationCanceledException)
        {
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
            RefreshFilmstripItems();
            UpdateFolderNavigationOverlay();
            ViewerSessionState.SaveImage(currentPath, _folderImagePaths, _currentNavigationIsExplicit);
            QueueAdjacentPreloads();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private (List<string> Paths, int CurrentIndex) BuildFolderImageList(string currentPath, CancellationToken cancellationToken)
    {
        try
        {
            return _folderImageIndex.GetFolderImages(currentPath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
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
