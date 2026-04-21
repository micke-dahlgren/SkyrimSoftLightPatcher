using SkyrimLightingPatcher.App.Models;
using SkyrimLightingPatcher.App.Services;
using SkyrimLightingPatcher.Core.Interfaces;
using SkyrimLightingPatcher.Core.Models;

namespace SkyrimLightingPatcher.Tests;

public sealed class DebugFaultInjectingServicesTests
{
    [Fact]
    public async Task PatchExecutor_PreparingOutputLowDisk_ThrowsBeforeInnerExecutorRuns()
    {
        var state = new DebugFaultState
        {
            PatchFailureMode = DebugPatchFailureMode.PatchLowDiskPreparingOutput,
        };
        var inner = new RecordingPatchExecutor();
        var sut = new DebugFaultInjectingPatchExecutor(state, inner);

        var report = CreateReport();
        await Assert.ThrowsAsync<LowDiskSpaceException>(() => sut.ExecuteAsync(report, CreateArchivePath()));
        Assert.False(inner.WasInvoked);
    }

    [Fact]
    public async Task PatchExecutor_WritingOutputManifestLowDisk_ThrowsWhenManifestStageStarts()
    {
        var state = new DebugFaultState
        {
            PatchFailureMode = DebugPatchFailureMode.PatchLowDiskWritingOutputManifest,
        };
        var inner = new RecordingPatchExecutor("__status__: Finalizing patch manifest...");
        var sut = new DebugFaultInjectingPatchExecutor(state, inner);

        var report = CreateReport();
        var error = await Assert.ThrowsAsync<LowDiskSpaceException>(() => sut.ExecuteAsync(report, CreateArchivePath()));

        Assert.Equal(PatchExecutionStages.WritingOutputManifest, error.StageName);
        Assert.True(inner.WasInvoked);
    }

    [Fact]
    public async Task PatchExecutor_CreatingArchiveLowDisk_ThrowsWhenArchiveStageStarts()
    {
        var state = new DebugFaultState
        {
            PatchFailureMode = DebugPatchFailureMode.PatchLowDiskCreatingArchive,
        };
        var inner = new RecordingPatchExecutor("__status__: Creating output archive (.zip)...");
        var sut = new DebugFaultInjectingPatchExecutor(state, inner);

        var report = CreateReport();
        var error = await Assert.ThrowsAsync<LowDiskSpaceException>(() => sut.ExecuteAsync(report, CreateArchivePath()));

        Assert.Equal(PatchExecutionStages.CreatingArchive, error.StageName);
        Assert.True(inner.WasInvoked);
    }

    [Fact]
    public async Task PatchExecutor_WritingRunManifestLowDisk_ThrowsWhenRunManifestStageStarts()
    {
        var state = new DebugFaultState
        {
            PatchFailureMode = DebugPatchFailureMode.PatchLowDiskWritingRunManifest,
        };
        var inner = new RecordingPatchExecutor("__status__: Recording patch run metadata...");
        var sut = new DebugFaultInjectingPatchExecutor(state, inner);

        var report = CreateReport();
        var error = await Assert.ThrowsAsync<LowDiskSpaceException>(() => sut.ExecuteAsync(report, CreateArchivePath()));

        Assert.Equal(PatchExecutionStages.WritingRunManifest, error.StageName);
        Assert.True(inner.WasInvoked);
    }

    [Fact]
    public async Task NifMeshService_WritingPatchedFilesLowDisk_ThrowsLowDiskSpaceException()
    {
        var state = new DebugFaultState
        {
            PatchFailureMode = DebugPatchFailureMode.PatchLowDiskWritingPatchedFiles,
        };
        state.BeginPatchRun();
        var inner = new RecordingNifMeshService();
        var sut = new DebugFaultInjectingNifMeshService(state, inner);

        var error = await Assert.ThrowsAsync<LowDiskSpaceException>(() =>
            sut.WritePatchedFileAsync("source.nif", "output.nif", []));

        Assert.Equal(PatchExecutionStages.WritingPatchedFiles, error.StageName);
        Assert.False(inner.WasWriteInvoked);
    }

    private static ScanReport CreateReport()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "debug-fault-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var filePath = Path.Combine(rootPath, "meshes", "sample.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, "sample");

        return ScanReport.Create(
            new ScanRequest(rootPath, PatchSettings.Default),
            [
                new FileScanResult(
                    filePath,
                    [
                        new ShapeScanResult(
                            new NifShapeProbe(
                                filePath,
                                "shape:1",
                                "Shape",
                                ShaderMetadata.Empty,
                                [],
                                true,
                                0.1f),
                            ShapeKind.Eye,
                            true,
                            0.2f,
                            "Eye",
                            ["Eligible"]),
                    ]),
            ]);
    }

    private static string CreateArchivePath()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), "debug-fault-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputRoot);
        return Path.Combine(outputRoot, "output.zip");
    }

    private sealed class RecordingPatchExecutor(string? statusToReport = null) : IPatchExecutor
    {
        public bool WasInvoked { get; private set; }

        public Task<PatchRunManifest> ExecuteAsync(
            ScanReport report,
            string outputArchivePath,
            IProgress<PatchProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default,
            string? outputRootPath = null)
        {
            WasInvoked = true;
            if (!string.IsNullOrWhiteSpace(statusToReport))
            {
                progress?.Report(new PatchProgressUpdate(statusToReport, 0, 1, 0, 0));
            }

            var resolvedOutputRootPath = outputRootPath
                                         ?? Path.Combine(Path.GetDirectoryName(outputArchivePath)!, "Glowing Mesh Patcher Output");
            return Task.FromResult(new PatchRunManifest(
                Guid.NewGuid().ToString("N"),
                report.Request.RootPath,
                resolvedOutputRootPath,
                outputArchivePath,
                "Glowing Mesh Patcher Output",
                false,
                DateTimeOffset.Now,
                report.Request.Settings,
                []));
        }
    }

    private sealed class RecordingNifMeshService : INifMeshService
    {
        public bool WasWriteInvoked { get; private set; }

        public Task<IReadOnlyList<NifShapeProbe>> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task WritePatchedFileAsync(
            string sourcePath,
            string outputPath,
            IReadOnlyList<ShapePatchOperation> operations,
            CancellationToken cancellationToken = default)
        {
            WasWriteInvoked = true;
            return Task.CompletedTask;
        }
    }
}
