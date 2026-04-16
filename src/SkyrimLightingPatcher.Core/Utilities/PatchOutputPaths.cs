namespace SkyrimLightingPatcher.Core.Utilities;

public static class PatchOutputPaths
{
    public const string OutputModName = "Soft Light Mesh Patcher Output";
    public const string OutputManifestFileName = "softlight-patch-manifest.json";
    private const string GeneratedModsFolderName = "GeneratedMods";
    private const string VortexMarkerFileName = "__vortex_staging_folder";

    public static string GetApplicationHomeDirectory()
    {
        return Environment.GetEnvironmentVariable("SKYRIM_LIGHTING_PATCHER_HOME")
               ?? Path.Combine(
                   Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                   "SkyrimLightingPatcher");
    }

    public static string GetOutputRootPath(string rootPath)
    {
        return Path.Combine(GetApplicationHomeDirectory(), GeneratedModsFolderName, OutputModName);
    }

    public static string GetManifestCopyPath(string outputRootPath)
    {
        return Path.Combine(outputRootPath, OutputManifestFileName);
    }

    public static string GetOutputRelativePath(string rootPath, string filePath)
    {
        var relativePath = PathUtility.NormalizeSlashes(PathUtility.GetRelativeOrFileName(rootPath, filePath))
            .TrimStart('\\');
        var segments = relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

        if (IsVortexStagingFolder(rootPath) && segments.Length > 1)
        {
            return Path.Combine(segments.Skip(1).ToArray());
        }

        var meshesIndex = Array.FindIndex(segments, static segment => string.Equals(segment, "meshes", StringComparison.OrdinalIgnoreCase));
        if (meshesIndex >= 0)
        {
            return Path.Combine(segments.Skip(meshesIndex).ToArray());
        }

        var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(rootPath));
        if (string.Equals(rootName, "meshes", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine("meshes", relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        }

        return relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
    }

    public static string? TryGetSourceModName(string rootPath, string filePath)
    {
        if (!IsVortexStagingFolder(rootPath))
        {
            return null;
        }

        var relativePath = PathUtility.NormalizeSlashes(PathUtility.GetRelativeOrFileName(rootPath, filePath));
        var segments = relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 1 ? segments[0] : null;
    }

    public static bool IsManagedOutputRoot(string rootPath, string outputRootPath)
    {
        var actualOutputRoot = Path.GetFullPath(outputRootPath);
        var expectedFromScanRoot = Path.GetFullPath(GetOutputRootPath(rootPath));
        if (string.Equals(expectedFromScanRoot, actualOutputRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var expectedAppHomeOutput = Path.GetFullPath(Path.Combine(GetApplicationHomeDirectory(), GeneratedModsFolderName, OutputModName));
        return string.Equals(expectedAppHomeOutput, actualOutputRoot, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsFileInsideManagedOutputRoot(string scanRootPath, string filePath)
    {
        var outputRootPath = GetOutputRootPath(scanRootPath);
        return PathUtility.IsUnderRoot(outputRootPath, filePath);
    }

    public static bool IsVortexStagingFolder(string rootPath)
    {
        return Directory.Exists(rootPath) &&
               File.Exists(Path.Combine(rootPath, VortexMarkerFileName));
    }
}
