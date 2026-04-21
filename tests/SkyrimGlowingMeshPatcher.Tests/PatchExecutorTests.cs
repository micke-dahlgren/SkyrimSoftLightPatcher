using System.IO.Compression;
using SkyrimGlowingMeshPatcher.Core.Interfaces;
using SkyrimGlowingMeshPatcher.Core.Models;
using SkyrimGlowingMeshPatcher.Core.Services;
using SkyrimGlowingMeshPatcher.Core.Utilities;

namespace SkyrimGlowingMeshPatcher.Tests;

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

        var manifest = await patchExecutor.ExecuteAsync(
            report,
            archivePath,
            OutputRootForArchive(archivePath),
            new ListProgress<PatchProgressUpdate>(progressUpdates));

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

    [Fact]
    public async Task ExecuteAsync_UsesProvidedOutputRootPath()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "skyrim-lighting-patch-tests", Guid.NewGuid().ToString("N"));
        var appHome = Path.Combine(Path.GetTempPath(), "skyrim-lighting-app-home", Guid.NewGuid().ToString("N"));
        using var scope = new TestEnvironmentScope("SKYRIM_LIGHTING_PATCHER_HOME", appHome);
        Directory.CreateDirectory(rootPath);

        var sourceFile = Path.Combine(rootPath, "meshes", "sample.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        await File.WriteAllTextAsync(sourceFile, "sample");

        var report = ScanReport.Create(
            new ScanRequest(rootPath, new PatchSettings(0.4f, 0.2f)),
            [
                new FileScanResult(
                    sourceFile,
                    [CreateShapeResult(sourceFile, "Eye", ShapeKind.Eye, 0.1f, 0.4f)]),
            ]);

        var destinationPath = Path.Combine(rootPath, "custom-output-destination");
        var outputRootPath = Path.Combine(destinationPath, "Glowing Mesh Patcher Output");
        var archivePath = Path.Combine(destinationPath, "Glowing Mesh Patcher Output.zip");
        var unexpectedFallbackPath = Path.Combine(rootPath, "Glowing Mesh Patcher Output");
        var patchExecutor = new PatchExecutor(new PatchPlanner(), new FakePatchMeshService(), new ScanFileResolver(), new BackupStore());

        var manifest = await patchExecutor.ExecuteAsync(report, archivePath, outputRootPath);

        Assert.Equal(Path.GetFullPath(outputRootPath), manifest.OutputRootPath);
        Assert.True(Directory.Exists(outputRootPath));
        Assert.True(File.Exists(archivePath));
        Assert.All(manifest.Files, file => Assert.StartsWith(
            Path.GetFullPath(outputRootPath),
            Path.GetFullPath(file.OutputPath),
            StringComparison.OrdinalIgnoreCase));
        Assert.False(Directory.Exists(unexpectedFallbackPath));
    }

    [Fact]
    public async Task ExecuteAsync_WhenArchiveCreationFails_ThrowsAndKeepsLooseOutput()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "skyrim-lighting-patch-tests", Guid.NewGuid().ToString("N"));
        var appHome = Path.Combine(Path.GetTempPath(), "skyrim-lighting-app-home", Guid.NewGuid().ToString("N"));
        using var scope = new TestEnvironmentScope("SKYRIM_LIGHTING_PATCHER_HOME", appHome);
        Directory.CreateDirectory(rootPath);

        var sourceFile = Path.Combine(rootPath, "meshes", "sample.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        await File.WriteAllTextAsync(sourceFile, "sample");

        var report = ScanReport.Create(
            new ScanRequest(rootPath, new PatchSettings(0.4f, 0.2f)),
            [
                new FileScanResult(
                    sourceFile,
                    [CreateShapeResult(sourceFile, "Eye", ShapeKind.Eye, 0.1f, 0.4f)]),
            ]);

        var destinationPath = Path.Combine(rootPath, "custom-output-destination");
        var outputRootPath = Path.Combine(destinationPath, "Glowing Mesh Patcher Output");
        var archivePath = Path.Combine(destinationPath, "Glowing Mesh Patcher Output.zip");
        var archiveTempPath = archivePath + ".tmp";
        Directory.CreateDirectory(archiveTempPath);

        var patchExecutor = new PatchExecutor(new PatchPlanner(), new FakePatchMeshService(), new ScanFileResolver(), new BackupStore());
        var error = await Assert.ThrowsAsync<PatchArchiveCreationException>(() =>
            patchExecutor.ExecuteAsync(report, archivePath, outputRootPath));

        Assert.Equal(Path.GetFullPath(outputRootPath), Path.GetFullPath(error.OutputRootPath));
        Assert.Equal(Path.GetFullPath(archivePath), Path.GetFullPath(error.OutputArchivePath));
        Assert.True(Directory.Exists(outputRootPath));
        Assert.NotEmpty(Directory.EnumerateFiles(outputRootPath, "*.nif", SearchOption.AllDirectories));
        Assert.False(File.Exists(archivePath));
    }

    [Fact]
    public async Task ExecuteAsync_WhenLowSpaceBeforePreparingOutput_ThrowsLowDiskSpaceException()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "skyrim-lighting-patch-tests", Guid.NewGuid().ToString("N"));
        var appHome = Path.Combine(Path.GetTempPath(), "skyrim-lighting-app-home", Guid.NewGuid().ToString("N"));
        using var scope = new TestEnvironmentScope("SKYRIM_LIGHTING_PATCHER_HOME", appHome);
        Directory.CreateDirectory(rootPath);

        var sourceFile = Path.Combine(rootPath, "meshes", "sample.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        await File.WriteAllTextAsync(sourceFile, "sample");

        var report = ScanReport.Create(
            new ScanRequest(rootPath, new PatchSettings(0.4f, 0.2f)),
            [
                new FileScanResult(
                    sourceFile,
                    [CreateShapeResult(sourceFile, "Eye", ShapeKind.Eye, 0.1f, 0.4f)]),
            ]);

        var monitor = new FakeDiskSpaceMonitor();
        monitor.AvailableByStage[PatchExecutionStages.PreparingOutput] = 0;

        var patchExecutor = new PatchExecutor(
            new PatchPlanner(),
            new FakePatchMeshService(),
            new ScanFileResolver(),
            new BackupStore(),
            monitor);
        var archivePath = Path.Combine(rootPath, "output.zip");

        var error = await Assert.ThrowsAsync<LowDiskSpaceException>(() =>
            patchExecutor.ExecuteAsync(report, archivePath, OutputRootForArchive(archivePath)));

        Assert.Equal(PatchExecutionStages.PreparingOutput, error.StageName);
        Assert.Contains("Quick cleanup command", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(error.RequiredBytes > error.AvailableBytes);
    }

    [Fact]
    public async Task ExecuteAsync_WhenLowSpaceBeforeWritingPatchedFiles_ThrowsLowDiskSpaceException()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "skyrim-lighting-patch-tests", Guid.NewGuid().ToString("N"));
        var appHome = Path.Combine(Path.GetTempPath(), "skyrim-lighting-app-home", Guid.NewGuid().ToString("N"));
        using var scope = new TestEnvironmentScope("SKYRIM_LIGHTING_PATCHER_HOME", appHome);
        Directory.CreateDirectory(rootPath);

        var sourceFile = Path.Combine(rootPath, "meshes", "sample.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        await File.WriteAllTextAsync(sourceFile, "sample");

        var report = ScanReport.Create(
            new ScanRequest(rootPath, new PatchSettings(0.4f, 0.2f)),
            [
                new FileScanResult(
                    sourceFile,
                    [CreateShapeResult(sourceFile, "Eye", ShapeKind.Eye, 0.1f, 0.4f)]),
            ]);

        var monitor = new FakeDiskSpaceMonitor();
        monitor.AvailableByStage[PatchExecutionStages.WritingPatchedFiles] = 0;

        var patchExecutor = new PatchExecutor(
            new PatchPlanner(),
            new FakePatchMeshService(),
            new ScanFileResolver(),
            new BackupStore(),
            monitor);
        var archivePath = Path.Combine(rootPath, "output.zip");

        var error = await Assert.ThrowsAsync<LowDiskSpaceException>(() =>
            patchExecutor.ExecuteAsync(report, archivePath, OutputRootForArchive(archivePath)));

        Assert.Equal(PatchExecutionStages.WritingPatchedFiles, error.StageName);
        Assert.False(File.Exists(archivePath));
    }

    [Fact]
    public async Task ExecuteAsync_WhenLowSpaceBeforeWritingOutputManifest_ThrowsLowDiskSpaceException()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "skyrim-lighting-patch-tests", Guid.NewGuid().ToString("N"));
        var appHome = Path.Combine(Path.GetTempPath(), "skyrim-lighting-app-home", Guid.NewGuid().ToString("N"));
        using var scope = new TestEnvironmentScope("SKYRIM_LIGHTING_PATCHER_HOME", appHome);
        Directory.CreateDirectory(rootPath);

        var sourceFile = Path.Combine(rootPath, "meshes", "sample.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        await File.WriteAllTextAsync(sourceFile, "sample");

        var report = ScanReport.Create(
            new ScanRequest(rootPath, new PatchSettings(0.4f, 0.2f)),
            [
                new FileScanResult(
                    sourceFile,
                    [CreateShapeResult(sourceFile, "Eye", ShapeKind.Eye, 0.1f, 0.4f)]),
            ]);

        var monitor = new FakeDiskSpaceMonitor();
        monitor.AvailableByStage[PatchExecutionStages.WritingOutputManifest] = 0;

        var patchExecutor = new PatchExecutor(
            new PatchPlanner(),
            new FakePatchMeshService(),
            new ScanFileResolver(),
            new BackupStore(),
            monitor);
        var archivePath = Path.Combine(rootPath, "output.zip");

        var error = await Assert.ThrowsAsync<LowDiskSpaceException>(() =>
            patchExecutor.ExecuteAsync(report, archivePath, OutputRootForArchive(archivePath)));

        Assert.Equal(PatchExecutionStages.WritingOutputManifest, error.StageName);
        Assert.False(File.Exists(archivePath));
    }

    [Fact]
    public async Task ExecuteAsync_WhenLowSpaceBeforeCreatingArchive_ThrowsLowDiskSpaceExceptionAndKeepsLooseOutput()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "skyrim-lighting-patch-tests", Guid.NewGuid().ToString("N"));
        var appHome = Path.Combine(Path.GetTempPath(), "skyrim-lighting-app-home", Guid.NewGuid().ToString("N"));
        using var scope = new TestEnvironmentScope("SKYRIM_LIGHTING_PATCHER_HOME", appHome);
        Directory.CreateDirectory(rootPath);

        var sourceFile = Path.Combine(rootPath, "meshes", "sample.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        await File.WriteAllTextAsync(sourceFile, "sample");

        var report = ScanReport.Create(
            new ScanRequest(rootPath, new PatchSettings(0.4f, 0.2f)),
            [
                new FileScanResult(
                    sourceFile,
                    [CreateShapeResult(sourceFile, "Eye", ShapeKind.Eye, 0.1f, 0.4f)]),
            ]);

        var monitor = new FakeDiskSpaceMonitor();
        monitor.AvailableByStage[PatchExecutionStages.CreatingArchive] = 0;

        var destinationPath = Path.Combine(rootPath, "custom-output-destination");
        var outputRootPath = Path.Combine(destinationPath, "Glowing Mesh Patcher Output");
        var archivePath = Path.Combine(destinationPath, "Glowing Mesh Patcher Output.zip");

        var patchExecutor = new PatchExecutor(
            new PatchPlanner(),
            new FakePatchMeshService(),
            new ScanFileResolver(),
            new BackupStore(),
            monitor);
        var error = await Assert.ThrowsAsync<LowDiskSpaceException>(() =>
            patchExecutor.ExecuteAsync(report, archivePath, outputRootPath));

        Assert.Equal(PatchExecutionStages.CreatingArchive, error.StageName);
        Assert.True(Directory.Exists(outputRootPath));
        Assert.NotEmpty(Directory.EnumerateFiles(outputRootPath, "*.nif", SearchOption.AllDirectories));
        Assert.False(File.Exists(archivePath));
    }

    [Fact]
    public async Task ExecuteAsync_WhenLowSpaceBeforeWritingRunManifest_ThrowsLowDiskSpaceExceptionAfterArchiveIsCreated()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "skyrim-lighting-patch-tests", Guid.NewGuid().ToString("N"));
        var appHome = Path.Combine(Path.GetTempPath(), "skyrim-lighting-app-home", Guid.NewGuid().ToString("N"));
        using var scope = new TestEnvironmentScope("SKYRIM_LIGHTING_PATCHER_HOME", appHome);
        Directory.CreateDirectory(rootPath);

        var sourceFile = Path.Combine(rootPath, "meshes", "sample.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        await File.WriteAllTextAsync(sourceFile, "sample");

        var report = ScanReport.Create(
            new ScanRequest(rootPath, new PatchSettings(0.4f, 0.2f)),
            [
                new FileScanResult(
                    sourceFile,
                    [CreateShapeResult(sourceFile, "Eye", ShapeKind.Eye, 0.1f, 0.4f)]),
            ]);

        var monitor = new FakeDiskSpaceMonitor();
        monitor.AvailableByStage[PatchExecutionStages.WritingRunManifest] = 0;

        var archivePath = Path.Combine(rootPath, "output.zip");
        var patchExecutor = new PatchExecutor(
            new PatchPlanner(),
            new FakePatchMeshService(),
            new ScanFileResolver(),
            new BackupStore(),
            monitor);
        var error = await Assert.ThrowsAsync<LowDiskSpaceException>(() =>
            patchExecutor.ExecuteAsync(report, archivePath, OutputRootForArchive(archivePath)));

        Assert.Equal(PatchExecutionStages.WritingRunManifest, error.StageName);
        Assert.True(File.Exists(archivePath));
    }

    [Fact]
    public async Task ExecuteAsync_ReservesMinimumWorkingSpace()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "skyrim-lighting-patch-tests", Guid.NewGuid().ToString("N"));
        var appHome = Path.Combine(Path.GetTempPath(), "skyrim-lighting-app-home", Guid.NewGuid().ToString("N"));
        using var scope = new TestEnvironmentScope("SKYRIM_LIGHTING_PATCHER_HOME", appHome);
        Directory.CreateDirectory(rootPath);

        var sourceFile = Path.Combine(rootPath, "meshes", "sample.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        await File.WriteAllTextAsync(sourceFile, "sample");

        var report = ScanReport.Create(
            new ScanRequest(rootPath, new PatchSettings(0.4f, 0.2f)),
            [
                new FileScanResult(
                    sourceFile,
                    [CreateShapeResult(sourceFile, "Eye", ShapeKind.Eye, 0.1f, 0.4f)]),
            ]);

        var monitor = new FakeDiskSpaceMonitor();
        var patchExecutor = new PatchExecutor(
            new PatchPlanner(),
            new FakePatchMeshService(),
            new ScanFileResolver(),
            new BackupStore(),
            monitor);
        var archivePath = Path.Combine(rootPath, "output.zip");

        _ = await patchExecutor.ExecuteAsync(report, archivePath, OutputRootForArchive(archivePath));

        Assert.Contains(
            monitor.Reservations,
            reservation =>
                reservation.Stage == PatchExecutionStages.PreparingOutput &&
                reservation.Bytes == 256L * 1024 * 1024);
    }

    [Fact]
    public async Task ExecuteAsync_WhenOneFileFails_WritesErrorLogAndContinuesRun()
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

        var patchExecutor = new PatchExecutor(
            new PatchPlanner(),
            new SingleFailurePatchMeshService("first.nif"),
            new ScanFileResolver(),
            new BackupStore());
        var archivePath = Path.Combine(rootPath, "LightingEffect1 Mesh Patcher Output.zip");

        var manifest = await patchExecutor.ExecuteAsync(report, archivePath, OutputRootForArchive(archivePath));

        Assert.Equal(2, manifest.Files.Count);
        Assert.Equal(1, manifest.Files.Count(static file => file.Status == "Failed"));
        Assert.Equal(1, manifest.Files.Count(static file => file.Status == "Patched"));

        var failedFile = Assert.Single(manifest.Files.Where(static file => file.Status == "Failed"));
        Assert.Equal(firstFile, failedFile.FilePath);
        Assert.Contains("Simulated write failure", failedFile.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var errorLogPath = Path.Combine(manifest.OutputRootPath, PatchOutputPaths.PatchErrorLogFileName);
        Assert.True(File.Exists(errorLogPath));
        var errorLogContent = await File.ReadAllTextAsync(errorLogPath);
        Assert.Contains(firstFile, errorLogContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Simulated write failure", errorLogContent, StringComparison.OrdinalIgnoreCase);

        Assert.True(File.Exists(archivePath));
        var archiveErrorLogPath = PatchOutputPaths.GetArchiveErrorLogPath(archivePath);
        Assert.True(File.Exists(archiveErrorLogPath));
        var archiveErrorLogContent = await File.ReadAllTextAsync(archiveErrorLogPath);
        Assert.Contains(firstFile, archiveErrorLogContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Simulated write failure", archiveErrorLogContent, StringComparison.OrdinalIgnoreCase);
        using var archive = ZipFile.OpenRead(archivePath);
        Assert.Contains(
            archive.Entries,
            static entry => string.Equals(entry.FullName, PatchOutputPaths.PatchErrorLogFileName, StringComparison.OrdinalIgnoreCase));
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

    private static string OutputRootForArchive(string archivePath)
    {
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(archivePath))!, PatchOutputPaths.OutputModName);
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

    private sealed class SingleFailurePatchMeshService(string fileNameToFail) : INifMeshService
    {
        private bool hasFailed;

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
            if (!hasFailed &&
                string.Equals(Path.GetFileName(sourcePath), fileNameToFail, StringComparison.OrdinalIgnoreCase))
            {
                hasFailed = true;
                throw new IOException($"Simulated write failure for {Path.GetFileName(sourcePath)}");
            }

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

    private sealed class FakeDiskSpaceMonitor : IDiskSpaceMonitor
    {
        public long DefaultAvailableBytes { get; set; } = long.MaxValue;

        public Dictionary<string, long> AvailableByStage { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<ReservationRecord> Reservations { get; } = [];

        public long GetAvailableBytes(string stageName, string targetPath)
        {
            return AvailableByStage.TryGetValue(stageName, out var availableBytes)
                ? availableBytes
                : DefaultAvailableBytes;
        }

        public IDisposable ReserveSpace(string stageName, string targetPath, string reservationName, long bytes)
        {
            Reservations.Add(new ReservationRecord(stageName, targetPath, reservationName, bytes));
            return new NoOpReservation();
        }
    }

    private sealed record ReservationRecord(string Stage, string TargetPath, string ReservationName, long Bytes);

    private sealed class NoOpReservation : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
