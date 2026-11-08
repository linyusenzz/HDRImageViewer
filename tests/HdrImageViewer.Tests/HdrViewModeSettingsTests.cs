using HdrImageViewer.Rendering;
using HdrImageViewer.Services;
using Xunit;

namespace HdrImageViewer.Tests;

public sealed class HdrViewModeSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HdrImageViewerTests", Path.GetRandomFileName());
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public HdrViewModeSettingsTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"HdrViewMode\":999}")]
    [InlineData("{\"HdrViewMode\":-1}")]
    [InlineData("{\"HdrViewMode\":\"invalid\"}")]
    public void OldOrInvalidSettings_DefaultToAdaptive(string json)
    {
        File.WriteAllText(SettingsPath, json);

        Assert.Equal(GainmapViewMode.Adaptive, AppSettingsService.LoadSettings(SettingsPath).HdrViewMode);
    }

    [Fact]
    public void FirstLaunch_DefaultsToAdaptive()
    {
        var settings = AppSettingsService.LoadSettings(SettingsPath);

        Assert.Equal(GainmapViewMode.Adaptive, settings.HdrViewMode);
        Assert.Equal(GainmapViewMode.Adaptive, settings.GetEffectiveHdrViewMode(true, false));
    }

    [Theory]
    [InlineData(GainmapViewMode.Sdr)]
    [InlineData(GainmapViewMode.Adaptive)]
    [InlineData(GainmapViewMode.AlternateImage)]
    [InlineData(GainmapViewMode.GainMap)]
    public void Preference_SurvivesSdrFallbackSaveReloadAndCopy(GainmapViewMode preferred)
    {
        var settings = new AppUserSettings { HdrViewMode = preferred };
        Assert.Equal(GainmapViewMode.Sdr, settings.GetEffectiveHdrViewMode(false, false));
        Assert.Equal(preferred, settings.HdrViewMode);
        AppSettingsService.SaveSettings(settings, SettingsPath);

        var restored = AppSettingsService.Copy(AppSettingsService.LoadSettings(SettingsPath));

        Assert.Equal(preferred, restored.HdrViewMode);
        Assert.Equal(preferred, restored.GetEffectiveHdrViewMode(true, false));
        Assert.Equal(preferred == GainmapViewMode.GainMap ? GainmapViewMode.Adaptive : preferred,
            restored.GetEffectiveHdrViewMode(false, true));
    }

    [Fact]
    public void GainMapPreference_SurvivesUnsupportedHdrAndSdrUntilGainMapReturns()
    {
        var settings = new AppUserSettings { HdrViewMode = GainmapViewMode.GainMap };

        Assert.Equal(GainmapViewMode.GainMap, settings.GetEffectiveHdrViewMode(true, false));
        Assert.Equal(GainmapViewMode.Adaptive, settings.GetEffectiveHdrViewMode(false, true));
        Assert.Equal(GainmapViewMode.Sdr, settings.GetEffectiveHdrViewMode(false, false));
        AppSettingsService.SaveSettings(settings, SettingsPath);
        var restored = AppSettingsService.LoadSettings(SettingsPath);
        Assert.Equal(GainmapViewMode.GainMap, restored.GetEffectiveHdrViewMode(true, false));
    }

    [Fact]
    public void ExplicitSelection_ReplacesPreferenceAfterFallback()
    {
        var settings = new AppUserSettings { HdrViewMode = GainmapViewMode.GainMap };
        Assert.Equal(GainmapViewMode.Adaptive, settings.GetEffectiveHdrViewMode(false, true));

        settings.HdrViewMode = GainmapViewMode.AlternateImage;
        AppSettingsService.SaveSettings(settings, SettingsPath);
        var restored = AppSettingsService.LoadSettings(SettingsPath);

        Assert.Equal(GainmapViewMode.AlternateImage, restored.GetEffectiveHdrViewMode(true, false));
        Assert.Equal(GainmapViewMode.AlternateImage, restored.GetEffectiveHdrViewMode(false, true));
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }
}
