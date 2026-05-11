// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using HdrImageViewer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace HdrImageViewer.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _isLoadingSettings;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoadingSettings = true;
        var settings = AppSettingsService.Current;
        MouseWheelBehaviorSelector.SelectedIndex = settings.MouseWheelBehavior == MouseWheelBehavior.ZoomImage ? 1 : 0;
        TouchpadGesturesToggle.IsOn = settings.TouchpadGesturesEnabled;
        PreloadAdjacentImagesToggle.IsOn = settings.PreloadAdjacentImages;
        _isLoadingSettings = false;
    }

    private void MouseWheelBehaviorSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || MouseWheelBehaviorSelector.SelectedIndex < 0)
        {
            return;
        }

        AppSettingsService.SetMouseWheelBehavior(
            MouseWheelBehaviorSelector.SelectedIndex == 1
                ? MouseWheelBehavior.ZoomImage
                : MouseWheelBehavior.NavigateImages);
    }

    private void TouchpadGesturesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        AppSettingsService.SetTouchpadGesturesEnabled(TouchpadGesturesToggle.IsOn);
    }

    private void PreloadAdjacentImagesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        AppSettingsService.SetPreloadAdjacentImages(PreloadAdjacentImagesToggle.IsOn);
    }
}
