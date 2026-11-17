using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Windows.Storage;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace HdrImageViewer;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceKey = "HdrImageViewer.Main";
    private static AppInstance? s_mainInstance;

    public static Window? MainWindow { get; private set; }

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        Services.Localization.ApplyLanguagePreference(Services.AppSettingsService.Current.Language);

        InitializeComponent();
        HighContrastAdjustment = ApplicationHighContrastAdjustment.Auto;
        UnhandledException += App_UnhandledException;
        AppDomain.CurrentDomain.UnhandledException += AppDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        WriteCrashLog($"XAML unhandled exception: {e.Exception}");
    }

    private static void AppDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        WriteCrashLog($"AppDomain unhandled exception, terminating={e.IsTerminating}: {e.ExceptionObject}");
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog($"Unobserved task exception: {e.Exception}");
    }

    internal static void LogUnhandledUiException(string context, Exception exception)
    {
        WriteCrashLog($"{context}: {exception}");
    }

    internal static void UnregisterSingleInstance()
    {
        try
        {
            if (s_mainInstance is { } instance)
            {
                instance.Activated -= MainInstance_Activated;
                instance.UnregisterKey();
                s_mainInstance = null;
            }
        }
        catch (Exception ex)
        {
            WriteCrashLog($"Single-instance unregister failed: {ex}");
        }
    }

    private static void WriteCrashLog(string message)
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HdrImageViewer",
                "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(
                logPath,
                $"[{DateTimeOffset.Now:O}] {message}\n");
        }
        catch
        {
        }
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var activationFilePaths = GetActivationFilePaths(args);
        if (TryRedirectOrRegisterSingleInstance(activationFilePaths))
        {
            return;
        }

        MainWindow = new MainWindow(activationFilePaths);
        MainWindow.Activate();
    }

    private static bool TryRedirectOrRegisterSingleInstance(IReadOnlyList<string> activationFilePaths)
    {
        try
        {
            var mainInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);
            if (mainInstance.IsCurrent)
            {
                s_mainInstance = mainInstance;
                mainInstance.Activated += MainInstance_Activated;
                return false;
            }

            if (activationFilePaths.Count > 0)
            {
                var activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
                Task.Run(() => mainInstance.RedirectActivationToAsync(activatedArgs).AsTask())
                    .GetAwaiter()
                    .GetResult();
                Environment.Exit(0);
                return true;
            }
        }
        catch (Exception ex)
        {
            WriteCrashLog($"Single-instance registration or redirect failed: {ex}");
        }

        return false;
    }

    private static void MainInstance_Activated(object? sender, AppActivationArguments e)
    {
        var paths = ExtractActivatedFilePaths(e);
        if (MainWindow is MainWindow mainWindow)
        {
            _ = mainWindow.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await mainWindow.ActivateAndOpenFilesAsync(paths);
                }
                catch (Exception ex)
                {
                    WriteCrashLog($"Redirected file activation failed: {ex}");
                }
            });
        }
    }

    private static IReadOnlyList<string> ExtractActivatedFilePaths(AppActivationArguments? appActivationArgs)
    {
        try
        {
            if (appActivationArgs?.Kind == ExtendedActivationKind.File
                && appActivationArgs.Data is IFileActivatedEventArgs fileArgs)
            {
                return fileArgs.Files
                    .OfType<StorageFile>()
                    .Select(file => file.Path)
                    .Where(IsExistingPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (appActivationArgs?.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launchData)
            {
                var parsed = ParseCommandLinePaths(launchData.Arguments);
                if (parsed.Count > 0)
                {
                    return parsed;
                }
            }
        }
        catch (Exception ex)
        {
            WriteCrashLog($"File activation argument parsing failed: {ex}");
        }

        return [];
    }

    private static IReadOnlyList<string> GetActivationFilePaths(Microsoft.UI.Xaml.LaunchActivatedEventArgs launchArgs)
    {
        try
        {
            var extracted = ExtractActivatedFilePaths(AppInstance.GetCurrent().GetActivatedEventArgs());
            if (extracted.Count > 0)
            {
                return extracted;
            }
        }
        catch (Exception ex)
        {
            WriteCrashLog($"File activation argument parsing failed: {ex}");
        }

        var launchPaths = ParseCommandLinePaths(launchArgs.Arguments);
        if (launchPaths.Count > 0)
        {
            return launchPaths;
        }

        return Environment.GetCommandLineArgs()
            .Skip(1)
            .Where(IsExistingPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> ParseCommandLinePaths(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return [];
        }

        try
        {
            return SplitCommandLine(arguments)
                .Where(IsExistingPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            WriteCrashLog($"Launch argument parsing failed: {ex}");
            return [];
        }
    }

    private static IEnumerable<string> SplitCommandLine(string arguments)
    {
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var ch in arguments)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static bool IsExistingPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath)
                && string.Equals(Path.GetFullPath(path), Path.GetFullPath(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        catch
        {
        }

        var extension = Path.GetExtension(path);
        return !string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase);
    }
}
