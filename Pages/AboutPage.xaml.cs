using System.Reflection;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace HdrImageViewer.Pages;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = $"版本：{GetAppVersion()}";
    }

    private static string GetAppVersion()
    {
        try
        {
            var version = Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
        catch
        {
            var informationalVersion = typeof(App).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informationalVersion))
            {
                return informationalVersion.Split('+')[0];
            }

            return typeof(App).Assembly.GetName().Version?.ToString() ?? "未知";
        }
    }
}
