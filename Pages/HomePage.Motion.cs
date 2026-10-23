using HdrImageViewer.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.Numerics;

namespace HdrImageViewer.Pages;

public sealed partial class HomePage
{
    internal void ConfigureVisibilityMotion(VisibilityMotion motion)
    {
        motion.Add(FilmstripRow, new Vector3(0, 12, 0));
        motion.Add(CropToolsPanel, new Vector3(0, 16, 0));
        motion.Add(InspectorScroll, new Vector3(0, 12, 0));
        motion.Add(ViewerToolsPanel, new Vector3(0, 12, 0));
        motion.Add(GamutToolsPanel, new Vector3(0, 12, 0));
        motion.Add(ComparisonControls, new Vector3(0, 8, 0));
    }

    private void SetInspectorLayoutImmediate(bool showInspector)
    {
        _inspectorStoryboard?.Stop();
        _inspectorStoryboard = null;
        _inspectorTargetVisible = showInspector;
        InspectorColumn.Width = new GridLength((showInspector || _isCropModeEnabled) && !_inspectorIsOverlay ? InspectorPanelWidth : 0);
        InspectorPanel.Visibility = showInspector ? Visibility.Visible : Visibility.Collapsed;
        InspectorPanel.IsHitTestVisible = showInspector;
        InspectorPanel.Opacity = 1;
        InspectorPanelTransform.X = 0;
    }

    private void AnimateInspectorLayout(bool showInspector)
    {
        // Resizing a visible pane is not another entrance.
        if (_inspectorTargetVisible == showInspector
            && (_inspectorStoryboard is not null || (InspectorPanel.Visibility == Visibility.Visible) == showInspector))
        {
            if (_inspectorStoryboard is null) SetInspectorLayoutImmediate(showInspector);
            return;
        }

        var wasVisible = InspectorPanel.Visibility == Visibility.Visible;
        var opacity = wasVisible ? InspectorPanel.Opacity : 0;
        var offset = wasVisible ? InspectorPanelTransform.X : InspectorAnimationOffset;
        _inspectorStoryboard?.Stop();
        _inspectorStoryboard = null;
        _inspectorTargetVisible = showInspector;
        InspectorPanel.Opacity = opacity;
        InspectorPanelTransform.X = offset;
        InspectorPanel.IsHitTestVisible = showInspector;
        if (showInspector)
        {
            InspectorColumn.Width = new GridLength(_inspectorIsOverlay ? 0 : InspectorPanelWidth);
            InspectorPanel.Visibility = Visibility.Visible;
        }
        else if (!wasVisible)
        {
            SetInspectorLayoutImmediate(false);
            return;
        }

        var targetOffset = showInspector ? 0 : InspectorAnimationOffset;
        var storyboard = CreatePanelMotion(InspectorPanel, InspectorPanelTransform, "X", opacity,
            offset, showInspector, targetOffset, InspectorAnimationOffset, InspectorAnimationMilliseconds);
        _inspectorStoryboard = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_inspectorStoryboard, storyboard)) return;
            SetInspectorLayoutImmediate(showInspector);
        };
        storyboard.Begin();
    }

    private bool ViewerChromeHasKeyboardFocus()
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is not Control { FocusState: FocusState.Keyboard or FocusState.Programmatic } focused)
            return false;
        for (DependencyObject? current = focused; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, PhotoToolbarOverlay)) return true;
        return false;
    }

    private void ViewerChromeHideTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_isPointerOverViewerChrome || ViewerChromeHasKeyboardFocus() || _isExportInProgress)
        {
            sender.Start();
            return;
        }
        if (IsImmersiveViewer && ViewModel.HasImage && !_isCropModeEnabled) SetViewerChromeVisible(false, true);
    }

    private void ViewerChrome_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverViewerChrome = true;
        _viewerChromeHideTimer?.Stop();
        SetViewerChromeVisible(true, true);
    }

    private void ViewerChrome_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverViewerChrome = false;
        ShowViewerChromeTemporarily();
    }

    private void ViewerChrome_GotFocus(object sender, RoutedEventArgs e) => ShowViewerChromeTemporarily();

    private void ShowViewerChromeTemporarily()
    {
        var canHide = IsImmersiveViewer && ViewModel.HasImage && !_isCropModeEnabled;
        SetViewerChromeVisible(true, canHide);
        _viewerChromeHideTimer?.Stop();
        if (canHide && !_isPointerOverViewerChrome) _viewerChromeHideTimer?.Start();
    }

    private void SetViewerChromeVisible(bool visible, bool animate)
    {
        if (PhotoToolbarOverlay is null || PhotoToolbarOverlayTransform is null) return;
        animate &= _motionSettings.AnimationsEnabled;
        if (animate && _isViewerChromeVisible == visible
            && (_viewerChromeStoryboard is not null || (PhotoToolbarOverlay.Visibility == Visibility.Visible) == visible)) return;

        var opacity = PhotoToolbarOverlay.Visibility == Visibility.Visible ? PhotoToolbarOverlay.Opacity : 0;
        var offset = PhotoToolbarOverlay.Visibility == Visibility.Visible ? PhotoToolbarOverlayTransform.Y : 18;
        _viewerChromeStoryboard?.Stop();
        _viewerChromeStoryboard = null;
        _isViewerChromeVisible = visible;
        PhotoToolbarOverlay.IsHitTestVisible = visible;
        if (!animate)
        {
            PhotoToolbarOverlay.Opacity = visible ? 1 : 0;
            PhotoToolbarOverlayTransform.Y = visible ? 0 : 18;
            PhotoToolbarOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        PhotoToolbarOverlay.Visibility = Visibility.Visible;
        PhotoToolbarOverlay.Opacity = opacity;
        PhotoToolbarOverlayTransform.Y = offset;
        var storyboard = CreatePanelMotion(PhotoToolbarOverlay, PhotoToolbarOverlayTransform, "Y", opacity,
            offset, visible, visible ? 0 : 18, 18, ViewerChromeAnimationMilliseconds);
        _viewerChromeStoryboard = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_viewerChromeStoryboard, storyboard)) return;
            SetViewerChromeVisible(visible, false);
        };
        storyboard.Begin();
    }

    private static Storyboard CreatePanelMotion(FrameworkElement panel, TranslateTransform transform,
        string axis, double opacity, double offset, bool entering, double targetOffset, double distance, double duration)
    {
        var storyboard = new Storyboard();
        var milliseconds = ViewerMotion.RemainingDuration(offset, targetOffset, distance, entering ? duration : duration * 0.8);
        AddDoubleAnimation(storyboard, panel, "Opacity", opacity, entering ? 1 : 0, milliseconds, entering);
        AddDoubleAnimation(storyboard, transform, axis, offset, targetOffset, milliseconds, entering);
        return storyboard;
    }

    private static void AddDoubleAnimation(Storyboard storyboard, DependencyObject target, string path,
        double from, double to, double milliseconds, bool entering)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = new CubicEase { EasingMode = entering ? EasingMode.EaseOut : EasingMode.EaseIn }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, path);
        storyboard.Children.Add(animation);
    }
}
