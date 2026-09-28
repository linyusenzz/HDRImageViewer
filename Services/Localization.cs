// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;

namespace HdrImageViewer.Services;

public static class Localization
{
#if WINDOWS
    private static Microsoft.Windows.ApplicationModel.Resources.ResourceLoader? s_resourceLoader;
    private static bool s_resourceLoaderFailed;
#endif

    public static string GetString(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

#if WINDOWS
        if (!s_resourceLoaderFailed)
        {
            try
            {
                s_resourceLoader ??= new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader();
                var resourceString = s_resourceLoader.GetString(key);
                if (!string.IsNullOrEmpty(resourceString))
                {
                    return resourceString;
                }
            }
            catch
            {
                s_resourceLoaderFailed = true;
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
