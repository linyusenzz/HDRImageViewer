using System.Globalization;
using HdrImageViewer.Services;
using HdrImageViewer.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace HdrImageViewer.Pages;

public sealed partial class HomePage
{
    private bool _updatingZoomSlider;
    private string _statusFileSize = string.Empty;
    private (int Width, int Height)? _sourceOrientedPixelSize;

    private static bool IsImmersiveViewer => App.MainWindow is MainWindow { IsImmersiveViewing: true };

    // The viewer owns the commands and their state; the window hosts them in
    // its native TitleBar so browsing uses a single row of window chrome.
    public CommandBar TakeCommandBar()
    {
        HomeRoot.Children.Remove(TopActionBar);
        return TopActionBar;
    }

    private void InitializeViewerChrome()
    {
        OpenImageButton.Label = Localization.GetString("ViewerCommandOpen");
        TopCropButton.Label = Localization.GetString("ViewerCommandCrop");
        SingleLayerHdrSaveAsButton.Label = Localization.GetString("ViewerCommandExport");
        TopInspectorToggleButton.Label = Localization.GetString("ViewerCommandInfo");
        TopViewerToolsButton.Label = Localization.GetString("ViewerCommandAnalysis");
        TopBatchExportButton.Label = Localization.GetString("ViewerCommandBatch");
        ViewerSettingsButton.Label = Localization.GetString("NavSettings");
        ViewerAboutButton.Label = Localization.GetString("NavAbout");
        ReloadImageButton.Label = Localization.GetString("ToolbarReloadImage");
        ZoomFitMenuItem.Text = Localization.GetString("ToolbarZoomFit");
        ActualSizeButton.Text = Localization.GetString("ToolbarZoomActual");
        ZoomFillButton.Text = Localization.GetString("ToolbarZoomFillCanvas");
        SetChromeName(FilmstripToggleButton, "ViewerFilmstripToggle");
        SetChromeName(CloseInspectorButton, "ViewerCloseInspector");
        SetChromeName(ZoomMenuButton, "ViewerZoom");
        SetChromeName(ZoomSlider, "ViewerZoom");
        CropPanelTitle.Text = Localization.GetString("ViewerCommandCrop");
        CropPanelHint.Text = Localization.GetString("ViewerCropHint");
        CropOutputLabel.Text = Localization.GetString("ViewerCropOutput");
        CropExportModeSelector.Header = Localization.GetString("CropExportMode");
        CropHdrTransferSelector.Header = Localization.GetString("CropHdrCurve");
        CropUltraHdrGainMapModeSelector.Header = Localization.GetString("CropUltraHdrGainMapMode");
        CropUltraHdrBaseGamutSelector.Header = Localization.GetString("CropUltraHdrBaseGamut");
    }

    private static void SetChromeName(FrameworkElement element, string resourceKey)
    {
        var name = Localization.GetString(resourceKey);
        AutomationProperties.SetName(element, name);
        ToolTipService.SetToolTip(element, name);
    }

    private void ViewerSettings_Click(object sender, RoutedEventArgs e)
    {
        _viewerChromeHideTimer?.Stop();
        (App.MainWindow as MainWindow)?.NavigateToSettings();
    }

    private void ViewerAbout_Click(object sender, RoutedEventArgs e)
    {
        _viewerChromeHideTimer?.Stop();
        (App.MainWindow as MainWindow)?.NavigateToAbout();
    }

    private void ExitViewingMode_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_isCropModeEnabled)
        {
            SetCropMode(false);
            args.Handled = true;
        }
        else if (App.MainWindow is MainWindow { IsImmersiveViewing: true } mainWindow)
        {
            mainWindow.SetImmersiveViewing(false);
            args.Handled = true;
        }
    }

    private void FilmstripToggle_Click(object sender, RoutedEventArgs e) =>
        AppSettingsService.SetShowFilmstrip(FilmstripToggleButton.IsChecked == true);

    private void CloseInspector_Click(object sender, RoutedEventArgs e)
    {
        ToggleInspectorPanel();
        TopInspectorToggleButton.Focus(FocusState.Programmatic);
    }

    private async void ZoomPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string text }
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var percentage))
            await ApplyZoomPercentageAsync(percentage);
    }

    private async void ZoomSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingZoomSlider || ZoomSlider is null || !ViewModel.HasImage || !IsLoaded) return;
        ShowViewerChromeTemporarily();
        await ApplyZoomPercentageAsync(e.NewValue, debounce: true);
    }

    private void UpdateZoomPresentation()
    {
        var hasImage = ViewModel.HasImage;
        ZoomMenuButton.IsEnabled = hasImage;
        ZoomSlider.IsEnabled = hasImage;
        var actualSizeScale = CalculateActualSizeZoomScale();
        var fitWidth = ImageSurface.Height > 0 && PreviewSurface.ActualHeight > 0
            ? ViewerViewportMath.CalculateFitSize(PreviewSurface.ActualWidth, PreviewSurface.ActualHeight, ImageSurface.Width / ImageSurface.Height).Width : 0;
        var percentage = ViewerViewportMath.CalculateZoomPercentage(ImageSurface.Width * NativeZoomFactor, fitWidth, actualSizeScale);
        ZoomOutButton.IsEnabled = hasImage && percentage > 1.01;
        ZoomInButton.IsEnabled = hasImage && percentage < 799.99;
        ZoomLevelText.Text = hasImage ? $"{percentage:0.#}%" : "—";
        ToolTipService.SetToolTip(ZoomMenuButton, !hasImage ? Localization.GetString("ViewerZoom")
            : _isFitZoom ? Localization.GetString("ZoomModeFit")
            : _isFillZoom ? Localization.GetString("ZoomModeFill") : Localization.GetString("ViewerZoom"));
        AutomationProperties.SetName(ZoomMenuButton, $"{Localization.GetString("ViewerZoom")}: {ZoomLevelText.Text}");
        _updatingZoomSlider = true;
        try
        {
            ZoomSlider.Value = Math.Clamp(percentage, ZoomSlider.Minimum, ZoomSlider.Maximum);
        }
        finally
        {
            _updatingZoomSlider = false;
        }
    }

    private void UpdateViewerStatus(bool readFileSize = false)
    {
        if (ImageStatusText is null) return;
        if (!ViewModel.HasImage)
        {
            ImageStatusText.Text = string.Empty;
            _statusFileSize = string.Empty;
            return;
        }
        if (readFileSize)
        {
            _statusFileSize = string.Empty;
            try
            {
                var bytes = new FileInfo(ViewModel.FilePath).Length;
                _statusFileSize = bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#} MB" : $"{bytes / 1024.0:0.#} KB";
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var sourceSize = GetKnownSourcePixelSize();
        var dimensions = sourceSize is { } size ? $" · {size.Width} × {size.Height}" : string.Empty;
        var fileSize = _statusFileSize.Length > 0 ? $" · {_statusFileSize}" : string.Empty;
        ImageStatusText.Text = $"{ViewModel.FormatName}{dimensions}{fileSize}";
        ToolTipService.SetToolTip(ImageStatusText, $"{ViewModel.FileName}\n{ViewModel.HdrKind}\n{ViewModel.RenderStatus}");
    }

    private (int Width, int Height)? GetKnownSourcePixelSize()
    {
        if (_sourceOrientedPixelSize is { Width: > 0, Height: > 0 } size) return size;
        var document = _currentDocument;
        var width = document?.JxlProbe?.PixelWidth ?? document?.ExrProbe?.PixelWidth
            ?? (int?)document?.WicImageProbe?.PixelWidth;
        var height = document?.JxlProbe?.PixelHeight ?? document?.ExrProbe?.PixelHeight
            ?? (int?)document?.WicImageProbe?.PixelHeight;
        if (width is not > 0 || height is not > 0) return null;
        return _renderer.ContentOrientation is >= 5 and <= 8 ? (height.Value, width.Value) : (width.Value, height.Value);
    }
}
