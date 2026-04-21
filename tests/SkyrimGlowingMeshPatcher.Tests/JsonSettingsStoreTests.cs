using SkyrimGlowingMeshPatcher.Core.Models;
using SkyrimGlowingMeshPatcher.Core.Services;

namespace SkyrimGlowingMeshPatcher.Tests;

public sealed class JsonSettingsStoreTests
{
    [Fact]
    public async Task LoadAsync_ReturnsDefault_WhenSettingsFileDoesNotExist()
    {
        var appHome = Path.Combine(Path.GetTempPath(), "skyrim-lighting-app-home", Guid.NewGuid().ToString("N"));
        using var scope = new TestEnvironmentScope("SKYRIM_LIGHTING_PATCHER_HOME", appHome);

        var store = new JsonSettingsStore();
        var loaded = await store.LoadAsync();

        Assert.Equal(AppSettings.Default, loaded);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsSettings()
    {
        var appHome = Path.Combine(Path.GetTempPath(), "skyrim-lighting-app-home", Guid.NewGuid().ToString("N"));
        using var scope = new TestEnvironmentScope("SKYRIM_LIGHTING_PATCHER_HOME", appHome);

        var expected = new AppSettings(
            @"C:\mods\staging",
            new PatchSettings(
                EyeValue: 0.7f,
                BodyValue: 0.4f,
                EnableOther: true,
                OtherValue: 0.2f,
                EnableEye: true,
                EnableBody: false));

        var store = new JsonSettingsStore();
        await store.SaveAsync(expected);
        var loaded = await store.LoadAsync();

        Assert.Equal(expected, loaded);
        Assert.True(File.Exists(Path.Combine(appHome, "settings.json")));
    }
}
