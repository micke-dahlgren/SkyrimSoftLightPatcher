namespace SkyrimLightingPatcher.App.Utilities;

public static class FolderPickerStartPathResolver
{
    public static string? ResolveExistingFolderPath(string? assignedPath)
    {
        if (string.IsNullOrWhiteSpace(assignedPath))
        {
            return null;
        }

        var currentPath = assignedPath;
        if (File.Exists(currentPath))
        {
            currentPath = Path.GetDirectoryName(currentPath) ?? string.Empty;
        }

        while (!string.IsNullOrWhiteSpace(currentPath))
        {
            if (Directory.Exists(currentPath))
            {
                return currentPath;
            }

            var parent = Path.GetDirectoryName(currentPath);
            if (string.Equals(parent, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            currentPath = parent ?? string.Empty;
        }

        return null;
    }
}
