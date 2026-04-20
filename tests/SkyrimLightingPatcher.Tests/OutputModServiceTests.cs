using SkyrimLightingPatcher.Core.Models;
using SkyrimLightingPatcher.Core.Services;

namespace SkyrimLightingPatcher.Tests;

public sealed class OutputModServiceTests
{
    [Fact]
    public async Task ListRunsAsync_OnlyReturnsExistingGeneratedOutputs()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "skyrim-lighting-output-tests", Guid.NewGuid().ToString("N"));
        var appHome = Path.Combine(Path.GetTempPath(), "skyrim-lighting-app-home", Guid.NewGuid().ToString("N"));
        var outputPath = Path.Combine(appHome, "GeneratedMods", "Glowing Mesh Patcher Output");
        var archivePath = Path.Combine(rootPath, "Glowing Mesh Patcher Output.zip");
        using var scope = new TestEnvironmentScope("SKYRIM_LIGHTING_PATCHER_HOME", appHome);
        Directory.CreateDirectory(rootPath);
        Directory.CreateDirectory(outputPath);
        await File.WriteAllTextAsync(archivePath, "zip");

        var store = new BackupStore();
        var manifest = new PatchRunManifest(
            Guid.NewGuid().ToString("N"),
            rootPath,
            outputPath,
            archivePath,
            "Glowing Mesh Patcher Output",
            false,
            DateTimeOffset.Now,
            new PatchSettings(0.4f, 0.1f),
            []);

        await store.WriteManifestAsync(manifest);

        var service = new OutputModService(store);
        var runs = await service.ListRunsAsync(rootPath);

        var run = Assert.Single(runs);
        Assert.Equal(archivePath, run.OutputArchivePath);
    }

    [Fact]
    public async Task DeleteAsync_RemovesGeneratedOutputFolder()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "skyrim-lighting-output-tests", Guid.NewGuid().ToString("N"));
        var appHome = Path.Combine(Path.GetTempPath(), "skyrim-lighting-app-home", Guid.NewGuid().ToString("N"));
        var outputPath = Path.Combine(appHome, "GeneratedMods", "Glowing Mesh Patcher Output");
        var archivePath = Path.Combine(rootPath, "Glowing Mesh Patcher Output.zip");
        using var scope = new TestEnvironmentScope("SKYRIM_LIGHTING_PATCHER_HOME", appHome);
        Directory.CreateDirectory(rootPath);
        Directory.CreateDirectory(outputPath);
        await File.WriteAllTextAsync(Path.Combine(outputPath, "sample.nif"), "patched");

        var store = new BackupStore();
        var manifest = new PatchRunManifest(
            Guid.NewGuid().ToString("N"),
            rootPath,
            outputPath,
            archivePath,
            "Glowing Mesh Patcher Output",
            false,
            DateTimeOffset.Now,
            new PatchSettings(0.4f, 0.1f),
            []);

        await store.WriteManifestAsync(manifest);

        var service = new OutputModService(store);
        await service.DeleteAsync(manifest.RunId);

        Assert.False(Directory.Exists(outputPath));
    }

    [Fact]
    public async Task DeleteAsync_RemovesGeneratedOutputFolderWhenItMatchesArchiveDestination()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "skyrim-lighting-output-tests", Guid.NewGuid().ToString("N"));
        var appHome = Path.Combine(Path.GetTempPath(), "skyrim-lighting-app-home", Guid.NewGuid().ToString("N"));
        var destinationPath = Path.Combine(Path.GetTempPath(), "skyrim-lighting-output-destination", Guid.NewGuid().ToString("N"));
        var outputPath = Path.Combine(destinationPath, "Glowing Mesh Patcher Output");
        var archivePath = Path.Combine(destinationPath, "Glowing Mesh Patcher Output.zip");
        using var scope = new TestEnvironmentScope("SKYRIM_LIGHTING_PATCHER_HOME", appHome);
        Directory.CreateDirectory(rootPath);
        Directory.CreateDirectory(outputPath);
        await File.WriteAllTextAsync(Path.Combine(outputPath, "sample.nif"), "patched");

        var store = new BackupStore();
        var manifest = new PatchRunManifest(
            Guid.NewGuid().ToString("N"),
            rootPath,
            outputPath,
            archivePath,
            "Glowing Mesh Patcher Output",
            false,
            DateTimeOffset.Now,
            new PatchSettings(0.4f, 0.1f),
            []);

        await store.WriteManifestAsync(manifest);

        var service = new OutputModService(store);
        await service.DeleteAsync(manifest.RunId);

        Assert.False(Directory.Exists(outputPath));
    }

    [Fact]
    public async Task DeleteAsync_ThrowsForUnmanagedOutputFolder()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "skyrim-lighting-output-tests", Guid.NewGuid().ToString("N"));
        var appHome = Path.Combine(Path.GetTempPath(), "skyrim-lighting-app-home", Guid.NewGuid().ToString("N"));
        var unmanagedOutputPath = Path.Combine(Path.GetTempPath(), "skyrim-lighting-unmanaged", Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(rootPath, "Glowing Mesh Patcher Output.zip");
        using var scope = new TestEnvironmentScope("SKYRIM_LIGHTING_PATCHER_HOME", appHome);
        Directory.CreateDirectory(rootPath);
        Directory.CreateDirectory(unmanagedOutputPath);

        var store = new BackupStore();
        var manifest = new PatchRunManifest(
            Guid.NewGuid().ToString("N"),
            rootPath,
            unmanagedOutputPath,
            archivePath,
            "Glowing Mesh Patcher Output",
            false,
            DateTimeOffset.Now,
            new PatchSettings(0.4f, 0.1f),
            []);
        await store.WriteManifestAsync(manifest);

        var service = new OutputModService(store);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(manifest.RunId));

        Assert.Contains("unmanaged output folder", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(unmanagedOutputPath));
    }

    [Fact]
    public async Task DeleteAsync_ThrowsWhenRunDoesNotExist()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "skyrim-lighting-output-tests", Guid.NewGuid().ToString("N"));
        var appHome = Path.Combine(Path.GetTempPath(), "skyrim-lighting-app-home", Guid.NewGuid().ToString("N"));
        using var scope = new TestEnvironmentScope("SKYRIM_LIGHTING_PATCHER_HOME", appHome);
        Directory.CreateDirectory(rootPath);

        var service = new OutputModService(new BackupStore());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync("missing-run"));
        Assert.Contains("was not found", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
