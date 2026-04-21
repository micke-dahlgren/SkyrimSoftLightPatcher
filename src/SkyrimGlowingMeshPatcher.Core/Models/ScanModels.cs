using System.Collections.ObjectModel;

namespace SkyrimGlowingMeshPatcher.Core.Models;

public sealed record PatchSettings(
    float EyeValue,
    float BodyValue,
    bool EnableOther = false,
    float OtherValue = 0.15f,
    bool EnableEye = true,
    bool EnableBody = true)
{
    public static PatchSettings Default { get; } = new(1.0f, 1.0f, false, 0.15f, true, true);

    public PatchSettings ClampToSafeRange()
    {
        return new PatchSettings(
            Math.Clamp(EyeValue, 0.0f, 1.0f),
            Math.Clamp(BodyValue, 0.0f, 1.0f),
            EnableOther,
            Math.Clamp(OtherValue, 0.0f, 1.0f),
            EnableEye,
            EnableBody);
    }
}

public sealed record AppSettings(string? LastRootPath, PatchSettings PatchSettings)
{
    public static AppSettings Default { get; } = new(null, PatchSettings.Default);
}

public enum ModManagerKind
{
    Vortex = 0,
    ModOrganizer2 = 1,
}

public sealed record ScanRequest(
    string RootPath,
    PatchSettings Settings,
    string? SkyrimDataPath = null,
    ModManagerKind ModManager = ModManagerKind.Vortex);

public sealed record VortexStagingFolder(string RootPath, string Source);
public enum ModOrganizer2InstanceKind
{
    Global = 0,
    Portable = 1,
}

public sealed record ModOrganizer2Instance(
    string InstancePath,
    string ModsPath,
    string ProfilesPath,
    string SelectedProfileName,
    string Source,
    ModOrganizer2InstanceKind InstanceKind = ModOrganizer2InstanceKind.Global);

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
    int TotalFiles,
    int CandidateFiles,
    int PatchableEyeShapes,
    int PatchableBodyShapes,
    int PatchableOtherShapes,
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
    Other = 3,
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
    bool HasRimLighting,
    bool HasBackLighting,
    float LightingEffect1,
    float LightingEffect2)
{
    public NifShapeProbe(
        string FilePath,
        string ShapeKey,
        string ShapeName,
        ShaderMetadata Shader,
        IReadOnlyList<string> TexturePaths,
        bool HasSoftLighting,
        float LightingEffect1)
        : this(FilePath, ShapeKey, ShapeName, Shader, TexturePaths, HasSoftLighting, false, false, LightingEffect1, 0.0f)
    {
    }
}

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
    float? TargetValue1,
    float? TargetValue2,
    string Decision,
    IReadOnlyList<string> Reasons)
{
    public float? TargetValue => TargetValue1;

    public ShapeScanResult(
        NifShapeProbe Probe,
        ShapeKind Kind,
        bool IsPatchCandidate,
        float? TargetValue,
        string Decision,
        IReadOnlyList<string> Reasons)
        : this(Probe, Kind, IsPatchCandidate, TargetValue, null, Decision, Reasons)
    {
    }
}

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
    int PatchableOtherShapes,
    int PatchableShapes,
    int ErrorFiles,
    int SkippedShapes,
    string? ScanErrorLogPath = null)
{
    public static ScanReport Create(ScanRequest request, IReadOnlyList<FileScanResult> files)
    {
        var patchableEyeShapes = 0;
        var patchableBodyShapes = 0;
        var patchableOtherShapes = 0;
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
                        case ShapeKind.Other:
                            patchableOtherShapes++;
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
            patchableOtherShapes,
            patchableShapes,
            errorFiles,
            skippedShapes,
            null);
    }

    public ReadOnlyCollection<FileScanResult> FileCollection => Files.ToList().AsReadOnly();

    public IReadOnlyList<FileScanResult> PreviewFiles =>
        Files.Where(static file => file.PatchCandidateCount > 0).ToArray();
}
