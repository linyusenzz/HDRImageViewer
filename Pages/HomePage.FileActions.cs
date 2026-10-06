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
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; {Localization.GetString("StatusSaveAsNoExtension")}");
            return;
        }

        var picker = new FileSavePicker(GetMainWindowId())
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(document.FileName),
            DefaultFileExtension = extension,
        };
        picker.FileTypeChoices.Add(Localization.GetString("FileTypeOriginalFormat", document.Format.DisplayName), [extension]);

        var outputFile = await PickSaveDestinationAsync(picker);
        if (outputFile is null)
        {
            return;
        }

        if (string.Equals(document.Path, outputFile.Path, StringComparison.OrdinalIgnoreCase))
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; {Localization.GetString("StatusSaveAsSameFile")}");
            return;
        }

        var progressStarted = false;
        try
        {
            progressStarted = await TryBeginExportProgressAsync(
                Localization.GetString("ExportOverlayTitle.Text"),
                Localization.GetString("ExportCopyingOriginalFile"));
            if (!progressStarted) return;
            await ExportFileTransaction.CopyAsync(document.Path, outputFile.Path, ExportToken);
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; {Localization.GetString("StatusSaveAsOriginalFormat", outputFile.Path)}");
        }
        catch (OperationCanceledException)
        {
            ViewModel.UpdateRenderStatus(Localization.GetString("StatusSaveAsCancelled"));
        }
        catch (Exception ex)
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; {Localization.GetString("StatusSaveAsFailedFormat", ex.GetType().Name, ex.Message)}");
        }
        finally
        {
            if (progressStarted) EndExportProgress();
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
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; {Localization.GetString("StatusImageCopiedFormat", document.FileName)}");
        }
        catch (Exception ex)
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; {Localization.GetString("StatusCopyImageFailedFormat", ex.GetType().Name, ex.Message)}");
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
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; {Localization.GetString("StatusPathCopiedFormat", path)}");
        }
        catch (Exception ex)
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; {Localization.GetString("StatusCopyPathFailedFormat", ex.GetType().Name, ex.Message)}");
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
            Title = Localization.GetString("DeleteDialogTitleFormat", document.FileName),
            Content = new TextBlock
            {
                Text = Localization.GetString("DeleteDialogContent.Text"),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Localization.GetString("DeleteDialogConfirm"),
            CloseButtonText = Localization.GetString("DeleteDialogCancel"),
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
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; {Localization.GetString("StatusDeleteFailedFormat", ex.GetType().Name, ex.Message)}");
            return;
        }

        var deletedFileName = document.FileName;
        var wasExplicitNavigation = _currentNavigationIsExplicit;
        _currentDocument = null;
        _folderImagePaths = remainingPaths;
        _currentFolderIndex = nextPath is null
            ? -1
            : remainingPaths.FindIndex(path => string.Equals(path, nextPath, StringComparison.OrdinalIgnoreCase));

        _folderImageIndex.InvalidatePath(document.Path);
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
                ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; {Localization.GetString("StatusMovedToRecycleBinFormat", deletedFileName)}");
                return;
            }
        }

        await ClearCurrentImageAsync(Localization.GetString("StatusMovedToRecycleBinFormat", deletedFileName));
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
        FilmstripItems.ReplaceAll([]);
        ImagePreloadCache.KeepOnly(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        ViewerSessionState.Clear();

        EndSwapChainZoomPreview();
        _presentedImageWidth = 0.0;
        _presentedImageHeight = 0.0;
        FallbackImage.Source = null;
        FallbackImage.Visibility = Visibility.Collapsed;
        HdrSwapChainHost.Visibility = Visibility.Collapsed;
        await _renderer.ClearAsync(_lifetime.Token);
        ImageSurface.Visibility = Visibility.Collapsed;
        ViewModel.ClearImage(status);
        UpdateFolderNavigationOverlay();
        ApplyInspectorLayout();
    }
}
