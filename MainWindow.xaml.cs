using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using HdrImageViewer.Pages;
using HdrImageViewer.Services;
using HdrImageViewer.ViewModels;
using System.ComponentModel;
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
        ViewerCommandsHost.Children.Add(ViewerPage.TakeCommandBar());

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");
        ViewerPage.ViewModel.PropertyChanged += ViewerViewModel_PropertyChanged;
        UpdateWindowTitle();
        ResizeToInitialDesignSize();
        AppWindow.Changed += AppWindow_Changed;
        AppSettingsService.SettingsChanged += AppSettingsService_SettingsChanged;
        Closed += MainWindow_Closed;
        WindowRoot.ActualThemeChanged += WindowRoot_ActualThemeChanged;
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
        WindowRoot.ActualThemeChanged -= WindowRoot_ActualThemeChanged;
        ViewerPage.ViewModel.PropertyChanged -= ViewerViewModel_PropertyChanged;
        AppWindow.Changed -= AppWindow_Changed;
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
        UpdateCaptionTheme();
    }

    private void WindowRoot_ActualThemeChanged(FrameworkElement sender, object args) => UpdateCaptionTheme();

    private void UpdateCaptionTheme() => AppWindow.TitleBar.PreferredTheme =
        WindowRoot.ActualTheme == ElementTheme.Dark ? TitleBarTheme.Dark : TitleBarTheme.Light;

    private void ViewerTitleContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep a readable filename and a native drag region as commands overflow.
        ViewerCommandsHost.MaxWidth = Math.Max(48, e.NewSize.Width - Math.Min(360, e.NewSize.Width * 0.35) - 24);
        AppTitleBar.RecomputeDragRegions();
    }

    private void ViewerCommandsHost_SizeChanged(object sender, SizeChangedEventArgs e) => AppTitleBar.RecomputeDragRegions();

    private void WindowRoot_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (ViewerPage.Visibility == Visibility.Visible)
            ViewerPage.HandleViewerKeyDown(sender, e);
    }

    private void ViewerViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ImageWorkspaceViewModel.FileName) or nameof(ImageWorkspaceViewModel.HasImage))
        {
            UpdateWindowTitle();
        }
    }

    private void UpdateWindowTitle()
    {
        var fileName = ViewerPage.ViewModel.FileName;
        var title = ViewerPage.ViewModel.HasImage && !string.IsNullOrWhiteSpace(fileName)
            ? fileName
            : Localization.GetString("AppTitle");
        if (NavFrame.Visibility == Visibility.Visible)
            title = Localization.GetString("AppTitle");
        var showingContentPage = NavFrame.Visibility == Visibility.Visible;
        ViewerTitleContent.Visibility = showingContentPage ? Visibility.Collapsed : Visibility.Visible;
        AppTitleBar.Title = showingContentPage ? title : string.Empty;
        ViewerFileName.Text = title;
        ToolTipService.SetToolTip(ViewerFileName, title);
        AutomationProperties.SetName(AppTitleBar, title);
        AppWindow.Title = title;
    }

    private async void WindowRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isInitialNavigationComplete)
        {
            return;
        }

        _isInitialNavigationComplete = true;
        UpdateCaptionTheme();
        ShowViewerPage();
        if (_activationFilePaths.Count > 0)
        {
            await ViewerPage.OpenImagePathsAsync(_activationFilePaths);
        }
    }

    public void NavigateToSettings() => ShowContentPage(typeof(SettingsPage));

    public void NavigateToAbout() => ShowContentPage(typeof(AboutPage));

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        ShowViewerPage();
    }

    private void ShowViewerPage()
    {
        ViewerPage.Visibility = Visibility.Visible;
        NavFrame.Visibility = Visibility.Collapsed;
        NavFrame.BackStack.Clear();
        AppTitleBar.IsBackButtonVisible = false;
        UpdateWindowTitle();
    }

    private void ShowContentPage(Type pageType)
    {
        AppTitleBar.IsBackButtonVisible = true;
        AppTitleBar.IsBackButtonEnabled = true;
        if (NavFrame.CurrentSourcePageType == pageType)
        {
            ViewerPage.Visibility = Visibility.Collapsed;
            NavFrame.Visibility = Visibility.Visible;
            UpdateWindowTitle();
            return;
        }

        ViewerPage.Visibility = Visibility.Collapsed;
        NavFrame.Visibility = Visibility.Visible;
        UpdateWindowTitle();
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
        ImmersiveViewingChanged?.Invoke(this, isImmersive);
    }
}
