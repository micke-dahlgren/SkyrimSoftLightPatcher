namespace SkyrimGlowingMeshPatcher.Core.Utilities;

internal static class PathUtility
{
    public static string NormalizeForComparison(string path)
    {
        return path
            .Replace('/', '\\')
            .Trim()
            .TrimEnd('\\')
            .ToUpperInvariant();
    }

    public static string NormalizeSlashes(string value)
    {
        return value.Replace('/', '\\');
    }

    public static bool IsUnderRoot(string rootPath, string filePath)
    {
        var normalizedRoot = EnsureTrailingSlash(Path.GetFullPath(rootPath));
        var normalizedFile = Path.GetFullPath(filePath);
        return normalizedFile.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    public static string GetRelativeOrFileName(string rootPath, string filePath)
    {
        if (!IsUnderRoot(rootPath, filePath))
        {
            return Path.GetFileName(filePath);
        }

        return Path.GetRelativePath(rootPath, filePath);
    }

    public static string EnsureTrailingSlash(string value)
    {
        return value.EndsWith(Path.DirectorySeparatorChar) || value.EndsWith(Path.AltDirectorySeparatorChar)
            ? value
            : value + Path.DirectorySeparatorChar;
    }
}
