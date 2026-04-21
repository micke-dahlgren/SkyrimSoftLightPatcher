using SkyrimLightingPatcher.Core.Models;
using SkyrimLightingPatcher.Core.Services;
using SkyrimLightingPatcher.Core.Utilities;

namespace SkyrimLightingPatcher.Tests;

public sealed class BackupStoreTests
{
    [Fact]
    public async Task WriteManifest_ThenListRuns_ReturnsMatchingRoot()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "skyrim-lighting-tests", Guid.NewGuid().ToString("N"));
        var appHome = Path.Combine(Path.GetTempPath(), "skyrim-lighting-app-home", Guid.NewGuid().ToString("N"));
        using var scope = new TestEnvironmentScope("SKYRIM_LIGHTING_PATCHER_HOME", appHome);
        Directory.CreateDirectory(rootPath);
        var store = new BackupStore();
        var outputRootPath = Path.Combine(appHome, "GeneratedMods", "LightingEffect1 Mesh Patcher Output");
        var outputArchivePath = Path.Combine(rootPath, "LightingEffect1 Mesh Patcher Output.zip");
        Directory.CreateDirectory(outputRootPath);
        await File.WriteAllTextAsync(outputArchivePath, "zip");
        var manifest = new PatchRunManifest(
            Guid.NewGuid().ToString("N"),
            rootPath,
            outputRootPath,
            outputArchivePath,
            "LightingEffect1 Mesh Patcher Output",
            false,
            DateTimeOffset.Now,
            new PatchSettings(0.3f, 0.1f),
            [new FilePatchRecord(
                Path.Combine(rootPath, "a.nif"),
                Path.Combine(outputRootPath, "a.nif"),
                string.Empty,
                "Patched",
                [])]);

        await store.WriteManifestAsync(manifest);

        var runs = await store.ListRunsAsync(rootPath);

        Assert.Contains(runs, run => run.RunId == manifest.RunId);
    }

    [Fact]
    public async Task ListRunsAsync_PrunesOrphanedRunFolders()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "skyrim-lighting-tests", Guid.NewGuid().ToString("N"));
        var appHome = Path.Combine(Path.GetTempPath(), "skyrim-lighting-app-home", Guid.NewGuid().ToString("N"));
        using var scope = new TestEnvironmentScope("SKYRIM_LIGHTING_PATCHER_HOME", appHome);
        Directory.CreateDirectory(rootPath);
        var store = new BackupStore();

        var orphanRunId = Guid.NewGuid().ToString("N");
        var orphanManifest = new PatchRunManifest(
            orphanRunId,
            rootPath,
            Path.Combine(appHome, "GeneratedMods", "missing-output"),
            Path.Combine(rootPath, "missing-output.zip"),
            "Glowing Mesh Patcher Output",
            false,
            DateTimeOffset.Now.AddMinutes(-10),
            PatchSettings.Default,
            []);
        await store.WriteManifestAsync(orphanManifest);

        var validRunId = Guid.NewGuid().ToString("N");
        var validOutputRoot = Path.Combine(appHome, "GeneratedMods", "valid-output");
        var validArchivePath = Path.Combine(rootPath, "valid-output.zip");
        Directory.CreateDirectory(validOutputRoot);
        await File.WriteAllTextAsync(validArchivePath, "zip");
        var validManifest = new PatchRunManifest(
            validRunId,
            rootPath,
            validOutputRoot,
            validArchivePath,
            "Glowing Mesh Patcher Output",
            false,
            DateTimeOffset.Now,
            PatchSettings.Default,
            []);
        await store.WriteManifestAsync(validManifest);

        var runs = await store.ListRunsAsync(rootPath);

        Assert.Contains(runs, run => run.RunId == validRunId);
        Assert.DoesNotContain(runs, run => run.RunId == orphanRunId);
        Assert.False(Directory.Exists(Path.Combine(appHome, PatchOutputPaths.BackupsFolderName, orphanRunId)));
    }

    [Fact]
    public async Task WriteManifestAsync_KeepOnlyLatestTwentyRunsPerRoot()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "skyrim-lighting-tests", Guid.NewGuid().ToString("N"));
        var appHome = Path.Combine(Path.GetTempPath(), "skyrim-lighting-app-home", Guid.NewGuid().ToString("N"));
        using var scope = new TestEnvironmentScope("SKYRIM_LIGHTING_PATCHER_HOME", appHome);
        Directory.CreateDirectory(rootPath);
        var store = new BackupStore();

        var runIds = new List<string>();
        for (var index = 0; index < 25; index++)
        {
            var runId = $"run-{index:00}";
            runIds.Add(runId);
            var archivePath = Path.Combine(rootPath, $"output-{index:00}.zip");
            await File.WriteAllTextAsync(archivePath, "zip");
            var manifest = new PatchRunManifest(
                runId,
                rootPath,
                Path.Combine(appHome, "GeneratedMods", $"output-{index:00}"),
                archivePath,
                "Glowing Mesh Patcher Output",
                false,
                DateTimeOffset.Now.AddMinutes(index),
                PatchSettings.Default,
                []);

            await store.WriteManifestAsync(manifest);
        }

        var runs = await store.ListRunsAsync(rootPath);
        var backupDirectories = Directory.Exists(Path.Combine(appHome, PatchOutputPaths.BackupsFolderName))
            ? Directory.EnumerateDirectories(Path.Combine(appHome, PatchOutputPaths.BackupsFolderName)).ToArray()
            : Array.Empty<string>();

        Assert.Equal(20, runs.Count);
        Assert.Equal(20, backupDirectories.Length);
        Assert.Contains(runs, run => run.RunId == "run-24");
        Assert.DoesNotContain(runs, run => run.RunId == "run-00");
        Assert.False(Directory.Exists(Path.Combine(appHome, PatchOutputPaths.BackupsFolderName, "run-00")));
    }
}
