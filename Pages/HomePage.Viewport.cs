using HdrImageViewer.Presentation;
using HdrImageViewer.Rendering;
using HdrImageViewer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HdrImageViewer.Pages;

public sealed partial class HomePage
{
    private const int ViewportPresentCoalesceMilliseconds = 16;
    private const int MinimumSwapChainPixels = 2;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _viewportPresentTimer;
    private bool _viewportPresentInFlight;
    private bool _viewportPresentDirty;
    private bool _isZoomPreviewActive;
    private bool _isUpdatingSwapChainHostLayout;
    private bool _swapChainHostContained;
    private double _presentedImageWidth;
    private double _presentedImageHeight;
    private double? _layoutScrollX;
    private double? _layoutScrollY;

    private void EnsureViewportPresentTimer()
    {
        if (_viewportPresentTimer is not null)
        {
            return;
        }

        _viewportPresentTimer = DispatcherQueue.CreateTimer();
        _viewportPresentTimer.Interval = TimeSpan.FromMilliseconds(ViewportPresentCoalesceMilliseconds);
        _viewportPresentTimer.IsRepeating = false;
        _viewportPresentTimer.Tick += ViewportPresentTimer_Tick;
    }

    private void ViewportPresentTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        _ = PresentViewportAsync(_lifetime.Token);
    }

    private void ImageScroller_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!_isZoomPreviewActive && !_isPanning && !e.IsIntermediate)
        {
            if (HasPendingLayoutScrollMismatch())
            {
                ReapplyLayoutScrollOverride();
                return;
            }

            ClearLayoutScrollOverride();
        }

        if (_isZoomPreviewActive
            || _isPanning
            || !ViewModel.HasImage
            || HdrSwapChainHost.Visibility != Visibility.Visible)
        {
            return;
        }

        ScheduleViewportPresent();
    }

    private void ScheduleViewportPresent()
    {
        if (_isZoomPreviewActive)
        {
            return;
        }

        _viewportPresentDirty = true;
        if (_viewportPresentInFlight)
        {
            return;
        }

        EnsureViewportPresentTimer();
        _viewportPresentTimer!.Start();
    }

    private async Task PresentViewportAsync(CancellationToken cancellationToken = default)
    {
        var effectiveCancellationToken = cancellationToken == default
            ? _lifetime.Token
            : cancellationToken;
        if (_viewportPresentInFlight)
        {
            _viewportPresentDirty = true;
            return;
        }

        _viewportPresentInFlight = true;
        try
        {
            do
            {
                _viewportPresentDirty = false;
                if (!TryGetSwapChainHostLayout(out var hostLayout)
                    || !hostLayout.IsValid)
                {
                    return;
                }

                ApplySwapChainHostPlacement(hostLayout);
                if (!TryCreateRenderViewport(hostLayout, out var viewport))
                {
                    return;
                }

                effectiveCancellationToken.ThrowIfCancellationRequested();
                await _renderer.RedrawAsync(viewport, effectiveCancellationToken);
                RememberPresentedImageSize();
                PositionComparisonDivider();
            }
            while (_viewportPresentDirty && !effectiveCancellationToken.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            ViewModel.UpdateRenderStatus(Localization.GetString("StatusRendererResizeFailed", ex.GetType().Name, ex.Message));
        }
        finally
        {
            _viewportPresentInFlight = false;
            if (_viewportPresentDirty && !effectiveCancellationToken.IsCancellationRequested)
            {
                ScheduleViewportPresent();
            }
        }
    }

    private bool TryGetCurrentImageSize(out double imageWidth, out double imageHeight)
    {
        imageWidth = ImageSurface.Width > 0.0 ? ImageSurface.Width : 0.0;
        imageHeight = ImageSurface.Height > 0.0 ? ImageSurface.Height : 0.0;
        if (imageWidth >= MinimumSwapChainPixels && imageHeight >= MinimumSwapChainPixels)
        {
            return true;
        }

        imageWidth = PreviewSurface.ActualWidth;
        imageHeight = PreviewSurface.ActualHeight;
        return imageWidth >= MinimumSwapChainPixels && imageHeight >= MinimumSwapChainPixels;
    }

    private bool TryGetSwapChainHostLayout(out ViewerSwapChainHostLayout layout)
    {
        layout = ViewerSwapChainHostLayout.Invalid;
        if (!TryGetCurrentImageSize(out var imageWidth, out var imageHeight))
        {
            return false;
        }

        layout = CreateSwapChainHostLayout(imageWidth, imageHeight);
        return layout.IsValid;
    }

    private ViewerSwapChainHostLayout CreateSwapChainHostLayout(double imageWidth, double imageHeight)
    {
        var previewWidth = PreviewSurface.ActualWidth;
        var previewHeight = PreviewSurface.ActualHeight;
        var contentWidth = ImageViewport.Width > 0.0
            ? ImageViewport.Width
            : Math.Max(previewWidth, imageWidth);
        var contentHeight = ImageViewport.Height > 0.0
            ? ImageViewport.Height
            : Math.Max(previewHeight, imageHeight);
        // Keep the swap chain at viewport size throughout a zoom. Only the
        // image's shader transform changes; do not resize buffers every frame.
        if (_isZoomPreviewActive)
        {
            var image = ViewerViewportMath.CalculateVisibleImageLayout(previewWidth, previewHeight,
                imageWidth, imageHeight, contentWidth, contentHeight,
                _layoutScrollX ?? ImageScroller?.HorizontalOffset ?? 0,
                _layoutScrollY ?? ImageScroller?.VerticalOffset ?? 0);
            return new ViewerSwapChainHostLayout(previewWidth, previewHeight,
                image.ScaleX, image.ScaleY, image.OffsetX, image.OffsetY);
        }
        return ViewerViewportMath.CalculateSwapChainHostLayout(
            previewWidth,
            previewHeight,
            imageWidth,
            imageHeight,
            contentWidth,
            contentHeight,
            _layoutScrollX ?? ImageScroller?.HorizontalOffset ?? 0.0,
            _layoutScrollY ?? ImageScroller?.VerticalOffset ?? 0.0);
    }

    private bool TryCreateRenderViewport(
        ViewerSwapChainHostLayout hostLayout,
        out HdrRenderViewport viewport)
    {
        viewport = default;
        if (HdrSwapChainHost.Visibility != Visibility.Visible || !hostLayout.IsValid)
        {
            return false;
        }

        var (pixelWidth, pixelHeight) = ViewerViewportMath.CalculateSwapChainPixelSize(
            hostLayout.HostWidth,
            hostLayout.HostHeight,
            HdrSwapChainHost.CompositionScaleX,
            HdrSwapChainHost.CompositionScaleY);
        if (pixelWidth < MinimumSwapChainPixels || pixelHeight < MinimumSwapChainPixels)
        {
            return false;
        }

        viewport = new HdrRenderViewport(
            pixelWidth,
            pixelHeight,
            hostLayout.ScaleX,
            hostLayout.ScaleY,
            hostLayout.OffsetX,
            hostLayout.OffsetY);
        return viewport.IsValid;
    }

    private void ApplySwapChainHostPlacement(double imageWidth, double imageHeight)
    {
        ApplySwapChainHostPlacement(CreateSwapChainHostLayout(imageWidth, imageHeight));
    }

    private void ApplySwapChainHostPlacement(ViewerSwapChainHostLayout hostLayout)
    {
        if (!hostLayout.IsValid)
        {
            return;
        }

        var coversPreview = hostLayout.CoversPreview(
            PreviewSurface.ActualWidth,
            PreviewSurface.ActualHeight);
        if (coversPreview && !_swapChainHostContained)
        {
            return;
        }

        if (!coversPreview
            && _swapChainHostContained
            && Math.Abs(HdrSwapChainHost.Width - hostLayout.HostWidth) <= 0.5
            && Math.Abs(HdrSwapChainHost.Height - hostLayout.HostHeight) <= 0.5)
        {
            return;
        }

        _isUpdatingSwapChainHostLayout = true;
        try
        {
            if (coversPreview)
            {
                HdrSwapChainHost.HorizontalAlignment = HorizontalAlignment.Stretch;
                HdrSwapChainHost.VerticalAlignment = VerticalAlignment.Stretch;
                HdrSwapChainHost.ClearValue(FrameworkElement.WidthProperty);
                HdrSwapChainHost.ClearValue(FrameworkElement.HeightProperty);
                _swapChainHostContained = false;
            }
            else
            {
                HdrSwapChainHost.HorizontalAlignment = HorizontalAlignment.Center;
                HdrSwapChainHost.VerticalAlignment = VerticalAlignment.Center;
                HdrSwapChainHost.Width = hostLayout.HostWidth;
                HdrSwapChainHost.Height = hostLayout.HostHeight;
                _swapChainHostContained = true;
            }

            if (!_isZoomPreviewActive) HdrSwapChainHost.UpdateLayout();
        }
        finally
        {
            _isUpdatingSwapChainHostLayout = false;
        }
    }

    private void ShowHdrSwapChainHost()
    {
        EndSwapChainZoomPreview();
        HdrSwapChainHost.Visibility = Visibility.Visible;
        PreviewSurface.UpdateLayout();
        HdrSwapChainHost.UpdateLayout();
    }

    private void RememberPresentedImageSize()
    {
        if (ImageSurface.Width > 0.0 && ImageSurface.Height > 0.0)
        {
            _presentedImageWidth = ImageSurface.Width;
            _presentedImageHeight = ImageSurface.Height;
        }
    }

    private void BeginSwapChainZoomPreview()
    {
        _isZoomPreviewActive = true;
        _zoomRenderCts?.Cancel();
    }

    private void ApplySwapChainZoomPreview(
        double targetImageWidth,
        double targetImageHeight,
        double anchorViewportX,
        double anchorViewportY)
    {
        if (HdrSwapChainHost.Visibility != Visibility.Visible)
        {
            return;
        }

        _ = anchorViewportX;
        _ = anchorViewportY;
        ClearSwapChainZoomPreviewTransform();
        ApplySwapChainHostPlacement(targetImageWidth, targetImageHeight);
        _ = PresentViewportAsync(_lifetime.Token);
    }

    private void SetLayoutScrollOverride(double scrollX, double scrollY)
    {
        _layoutScrollX = scrollX;
        _layoutScrollY = scrollY;
    }

    private void ClearLayoutScrollOverride()
    {
        _layoutScrollX = null;
        _layoutScrollY = null;
    }

    private bool HasPendingLayoutScrollMismatch()
    {
        if (_layoutScrollX is not double scrollX || _layoutScrollY is not double scrollY || ImageScroller is null)
        {
            return false;
        }

        return Math.Abs(ImageScroller.HorizontalOffset - scrollX) > 1.0
            || Math.Abs(ImageScroller.VerticalOffset - scrollY) > 1.0;
    }

    private void ReapplyLayoutScrollOverride()
    {
        if (_layoutScrollX is not double scrollX || _layoutScrollY is not double scrollY || ImageScroller is null)
        {
            return;
        }

        ImageScroller.ChangeView(scrollX, scrollY, null, disableAnimation: true);
    }

    private void ClearSwapChainZoomPreviewTransform()
    {
        if (SwapChainZoomPreviewTransform is null)
        {
            return;
        }

        SwapChainZoomPreviewTransform.ScaleX = 1.0;
        SwapChainZoomPreviewTransform.ScaleY = 1.0;
        HdrSwapChainHost.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
    }

    private void EndSwapChainZoomPreview()
    {
        _isZoomPreviewActive = false;
        ClearSwapChainZoomPreviewTransform();
    }
}
