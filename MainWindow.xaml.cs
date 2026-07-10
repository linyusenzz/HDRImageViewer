using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using HdrImageViewer.Pages;
using HdrImageViewer.Services;
using System.Runtime.InteropServices;
using Windows.Graphics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace HdrImageViewer;

public sealed partial class MainWindow : Window
{
    private const int InitialWindowWidthDips = 1320;
    private const int InitialWindowHeightDips = 840;
    private const int MinimumWindowWidthDips = 420;
    private const int MinimumWindowHeightDips = 320;

    private readonly IReadOnlyList<string> _activationFilePaths;
    private bool _isInitialNavigationComplete;
    private bool _isImmersiveViewing;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    public event EventHandler<bool>? ImmersiveViewingChanged;

    public bool IsImmersiveViewing => _isImmersiveViewing;

    public MainWindow(IReadOnlyList<string>? activationFilePaths = null)
    {
        _activationFilePaths = activationFilePaths ?? [];
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Title = "HDR 图片查看器";
        ResizeToInitialDesignSize();
        AppWindow.Changed += AppWindow_Changed;
        AppSettingsService.SettingsChanged += AppSettingsService_SettingsChanged;
        Closed += MainWindow_Closed;
        ApplyTheme(AppSettingsService.Current.Theme);
    }

    private void ResizeToInitialDesignSize()
    {
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(InitialWindowWidthDips * scale),
            (int)Math.Round(InitialWindowHeightDips * scale)));
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        AppSettingsService.SettingsChanged -= AppSettingsService_SettingsChanged;
    }

    private void AppSettingsService_SettingsChanged(object? sender, EventArgs e)
    {
        ApplyTheme(AppSettingsService.Current.Theme);
    }

    private void ApplyTheme(AppTheme theme)
    {
        WindowRoot.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    private async void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isInitialNavigationComplete)
        {
            return;
        }

        _isInitialNavigationComplete = true;
        if (NavView.SettingsItem is NavigationViewItem settingsItem)
        {
            settingsItem.Content = "设置";
            AutomationProperties.SetAutomationId(settingsItem, "SettingsNavigationItem");
            AutomationProperties.SetName(settingsItem, "设置");
        }

        ShowViewerPage();
        if (_activationFilePaths.Count > 0)
        {
            await ViewerPage.OpenImagePathsAsync(_activationFilePaths);
        }
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        ShowViewerPage();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ShowContentPage(typeof(SettingsPage));
        }
        else if (args.SelectedItem is NavigationViewItem item)
        {
            switch (item.Tag)
            {
                case "viewer":
                case "home":
                    ShowViewerPage();
                    break;
                case "about":
                    ShowContentPage(typeof(AboutPage));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown navigation item tag: {item.Tag}");
            }
        }
    }

    private void ShowViewerPage()
    {
        ViewerPage.Visibility = Visibility.Visible;
        NavFrame.Visibility = Visibility.Collapsed;
        NavFrame.BackStack.Clear();
    }

    private void ShowContentPage(Type pageType)
    {
        if (NavFrame.CurrentSourcePageType == pageType)
        {
            ViewerPage.Visibility = Visibility.Collapsed;
            NavFrame.Visibility = Visibility.Visible;
            return;
        }

        ViewerPage.Visibility = Visibility.Collapsed;
        NavFrame.Visibility = Visibility.Visible;
        NavFrame.Navigate(pageType);
        NavFrame.BackStack.Clear();
    }

    public bool ToggleImmersiveViewing()
    {
        SetImmersiveViewing(!IsImmersiveViewing);
        return IsImmersiveViewing;
    }

    public void SetImmersiveViewing(bool isImmersive)
    {
        if (isImmersive)
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
        else
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);
        }

        ApplyImmersiveShell(isImmersive);
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange)
        {
            EnforceMinimumWindowSize(sender);
        }

        if (args.DidPresenterChange)
        {
            ApplyImmersiveShell(sender.Presenter.Kind == AppWindowPresenterKind.FullScreen);
        }
    }

    private static void EnforceMinimumWindowSize(AppWindow window)
    {
        if (window.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            return;
        }

        var hwnd = Win32Interop.GetWindowFromWindowId(window.Id);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var minimumWidth = (int)Math.Ceiling(MinimumWindowWidthDips * scale);
        var minimumHeight = (int)Math.Ceiling(MinimumWindowHeightDips * scale);
        if (window.Size.Width < minimumWidth || window.Size.Height < minimumHeight)
        {
            window.Resize(new SizeInt32(
                Math.Max(window.Size.Width, minimumWidth),
                Math.Max(window.Size.Height, minimumHeight)));
        }
    }

    private void ApplyImmersiveShell(bool isImmersive)
    {
        if (_isImmersiveViewing == isImmersive)
        {
            return;
        }

        _isImmersiveViewing = isImmersive;
        AppTitleBar.Visibility = isImmersive ? Visibility.Collapsed : Visibility.Visible;
        TitleBarRow.Height = isImmersive ? new GridLength(0) : new GridLength(48);
        NavView.IsPaneVisible = !isImmersive;
        NavView.IsSettingsVisible = !isImmersive;
        ImmersiveViewingChanged?.Invoke(this, isImmersive);
    }
}
