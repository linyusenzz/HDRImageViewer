using System.Text.Json;

namespace HdrImageViewer.Services;

public enum MouseWheelBehavior
{
    NavigateImages,
    ZoomImage,
}

public sealed class AppUserSettings
{
    public MouseWheelBehavior MouseWheelBehavior { get; set; } = MouseWheelBehavior.NavigateImages;

    public bool TouchpadGesturesEnabled { get; set; } = true;

    public bool PreloadAdjacentImages { get; set; } = true;

    public bool ShowInspectorPanel { get; set; } = true;

    public bool ShowFilmstrip { get; set; } = true;
}

public static class AppSettingsService
{
    private static readonly object s_lock = new();
    private static readonly string s_settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HdrImageViewer",
        "settings.json");
    private static AppUserSettings? s_current;

    public static event EventHandler? SettingsChanged;

    public static AppUserSettings Current
    {
        get
        {
            lock (s_lock)
            {
                s_current ??= LoadSettings();
                return Copy(s_current);
            }
        }
    }

    public static void SetMouseWheelBehavior(MouseWheelBehavior behavior)
    {
        Update(settings => settings.MouseWheelBehavior = behavior);
    }

    public static void SetTouchpadGesturesEnabled(bool enabled)
    {
        Update(settings => settings.TouchpadGesturesEnabled = enabled);
    }

    public static void SetPreloadAdjacentImages(bool enabled)
    {
        Update(settings => settings.PreloadAdjacentImages = enabled);
    }

    public static void SetShowInspectorPanel(bool enabled)
    {
        Update(settings => settings.ShowInspectorPanel = enabled);
    }

    public static void SetShowFilmstrip(bool enabled)
    {
        Update(settings => settings.ShowFilmstrip = enabled);
    }

    private static void Update(Action<AppUserSettings> update)
    {
        lock (s_lock)
        {
            s_current ??= LoadSettings();
            update(s_current);
            SaveSettings(s_current);
        }

        SettingsChanged?.Invoke(null, EventArgs.Empty);
    }

    private static AppUserSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(s_settingsPath))
            {
                return new AppUserSettings();
            }

            var json = File.ReadAllText(s_settingsPath);
            return JsonSerializer.Deserialize<AppUserSettings>(json) ?? new AppUserSettings();
        }
        catch
        {
            return new AppUserSettings();
        }
    }

    private static void SaveSettings(AppUserSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(s_settingsPath)!);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(s_settingsPath, json);
        }
        catch
        {
        }
    }

    private static AppUserSettings Copy(AppUserSettings settings)
    {
        return new AppUserSettings
        {
            MouseWheelBehavior = settings.MouseWheelBehavior,
            TouchpadGesturesEnabled = settings.TouchpadGesturesEnabled,
            PreloadAdjacentImages = settings.PreloadAdjacentImages,
            ShowInspectorPanel = settings.ShowInspectorPanel,
            ShowFilmstrip = settings.ShowFilmstrip,
        };
    }
}
