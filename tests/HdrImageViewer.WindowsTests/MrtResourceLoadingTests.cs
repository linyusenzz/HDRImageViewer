using System;
using System.IO;
using System.Reflection;
using HdrImageViewer.Services;
using Xunit;

namespace HdrImageViewer.WindowsTests;

public sealed class MrtResourceLoadingTests
{
    static MrtResourceLoadingTests()
    {
        TryInitializeBootstrap();
    }

    private static void TryInitializeBootstrap()
    {
        try
        {
            // Windows App SDK 2.x Major.Minor version: 0x00020002
            Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap.TryInitialize(0x00020002, "", out _);
        }
        catch
        {
            try
            {
                // Fallback attempt for 1.6/any installed version
                Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap.TryInitialize(0x00010006, "", out _);
            }
            catch
            {
                // Ignore if running inside package or already bootstrapped
            }
        }
    }

    private static string GetPriPath()
    {
        var baseDir = AppContext.BaseDirectory;
        return File.Exists(Path.Combine(baseDir, "resources.pri"))
            ? Path.Combine(baseDir, "resources.pri")
            : Path.Combine(baseDir, "HdrImageViewer.pri");
    }

    [Fact]
    public void PriResourceFileExistsInTestOutputDirectory()
    {
        var priPath = GetPriPath();
        Assert.True(File.Exists(priPath), $"PRI file not found in test output directory: {priPath}");
        var fileInfo = new FileInfo(priPath);
        Assert.True(fileInfo.Length > 100_000, $"PRI file seems too small ({fileInfo.Length} bytes), resources may be incomplete");
    }

    [Fact]
    public void SegmentedKeyResolvesThroughWindowsResourceLoaderAgainstPri()
    {
        // Must exercise Microsoft.Windows.ApplicationModel.Resources.ResourceLoader directly
        // against the actual generated Windows PRI resources.
        var priPath = GetPriPath();
        var loader = new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader(priPath);

        // Normalize segmented key: "InspectorTabDetails.Text" -> "InspectorTabDetails/Text"
        var normalizedKey = Localization.NormalizeKeyForMrt("InspectorTabDetails.Text");
        Assert.Equal("InspectorTabDetails/Text", normalizedKey);

        var value = loader.GetString(normalizedKey);
        Assert.False(string.IsNullOrWhiteSpace(value), $"ResourceLoader failed to resolve {normalizedKey} from {priPath}");
    }

    [Fact]
    public void MissingResourceLookupDoesNotPoisonSubsequentLookups()
    {
        // 1. Resolve a known valid localized resource
        var firstValid = Localization.GetString("InspectorTabDetails.Text");
        Assert.False(string.IsNullOrWhiteSpace(firstValid));

        // 2. Attempt a missing / invalid resource
        var missingResult = Localization.GetString("NonExistentComponent.InvalidProperty999");
        Assert.Equal("NonExistentComponent.InvalidProperty999", missingResult);

        // 3. Resolve another known valid localized resource
        var secondValid = Localization.GetString("InspectorTabAnalysis.Text");
        Assert.False(string.IsNullOrWhiteSpace(secondValid));

        // 4. Confirm third call resolves successfully
        Assert.NotEqual("InspectorTabAnalysis.Text", secondValid);
    }

    [Fact]
    public void SystemDefaultClearsPrimaryLanguageOverrideTransition()
    {
        // Test the restart boundary across separate fresh processes:
        // 1. Capture baseline in fresh process with no override (System default)
        var (baselineText, baselineOverride) = RunProbe("InspectorTabDetails.Text", languageOverride: string.Empty);
        Assert.Equal(string.Empty, baselineOverride);
        Assert.False(string.IsNullOrWhiteSpace(baselineText));

        // 2. Select an explicit language that differs from the system baseline
        var explicitLang = string.Equals(baselineText, "Details", StringComparison.Ordinal) ? "ru-RU" : "en-US";
        var expectedExplicitText = string.Equals(explicitLang, "ru-RU", StringComparison.Ordinal) ? "Сведения" : "Details";

        var (explicitText, explicitOverride) = RunProbe("InspectorTabDetails.Text", languageOverride: explicitLang);
        Assert.Equal(explicitLang, explicitOverride);
        Assert.Equal(expectedExplicitText, explicitText);
        Assert.NotEqual(baselineText, explicitText);

        // Also test another explicit language (Chinese)
        var (zhText, zhOverride) = RunProbe("InspectorTabDetails.Text", languageOverride: "zh-CN");
        Assert.Equal("zh-CN", zhOverride);
        Assert.Equal("详情", zhText);
        if (!string.Equals(baselineText, "详情", StringComparison.Ordinal))
        {
            Assert.NotEqual(baselineText, zhText);
        }

        // 3. Return to System default (empty) in a fresh process simulating restart
        var (restoredText, restoredOverride) = RunProbe("InspectorTabDetails.Text", languageOverride: string.Empty);
        Assert.Equal(string.Empty, restoredOverride);
        Assert.Equal(baselineText, restoredText);
        Assert.NotEqual(explicitText, restoredText);
    }

    [Fact]
    public void PriResourcesContainSegmentedKeysAcrossMultipleLocales()
    {
        var priPath = GetPriPath();
        var manager = new Microsoft.Windows.ApplicationModel.Resources.ResourceManager(priPath);
        var resourceMap = manager.MainResourceMap.GetSubtree("Resources");
        var normalizedKey = Localization.NormalizeKeyForMrt("InspectorTabDetails.Text");
        Assert.Equal("InspectorTabDetails/Text", normalizedKey);

        // Test Russian candidate
        var ruContext = manager.CreateResourceContext();
        ruContext.QualifierValues["Language"] = "ru-RU";
        var ruCandidate = resourceMap.GetValue(normalizedKey, ruContext);
        Assert.NotNull(ruCandidate);
        Assert.Equal("Сведения", ruCandidate.ValueAsString);

        // Test Chinese candidate
        var zhContext = manager.CreateResourceContext();
        zhContext.QualifierValues["Language"] = "zh-CN";
        var zhCandidate = resourceMap.GetValue(normalizedKey, zhContext);
        Assert.NotNull(zhCandidate);
        Assert.Equal("详情", zhCandidate.ValueAsString);

        // Test English candidate
        var enContext = manager.CreateResourceContext();
        enContext.QualifierValues["Language"] = "en-US";
        var enCandidate = resourceMap.GetValue(normalizedKey, enContext);
        Assert.NotNull(enCandidate);
        Assert.Equal("Details", enCandidate.ValueAsString);

        // Test German candidate
        var deContext = manager.CreateResourceContext();
        deContext.QualifierValues["Language"] = "de-DE";
        var deCandidate = resourceMap.GetValue(normalizedKey, deContext);
        Assert.NotNull(deCandidate);
        Assert.Equal("Details", deCandidate.ValueAsString);

        // Test French candidate
        var frContext = manager.CreateResourceContext();
        frContext.QualifierValues["Language"] = "fr-FR";
        var frCandidate = resourceMap.GetValue(normalizedKey, frContext);
        Assert.NotNull(frCandidate);
        Assert.Equal("Détails", frCandidate.ValueAsString);
    }

    [Fact]
    public void NewlyAddedGainMapFormatKeysResolveThroughPri()
    {
        var priPath = GetPriPath();
        var manager = new Microsoft.Windows.ApplicationModel.Resources.ResourceManager(priPath);
        var resourceMap = manager.MainResourceMap.GetSubtree("Resources");

        // Test HEIC Gain Map key
        var enContext = manager.CreateResourceContext();
        enContext.QualifierValues["Language"] = "en-US";
        var enHeic = resourceMap.GetValue("BatchFormatGainMapHeic", enContext);
        Assert.NotNull(enHeic);
        Assert.Contains("HEIC", enHeic.ValueAsString);
        Assert.Contains("Gain Map", enHeic.ValueAsString);

        var ruContext = manager.CreateResourceContext();
        ruContext.QualifierValues["Language"] = "ru-RU";
        var ruHeic = resourceMap.GetValue("BatchFormatGainMapHeic", ruContext);
        Assert.NotNull(ruHeic);
        Assert.Contains("HEIC", ruHeic.ValueAsString);
        Assert.Contains("Gain Map", ruHeic.ValueAsString);

        // Test AVIF Gain Map key
        var enAvif = resourceMap.GetValue("BatchFormatGainMapAvif", enContext);
        Assert.NotNull(enAvif);
        Assert.Contains("AVIF", enAvif.ValueAsString);
        Assert.Contains("Gain Map", enAvif.ValueAsString);

        var zhContext = manager.CreateResourceContext();
        zhContext.QualifierValues["Language"] = "zh-CN";
        var zhAvif = resourceMap.GetValue("BatchFormatGainMapAvif", zhContext);
        Assert.NotNull(zhAvif);
        Assert.Contains("AVIF", zhAvif.ValueAsString);
        Assert.Contains("Gain Map", zhAvif.ValueAsString);

        // Test Crop flyout P3 gamut key
        var enCropP3 = resourceMap.GetValue("CropUltraHdrBaseGamutP3", enContext);
        Assert.NotNull(enCropP3);
        Assert.Equal("Base P3 · Auto", enCropP3.ValueAsString);

        var zhCropP3 = resourceMap.GetValue("CropUltraHdrBaseGamutP3", zhContext);
        Assert.NotNull(zhCropP3);
        Assert.Equal("Base P3 · 自动", zhCropP3.ValueAsString);

        // Test another segmented key normalized
        var aboutAppKey = Localization.NormalizeKeyForMrt("AboutAppName.Text");
        Assert.Equal("AboutAppName/Text", aboutAppKey);
        var zhAbout = resourceMap.GetValue(aboutAppKey, zhContext);
        Assert.NotNull(zhAbout);
        Assert.Equal("HDR 图片查看器", zhAbout.ValueAsString);
    }

    [Fact]
    public void AppSettingsLanguagePersistenceAndLifecycleTransition()
    {
        var originalLanguage = AppSettingsService.Current.Language;
        try
        {
            // 1. Initial baseline with System default in Settings
            AppSettingsService.SetLanguage(string.Empty);
            var (baselineText, baselineOverride) = RunProbe("InspectorTabDetails.Text");
            Assert.Equal(string.Empty, baselineOverride);
            Assert.False(string.IsNullOrWhiteSpace(baselineText));

            // 2. User selects explicit language (persisted without live mutation)
            var explicitLang = string.Equals(baselineText, "Details", StringComparison.Ordinal) ? "ru-RU" : "en-US";
            var expectedExplicitText = string.Equals(explicitLang, "ru-RU", StringComparison.Ordinal) ? "Сведения" : "Details";

            AppSettingsService.SetLanguage(explicitLang);
            Assert.Equal(explicitLang, AppSettingsService.Current.Language);

            // Verify translated text after restart in a fresh process
            var (explicitText, explicitOverride) = RunProbe("InspectorTabDetails.Text");
            Assert.Equal(explicitLang, explicitOverride);
            Assert.Equal(expectedExplicitText, explicitText);
            Assert.NotEqual(baselineText, explicitText);

            // 3. User selects System default (persisted without live mutation)
            AppSettingsService.SetLanguage(string.Empty);
            Assert.Equal(string.Empty, AppSettingsService.Current.Language);

            // Verify restoration to baseline text after restart in a fresh process
            var (restoredText, restoredOverride) = RunProbe("InspectorTabDetails.Text");
            Assert.Equal(string.Empty, restoredOverride);
            Assert.Equal(baselineText, restoredText);
            Assert.NotEqual(explicitText, restoredText);
        }
        finally
        {
            AppSettingsService.SetLanguage(originalLanguage);
        }
    }

#if DEBUG
    private const string CurrentConfiguration = "Debug";
    private const string AlternateConfiguration = "Release";
#else
    private const string CurrentConfiguration = "Release";
    private const string AlternateConfiguration = "Debug";
#endif

    private static string? GetAssemblyMetadata(string key)
    {
        return typeof(MrtResourceLoadingTests).Assembly
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;
    }

    private static bool IsValidProbe(string? exePath)
    {
        return !string.IsNullOrEmpty(exePath) &&
               File.Exists(exePath) &&
               File.Exists(Path.ChangeExtension(exePath, ".dll"));
    }

    private static string FindIntegrationTestsExecutable()
    {
        // 1. Check local Probe or IntegrationTests subfolder (copied by MSBuild target)
        var localProbe = Path.Combine(AppContext.BaseDirectory, "Probe", "HdrImageViewer.IntegrationTests.exe");
        if (IsValidProbe(localProbe))
        {
            return localProbe;
        }

        var localSubdir = Path.Combine(AppContext.BaseDirectory, "IntegrationTests", "HdrImageViewer.IntegrationTests.exe");
        if (IsValidProbe(localSubdir))
        {
            return localSubdir;
        }

        var localBase = Path.Combine(AppContext.BaseDirectory, "HdrImageViewer.IntegrationTests.exe");
        if (IsValidProbe(localBase))
        {
            return localBase;
        }

        // 2. Check path passed via MSBuild assembly metadata
        var metadataDir = GetAssemblyMetadata("IntegrationTestsProbeDir");
        if (!string.IsNullOrEmpty(metadataDir))
        {
            var metadataExe = Path.Combine(metadataDir, "HdrImageViewer.IntegrationTests.exe");
            if (IsValidProbe(metadataExe))
            {
                return metadataExe;
            }
        }

        var metadataNoPlatformDir = GetAssemblyMetadata("IntegrationTestsProbeDirNoPlatform");
        if (!string.IsNullOrEmpty(metadataNoPlatformDir))
        {
            var metadataNoPlatformExe = Path.Combine(metadataNoPlatformDir, "HdrImageViewer.IntegrationTests.exe");
            if (IsValidProbe(metadataNoPlatformExe))
            {
                return metadataNoPlatformExe;
            }
        }

        // 3. Search directory tree prioritizing the active build configuration (Release/Debug) and x64 layout
        var configurations = new[] { CurrentConfiguration, AlternateConfiguration };
        var platforms = new[] { "x64", "" };
        const string tfmRid = "net10.0-windows10.0.26100.0\\win-x64";

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            foreach (var config in configurations)
            {
                foreach (var platform in platforms)
                {
                    var relativeParts = string.IsNullOrEmpty(platform)
                        ? new[] { "bin", config, tfmRid, "HdrImageViewer.IntegrationTests.exe" }
                        : new[] { "bin", platform, config, tfmRid, "HdrImageViewer.IntegrationTests.exe" };

                    // Under tests/HdrImageViewer.IntegrationTests/
                    var candidate = Path.Combine([dir.FullName, "tests", "HdrImageViewer.IntegrationTests", .. relativeParts]);
                    if (IsValidProbe(candidate))
                    {
                        return candidate;
                    }

                    // Sibling HdrImageViewer.IntegrationTests/
                    var sibling = Path.Combine([dir.FullName, "HdrImageViewer.IntegrationTests", .. relativeParts]);
                    if (IsValidProbe(sibling))
                    {
                        return sibling;
                    }
                }
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("HdrImageViewer.IntegrationTests.exe with matching dll was not found.");
    }

    private static (string text, string appliedOverride) RunProbe(string key, string? languageOverride = null)
    {
        var exe = FindIntegrationTestsExecutable();
        var arguments = languageOverride is null
            ? $"--probe-resource \"{key}\" --use-settings"
            : $"--probe-resource \"{key}\" \"{languageOverride}\"";

        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exe,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(15000);

        Assert.True(process.ExitCode == 0, $"Probe process failed (code {process.ExitCode}): {stderr}\n{stdout}");

        string text = string.Empty;
        string appliedOverride = string.Empty;
        foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("TEXT="))
            {
                text = line["TEXT=".Length..];
            }
            else if (line.StartsWith("OVERRIDE="))
            {
                appliedOverride = line["OVERRIDE=".Length..];
            }
        }

        return (text, appliedOverride);
    }
}


