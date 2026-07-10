using System.Text.Json;
using HdrImageViewer.Rendering;

namespace HdrImageViewer.Services;

public enum MouseWheelBehavior
{
    NavigateImages,
    ZoomImage,
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed class AppUserSettings
{
    public const int DefaultAdjacentPreloadRadius = 5;
    public const int MinAdjacentPreloadRadius = 1;
    public const int MaxAdjacentPreloadRadius = 12;

    public MouseWheelBehavior MouseWheelBehavior { get; set; } = MouseWheelBehavior.NavigateImages;

    public AppTheme Theme { get; set; } = AppTheme.System;

    public bool TouchpadGesturesEnabled { get; set; } = true;

    public bool PreloadAdjacentImages { get; set; } = true;

    public int AdjacentPreloadRadius { get; set; } = DefaultAdjacentPreloadRadius;

    public bool ShowInspectorPanel { get; set; } = true;

    public bool ShowFilmstrip { get; set; } = true;

    public ColorGamutMappingMode ColorGamutMappingMode { get; set; } = ColorGamutMappingMode.Managed;

    public static int NormalizeAdjacentPreloadRadius(int radius)
    {
        return Math.Clamp(radius, MinAdjacentPreloadRadius, MaxAdjacentPreloadRadius);
    }
}

public static class AppSettingsService
{
    private static readonly object s_lock = new();
    private static readonly JsonSerializerOptions s_writeOptions = new() { WriteIndented = true };
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

    public static void SetTheme(AppTheme theme)
    {
        Update(settings => settings.Theme = Enum.IsDefined(theme) ? theme : AppTheme.System);
    }

    public static void SetTouchpadGesturesEnabled(bool enabled)
    {
        Update(settings => settings.TouchpadGesturesEnabled = enabled);
    }

    public static void SetPreloadAdjacentImages(bool enabled)
    {
        Update(settings => settings.PreloadAdjacentImages = enabled);
    }

    public static void SetAdjacentPreloadRadius(int radius)
    {
        Update(settings => settings.AdjacentPreloadRadius = AppUserSettings.NormalizeAdjacentPreloadRadius(radius));
    }

    public static void SetShowInspectorPanel(bool enabled)
    {
        Update(settings => settings.ShowInspectorPanel = enabled);
    }

    public static void SetShowFilmstrip(bool enabled)
    {
        Update(settings => settings.ShowFilmstrip = enabled);
    }

    public static void SetColorGamutMappingMode(ColorGamutMappingMode mode)
    {
        Update(settings => settings.ColorGamutMappingMode = Enum.IsDefined(mode)
            ? mode
            : ColorGamutMappingMode.Managed);
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
            var settings = JsonSerializer.Deserialize<AppUserSettings>(json) ?? new AppUserSettings();
            if (!Enum.IsDefined(settings.Theme))
            {
                settings.Theme = AppTheme.System;
            }

            if (!Enum.IsDefined(settings.ColorGamutMappingMode))
            {
                settings.ColorGamutMappingMode = ColorGamutMappingMode.Managed;
            }

            settings.AdjacentPreloadRadius = AppUserSettings.NormalizeAdjacentPreloadRadius(settings.AdjacentPreloadRadius);
            return settings;
        }
        catch
        {
            return new AppUserSettings();
        }
    }

    private static void SaveSettings(AppUserSettings settings)
    {
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(s_settingsPath)!);
            var json = JsonSerializer.Serialize(settings, s_writeOptions);
            temporaryPath = s_settingsPath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, s_settingsPath, overwrite: true);
            temporaryPath = null;
        }
        catch
        {
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                }
            }
        }
    }

    private static AppUserSettings Copy(AppUserSettings settings)
    {
        return new AppUserSettings
        {
            MouseWheelBehavior = settings.MouseWheelBehavior,
            Theme = Enum.IsDefined(settings.Theme) ? settings.Theme : AppTheme.System,
            TouchpadGesturesEnabled = settings.TouchpadGesturesEnabled,
            PreloadAdjacentImages = settings.PreloadAdjacentImages,
            AdjacentPreloadRadius = AppUserSettings.NormalizeAdjacentPreloadRadius(settings.AdjacentPreloadRadius),
            ShowInspectorPanel = settings.ShowInspectorPanel,
            ShowFilmstrip = settings.ShowFilmstrip,
            ColorGamutMappingMode = Enum.IsDefined(settings.ColorGamutMappingMode)
                ? settings.ColorGamutMappingMode
                : ColorGamutMappingMode.Managed,
        };
    }
}
