namespace SkyrimLightingPatcher.Core.Models;

public sealed class PatchArchiveCreationException : Exception
{
    public PatchArchiveCreationException(
        string outputRootPath,
        string outputArchivePath,
        Exception innerException)
        : base(
            $"Failed to create output archive '{outputArchivePath}'. Loose files remain at '{outputRootPath}'.",
            innerException)
    {
        OutputRootPath = outputRootPath;
        OutputArchivePath = outputArchivePath;
    }

    public string OutputRootPath { get; }

    public string OutputArchivePath { get; }
}
