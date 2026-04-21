using System.Security.Cryptography;
using System.Text;

namespace SkyrimLightingPatcher.Core.Utilities;

public static class PatchOutputPaths
{
    public const string ApplicationFolderName = "SkyrimGlowingMeshPatcher";
    public const string OutputModName = "Glowing Mesh Patcher Output";
    public const string OutputManifestFileName = "softlight-patch-manifest.json";
    public const string PatchErrorLogFileName = "error.log.txt";
    public const string OutputArchivePrefix = "GlowingMeshPatch";
    public const int OutputArchiveHashLength = 6;
    public const string BackupsFolderName = "Backups";
    public const string BackupManifestFileName = "manifest.json";
    private const string GeneratedModsFolderName = "GeneratedMods";
    private const string VortexMarkerFileName = "__vortex_staging_folder";
    private const string LegacyApplicationFolderName = "SkyrimLightingPatcher";

    public static string GetApplicationHomeDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable("SKYRIM_LIGHTING_PATCHER_HOME");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        var localAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return ResolveDefaultApplicationHomeDirectory(localAppDataPath);
    }

    private static string ResolveDefaultApplicationHomeDirectory(string localAppDataPath)
    {
        var appHomePath = Path.Combine(localAppDataPath, ApplicationFolderName);
        if (Directory.Exists(appHomePath))
        {
            return appHomePath;
        }

        var legacyAppHomePath = Path.Combine(localAppDataPath, LegacyApplicationFolderName);
        if (!Directory.Exists(legacyAppHomePath))
        {
            return appHomePath;
        }

        try
        {
            Directory.Move(legacyAppHomePath, appHomePath);
            return appHomePath;
        }
        catch
        {
            // Fallback keeps compatibility when migration is blocked by locks/permissions.
            return legacyAppHomePath;
        }
    }

    public static string GetOutputRootPath(string rootPath)
    {
        return Path.Combine(GetApplicationHomeDirectory(), GeneratedModsFolderName, OutputModName);
    }

    public static string GetManifestCopyPath(string outputRootPath)
    {
        return Path.Combine(outputRootPath, OutputManifestFileName);
    }

    public static string GetPatchErrorLogPath(string outputRootPath)
    {
        return Path.Combine(outputRootPath, PatchErrorLogFileName);
    }

    public static string GetArchiveErrorLogPath(string outputArchivePath)
    {
        var archiveDirectory = Path.GetDirectoryName(Path.GetFullPath(outputArchivePath))
                               ?? GetApplicationHomeDirectory();
        return Path.Combine(archiveDirectory, PatchErrorLogFileName);
    }

    public static string GetBackupManifestPath(string runId)
    {
        return Path.Combine(GetApplicationHomeDirectory(), BackupsFolderName, runId, BackupManifestFileName);
    }

    public static string CreateStampedArchiveFileName(string seed)
    {
        if (string.IsNullOrWhiteSpace(seed))
        {
            throw new ArgumentException("A non-empty seed is required to generate an archive name.", nameof(seed));
        }

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var hashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();
        var suffix = hashHex[..OutputArchiveHashLength];
        return $"{OutputArchivePrefix}_{suffix}.zip";
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

    public static bool IsManagedOutputRoot(string rootPath, string outputRootPath, string? outputArchivePath = null)
    {
        var actualOutputRoot = Path.GetFullPath(outputRootPath);
        var expectedFromScanRoot = Path.GetFullPath(GetOutputRootPath(rootPath));
        if (string.Equals(expectedFromScanRoot, actualOutputRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var expectedAppHomeOutput = Path.GetFullPath(Path.Combine(GetApplicationHomeDirectory(), GeneratedModsFolderName, OutputModName));
        if (string.Equals(expectedAppHomeOutput, actualOutputRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var archiveDerivedOutputRoot = TryGetManagedOutputRootFromArchive(outputArchivePath);
        return archiveDerivedOutputRoot is not null &&
               string.Equals(archiveDerivedOutputRoot, actualOutputRoot, StringComparison.OrdinalIgnoreCase);
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

    private static string? TryGetManagedOutputRootFromArchive(string? outputArchivePath)
    {
        if (string.IsNullOrWhiteSpace(outputArchivePath))
        {
            return null;
        }

        try
        {
            var archiveDirectory = Path.GetDirectoryName(Path.GetFullPath(outputArchivePath));
            return string.IsNullOrWhiteSpace(archiveDirectory)
                ? null
                : Path.GetFullPath(Path.Combine(archiveDirectory, OutputModName));
        }
        catch
        {
            return null;
        }
    }
}
