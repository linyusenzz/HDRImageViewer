using HdrImageViewer.Rendering;
using HdrImageViewer.Models;
using HdrImageViewer.Presentation;
using HdrImageViewer.Services;
using HdrImageViewer.ViewModels;
using Microsoft.Graphics.Display;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SharpGen.Runtime;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.DXGI;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace HdrImageViewer.Pages;

public sealed partial class HomePage : Page
{
    private const uint MonitorDefaultToNearest = 2;
    private const int AdjacentPreloadRadius = 5;
    private const int ZoomRenderDebounceMilliseconds = 60;
    private const int ZoomAnimationFrameMilliseconds = 16;
    private const double ZoomAnimationCatchUp = 0.58;
    private const double MinCropWidth = 96.0;
    private const double MinCropHeight = 72.0;

    private readonly D3D11HdrRenderPipeline _renderer = new();
    private readonly CancellationTokenSource _lifetime = new();
    private DisplayInformation? _displayInformation;
    private HdrImageDocument? _currentDocument;
    private List<string> _folderImagePaths = [];
    private int _currentFolderIndex = -1;
    private bool _isFolderNavigationLoading;
    private double _zoomScale = 1.0;
    private double _targetZoomScale = 1.0;
    private double _committedZoomScale = 1.0;
    private bool _hasPendingZoomAnchor;
    private double _pendingZoomAnchorX = 0.5;
    private double _pendingZoomAnchorY = 0.5;
    private double _pendingZoomViewportX = 0.5;
    private double _pendingZoomViewportY = 0.5;
    private bool _isFitZoom = true;
    private bool _isFillZoom;
    private double _wheelNavigateAccumulator;
    private bool _isCropModeEnabled;
    private bool _isDraggingCropFrame;
    private bool _isPanning;
    private bool _isZoomCommitInProgress;
    private bool _suppressSwapChainSizeChangedForZoom;
    private bool _hasRestoredViewerSession;
    private bool _currentNavigationIsExplicit;
    private Windows.Foundation.Point _panStartPointerPosition;
    private double _panStartScrollOffsetX;
    private double _panStartScrollOffsetY;
    private uint _panPointerId;
    private Windows.Foundation.Point _cropDragStartPointerPosition;
    private Thickness _cropDragStartMargin;
    private uint _cropDragPointerId;
    private CancellationTokenSource? _preloadCts;
    private CancellationTokenSource? _thumbnailCts;
    private CancellationTokenSource? _zoomRenderCts;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _zoomAnimationTimer;
    private bool _hasZoomAnimationAnchor;
    private double _zoomAnimationAnchorX = 0.5;
    private double _zoomAnimationAnchorY = 0.5;
    private double _zoomAnimationViewportX = 0.5;
    private double _zoomAnimationViewportY = 0.5;
    private readonly object _preloadCacheGate = new();
    private HashSet<string> _preloadCacheKeepPaths = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _preloadCacheDecodedPriorityPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageSource> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private AppUserSettings _settings = AppSettingsService.Current;
    private bool _updatingHdrModeControls;
    private bool _isImmersiveEventAttached;
    private bool _isDisplayInformationEventAttached;

    public ImageWorkspaceViewModel ViewModel { get; } = new();

    public ObservableCollection<FilmstripImageItem> FilmstripItems { get; } = [];

    private enum CropExportMode
    {
        SdrPreview,
        GainMapPreserve,
        UltraHdrConvert,
        SingleLayerHdr,
    }

    public HomePage()
    {
        InitializeComponent();
        _zoomAnimationTimer = DispatcherQueue.CreateTimer();
        _zoomAnimationTimer.Interval = TimeSpan.FromMilliseconds(ZoomAnimationFrameMilliseconds);
        _zoomAnimationTimer.Tick += ZoomAnimationTimer_Tick;
        UpdateFolderNavigationOverlay();
        UpdateZoomControls();
        AppSettingsService.SettingsChanged += AppSettingsService_SettingsChanged;
        Loaded += HomePage_Loaded;
        Unloaded += HomePage_Unloaded;
    }

    private async void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            InitializeDisplayInformation();
            RefreshRendererDisplayConfiguration();
            if (App.MainWindow is MainWindow mainWindow)
            {
                if (!_isImmersiveEventAttached)
                {
                    mainWindow.ImmersiveViewingChanged += MainWindow_ImmersiveViewingChanged;
                    _isImmersiveEventAttached = true;
                }

                ApplyImmersiveViewingState(mainWindow.IsImmersiveViewing);
            }

            _renderer.Attach(HdrSwapChainHost);
            await ResizeRendererAsync();
            _lifetime.Token.ThrowIfCancellationRequested();
            if (!_hasRestoredViewerSession && !ViewModel.HasImage)
            {
                _hasRestoredViewerSession = true;
                await RestoreViewerSessionAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            ViewModel.UpdateRenderStatus($"主页加载失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void HomePage_Unloaded(object sender, RoutedEventArgs e)
    {
        _lifetime.Cancel();
        AppSettingsService.SettingsChanged -= AppSettingsService_SettingsChanged;
        _zoomAnimationTimer?.Stop();
        if (_zoomAnimationTimer is not null)
        {
            _zoomAnimationTimer.Tick -= ZoomAnimationTimer_Tick;
            _zoomAnimationTimer = null;
        }

        if (_displayInformation is not null && _isDisplayInformationEventAttached)
        {
            _displayInformation.AdvancedColorInfoChanged -= DisplayInformation_AdvancedColorInfoChanged;
            _isDisplayInformationEventAttached = false;
        }

        _displayInformation?.Dispose();
        _displayInformation = null;

        CancelAndDispose(ref _preloadCts);
        CancelAndDispose(ref _thumbnailCts);
        CancelAndDispose(ref _zoomRenderCts);
        SetPreloadCacheScope(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        TrimImagePreloadCache();
        if (App.MainWindow is MainWindow mainWindow)
        {
            mainWindow.ImmersiveViewingChanged -= MainWindow_ImmersiveViewingChanged;
            _isImmersiveEventAttached = false;
        }

        _renderer.Dispose();
        _lifetime.Dispose();
    }

    private void MainWindow_ImmersiveViewingChanged(object? sender, bool isImmersive)
    {
        ApplyImmersiveViewingState(isImmersive);
    }

    private void ApplyImmersiveViewingState(bool isImmersive)
    {
        InspectorPanel.Visibility = isImmersive ? Visibility.Collapsed : Visibility.Visible;
        InspectorColumn.Width = isImmersive ? new GridLength(0) : new GridLength(360);
        FullScreenIcon.Glyph = isImmersive ? "\uE73F" : "\uE740";
        TopFullScreenIcon.Glyph = isImmersive ? "\uE73F" : "\uE740";
        ToolTipService.SetToolTip(FullScreenButton, isImmersive ? "退出全屏" : "全屏");
        ToolTipService.SetToolTip(TopFullScreenButton, isImmersive ? "退出全屏" : "全屏");
        UpdateFilmstripChromeLayout();
        _ = ApplyViewportResizeAsync();
    }

    private async Task ApplyViewportResizeAsync()
    {
        await Task.Yield();
        UpdateImageSurfaceLayout();
        CenterScrollableImage();
        RefreshRendererDisplayConfiguration();
        await ResizeRendererAsync();
        ViewModel.UpdateRenderStatus(_renderer.LastRenderStatus);
    }

    private async void OpenImage_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.Thumbnail,
        };

        foreach (var fileType in DecoderCatalog.FileTypeFilter)
        {
            picker.FileTypeFilter.Add(fileType);
        }

        if (App.MainWindow is not null)
        {
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        }

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        await LoadImagePathAsync(file.Path, invalidateRendererCache: true);
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "打开图片";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items
                .OfType<StorageFile>()
                .Select(file => file.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path) && IsSupportedImagePath(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (paths.Count == 0)
            {
                ViewModel.UpdateRenderStatus("拖放未打开: 没有识别到支持的图片文件。");
                return;
            }

            await LoadDroppedImagePathsAsync(paths);
        }
        catch (Exception ex)
        {
            ViewModel.UpdateRenderStatus($"拖放打开失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task LoadDroppedImagePathsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        if (paths.Count == 1)
        {
            await LoadImagePathAsync(paths[0], invalidateRendererCache: true);
            return;
        }

        await LoadImagePathAsync(paths[0], invalidateRendererCache: true, explicitNavigationPaths: paths);
        ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 已拖入 {paths.Count} 张图片，按拖入顺序浏览");
    }

    private async void ReloadImage_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ViewModel.FilePath))
        {
            return;
        }

        await LoadImagePathAsync(ViewModel.FilePath, invalidateRendererCache: true);
    }

    private async Task LoadImagePathAsync(
        string path,
        bool invalidateRendererCache,
        IReadOnlyList<string>? explicitNavigationPaths = null)
    {
        _zoomRenderCts?.Cancel();
        ImageSurface.Visibility = Visibility.Visible;
        FallbackImage.Source = null;
        FallbackImage.Visibility = Visibility.Collapsed;

        var renderStatus = string.Empty;
        try
        {
            var openTimer = Stopwatch.StartNew();
            RefreshRendererDisplayConfiguration();
            var probeTimer = Stopwatch.StartNew();
            var document = await ViewModel.LoadFileAsync(path, _lifetime.Token);
            _currentDocument = document;
            UpdateHdrModeControlsForDocument(document);
            if (explicitNavigationPaths is not null)
            {
                _currentNavigationIsExplicit = true;
                SetExplicitImageList(explicitNavigationPaths, path);
            }
            else
            {
                _currentNavigationIsExplicit = false;
                RefreshFolderImageList(path);
            }
            ResetZoomToFit();
            ResetInteractionScaleTransform();
            probeTimer.Stop();
            var preferNativeHdrSurface = document.GainMapProbe?.IsRenderableUltraHdr == true
                || document.HeifAvifProbe?.HasHdrTransfer == true;
            if (preferNativeHdrSurface)
            {
                FallbackImage.Source = null;
                FallbackImage.Visibility = Visibility.Collapsed;
            }
            else
            {
                FallbackImage.Source = new BitmapImage(new Uri(path));
                FallbackImage.Visibility = Visibility.Visible;
            }

            var resizeTimer = Stopwatch.StartNew();
            UpdateImageSurfaceLayout();
            await ResizeRendererAsync();
            resizeTimer.Stop();

            var renderTimer = Stopwatch.StartNew();
            if (invalidateRendererCache)
            {
                _renderer.InvalidateImageCache();
            }

            await _renderer.LoadAsync(document, _lifetime.Token);
            if (UpdateImageSurfaceLayout())
            {
                await ResizeRendererAsync();
                await _renderer.LoadAsync(document, _lifetime.Token);
            }

            renderTimer.Stop();
            renderStatus = _renderer.LastRenderStatus;
            var gainMapPresented = document.GainMapProbe?.IsRenderableUltraHdr == true
                && _renderer.LastRenderStatus.StartsWith("Gain-map shader presented", StringComparison.Ordinal);
            var d3dPresented = _renderer.LastRenderStatus.StartsWith("Gain-map shader presented", StringComparison.Ordinal)
                || _renderer.LastRenderStatus.StartsWith("Base image D2D system pipeline presented", StringComparison.Ordinal)
                || _renderer.LastRenderStatus.StartsWith("Base image shader presented", StringComparison.Ordinal);
            if (d3dPresented && _renderer.IsSwapChainPanelBound && _renderer.LastFrameHasVisiblePixels)
            {
                FallbackImage.Visibility = Visibility.Collapsed;
            }
            else if (gainMapPresented)
            {
                renderStatus = $"{renderStatus}; showing SDR fallback while D3D surface is verified";
            }

            openTimer.Stop();
            renderStatus = $"{renderStatus}; open timing probe {probeTimer.ElapsedMilliseconds}ms, resize {resizeTimer.ElapsedMilliseconds}ms, render {renderTimer.ElapsedMilliseconds}ms, total {openTimer.ElapsedMilliseconds}ms";
            ViewerSessionState.SaveImage(document.Path, _folderImagePaths, _currentNavigationIsExplicit);
            QueueAdjacentPreloads();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            ViewModel.UpdateRenderStatus($"打开失败: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (!string.IsNullOrWhiteSpace(renderStatus))
        {
            ViewModel.UpdateRenderStatus(renderStatus);
        }
    }

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

    private void RefreshFolderImageList(string currentPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(currentPath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                _folderImagePaths = [currentPath];
                _currentFolderIndex = 0;
                RefreshFilmstripItems();
                UpdateFolderNavigationOverlay();
                return;
            }

            _folderImagePaths = Directory
                .EnumerateFiles(directory)
                .Where(IsSupportedImagePath)
                .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            _currentFolderIndex = _folderImagePaths.FindIndex(
                file => string.Equals(file, currentPath, StringComparison.OrdinalIgnoreCase));
            if (_currentFolderIndex < 0)
            {
                _folderImagePaths.Add(currentPath);
                _folderImagePaths = _folderImagePaths
                    .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                _currentFolderIndex = _folderImagePaths.FindIndex(
                    file => string.Equals(file, currentPath, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch
        {
            _folderImagePaths = [currentPath];
            _currentFolderIndex = 0;
        }

        RefreshFilmstripItems();
        UpdateFolderNavigationOverlay();
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

    private void UpdateFolderNavigationOverlay()
    {
        var hasImage = _folderImagePaths.Count > 0 && _currentFolderIndex >= 0;
        PhotoToolbarOverlay.Visibility = Visibility.Visible;
        if (!hasImage)
        {
            PreviousImageButton.IsEnabled = false;
            NextImageButton.IsEnabled = false;
            SidePreviousImageButton.IsEnabled = false;
            SideNextImageButton.IsEnabled = false;
            SidePreviousImageButton.Visibility = Visibility.Collapsed;
            SideNextImageButton.Visibility = Visibility.Collapsed;
            FilmstripRow.Visibility = Visibility.Collapsed;
            ImageFilmstrip.SelectedIndex = -1;
            FolderPositionText.Text = "0 / 0";
            FolderFileNameText.Text = ViewModel.FileName;
            FolderFileNameText.Visibility = Visibility.Visible;
            CropButton.IsEnabled = false;
            TopCropButton.IsEnabled = false;
            FullScreenButton.IsEnabled = false;
            TopFullScreenButton.IsEnabled = false;
            UpdateFilmstripChromeLayout();
            UpdateZoomControls();
            return;
        }

        var canGoPrevious = _currentFolderIndex > 0;
        var canGoNext = _currentFolderIndex < _folderImagePaths.Count - 1;
        PreviousImageButton.IsEnabled = canGoPrevious;
        NextImageButton.IsEnabled = canGoNext;
        SidePreviousImageButton.IsEnabled = canGoPrevious;
        SideNextImageButton.IsEnabled = canGoNext;
        SidePreviousImageButton.Visibility = canGoPrevious ? Visibility.Visible : Visibility.Collapsed;
        SideNextImageButton.Visibility = canGoNext ? Visibility.Visible : Visibility.Collapsed;
        var showFilmstrip = _folderImagePaths.Count > 1;
        FilmstripRow.Visibility = showFilmstrip ? Visibility.Visible : Visibility.Collapsed;
        FolderFileNameText.Visibility = showFilmstrip ? Visibility.Collapsed : Visibility.Visible;
        FolderPositionText.Text = $"{_currentFolderIndex + 1} / {_folderImagePaths.Count}";
        FolderFileNameText.Text = Path.GetFileName(_folderImagePaths[_currentFolderIndex]);
        CropButton.IsEnabled = true;
        TopCropButton.IsEnabled = true;
        FullScreenButton.IsEnabled = true;
        TopFullScreenButton.IsEnabled = true;
        UpdateFilmstripSelection();
        UpdateFilmstripChromeLayout();
        UpdateZoomControls();
    }

    private void RefreshFilmstripItems()
    {
        PruneThumbnailCache();

        var needsRebuild = FilmstripItems.Count != _folderImagePaths.Count;
        if (!needsRebuild)
        {
            for (var index = 0; index < FilmstripItems.Count; index++)
            {
                if (!string.Equals(FilmstripItems[index].Path, _folderImagePaths[index], StringComparison.OrdinalIgnoreCase))
                {
                    needsRebuild = true;
                    break;
                }
            }
        }

        if (needsRebuild)
        {
            _thumbnailCts?.Cancel();
            FilmstripItems.Clear();
            foreach (var path in _folderImagePaths)
            {
                var item = new FilmstripImageItem(path);
                if (_thumbnailCache.TryGetValue(path, out var cachedThumbnail))
                {
                    item.Thumbnail = cachedThumbnail;
                }

                FilmstripItems.Add(item);
            }

            QueueFilmstripThumbnailLoads();
        }

        UpdateFilmstripSelection();
        UpdateFilmstripChromeLayout();
    }

    private void UpdateFilmstripSelection()
    {
        if (ImageFilmstrip is null)
        {
            return;
        }

        if (_currentFolderIndex < 0 || _currentFolderIndex >= FilmstripItems.Count)
        {
            ImageFilmstrip.SelectedIndex = -1;
            return;
        }

        ImageFilmstrip.SelectedIndex = _currentFolderIndex;
        var selectedItem = FilmstripItems[_currentFolderIndex];
        DispatcherQueue.TryEnqueue(() => ImageFilmstrip.ScrollIntoView(selectedItem, ScrollIntoViewAlignment.Leading));
    }

    private void UpdateFilmstripChromeLayout()
    {
        if (ImageFilmstrip is null || PhotoToolbarOverlay is null)
        {
            return;
        }

        var availableWidth = PreviewSurface.ActualWidth > 0.0
            ? PreviewSurface.ActualWidth
            : 980.0;
        var overlayMaxWidth = Math.Clamp(availableWidth - 36.0, 360.0, 1180.0);
        PhotoToolbarOverlay.MaxWidth = overlayMaxWidth;

        var itemWidth = 68.0;
        var reservedToolbarWidth = 610.0;
        var desiredFilmstripWidth = Math.Min(FilmstripItems.Count * itemWidth + 2.0, overlayMaxWidth - reservedToolbarWidth);
        ImageFilmstrip.Width = Math.Max(0.0, desiredFilmstripWidth);
        FilmstripRow.Width = ImageFilmstrip.Width;
    }

    private void QueueFilmstripThumbnailLoads()
    {
        _thumbnailCts?.Cancel();
        if (FilmstripItems.Count == 0)
        {
            return;
        }

        _thumbnailCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _thumbnailCts.Token;
        var loadOrder = CreateFilmstripThumbnailLoadOrder();
        _ = LoadFilmstripThumbnailsAsync(loadOrder, token);
    }

    private IReadOnlyList<FilmstripImageItem> CreateFilmstripThumbnailLoadOrder()
    {
        if (_currentFolderIndex < 0)
        {
            return FilmstripItems.ToList();
        }

        return FilmstripItems
            .Select((item, index) => new
            {
                Item = item,
                Distance = Math.Abs(index - _currentFolderIndex),
                Index = index,
            })
            .OrderBy(entry => entry.Distance)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Item)
            .ToList();
    }

    private async Task LoadFilmstripThumbnailsAsync(IReadOnlyList<FilmstripImageItem> items, CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (item.Thumbnail is not null)
            {
                continue;
            }

            if (_thumbnailCache.TryGetValue(item.Path, out var cachedThumbnail))
            {
                item.Thumbnail = cachedThumbnail;
                continue;
            }

            ImageSource? thumbnail;
            try
            {
                thumbnail = await CreateFilmstripThumbnailAsync(item.Path, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (thumbnail is null || cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            _thumbnailCache[item.Path] = thumbnail;
            item.Thumbnail = thumbnail;
        }
    }

    private static Task<ImageSource?> CreateFilmstripThumbnailAsync(string path, CancellationToken cancellationToken)
    {
        return PhotoThumbnailService.CreateAsync(path, 256, cancellationToken);
    }

    private void PruneThumbnailCache()
    {
        var keepPaths = new HashSet<string>(_folderImagePaths, StringComparer.OrdinalIgnoreCase);
        foreach (var path in _thumbnailCache.Keys.ToList())
        {
            if (!keepPaths.Contains(path))
            {
                _thumbnailCache.Remove(path);
            }
        }
    }

    private async void ImageFilmstrip_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FilmstripImageItem item
            || _isFolderNavigationLoading
            || string.Equals(item.Path, ViewModel.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _isFolderNavigationLoading = true;
        try
        {
            await LoadImagePathAsync(item.Path, invalidateRendererCache: false);
        }
        finally
        {
            _isFolderNavigationLoading = false;
        }
    }

    private static bool IsSupportedImagePath(string path)
    {
        var extension = Path.GetExtension(path);
        return DecoderCatalog.FileTypeFilter.Any(
            filter => string.Equals(filter, extension, StringComparison.OrdinalIgnoreCase));
    }

    private async void HdrSwapChainHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isZoomCommitInProgress || _suppressSwapChainSizeChangedForZoom)
        {
            return;
        }

        RefreshRendererDisplayConfiguration();
        await ResizeRendererAsync();
        ViewModel.UpdateRenderStatus(_renderer.LastRenderStatus);
    }

    private async void PreviewSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePreviewSurfaceClip();
        UpdateFilmstripChromeLayout();
        UpdateImageSurfaceLayout();
        CenterScrollableImage();
        RefreshRendererDisplayConfiguration();
        await ResizeRendererAsync();
        ViewModel.UpdateRenderStatus(_renderer.LastRenderStatus);
    }

    private async void HdrSwapChainHost_CompositionScaleChanged(SwapChainPanel sender, object args)
    {
        RefreshRendererDisplayConfiguration();
        await ResizeRendererAsync();
        ViewModel.UpdateRenderStatus(_renderer.LastRenderStatus);
    }

    private async void DisplayInformation_AdvancedColorInfoChanged(DisplayInformation sender, object args)
    {
        RefreshRendererDisplayConfiguration();
        await ResizeRendererAsync();
        ViewModel.UpdateRenderStatus(_renderer.LastRenderStatus);
    }

    private async void HdrPreviewModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingHdrModeControls)
        {
            return;
        }

        await ApplyHdrPreviewOverrideAsync();
    }

    private async void HdrHeadroomModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingHdrModeControls)
        {
            return;
        }

        await ApplyHdrPreviewOverrideAsync();
    }

    private async void SdrWhiteOverrideToggle_Toggled(object sender, RoutedEventArgs e)
    {
        UpdateSdrWhiteControls();
        await ApplyHdrPreviewOverrideAsync();
    }

    private async void SdrWhiteSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateSdrWhiteControls();
        await ApplyHdrPreviewOverrideAsync();
    }

    private async void HdrGainSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateHdrGainValueText();
        if (HdrPreviewModeSelector is not null && HdrGainPanel?.Visibility == Visibility.Visible)
        {
            await ApplyHdrPreviewOverrideAsync();
        }
    }

    private void AppSettingsService_SettingsChanged(object? sender, EventArgs e)
    {
        _settings = AppSettingsService.Current;
        QueueAdjacentPreloads();
    }

    private async void PreviewSurface_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!ViewModel.HasImage)
        {
            return;
        }

        var pointerPoint = e.GetCurrentPoint(PreviewSurface);
        var wheelDelta = pointerPoint.Properties.MouseWheelDelta;
        if (wheelDelta == 0)
        {
            return;
        }

        var isControlDown = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        var shouldZoom = _settings.MouseWheelBehavior == MouseWheelBehavior.ZoomImage;
        if (isControlDown)
        {
            shouldZoom = !shouldZoom;
        }

        if (shouldZoom)
        {
            SetPendingZoomAnchor(pointerPoint.Position);
            var factor = Math.Pow(1.06, wheelDelta / 120.0);
            await ZoomByFactorAsync(factor, deferRender: true);
        }
        else
        {
            _wheelNavigateAccumulator += wheelDelta;
            if (Math.Abs(_wheelNavigateAccumulator) >= 120.0)
            {
                var direction = _wheelNavigateAccumulator < 0.0 ? 1 : -1;
                _wheelNavigateAccumulator = 0.0;
                await NavigateFolderImageAsync(direction);
            }
        }

        e.Handled = true;
    }

    private void PreviewSurface_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!ViewModel.HasImage || _isCropModeEnabled || ImageScroller is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(PreviewSurface);
        if (point.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse
            && !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (!CanPanImage())
        {
            return;
        }

        _isPanning = true;
        _panPointerId = e.Pointer.PointerId;
        _panStartPointerPosition = point.Position;
        _panStartScrollOffsetX = ImageScroller.HorizontalOffset;
        _panStartScrollOffsetY = ImageScroller.VerticalOffset;
        PreviewSurface.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void PreviewSurface_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isPanning || e.Pointer.PointerId != _panPointerId || ImageScroller is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(PreviewSurface);
        var deltaX = _panStartPointerPosition.X - point.Position.X;
        var deltaY = _panStartPointerPosition.Y - point.Position.Y;
        var newOffsetX = Math.Clamp(_panStartScrollOffsetX + deltaX, 0.0, ImageScroller.ScrollableWidth);
        var newOffsetY = Math.Clamp(_panStartScrollOffsetY + deltaY, 0.0, ImageScroller.ScrollableHeight);
        ImageScroller.ChangeView(newOffsetX, newOffsetY, null, disableAnimation: true);
        e.Handled = true;
    }

    private void PreviewSurface_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        EndPan(e);
    }

    private void PreviewSurface_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        EndPan(e);
    }

    private void EndPan(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _panPointerId)
        {
            return;
        }

        _isPanning = false;
        PreviewSurface.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private bool CanPanImage()
    {
        if (ImageScroller is null)
        {
            return false;
        }

        return ImageScroller.ScrollableWidth > 1.0 || ImageScroller.ScrollableHeight > 1.0;
    }

    private async void PreviewSurface_ManipulationDelta(object sender, Microsoft.UI.Xaml.Input.ManipulationDeltaRoutedEventArgs e)
    {
        if (!_settings.TouchpadGesturesEnabled || !ViewModel.HasImage)
        {
            return;
        }

        var scale = e.Delta.Scale;
        if (Math.Abs(scale - 1.0) < 0.01)
        {
            return;
        }

        _isFitZoom = false;
        _isFillZoom = false;
        SetPendingZoomAnchorToViewportCenter();
        await ZoomByFactorAsync(scale, deferRender: true);
        e.Handled = true;
    }

    private async void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasImage)
        {
            return;
        }

        _isFitZoom = false;
        _isFillZoom = false;
        await ZoomByFactorAsync(1.0 / 1.25);
    }

    private async void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasImage)
        {
            return;
        }

        _isFitZoom = false;
        _isFillZoom = false;
        await ZoomByFactorAsync(1.25);
    }

    private async void ActualSize_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasImage)
        {
            return;
        }

        _isFitZoom = false;
        _isFillZoom = false;
        _zoomScale = CalculateActualSizeZoomScale();
        await ApplyZoomAsync();
    }

    private async void ZoomFit_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasImage)
        {
            return;
        }

        ResetZoomToFit();
        await ApplyZoomAsync();
    }

    private async void ZoomFill_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasImage)
        {
            return;
        }

        _isFitZoom = false;
        _isFillZoom = true;
        _zoomScale = 1.0;
        await ApplyZoomAsync();
    }

    private void CropButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasImage)
        {
            return;
        }

        SetCropMode(!_isCropModeEnabled);
    }

    private void CancelCrop_Click(object sender, RoutedEventArgs e)
    {
        SetCropMode(false);
    }

    private async void ApplyCrop_Click(object sender, RoutedEventArgs e)
    {
        await ExportCurrentCropAsync();
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is MainWindow mainWindow)
        {
            var isImmersive = mainWindow.ToggleImmersiveViewing();
            ApplyImmersiveViewingState(isImmersive);
        }
    }

    private void SetCropMode(bool isEnabled)
    {
        _isCropModeEnabled = isEnabled;
        CropOverlay.Visibility = isEnabled ? Visibility.Visible : Visibility.Collapsed;
        UpdateCropExportControls();
        if (isEnabled)
        {
            InitializeCropFrame();
        }
        else
        {
            _isDraggingCropFrame = false;
        }

        ToolTipService.SetToolTip(CropButton, isEnabled ? "退出裁切" : "裁切");
        ToolTipService.SetToolTip(TopCropButton, isEnabled ? "退出裁切" : "裁切");
    }

    private void InitializeCropFrame()
    {
        CropOverlay.UpdateLayout();
        ImageSurface.UpdateLayout();
        var imageRect = GetImageRectInCropOverlay();
        if (imageRect.Width <= 1.0 || imageRect.Height <= 1.0)
        {
            CropFrame.HorizontalAlignment = HorizontalAlignment.Center;
            CropFrame.VerticalAlignment = VerticalAlignment.Center;
            CropFrame.Width = Math.Min(520.0, Math.Max(160.0, CropOverlay.ActualWidth * 0.5));
            CropFrame.Height = Math.Min(340.0, Math.Max(120.0, CropOverlay.ActualHeight * 0.5));
            CropFrame.Margin = new Thickness(0);
            return;
        }

        var width = Math.Clamp(imageRect.Width * 0.72, 160.0, imageRect.Width);
        var height = Math.Clamp(imageRect.Height * 0.72, 120.0, imageRect.Height);
        var left = imageRect.Left + ((imageRect.Width - width) / 2.0);
        var top = imageRect.Top + ((imageRect.Height - height) / 2.0);
        SetCropFrameRect(left, top, width, height);
    }

    private void CropFrame_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isCropModeEnabled)
        {
            return;
        }

        if (IsCropResizeThumbSource(e.OriginalSource))
        {
            return;
        }

        var point = e.GetCurrentPoint(CropOverlay);
        if (point.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse
            && !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isDraggingCropFrame = true;
        _cropDragPointerId = e.Pointer.PointerId;
        _cropDragStartPointerPosition = point.Position;
        _cropDragStartMargin = CropFrame.Margin;
        CropFrame.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void CropFrame_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isDraggingCropFrame || e.Pointer.PointerId != _cropDragPointerId)
        {
            return;
        }

        var point = e.GetCurrentPoint(CropOverlay).Position;
        var deltaX = point.X - _cropDragStartPointerPosition.X;
        var deltaY = point.Y - _cropDragStartPointerPosition.Y;
        var imageRect = GetImageRectInCropOverlay();
        var left = _cropDragStartMargin.Left + deltaX;
        var top = _cropDragStartMargin.Top + deltaY;
        left = Math.Clamp(left, imageRect.Left, Math.Max(imageRect.Left, imageRect.Right - CropFrame.Width));
        top = Math.Clamp(top, imageRect.Top, Math.Max(imageRect.Top, imageRect.Bottom - CropFrame.Height));
        SetCropFrameRect(left, top, CropFrame.Width, CropFrame.Height);
        e.Handled = true;
    }

    private void CropFrame_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _cropDragPointerId)
        {
            return;
        }

        _isDraggingCropFrame = false;
        CropFrame.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void CropResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string corner })
        {
            return;
        }

        var imageRect = GetImageRectInCropOverlay();
        var left = CropFrame.Margin.Left;
        var top = CropFrame.Margin.Top;
        var right = left + CropFrame.Width;
        var bottom = top + CropFrame.Height;

        if (corner.Contains("Left", StringComparison.Ordinal))
        {
            left = Math.Clamp(left + e.HorizontalChange, imageRect.Left, right - MinCropWidth);
        }
        else if (corner.Contains("Right", StringComparison.Ordinal))
        {
            right = Math.Clamp(right + e.HorizontalChange, left + MinCropWidth, imageRect.Right);
        }

        if (corner.Contains("Top", StringComparison.Ordinal))
        {
            top = Math.Clamp(top + e.VerticalChange, imageRect.Top, bottom - MinCropHeight);
        }
        else if (corner.Contains("Bottom", StringComparison.Ordinal))
        {
            bottom = Math.Clamp(bottom + e.VerticalChange, top + MinCropHeight, imageRect.Bottom);
        }

        SetCropFrameRect(left, top, right - left, bottom - top);
    }

    private static bool IsCropResizeThumbSource(object? source)
    {
        if (source is not DependencyObject current)
        {
            return false;
        }

        while (current is not null)
        {
            if (current is Thumb { Tag: string tag } && IsCropResizeTag(tag))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool IsCropResizeTag(string tag)
    {
        return tag.Contains("Left", StringComparison.Ordinal)
            || tag.Contains("Right", StringComparison.Ordinal)
            || tag.Contains("Top", StringComparison.Ordinal)
            || tag.Contains("Bottom", StringComparison.Ordinal);
    }

    private Windows.Foundation.Rect GetImageRectInCropOverlay()
    {
        if (ImageSurface.ActualWidth <= 1.0 || ImageSurface.ActualHeight <= 1.0)
        {
            return new Windows.Foundation.Rect(0, 0, CropOverlay.ActualWidth, CropOverlay.ActualHeight);
        }

        var transform = ImageSurface.TransformToVisual(CropOverlay);
        var topLeft = transform.TransformPoint(new Windows.Foundation.Point(0.0, 0.0));
        var bottomRight = transform.TransformPoint(new Windows.Foundation.Point(ImageSurface.ActualWidth, ImageSurface.ActualHeight));
        var left = Math.Clamp(Math.Min(topLeft.X, bottomRight.X), 0.0, CropOverlay.ActualWidth);
        var top = Math.Clamp(Math.Min(topLeft.Y, bottomRight.Y), 0.0, CropOverlay.ActualHeight);
        var right = Math.Clamp(Math.Max(topLeft.X, bottomRight.X), 0.0, CropOverlay.ActualWidth);
        var bottom = Math.Clamp(Math.Max(topLeft.Y, bottomRight.Y), 0.0, CropOverlay.ActualHeight);
        return new Windows.Foundation.Rect(left, top, Math.Max(0.0, right - left), Math.Max(0.0, bottom - top));
    }

    private void SetCropFrameRect(double left, double top, double width, double height)
    {
        CropFrame.HorizontalAlignment = HorizontalAlignment.Left;
        CropFrame.VerticalAlignment = VerticalAlignment.Top;
        CropFrame.Width = Math.Max(MinCropWidth, width);
        CropFrame.Height = Math.Max(MinCropHeight, height);
        CropFrame.Margin = new Thickness(left, top, 0, 0);
    }

    private void CropExportModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCropExportControls();
        if (_isCropModeEnabled)
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; {BuildCropExportModeStatus()}");
        }
    }

    private void CropHdrTransferSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isCropModeEnabled && SelectedCropExportMode == CropExportMode.SingleLayerHdr)
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; {BuildCropExportModeStatus()}");
        }
    }

    private void UpdateCropExportControls()
    {
        if (CropHdrTransferSelector is not null)
        {
            CropHdrTransferSelector.Visibility = SelectedCropExportMode == CropExportMode.SingleLayerHdr
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private CropExportMode SelectedCropExportMode => CropExportModeSelector?.SelectedIndex switch
    {
        1 => CropExportMode.GainMapPreserve,
        2 => CropExportMode.UltraHdrConvert,
        3 => CropExportMode.SingleLayerHdr,
        _ => CropExportMode.SdrPreview,
    };

    private CropHdrTransfer SelectedCropHdrTransfer => CropHdrTransferSelector?.SelectedIndex == 1
        ? CropHdrTransfer.Hlg
        : CropHdrTransfer.Pq;

    private async Task ExportCurrentCropAsync()
    {
        if (_currentDocument is null)
        {
            return;
        }

        if (!TryCalculateCropBounds(out var bounds))
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 裁切失败: 裁切框没有覆盖图片");
            return;
        }

        switch (SelectedCropExportMode)
        {
            case CropExportMode.GainMapPreserve:
                await ExportPreservedGainMapCropAsync(bounds);
                return;
            case CropExportMode.UltraHdrConvert:
                await ExportUltraHdrConvertedCropAsync(bounds);
                return;
            case CropExportMode.SingleLayerHdr:
                await ExportSingleLayerHdrCropAsync(bounds);
                return;
            default:
                await ExportSdrPreviewCropAsync(bounds);
                return;
        }
    }

    private async Task ExportSdrPreviewCropAsync(BitmapBounds bounds)
    {
        if (_currentDocument is null)
        {
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = CreateCropSuggestedFileName(_currentDocument, CropExportMode.SdrPreview),
        };
        picker.FileTypeChoices.Add("PNG SDR 预览", [".png"]);
        picker.FileTypeChoices.Add("TIFF 16-bit SDR 预览", [".tif"]);
        picker.FileTypeChoices.Add("JPEG SDR 预览", [".jpg"]);

        if (App.MainWindow is not null)
        {
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        }

        var outputFile = await picker.PickSaveFileAsync();
        if (outputFile is null)
        {
            return;
        }

        try
        {
            CachedFileManager.DeferUpdates(outputFile);
            await using var inputStream = File.OpenRead(_currentDocument.Path);
            using var source = inputStream.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(source);
            var transform = new BitmapTransform { Bounds = bounds };
            var exportFormat = GetSdrPreviewExportFormat(outputFile.FileType);
            var pixelData = await decoder.GetPixelDataAsync(
                exportFormat.PixelFormat,
                exportFormat.AlphaMode,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);

            await using var output = await outputFile.OpenStreamForWriteAsync();
            output.SetLength(0);
            using var destination = output.AsRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(exportFormat.EncoderId, destination);
            encoder.SetPixelData(
                exportFormat.PixelFormat,
                exportFormat.AlphaMode,
                bounds.Width,
                bounds.Height,
                decoder.DpiX,
                decoder.DpiY,
                pixelData.DetachPixelData());
            await encoder.FlushAsync();
            await CachedFileManager.CompleteUpdatesAsync(outputFile);

            SetCropMode(false);
            var hdrNote = IsHdrCropExportPreview(_currentDocument)
                ? "; 注意: 当前导出为 SDR 预览裁切，不保留 HLG/PQ/gain-map HDR 元数据"
                : string.Empty;
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 已导出裁切 {exportFormat.DisplayName}: {outputFile.Path}{hdrNote}");
        }
        catch (Exception ex)
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 裁切导出失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task ExportPreservedGainMapCropAsync(BitmapBounds bounds)
    {
        if (_currentDocument is null)
        {
            return;
        }

        if (_currentDocument.GainMapProbe?.IsRenderableUltraHdr != true)
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; Gain-map 保真裁切不可用: 只支持已经包含可渲染 JPEG gain-map 的图片。");
            return;
        }

        var availableChoices = HdrExportBackendCatalog.GetChoices(HdrExportMode.GainMap)
            .Where(choice => choice.IsAvailable)
            .ToArray();
        if (availableChoices.Length == 0)
        {
            ViewModel.UpdateRenderStatus(
                $"{_renderer.LastRenderStatus}; Gain-map 保真裁切暂未接入可写后端: 需要 libultrahdr scenario 4 重新封装 base + gain map。裁切区域 {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height} 已计算；未弹保存框、未生成文件。{HdrExportBackendCatalog.BuildBackendSummary()}");
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = CreateCropSuggestedFileName(_currentDocument, CropExportMode.GainMapPreserve),
        };
        AddAvailableExportChoices(picker, HdrExportMode.GainMap);

        if (App.MainWindow is not null)
        {
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        }

        var outputFile = await picker.PickSaveFileAsync();
        if (outputFile is null)
        {
            return;
        }

        try
        {
            await DeleteUnwrittenPickerFileAsync(outputFile);
            var exportSummary = await GainMapHdrExportService.ExportPreservedJpegGainMapCropAsync(
                _currentDocument,
                bounds,
                outputFile.Path,
                _lifetime.Token);
            SetCropMode(false);
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 已导出 Gain-map 保真裁切: {outputFile.Path}; {exportSummary}");
        }
        catch (Exception ex)
        {
            await DeleteUnwrittenPickerFileAsync(outputFile);
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; Gain-map 保真裁切失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task ExportUltraHdrConvertedCropAsync(BitmapBounds bounds)
    {
        if (_currentDocument is null)
        {
            return;
        }

        if (!CanExportGainMapHdr(_currentDocument))
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 转为 Ultra HDR 不可用: 当前文件没有可重建的 gain-map 或单层 HDR 数据。");
            return;
        }

        var sourceKind = DescribeGainMapExportSource(_currentDocument);
        var availableChoices = HdrExportBackendCatalog.GetChoices(HdrExportMode.GainMap)
            .Where(choice => choice.IsAvailable)
            .ToArray();
        if (availableChoices.Length == 0)
        {
            ViewModel.UpdateRenderStatus(
                $"{_renderer.LastRenderStatus}; 转为 Ultra HDR 暂未接入可写后端: {sourceKind} 需要 libultrahdr/libavif/libheif 写出 gain-map metadata。裁切区域 {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height} 已计算；未弹保存框、未生成文件。{HdrExportBackendCatalog.BuildBackendSummary()}");
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = CreateCropSuggestedFileName(_currentDocument, CropExportMode.UltraHdrConvert),
        };
        AddAvailableExportChoices(picker, HdrExportMode.GainMap);

        if (App.MainWindow is not null)
        {
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        }

        var outputFile = await picker.PickSaveFileAsync();
        if (outputFile is null)
        {
            return;
        }

        try
        {
            await DeleteUnwrittenPickerFileAsync(outputFile);
            var exportSummary = await GainMapHdrExportService.ExportJpegUltraHdrAsync(
                _currentDocument,
                bounds,
                outputFile.Path,
                _lifetime.Token);
            SetCropMode(false);
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 已转为 Ultra HDR: {outputFile.Path}; {exportSummary}");
        }
        catch (Exception ex)
        {
            await DeleteUnwrittenPickerFileAsync(outputFile);
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 转为 Ultra HDR 失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task ExportSingleLayerHdrCropAsync(BitmapBounds bounds)
    {
        if (_currentDocument is null)
        {
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = CreateCropSuggestedFileName(_currentDocument, CropExportMode.SingleLayerHdr),
        };
        AddAvailableExportChoices(picker, HdrExportMode.SingleLayer);

        if (App.MainWindow is not null)
        {
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        }

        var outputFile = await picker.PickSaveFileAsync();
        if (outputFile is null)
        {
            return;
        }

        try
        {
            var transfer = SelectedCropHdrTransfer;
            await DeleteUnwrittenPickerFileAsync(outputFile);
            var exportTransfer = transfer == CropHdrTransfer.Hlg
                ? SingleLayerHdrExportTransfer.Hlg
                : SingleLayerHdrExportTransfer.Pq;
            var exportSummary = await SingleLayerHdrExportService.ExportAsync(
                _currentDocument,
                bounds,
                outputFile.Path,
                exportTransfer,
                _lifetime.Token);

            SetCropMode(false);
            var transferLabel = transfer == CropHdrTransfer.Hlg ? "HLG" : "PQ";
            ViewModel.UpdateRenderStatus(
                $"{_renderer.LastRenderStatus}; 已导出单层 HDR {transferLabel}: {outputFile.Path}; {exportSummary}; {bounds.Width}x{bounds.Height}");
        }
        catch (Exception ex)
        {
            await DeleteUnwrittenPickerFileAsync(outputFile);
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 单层 HDR 裁切导出失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void AddAvailableExportChoices(FileSavePicker picker, HdrExportMode mode)
    {
        var choices = HdrExportBackendCatalog.GetChoices(mode)
            .Where(choice => choice.IsAvailable)
            .ToArray();
        foreach (var choice in choices)
        {
            picker.FileTypeChoices.Add($"{choice.DisplayName} ({choice.Backend})", [choice.Extension]);
        }

        if (choices.Length == 0)
        {
            var fallback = mode == HdrExportMode.GainMap
                ? HdrExportBackendCatalog.GetChoices(mode).First(choice => choice.Extension == ".jpg")
                : HdrExportBackendCatalog.GetChoices(mode).First(choice => choice.Extension == ".jxl");
            picker.FileTypeChoices.Add($"{fallback.DisplayName} ({fallback.Backend})", [fallback.Extension]);
        }
    }
    private string BuildCropExportModeStatus()
    {
        return SelectedCropExportMode switch
        {
            CropExportMode.GainMapPreserve => "导出模式: Gain-map 保真裁切，裁切 base/gainmap 并保留原映射参数",
            CropExportMode.UltraHdrConvert => "导出模式: 转为 Ultra HDR，从重建 HDR 重新生成 SDR base、gainmap 和 metadata",
            CropExportMode.SingleLayerHdr => $"导出模式: 单层 HDR 转换，目标 {DescribeCropHdrTransfer(SelectedCropHdrTransfer)}；JXL/AVIF metadata 会写入对应 transfer",
            _ => "导出模式: SDR 预览，使用系统 BitmapEncoder 输出 sRGB 裁切图",
        };
    }

    private static bool CanExportGainMapHdr(HdrImageDocument document)
    {
        return document.GainMapProbe?.IsRenderableUltraHdr == true
            || document.HeifAvifProbe?.HasHdrTransfer == true
            || document.HeifAvifProbe?.HasGainMapAuxiliary == true
            || document.Format.Kind is HdrImageKind.SingleLayerHdr;
    }

    private static string DescribeGainMapExportSource(HdrImageDocument document)
    {
        if (document.GainMapProbe?.IsRenderableUltraHdr == true)
        {
            return "JPEG Ultra HDR / Apple HDRGainMap source";
        }

        if (document.HeifAvifProbe?.HasGainMapAuxiliary == true)
        {
            return "HEIF/AVIF gain-map auxiliary source";
        }

        if (document.HeifAvifProbe?.HasHdrTransfer == true || document.Format.Kind is HdrImageKind.SingleLayerHdr)
        {
            return "single-layer HDR source reconstructed to JPEG Ultra HDR";
        }

        return document.Format.DisplayName;
    }

    private static string DescribeCropHdrTransfer(CropHdrTransfer transfer)
    {
        return transfer == CropHdrTransfer.Hlg ? "HLG (ARIB STD-B67)" : "PQ HDR10 (SMPTE ST 2084)";
    }

    private static string CreateCropSuggestedFileName(HdrImageDocument document, CropExportMode mode)
    {
        var suffix = mode switch
        {
            CropExportMode.GainMapPreserve => "crop-gainmap-preserve",
            CropExportMode.UltraHdrConvert => "crop-ultra-hdr",
            CropExportMode.SingleLayerHdr => "crop-single-layer-hdr",
            _ => IsHdrCropExportPreview(document) ? "crop-sdr-preview" : "crop",
        };
        return $"{Path.GetFileNameWithoutExtension(document.FileName)}-{suffix}";
    }

    private static bool IsHdrCropExportPreview(HdrImageDocument document)
    {
        return document.Format.Kind is HdrImageKind.GainMap or HdrImageKind.SingleLayerHdr
            || document.GainMapProbe?.IsRenderableUltraHdr == true
            || document.HeifAvifProbe?.HasHdrTransfer == true
            || document.HeifAvifProbe?.HasGainMapAuxiliary == true;
    }

    private static CropExportFormat GetSdrPreviewExportFormat(string fileType)
    {
        return fileType.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new CropExportFormat(
                BitmapEncoder.JpegEncoderId,
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                "JPEG SDR 预览"),
            ".tif" or ".tiff" => new CropExportFormat(
                BitmapEncoder.TiffEncoderId,
                BitmapPixelFormat.Rgba16,
                BitmapAlphaMode.Premultiplied,
                "TIFF 16-bit SDR 预览"),
            _ => new CropExportFormat(
                BitmapEncoder.PngEncoderId,
                BitmapPixelFormat.Rgba8,
                BitmapAlphaMode.Premultiplied,
                "PNG SDR 预览"),
        };
    }

    private static async Task DeleteUnwrittenPickerFileAsync(StorageFile file)
    {
        try
        {
            await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
        }
        catch
        {
            // Best-effort cleanup: the important part is that unsupported HDR exports never write fake image data.
        }
    }

    private static bool HasExecutableOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(directory, fileName)))
                {
                    return true;
                }
            }
            catch
            {
                continue;
            }
        }

        return false;
    }

    private enum CropHdrTransfer
    {
        Pq,
        Hlg,
    }

    private sealed record CropExportFormat(
        Guid EncoderId,
        BitmapPixelFormat PixelFormat,
        BitmapAlphaMode AlphaMode,
        string DisplayName);
    private bool TryCalculateCropBounds(out BitmapBounds bounds)
    {
        bounds = default;
        if (_renderer.ContentPixelWidth <= 0
            || _renderer.ContentPixelHeight <= 0
            || ImageSurface.ActualWidth <= 1.0
            || ImageSurface.ActualHeight <= 1.0)
        {
            return false;
        }

        var transform = CropFrame.TransformToVisual(ImageSurface);
        var topLeft = transform.TransformPoint(new Windows.Foundation.Point(0.0, 0.0));
        var bottomRight = transform.TransformPoint(new Windows.Foundation.Point(CropFrame.ActualWidth, CropFrame.ActualHeight));
        var left = Math.Clamp(Math.Min(topLeft.X, bottomRight.X), 0.0, ImageSurface.ActualWidth);
        var top = Math.Clamp(Math.Min(topLeft.Y, bottomRight.Y), 0.0, ImageSurface.ActualHeight);
        var right = Math.Clamp(Math.Max(topLeft.X, bottomRight.X), 0.0, ImageSurface.ActualWidth);
        var bottom = Math.Clamp(Math.Max(topLeft.Y, bottomRight.Y), 0.0, ImageSurface.ActualHeight);
        if (right - left < 2.0 || bottom - top < 2.0)
        {
            return false;
        }

        var pixelLeft = (uint)Math.Clamp(Math.Round(left / ImageSurface.ActualWidth * _renderer.ContentPixelWidth), 0.0, _renderer.ContentPixelWidth - 1.0);
        var pixelTop = (uint)Math.Clamp(Math.Round(top / ImageSurface.ActualHeight * _renderer.ContentPixelHeight), 0.0, _renderer.ContentPixelHeight - 1.0);
        var pixelRight = (uint)Math.Clamp(Math.Round(right / ImageSurface.ActualWidth * _renderer.ContentPixelWidth), pixelLeft + 1.0, _renderer.ContentPixelWidth);
        var pixelBottom = (uint)Math.Clamp(Math.Round(bottom / ImageSurface.ActualHeight * _renderer.ContentPixelHeight), pixelTop + 1.0, _renderer.ContentPixelHeight);
        bounds = new BitmapBounds
        {
            X = pixelLeft,
            Y = pixelTop,
            Width = pixelRight - pixelLeft,
            Height = pixelBottom - pixelTop,
        };
        return bounds.Width > 0 && bounds.Height > 0;
    }

    private async Task ApplyZoomAsync()
    {
        StopZoomAnimation();
        _targetZoomScale = _zoomScale;
        var hasAnchor = TryConsumePendingZoomAnchor(
                out var anchorX,
                out var anchorY,
                out var anchorViewportX,
                out var anchorViewportY)
            || TryCaptureViewportAnchor(out anchorX, out anchorY, out anchorViewportX, out anchorViewportY);
        ResetInteractionScaleTransform();
        UpdateImageSurfaceLayout();
        if (!_isFitZoom && !_isFillZoom && hasAnchor)
        {
            RestoreViewportAnchor(anchorX, anchorY, anchorViewportX, anchorViewportY);
        }

        RefreshRendererDisplayConfiguration();
        await ResizeRendererAsync();

        _committedZoomScale = _zoomScale;
        if (_isFitZoom || _isFillZoom || !hasAnchor)
        {
            CenterScrollableImage();
        }
        else
        {
            RestoreViewportAnchor(anchorX, anchorY, anchorViewportX, anchorViewportY);
        }

        UpdateZoomControls();
        ViewModel.UpdateRenderStatus(_renderer.LastRenderStatus);
    }

    private void PreviewDeferredZoom()
    {
        UpdateZoomControls();
    }

    private void ResetInteractionScaleTransform()
    {
        ImageInteractionScaleTransform.ScaleX = 1.0;
        ImageInteractionScaleTransform.ScaleY = 1.0;
        ImageSurface.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
    }

    private async Task ZoomByFactorAsync(double factor, bool deferRender = false)
    {
        _isFitZoom = false;
        _isFillZoom = false;
        var hasAnchor = TryConsumePendingZoomAnchor(
                out var anchorX,
                out var anchorY,
                out var anchorViewportX,
                out var anchorViewportY)
            || TryCaptureViewportAnchor(out anchorX, out anchorY, out anchorViewportX, out anchorViewportY);
        ResetInteractionScaleTransform();
        _targetZoomScale = Math.Clamp(_targetZoomScale * factor, 0.25, 4.0);
        if (deferRender)
        {
            StartZoomAnimation(hasAnchor, anchorX, anchorY, anchorViewportX, anchorViewportY);
            return;
        }

        _zoomScale = _targetZoomScale;
        await ApplyZoomAsync();
    }

    private void StartZoomAnimation(
        bool hasAnchor,
        double anchorX,
        double anchorY,
        double anchorViewportX,
        double anchorViewportY)
    {
        _hasZoomAnimationAnchor = hasAnchor;
        _zoomAnimationAnchorX = anchorX;
        _zoomAnimationAnchorY = anchorY;
        _zoomAnimationViewportX = anchorViewportX;
        _zoomAnimationViewportY = anchorViewportY;
        RunZoomAnimationStep();
        _zoomAnimationTimer?.Start();
    }

    private void StopZoomAnimation()
    {
        _zoomAnimationTimer?.Stop();
        _hasZoomAnimationAnchor = false;
    }

    private void ZoomAnimationTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (RunZoomAnimationStep())
        {
            sender.Stop();
        }
    }

    private bool RunZoomAnimationStep()
    {
        if (!ViewModel.HasImage || _lifetime.IsCancellationRequested)
        {
            return true;
        }

        var delta = _targetZoomScale - _zoomScale;
        if (Math.Abs(delta) < 0.0025)
        {
            _zoomScale = _targetZoomScale;
            ApplyAnimatedZoomFrame();
            _committedZoomScale = _zoomScale;
            PreviewDeferredZoom();
            QueueDeferredZoomRender();
            return true;
        }

        _zoomScale += delta * ZoomAnimationCatchUp;
        ApplyAnimatedZoomFrame();
        return false;
    }

    private void ApplyAnimatedZoomFrame()
    {
        if (!TryCalculateImageSurfaceTargetSize(out var targetWidth, out var targetHeight))
        {
            return;
        }

        var anchorX = _zoomAnimationAnchorX;
        var anchorY = _zoomAnimationAnchorY;
        var anchorViewportX = _zoomAnimationViewportX;
        var anchorViewportY = _zoomAnimationViewportY;
        NormalizeAnchorForTargetSize(
            ref anchorX,
            ref anchorY,
            ref anchorViewportX,
            ref anchorViewportY,
            targetWidth,
            targetHeight);

        _isZoomCommitInProgress = true;
        _suppressSwapChainSizeChangedForZoom = true;
        try
        {
            ApplyImageSurfaceSize(targetWidth, targetHeight);
        }
        finally
        {
            _isZoomCommitInProgress = false;
        }

        if (_hasZoomAnimationAnchor)
        {
            RestoreViewportAnchor(anchorX, anchorY, anchorViewportX, anchorViewportY);
        }
        else
        {
            CenterScrollableImage();
        }

        UpdateZoomControls();
    }

    private void QueueDeferredZoomRender()
    {
        _zoomRenderCts?.Cancel();
        _zoomRenderCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _zoomRenderCts.Token;
        _ = RenderZoomAfterInputSettlesAsync(token);
    }

    private async Task RenderZoomAfterInputSettlesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ZoomRenderDebounceMilliseconds, cancellationToken);
            if (!TryCalculateImageSurfaceTargetSize(out var targetWidth, out var targetHeight))
            {
                return;
            }

            RefreshRendererDisplayConfiguration();
            _isZoomCommitInProgress = true;
            try
            {
                await ResizeRendererAsync(targetWidth, targetHeight, cancellationToken);
            }
            finally
            {
                _isZoomCommitInProgress = false;
                _suppressSwapChainSizeChangedForZoom = false;
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                _committedZoomScale = _zoomScale;
                UpdateZoomControls();
                ViewModel.UpdateRenderStatus(_renderer.LastRenderStatus);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ResetZoomToFit()
    {
        StopZoomAnimation();
        _isFitZoom = true;
        _isFillZoom = false;
        _zoomScale = 1.0;
        _targetZoomScale = 1.0;
        _committedZoomScale = 1.0;
        ClearPendingZoomAnchor();
        ResetInteractionScaleTransform();
        UpdateZoomControls();
    }

    private void UpdateZoomControls()
    {
        if (ZoomLevelText is null)
        {
            return;
        }

        var hasImage = ViewModel.HasImage;
        ZoomOutButton.IsEnabled = hasImage && (!_isFitZoom || _zoomScale > 0.26);
        ZoomInButton.IsEnabled = hasImage && (!_isFitZoom || _zoomScale < 4.0);
        ZoomFitButton.IsEnabled = hasImage && !_isFitZoom;
        ActualSizeButton.IsEnabled = hasImage;
        ZoomFillButton.IsEnabled = hasImage && !_isFillZoom;
        ZoomLevelText.Text = hasImage
            ? (_isFillZoom ? "填满" : _isFitZoom ? "适合" : $"{_zoomScale * 100.0:0}%")
            : "适合";
    }

    private async Task ApplyHdrPreviewOverrideAsync()
    {
        if (HdrPreviewModeSelector is null || HdrGainSlider is null)
        {
            return;
        }

        var viewMode = HdrPreviewModeSelector.SelectedIndex switch
        {
            0 => GainmapViewMode.Sdr,
            2 => GainmapViewMode.AlternateImage,
            3 => GainmapViewMode.GainMap,
            _ => GainmapViewMode.Adaptive,
        };
        var headroomMode = HdrHeadroomModeSelector?.SelectedIndex switch
        {
            1 => HdrHeadroomMode.Manual,
            2 => HdrHeadroomMode.AblSoftProof,
            _ => HdrHeadroomMode.SystemAdaptive,
        };
        if (headroomMode == HdrHeadroomMode.AblSoftProof)
        {
            headroomMode = HdrHeadroomMode.SystemAdaptive;
        }

        var headroomControlsEnabled = viewMode == GainmapViewMode.Adaptive;
        var usesSlider = headroomControlsEnabled && headroomMode == HdrHeadroomMode.Manual;
        if (HdrHeadroomModeSelector is not null)
        {
            HdrHeadroomModeSelector.IsEnabled = headroomControlsEnabled;
            HdrHeadroomModeSelector.Opacity = headroomControlsEnabled ? 1.0 : 0.55;
        }

        HdrGainPanel.Visibility = usesSlider ? Visibility.Visible : Visibility.Collapsed;
        HdrGainSlider.IsEnabled = true;
        HdrGainSlider.IsHitTestVisible = usesSlider;
        HdrGainSlider.Opacity = usesSlider ? 1.0 : 0.55;

        if (IsLoaded)
        {
            RefreshRendererDisplayConfiguration();
        }

        UpdateHdrGainValueText();
        _renderer.ViewMode = viewMode;
        _renderer.HeadroomMode = headroomMode;
        _renderer.DisplayCapacityOverrideLog2 = usesSlider ? CalculateManualDisplayCapacityStops() : null;
        _renderer.AdaptiveToneMappingEnabled = false;

        if (IsLoaded)
        {
            await ResizeRendererAsync();
            ViewModel.UpdateRenderStatus(_renderer.LastRenderStatus);
        }
    }

    private void UpdateHdrGainValueText()
    {
        if (HdrGainValueText is not null && HdrGainSlider is not null)
        {
            HdrGainValueText.Text = $"{HdrGainSlider.Value:0} nits ({CalculateManualDisplayCapacityStops():0.##} 档)";
        }
    }

    private void UpdateHdrModeControlsForDocument(HdrImageDocument document)
    {
        if (HdrPreviewModeSelector is null)
        {
            return;
        }

        var hasGainMap = document.GainMapProbe?.IsRenderableUltraHdr == true;
        var isSingleLayerHdr = document.Format.Kind == HdrImageKind.SingleLayerHdr
            || document.HeifAvifProbe?.HasHdrTransfer == true;
        var supportsHdrModes = hasGainMap || isSingleLayerHdr;

        _updatingHdrModeControls = true;
        try
        {
            SetHdrModeItemEnabled(0, true);
            SetHdrModeItemEnabled(1, supportsHdrModes);
            SetHdrModeItemEnabled(2, supportsHdrModes);
            SetHdrModeItemEnabled(3, hasGainMap);

            if (!supportsHdrModes)
            {
                HdrPreviewModeSelector.SelectedIndex = 0;
            }
            else if (HdrPreviewModeSelector.SelectedIndex < 0 || !IsHdrModeItemEnabled(HdrPreviewModeSelector.SelectedIndex))
            {
                HdrPreviewModeSelector.SelectedIndex = 1;
            }

            if (HdrHeadroomModeSelector is not null && HdrHeadroomModeSelector.SelectedIndex < 0)
            {
                HdrHeadroomModeSelector.SelectedIndex = 0;
            }
        }
        finally
        {
            _updatingHdrModeControls = false;
        }

        _ = ApplyHdrPreviewOverrideAsync();
    }

    private void SetHdrModeItemEnabled(int index, bool enabled)
    {
        if (HdrPreviewModeSelector?.Items.Count > index
            && HdrPreviewModeSelector.Items[index] is ComboBoxItem item)
        {
            item.IsEnabled = enabled;
            item.Opacity = enabled ? 1.0 : 0.45;
        }
    }

    private bool IsHdrModeItemEnabled(int index)
    {
        return HdrPreviewModeSelector?.Items.Count > index
            && HdrPreviewModeSelector.Items[index] is ComboBoxItem item
            && item.IsEnabled;
    }

    private void SnapHdrGainSliderToDisplayPeakIfNeeded()
    {
        if (HdrGainSlider is null)
        {
            return;
        }

        var displayPeak = _renderer.DisplayConfiguration.MaxLuminanceInNits;
        if (displayPeak <= 0.0)
        {
            return;
        }

        if (Math.Abs(HdrGainSlider.Value - 1000.0) <= 1.0)
        {
            HdrGainSlider.Value = Math.Clamp(displayPeak, HdrGainSlider.Minimum, HdrGainSlider.Maximum);
        }
    }

    private float CalculateManualDisplayCapacityStops()
    {
        if (HdrGainSlider is null)
        {
            return 0.0f;
        }

        var sdrWhite = Math.Max(_renderer.DisplayConfiguration.SdrWhiteLevelInNits, 80.0);
        var targetPeak = Math.Max(HdrGainSlider.Value, sdrWhite);
        return (float)Math.Clamp(Math.Log2(targetPeak / sdrWhite), 0.0, 16.0);
    }

    private void InitializeDisplayInformation()
    {
        if (App.MainWindow is null)
        {
            return;
        }

        try
        {
            if (_displayInformation is null)
            {
                var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
                var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
                _displayInformation = DisplayInformation.CreateForWindowId(windowId);
            }

            if (!_isDisplayInformationEventAttached)
            {
                _displayInformation.AdvancedColorInfoChanged += DisplayInformation_AdvancedColorInfoChanged;
                _isDisplayInformationEventAttached = true;
            }
        }
        catch
        {
            _displayInformation = null;
            _isDisplayInformationEventAttached = false;
        }
    }

    private void RefreshRendererDisplayConfiguration()
    {
        _renderer.DisplayConfiguration = CreateDisplayConfiguration();
    }

    private HdrDisplayConfiguration CreateDisplayConfiguration()
    {
        if (_displayInformation is null)
        {
            return HdrDisplayConfiguration.Unknown;
        }

        try
        {
            var advancedColorInfo = _displayInformation.GetAdvancedColorInfo();
            var kind = advancedColorInfo.CurrentAdvancedColorKind;
            var sdrWhite = advancedColorInfo.SdrWhiteLevelInNits > 0.0
                ? advancedColorInfo.SdrWhiteLevelInNits
                : 80.0;
            var systemSdrWhite = sdrWhite;
            var hasSdrWhiteOverride = SdrWhiteOverrideToggle?.IsOn == true && SdrWhiteSlider is not null;
            if (hasSdrWhiteOverride)
            {
                sdrWhite = Math.Clamp(SdrWhiteSlider!.Value, 80.0, 800.0);
            }
            var advancedColorPeak = advancedColorInfo.MaxLuminanceInNits;
            var advancedColorFullFrame = advancedColorInfo.MaxAverageFullFrameLuminanceInNits;
            var peakLuminance = advancedColorPeak;
            var fullFrameLuminance = advancedColorFullFrame;
            var details = "Windows App SDK DisplayInformation";
            string? dxgiDisplayDeviceName = null;

            if (App.MainWindow is not null)
            {
                var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
                var dxgiPeak = TryGetDxgiMaxLuminanceForWindow(
                    hwnd,
                    out var dxgiFullFrame,
                    out var dxgiDetails,
                    out dxgiDisplayDeviceName);
                if (dxgiPeak is > 0.0)
                {
                    peakLuminance = dxgiPeak.Value;
                    fullFrameLuminance = dxgiFullFrame ?? fullFrameLuminance;
                    details = $"DXGI output luminance; {dxgiDetails}; AdvancedColor peak/full-frame {advancedColorPeak:0}/{advancedColorFullFrame:0} nits";
                }
            }

            if (kind == DisplayAdvancedColorKind.HighDynamicRange
                && EdidHdrMetadataReader.TryReadForDisplay(dxgiDisplayDeviceName, out var edidMetadata))
            {
                var edidSummary = edidMetadata.MaxFrameAverageLuminanceInNits is > 0.0
                    ? $"EDID HDR peak/full-frame {edidMetadata.MaxLuminanceInNits:0}/{edidMetadata.MaxFrameAverageLuminanceInNits.Value:0} nits from {edidMetadata.Source}"
                    : $"EDID HDR peak {edidMetadata.MaxLuminanceInNits:0} nits from {edidMetadata.Source}";

                if (ShouldUseEdidPeakFallback(
                    peakLuminance,
                    fullFrameLuminance,
                    sdrWhite,
                    edidMetadata.MaxLuminanceInNits))
                {
                    peakLuminance = edidMetadata.MaxLuminanceInNits;
                    fullFrameLuminance = edidMetadata.MaxFrameAverageLuminanceInNits ?? fullFrameLuminance;
                    details = $"{details}; using {edidSummary} because Windows peak is not reliable";
                }
                else
                {
                    details = $"{details}; {edidSummary}";
                }
            }

            if (hasSdrWhiteOverride)
            {
                details = $"{details}; app SDR white override {sdrWhite:0} nits (system {systemSdrWhite:0} nits)";
            }

            return new HdrDisplayConfiguration(
                kind.ToString(),
                kind == DisplayAdvancedColorKind.HighDynamicRange,
                advancedColorInfo.IsAdvancedColorKindAvailable(DisplayAdvancedColorKind.HighDynamicRange),
                sdrWhite,
                peakLuminance,
                fullFrameLuminance,
                details);
        }
        catch (Exception ex)
        {
            return HdrDisplayConfiguration.Unknown with
            {
                Details = $"Display HDR state unavailable: {ex.GetType().Name}"
            };
        }
    }

    private void UpdateSdrWhiteControls()
    {
        if (SdrWhiteOverrideToggle is null || SdrWhiteSlider is null || SdrWhiteValueText is null)
        {
            return;
        }

        var enabled = SdrWhiteOverrideToggle.IsOn;
        SdrWhiteSlider.IsEnabled = enabled;
        SdrWhiteSlider.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        SdrWhiteValueText.Text = enabled ? $"{SdrWhiteSlider.Value:0} nits" : "系统";
    }

    private Task ResizeRendererAsync()
    {
        var surfaceWidth = GetRenderSurfaceWidth();
        var surfaceHeight = GetRenderSurfaceHeight();
        return ResizeRendererAsync(surfaceWidth, surfaceHeight, _lifetime.Token);
    }

    private Task ResizeRendererAsync(double surfaceWidth, double surfaceHeight, CancellationToken cancellationToken = default)
    {
        var pixelWidth = Math.Max(1, (int)Math.Round(surfaceWidth * HdrSwapChainHost.CompositionScaleX));
        var pixelHeight = Math.Max(1, (int)Math.Round(surfaceHeight * HdrSwapChainHost.CompositionScaleY));
        return _renderer.ResizeAsync(pixelWidth, pixelHeight, cancellationToken == default ? _lifetime.Token : cancellationToken);
    }

    private bool UpdateImageSurfaceLayout()
    {
        if (!TryCalculateImageSurfaceTargetSize(out var targetWidth, out var targetHeight))
        {
            return false;
        }

        return ApplyImageSurfaceSize(targetWidth, targetHeight);
    }

    private bool TryCalculateImageSurfaceTargetSize(out double targetWidth, out double targetHeight)
    {
        UpdatePreviewSurfaceClip();
        var availableWidth = PreviewSurface.ActualWidth;
        var availableHeight = PreviewSurface.ActualHeight;
        targetWidth = availableWidth;
        targetHeight = availableHeight;
        if (availableWidth <= 0.0 || availableHeight <= 0.0)
        {
            return false;
        }

        if (_renderer.ContentDisplayAspectRatio is { } aspectRatio && aspectRatio > 0.0)
        {
            (targetWidth, targetHeight) = _isFillZoom
                ? CalculateFillSize(availableWidth, availableHeight, aspectRatio)
                : CalculateFitSize(availableWidth, availableHeight, aspectRatio);
        }

        if (!_isFitZoom && !_isFillZoom)
        {
            targetWidth *= _zoomScale;
            targetHeight *= _zoomScale;
        }

        return true;
    }

    private void UpdatePreviewSurfaceClip()
    {
        if (PreviewSurface.ActualWidth <= 0.0 || PreviewSurface.ActualHeight <= 0.0)
        {
            return;
        }

        PreviewSurface.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0.0, 0.0, PreviewSurface.ActualWidth, PreviewSurface.ActualHeight)
        };
    }

    private bool ApplyImageSurfaceSize(double width, double height)
    {
        var targetWidth = Math.Max(1.0, width);
        var targetHeight = Math.Max(1.0, height);
        var viewportWidth = Math.Max(PreviewSurface.ActualWidth, targetWidth);
        var viewportHeight = Math.Max(PreviewSurface.ActualHeight, targetHeight);
        var changed = Math.Abs(ImageSurface.Width - targetWidth) > 0.5
            || Math.Abs(ImageSurface.Height - targetHeight) > 0.5
            || double.IsNaN(ImageViewport.Width)
            || double.IsNaN(ImageViewport.Height)
            || Math.Abs(ImageViewport.Width - viewportWidth) > 0.5
            || Math.Abs(ImageViewport.Height - viewportHeight) > 0.5;
        ImageViewport.Width = viewportWidth;
        ImageViewport.Height = viewportHeight;
        ImageSurface.Width = targetWidth;
        ImageSurface.Height = targetHeight;
        HdrSwapChainHost.Width = ImageSurface.Width;
        HdrSwapChainHost.Height = ImageSurface.Height;
        FallbackImage.Width = ImageSurface.Width;
        FallbackImage.Height = ImageSurface.Height;
        ImageSurface.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0.0, 0.0, ImageSurface.Width, ImageSurface.Height)
        };
        ImageSurface.UpdateLayout();
        return changed;
    }

    private static (double Width, double Height) CalculateFitSize(double availableWidth, double availableHeight, double aspectRatio)
    {
        var availableAspectRatio = availableWidth / availableHeight;
        if (availableAspectRatio > aspectRatio)
        {
            var height = availableHeight;
            return (height * aspectRatio, height);
        }

        var width = availableWidth;
        return (width, width / aspectRatio);
    }

    private static (double Width, double Height) CalculateFillSize(double availableWidth, double availableHeight, double aspectRatio)
    {
        var availableAspectRatio = availableWidth / availableHeight;
        if (availableAspectRatio > aspectRatio)
        {
            var width = availableWidth;
            return (width, width / aspectRatio);
        }

        var height = availableHeight;
        return (height * aspectRatio, height);
    }

    private double CalculateActualSizeZoomScale()
    {
        var availableWidth = PreviewSurface.ActualWidth;
        var availableHeight = PreviewSurface.ActualHeight;
        var aspectRatio = _renderer.ContentDisplayAspectRatio;
        if (availableWidth <= 0.0 || availableHeight <= 0.0 || aspectRatio is null or <= 0.0)
        {
            return 1.0;
        }

        var (fitWidth, fitHeight) = CalculateFitSize(availableWidth, availableHeight, aspectRatio.Value);
        var contentWidth = _renderer.ContentPixelWidth;
        var contentHeight = _renderer.ContentPixelHeight;
        if (contentWidth <= 0 || contentHeight <= 0)
        {
            return 1.0;
        }

        if (Math.Abs(_renderer.ContentOrientation % 180.0f) is > 45.0f and < 135.0f)
        {
            (contentWidth, contentHeight) = (contentHeight, contentWidth);
        }

        var scaleX = HdrSwapChainHost.CompositionScaleX > 0.0
            ? contentWidth / Math.Max(fitWidth * HdrSwapChainHost.CompositionScaleX, 1.0)
            : contentWidth / Math.Max(fitWidth, 1.0);
        var scaleY = HdrSwapChainHost.CompositionScaleY > 0.0
            ? contentHeight / Math.Max(fitHeight * HdrSwapChainHost.CompositionScaleY, 1.0)
            : contentHeight / Math.Max(fitHeight, 1.0);
        return Math.Clamp(Math.Min(scaleX, scaleY), 0.25, 4.0);
    }

    private void CenterScrollableImage()
    {
        if (ImageScroller is null || ImageViewport is null)
        {
            return;
        }

        var horizontalOffset = Math.Max(0.0, (ImageViewport.Width - PreviewSurface.ActualWidth) / 2.0);
        var verticalOffset = Math.Max(0.0, (ImageViewport.Height - PreviewSurface.ActualHeight) / 2.0);
        ImageScroller.ChangeView(horizontalOffset, verticalOffset, null, true);
    }

    private bool TryCaptureViewportAnchor(
        out double anchorX,
        out double anchorY,
        out double anchorViewportX,
        out double anchorViewportY)
    {
        return TryCaptureViewportPointAnchor(
            new Windows.Foundation.Point(
                PreviewSurface.ActualWidth / 2.0,
                PreviewSurface.ActualHeight / 2.0),
            out anchorX,
            out anchorY,
            out anchorViewportX,
            out anchorViewportY);
    }

    private void SetPendingZoomAnchorToViewportCenter()
    {
        if (PreviewSurface.ActualWidth <= 1.0 || PreviewSurface.ActualHeight <= 1.0)
        {
            ClearPendingZoomAnchor();
            return;
        }

        SetPendingZoomAnchor(new Windows.Foundation.Point(
            PreviewSurface.ActualWidth / 2.0,
            PreviewSurface.ActualHeight / 2.0));
    }

    private void SetPendingZoomAnchor(Windows.Foundation.Point pointInPreview)
    {
        if (!TryCaptureViewportPointAnchor(pointInPreview, out var anchorX, out var anchorY, out var anchorViewportX, out var anchorViewportY))
        {
            ClearPendingZoomAnchor();
            return;
        }

        _hasPendingZoomAnchor = true;
        _pendingZoomAnchorX = anchorX;
        _pendingZoomAnchorY = anchorY;
        _pendingZoomViewportX = anchorViewportX;
        _pendingZoomViewportY = anchorViewportY;
    }

    private bool TryCaptureViewportPointAnchor(
        Windows.Foundation.Point pointInPreview,
        out double anchorX,
        out double anchorY,
        out double anchorViewportX,
        out double anchorViewportY)
    {
        anchorX = 0.5;
        anchorY = 0.5;
        anchorViewportX = Math.Clamp(pointInPreview.X, 0.0, Math.Max(0.0, PreviewSurface.ActualWidth));
        anchorViewportY = Math.Clamp(pointInPreview.Y, 0.0, Math.Max(0.0, PreviewSurface.ActualHeight));
        if (ImageScroller is null
            || ImageViewport is null
            || ImageSurface.Width <= 1.0
            || ImageSurface.Height <= 1.0)
        {
            return false;
        }

        var imageLeft = Math.Max(0.0, (ImageViewport.Width - ImageSurface.Width) / 2.0);
        var imageTop = Math.Max(0.0, (ImageViewport.Height - ImageSurface.Height) / 2.0);
        var contentX = ImageScroller.HorizontalOffset + anchorViewportX;
        var contentY = ImageScroller.VerticalOffset + anchorViewportY;
        var layoutX = (contentX - imageLeft) / ImageSurface.Width;
        var layoutY = (contentY - imageTop) / ImageSurface.Height;
        var scaleX = Math.Abs(ImageInteractionScaleTransform.ScaleX) > 0.0001
            ? ImageInteractionScaleTransform.ScaleX
            : 1.0;
        var scaleY = Math.Abs(ImageInteractionScaleTransform.ScaleY) > 0.0001
            ? ImageInteractionScaleTransform.ScaleY
            : 1.0;
        var origin = ImageSurface.RenderTransformOrigin;
        anchorX = Math.Clamp(origin.X + ((layoutX - origin.X) / scaleX), 0.0, 1.0);
        anchorY = Math.Clamp(origin.Y + ((layoutY - origin.Y) / scaleY), 0.0, 1.0);
        return true;
    }

    private bool TryConsumePendingZoomAnchor(
        out double anchorX,
        out double anchorY,
        out double anchorViewportX,
        out double anchorViewportY)
    {
        anchorX = _pendingZoomAnchorX;
        anchorY = _pendingZoomAnchorY;
        anchorViewportX = _pendingZoomViewportX;
        anchorViewportY = _pendingZoomViewportY;
        if (!_hasPendingZoomAnchor)
        {
            return false;
        }

        _hasPendingZoomAnchor = false;
        return true;
    }

    private void ClearPendingZoomAnchor()
    {
        _hasPendingZoomAnchor = false;
        _pendingZoomAnchorX = 0.5;
        _pendingZoomAnchorY = 0.5;
        _pendingZoomViewportX = 0.5;
        _pendingZoomViewportY = 0.5;
    }

    private void NormalizeAnchorForTargetSize(ref double anchorX, ref double anchorY, double targetWidth, double targetHeight)
    {
        var horizontalBlend = CalculatePointerAnchorBlend(targetWidth, PreviewSurface.ActualWidth);
        var verticalBlend = CalculatePointerAnchorBlend(targetHeight, PreviewSurface.ActualHeight);
        anchorX = Lerp(0.5, anchorX, horizontalBlend);
        anchorY = Lerp(0.5, anchorY, verticalBlend);
    }

    private void NormalizeAnchorForTargetSize(
        ref double anchorX,
        ref double anchorY,
        ref double anchorViewportX,
        ref double anchorViewportY,
        double targetWidth,
        double targetHeight)
    {
        var horizontalBlend = CalculatePointerAnchorBlend(targetWidth, PreviewSurface.ActualWidth);
        var verticalBlend = CalculatePointerAnchorBlend(targetHeight, PreviewSurface.ActualHeight);
        anchorX = Lerp(0.5, anchorX, horizontalBlend);
        anchorY = Lerp(0.5, anchorY, verticalBlend);
        anchorViewportX = Lerp(PreviewSurface.ActualWidth / 2.0, anchorViewportX, horizontalBlend);
        anchorViewportY = Lerp(PreviewSurface.ActualHeight / 2.0, anchorViewportY, verticalBlend);
    }

    private static double CalculatePointerAnchorBlend(double targetSize, double viewportSize)
    {
        if (targetSize <= 0.0 || viewportSize <= 1.0)
        {
            return 0.0;
        }

        var transition = Math.Clamp(viewportSize * 0.18, 96.0, 220.0);
        var t = Math.Clamp((targetSize - viewportSize) / transition, 0.0, 1.0);
        return t * t * (3.0 - (2.0 * t));
    }

    private static double Lerp(double from, double to, double amount)
    {
        return from + ((to - from) * amount);
    }

    private void RestoreViewportAnchor(double anchorX, double anchorY, double anchorViewportX, double anchorViewportY)
    {
        if (ImageScroller is null || ImageViewport is null)
        {
            return;
        }

        var imageLeft = Math.Max(0.0, (ImageViewport.Width - ImageSurface.Width) / 2.0);
        var imageTop = Math.Max(0.0, (ImageViewport.Height - ImageSurface.Height) / 2.0);
        var viewportX = Math.Clamp(anchorViewportX, 0.0, Math.Max(0.0, PreviewSurface.ActualWidth));
        var viewportY = Math.Clamp(anchorViewportY, 0.0, Math.Max(0.0, PreviewSurface.ActualHeight));
        var targetOffsetX = imageLeft + (ImageSurface.Width * Math.Clamp(anchorX, 0.0, 1.0)) - viewportX;
        var targetOffsetY = imageTop + (ImageSurface.Height * Math.Clamp(anchorY, 0.0, 1.0)) - viewportY;
        targetOffsetX = Math.Clamp(targetOffsetX, 0.0, ImageScroller.ScrollableWidth);
        targetOffsetY = Math.Clamp(targetOffsetY, 0.0, ImageScroller.ScrollableHeight);
        ImageScroller.ChangeView(targetOffsetX, targetOffsetY, null, disableAnimation: true);
    }

    private void QueueAdjacentPreloads()
    {
        CancelAndDispose(ref _preloadCts);
        if (!_settings.PreloadAdjacentImages || _folderImagePaths.Count == 0 || _currentFolderIndex < 0)
        {
            SetPreloadCacheScope(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            TrimImagePreloadCache();
            return;
        }

        var keepPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            _folderImagePaths[_currentFolderIndex],
        };
        var priorityPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            _folderImagePaths[_currentFolderIndex],
        };

        var preloadPaths = new List<string>();
        var hotPreloadPaths = new List<string>();
        for (var offset = 1; offset <= AdjacentPreloadRadius; offset++)
        {
            var isHot = offset == 1;
            AddPreloadPath(_currentFolderIndex + offset, isHot);
            AddPreloadPath(_currentFolderIndex - offset, isHot);
        }

        SetPreloadCacheScope(keepPaths, priorityPaths);
        TrimImagePreloadCache();

        if (preloadPaths.Count == 0)
        {
            return;
        }

        _preloadCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _preloadCts.Token;
        _ = Task.Run(async () =>
        {
            if (hotPreloadPaths.Count > 0)
            {
                try
                {
                    await Task.WhenAll(hotPreloadPaths.Select(PreloadOneAsync));
                    TrimImagePreloadCache();
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
                    await ImagePreloadCache.PreloadAsync(path, token);
                    TrimImagePreloadCache();
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
                await ImagePreloadCache.PreloadAsync(path, token);
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
            if (index < 0 || index >= _folderImagePaths.Count)
            {
                return;
            }

            var path = _folderImagePaths[index];
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

    private void SetPreloadCacheScope(HashSet<string> keepPaths, HashSet<string> decodedPriorityPaths)
    {
        lock (_preloadCacheGate)
        {
            _preloadCacheKeepPaths = new HashSet<string>(keepPaths, StringComparer.OrdinalIgnoreCase);
            _preloadCacheDecodedPriorityPaths = new HashSet<string>(decodedPriorityPaths, StringComparer.OrdinalIgnoreCase);
        }
    }

    private void TrimImagePreloadCache()
    {
        HashSet<string> keepPaths;
        HashSet<string> decodedPriorityPaths;
        lock (_preloadCacheGate)
        {
            keepPaths = new HashSet<string>(_preloadCacheKeepPaths, StringComparer.OrdinalIgnoreCase);
            decodedPriorityPaths = new HashSet<string>(_preloadCacheDecodedPriorityPaths, StringComparer.OrdinalIgnoreCase);
        }

        ImagePreloadCache.KeepOnly(keepPaths, decodedPriorityPaths);
    }

    private static void CancelAndDispose(ref CancellationTokenSource? cancellationTokenSource)
    {
        var source = cancellationTokenSource;
        cancellationTokenSource = null;
        if (source is null)
        {
            return;
        }

        try
        {
            source.Cancel();
        }
        finally
        {
            source.Dispose();
        }
    }

    private double GetRenderSurfaceWidth()
    {
        return ImageSurface.Width is > 0.0 ? ImageSurface.Width : HdrSwapChainHost.ActualWidth;
    }

    private double GetRenderSurfaceHeight()
    {
        return ImageSurface.Height is > 0.0 ? ImageSurface.Height : HdrSwapChainHost.ActualHeight;
    }

    private static bool ShouldUseEdidPeakFallback(
        double reportedPeakLuminance,
        double reportedFullFrameLuminance,
        double sdrWhiteLuminance,
        double edidPeakLuminance)
    {
        if (edidPeakLuminance <= 0.0)
        {
            return false;
        }

        if (reportedPeakLuminance <= 0.0 || reportedPeakLuminance <= sdrWhiteLuminance)
        {
            return true;
        }

        var reportedPeakLooksLikeFullFrame = reportedFullFrameLuminance > 0.0
            && Math.Abs(reportedPeakLuminance - reportedFullFrameLuminance) <= Math.Max(1.0, reportedPeakLuminance * 0.02);

        return reportedPeakLooksLikeFullFrame && edidPeakLuminance > reportedPeakLuminance * 1.25;
    }

    private static double? TryGetDxgiMaxLuminanceForWindow(
        IntPtr hwnd,
        out double? fullFrameLuminance,
        out string details,
        out string? displayDeviceName)
    {
        fullFrameLuminance = null;
        details = "DXGI output unavailable";
        displayDeviceName = null;
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            double? firstAttachedPeak = null;
            double? firstAttachedFullFrame = null;
            string? firstAttachedDetails = null;
            string? firstAttachedDisplayDeviceName = null;

            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                var adapterResult = factory.EnumAdapters1(adapterIndex, out var adapter);
                if (adapterResult.Failure)
                {
                    break;
                }

                using (adapter)
                {
                    for (uint outputIndex = 0; ; outputIndex++)
                    {
                        var outputResult = adapter.EnumOutputs(outputIndex, out var output);
                        if (outputResult.Failure)
                        {
                            break;
                        }

                        using (output)
                        using (var output6 = output.QueryInterfaceOrNull<IDXGIOutput6>())
                        {
                            if (output6 is null)
                            {
                                continue;
                            }

                            var description = output6.Description1;
                            if (!description.AttachedToDesktop || description.MaxLuminance <= 0.0f)
                            {
                                continue;
                            }

                            var outputDetails = $"{description.DeviceName} max {description.MaxLuminance:0} nits full-frame {description.MaxFullFrameLuminance:0} nits";
                            firstAttachedPeak ??= description.MaxLuminance;
                            firstAttachedFullFrame ??= description.MaxFullFrameLuminance;
                            firstAttachedDetails ??= outputDetails;
                            firstAttachedDisplayDeviceName ??= description.DeviceName;
                            if (description.Monitor == monitor)
                            {
                                fullFrameLuminance = description.MaxFullFrameLuminance;
                                details = outputDetails;
                                displayDeviceName = description.DeviceName;
                                return description.MaxLuminance;
                            }
                        }
                    }
                }
            }

            if (firstAttachedPeak is not null)
            {
                fullFrameLuminance = firstAttachedFullFrame;
                details = $"{firstAttachedDetails}; monitor handle match unavailable";
                displayDeviceName = firstAttachedDisplayDeviceName;
                return firstAttachedPeak;
            }
        }
        catch (Exception ex)
        {
            details = $"DXGI output unavailable: {ex.GetType().Name}";
        }

        return null;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
}



