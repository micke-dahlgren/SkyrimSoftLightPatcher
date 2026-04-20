using SkyrimLightingPatcher.Core.Interfaces;
using SkyrimLightingPatcher.Core.Models;

namespace SkyrimLightingPatcher.App.ViewModels;

public partial class MainWindowViewModel
{
    private sealed class DesignTimeSettingsStore : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AppSettings(
                @"C:\Users\Example\AppData\Roaming\Vortex\skyrimse\mods",
                new PatchSettings(1.0f, 1.0f, false, 1.0f, true, true)));
        }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class DesignTimeScanService : IScanService
    {
        public Task<ScanReport> ScanAsync(
            ScanRequest request,
            IProgress<ScanProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default)
        {
            FileScanResult[] files =
            [
                new FileScanResult(
                    Path.Combine(request.RootPath, "Aesthetic Eye Pack", "meshes", "actors", "character", "eyes", "blue.nif"),
                    [
                        new ShapeScanResult(
                            new NifShapeProbe(
                                "example.nif",
                                "shape-0",
                                "Eyes",
                                new ShaderMetadata("EnvironmentMap", ["Soft_Lighting"]),
                                [@"textures\actors\character\eyes\blueeye.dds"],
                                true,
                                false,
                                false,
                                0.20f,
                                0.00f),
                            ShapeKind.Eye,
                            true,
                            0.0f,
                            0.0f,
                            "Eye",
                            ["Matched eye texture directory.", "Eligible for patching."]),
                    ]),
                new FileScanResult(
                    Path.Combine(request.RootPath, "Skysight Skins", "meshes", "actors", "character", "facegendata", "facegeom", "npc_01.nif"),
                    [
                        new ShapeScanResult(
                            new NifShapeProbe(
                                "example.nif",
                                "shape-1",
                                "FemaleHead",
                                new ShaderMetadata("Face", ["Soft_Lighting"]),
                                [@"textures\actors\character\femalebody_1.dds"],
                                true,
                                false,
                                false,
                                0.15f,
                                0.00f),
                            ShapeKind.Body,
                            true,
                            0.0f,
                            0.0f,
                            "Body",
                            ["Matched skin-like texture path.", "Eligible for patching."]),
                    ]),
            ];

            progress?.Report(new ScanProgressUpdate(files[0].FilePath, 1, 2, 1, 1, 0, 0, 1, 0));
            progress?.Report(new ScanProgressUpdate(files[1].FilePath, 2, 2, 2, 1, 1, 0, 2, 0));

            return Task.FromResult(ScanReport.Create(request, files));
        }
    }

    private sealed class DesignTimePatchExecutor : IPatchExecutor
    {
        public Task<PatchRunManifest> ExecuteAsync(
            ScanReport report,
            string outputArchivePath,
            IProgress<PatchProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default,
            string? outputRootPath = null)
        {
            progress?.Report(new PatchProgressUpdate("example-1.nif", 0, 2, 0, 0));
            progress?.Report(new PatchProgressUpdate("example-1.nif", 1, 2, 1, 0));
            progress?.Report(new PatchProgressUpdate("example-2.nif", 2, 2, 2, 0));
            progress?.Report(new PatchProgressUpdate("__status__: Creating output archive (.zip)...", 2, 2, 2, 0));
            return Task.FromResult(new PatchRunManifest(
                "design-time",
                report.Request.RootPath,
                outputRootPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyrimLightingPatcher", "GeneratedMods", "Glowing Mesh Patcher Output"),
                outputArchivePath,
                "Glowing Mesh Patcher Output",
                false,
                DateTimeOffset.Now,
                report.Request.Settings,
                []));
        }
    }

    private sealed class DesignTimeOutputModService : IOutputModService
    {
        public Task<IReadOnlyList<BackupRunInfo>> ListRunsAsync(string rootPath, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<BackupRunInfo> runs =
            [
                new BackupRunInfo(
                    "202604130001",
                    rootPath,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyrimLightingPatcher", "GeneratedMods", "Glowing Mesh Patcher Output"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Glowing Mesh Patcher Output.zip"),
                    "Glowing Mesh Patcher Output",
                    DateTimeOffset.Now.AddMinutes(-30),
                    4,
                    9,
                    "manifest.json"),
            ];

            return Task.FromResult(runs);
        }

        public Task<PatchRunManifest> DeleteAsync(string runId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class DesignTimeVortexPathResolver : IVortexPathResolver
    {
        public Task<VortexStagingFolder?> TryResolveSkyrimSeAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<VortexStagingFolder?>(new VortexStagingFolder(
                @"C:\Users\Example\AppData\Roaming\Vortex\skyrimse\mods",
                "Detected Vortex Skyrim SE staging folder."));
        }

        public Task<string?> TryResolveSkyrimDataPathAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(@"C:\Games\Skyrim Special Edition\Data");
        }
    }

    private sealed class DesignTimeModOrganizer2PathResolver : IModOrganizer2PathResolver
    {
        public Task<ModOrganizer2Instance?> TryResolveSkyrimSeAsync(
            ModOrganizer2InstanceKind? preferredKind = null,
            CancellationToken cancellationToken = default)
        {
            var instanceKind = preferredKind ?? ModOrganizer2InstanceKind.Global;
            return Task.FromResult<ModOrganizer2Instance?>(new ModOrganizer2Instance(
                @"C:\Users\Example\AppData\Local\ModOrganizer\Skyrim Special Edition",
                @"C:\Users\Example\AppData\Local\ModOrganizer\Skyrim Special Edition\mods",
                @"C:\Users\Example\AppData\Local\ModOrganizer\Skyrim Special Edition\profiles",
                "Default",
                instanceKind == ModOrganizer2InstanceKind.Portable
                    ? "Detected Mod Organizer 2 Skyrim SE portable instance."
                    : "Detected Mod Organizer 2 Skyrim SE global instance.",
                instanceKind));
        }
    }
}
