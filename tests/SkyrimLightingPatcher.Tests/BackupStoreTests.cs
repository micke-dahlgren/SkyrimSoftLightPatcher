using System.Text.Json;
using SkyrimLightingPatcher.Core.Models;
using SkyrimLightingPatcher.Core.Services;

namespace SkyrimLightingPatcher.Tests;

public sealed class BackupStoreTests
{
    [Fact]
    public async Task WriteManifest_ThenListRuns_ReturnsMatchingRoot()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "skyrim-lighting-tests", Guid.NewGuid().ToString("N"));
        var appHome = Path.Combine(Path.GetTempPath(), "skyrim-lighting-app-home", Guid.NewGuid().ToString("N"));
        using var scope = new TestEnvironmentScope("SKYRIM_LIGHTING_PATCHER_HOME", appHome);
        var store = new BackupStore();
        var manifest = new PatchRunManifest(
            Guid.NewGuid().ToString("N"),
            rootPath,
            Path.Combine(appHome, "GeneratedMods", "LightingEffect1 Mesh Patcher Output"),
            Path.Combine(rootPath, "LightingEffect1 Mesh Patcher Output.zip"),
            "LightingEffect1 Mesh Patcher Output",
            false,
            DateTimeOffset.Now,
            new PatchSettings(0.3f, 0.1f),
            [new FilePatchRecord(
                Path.Combine(rootPath, "a.nif"),
                Path.Combine(appHome, "GeneratedMods", "LightingEffect1 Mesh Patcher Output", "a.nif"),
                string.Empty,
                "Patched",
                [])]);

        await store.WriteManifestAsync(manifest);

        var runs = await store.ListRunsAsync(rootPath);

        Assert.Contains(runs, run => run.RunId == manifest.RunId);
    }

    [Fact]
    public async Task BackupFileAsync_CopiesTheOriginalFile()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "skyrim-lighting-tests", Guid.NewGuid().ToString("N"));
        var appHome = Path.Combine(Path.GetTempPath(), "skyrim-lighting-app-home", Guid.NewGuid().ToString("N"));
        using var scope = new TestEnvironmentScope("SKYRIM_LIGHTING_PATCHER_HOME", appHome);
        var store = new BackupStore();
        Directory.CreateDirectory(rootPath);
        var sourcePath = Path.Combine(rootPath, "meshes", "body.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllTextAsync(sourcePath, JsonSerializer.Serialize(new { sample = true }));

        var backupPath = await store.BackupFileAsync(Guid.NewGuid().ToString("N"), rootPath, sourcePath);

        Assert.True(File.Exists(backupPath));
        Assert.Equal(await File.ReadAllTextAsync(sourcePath), await File.ReadAllTextAsync(backupPath));
    }
}
