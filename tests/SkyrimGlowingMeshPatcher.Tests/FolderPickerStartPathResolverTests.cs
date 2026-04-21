using SkyrimGlowingMeshPatcher.App.Utilities;

namespace SkyrimGlowingMeshPatcher.Tests;

public sealed class FolderPickerStartPathResolverTests
{
    [Fact]
    public void ResolveExistingFolderPath_ReturnsNull_WhenInputMissing()
    {
        Assert.Null(FolderPickerStartPathResolver.ResolveExistingFolderPath(null));
        Assert.Null(FolderPickerStartPathResolver.ResolveExistingFolderPath(string.Empty));
        Assert.Null(FolderPickerStartPathResolver.ResolveExistingFolderPath("   "));
    }

    [Fact]
    public void ResolveExistingFolderPath_ReturnsExactDirectory_WhenDirectoryExists()
    {
        var directory = CreateTempDirectory();

        var resolved = FolderPickerStartPathResolver.ResolveExistingFolderPath(directory);

        Assert.Equal(directory, resolved);
    }

    [Fact]
    public async Task ResolveExistingFolderPath_ReturnsParentDirectory_WhenInputIsFile()
    {
        var directory = CreateTempDirectory();
        var filePath = Path.Combine(directory, "settings.json");
        await File.WriteAllTextAsync(filePath, "{}");

        var resolved = FolderPickerStartPathResolver.ResolveExistingFolderPath(filePath);

        Assert.Equal(directory, resolved);
    }

    [Fact]
    public void ResolveExistingFolderPath_WalksUpUntilExistingParent()
    {
        var directory = CreateTempDirectory();
        var nestedMissingPath = Path.Combine(directory, "missing", "deeper", "path");

        var resolved = FolderPickerStartPathResolver.ResolveExistingFolderPath(nestedMissingPath);

        Assert.Equal(directory, resolved);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "skyrim-lighting-folder-picker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
