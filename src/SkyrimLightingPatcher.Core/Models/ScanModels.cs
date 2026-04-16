using System.Collections.ObjectModel;

namespace SkyrimLightingPatcher.Core.Models;

public sealed record PatchSettings(float EyeValue, float BodyValue)
{
    public static PatchSettings Default { get; } = new(1.0f, 1.0f);

    public PatchSettings ClampToSafeRange()
    {
        return new PatchSettings(
            Math.Clamp(EyeValue, 0.0f, 1.0f),
            Math.Clamp(BodyValue, 0.0f, 1.0f));
    }
}

public sealed record AppSettings(string? LastRootPath, PatchSettings PatchSettings)
{
    public static AppSettings Default { get; } = new(null, PatchSettings.Default);
}

public sealed record ScanRequest(string RootPath, PatchSettings Settings);

public sealed record VortexStagingFolder(string RootPath, string Source);

public enum MeshSourceKind
{
    Loose = 0,
    Archive = 1,
}

public sealed record MeshSource(
    string SourceKey,
    string DisplayPath,
    string OutputRelativePath,
    string LocalPath,
    MeshSourceKind Kind,
    string? SourceModName = null,
    string? ArchivePath = null,
    string? ArchiveEntryPath = null);

public sealed record ScanProgressUpdate(
    string CurrentFilePath,
    int FilesScanned,
    int CandidateFiles,
    int PatchableEyeShapes,
    int PatchableBodyShapes,
    int PatchableShapes,
    int ErrorFiles);

public sealed record PatchProgressUpdate(
    string CurrentFilePath,
    int FilesProcessed,
    int TotalFiles,
    int SuccessfulFiles,
    int FailedFiles);

public enum ShapeKind
{
    Ignore = 0,
    Eye = 1,
    Body = 2,
}

public sealed record ShaderMetadata(string? ShaderType, IReadOnlyList<string> Flags)
{
    public static ShaderMetadata Empty { get; } = new(null, Array.Empty<string>());
}

public sealed record NifShapeProbe(
    string FilePath,
    string ShapeKey,
    string ShapeName,
    ShaderMetadata Shader,
    IReadOnlyList<string> TexturePaths,
    bool HasSoftLighting,
    float LightingEffect1);

public sealed record ShapeClassification(ShapeKind Kind, string Decision, IReadOnlyList<string> Reasons)
{
    public static ShapeClassification Ignore(string decision, params string[] reasons)
    {
        return new ShapeClassification(ShapeKind.Ignore, decision, reasons);
    }
}

public sealed record ShapeScanResult(
    NifShapeProbe Probe,
    ShapeKind Kind,
    bool IsPatchCandidate,
    float? TargetValue,
    string Decision,
    IReadOnlyList<string> Reasons);

public sealed record FileScanResult(
    string FilePath,
    IReadOnlyList<ShapeScanResult> Shapes,
    string? ErrorMessage = null,
    MeshSource? Source = null)
{
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public int PatchCandidateCount => Shapes.Count(static shape => shape.IsPatchCandidate);

    public IReadOnlyList<ShapeScanResult> PatchCandidateShapes =>
        Shapes.Where(static shape => shape.IsPatchCandidate).ToArray();
}

public sealed record ScanReport(
    ScanRequest Request,
    IReadOnlyList<FileScanResult> Files,
    int FilesScanned,
    int CandidateFiles,
    int PatchableEyeShapes,
    int PatchableBodyShapes,
    int PatchableShapes,
    int ErrorFiles,
    int SkippedShapes,
    string? ScanErrorLogPath = null)
{
    public static ScanReport Create(ScanRequest request, IReadOnlyList<FileScanResult> files)
    {
        var patchableEyeShapes = 0;
        var patchableBodyShapes = 0;
        var patchableShapes = 0;
        var errorFiles = 0;
        var skippedShapes = 0;
        var candidateFiles = 0;

        foreach (var file in files)
        {
            if (file.HasError)
            {
                errorFiles++;
                continue;
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
                    }

                    patchableShapes++;
                }
                else
                {
                    skippedShapes++;
                }
            }
        }

        return new ScanReport(
            request,
            files,
            files.Count,
            candidateFiles,
            patchableEyeShapes,
            patchableBodyShapes,
            patchableShapes,
            errorFiles,
            skippedShapes,
            null);
    }

    public ReadOnlyCollection<FileScanResult> FileCollection => Files.ToList().AsReadOnly();

    public IReadOnlyList<FileScanResult> PreviewFiles =>
        Files.Where(static file => file.PatchCandidateCount > 0).ToArray();
}
