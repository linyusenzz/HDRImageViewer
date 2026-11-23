using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using HdrImageViewer.Models;
using HdrImageViewer.Presentation;
using HdrImageViewer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace HdrImageViewer.Pages;

public sealed partial class HomePage
{
    private InMemoryRandomAccessStream? _clipboardBitmapStream;

    private async void SaveAsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveCurrentImageAsAsync();
        }
        catch (Exception ex)
        {
            HandleUnexpectedUiException(ex);
        }
    }

    private async void CopyImageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await CopyCurrentImageAsync();
        }
        catch (Exception ex)
        {
            HandleUnexpectedUiException(ex);
        }
    }

    private async void CopyPathMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await CopyCurrentImagePathAsync();
        }
        catch (Exception ex)
        {
            HandleUnexpectedUiException(ex);
        }
    }

    private void ShowInExplorerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ShowCurrentImageInExplorer();
    }

    private void FileInfoMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ToggleCurrentFileInfo();
    }

    private async void DeleteImageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await DeleteCurrentImageAsync();
        }
        catch (Exception ex)
        {
            HandleUnexpectedUiException(ex);
        }
    }

    private void ShowCurrentImageInExplorer()
    {
        var path = _currentDocument?.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
            {
                Arguments = $"/select,\"{fullPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; {ex.GetType().Name}: {ex.Message}");
        }
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

    private async Task SaveOriginalImageCopyAsync(HdrImageDocument document)
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
            if (!progressStarted)
            {
                return;
            }

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
            if (progressStarted)
            {
                EndExportProgress();
            }
        }
    }

    private async Task CopyCurrentImageAsync()
    {
        var document = _currentDocument;
        if (document is null)
        {
            return;
        }

        InMemoryRandomAccessStream? pngStream = null;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(document.Path);
            pngStream = await CreateClipboardSrgbPngStreamAsync(document, _lifetimeToken);
            var dataPackage = new DataPackage
            {
                RequestedOperation = DataPackageOperation.Copy,
            };
            dataPackage.Properties.Title = document.FileName;
            if (pngStream is not null)
            {
                dataPackage.SetBitmap(RandomAccessStreamReference.CreateFromStream(pngStream));
            }
            else
            {
                dataPackage.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
            }

            dataPackage.SetStorageItems(new IStorageItem[] { file });
            await SetClipboardContentAsync(dataPackage);

            if (pngStream is not null)
            {
                var previousStream = _clipboardBitmapStream;
                _clipboardBitmapStream = pngStream;
                pngStream = null;
                previousStream?.Dispose();
            }

            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; {Localization.GetString("StatusImageCopiedFormat", document.FileName)}");
        }
        catch (Exception ex)
        {
            pngStream?.Dispose();
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; {Localization.GetString("StatusCopyImageFailedFormat", ex.GetType().Name, ex.Message)}");
        }
    }

    private async Task<InMemoryRandomAccessStream?> CreateClipboardSrgbPngStreamAsync(
        HdrImageDocument document,
        CancellationToken cancellationToken)
    {
        var preferToneMapped = IsHdrCropExportPreview(document)
            || IsNonStandardWicExtension(Path.GetExtension(document.Path));

        if (preferToneMapped)
        {
            var toneMapped = await TryEncodeToneMappedBitmapToPngAsync(document, cancellationToken);
            if (toneMapped is not null)
            {
                return toneMapped;
            }
        }

        var wicDecoded = await TryEncodeWicSrgbToPngAsync(document.Path, cancellationToken);
        if (wicDecoded is not null)
        {
            return wicDecoded;
        }

        if (!preferToneMapped)
        {
            var fallbackToneMapped = await TryEncodeToneMappedBitmapToPngAsync(document, cancellationToken);
            if (fallbackToneMapped is not null)
            {
                return fallbackToneMapped;
            }
        }

        return await TryEncodeRenderedPreviewToPngAsync(cancellationToken);
    }

    private static bool IsNonStandardWicExtension(string? extension)
    {
        return extension is not null
            && (extension.Equals(".exr", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".hdr", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jxl", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".avif", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".heic", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".heif", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".hif", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<InMemoryRandomAccessStream?> TryEncodeToneMappedBitmapToPngAsync(
        HdrImageDocument document,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] pixels;
            int width;
            int height;
            BitmapAlphaMode alphaMode;

            if (document.HasRenderableGainMap)
            {
                var inputs = await GainMapRenderInputDecoder.DecodeRenderInputsAsync(
                    document,
                    maxPixelSize: null,
                    cancellationToken);
                var orientation = new ExifOrientationTransform(
                    inputs.Primary.PixelWidth,
                    inputs.Primary.PixelHeight,
                    (int)inputs.Constants.Orientation.X);
                width = orientation.Width;
                height = orientation.Height;
                alphaMode = BitmapAlphaMode.Ignore;
                pixels = await Task.Run(
                    () => ThumbnailPixelConverter.ConvertGainMapToBgra8(inputs, cancellationToken),
                    cancellationToken);
            }
            else
            {
                var bitmap = await BitmapDecodeService.DecodeDocumentAsync(
                    document,
                    maxPixelSize: null,
                    cancellationToken);
                width = bitmap.PixelWidth;
                height = bitmap.PixelHeight;
                alphaMode = BitmapAlphaMode.Premultiplied;
                pixels = await Task.Run(
                    () => bitmap.IsHdrEncoded
                        ? ThumbnailPixelConverter.ConvertHdrToBgra8(bitmap, cancellationToken, premultiplyAlpha: true)
                        : ThumbnailPixelConverter.ConvertSdrToBgra8(bitmap, cancellationToken, premultiplyAlpha: true),
                    cancellationToken);
            }

            if (width <= 0 || height <= 0)
            {
                return null;
            }

            return await EncodeBgra8PngStreamAsync(
                pixels,
                (uint)width,
                (uint)height,
                96.0,
                96.0,
                alphaMode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<InMemoryRandomAccessStream?> TryEncodeWicSrgbToPngAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var (pixels, width, height, dpiX, dpiY) = await Task.Run(async () =>
            {
                await using var inputStream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    64 * 1024,
                    useAsync: true);
                using var source = inputStream.AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(source);
                cancellationToken.ThrowIfCancellationRequested();
                var pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    new BitmapTransform(),
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb);
                cancellationToken.ThrowIfCancellationRequested();
                return (
                    pixelData.DetachPixelData(),
                    decoder.OrientedPixelWidth,
                    decoder.OrientedPixelHeight,
                    decoder.DpiX > 0 ? decoder.DpiX : 96.0,
                    decoder.DpiY > 0 ? decoder.DpiY : 96.0);
            }, cancellationToken);

            return await EncodeBgra8PngStreamAsync(
                pixels,
                width,
                height,
                dpiX,
                dpiY,
                BitmapAlphaMode.Premultiplied);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<InMemoryRandomAccessStream?> TryEncodeRenderedPreviewToPngAsync(
        CancellationToken cancellationToken)
    {
        if (HdrSwapChainHost.Visibility != Visibility.Visible)
        {
            return null;
        }

        try
        {
            using var frame = await _renderer.CaptureRenderedFrameAsync(cancellationToken);
            if (frame is null
                || frame.Width <= 0
                || frame.Height <= 0
                || frame.Layout.X <= 0.0f
                || frame.Layout.Y <= 0.0f)
            {
                return null;
            }

            var width = frame.Width;
            var height = frame.Height;
            var layout = frame.Layout;
            var sdrScale = Math.Max(frame.SdrWhiteNits / 80.0f, 1.0f);
            var strips = frame.Strips;
            var pixels = await Task.Run(() =>
            {
                var buffer = new byte[checked(width * height * 4)];
                var invWidth = 1.0f / width;
                var invLayoutX = 1.0f / layout.X;
                var options = new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8),
                };

                Parallel.For(0, strips.Count, options, stripIndex =>
                {
                    var strip = strips[stripIndex];
                    var stripBytes = strip.Pixels;
                    for (var localY = 0; localY < strip.RowCount; localY++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var y = strip.StartY + localY;
                        var v = (((y + 0.5f) / height) - layout.W) / layout.Y;
                        if (v < 0.0f || v > 1.0f)
                        {
                            continue;
                        }

                        var srcRowOffset = localY * width * 8;
                        var dstRowOffset = y * width * 4;
                        for (var x = 0; x < width; x++)
                        {
                            var u = (((x + 0.5f) * invWidth) - layout.Z) * invLayoutX;
                            if (u < 0.0f || u > 1.0f)
                            {
                                continue;
                            }

                            var srcOffset = srcRowOffset + (x * 8);
                            var rLinear = ReadHalfLittleEndian(stripBytes, srcOffset);
                            var gLinear = ReadHalfLittleEndian(stripBytes, srcOffset + 2);
                            var bLinear = ReadHalfLittleEndian(stripBytes, srcOffset + 4);
                            if (!float.IsFinite(rLinear) || !float.IsFinite(gLinear) || !float.IsFinite(bLinear))
                            {
                                continue;
                            }

                            var dstOffset = dstRowOffset + (x * 4);
                            buffer[dstOffset] = LinearToSrgbByte(ToneMapChannel(bLinear / sdrScale));
                            buffer[dstOffset + 1] = LinearToSrgbByte(ToneMapChannel(gLinear / sdrScale));
                            buffer[dstOffset + 2] = LinearToSrgbByte(ToneMapChannel(rLinear / sdrScale));
                            buffer[dstOffset + 3] = 255;
                        }
                    }
                });

                return buffer;
            }, cancellationToken);

            return await EncodeBgra8PngStreamAsync(
                pixels,
                (uint)width,
                (uint)height,
                96.0,
                96.0,
                BitmapAlphaMode.Premultiplied);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static float ReadHalfLittleEndian(byte[] pixels, int offset) =>
        (float)BitConverter.UInt16BitsToHalf((ushort)(pixels[offset] | (pixels[offset + 1] << 8)));

    private static float ToneMapChannel(float linear)
    {
        if (!float.IsFinite(linear) || linear <= 0.0f)
        {
            return 0.0f;
        }

        return linear <= 0.8f
            ? linear
            : 0.8f + (0.2f * ((linear - 0.8f) / (1.0f + linear - 0.8f)));
    }

    private static byte LinearToSrgbByte(float linear)
    {
        var clamped = Math.Clamp(linear, 0.0f, 1.0f);
        var encoded = clamped <= 0.0031308f
            ? 12.92f * clamped
            : (1.055f * MathF.Pow(clamped, 1.0f / 2.4f)) - 0.055f;
        return (byte)Math.Clamp((int)MathF.Round(encoded * 255.0f), 0, 255);
    }

    private static async Task<InMemoryRandomAccessStream> EncodeBgra8PngStreamAsync(
        byte[] bgraPixels,
        uint width,
        uint height,
        double dpiX,
        double dpiY,
        BitmapAlphaMode alphaMode)
    {
        var pngStream = new InMemoryRandomAccessStream();
        try
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, pngStream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                alphaMode,
                width,
                height,
                dpiX,
                dpiY,
                bgraPixels);
            await encoder.FlushAsync();
            pngStream.Seek(0);
            return pngStream;
        }
        catch
        {
            pngStream.Dispose();
            throw;
        }
    }

    private void DisposeClipboardStream()
    {
        if (_clipboardBitmapStream is { } stream)
        {
            _clipboardBitmapStream = null;
            stream.Dispose();
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
            _ = InspectorScroll.ChangeView(null, 0.0, null, disableAnimation: false);
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
            _ = remainingPaths.RemoveAll(path => string.Equals(path, document.Path, StringComparison.OrdinalIgnoreCase));
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
        _currentDocumentFileLength = null;
        _folderImagePaths = remainingPaths;
        _currentFolderIndex = nextPath is null
            ? -1
            : remainingPaths.FindIndex(path => string.Equals(path, nextPath, StringComparison.OrdinalIgnoreCase));

        _folderImageIndex.InvalidatePath(document.Path);
        ImagePreloadCache.KeepOnly(remainingPaths.ToHashSet(StringComparer.OrdinalIgnoreCase));
        if (nextPath is not null && await Task.Run(() => File.Exists(nextPath), _lifetimeToken))
        {
            _ = await LoadImagePathAsync(
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
        _analysisAutoRefreshTimer?.Stop();
        CancelAndDispose(ref _folderRefreshCts);
        StopCompanionMediaPlayback(resetSource: true);
        _filmstripThumbnails.Cancel();
        if (_isCropModeEnabled)
        {
            SetCropMode(false);
        }

        _currentDocument = null;
        _currentDocumentFileLength = null;
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
        UpdateLuminanceHeatmapControls();
        PositionComparisonDivider();
        await _renderer.ClearAsync(_lifetimeToken);
        ImageSurface.Visibility = Visibility.Collapsed;
        ViewModel.ClearImage(status);
        UpdateFolderNavigationOverlay();
        ApplyInspectorLayout();
    }
}
