// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using HdrImageViewer.Services;
using HdrImageViewer.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

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

        MouseWheelNavigateItem.Content = Localization.GetString("SettingsMouseWheelNavigate");
        MouseWheelZoomItem.Content = Localization.GetString("SettingsMouseWheelZoom");
        LanguageSystemItem.Content = Localization.GetString("SettingsLanguageSystem");
        ThemeSystemItem.Content = Localization.GetString("SettingsThemeSystem");
        ThemeLightItem.Content = Localization.GetString("SettingsThemeLight");
        ThemeDarkItem.Content = Localization.GetString("SettingsThemeDark");
        GamutManagedItem.Content = Localization.GetString("SettingsGamutManaged");
        GamutClipItem.Content = Localization.GetString("SettingsGamutClip");

        TouchpadGesturesToggle.OnContent = Localization.GetString("SettingsToggleOn");
        TouchpadGesturesToggle.OffContent = Localization.GetString("SettingsToggleOff");
        PreloadAdjacentImagesToggle.OnContent = Localization.GetString("SettingsToggleOn");
        PreloadAdjacentImagesToggle.OffContent = Localization.GetString("SettingsToggleOff");
        ShowInspectorPanelToggle.OnContent = Localization.GetString("SettingsToggleOn");
        ShowInspectorPanelToggle.OffContent = Localization.GetString("SettingsToggleOff");
        ShowFilmstripToggle.OnContent = Localization.GetString("SettingsToggleOn");
        ShowFilmstripToggle.OffContent = Localization.GetString("SettingsToggleOff");
        LanguageRestartInfoBar.Message = Localization.GetString("SettingsLanguageRestartTip.Text");

        MouseWheelBehaviorSelector.SelectedIndex = settings.MouseWheelBehavior == MouseWheelBehavior.ZoomImage ? 1 : 0;
        ThemeSelector.SelectedIndex = settings.Theme switch
        {
            AppTheme.Light => 1,
            AppTheme.Dark => 2,
            _ => 0,
        };

        var currentLang = settings.Language;
        var langIndex = 0;
        for (int i = 0; i < LanguageSelector.Items.Count; i++)
        {
            if (LanguageSelector.Items[i] is ComboBoxItem item && string.Equals(item.Tag?.ToString(), currentLang, StringComparison.OrdinalIgnoreCase))
            {
                langIndex = i;
                break;
            }
        }
        LanguageSelector.SelectedIndex = langIndex;

        TouchpadGesturesToggle.IsOn = settings.TouchpadGesturesEnabled;
        PreloadAdjacentImagesToggle.IsOn = settings.PreloadAdjacentImages;
        AdjacentPreloadRadiusBox.Value = settings.AdjacentPreloadRadius;
        AdjacentPreloadRadiusBox.IsEnabled = settings.PreloadAdjacentImages;
        ShowInspectorPanelToggle.IsOn = settings.ShowInspectorPanel;
        ShowFilmstripToggle.IsOn = settings.ShowFilmstrip;
        ColorGamutMappingModeSelector.SelectedIndex = settings.ColorGamutMappingMode switch
        {
            ColorGamutMappingMode.Clip => 1,
            _ => 0,
        };
        _isLoadingSettings = false;
    }

    private void LanguageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || LanguageSelector.SelectedIndex < 0)
        {
            return;
        }

        if (LanguageSelector.SelectedItem is ComboBoxItem item)
        {
            var langTag = item.Tag?.ToString() ?? string.Empty;
            if (!string.Equals(AppSettingsService.Current.Language, langTag, StringComparison.OrdinalIgnoreCase))
            {
                AppSettingsService.SetLanguage(langTag);
                LanguageRestartInfoBar.IsOpen = true;
            }
        }
    }

    private void RestartNowButton_Click(object sender, RoutedEventArgs e)
    {
        Microsoft.Windows.AppLifecycle.AppInstance.Restart(string.Empty);
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

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || ThemeSelector.SelectedIndex < 0)
        {
            return;
        }

        AppSettingsService.SetTheme(ThemeSelector.SelectedIndex switch
        {
            1 => AppTheme.Light,
            2 => AppTheme.Dark,
            _ => AppTheme.System,
        });
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
        AdjacentPreloadRadiusBox.IsEnabled = PreloadAdjacentImagesToggle.IsOn;
    }

    private void AdjacentPreloadRadiusBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isLoadingSettings || double.IsNaN(args.NewValue))
        {
            return;
        }

        var radius = AppUserSettings.NormalizeAdjacentPreloadRadius((int)Math.Round(args.NewValue));
        if (Math.Abs(sender.Value - radius) > 0.001)
        {
            sender.Value = radius;
        }

        AppSettingsService.SetAdjacentPreloadRadius(radius);
    }

    private void ShowInspectorPanelToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        AppSettingsService.SetShowInspectorPanel(ShowInspectorPanelToggle.IsOn);
    }

    private void ShowFilmstripToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        AppSettingsService.SetShowFilmstrip(ShowFilmstripToggle.IsOn);
    }

    private void ColorGamutMappingModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || ColorGamutMappingModeSelector.SelectedIndex < 0)
        {
            return;
        }

        AppSettingsService.SetColorGamutMappingMode(ColorGamutMappingModeSelector.SelectedIndex switch
        {
            1 => ColorGamutMappingMode.Clip,
            _ => ColorGamutMappingMode.Managed,
        });
    }

    private async void OpenDefaultAppsSettings_Click(object sender, RoutedEventArgs e)
    {
        await Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps"));
    }
}
