namespace SkyrimLightingPatcher.Core.Models;

public static class PatchExecutionStages
{
    public const string PreparingOutput = "preparing output folder";
    public const string WritingPatchedFiles = "writing patched mesh files";
    public const string WritingOutputManifest = "writing patch manifest";
    public const string CreatingArchive = "creating output archive";
    public const string WritingRunManifest = "recording patch run metadata";
}
