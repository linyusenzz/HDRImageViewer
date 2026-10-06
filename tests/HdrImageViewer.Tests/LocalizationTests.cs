using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HdrImageViewer.Services;
using Xunit;

namespace HdrImageViewer.Tests;

public sealed class LocalizationTests
{
    private static readonly string[] ExpectedLocales =
    [
        "zh-CN",
        "en-US",
        "ru-RU",
        "de-DE",
        "fr-FR",
        "es-ES",
        "it-IT",
        "pt-BR",
        "ja-JP",
        "ko-KR",
        "pl-PL",
        "uk-UA"
    ];

    private static string GetRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "HdrImageViewer.csproj")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root containing HdrImageViewer.csproj");
    }

    private static Dictionary<string, string> LoadResw(string filePath)
    {
        Assert.True(File.Exists(filePath), $"Resource file not found: {filePath}");
        var doc = XDocument.Load(filePath);
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        var dataElements = doc.Descendants("data");

        foreach (var data in dataElements)
        {
            var nameAttr = data.Attribute("name");
            Assert.NotNull(nameAttr);
            var key = nameAttr.Value;
            var valueElement = data.Element("value");
            Assert.NotNull(valueElement);
            var value = valueElement.Value;
            dict[key] = value;
        }

        return dict;
    }

    [Fact]
    public void AllTwelveLocalesExistAndHaveExactSameKeysAndNonEmptyValues()
    {
        var root = GetRepositoryRoot();
        var stringsDir = Path.Combine(root, "Strings");
        Assert.True(Directory.Exists(stringsDir), $"Strings directory not found at {stringsDir}");

        var localeMaps = new Dictionary<string, Dictionary<string, string>>();
        foreach (var locale in ExpectedLocales)
        {
            var reswPath = Path.Combine(stringsDir, locale, "Resources.resw");
            var map = LoadResw(reswPath);
            localeMaps[locale] = map;

            // Verify no values are empty or whitespace
            foreach (var kvp in map)
            {
                Assert.False(string.IsNullOrWhiteSpace(kvp.Value),
                    $"Locale '{locale}' has empty or whitespace value for key '{kvp.Key}'");
            }
        }

        var baselineLocale = "zh-CN";
        var baselineKeys = localeMaps[baselineLocale].Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(baselineKeys.Count > 300, $"Expected baseline to have > 300 keys, found {baselineKeys.Count}");

        foreach (var locale in ExpectedLocales)
        {
            if (locale == baselineLocale) continue;

            var currentKeys = localeMaps[locale].Keys.ToHashSet(StringComparer.Ordinal);
            var missingKeys = baselineKeys.Where(k => !currentKeys.Contains(k)).ToList();
            var extraKeys = currentKeys.Where(k => !localeMaps[baselineLocale].ContainsKey(k)).ToList();

            Assert.True(missingKeys.Count == 0,
                $"Locale '{locale}' is missing {missingKeys.Count} keys from baseline: {string.Join(", ", missingKeys.Take(10))}");
            Assert.True(extraKeys.Count == 0,
                $"Locale '{locale}' has {extraKeys.Count} extra keys not in baseline: {string.Join(", ", extraKeys.Take(10))}");
        }
    }

    [Fact]
    public void FormatPlaceholdersMatchBetweenTranslations()
    {
        var root = GetRepositoryRoot();
        var placeholderRegex = new Regex(@"\{(\d+)\}", RegexOptions.Compiled);

        var baselinePath = Path.Combine(root, "Strings", "zh-CN", "Resources.resw");
        var baselineMap = LoadResw(baselinePath);

        foreach (var locale in ExpectedLocales)
        {
            if (locale == "zh-CN") continue;
            var reswPath = Path.Combine(root, "Strings", locale, "Resources.resw");
            var currentMap = LoadResw(reswPath);

            foreach (var (key, baselineVal) in baselineMap)
            {
                var baselinePlaceholders = placeholderRegex.Matches(baselineVal)
                    .Select(m => m.Groups[1].Value)
                    .OrderBy(x => x)
                    .ToList();

                if (baselinePlaceholders.Count == 0) continue;

                Assert.True(currentMap.TryGetValue(key, out var currentVal),
                    $"Locale '{locale}' missing key '{key}'");

                var currentPlaceholders = placeholderRegex.Matches(currentVal!)
                    .Select(m => m.Groups[1].Value)
                    .OrderBy(x => x)
                    .ToList();

                Assert.Equal(baselinePlaceholders, currentPlaceholders);
            }
        }
    }

    private static IEnumerable<string> EnumerateSourceFiles(string directory, string pattern)
    {
        foreach (var file in Directory.EnumerateFiles(directory, pattern)) yield return file;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith('.') || new[] { "bin", "obj", "artifacts", "AppPackages", "external", "native", "dist" }
                .Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            foreach (var file in EnumerateSourceFiles(child, pattern)) yield return file;
        }
    }

    [Fact]
    public void NoUnlocalizedChineseInXamlFiles()
    {
        var root = GetRepositoryRoot();
        var xamlFiles = EnumerateSourceFiles(root, "*.xaml").ToList();

        Assert.NotEmpty(xamlFiles);

        var cjkRegex = new Regex(@"[\u4e00-\u9fff]", RegexOptions.Compiled);
        var violations = new List<string>();

        foreach (var file in xamlFiles)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                // Language selector endonyms in SettingsPage (e.g. 中文（简体）, 日本語) are intentionally native
                if (line.Contains("Tag=\"zh-CN\"") || line.Contains("Tag=\"ja-JP\""))
                {
                    continue;
                }

                if (cjkRegex.IsMatch(line))
                {
                    var relPath = Path.GetRelativePath(root, file);
                    violations.Add($"{relPath}:{i + 1}: {line.Trim()}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} untranslated Chinese text lines in XAML:\n{string.Join("\n", violations.Take(10))}");
    }

    [Fact]
    public void NoUnlocalizedChineseInUserInterfaceCode()
    {
        var root = GetRepositoryRoot();
        var uiFiles = new[]
        {
            Path.Combine(root, "MainWindow.xaml.cs"),
            Path.Combine(root, "Pages", "AboutPage.xaml.cs"),
            Path.Combine(root, "Pages", "SettingsPage.xaml.cs"),
            Path.Combine(root, "Pages", "BatchExportWindow.xaml.cs"),
            Path.Combine(root, "Pages", "HomePage.FileActions.cs"),
            Path.Combine(root, "Pages", "HomePage.Export.cs"),
            Path.Combine(root, "Pages", "HomePage.Chromaticity.cs"),
            Path.Combine(root, "Pages", "HomePage.Crop.cs"),
            Path.Combine(root, "Pages", "HomePage.CompanionMedia.cs"),
            Path.Combine(root, "Pages", "HomePage.ViewerTools.cs"),
            Path.Combine(root, "Pages", "HomePage.Viewport.cs"),
            Path.Combine(root, "ViewModels", "ImageWorkspaceViewModel.cs"),
            Path.Combine(root, "Rendering", "D3D11HdrRenderPipeline.ViewerTools.cs"),
            Path.Combine(root, "Services", "BatchExportQueue.cs")
        };

        var cjkRegex = new Regex(@"[\u4e00-\u9fff]", RegexOptions.Compiled);
        var violations = new List<string>();

        foreach (var file in uiFiles)
        {
            if (!File.Exists(file)) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (cjkRegex.IsMatch(line))
                {
                    var relPath = Path.GetRelativePath(root, file);
                    violations.Add($"{relPath}:{i + 1}: {line.Trim()}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Found untranslated Chinese literals in UI code:\n{string.Join("\n", violations)}");
    }

    [Fact]
    public void NoUnexpectedChineseLiteralsInCSharpSource()
    {
        var root = GetRepositoryRoot();
        var csFiles = EnumerateSourceFiles(root, "*.cs").ToList();

        Assert.NotEmpty(csFiles);

        var cjkRegex = new Regex(@"[\u4e00-\u9fff]", RegexOptions.Compiled);
        var violations = new List<string>();

        // Whitelisted files that are allowed to have CJK tokens:
        // - FallbackResources.cs contains compiled fallbacks for zh-CN
        // - LocalizationTests.cs contains this test and comments
        // - LivePhotoProbeTests.cs contains fixture test tokens
        // - DecoderCatalogTests.cs contains fixture test tokens
        // - HomePage.xaml.cs contains internal diagnostic/decoder probe tokens
        // - Internal decoder/format probe descriptors, CLI adapters and exceptions
        var whitelistedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Services\\FallbackResources.cs",
            "tests\\HdrImageViewer.Tests\\LocalizationTests.cs",
            "tests\\HdrImageViewer.Tests\\LivePhotoProbeTests.cs",
            "tests\\HdrImageViewer.Tests\\DecoderCatalogTests.cs",
            "tests\\HdrImageViewer.WindowsTests\\MrtResourceLoadingTests.cs",
            "tests\\HdrImageViewer.IntegrationTests\\MrtLocalizationRegressionChecks.cs",
            "Pages\\HomePage.xaml.cs",
            "Services\\DecoderCatalog.cs",
            "Services\\GainMapHdrExportService.cs",
            "Services\\SingleLayerHdrExportService.cs",
            "Services\\AvifGainMapDecoder.cs",
            "Services\\BitmapDecodeService.cs",
            "Services\\HdrExportBackendCatalog.cs",
            "Services\\JxlGainMapDecoder.cs",
            "Services\\JxlNativeDecoder.cs",
            "Services\\JxlProbe.cs",
            "Services\\ExrProbe.cs",
            "Services\\LivePhotoProbe.cs",
            "Services\\NativeExrDecoder.cs",
            "Services\\NativeProcessRunner.cs",
            "Services\\PortableImageReader.cs",
            "Models\\ExrProbeResult.cs",
            "Models\\GainMapMetadata.cs",
            "Models\\GainMapProbeResult.cs",
            "Models\\HeifAvifProbeResult.cs",
            "Models\\WicImageProbeResult.cs",
            "Models\\JxlProbeResult.cs"
        };

        foreach (var file in csFiles)
        {
            var relPath = Path.GetRelativePath(root, file);
            if (whitelistedFiles.Contains(relPath))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (cjkRegex.IsMatch(line))
                {
                    violations.Add($"{relPath}:{i + 1}: {line.Trim()}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Found unexpected Chinese literals in C# code:\n{string.Join("\n", violations.Take(10))}");
    }

    [Fact]
    public void LocalizationHelperResolvesKeysAndFormatsProperly()
    {
        // Tests Localization.GetString with FallbackResources in unit test environment
        var appTitle = Localization.GetString("AppTitle");
        Assert.False(string.IsNullOrWhiteSpace(appTitle));

        var formattedVersion = Localization.GetString("VersionFormat", "2.1.0");
        Assert.Contains("2.1.0", formattedVersion);

        var aboutVersion = Localization.GetString("AboutVersionFormat", "2.1.0");
        Assert.Contains("2.1.0", aboutVersion);

        // Fallback for non-existent key returns key name
        var nonExistent = Localization.GetString("NonExistentTestKey123");
        Assert.Equal("NonExistentTestKey123", nonExistent);
    }

    [Fact]
    public void NormalizeKeyForMrtTransformsSegmentedKeysProperly()
    {
        Assert.Equal("InspectorTabDetails/Text", Localization.NormalizeKeyForMrt("InspectorTabDetails.Text"));
        Assert.Equal("InspectorTabAnalysis/Text", Localization.NormalizeKeyForMrt("InspectorTabAnalysis.Text"));
        Assert.Equal("AboutAppName/Text", Localization.NormalizeKeyForMrt("AboutAppName.Text"));
        Assert.Equal("AppTitle", Localization.NormalizeKeyForMrt("AppTitle"));
        Assert.Equal(string.Empty, Localization.NormalizeKeyForMrt(string.Empty));
        Assert.Equal("SettingsOpenDefaultAppsButton/AutomationProperties/Name",
            Localization.NormalizeKeyForMrt("SettingsOpenDefaultAppsButton.AutomationProperties.Name"));
    }

    [Fact]
    public void SystemDefaultClearsLanguageOverrideStateTransition()
    {
        // Initial state: System default (empty)
        Localization.ApplyLanguagePreference(string.Empty);
        Assert.Equal(string.Empty, Localization.GetAppliedLanguageOverride());

        // Step 1: User selects an explicit language (e.g. Russian)
        Localization.ApplyLanguagePreference("ru-RU");
        Assert.Equal("ru-RU", Localization.GetAppliedLanguageOverride());

        // Step 2: Restart simulation with explicit language persisted
        Localization.ApplyLanguagePreference("ru-RU");
        Assert.Equal("ru-RU", Localization.GetAppliedLanguageOverride());

        // Step 3: User selects System default (empty string)
        Localization.ApplyLanguagePreference(string.Empty);
        Assert.Equal(string.Empty, Localization.GetAppliedLanguageOverride());

        // Step 4: Restart simulation with empty string from persisted settings
        Localization.ApplyLanguagePreference(null);
        var finalOverride = Localization.GetAppliedLanguageOverride();
        Assert.Equal(string.Empty, finalOverride);
        Assert.NotEqual("ru-RU", finalOverride);
    }
}
