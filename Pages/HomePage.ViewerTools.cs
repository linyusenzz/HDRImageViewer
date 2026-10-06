using HdrImageViewer.Rendering;
using HdrImageViewer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace HdrImageViewer.Pages;

public sealed partial class HomePage
{
    private bool _movingComparison;
    private bool _updatingViewerTools;
    private bool _toolsRendering;
    private bool _toolsRenderPending;
    private long _analysisRefreshVersion;
    private BatchExportWindow? _batchExportWindow;

    private void BatchExport_Click(object sender, RoutedEventArgs e)
    {
        if (_batchExportWindow is null)
        {
            _batchExportWindow = new BatchExportWindow();
            _batchExportWindow.Closed += (_, _) => _batchExportWindow = null;
        }
        _batchExportWindow.Activate();
    }

    private void ViewerTools_Click(object sender, RoutedEventArgs e)
    {
        ShowInspectorForTools();
        InspectorSectionSelector.SelectedItem = InspectorAnalysisTab;
    }

    private async void InspectorSection_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (InspectorScroll is null || ViewerToolsPanel is null || GamutToolsPanel is null) return;
        var analysis = sender.SelectedItem == InspectorAnalysisTab;
        var gamut = sender.SelectedItem == InspectorGamutTab;
        InspectorScroll.Visibility = analysis || gamut ? Visibility.Collapsed : Visibility.Visible;
        ViewerToolsPanel.Visibility = analysis ? Visibility.Visible : Visibility.Collapsed;
        GamutToolsPanel.Visibility = gamut ? Visibility.Visible : Visibility.Collapsed;
        if ((analysis || gamut) && IsLoaded && _renderer.AnalysisSnapshot?.Version != _renderer.PreviewVersion)
            await RefreshAnalysisAsync();
        if (gamut) DrawChromaticity();
    }

    private void ResetViewerToolsForDocument()
    {
        _analysisRefreshVersion++;
        _renderer.ClearAnalysis();
        _updatingViewerTools = true;
        ComparisonToggle.IsChecked = false;
        _renderer.ComparisonEnabled = false;
        ComparisonDivider.Visibility = Visibility.Collapsed;
        AnalysisSummary.Text = Localization.GetString("AnalysisSummary.Text");
        AverageLuminanceText.Text = PeakLuminanceText.Text = "—";
        ComparisonSlider.Value = 50;
        _renderer.ComparisonPosition = 0.5f;
        UpdateViewerToolControls();
        PixelSampleText.Text = Localization.GetString("PixelSampleText.Text");
        HistogramWhiteText.Text = Localization.GetString("HistogramWhiteText.Text");
        DrawHistogram();
        ClearChromaticitySample();
        GamutSummaryText.Text = Localization.GetString("GamutSummaryText.Text");
        DrawChromaticity();
        _updatingViewerTools = false;
    }

    private async void ViewerToolSetting_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingViewerTools || !IsLoaded) return;
        if (HdrSwapChainHost.Visibility != Visibility.Visible)
        {
            ResetViewerToolsForDocument();
            AnalysisSummary.Text = Localization.GetString("AnalysisSdrNotSupported");
            return;
        }
        _renderer.ComparisonEnabled = ComparisonToggle.IsChecked == true;
        UpdateViewerToolControls();
        await PresentViewerToolsAsync();
    }

    private void UpdateViewerToolControls()
    {
        var compare = ComparisonToggle.IsChecked == true;
        ComparisonControls.Visibility = compare ? Visibility.Visible : Visibility.Collapsed;
        ResetViewerToolsButton.IsEnabled = compare;
        ComparisonPositionText.Text = $"SDR {ComparisonSlider.Value:0}% · HDR {100 - ComparisonSlider.Value:0}%";
    }

    private async void ResetViewerTools_Click(object sender, RoutedEventArgs e)
    {
        _updatingViewerTools = true;
        ComparisonToggle.IsChecked = false;
        _renderer.ComparisonEnabled = false;
        UpdateViewerToolControls();
        _updatingViewerTools = false;
        await PresentViewerToolsAsync();
    }

    private void CenterComparison_Click(object sender, RoutedEventArgs e) => ComparisonSlider.Value = 50;

    private void ComparisonDivider_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        ComparisonSlider.Value = 50;
        e.Handled = true;
    }

    private async void ComparisonSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingViewerTools || !IsLoaded) return;
        _renderer.ComparisonPosition = (float)(e.NewValue / 100);
        UpdateViewerToolControls();
        if (_renderer.ComparisonEnabled) await PresentViewerToolsAsync();
    }

    private async Task PresentViewerToolsAsync()
    {
        _toolsRenderPending = true;
        if (_toolsRendering) return;
        _toolsRendering = true;
        try
        {
            while (_toolsRenderPending && !_lifetime.IsCancellationRequested)
            {
                _toolsRenderPending = false;
                await PresentViewportAsync(_lifetime.Token);
                PositionComparisonDivider();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AnalysisSummary.Text = ex.Message; }
        finally { _toolsRendering = false; }
    }

    private void PositionComparisonDivider()
    {
        if (_chromaticitySample is not null && _renderer.AnalysisSnapshot?.Version != _renderer.PreviewVersion)
            ClearChromaticitySample(Localization.GetString("GamutChangedRefreshPrompt"));
        var show = _renderer.ComparisonEnabled && HdrSwapChainHost.Visibility == Visibility.Visible;
        ComparisonDivider.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var origin = HdrSwapChainHost.TransformToVisual(PreviewSurface).TransformPoint(new Point());
        ComparisonDivider.Margin = new Thickness(
            origin.X + HdrSwapChainHost.ActualWidth * _renderer.ComparisonPosition - 16,
            origin.Y, 0, Math.Max(0, PreviewSurface.ActualHeight - origin.Y - HdrSwapChainHost.ActualHeight));
        ComparisonSdrBadge.Visibility = HdrSwapChainHost.ActualWidth * _renderer.ComparisonPosition >= 64
            ? Visibility.Visible : Visibility.Collapsed;
        ComparisonHdrBadge.Visibility = HdrSwapChainHost.ActualWidth * (1 - _renderer.ComparisonPosition) >= 64
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ComparisonDivider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _movingComparison = ComparisonDivider.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ComparisonDivider_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_movingComparison) return;
        var x = e.GetCurrentPoint(HdrSwapChainHost).Position.X;
        ComparisonSlider.Value = Math.Clamp(x / Math.Max(1, HdrSwapChainHost.ActualWidth) * 100, 0, 100);
        e.Handled = true;
    }

    private void ComparisonDivider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _movingComparison = false;
        ComparisonDivider.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void ComparisonDivider_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => _movingComparison = false;

    private async void RefreshAnalysis_Click(object sender, RoutedEventArgs e) => await RefreshAnalysisAsync();

    private async Task RefreshAnalysisAsync()
    {
        if (!RefreshAnalysisButton.IsEnabled) return;
        if (HdrSwapChainHost.Visibility != Visibility.Visible)
        {
            AnalysisSummary.Text = Localization.GetString("AnalysisNoHdrPreview");
            GamutSummaryText.Text = AnalysisSummary.Text;
            return;
        }
        RefreshAnalysisButton.IsEnabled = false;
        RefreshGamutButton.IsEnabled = false;
        var refreshVersion = ++_analysisRefreshVersion;
        try
        {
            AnalysisSummary.Text = GamutSummaryText.Text = Localization.GetString("AnalysisAnalyzingPreview");
            await PresentViewerToolsAsync();
            var snapshot = await _renderer.AnalyzeCurrentPreviewAsync(_lifetime.Token);
            if (refreshVersion != _analysisRefreshVersion || _lifetime.IsCancellationRequested) return;
            if (snapshot is not null && snapshot.Version == _renderer.PreviewVersion)
            {
                AverageLuminanceText.Text = $"{snapshot.AverageNits:0.0} nits";
                PeakLuminanceText.Text = $"{snapshot.PeakNits:0.0} nits";
                AnalysisSummary.Text = Localization.GetString("AnalysisPixelCountFormat", $"{snapshot.Count:N0}");
                HistogramWhiteText.Text = Localization.GetString("HistogramSdrWhiteFormat", $"{snapshot.SdrWhiteNits:0.#}");
                PixelSampleText.Text = Localization.GetString("PixelSampleInstruction");
                GamutSummaryText.Text = snapshot.ChromaticityCount > 0
                    ? Localization.GetString("GamutColorCountFormat", $"{snapshot.ChromaticityCount:N0}")
                    : Localization.GetString("GamutNoValidColor");
            }
            else
            {
                AnalysisSummary.Text = _renderer.AnalysisError ?? Localization.GetString("AnalysisNotReady");
                GamutSummaryText.Text = AnalysisSummary.Text;
            }
            DrawHistogram();
            ClearChromaticitySample();
            DrawChromaticity();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (refreshVersion == _analysisRefreshVersion)
                AnalysisSummary.Text = GamutSummaryText.Text = ex.Message;
        }
        finally { RefreshAnalysisButton.IsEnabled = RefreshGamutButton.IsEnabled = true; }
    }

    private void Histogram_SizeChanged(object sender, SizeChangedEventArgs e) => DrawHistogram();
    private void Histogram_ActualThemeChanged(FrameworkElement sender, object args) => DrawHistogram();

    private void DrawHistogram()
    {
        if (LuminanceHistogram is null || HistogramEmptyText is null) return;
        LuminanceHistogram.Children.Clear();
        var width = LuminanceHistogram.ActualWidth;
        var height = LuminanceHistogram.ActualHeight;
        if (width <= 0 || height <= 0) return;
        var snapshot = _renderer.AnalysisSnapshot;
        HistogramEmptyText.Visibility = snapshot is { Count: > 0 } ? Visibility.Collapsed : Visibility.Visible;
        var gridBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];
        var labelBrush = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var baseline = height - 1;
        // Shared SDR/HDR boundary, with one-stop grid lines in the HDR half.
        var hdrShade = new Rectangle
        {
            Width = width / 2,
            Height = height,
            Fill = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"]
        };
        Canvas.SetLeft(hdrShade, width / 2);
        LuminanceHistogram.Children.Add(hdrShade);
        for (var stop = 1; stop < 6; stop++)
        {
            var x = width * (0.5 + stop / 12.0);
            LuminanceHistogram.Children.Add(new Line
            {
                X1 = x,
                X2 = x,
                Y1 = 4,
                Y2 = baseline,
                Stroke = gridBrush,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 4 }
            });
            if (stop % 2 != 0) continue;
            var label = new TextBlock
            {
                Text = $"+{stop}",
                Foreground = labelBrush,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"]
            };
            Canvas.SetLeft(label, x + 3); Canvas.SetTop(label, 0);
            LuminanceHistogram.Children.Add(label);
        }
        if (snapshot is { Count: > 0 })
        {
            var channels = new[] { snapshot.RedHistogram, snapshot.GreenHistogram, snapshot.BlueHistogram };
            var colors = new[] { Windows.UI.Color.FromArgb(255, 241, 107, 112),
                Windows.UI.Color.FromArgb(255, 84, 186, 131), Windows.UI.Color.FromArgb(255, 99, 164, 237) };
            var maximum = Math.Max(1, channels.Max(channel => channel.Max()));
            for (var channel = 0; channel < channels.Length; channel++)
            {
                var points = new PointCollection();
                for (var i = 0; i < channels[channel].Length; i++)
                    points.Add(new Point((i + 0.5) / channels[channel].Length * width,
                        baseline - Math.Sqrt(channels[channel][i] / (double)maximum) * (height - 8)));
                var areaPoints = new PointCollection { new Point(points[0].X, baseline) };
                foreach (var point in points) areaPoints.Add(point);
                areaPoints.Add(new Point(points[^1].X, baseline));
                LuminanceHistogram.Children.Add(new Polygon
                {
                    Points = areaPoints,
                    Fill = new SolidColorBrush(colors[channel]),
                    Opacity = 0.08
                });
                LuminanceHistogram.Children.Add(new Polyline
                {
                    Points = points,
                    Stroke = new SolidColorBrush(colors[channel]),
                    StrokeThickness = 1.25,
                    StrokeLineJoin = PenLineJoin.Round
                });
            }
        }
        LuminanceHistogram.Children.Add(new Line
        {
            X1 = width / 2,
            X2 = width / 2,
            Y1 = 0,
            Y2 = height,
            Stroke = labelBrush,
            StrokeThickness = 1
        });
        LuminanceHistogram.Children.Add(new Line
        {
            X1 = 0,
            X2 = width,
            Y1 = baseline,
            Y2 = baseline,
            Stroke = labelBrush,
            StrokeThickness = 1
        });
    }

    private void UpdatePixelSample(PointerRoutedEventArgs e)
    {
        PositionComparisonDivider();
        if (_renderer.AnalysisSnapshot is not { } snapshot) return;
        if (snapshot.Version != _renderer.PreviewVersion || HdrSwapChainHost.Visibility != Visibility.Visible)
        {
            PixelSampleText.Text = Localization.GetString("PixelSampleChangedPrompt");
            ClearChromaticitySample(Localization.GetString("GamutChangedRefreshPrompt"));
            return;
        }
        var p = e.GetCurrentPoint(HdrSwapChainHost).Position;
        var x = (int)Math.Floor(p.X / Math.Max(1, HdrSwapChainHost.ActualWidth) * snapshot.Width);
        var y = (int)Math.Floor(p.Y / Math.Max(1, HdrSwapChainHost.ActualHeight) * snapshot.Height);
        var sample = snapshot.Sample(x, y);
        UpdateChromaticitySample(sample);
        PixelSampleText.Text = sample is { } rgb
            ? Localization.GetString("PixelSampleFormat", x, y, $"{LuminanceSnapshot.ToNits(rgb):0.00}", $"{rgb.X:0.000}", $"{rgb.Y:0.000}", $"{rgb.Z:0.000}")
            : Localization.GetString("PixelSampleOutOfBounds");
    }
}
