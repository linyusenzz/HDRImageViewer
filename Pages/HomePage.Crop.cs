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
using Microsoft.UI.Xaml.Media.Animation;
using SharpGen.Runtime;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime;
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

namespace HdrImageViewer.Pages;

public sealed partial class HomePage
{
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

    private void CropUltraHdrGainMapModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isCropModeEnabled && SelectedCropExportMode == CropExportMode.UltraHdrConvert)
        {
            ViewModel.UpdateRenderStatus($"{_renderer.LastRenderStatus}; {BuildCropExportModeStatus()}");
        }
    }

    private void CropUltraHdrBaseGamutSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isCropModeEnabled && SelectedCropExportMode == CropExportMode.UltraHdrConvert)
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

        if (CropUltraHdrGainMapModeSelector is not null)
        {
            CropUltraHdrGainMapModeSelector.Visibility = SelectedCropExportMode == CropExportMode.UltraHdrConvert
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (CropUltraHdrBaseGamutSelector is not null)
        {
            CropUltraHdrBaseGamutSelector.Visibility = SelectedCropExportMode == CropExportMode.UltraHdrConvert
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

    private UltraHdrGainMapChannelMode SelectedUltraHdrGainMapChannelMode => CropUltraHdrGainMapModeSelector?.SelectedIndex == 1
        ? UltraHdrGainMapChannelMode.Rgb
        : UltraHdrGainMapChannelMode.Monochrome;

    private UltraHdrSdrBaseColorGamut SelectedUltraHdrSdrBaseColorGamut => CropUltraHdrBaseGamutSelector?.SelectedIndex switch
    {
        1 => UltraHdrSdrBaseColorGamut.Bt709,
        2 => UltraHdrSdrBaseColorGamut.DisplayP3,
        3 => UltraHdrSdrBaseColorGamut.Bt2100,
        _ => UltraHdrSdrBaseColorGamut.Auto,
    };

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
}
