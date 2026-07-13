using HdrImageViewer.Infrastructure;
using HdrImageViewer.Rendering;
using HdrImageViewer.Models;
using HdrImageViewer.Presentation;
using HdrImageViewer.Services;
using HdrImageViewer.ViewModels;
using Microsoft.Graphics.Display;
using Microsoft.UI;
using Microsoft.Windows.Storage.Pickers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media.Animation;
using SharpGen.Runtime;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Vortice.DXGI;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using WinRT.Interop;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace HdrImageViewer.Pages;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "WinUI pages are never disposed by the framework; the disposable fields are cleaned up in HomePage_Unloaded, and _renderer/_lifetime intentionally live for the app's lifetime.")]
public sealed partial class HomePage : Page
{
    private const uint MonitorDefaultToNearest = 2;
    private const int ZoomRenderDebounceMilliseconds = 60;
    private const int ZoomAnimationFrameMilliseconds = 16;
    private const int ViewerChromeAutoHideMilliseconds = 2200;
    private const int ViewerChromeAnimationMilliseconds = 160;
    private const int InspectorAnimationMilliseconds = 180;
    private const double InspectorAnimationOffset = 24.0;
    private const double ZoomAnimationCatchUp = 0.58;
    private const double MinCropWidth = 96.0;
    private const double MinCropHeight = 72.0;
    private const double InspectorPanelWidth = 352.0;
    private const double InspectorMinimumWindowWidth = 920.0;
    private const double ViewerChromeHorizontalInset = 32.0;
    private const double ViewerChromeMaxWidth = 1180.0;
    private const double CompactViewerChromeBreakpoint = 860.0;
    private const double FilmstripItemWidth = 68.0;
    private const double ToolbarReservedWidth = 640.0;

    private static readonly TimeSpan MemoryTrimDebounceDelay = TimeSpan.FromSeconds(2.5);
    private static readonly Timer s_memoryTrimTimer = new(_ => TrimImageLoadMemory(), null, Timeout.Infinite, Timeout.Infinite);

    private readonly D3D11HdrRenderPipeline _renderer = new();
    private readonly CancellationTokenSource _lifetime = new();
    private DisplayInformation? _displayInformation;
    private HdrImageDocument? _currentDocument;
    private List<string> _folderImagePaths = [];
    private int _currentFolderIndex = -1;
    private FilmstripImageItem? _currentFilmstripItem;
    private string? _lastFolderListDirectory;
    private long _lastFolderListRefreshTicks;
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
    private CancellationTokenSource? _folderRefreshCts;
    private CancellationTokenSource? _zoomRenderCts;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _zoomAnimationTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _viewerChromeHideTimer;
    private Storyboard? _inspectorStoryboard;
    private bool _isViewerChromeVisible = true;
    private bool _hasAppliedInspectorLayout;
    private bool _inspectorTargetVisible = true;
    private bool _hasZoomAnimationAnchor;
    private double _zoomAnimationAnchorX = 0.5;
    private double _zoomAnimationAnchorY = 0.5;
    private double _zoomAnimationViewportX = 0.5;
    private double _zoomAnimationViewportY = 0.5;
    private readonly ImagePreloadController _imagePreloads;
    private readonly FilmstripThumbnailController _filmstripThumbnails;
    private readonly ImageLoadController _imageLoads;
    private AppUserSettings _settings = AppSettingsService.Current;
    private bool _updatingHdrModeControls;
    private bool _isImmersiveEventAttached;
    private bool _isDisplayInformationEventAttached;
    private bool _isSettingsEventAttached;
    private bool _isExportInProgress;
    private bool _useXamlFallbackLayoutAspectRatio;
    private double? _xamlFallbackDisplayAspectRatio;
    private MediaPlayer? _livePhotoMediaPlayer;
    private bool _isCompanionMediaPlaybackActive;

    public ImageWorkspaceViewModel ViewModel { get; } = new();

    public RangeObservableCollection<FilmstripImageItem> FilmstripItems { get; } = [];

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static string CompanionMuteGlyph(bool muted) =>
        muted ? "\uE74F" : "\uE767";

    private enum CropExportMode
    {
        SdrPreview,
        GainMapPreserve,
        UltraHdrConvert,
        SingleLayerHdr,
    }

    private enum SaveAsExportMode
    {
        SingleLayerHdr,
        UltraHdrConvert,
        GainMapPreserve,
    }

    public HomePage()
    {
        InitializeComponent();
        InitializeLivePhotoPlayer();
        _filmstripThumbnails = new FilmstripThumbnailController(FilmstripItems, _lifetime.Token);
        _imagePreloads = new ImagePreloadController(_lifetime.Token);
        _imageLoads = new ImageLoadController(_lifetime.Token);
        RegisterKeyboardAccelerators();
        _zoomAnimationTimer = DispatcherQueue.CreateTimer();
        _zoomAnimationTimer.Interval = TimeSpan.FromMilliseconds(ZoomAnimationFrameMilliseconds);
        _zoomAnimationTimer.Tick += ZoomAnimationTimer_Tick;
        _viewerChromeHideTimer = DispatcherQueue.CreateTimer();
        _viewerChromeHideTimer.Interval = TimeSpan.FromMilliseconds(ViewerChromeAutoHideMilliseconds);
        _viewerChromeHideTimer.Tick += ViewerChromeHideTimer_Tick;
        UpdateFolderNavigationOverlay();
        UpdateZoomControls();
        Loaded += HomePage_Loaded;
        Unloaded += HomePage_Unloaded;
    }

    private void InitializeLivePhotoPlayer()
    {
        _livePhotoMediaPlayer = new MediaPlayer
        {
            IsMuted = true,
        };
        _livePhotoMediaPlayer.MediaOpened += LivePhotoMediaPlayer_MediaOpened;
        _livePhotoMediaPlayer.MediaEnded += LivePhotoMediaPlayer_MediaEnded;
        _livePhotoMediaPlayer.MediaFailed += LivePhotoMediaPlayer_MediaFailed;
        LivePhotoPlayer.SetMediaPlayer(_livePhotoMediaPlayer);
    }

    private void RegisterKeyboardAccelerators()
    {
        AddKeyboardAccelerator(VirtualKey.Left, async () => await NavigateFolderImageAsync(-1));
        AddKeyboardAccelerator(VirtualKey.Right, async () => await NavigateFolderImageAsync(1));
    }

    private void AddKeyboardAccelerator(VirtualKey key, Func<Task> action)
    {
        var accelerator = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = key,
        };
        accelerator.Invoked += async (_, args) =>
        {
            args.Handled = true;
            await action();
        };
        KeyboardAccelerators.Add(accelerator);
    }

    private async void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            InitializeDisplayInformation();
            AttachSettingsChanged();
            _settings = AppSettingsService.Current;
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

            ApplyViewerSettings();

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
        StopCompanionMediaPlayback(resetSource: true);
        DetachSettingsChanged();
        _zoomAnimationTimer?.Stop();

        if (_displayInformation is not null && _isDisplayInformationEventAttached)
        {
            _displayInformation.AdvancedColorInfoChanged -= DisplayInformation_AdvancedColorInfoChanged;
            _isDisplayInformationEventAttached = false;
        }

        _imagePreloads.Dispose();
        _imageLoads.Dispose();
        CancelAndDispose(ref _folderRefreshCts);
        _filmstripThumbnails.Dispose();
        CancelAndDispose(ref _zoomRenderCts);
        _viewerChromeHideTimer?.Stop();
        if (App.MainWindow is MainWindow mainWindow)
        {
            mainWindow.ImmersiveViewingChanged -= MainWindow_ImmersiveViewingChanged;
            _isImmersiveEventAttached = false;
        }

        if (_livePhotoMediaPlayer is not null)
        {
            _livePhotoMediaPlayer.MediaOpened -= LivePhotoMediaPlayer_MediaOpened;
            _livePhotoMediaPlayer.MediaEnded -= LivePhotoMediaPlayer_MediaEnded;
            _livePhotoMediaPlayer.MediaFailed -= LivePhotoMediaPlayer_MediaFailed;
            _livePhotoMediaPlayer.Dispose();
            _livePhotoMediaPlayer = null;
        }
    }

    private void HomeRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyInspectorLayout();
        UpdateFilmstripChromeLayout();
    }

    private void MainWindow_ImmersiveViewingChanged(object? sender, bool isImmersive)
    {
        ApplyImmersiveViewingState(isImmersive);
    }

    private void ApplyImmersiveViewingState(bool isImmersive)
    {
        ApplyInspectorLayout(isImmersive);
        FullScreenIcon.Glyph = isImmersive ? "\uE73F" : "\uE740";
        TopFullScreenIcon.Glyph = isImmersive ? "\uE73F" : "\uE740";
        ToolTipService.SetToolTip(FullScreenButton, isImmersive ? "退出全屏" : "全屏");
        ToolTipService.SetToolTip(TopFullScreenButton, isImmersive ? "退出全屏" : "全屏");
        ShowViewerChromeTemporarily();
        UpdateFilmstripChromeLayout();
        _ = ApplyViewportResizeAsync();
    }

    private void ApplyInspectorLayout(bool? immersiveOverride = null)
    {
        if (InspectorPanel is null || InspectorColumn is null || InspectorPanelTransform is null)
        {
            return;
        }

        var isImmersive = immersiveOverride
            ?? (App.MainWindow is MainWindow mainWindow && mainWindow.IsImmersiveViewing);
        var hasInspectorWidth = HomeRoot.ActualWidth >= InspectorMinimumWindowWidth;
        var showInspector = _settings.ShowInspectorPanel && !isImmersive && hasInspectorWidth;
        if (TopInspectorToggleButton is not null)
        {
            TopInspectorToggleButton.IsChecked = _settings.ShowInspectorPanel;
            TopInspectorToggleButton.Visibility = isImmersive || !hasInspectorWidth
                ? Visibility.Collapsed
                : Visibility.Visible;
            ToolTipService.SetToolTip(
                TopInspectorToggleButton,
                _settings.ShowInspectorPanel ? "隐藏详情栏 (I)" : "显示详情栏 (I)");
        }

        var shouldAnimate = _hasAppliedInspectorLayout
            && new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        _hasAppliedInspectorLayout = true;
        if (!shouldAnimate)
        {
            SetInspectorLayoutImmediate(showInspector);
            return;
        }

        AnimateInspectorLayout(showInspector);
    }

    private void SetInspectorLayoutImmediate(bool showInspector)
    {
        _inspectorStoryboard?.Stop();
        _inspectorStoryboard = null;
        _inspectorTargetVisible = showInspector;
        InspectorColumn.Width = showInspector ? new GridLength(InspectorPanelWidth) : new GridLength(0);
        InspectorPanel.Visibility = showInspector ? Visibility.Visible : Visibility.Collapsed;
        InspectorPanel.Opacity = 1.0;
        InspectorPanelTransform.X = 0.0;
    }

    private void AnimateInspectorLayout(bool showInspector)
    {
        if (_inspectorTargetVisible == showInspector && _inspectorStoryboard is not null)
        {
            return;
        }

        _inspectorStoryboard?.Stop();
        _inspectorStoryboard = null;
        _inspectorTargetVisible = showInspector;

        if (showInspector)
        {
            InspectorColumn.Width = new GridLength(InspectorPanelWidth);
            InspectorPanel.Visibility = Visibility.Visible;
            InspectorPanel.Opacity = 1.0;
            InspectorPanelTransform.X = 0.0;
        }
        else if (InspectorPanel.Visibility != Visibility.Visible)
        {
            SetInspectorLayoutImmediate(showInspector: false);
            return;
        }

        var storyboard = new Storyboard();
        var duration = new Duration(TimeSpan.FromMilliseconds(InspectorAnimationMilliseconds));
        var easing = new CubicEase
        {
            EasingMode = showInspector ? EasingMode.EaseOut : EasingMode.EaseIn,
        };
        AddDoubleAnimation(
            storyboard,
            InspectorPanel,
            "Opacity",
            showInspector ? 0.0 : 1.0,
            showInspector ? 1.0 : 0.0,
            duration,
            easing);
        AddDoubleAnimation(
            storyboard,
            InspectorPanelTransform,
            "X",
            showInspector ? InspectorAnimationOffset : 0.0,
            showInspector ? 0.0 : InspectorAnimationOffset,
            duration,
            easing);
        storyboard.Completed += (_, _) =>
        {
            storyboard.Stop();
            if (_inspectorTargetVisible != showInspector)
            {
                return;
            }

            _inspectorStoryboard = null;
            if (showInspector)
            {
                InspectorPanel.Opacity = 1.0;
                InspectorPanelTransform.X = 0.0;
            }
            else
            {
                InspectorPanel.Visibility = Visibility.Collapsed;
                InspectorColumn.Width = new GridLength(0);
                InspectorPanel.Opacity = 1.0;
                InspectorPanelTransform.X = 0.0;
            }
        };
        _inspectorStoryboard = storyboard;
        storyboard.Begin();
    }

    private void ViewerChromeHideTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (ViewModel.HasImage && !_isCropModeEnabled)
        {
            SetViewerChromeVisible(false, animate: true);
        }
    }

    private void ShowViewerChromeTemporarily()
    {
        if (!ViewModel.HasImage || _isCropModeEnabled)
        {
            _viewerChromeHideTimer?.Stop();
            SetViewerChromeVisible(true, animate: false);
            return;
        }

        SetViewerChromeVisible(true, animate: true);
        _viewerChromeHideTimer?.Stop();
        _viewerChromeHideTimer?.Start();
    }

    private void SetViewerChromeVisible(bool visible, bool animate)
    {
        if (PhotoToolbarOverlay is null || PhotoToolbarOverlayTransform is null)
        {
            return;
        }

        if (_isViewerChromeVisible == visible && PhotoToolbarOverlay.Visibility == Visibility.Visible)
        {
            return;
        }

        _isViewerChromeVisible = visible;
        if (!animate)
        {
            PhotoToolbarOverlay.Opacity = visible ? 1.0 : 0.0;
            PhotoToolbarOverlayTransform.Y = visible ? 0.0 : 18.0;
            PhotoToolbarOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (visible)
        {
            PhotoToolbarOverlay.Visibility = Visibility.Visible;
        }

        var storyboard = new Storyboard();
        var duration = new Duration(TimeSpan.FromMilliseconds(ViewerChromeAnimationMilliseconds));
        AddDoubleAnimation(storyboard, PhotoToolbarOverlay, "Opacity", visible ? 1.0 : 0.0, duration);
        AddDoubleAnimation(storyboard, PhotoToolbarOverlayTransform, "Y", visible ? 0.0 : 18.0, duration);
        if (!visible)
        {
            storyboard.Completed += (_, _) =>
            {
                if (!_isViewerChromeVisible)
                {
                    PhotoToolbarOverlay.Visibility = Visibility.Collapsed;
                }
            };
        }

        storyboard.Begin();
    }

    private static void AddDoubleAnimation(
        Storyboard storyboard,
        DependencyObject target,
        string path,
        double to,
        Duration duration)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = duration,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, path);
        storyboard.Children.Add(animation);
    }

    private static void AddDoubleAnimation(
        Storyboard storyboard,
        DependencyObject target,
        string path,
        double from,
        double to,
        Duration duration,
        EasingFunctionBase? easingFunction = null)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EnableDependentAnimation = true,
            EasingFunction = easingFunction,
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, path);
        storyboard.Children.Add(animation);
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
        var picker = new FileOpenPicker(GetMainWindowId())
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.Thumbnail,
        };

        foreach (var fileType in DecoderCatalog.FileTypeFilter)
        {
            picker.FileTypeFilter.Add(fileType);
        }

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        await LoadImagePathAsync(file.Path, invalidateRendererCache: true);
    }

    private static WindowId GetMainWindowId()
    {
        return App.MainWindow?.AppWindow.Id
            ?? throw new InvalidOperationException("主窗口尚未初始化，无法显示文件选择器。");
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

    public async Task OpenImagePathsAsync(IReadOnlyList<string> paths)
    {
        var supportedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path) && IsSupportedImagePath(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (supportedPaths.Count == 0)
        {
            ViewModel.UpdateRenderStatus("系统打开请求未处理: 没有识别到支持的图片文件。");
            return;
        }

        await LoadDroppedImagePathsAsync(supportedPaths);
        if (supportedPaths.Count == 1)
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 已从系统文件关联打开");
        }
        else
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; 已从系统文件关联打开 {supportedPaths.Count} 张图片");
        }
    }

    private async void ReloadImage_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ViewModel.FilePath))
        {
            return;
        }

        await LoadImagePathAsync(ViewModel.FilePath, invalidateRendererCache: true);
    }

    private async Task ShowSdrFallbackImageAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            // Packaged WinUI 3 apps cannot reliably load arbitrary file:// paths
            // through BitmapImage.UriSource (it loads asynchronously and fails
            // silently rather than throwing), so stream the file in explicitly.
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            cancellationToken.ThrowIfCancellationRequested();
            var bitmap = new BitmapImage
            {
                DecodePixelWidth = CalculateViewerPreloadMaxPixelSize(),
            };
            await bitmap.SetSourceAsync(stream);
            SetXamlFallbackDisplayAspectRatio(bitmap.PixelWidth, bitmap.PixelHeight);
            FallbackImage.Source = bitmap;
            ShowFallbackImageLayer();
            UpdateImageSurfaceLayout();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Last resort: keep the URI-based attempt so behaviour never
            // regresses on hosts where the stream path is unavailable.
            FallbackImage.Source = new BitmapImage
            {
                DecodePixelWidth = CalculateViewerPreloadMaxPixelSize(),
                UriSource = new Uri(path),
            };
            ShowFallbackImageLayer();
            UpdateImageSurfaceLayout();
        }
    }

    private void SetXamlFallbackDisplayAspectRatio(int pixelWidth, int pixelHeight)
    {
        if (pixelWidth > 0 && pixelHeight > 0)
        {
            _xamlFallbackDisplayAspectRatio = (double)pixelWidth / pixelHeight;
        }
    }

    private void ShowFallbackImageLayer()
    {
        _useXamlFallbackLayoutAspectRatio = true;
        _renderer.DetachSwapChainForXamlFallback();
        HdrSwapChainHost.Visibility = Visibility.Collapsed;
        FallbackImage.Visibility = Visibility.Visible;
    }

    private void HideFallbackImageLayer()
    {
        _useXamlFallbackLayoutAspectRatio = false;
        _xamlFallbackDisplayAspectRatio = null;
        FallbackImage.Source = null;
        FallbackImage.Visibility = Visibility.Collapsed;
        HdrSwapChainHost.Visibility = Visibility.Visible;
        _renderer.RestoreSwapChainPanelBinding();
    }

    private static bool ShouldUseXamlColorManagedImage(HdrImageDocument document)
    {
        // Wide-gamut (Display P3 / BT.2020) SDR images must go through the D3D
        // renderer: the decoder applies an ICC -> sRGB transform for them and
        // the base-image shader presents the result. The WinUI Image control
        // does not colour-manage ICC-tagged PNGs, so routing them here would
        // show the raw, mis-interpreted wide-gamut pixels instead.
        if (document.WicImageProbe?.UsesDisplayP3Primaries == true
            || document.WicImageProbe?.UsesBt2020Primaries == true)
        {
            return false;
        }

        if (document.JxlProbe?.IsJxl == true)
        {
            return false;
        }

        return !document.HasRenderableGainMap
            && document.HeifAvifProbe?.HasHdrTransfer != true
            && document.Format.Kind != HdrImageKind.SingleLayerHdr;
    }

    private async Task LoadImagePathAsync(
        string path,
        bool invalidateRendererCache,
        IReadOnlyList<string>? explicitNavigationPaths = null)
    {
        var imageLoad = _imageLoads.Begin();
        var cancellationToken = imageLoad.Token;
        _zoomRenderCts?.Cancel();
        CancelAndDispose(ref _folderRefreshCts);
        StopCompanionMediaPlayback(resetSource: true);
        ImageSurface.Visibility = Visibility.Visible;
        HideFallbackImageLayer();

        var renderStatus = string.Empty;
        try
        {
            ViewModel.BeginFileLoad();
            var openTimer = Stopwatch.StartNew();
            RefreshRendererDisplayConfiguration();
            var probeTimer = Stopwatch.StartNew();
            var loadResult = await ImageWorkspaceViewModel.LoadFileAsync(path, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_imageLoads.IsCurrent(imageLoad))
            {
                return;
            }

            ViewModel.ApplyLoadResult(loadResult);
            var document = loadResult.Document;
            _currentDocument = document;
            await UpdateHdrModeControlsForDocumentAsync(document, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (explicitNavigationPaths is not null)
            {
                _currentNavigationIsExplicit = true;
                SetExplicitImageList(explicitNavigationPaths, path);
            }
            else
            {
                _currentNavigationIsExplicit = false;
                SelectCurrentFolderImage(path);
            }
            ResetZoomToFit();
            ResetInteractionScaleTransform();
            probeTimer.Stop();
            var useXamlColorManagedImage = ShouldUseXamlColorManagedImage(document);
            if (!useXamlColorManagedImage)
            {
                HideFallbackImageLayer();
            }

            var resizeTimer = Stopwatch.StartNew();
            UpdateImageSurfaceLayout();
            if (!useXamlColorManagedImage)
            {
                await ResizeRendererAsync(GetRenderSurfaceWidth(), GetRenderSurfaceHeight(), cancellationToken);
            }
            resizeTimer.Stop();

            var renderTimer = Stopwatch.StartNew();
            if (useXamlColorManagedImage)
            {
                await ShowSdrFallbackImageAsync(path, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                renderStatus = "SDR/ICC preview shown through WinUI Image color management; D3D decode skipped";
            }
            else
            {
                if (invalidateRendererCache)
                {
                    _renderer.InvalidateImageCache();
                }

                var loadTimer = Stopwatch.StartNew();
                await _renderer.LoadAsync(document, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                loadTimer.Stop();
                renderStatus = $"{_renderer.LastRenderStatus}; renderer load {loadTimer.ElapsedMilliseconds}ms";
                var postLayoutTimer = Stopwatch.StartNew();
                if (UpdateImageSurfaceLayout())
                {
                    await ResizeRendererAsync(GetRenderSurfaceWidth(), GetRenderSurfaceHeight(), cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                postLayoutTimer.Stop();
                if (postLayoutTimer.ElapsedMilliseconds > 0)
                {
                    renderStatus = $"{renderStatus}; post-load layout+resize {postLayoutTimer.ElapsedMilliseconds}ms";
                }

                // Use Contains rather than StartsWith: the base-image shader status
                // can be prefixed with a "Base D2D system pipeline skipped: ..."
                // note (e.g. when the decoder already applied ICC -> sRGB for a
                // wide-gamut SDR image), which would otherwise defeat a StartsWith
                // match and wrongly fall through to the XAML Image fallback.
                var gainMapPresented = document.HasRenderableGainMap
                    && renderStatus.Contains("Gain-map shader presented", StringComparison.Ordinal);
                var d3dPresented = renderStatus.Contains("Gain-map shader presented", StringComparison.Ordinal)
                    || renderStatus.Contains("Base image D2D system pipeline presented", StringComparison.Ordinal)
                    || renderStatus.Contains("Base image shader presented", StringComparison.Ordinal);
                if (d3dPresented && _renderer.IsSwapChainPanelBound && _renderer.LastFrameHasVisiblePixels)
                {
                    HideFallbackImageLayer();
                }
                else if (gainMapPresented)
                {
                    renderStatus = $"{renderStatus}; showing SDR fallback while D3D surface is verified";
                }
                else if (!document.HasRenderableGainMap
                    && document.HeifAvifProbe?.HasHdrTransfer != true
                    && document.Format.Kind != HdrImageKind.SingleLayerHdr)
                {
                    await ShowSdrFallbackImageAsync(path, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            renderTimer.Stop();

            if (!_imageLoads.IsCurrent(imageLoad))
            {
                return;
            }

            openTimer.Stop();
            renderStatus = $"{renderStatus}; open timing probe {probeTimer.ElapsedMilliseconds}ms, resize {resizeTimer.ElapsedMilliseconds}ms, render {renderTimer.ElapsedMilliseconds}ms, total {openTimer.ElapsedMilliseconds}ms";
            ViewerSessionState.SaveImage(document.Path, _folderImagePaths, _currentNavigationIsExplicit);
            if (_currentNavigationIsExplicit)
            {
                QueueAdjacentPreloads();
            }
            else
            {
                QueueFolderImageListRefresh(path);
            }
            RequestImageLoadMemoryTrim();

            if (!string.IsNullOrWhiteSpace(renderStatus)
                && _imageLoads.IsCurrent(imageLoad))
            {
                ViewModel.UpdateRenderStatus(renderStatus);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (_imageLoads.IsCurrent(imageLoad))
            {
                ViewModel.UpdateRenderStatus($"打开失败: {ex.GetType().Name}: {ex.Message}");
            }

            return;
        }
        finally
        {
            _imageLoads.Complete(imageLoad);
        }
    }

    private void UpdateFolderNavigationOverlay()
    {
        var hasImage = _folderImagePaths.Count > 0 && _currentFolderIndex >= 0;
        if (!hasImage)
        {
            _viewerChromeHideTimer?.Stop();
            SetViewerChromeVisible(true, animate: false);
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
        var showFilmstrip = _settings.ShowFilmstrip && _folderImagePaths.Count > 1;
        FilmstripRow.Visibility = showFilmstrip ? Visibility.Visible : Visibility.Collapsed;
        FolderFileNameText.Visibility = showFilmstrip ? Visibility.Collapsed : Visibility.Visible;
        FolderPositionText.Text = $"{_currentFolderIndex + 1} / {_folderImagePaths.Count}";
        var currentFileName = Path.GetFileName(_folderImagePaths[_currentFolderIndex]);
        FolderFileNameText.Text = currentFileName;
        CropButton.IsEnabled = true;
        TopCropButton.IsEnabled = true;
        FullScreenButton.IsEnabled = true;
        TopFullScreenButton.IsEnabled = true;
        UpdateFilmstripSelection();
        UpdateFilmstripChromeLayout();
        UpdateZoomControls();
        ShowViewerChromeTemporarily();
    }

    private void RefreshFilmstripItems()
    {
        _filmstripThumbnails.PruneCache(_folderImagePaths, _currentFolderIndex);

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
            var items = new List<FilmstripImageItem>(_folderImagePaths.Count);
            foreach (var path in _folderImagePaths)
            {
                var item = new FilmstripImageItem(path);
                if (_filmstripThumbnails.TryGetCached(path, out var cachedThumbnail))
                {
                    item.Thumbnail = cachedThumbnail;
                }

                items.Add(item);
            }

            // Single Reset instead of one Add per item: a per-item rebuild of a
            // bound ListView freezes the UI thread for seconds when the folder
            // holds tens of thousands of images.
            FilmstripItems.ReplaceAll(items);
        }

        // Queue on every refresh, not only on rebuild: navigation moves the
        // focus window, and items entering it would otherwise never get a
        // thumbnail once the initial window around the opening image finished.
        // Already-thumbnailed items are skipped, so repeat calls are cheap.
        _filmstripThumbnails.QueueLoads(_currentFolderIndex);

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
            if (_currentFilmstripItem is not null)
            {
                _currentFilmstripItem.IsCurrent = false;
                _currentFilmstripItem = null;
            }

            return;
        }

        ImageFilmstrip.SelectedIndex = _currentFolderIndex;
        var selectedItem = FilmstripItems[_currentFolderIndex];
        if (!ReferenceEquals(_currentFilmstripItem, selectedItem))
        {
            // IsCurrent is only ever set here, so toggling the two affected
            // items replaces the old walk over the whole collection (slow in
            // folders with tens of thousands of images).
            if (_currentFilmstripItem is not null)
            {
                _currentFilmstripItem.IsCurrent = false;
            }

            selectedItem.IsCurrent = true;
            _currentFilmstripItem = selectedItem;
        }
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
            : ViewerChromeMaxWidth;
        var overlayMaxWidth = Math.Min(
            Math.Max(1.0, availableWidth - ViewerChromeHorizontalInset),
            ViewerChromeMaxWidth);
        PhotoToolbarOverlay.MaxWidth = overlayMaxWidth;

        var isCompact = availableWidth < CompactViewerChromeBreakpoint;
        ReloadImageButton.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        CropButton.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        SingleLayerHdrSaveAsButton.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        var showFilmstrip = FilmstripRow.Visibility == Visibility.Visible;
        FolderFileNameText.Visibility = isCompact || showFilmstrip ? Visibility.Collapsed : Visibility.Visible;
        ZoomOutButton.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        ZoomLevelText.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        ZoomInButton.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        ActualSizeButton.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        ZoomFillButton.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;

        var desiredFilmstripWidth = Math.Min(
            FilmstripItems.Count * FilmstripItemWidth + 4.0,
            overlayMaxWidth - (isCompact ? 300.0 : ToolbarReservedWidth));
        ImageFilmstrip.Width = Math.Max(0.0, desiredFilmstripWidth);
        FilmstripRow.Width = ImageFilmstrip.Width;
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

    private async void ResetHdrGainToDisplayPeak_Click(object sender, RoutedEventArgs e)
    {
        SnapHdrGainSliderToDisplayPeak(force: true);
        await ApplyHdrPreviewOverrideAsync();
    }

    private void AppSettingsService_SettingsChanged(object? sender, EventArgs e)
    {
        _settings = AppSettingsService.Current;
        ApplyViewerSettings();
        QueueAdjacentPreloads();
        _ = ApplyHdrPreviewOverrideAsync();
    }

    private async void Page_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var isControlDown = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

        switch (e.Key)
        {
            case VirtualKey.Left:
                await NavigateFolderImageAsync(-1);
                e.Handled = true;
                break;
            case VirtualKey.Right:
                await NavigateFolderImageAsync(1);
                e.Handled = true;
                break;
            case VirtualKey.F:
                FullScreen_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case VirtualKey.I:
                ToggleInspectorPanel();
                e.Handled = true;
                break;
            case VirtualKey.B:
                AppSettingsService.SetShowFilmstrip(!_settings.ShowFilmstrip);
                e.Handled = true;
                break;
            case VirtualKey.Number0 when isControlDown:
                if (ViewModel.HasImage)
                {
                    ResetZoomToFit();
                    await ApplyZoomAsync();
                }

                e.Handled = true;
                break;
            case VirtualKey.Number1 when isControlDown:
                if (ViewModel.HasImage)
                {
                    _isFitZoom = false;
                    _isFillZoom = false;
                    _zoomScale = CalculateActualSizeZoomScale();
                    await ApplyZoomAsync();
                }

                e.Handled = true;
                break;
        }
    }

    private void AttachSettingsChanged()
    {
        if (_isSettingsEventAttached)
        {
            return;
        }

        AppSettingsService.SettingsChanged += AppSettingsService_SettingsChanged;
        _isSettingsEventAttached = true;
    }

    private void DetachSettingsChanged()
    {
        if (!_isSettingsEventAttached)
        {
            return;
        }

        AppSettingsService.SettingsChanged -= AppSettingsService_SettingsChanged;
        _isSettingsEventAttached = false;
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
        ShowViewerChromeTemporarily();

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

    private void TopInspectorToggleButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleInspectorPanel();
    }

    private void ToggleInspectorPanel()
    {
        AppSettingsService.SetShowInspectorPanel(!_settings.ShowInspectorPanel);
    }

    private void CancelCrop_Click(object sender, RoutedEventArgs e)
    {
        SetCropMode(false);
    }

    private async void ApplyCrop_Click(object sender, RoutedEventArgs e)
    {
        await ExportCurrentCropAsync();
    }

    private async void SingleLayerHdrSaveAs_Click(object sender, RoutedEventArgs e)
    {
        await ExportSingleLayerHdrSaveAsAsync();
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is MainWindow mainWindow)
        {
            var isImmersive = mainWindow.ToggleImmersiveViewing();
            ApplyImmersiveViewingState(isImmersive);
        }
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

    private async Task ApplyHdrPreviewOverrideAsync(CancellationToken cancellationToken = default)
    {
        if (HdrPreviewModeSelector is null)
        {
            return;
        }

        var effectiveCancellationToken = cancellationToken == default
            ? _lifetime.Token
            : cancellationToken;
        effectiveCancellationToken.ThrowIfCancellationRequested();

        var viewMode = GetSelectedHdrViewMode();
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
            HdrHeadroomModeSelector.Visibility = headroomControlsEnabled ? Visibility.Visible : Visibility.Collapsed;
        }

        if (HdrGainPanel is not null)
        {
            HdrGainPanel.Visibility = usesSlider ? Visibility.Visible : Visibility.Collapsed;
        }

        if (HdrGainSlider is not null)
        {
            HdrGainSlider.IsEnabled = true;
            HdrGainSlider.IsHitTestVisible = usesSlider;
            HdrGainSlider.Opacity = usesSlider ? 1.0 : 0.55;
        }

        UpdateSdrWhiteControls();

        if (IsLoaded)
        {
            RefreshRendererDisplayConfiguration();
        }

        UpdateHdrGainValueText();
        _renderer.ViewMode = viewMode;
        _renderer.HeadroomMode = headroomMode;
        _renderer.DisplayCapacityOverrideLog2 = usesSlider && HdrGainSlider is not null ? CalculateManualDisplayCapacityStops() : null;
        _renderer.AdaptiveToneMappingEnabled = false;
        _renderer.ColorGamutMappingMode = _settings.ColorGamutMappingMode;

        if (IsLoaded)
        {
            await ResizeRendererAsync(GetRenderSurfaceWidth(), GetRenderSurfaceHeight(), effectiveCancellationToken);
            effectiveCancellationToken.ThrowIfCancellationRequested();
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

    private async Task UpdateHdrModeControlsForDocumentAsync(
        HdrImageDocument document,
        CancellationToken cancellationToken = default)
    {
        if (HdrPreviewModeSelector is null)
        {
            return;
        }

        var effectiveCancellationToken = cancellationToken == default
            ? _lifetime.Token
            : cancellationToken;
        effectiveCancellationToken.ThrowIfCancellationRequested();

        var hasGainMap = document.HasRenderableGainMap;
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
            else
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

        UpdateSdrWhiteControls();
        await ApplyHdrPreviewOverrideAsync(effectiveCancellationToken);
        effectiveCancellationToken.ThrowIfCancellationRequested();
    }

    private void SetHdrModeItemEnabled(int index, bool enabled)
    {
        if (HdrPreviewModeSelector?.Items.Count > index
            && HdrPreviewModeSelector.Items[index] is ComboBoxItem item)
        {
            item.IsEnabled = enabled;
            item.Opacity = enabled ? 1.0 : 0.45;
            item.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ApplyViewerSettings()
    {
        _renderer.ColorGamutMappingMode = _settings.ColorGamutMappingMode;

        ApplyInspectorLayout();

        UpdateFolderNavigationOverlay();
    }

    private void SnapHdrGainSliderToDisplayPeakIfNeeded()
    {
        SnapHdrGainSliderToDisplayPeak(force: false);
    }

    private void SnapHdrGainSliderToDisplayPeak(bool force)
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

        if (force || Math.Abs(HdrGainSlider.Value - 1000.0) <= 1.0)
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
        _renderer.ReferenceWhiteExposureScale = CalculateReferenceWhiteExposureScale();
    }

    // The reference-white override slider is a diffuse-white / exposure control.
    // Single-layer PQ/HLG and standard gain maps use a 203-nit content reference;
    // Apple HDRGainMap defaults to the current display SDR white. App
    // scene-linear units remain anchored at 80 nits internally.
    private float CalculateReferenceWhiteExposureScale()
    {
        var hasOverride = SdrWhiteOverrideToggle?.IsOn == true
            && SdrWhiteSlider is not null
            && CurrentDocumentUsesReferenceWhite()
            && SelectedViewModeUsesReferenceWhite();
        if (!hasOverride)
        {
            return 1.0f;
        }

        var targetWhite = Math.Clamp(SdrWhiteSlider!.Value, 80.0, 600.0);
        return (float)(targetWhite / GetCurrentReferenceWhiteNits());
    }

    private double GetCurrentReferenceWhiteNits(double? displaySdrWhiteNits = null)
    {
        if (_currentDocument?.HasRenderableGainMap == true)
        {
            return CurrentDocumentUsesAppleGainMapReferenceWhite()
                ? Math.Max(displaySdrWhiteNits ?? (_renderer.DisplayConfiguration.SceneToSdrWhiteScale * 80.0), 80.0)
                : 203.0;
        }

        if (CurrentViewModelLooksLikeGainMapHdr())
        {
            return CurrentDocumentUsesAppleGainMapReferenceWhite()
                ? Math.Max(displaySdrWhiteNits ?? (_renderer.DisplayConfiguration.SceneToSdrWhiteScale * 80.0), 80.0)
                : 203.0;
        }

        return 203.0;
    }

    private bool CurrentDocumentUsesReferenceWhite()
    {
        if (_currentDocument is { } document
            && (document.HasRenderableGainMap
                || document.Format.Kind == HdrImageKind.SingleLayerHdr
                || document.HeifAvifProbe?.HasHdrTransfer == true))
        {
            return true;
        }

        return CurrentViewModelLooksLikeGainMapHdr() || CurrentViewModelLooksLikeSingleLayerHdr();
    }

    private bool CurrentDocumentUsesAppleGainMapReferenceWhite()
    {
        if (_currentDocument is { HasRenderableGainMap: true } document
            && (document.GainMapProbe?.Metadata?.Source.StartsWith("Apple HDRGainMap", StringComparison.Ordinal) == true
                || document.HeifAvifProbe?.HasAppleHdrGainMapSignal == true))
        {
            return true;
        }

        return ViewModel.GainMapStatus.Contains("Apple HDRGainMap", StringComparison.OrdinalIgnoreCase);
    }

    private bool CurrentViewModelLooksLikeGainMapHdr()
    {
        if (_currentDocument is { } document)
        {
            return document.HasRenderableGainMap;
        }

        if (!ViewModel.HasImage)
        {
            return false;
        }

        var kind = ViewModel.HdrKind;
        var status = ViewModel.GainMapStatus;
        if (status.Contains("metadata-only", StringComparison.OrdinalIgnoreCase)
            || status.Contains("未实现", StringComparison.Ordinal)
            || status.Contains("未启用", StringComparison.Ordinal)
            || status.Contains("按 SDR", StringComparison.Ordinal))
        {
            return false;
        }

        var hasGainMapToken = kind.Contains("GainMap", StringComparison.OrdinalIgnoreCase)
            || status.Contains("gain map", StringComparison.OrdinalIgnoreCase)
            || status.Contains("gain-map", StringComparison.OrdinalIgnoreCase)
            || status.Contains("HDRGainMap", StringComparison.OrdinalIgnoreCase)
            || status.Contains("增益图", StringComparison.Ordinal);
        var hasPositiveProbeToken = kind.Contains("GainMap", StringComparison.OrdinalIgnoreCase)
            || status.Contains("已定位", StringComparison.Ordinal)
            || status.Contains("检测到", StringComparison.Ordinal)
            || status.Contains("detected", StringComparison.OrdinalIgnoreCase);
        return hasGainMapToken && hasPositiveProbeToken;
    }

    private bool CurrentViewModelLooksLikeSingleLayerHdr()
    {
        if (!ViewModel.HasImage)
        {
            return false;
        }

        return ViewModel.HdrKind.Contains("SingleLayerHdr", StringComparison.OrdinalIgnoreCase)
            || ViewModel.TransferFunction.Contains("PQ", StringComparison.OrdinalIgnoreCase)
            || ViewModel.TransferFunction.Contains("HLG", StringComparison.OrdinalIgnoreCase)
            || ViewModel.TransferFunction.Contains("scRGB", StringComparison.OrdinalIgnoreCase);
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
            var hasReferenceWhiteOverride = SdrWhiteOverrideToggle?.IsOn == true
                && SdrWhiteSlider is not null
                && CurrentDocumentUsesReferenceWhite()
                && SelectedViewModeUsesReferenceWhite();
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

            if (hasReferenceWhiteOverride)
            {
                details = $"{details}; reference white override {SdrWhiteSlider!.Value:0} nits (auto {GetCurrentReferenceWhiteNits(systemSdrWhite):0} nits; display SDR white {systemSdrWhite:0} nits)";
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
        if (SdrWhitePanel is null || SdrWhiteOverrideToggle is null || SdrWhiteSlider is null || SdrWhiteValueText is null)
        {
            return;
        }

        if (!SelectedViewModeUsesReferenceWhite()
            || !CurrentDocumentUsesReferenceWhite())
        {
            SdrWhitePanel.Visibility = Visibility.Collapsed;
            SdrWhiteSlider.IsEnabled = false;
            SdrWhiteSlider.Visibility = Visibility.Collapsed;
            SdrWhiteValueText.Text = "固定";
            return;
        }

        SdrWhitePanel.Visibility = Visibility.Visible;
        var enabled = SdrWhiteOverrideToggle.IsOn;
        SdrWhiteSlider.IsEnabled = enabled;
        SdrWhiteSlider.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        var defaultPrefix = _currentDocument?.HasRenderableGainMap == true || CurrentViewModelLooksLikeGainMapHdr()
            ? "自动"
            : "绝对";
        SdrWhiteValueText.Text = enabled
            ? $"{SdrWhiteSlider.Value:0} nits"
            : $"{defaultPrefix} {GetCurrentReferenceWhiteNits():0} nits";
    }

    private GainmapViewMode GetSelectedHdrViewMode()
    {
        return HdrPreviewModeSelector?.SelectedIndex switch
        {
            0 => GainmapViewMode.Sdr,
            2 => GainmapViewMode.AlternateImage,
            3 => GainmapViewMode.GainMap,
            _ => GainmapViewMode.Adaptive,
        };
    }

    private bool SelectedViewModeUsesReferenceWhite()
    {
        return GetSelectedHdrViewMode() is not GainmapViewMode.Sdr and not GainmapViewMode.GainMap;
    }


    private Task ResizeRendererAsync()
    {
        var surfaceWidth = GetRenderSurfaceWidth();
        var surfaceHeight = GetRenderSurfaceHeight();
        return ResizeRendererAsync(surfaceWidth, surfaceHeight, _lifetime.Token);
    }

    private async Task ResizeRendererAsync(double surfaceWidth, double surfaceHeight, CancellationToken cancellationToken = default)
    {
        var pixelWidth = Math.Max(1, (int)Math.Round(surfaceWidth * HdrSwapChainHost.CompositionScaleX));
        var pixelHeight = Math.Max(1, (int)Math.Round(surfaceHeight * HdrSwapChainHost.CompositionScaleY));
        try
        {
            await _renderer.ResizeAsync(pixelWidth, pixelHeight, cancellationToken == default ? _lifetime.Token : cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected when the view is torn down or a newer resize supersedes this one.
        }
        catch (ObjectDisposedException)
        {
            // Renderer was disposed while a resize was in flight.
        }
        catch (Exception ex)
        {
            // A GPU/swap-chain failure (e.g. DXGI_ERROR_DEVICE_REMOVED) must not
            // crash the app from a fire-and-forget event handler; surface it instead.
            ViewModel.UpdateRenderStatus($"渲染器调整失败: {ex.GetType().Name}: {ex.Message}");
        }
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

        var contentAspectRatio = _useXamlFallbackLayoutAspectRatio
            ? _xamlFallbackDisplayAspectRatio
            : _renderer.ContentDisplayAspectRatio;
        if (contentAspectRatio is { } aspectRatio && aspectRatio > 0.0)
        {
            (targetWidth, targetHeight) = _isFillZoom
                ? ViewerViewportMath.CalculateFillSize(availableWidth, availableHeight, aspectRatio)
                : ViewerViewportMath.CalculateFitSize(availableWidth, availableHeight, aspectRatio);
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

    private double CalculateActualSizeZoomScale()
    {
        var availableWidth = PreviewSurface.ActualWidth;
        var availableHeight = PreviewSurface.ActualHeight;
        var aspectRatio = _renderer.ContentDisplayAspectRatio;
        if (availableWidth <= 0.0 || availableHeight <= 0.0 || aspectRatio is null or <= 0.0)
        {
            return 1.0;
        }

        var (fitWidth, fitHeight) = ViewerViewportMath.CalculateFitSize(availableWidth, availableHeight, aspectRatio.Value);
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
        var horizontalBlend = ViewerViewportMath.CalculatePointerAnchorBlend(targetWidth, PreviewSurface.ActualWidth);
        var verticalBlend = ViewerViewportMath.CalculatePointerAnchorBlend(targetHeight, PreviewSurface.ActualHeight);
        anchorX = ViewerViewportMath.Lerp(0.5, anchorX, horizontalBlend);
        anchorY = ViewerViewportMath.Lerp(0.5, anchorY, verticalBlend);
    }

    private void NormalizeAnchorForTargetSize(
        ref double anchorX,
        ref double anchorY,
        ref double anchorViewportX,
        ref double anchorViewportY,
        double targetWidth,
        double targetHeight)
    {
        var horizontalBlend = ViewerViewportMath.CalculatePointerAnchorBlend(targetWidth, PreviewSurface.ActualWidth);
        var verticalBlend = ViewerViewportMath.CalculatePointerAnchorBlend(targetHeight, PreviewSurface.ActualHeight);
        anchorX = ViewerViewportMath.Lerp(0.5, anchorX, horizontalBlend);
        anchorY = ViewerViewportMath.Lerp(0.5, anchorY, verticalBlend);
        anchorViewportX = ViewerViewportMath.Lerp(PreviewSurface.ActualWidth / 2.0, anchorViewportX, horizontalBlend);
        anchorViewportY = ViewerViewportMath.Lerp(PreviewSurface.ActualHeight / 2.0, anchorViewportY, verticalBlend);
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
        _imagePreloads.QueueAdjacent(
            _folderImagePaths,
            _currentFolderIndex,
            _settings.PreloadAdjacentImages,
            AppUserSettings.NormalizeAdjacentPreloadRadius(_settings.AdjacentPreloadRadius),
            CalculateViewerPreloadMaxPixelSize());
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

    private static void RequestImageLoadMemoryTrim()
    {
        // Debounced: a blocking gen-2 + LOH-compacting collection per image made
        // rapid folder navigation pay a full-GC pause for every frame advance.
        // Trim once after the user settles instead.
        s_memoryTrimTimer.Change(MemoryTrimDebounceDelay, Timeout.InfiniteTimeSpan);
    }

    private static void TrimImageLoadMemory()
    {
        // Non-blocking background collection: the previous aggressive blocking
        // compacting collect suspended every thread (including UI) while the
        // LOH was compacted, which showed up as a visible hitch right after
        // navigation settled. The background sweep still frees the dropped
        // decode buffers for reuse; CompactOnce stays set so the next natural
        // blocking full GC compacts the LOH.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);
    }

    private int CalculateViewerPreloadMaxPixelSize()
    {
        // Same formula as the renderer's own decode request. The viewport is
        // always at least as large as the fitted swap chain, so deriving the
        // preload target from the viewport guarantees the cached decode
        // satisfies the renderer's size check; the previous smaller 1.25x /
        // 2048-capped target made hot preloads undersized and unusable.
        var compositionScale = Math.Max(HdrSwapChainHost?.CompositionScaleX ?? 1.0, HdrSwapChainHost?.CompositionScaleY ?? 1.0);
        var width = PreviewSurface?.ActualWidth > 0.0 ? PreviewSurface.ActualWidth : 1280.0;
        var height = PreviewSurface?.ActualHeight > 0.0 ? PreviewSurface.ActualHeight : 900.0;
        return D3D11HdrRenderPipeline.CalculateDecodeTargetForSurface(Math.Max(width, height) * compositionScale);
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



