using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using HdrImageViewer.Rendering;
using HdrImageViewer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace HdrImageViewer.Pages;

public sealed partial class HomePage
{
    private WriteableBitmap? _chromaticityBackground;
    private WriteableBitmap? _chromaticityDensity;
    private LuminanceSnapshot? _chromaticitySnapshot;
    private bool _chromaticityShowingComparison;
    private Vector2? _chromaticitySample;
    private Canvas? _chromaticityMarker;
    private double _chromaticityScale;
    private double _chromaticityTop;
    private double _chromaticityLeft = 32;

    private void Chromaticity_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChromaticity();
    private void Chromaticity_ActualThemeChanged(FrameworkElement sender, object args)
    {
        _chromaticitySnapshot = null;
        _chromaticityDensity = null;
        DrawChromaticity();
    }

    private void GamutLayer_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        // Layer visibility only changes the plot; retain the captured pair and
        // its shared density scale so switching layers cannot change the data.
        _chromaticitySnapshot = null;
        _chromaticityDensity = null;
        DrawChromaticity();
    }

    private bool ShowGamutComparison => GamutComparisonToggle.IsChecked == true;

    private void UpdateGamutComparisonControls()
    {
        GamutComparisonToggle.IsEnabled = _currentDocument?.HasRenderableGainMap == true && RefreshGamutButton.IsEnabled;
        ToolTipService.SetToolTip(GamutComparisonToggle, Localization.GetString("GamutComparisonHelp"));
        GamutComparisonLegend.Visibility = ShowGamutComparison ? Visibility.Visible : Visibility.Collapsed;
        GamutSamplePanel.Visibility = ShowGamutComparison ? Visibility.Collapsed : Visibility.Visible;
        GamutSubtitleText.Text = Localization.GetString(ShowGamutComparison
            ? "GamutComparisonSubtitle" : "GamutDistributionSubtitle.Text");
        GamutAboutDescriptionText.Text = Localization.GetString(ShowGamutComparison
            ? "GamutComparisonHelp" : "GamutAboutDescription.Text");
    }

    private void UpdateGamutSummary(LuminanceSnapshot snapshot)
    {
        GamutSummaryText.Text = ShowGamutComparison
            ? snapshot.GainMapComparison is { } pair
                ? Localization.GetString("GamutComparisonCounts", $"{pair.SdrCount:N0}", $"{pair.AlternateCount:N0}")
                : Localization.GetString("AnalysisNotReady")
            : snapshot.ChromaticityCount > 0
                ? Localization.GetString("GamutColorCountFormat", $"{snapshot.ChromaticityCount:N0}")
                : Localization.GetString("GamutNoValidColor");
    }

    private async void GamutComparison_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingViewerTools || !IsLoaded) return;
        UpdateGamutComparisonControls();
        ClearChromaticitySample();
        if (_renderer.AnalysisSnapshot is { } snapshot && snapshot.Version == _renderer.PreviewVersion
            && (!ShowGamutComparison || snapshot.GainMapComparison is not null))
        {
            UpdateGamutSummary(snapshot);
            DrawChromaticity();
        }
        else
        {
            DrawChromaticity();
            await RefreshAnalysisAsync();
        }
    }

    private void GamutBoundary_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) DrawChromaticity();
    }

    private void ClearChromaticitySample(string? message = null)
    {
        _chromaticitySample = null;
        if (_chromaticityMarker is not null) _chromaticityMarker.Visibility = Visibility.Collapsed;
        ChromaticitySampleText.Text = message ?? Localization.GetString("ChromaticitySampleText.Text");
        ChromaticityGamutText.Text = Localization.GetString("ChromaticityGamutText.Text");
    }

    private void UpdateChromaticitySample(Vector3? rgb)
    {
        if (ShowGamutComparison) return;
        if (rgb is not { } color) { ClearChromaticitySample(Localization.GetString("GamutPointerNotValidArea")); return; }
        if (ChromaticityDiagram.FromScRgb(color) is not { } xy)
        {
            ClearChromaticitySample(Localization.GetString("GamutBlackOrInvalid"));
            return;
        }
        _chromaticitySample = xy;
        ChromaticitySampleText.Text = $"x {xy.X:0.0000}    y {xy.Y:0.0000}";
        static string Membership(IReadOnlyList<Vector2> gamut, Vector2 point) =>
            ChromaticityDiagram.Contains(gamut, point) ? Localization.GetString("GamutInside") : Localization.GetString("GamutOutside");
        ChromaticityGamutText.Text = $"sRGB {Membership(ChromaticityDiagram.Srgb, xy)} · P3 {Membership(ChromaticityDiagram.DisplayP3, xy)} · BT.2020 {Membership(ChromaticityDiagram.Bt2020, xy)}";
        if (ChromaticityDiagram.Bin(xy) < 0) ChromaticityGamutText.Text += $" · {Localization.GetString("GamutOutOfRange")}";
        PositionChromaticityMarker();
    }

    private Point ToChromaticityPoint(Vector2 xy) => new(_chromaticityLeft + xy.X * _chromaticityScale,
        _chromaticityTop + (ChromaticityDiagram.MaxY - xy.Y) * _chromaticityScale);

    private void PositionChromaticityMarker()
    {
        if (_chromaticityMarker is null) return;
        if (_chromaticitySample is not { } xy || ChromaticityDiagram.Bin(xy) < 0)
        {
            _chromaticityMarker.Visibility = Visibility.Collapsed;
            return;
        }
        var point = ToChromaticityPoint(xy);
        Canvas.SetLeft(_chromaticityMarker, point.X);
        Canvas.SetTop(_chromaticityMarker, point.Y);
        _chromaticityMarker.Visibility = Visibility.Visible;
    }

    private static WriteableBitmap CreateChromaticityBitmap(byte[] pixels)
    {
        var bitmap = new WriteableBitmap(ChromaticityDiagram.BinWidth, ChromaticityDiagram.BinHeight);
        using var stream = bitmap.PixelBuffer.AsStream();
        stream.Write(pixels);
        bitmap.Invalidate();
        return bitmap;
    }

    private void DrawChromaticity()
    {
        if (ChromaticityCanvas is null || Bt2020BoundaryToggle is null || ChromaticityCanvas.ActualWidth <= 0) return;
        ChromaticityCanvas.Children.Clear();
        _chromaticityMarker = null;
        var targetHeight = Math.Clamp((ChromaticityCanvas.ActualWidth - 48) * ChromaticityDiagram.MaxY / ChromaticityDiagram.MaxX + 56, 240, 420);
        if (Math.Abs(ChromaticityCanvas.Height - targetHeight) > 1) ChromaticityCanvas.Height = targetHeight;
        _chromaticityScale = Math.Min((ChromaticityCanvas.ActualWidth - 48) / ChromaticityDiagram.MaxX,
            (ChromaticityCanvas.ActualHeight - 56) / ChromaticityDiagram.MaxY);
        if (_chromaticityScale <= 0) return;
        _chromaticityTop = 16;
        var width = _chromaticityScale * ChromaticityDiagram.MaxX;
        var height = _chromaticityScale * ChromaticityDiagram.MaxY;
        _chromaticityLeft = 36 + (ChromaticityCanvas.ActualWidth - 48 - width) / 2;
        _chromaticityBackground ??= CreateChromaticityBitmap(ChromaticityDiagram.CreateBackground());
        void AddBitmap(WriteableBitmap bitmap, double opacity = 1)
        {
            var image = new Image { Source = bitmap, Width = width, Height = height, Stretch = Stretch.Fill, Opacity = opacity };
            Canvas.SetLeft(image, _chromaticityLeft); Canvas.SetTop(image, _chromaticityTop);
            ChromaticityCanvas.Children.Add(image);
        }
        AddBitmap(_chromaticityBackground, ShowGamutComparison ? .18 : .40);
        // Resolve brushes from themed elements in this page. Application resources
        // can still point at the system theme when the app explicitly selects Light.
        var foreground = GamutSummaryText.Foreground;
        var gridBrush = GamutPlotSurface.BorderBrush;
        void Label(string text, double x, double y, bool center = false, bool right = false)
        {
            var label = new TextBlock
            {
                Text = text,
                Foreground = foreground,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"]
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (center) x -= label.DesiredSize.Width / 2;
            if (right) x -= label.DesiredSize.Width;
            Canvas.SetLeft(label, x); Canvas.SetTop(label, y); ChromaticityCanvas.Children.Add(label);
        }
        for (var tick = 0; tick <= 4; tick++)
        {
            var coordinate = tick * .2f;
            var px = ToChromaticityPoint(new Vector2(coordinate, 0)).X;
            var py = ToChromaticityPoint(new Vector2(0, coordinate)).Y;
            ChromaticityCanvas.Children.Add(new Line { X1 = px, X2 = px, Y1 = _chromaticityTop, Y2 = _chromaticityTop + height, Stroke = gridBrush, Opacity = .55 });
            ChromaticityCanvas.Children.Add(new Line { X1 = _chromaticityLeft, X2 = _chromaticityLeft + width, Y1 = py, Y2 = py, Stroke = gridBrush, Opacity = .55 });
            Label($"{coordinate:0.0}", px, _chromaticityTop + height + 4, center: true);
            Label($"{coordinate:0.0}", _chromaticityLeft - 8, py - 8, right: true);
        }
        Label("y", _chromaticityLeft - 18, 0);
        Label("x", _chromaticityLeft + width - 8, _chromaticityTop + height + 18);
        var snapshot = _renderer.AnalysisSnapshot;
        if (ShowGamutComparison && snapshot?.Version != _renderer.PreviewVersion) snapshot = null;
        var dark = ActualTheme == ElementTheme.Dark;
        static SolidColorBrush Swatch(Vector3 color) => new(Windows.UI.Color.FromArgb(255, (byte)color.X, (byte)color.Y, (byte)color.Z));
        GamutSdrSwatch.Fill = Swatch(GainMapChromaticityComparison.SdrColor(dark));
        GamutAlternateSwatch.Fill = Swatch(GainMapChromaticityComparison.AlternateColor(dark));
        GamutOverlapSwatch.Fill = Swatch(GainMapChromaticityComparison.OverlapColor(dark));
        if (!ReferenceEquals(snapshot, _chromaticitySnapshot) || _chromaticityShowingComparison != ShowGamutComparison)
        {
            _chromaticitySnapshot = snapshot;
            _chromaticityShowingComparison = ShowGamutComparison;
            _chromaticityDensity = null;
            if (ShowGamutComparison)
            {
                if (snapshot?.GainMapComparison is { } pair)
                    _chromaticityDensity = CreateChromaticityBitmap(pair.CreateDensityPixels(dark,
                        GamutSdrLayerToggle.IsChecked == true, GamutAlternateLayerToggle.IsChecked == true));
            }
            else if (snapshot is { ChromaticityCount: > 0 })
            {
                var pixels = new byte[ChromaticityDiagram.BinWidth * ChromaticityDiagram.BinHeight * 4];
                var max = Math.Log(1 + snapshot.ChromaticityBins.Max());
                for (var i = 0; i < snapshot.ChromaticityBins.Length; i++)
                {
                    if (snapshot.ChromaticityBins[i] == 0) continue;
                    var x = i % ChromaticityDiagram.BinWidth;
                    var y = i / ChromaticityDiagram.BinWidth;
                    var xy = new Vector2((x + .5f) / ChromaticityDiagram.BinWidth * ChromaticityDiagram.MaxX,
                        (y + .5f) / ChromaticityDiagram.BinHeight * ChromaticityDiagram.MaxY);
                    var rgb = Vector3.Lerp(ChromaticityDiagram.IllustrationColor(xy), Vector3.One, .35f);
                    var alpha = (byte)(48 + 192 * Math.Pow(Math.Log(1 + snapshot.ChromaticityBins[i]) / max, .65));
                    var offset = ((ChromaticityDiagram.BinHeight - 1 - y) * ChromaticityDiagram.BinWidth + x) * 4;
                    pixels[offset] = (byte)(rgb.Z * alpha); pixels[offset + 1] = (byte)(rgb.Y * alpha);
                    pixels[offset + 2] = (byte)(rgb.X * alpha); pixels[offset + 3] = alpha;
                }
                _chromaticityDensity = CreateChromaticityBitmap(pixels);
            }
        }
        if (_chromaticityDensity is not null) AddBitmap(_chromaticityDensity);
        void Outline(IReadOnlyList<Vector2> vertices, Brush brush, DoubleCollection? dash = null, double opacity = .55)
        {
            var points = new PointCollection();
            foreach (var vertex in vertices) points.Add(ToChromaticityPoint(vertex));
            points.Add(ToChromaticityPoint(vertices[0]));
            var line = new Polyline { Points = points, Stroke = brush, StrokeThickness = 1, StrokeLineJoin = PenLineJoin.Round, Opacity = opacity };
            if (dash is not null) line.StrokeDashArray = dash;
            ChromaticityCanvas.Children.Add(line);
        }
        Outline(ChromaticityDiagram.SpectralLocus, foreground, opacity: .8);
        void Boundary(IReadOnlyList<Vector2> vertices, string name, DoubleCollection? dash = null)
        {
            Outline(vertices, foreground, dash);
            var top = ToChromaticityPoint(vertices[1]);
            Label(name, top.X + 4, top.Y - 20);
        }
        if (SrgbBoundaryToggle.IsChecked == true) Boundary(ChromaticityDiagram.Srgb, "sRGB");
        if (P3BoundaryToggle.IsChecked == true) Boundary(ChromaticityDiagram.DisplayP3, "P3", new DoubleCollection { 4, 3 });
        if (Bt2020BoundaryToggle.IsChecked == true) Boundary(ChromaticityDiagram.Bt2020, "2020", new DoubleCollection { 1, 3 });
        var white = ToChromaticityPoint(ChromaticityDiagram.WhitePoint);
        var whiteRing = new Ellipse { Width = 6, Height = 6, Stroke = foreground, StrokeThickness = 1 };
        Canvas.SetLeft(whiteRing, white.X - 3); Canvas.SetTop(whiteRing, white.Y - 3); ChromaticityCanvas.Children.Add(whiteRing);
        Label("D65", white.X - 10, white.Y + 6, right: true);
        _chromaticityMarker = new Canvas { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        foreach (var thickness in new[] { 3.0, 1.0 })
        {
            var brush = new SolidColorBrush(thickness == 3 ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
            _chromaticityMarker.Children.Add(new Line { X1 = -8, X2 = 8, Y1 = 0, Y2 = 0, Stroke = brush, StrokeThickness = thickness });
            _chromaticityMarker.Children.Add(new Line { X1 = 0, X2 = 0, Y1 = -8, Y2 = 8, Stroke = brush, StrokeThickness = thickness });
        }
        ChromaticityCanvas.Children.Add(_chromaticityMarker);
        PositionChromaticityMarker();
    }
}
