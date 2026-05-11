using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using HdrImageViewer.Pages;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace HdrImageViewer;

public sealed partial class MainWindow : Window
{
    private bool _isInitialNavigationComplete;
    private bool _isImmersiveViewing;

    public event EventHandler<bool>? ImmersiveViewingChanged;

    public bool IsImmersiveViewing => _isImmersiveViewing;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Title = "HDR 图片查看器";
        AppWindow.Changed += AppWindow_Changed;
    }

    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isInitialNavigationComplete)
        {
            return;
        }

        _isInitialNavigationComplete = true;
        if (NavView.SettingsItem is NavigationViewItem settingsItem)
        {
            settingsItem.Content = "设置";
            AutomationProperties.SetName(settingsItem, "设置");
        }

        ShowViewerPage();
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
        if (args.DidPresenterChange)
        {
            ApplyImmersiveShell(sender.Presenter.Kind == AppWindowPresenterKind.FullScreen);
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
