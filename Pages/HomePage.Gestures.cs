using HdrImageViewer.Presentation;
using Microsoft.UI.Xaml.Controls;

namespace HdrImageViewer.Pages;

public sealed partial class HomePage
{
    private bool _nativeManipulationActive;
    private bool _resettingNativeZoom;

    // Native scaling is transient. Bake it into the existing image layout when
    // the gesture ends, leaving wheel/buttons/crop with the same zoom authority.
    private double NativeZoomFactor => _resettingNativeZoom ? 1.0 : ImageScroller?.ZoomFactor ?? 1.0;

    private void ImageScroller_DirectManipulationStarted(object? sender, object e)
    {
        _nativeManipulationActive = true;
        StopZoomAnimation();
        _zoomRenderCts?.Cancel();
        EndSwapChainZoomPreview();
        ClearPendingZoomAnchor();
        // A prior wheel anchor must not pin native two-finger panning in place.
        ClearLayoutScrollOverride();
        _renderer.ClearAnalysis();
        ShowViewerChromeTemporarily();
    }

    private void ImageScroller_DirectManipulationCompleted(object? sender, object e)
    {
        _nativeManipulationActive = false;
        CommitNativeZoom();
        ScheduleViewportPresent();
    }

    private bool HandleNativeViewChanged(ScrollViewerViewChangedEventArgs e)
    {
        if (_resettingNativeZoom)
        {
            if (Math.Abs(ImageScroller.ZoomFactor - 1.0) > 0.00001) return true;
            _resettingNativeZoom = false;
        }

        if (_nativeManipulationActive) ClearLayoutScrollOverride();
        if (Math.Abs(NativeZoomFactor - 1.0) > 0.00001 && ViewModel.HasImage)
        {
            UpdateNativeZoomScale();
            UpdateZoomControls();
            if (!_nativeManipulationActive && !e.IsIntermediate) CommitNativeZoom();
        }
        return false;
    }

    private void UpdateNativeZoomScale()
    {
        if (ImageSurface.Width <= 0 || ImageSurface.Height <= 0
            || PreviewSurface.ActualWidth <= 0 || PreviewSurface.ActualHeight <= 0) return;
        var fit = ViewerViewportMath.CalculateFitSize(PreviewSurface.ActualWidth, PreviewSurface.ActualHeight,
            ImageSurface.Width / ImageSurface.Height);
        _zoomScale = _targetZoomScale = _committedZoomScale = ImageSurface.Width * NativeZoomFactor / fit.Width;
        _isFitZoom = _isFillZoom = _zoomTargetFit = _zoomTargetFill = false;
    }

    private void CommitNativeZoom()
    {
        var factor = NativeZoomFactor;
        if (Math.Abs(factor - 1.0) <= 0.00001 || !ViewModel.HasImage
            || !double.IsFinite(ImageSurface.Width) || !double.IsFinite(ImageSurface.Height)) return;

        var width = ImageSurface.Width * factor;
        var height = ImageSurface.Height * factor;
        var x = ViewerViewportMath.CalculateNativeZoomCommitOffset(ImageSurface.Width, ImageViewport.Width,
            PreviewSurface.ActualWidth, factor, ImageScroller.HorizontalOffset);
        var y = ViewerViewportMath.CalculateNativeZoomCommitOffset(ImageSurface.Height, ImageViewport.Height,
            PreviewSurface.ActualHeight, factor, ImageScroller.VerticalOffset);
        UpdateNativeZoomScale();
        _resettingNativeZoom = true;
        SetLayoutScrollOverride(x, y);
        ApplyImageSurfaceSize(width, height);
        ImageScroller.ChangeView(x, y, 1.0f, disableAnimation: true);
        UpdateZoomControls();
    }

    private void ResetNativeZoom()
    {
        _nativeManipulationActive = false;
        if (ImageScroller is null || Math.Abs(ImageScroller.ZoomFactor - 1.0) <= 0.00001) return;
        _resettingNativeZoom = true;
        ImageScroller.ChangeView(0.0, 0.0, 1.0f, disableAnimation: true);
    }

    private void UpdateNativeZoomLimits()
    {
        if (ImageScroller is null || ImageSurface.Width <= 0 || ImageSurface.Height <= 0
            || PreviewSurface.ActualWidth <= 0 || PreviewSurface.ActualHeight <= 0) return;
        var fit = ViewerViewportMath.CalculateFitSize(PreviewSurface.ActualWidth, PreviewSurface.ActualHeight,
            ImageSurface.Width / ImageSurface.Height);
        var baseScale = ImageSurface.Width / fit.Width;
        var actualSizeScale = CalculateActualSizeZoomScale();
        // ScrollViewer supports 0.1..10 per gesture; rebasing at completion
        // still allows the viewer's full 1%..800% range across gestures.
        ImageScroller.MinZoomFactor = (float)Math.Clamp(actualSizeScale * 0.01 / baseScale, 0.1, 1.0);
        ImageScroller.MaxZoomFactor = (float)Math.Clamp(actualSizeScale * 8.0 / baseScale, 1.0, 10.0);
    }
}
