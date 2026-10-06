using System;
using System.IO;
using System.Threading.Tasks;
using HdrImageViewer.Services;

namespace HdrImageViewer.IntegrationTests;

internal static class MrtLocalizationRegressionChecks
{
    public static Task RunAsync()
    {
        Console.WriteLine("=== Windows MRT / PRI Resource Loading Regression Checks ===");

        var baseDir = AppContext.BaseDirectory;
        var priPath = File.Exists(Path.Combine(baseDir, "resources.pri"))
            ? Path.Combine(baseDir, "resources.pri")
            : Path.Combine(baseDir, "HdrImageViewer.pri");

        if (!File.Exists(priPath))
        {
            throw new FileNotFoundException($"Windows PRI file not found in {baseDir}");
        }

        Console.WriteLine($"[1] Located PRI file: {priPath} ({new FileInfo(priPath).Length:N0} bytes)");

        // 1. Direct ResourceLoader with normalized segmented key
        var loader = new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader(priPath);
        var normalizedKey = Localization.NormalizeKeyForMrt("InspectorTabDetails.Text");
        if (normalizedKey != "InspectorTabDetails/Text")
        {
            throw new InvalidOperationException($"Expected InspectorTabDetails/Text but got {normalizedKey}");
        }

        var segmentedValue = loader.GetString(normalizedKey);
        if (string.IsNullOrWhiteSpace(segmentedValue))
        {
            throw new InvalidOperationException($"ResourceLoader returned empty string for {normalizedKey}");
        }
        Console.WriteLine($"[2] Segmented key '{normalizedKey}' resolved via ResourceLoader: '{segmentedValue}'");

        // 2. Non-poisoning check
        var firstValid = Localization.GetString("InspectorTabDetails.Text");
        var missing = Localization.GetString("NonExistentComponent.FakeKey123");
        var secondValid = Localization.GetString("InspectorTabAnalysis.Text");

        if (string.IsNullOrWhiteSpace(firstValid) || string.IsNullOrWhiteSpace(secondValid) || secondValid == "InspectorTabAnalysis.Text")
        {
            throw new InvalidOperationException("Non-poisoning check failed: valid resource did not resolve after missing key");
        }
        Console.WriteLine("[3] Non-poisoning verification passed: missing key did not disable ResourceLoader");

        // 3. Restart boundary and PrimaryLanguageOverride verification across separate fresh processes
        Console.WriteLine("[4] Testing restart boundary across separate processes...");
        var originalLanguage = AppSettingsService.Current.Language;
        try
        {
            // Step 3a: Capture no-override baseline in a fresh process
            AppSettingsService.SetLanguage(string.Empty);
            var (baselineText, baselineOverride) = RunProbe("InspectorTabDetails.Text");
            if (baselineOverride != string.Empty)
            {
                throw new InvalidOperationException($"Expected empty override for baseline, got '{baselineOverride}'");
            }
            if (string.IsNullOrWhiteSpace(baselineText))
            {
                throw new InvalidOperationException("Baseline text lookup returned empty string");
            }
            Console.WriteLine($"    Baseline (no-override): override='{baselineOverride}', text='{baselineText}'");

            // Step 3b: Select an explicit language differing from baseline and verify after restart
            var explicitLang = string.Equals(baselineText, "Details", StringComparison.Ordinal) ? "ru-RU" : "en-US";
            var expectedExplicitText = string.Equals(explicitLang, "ru-RU", StringComparison.Ordinal) ? "Сведения" : "Details";

            AppSettingsService.SetLanguage(explicitLang);
            var (explicitText, explicitOverride) = RunProbe("InspectorTabDetails.Text");
            if (explicitOverride != explicitLang)
            {
                throw new InvalidOperationException($"Expected override '{explicitLang}', got '{explicitOverride}'");
            }
            if (explicitText != expectedExplicitText)
            {
                throw new InvalidOperationException($"Expected text '{expectedExplicitText}' after restart, got '{explicitText}'");
            }
            if (explicitText == baselineText)
            {
                throw new InvalidOperationException($"Explicit text '{explicitText}' should differ from baseline '{baselineText}'");
            }
            Console.WriteLine($"    Explicit language '{explicitLang}' after restart: override='{explicitOverride}', text='{explicitText}'");

            // Step 3c: Select System default and verify baseline restoration after restart
            AppSettingsService.SetLanguage(string.Empty);
            var (restoredText, restoredOverride) = RunProbe("InspectorTabDetails.Text");
            if (restoredOverride != string.Empty)
            {
                throw new InvalidOperationException($"Expected empty override after restoring System default, got '{restoredOverride}'");
            }
            if (restoredText != baselineText)
            {
                throw new InvalidOperationException($"Restored text '{restoredText}' did not match baseline '{baselineText}'");
            }
            if (restoredText == explicitText)
            {
                throw new InvalidOperationException($"Restored text '{restoredText}' is still showing explicit language text '{explicitText}'");
            }
            Console.WriteLine($"    Restored baseline after restart: override='{restoredOverride}', text='{restoredText}'");
            Console.WriteLine("[4] Restart boundary state transition verified across separate processes");
        }
        finally
        {
            AppSettingsService.SetLanguage(originalLanguage);
        }

        // 4. Multi-language resolution from PRI
        var manager = new Microsoft.Windows.ApplicationModel.Resources.ResourceManager(priPath);
        var resourceMap = manager.MainResourceMap.GetSubtree("Resources");

        var ruContext = manager.CreateResourceContext();
        ruContext.QualifierValues["Language"] = "ru-RU";
        var ruVal = resourceMap.GetValue("InspectorTabDetails/Text", ruContext).ValueAsString;

        var zhContext = manager.CreateResourceContext();
        zhContext.QualifierValues["Language"] = "zh-CN";
        var zhVal = resourceMap.GetValue("InspectorTabDetails/Text", zhContext).ValueAsString;

        var enContext = manager.CreateResourceContext();
        enContext.QualifierValues["Language"] = "en-US";
        var enVal = resourceMap.GetValue("InspectorTabDetails/Text", enContext).ValueAsString;

        if (ruVal != "Сведения" || zhVal != "详情" || enVal != "Details")
        {
            throw new InvalidOperationException($"Multi-language PRI lookup mismatch: ru='{ruVal}', zh='{zhVal}', en='{enVal}'");
        }
        Console.WriteLine($"[5] Multi-language PRI lookup verified: ru='{ruVal}', zh='{zhVal}', en='{enVal}'");

        // 5. Gain Map format strings
        var heicVal = resourceMap.GetValue("BatchFormatGainMapHeic", enContext).ValueAsString;
        var avifVal = resourceMap.GetValue("BatchFormatGainMapAvif", enContext).ValueAsString;
        var cropP3Val = resourceMap.GetValue("CropUltraHdrBaseGamutP3", enContext).ValueAsString;

        if (!heicVal.Contains("HEIC") || !avifVal.Contains("AVIF") || cropP3Val != "Base P3 · Auto")
        {
            throw new InvalidOperationException($"Gain Map format keys mismatch in PRI: heic='{heicVal}', avif='{avifVal}', cropP3='{cropP3Val}'");
        }
        Console.WriteLine($"[6] Gain Map keys verified in PRI: heic='{heicVal}', avif='{avifVal}', cropP3='{cropP3Val}'");

        Console.WriteLine("=== All Windows MRT / PRI Resource Checks Passed Successfully ===");
        return Task.CompletedTask;
    }

#if DEBUG
    private const string CurrentConfiguration = "Debug";
    private const string AlternateConfiguration = "Release";
#else
    private const string CurrentConfiguration = "Release";
    private const string AlternateConfiguration = "Debug";
#endif

    private static bool IsValidProbe(string? exePath)
    {
        return !string.IsNullOrEmpty(exePath) &&
               File.Exists(exePath) &&
               File.Exists(Path.ChangeExtension(exePath, ".dll"));
    }

    private static string FindIntegrationTestsExecutable()
    {
        if (IsValidProbe(Environment.ProcessPath))
        {
            return Environment.ProcessPath!;
        }

        var localProbe = Path.Combine(AppContext.BaseDirectory, "Probe", "HdrImageViewer.IntegrationTests.exe");
        if (IsValidProbe(localProbe))
        {
            return localProbe;
        }

        var localBase = Path.Combine(AppContext.BaseDirectory, "HdrImageViewer.IntegrationTests.exe");
        if (IsValidProbe(localBase))
        {
            return localBase;
        }

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

                    var candidate = Path.Combine([dir.FullName, "tests", "HdrImageViewer.IntegrationTests", .. relativeParts]);
                    if (IsValidProbe(candidate))
                    {
                        return candidate;
                    }

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

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Probe process failed (code {process.ExitCode}): {stderr}\n{stdout}");
        }

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
