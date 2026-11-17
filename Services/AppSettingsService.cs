using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using HdrImageViewer.Rendering;

namespace HdrImageViewer.Services;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppUserSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext
{
}

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

    public bool SkipCloudPlaceholders { get; set; } = true;

    public bool ShowInspectorPanel { get; set; } = true;

    public bool ShowFilmstrip { get; set; } = true;

    public ColorGamutMappingMode ColorGamutMappingMode { get; set; } = ColorGamutMappingMode.Managed;

    public GainmapViewMode HdrViewMode { get; set; } = GainmapViewMode.Adaptive;

    public string Language { get; set; } = string.Empty;

    public static GainmapViewMode NormalizeHdrViewMode(GainmapViewMode mode)
    {
        return Enum.IsDefined(mode) ? mode : GainmapViewMode.Adaptive;
    }

    public GainmapViewMode GetEffectiveHdrViewMode(bool hasGainMap, bool isSingleLayerHdr)
    {
        // A document-specific fallback must never replace the saved preference.
        if (!hasGainMap && !isSingleLayerHdr)
        {
            return GainmapViewMode.Sdr;
        }

        var preferredMode = NormalizeHdrViewMode(HdrViewMode);
        return preferredMode == GainmapViewMode.GainMap && !hasGainMap
            ? GainmapViewMode.Adaptive
            : preferredMode;
    }

    public static int NormalizeAdjacentPreloadRadius(int radius)
    {
        return Math.Clamp(radius, MinAdjacentPreloadRadius, MaxAdjacentPreloadRadius);
    }
}

public static class AppSettingsService
{
    internal const string SettingsPathEnvironmentVariable = "HDRIV_SETTINGS_PATH";
    private const int SaveDebounceMilliseconds = 150;

    private static readonly object s_lock = new();
    private static readonly object s_saveGate = new();
    private static readonly Timer s_saveTimer = new(static _ => FlushPendingSave(), null, Timeout.Infinite, Timeout.Infinite);
    private static string s_settingsPath = ResolveDefaultSettingsPath();
    private static AppUserSettings? s_current;
    private static PendingSave? s_pendingSave;
    private static long s_saveVersion;

    static AppSettingsService()
    {
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => FlushPendingSave();
    }

    public static event EventHandler? SettingsChanged;

    public static AppUserSettings Current => Volatile.Read(ref s_current) ?? EnsureLoaded();

    public static void SetMouseWheelBehavior(MouseWheelBehavior behavior)
    {
        UpdateSettings(settings => settings.MouseWheelBehavior = behavior);
    }

    public static void SetTheme(AppTheme theme)
    {
        UpdateSettings(settings => settings.Theme = Enum.IsDefined(theme) ? theme : AppTheme.System);
    }

    public static void SetTouchpadGesturesEnabled(bool enabled)
    {
        UpdateSettings(settings => settings.TouchpadGesturesEnabled = enabled);
    }

    public static void SetPreloadAdjacentImages(bool enabled)
    {
        UpdateSettings(settings => settings.PreloadAdjacentImages = enabled);
    }

    public static void SetAdjacentPreloadRadius(int radius)
    {
        UpdateSettings(settings => settings.AdjacentPreloadRadius = AppUserSettings.NormalizeAdjacentPreloadRadius(radius));
    }

    public static void SetSkipCloudPlaceholders(bool enabled)
    {
        UpdateSettings(settings => settings.SkipCloudPlaceholders = enabled);
    }

    public static void SetShowInspectorPanel(bool enabled)
    {
        UpdateSettings(settings => settings.ShowInspectorPanel = enabled);
    }

    public static void SetShowFilmstrip(bool enabled)
    {
        UpdateSettings(settings => settings.ShowFilmstrip = enabled);
    }

    public static void SetColorGamutMappingMode(ColorGamutMappingMode mode)
    {
        UpdateSettings(settings => settings.ColorGamutMappingMode = Enum.IsDefined(mode)
            ? mode
            : ColorGamutMappingMode.Managed);
    }

    public static void SetLanguage(string language)
    {
        UpdateSettings(settings => settings.Language = language ?? string.Empty);
        FlushPendingSave();
    }

    public static void SetHdrViewMode(GainmapViewMode mode)
    {
        UpdateSettings(settings => settings.HdrViewMode = AppUserSettings.NormalizeHdrViewMode(mode));
    }

    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static void FlushPendingSave()
    {
        lock (s_saveGate)
        {
            PendingSave? pending;
            lock (s_lock)
            {
                pending = s_pendingSave;
                if (pending is null)
                {
                    return;
                }

                s_saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }

            while (pending is not null)
            {
                SaveSettings(pending.Settings, pending.Path);

                lock (s_lock)
                {
                    if (s_pendingSave is not null && s_pendingSave.Version == pending.Version)
                    {
                        s_pendingSave = null;
                        pending = null;
                    }
                    else
                    {
                        pending = s_pendingSave;
                        if (pending is not null)
                        {
                            s_saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
                        }
                    }
                }
            }
        }
    }

    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static IDisposable OverrideFilePathForTesting(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        FlushPendingSave();

        string previousPath;
        string? previousEnvironmentValue;
        lock (s_lock)
        {
            previousPath = s_settingsPath;
            previousEnvironmentValue = Environment.GetEnvironmentVariable(SettingsPathEnvironmentVariable);
            s_settingsPath = fullPath;
            Environment.SetEnvironmentVariable(SettingsPathEnvironmentVariable, fullPath);
            Volatile.Write(ref s_current, LoadSettings(fullPath));
        }

        return new SettingsPathOverrideScope(previousPath, previousEnvironmentValue);
    }

    private static AppUserSettings EnsureLoaded()
    {
        lock (s_lock)
        {
            var existing = s_current;
            if (existing is not null)
            {
                return existing;
            }

            var loaded = LoadSettings(s_settingsPath);
            Volatile.Write(ref s_current, loaded);
            return loaded;
        }
    }

    private static void UpdateSettings(Action<AppUserSettings> update)
    {
        lock (s_lock)
        {
            var current = s_current ?? LoadSettings(s_settingsPath);
            var next = Copy(current);
            update(next);
            next = Copy(next);
            Volatile.Write(ref s_current, next);
            var version = ++s_saveVersion;
            s_pendingSave = new PendingSave(next, s_settingsPath, version);
            s_saveTimer.Change(SaveDebounceMilliseconds, Timeout.Infinite);
        }

        SettingsChanged?.Invoke(null, EventArgs.Empty);
    }

    private static string ResolveDefaultSettingsPath()
    {
        var environmentPath = Environment.GetEnvironmentVariable(SettingsPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            return Path.GetFullPath(environmentPath);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HdrImageViewer",
            "settings.json");
    }

    internal static AppUserSettings LoadSettings(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return new AppUserSettings();
            }

            var json = File.ReadAllText(settingsPath);
            var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppUserSettings)
                ?? new AppUserSettings();
            if (!Enum.IsDefined(settings.Theme))
            {
                settings.Theme = AppTheme.System;
            }

            if (!Enum.IsDefined(settings.ColorGamutMappingMode))
            {
                settings.ColorGamutMappingMode = ColorGamutMappingMode.Managed;
            }

            settings.AdjacentPreloadRadius = AppUserSettings.NormalizeAdjacentPreloadRadius(settings.AdjacentPreloadRadius);
            settings.HdrViewMode = AppUserSettings.NormalizeHdrViewMode(settings.HdrViewMode);
            settings.Language ??= string.Empty;
            return settings;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppSettingsService] Failed to load settings from '{settingsPath}': {ex.Message}");
            return new AppUserSettings();
        }
    }

    internal static void SaveSettings(AppUserSettings settings, string settingsPath)
    {
        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppUserSettings);
            temporaryPath = settingsPath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, settingsPath, overwrite: true);
            temporaryPath = null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppSettingsService] Failed to save settings to '{settingsPath}': {ex.Message}");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception cleanupEx)
                {
                    Debug.WriteLine($"[AppSettingsService] Failed to delete temporary settings file '{temporaryPath}': {cleanupEx.Message}");
                }
            }
        }
    }

    internal static AppUserSettings Copy(AppUserSettings settings)
    {
        return new AppUserSettings
        {
            MouseWheelBehavior = settings.MouseWheelBehavior,
            Theme = Enum.IsDefined(settings.Theme) ? settings.Theme : AppTheme.System,
            TouchpadGesturesEnabled = settings.TouchpadGesturesEnabled,
            PreloadAdjacentImages = settings.PreloadAdjacentImages,
            AdjacentPreloadRadius = AppUserSettings.NormalizeAdjacentPreloadRadius(settings.AdjacentPreloadRadius),
            SkipCloudPlaceholders = settings.SkipCloudPlaceholders,
            ShowInspectorPanel = settings.ShowInspectorPanel,
            ShowFilmstrip = settings.ShowFilmstrip,
            ColorGamutMappingMode = Enum.IsDefined(settings.ColorGamutMappingMode)
                ? settings.ColorGamutMappingMode
                : ColorGamutMappingMode.Managed,
            Language = settings.Language ?? string.Empty,
            HdrViewMode = AppUserSettings.NormalizeHdrViewMode(settings.HdrViewMode),
        };
    }

    private sealed record PendingSave(AppUserSettings Settings, string Path, long Version);

    private sealed class SettingsPathOverrideScope(string previousPath, string? previousEnvironmentValue) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            FlushPendingSave();
            lock (s_lock)
            {
                s_settingsPath = previousPath;
                Environment.SetEnvironmentVariable(SettingsPathEnvironmentVariable, previousEnvironmentValue);
                Volatile.Write(ref s_current, LoadSettings(previousPath));
            }
        }
    }
}
