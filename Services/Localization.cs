// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;

namespace HdrImageViewer.Services;

public static class Localization
{
#if WINDOWS
    private static readonly object s_loaderLock = new();
    private static Microsoft.Windows.ApplicationModel.Resources.ResourceLoader? s_resourceLoader;
    private static bool s_resourceLoaderInitializationFailed;
#endif

    public static string CurrentLanguage { get; private set; } = string.Empty;

    /// <summary>
    /// Applies a language preference override to the application.
    /// If <paramref name="language"/> is null, empty, or whitespace, the language preference
    /// represents the Windows system/user default.
    /// In an unpackaged process, assigning an empty string to the Windows App SDK
    /// PrimaryLanguageOverride throws 0x80004005 and leaves any prior override intact;
    /// therefore, for System default the process-local SDK override is left unset on startup,
    /// allowing the runtime to naturally resolve system default resources.
    /// For packaged applications, the persisted override is explicitly cleared via the
    /// supported packaged WinRT PrimaryLanguageOverride API.
    /// </summary>
    public static void ApplyLanguagePreference(string? language)
    {
        var overrideValue = string.IsNullOrWhiteSpace(language) ? string.Empty : language.Trim();

#if WINDOWS
        try
        {
            if (string.IsNullOrEmpty(overrideValue))
            {
                try
                {
                    Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = string.Empty;
                }
                catch
                {
                    // Ignore if packaged API is unavailable in unpackaged execution
                }
            }
            else
            {
                Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = overrideValue;
                try
                {
                    Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = overrideValue;
                }
                catch
                {
                    // Ignore if packaged API is unavailable in unpackaged execution
                }
            }
        }
        catch
        {
            // Ignore if Windows App SDK globalization is unavailable in the current execution environment
        }

        ResetResourceLoader();
#endif

        CurrentLanguage = overrideValue;
    }

    /// <summary>
    /// Gets the currently applied Windows primary language override.
    /// Returns empty string if system default is active.
    /// </summary>
    public static string GetAppliedLanguageOverride()
    {
#if WINDOWS
        try
        {
            var sdkOverride = Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride;
            if (!string.IsNullOrEmpty(sdkOverride))
            {
                return sdkOverride;
            }
        }
        catch
        {
        }

        try
        {
            var packageOverride = Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride;
            if (!string.IsNullOrEmpty(packageOverride))
            {
                return packageOverride;
            }
        }
        catch
        {
        }

        return CurrentLanguage;
#else
        return CurrentLanguage;
#endif
    }

#if WINDOWS
    /// <summary>
    /// Resets any cached ResourceLoader instance so subsequent lookups recreate it
    /// with the active ResourceContext and language settings.
    /// </summary>
    public static void ResetResourceLoader()
    {
        lock (s_loaderLock)
        {
            s_resourceLoader = null;
            s_resourceLoaderInitializationFailed = false;
        }
    }

#endif

    /// <summary>
    /// Normalizes a resource key for Windows MRT ResourceLoader.
    /// Segmented WinUI x:Uid property keys (such as "InspectorTabDetails.Text")
    /// are indexed in MRT PRI resources using slashes (e.g. "InspectorTabDetails/Text").
    /// </summary>
    public static string NormalizeKeyForMrt(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        var dotIndex = key.IndexOf('.');
        if (dotIndex < 0)
        {
            return key;
        }

        return key.Replace('.', '/');
    }

#if WINDOWS
    private static Microsoft.Windows.ApplicationModel.Resources.ResourceLoader? GetResourceLoader()
    {
        lock (s_loaderLock)
        {
            if (s_resourceLoaderInitializationFailed)
            {
                return null;
            }

            if (s_resourceLoader != null)
            {
                return s_resourceLoader;
            }

            try
            {
                s_resourceLoader = CreateResourceLoader();
                return s_resourceLoader;
            }
            catch
            {
                s_resourceLoaderInitializationFailed = true;
                return null;
            }
        }
    }

    private static Microsoft.Windows.ApplicationModel.Resources.ResourceLoader CreateResourceLoader()
    {
        try
        {
            return new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader();
        }
        catch
        {
            var baseDir = AppContext.BaseDirectory;
            var candidates = new[] { "resources.pri", "HdrImageViewer.pri" };
            foreach (var candidate in candidates)
            {
                var priPath = System.IO.Path.Combine(baseDir, candidate);
                if (System.IO.File.Exists(priPath))
                {
                    try
                    {
                        return new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader(priPath);
                    }
                    catch
                    {
                        // Try next candidate
                    }
                }
            }

            throw;
        }
    }
#endif

    public static string GetString(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

#if WINDOWS
        var loader = GetResourceLoader();
        if (loader != null)
        {
            var mrtKey = NormalizeKeyForMrt(key);
            try
            {
                var resourceString = loader.GetString(mrtKey);
                if (!string.IsNullOrEmpty(resourceString))
                {
                    return resourceString;
                }
            }
            catch
            {
                // Individual lookup failed for normalized key (e.g. missing resource).
                // Do NOT mark the ResourceLoader as failed; continue to fallback.
            }

            if (!string.Equals(mrtKey, key, StringComparison.Ordinal))
            {
                try
                {
                    var directString = loader.GetString(key);
                    if (!string.IsNullOrEmpty(directString))
                    {
                        return directString;
                    }
                }
                catch
                {
                    // Individual lookup failed for direct key as well.
                }
            }
        }
#endif

        return FallbackResources.GetString(key);
    }

    public static string GetString(string key, params object[] args)
    {
        var format = GetString(key);
        if (string.IsNullOrEmpty(format))
        {
            return string.Empty;
        }

        if (args is null || args.Length == 0)
        {
            return format;
        }

        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, args);
        }
        catch (FormatException)
        {
            return format;
        }
    }
}
