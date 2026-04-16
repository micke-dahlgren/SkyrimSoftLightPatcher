using SkyrimLightingPatcher.Core.Interfaces;
using SkyrimLightingPatcher.Core.Models;
using SkyrimLightingPatcher.Core.Services;

namespace SkyrimLightingPatcher.Tests;

public sealed class PatchExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_ReportsPatchProgressForEachFile()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "skyrim-lighting-patch-tests", Guid.NewGuid().ToString("N"));
        var appHome = Path.Combine(Path.GetTempPath(), "skyrim-lighting-app-home", Guid.NewGuid().ToString("N"));
        using var scope = new TestEnvironmentScope("SKYRIM_LIGHTING_PATCHER_HOME", appHome);
        Directory.CreateDirectory(rootPath);

        var firstFile = Path.Combine(rootPath, "meshes", "first.nif");
        var secondFile = Path.Combine(rootPath, "meshes", "second.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(firstFile)!);
        await File.WriteAllTextAsync(firstFile, "first");
        await File.WriteAllTextAsync(secondFile, "second");

        var report = ScanReport.Create(
            new ScanRequest(rootPath, new PatchSettings(0.4f, 0.2f)),
            [
                new FileScanResult(
                    firstFile,
                    [CreateShapeResult(firstFile, "Eye", ShapeKind.Eye, 0.1f, 0.4f)]),
                new FileScanResult(
                    secondFile,
                    [CreateShapeResult(secondFile, "Body", ShapeKind.Body, 0.1f, 0.2f)]),
            ]);

        var progressUpdates = new List<PatchProgressUpdate>();
        var patchExecutor = new PatchExecutor(new PatchPlanner(), new FakePatchMeshService(), new ScanFileResolver(), new BackupStore());
        var archivePath = Path.Combine(rootPath, "LightingEffect1 Mesh Patcher Output.zip");

        var manifest = await patchExecutor.ExecuteAsync(report, archivePath, new ListProgress<PatchProgressUpdate>(progressUpdates));

        Assert.Equal(2, manifest.Files.Count);
        Assert.Equal(archivePath, manifest.OutputArchivePath);
        Assert.True(File.Exists(archivePath));
        Assert.NotEmpty(progressUpdates);
        Assert.Contains(progressUpdates, static update => update.FilesProcessed == 0 && update.TotalFiles == 2);
        Assert.Contains(progressUpdates, static update => update.FilesProcessed == 1 && update.SuccessfulFiles == 1);
        var finalUpdate = progressUpdates.Last(static update => update.FilesProcessed == 2);
        Assert.Equal(2, finalUpdate.TotalFiles);
        Assert.Equal(2, finalUpdate.SuccessfulFiles);
        Assert.Equal(0, finalUpdate.FailedFiles);
        Assert.Contains(progressUpdates, static update => update.CurrentFilePath == "__status__: Creating output archive (.zip)...");
    }

    private static ShapeScanResult CreateShapeResult(
        string filePath,
        string shapeName,
        ShapeKind kind,
        float currentValue,
        float targetValue)
    {
        return new ShapeScanResult(
            new NifShapeProbe(
                filePath,
                $"{Path.GetFileName(filePath)}:{shapeName}",
                shapeName,
                ShaderMetadata.Empty,
                Array.Empty<string>(),
                true,
                currentValue),
            kind,
            true,
            targetValue,
            kind.ToString(),
            ["Eligible"]);
    }

    private sealed class FakePatchMeshService : INifMeshService
    {
        public Task<IReadOnlyList<NifShapeProbe>> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public async Task WritePatchedFileAsync(
            string sourcePath,
            string outputPath,
            IReadOnlyList<ShapePatchOperation> operations,
            CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, $"{Path.GetFileName(sourcePath)}:{operations.Count}", cancellationToken);
        }
    }

    private sealed class ListProgress<T>(ICollection<T> updates) : IProgress<T>
    {
        public void Report(T value)
        {
            updates.Add(value);
        }
    }
}
