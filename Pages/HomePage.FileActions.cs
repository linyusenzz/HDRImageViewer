using System.Runtime.InteropServices;
using HdrImageViewer.Presentation;
using HdrImageViewer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace HdrImageViewer.Pages;

public sealed partial class HomePage
{
    private async void SaveAsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await SaveCurrentImageAsAsync();
    }

    private async void CopyImageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await CopyCurrentImageAsync();
    }

    private async void CopyPathMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await CopyCurrentImagePathAsync();
    }

    private void FileInfoMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ToggleCurrentFileInfo();
    }

    private async void DeleteImageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await DeleteCurrentImageAsync();
    }

    private async Task SaveCurrentImageAsAsync()
    {
        var document = _currentDocument;
        if (document is null)
        {
            return;
        }

        if (CanExportGainMapHdr(document) || document.GainMapProbe?.IsRenderableUltraHdr == true)
        {
            await ExportSingleLayerHdrSaveAsAsync();
            return;
        }

        await SaveOriginalImageCopyAsync(document);
    }

    private async Task SaveOriginalImageCopyAsync(Models.HdrImageDocument document)
    {
        var extension = Path.GetExtension(document.FileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 另存为失败: 原文件没有扩展名");
            return;
        }

        var picker = new FileSavePicker(GetMainWindowId())
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(document.FileName),
            DefaultFileExtension = extension,
        };
        picker.FileTypeChoices.Add($"{document.Format.DisplayName} 原始文件", [extension]);

        var outputFile = await PickSaveStorageFileAsync(picker);
        if (outputFile is null)
        {
            return;
        }

        if (string.Equals(document.Path, outputFile.Path, StringComparison.OrdinalIgnoreCase))
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 另存为已取消: 目标与原文件相同");
            return;
        }

        try
        {
            var sourceFile = await StorageFile.GetFileFromPathAsync(document.Path);
            CachedFileManager.DeferUpdates(outputFile);
            await sourceFile.CopyAndReplaceAsync(outputFile);
            await CachedFileManager.CompleteUpdatesAsync(outputFile);
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 已另存为原始格式: {outputFile.Path}");
        }
        catch (Exception ex)
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 另存为失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task CopyCurrentImageAsync()
    {
        var document = _currentDocument;
        if (document is null)
        {
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(document.Path);
            var dataPackage = new DataPackage
            {
                RequestedOperation = DataPackageOperation.Copy,
            };
            dataPackage.Properties.Title = document.FileName;
            dataPackage.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
            dataPackage.SetStorageItems(new IStorageItem[] { file });
            await SetClipboardContentAsync(dataPackage);
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 已复制图片，可直接粘贴到 PowerPoint: {document.FileName}");
        }
        catch (Exception ex)
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 复制图片失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task CopyCurrentImagePathAsync()
    {
        var path = _currentDocument?.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var dataPackage = new DataPackage
            {
                RequestedOperation = DataPackageOperation.Copy,
            };
            dataPackage.SetText($"\"{path}\"");
            await SetClipboardContentAsync(dataPackage);
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 已复制文件路径: {path}");
        }
        catch (Exception ex)
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 复制路径失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task SetClipboardContentAsync(DataPackage dataPackage)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetContent(dataPackage);
                Clipboard.Flush();
                return;
            }
            catch (COMException) when (attempt < 2)
            {
                await Task.Delay(50 * (attempt + 1));
            }
        }
    }

    private void ToggleCurrentFileInfo()
    {
        if (!ViewModel.HasImage)
        {
            return;
        }

        var showInspector = !_settings.ShowInspectorPanel;
        AppSettingsService.SetShowInspectorPanel(showInspector);

        if (showInspector)
        {
            InspectorScroll.ChangeView(null, 0.0, null, disableAnimation: false);
        }
    }

    private async Task DeleteCurrentImageAsync()
    {
        var document = _currentDocument;
        if (document is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = $"删除“{document.FileName}”？",
            Content = new TextBlock
            {
                Text = "此文件将被移到回收站。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            PrimaryButtonStyle = (Style)Resources["DeleteConfirmationButtonStyle"],
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || !ReferenceEquals(document, _currentDocument))
        {
            return;
        }

        var deletedIndex = _currentFolderIndex;
        var nextPath = ImageDeletionNavigation.SelectNextPath(_folderImagePaths, deletedIndex);
        var remainingPaths = new List<string>(_folderImagePaths);
        if (deletedIndex >= 0
            && deletedIndex < remainingPaths.Count
            && string.Equals(remainingPaths[deletedIndex], document.Path, StringComparison.OrdinalIgnoreCase))
        {
            remainingPaths.RemoveAt(deletedIndex);
        }
        else
        {
            remainingPaths.RemoveAll(path => string.Equals(path, document.Path, StringComparison.OrdinalIgnoreCase));
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(document.Path);
            await file.DeleteAsync(StorageDeleteOption.Default);
        }
        catch (Exception ex)
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 删除失败: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        var deletedFileName = document.FileName;
        var wasExplicitNavigation = _currentNavigationIsExplicit;
        _currentDocument = null;
        _folderImagePaths = remainingPaths;
        _currentFolderIndex = nextPath is null
            ? -1
            : remainingPaths.FindIndex(path => string.Equals(path, nextPath, StringComparison.OrdinalIgnoreCase));

        ImagePreloadCache.KeepOnly(remainingPaths.ToHashSet(StringComparer.OrdinalIgnoreCase));
        if (nextPath is not null && File.Exists(nextPath))
        {
            await LoadImagePathAsync(
                nextPath,
                invalidateRendererCache: true,
                explicitNavigationPaths: wasExplicitNavigation ? remainingPaths : null);
            if (_currentDocument is not null
                && string.Equals(_currentDocument.Path, nextPath, StringComparison.OrdinalIgnoreCase))
            {
                ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 已将 {deletedFileName} 移到回收站");
                return;
            }
        }

        await ClearCurrentImageAsync($"已将 {deletedFileName} 移到回收站");
    }

    private async Task ClearCurrentImageAsync(string status)
    {
        _imageLoads.CancelCurrent();
        _zoomRenderCts?.Cancel();
        _actualSizeCts?.Cancel();
        CancelAndDispose(ref _folderRefreshCts);
        StopCompanionMediaPlayback(resetSource: true);
        _filmstripThumbnails.Cancel();
        if (_isCropModeEnabled)
        {
            SetCropMode(false);
        }

        _currentDocument = null;
        _folderImagePaths = [];
        _currentFolderIndex = -1;
        _currentFilmstripItem = null;
        _currentNavigationIsExplicit = false;
        _lastFolderListDirectory = null;
        _lastFolderListRefreshTicks = 0;
        FilmstripItems.ReplaceAll([]);
        ImagePreloadCache.KeepOnly(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        ViewerSessionState.Clear();

        HideFallbackImageLayer();
        await _renderer.ClearAsync(_lifetime.Token);
        ImageSurface.Visibility = Visibility.Collapsed;
        ViewModel.ClearImage(status);
        UpdateFolderNavigationOverlay();
        ApplyInspectorLayout();
    }
}
