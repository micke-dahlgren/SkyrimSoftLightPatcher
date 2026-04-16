using System.Text.Json;
using SkyrimLightingPatcher.Core.Interfaces;
using SkyrimLightingPatcher.Core.Models;
using SkyrimLightingPatcher.Core.Utilities;

namespace SkyrimLightingPatcher.Core.Services;

public sealed class ScanService : IScanService
{
    private readonly JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly INifMeshService nifMeshService;
    private readonly IShapeClassifier shapeClassifier;
    private readonly IScanFileResolver scanFileResolver;

    public ScanService(INifMeshService nifMeshService, IShapeClassifier shapeClassifier)
        : this(nifMeshService, shapeClassifier, new ScanFileResolver())
    {
    }

    public ScanService(INifMeshService nifMeshService, IShapeClassifier shapeClassifier, IScanFileResolver scanFileResolver)
    {
        this.nifMeshService = nifMeshService;
        this.shapeClassifier = shapeClassifier;
        this.scanFileResolver = scanFileResolver;
    }

    public async Task<ScanReport> ScanAsync(
        ScanRequest request,
        IProgress<ScanProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedSettings = request.Settings.ClampToSafeRange();
        var normalizedRequest = request with { Settings = normalizedSettings };
        var files = new List<FileScanResult>();
        var filesScanned = 0;
        var candidateFiles = 0;
        var patchableEyeShapes = 0;
        var patchableBodyShapes = 0;
        var patchableOtherShapes = 0;
        var patchableShapes = 0;
        var errorFiles = 0;
        var scanErrors = new List<ScanErrorEntry>();
        var sources = await scanFileResolver.ResolveFilePathsAsync(request.RootPath, request.SkyrimDataPath, cancellationToken).ConfigureAwait(false);
        var totalFiles = sources.Count;

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileScanResult fileResult;
            try
            {
                var materializedPath = await scanFileResolver.MaterializeSourceAsync(source, cancellationToken).ConfigureAwait(false);
                var probes = await nifMeshService.ProbeAsync(materializedPath, cancellationToken).ConfigureAwait(false);
                var displayProbes = probes.Select(probe => probe with { FilePath = source.DisplayPath }).ToArray();
                var shapes = displayProbes.Select(probe => CreateScanResult(probe, normalizedSettings)).ToArray();
                fileResult = new FileScanResult(source.DisplayPath, shapes, Source: source);
            }
            catch (Exception exception)
            {
                fileResult = new FileScanResult(source.DisplayPath, Array.Empty<ShapeScanResult>(), exception.Message, source);
                scanErrors.Add(new ScanErrorEntry(
                    source.DisplayPath,
                    source.Kind,
                    source.ArchivePath,
                    source.ArchiveEntryPath,
                    exception.GetType().FullName ?? exception.GetType().Name,
                    exception.Message,
                    exception.ToString()));
            }

            files.Add(fileResult);
            UpdateCounts(
                fileResult,
                ref filesScanned,
                ref candidateFiles,
                ref patchableEyeShapes,
                ref patchableBodyShapes,
                ref patchableOtherShapes,
                ref patchableShapes,
                ref errorFiles);

            progress?.Report(new ScanProgressUpdate(
                source.DisplayPath,
                filesScanned,
                totalFiles,
                candidateFiles,
                patchableEyeShapes,
                patchableBodyShapes,
                patchableOtherShapes,
                patchableShapes,
                errorFiles));
        }

        var report = ScanReport.Create(normalizedRequest, files);
        if (scanErrors.Count == 0)
        {
            return report;
        }

        var logPath = await WriteScanErrorLogAsync(normalizedRequest, report, scanErrors, cancellationToken).ConfigureAwait(false);
        return report with { ScanErrorLogPath = logPath };
    }

    private async Task<string> WriteScanErrorLogAsync(
        ScanRequest request,
        ScanReport report,
        IReadOnlyList<ScanErrorEntry> scanErrors,
        CancellationToken cancellationToken)
    {
        var logsDirectory = Path.Combine(PatchOutputPaths.GetApplicationHomeDirectory(), "Logs");
        Directory.CreateDirectory(logsDirectory);

        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmssfff");
        var logPath = Path.Combine(logsDirectory, $"scan-errors-{timestamp}.json");
        var payload = new ScanErrorLog(
            DateTimeOffset.Now,
            request.RootPath,
            request.Settings,
            report.FilesScanned,
            report.ErrorFiles,
            scanErrors);

        await using var stream = File.Open(logPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, payload, serializerOptions, cancellationToken).ConfigureAwait(false);
        return logPath;
    }

    private static void UpdateCounts(
        FileScanResult file,
        ref int filesScanned,
        ref int candidateFiles,
        ref int patchableEyeShapes,
        ref int patchableBodyShapes,
        ref int patchableOtherShapes,
        ref int patchableShapes,
        ref int errorFiles)
    {
        filesScanned++;

        if (file.HasError)
        {
            errorFiles++;
            return;
        }

        if (file.PatchCandidateCount > 0)
        {
            candidateFiles++;
        }

        foreach (var shape in file.Shapes)
        {
            if (shape.IsPatchCandidate)
            {
                switch (shape.Kind)
                {
                    case ShapeKind.Eye:
                        patchableEyeShapes++;
                        break;
                    case ShapeKind.Body:
                        patchableBodyShapes++;
                        break;
                    case ShapeKind.Other:
                        patchableOtherShapes++;
                        break;
                }

                patchableShapes++;
            }
        }
    }

    private ShapeScanResult CreateScanResult(NifShapeProbe probe, PatchSettings settings)
    {
        var classification = shapeClassifier.Classify(probe);
        var categoryEnabled = classification.Kind switch
        {
            ShapeKind.Eye => settings.EnableEye,
            ShapeKind.Body => settings.EnableBody,
            ShapeKind.Other => settings.EnableOther,
            _ => false,
        };

        float? target1 = null;
        float? target2 = null;
        if (categoryEnabled && probe.HasSoftLighting)
        {
            target1 = 0.0f;
            target2 = 0.0f;
        }

        var needsEffect1Change = target1.HasValue &&
                                 Math.Abs(probe.LightingEffect1 - target1.Value) > 0.0001f;
        var needsEffect2Change = target2.HasValue &&
                                 Math.Abs(probe.LightingEffect2 - target2.Value) > 0.0001f;
        var needsFlagChange = categoryEnabled &&
                              probe.HasSoftLighting &&
                              probe.HasRimLighting;
        var isPatchCandidate = needsEffect1Change || needsEffect2Change || needsFlagChange;

        var reasons = new List<string>(classification.Reasons);
        if (!categoryEnabled)
        {
            reasons.Add("Shape ignored by classifier.");
        }
        else if (!probe.HasSoftLighting)
        {
            reasons.Add("Soft_Lighting flag not detected.");
        }
        else if (!isPatchCandidate)
        {
            reasons.Add("Lighting effects already disabled.");
        }
        else
        {
            reasons.Add(needsFlagChange
                ? "Eligible for patching (LE1/LE2 -> 0.0, clear Soft/Rim flags)."
                : "Eligible for patching (LE1/LE2 -> 0.0).");
        }

        return new ShapeScanResult(
            probe,
            classification.Kind,
            isPatchCandidate,
            target1,
            target2,
            classification.Decision,
            reasons);
    }

    private sealed record ScanErrorEntry(
        string SourcePath,
        MeshSourceKind SourceKind,
        string? ArchivePath,
        string? ArchiveEntryPath,
        string ExceptionType,
        string Message,
        string Details);

    private sealed record ScanErrorLog(
        DateTimeOffset Timestamp,
        string RootPath,
        PatchSettings Settings,
        int FilesScanned,
        int ErrorFiles,
        IReadOnlyList<ScanErrorEntry> Errors);
}
