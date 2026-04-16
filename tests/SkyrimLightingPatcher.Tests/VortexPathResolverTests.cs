using SkyrimLightingPatcher.Core.Services;

namespace SkyrimLightingPatcher.Tests;

public sealed class VortexPathResolverTests
{
    [Fact]
    public async Task TryResolveSkyrimSeAsync_ReturnsSkyrimSeModsFolderWhenMarkerExists()
    {
        var vortexRoot = CreateTempDirectory();
        var stagingFolder = Path.Combine(vortexRoot, "skyrimse", "mods");
        Directory.CreateDirectory(stagingFolder);
        await File.WriteAllTextAsync(Path.Combine(stagingFolder, "__vortex_staging_folder"), "marker");

        var resolver = new VortexPathResolver(vortexRoot);

        var result = await resolver.TryResolveSkyrimSeAsync();

        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(stagingFolder), result!.RootPath);
    }

    [Fact]
    public async Task TryResolveSkyrimSeAsync_PrefersSkyrimSeOverOtherGames()
    {
        var vortexRoot = CreateTempDirectory();
        var falloutStaging = Path.Combine(vortexRoot, "fallout4", "mods");
        var skyrimSeStaging = Path.Combine(vortexRoot, "skyrimse", "mods");

        Directory.CreateDirectory(falloutStaging);
        Directory.CreateDirectory(skyrimSeStaging);
        await File.WriteAllTextAsync(Path.Combine(falloutStaging, "__vortex_staging_folder"), "marker");
        await File.WriteAllTextAsync(Path.Combine(skyrimSeStaging, "__vortex_staging_folder"), "marker");

        var resolver = new VortexPathResolver(vortexRoot);

        var result = await resolver.TryResolveSkyrimSeAsync();

        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(skyrimSeStaging), result!.RootPath);
    }

    [Fact]
    public async Task TryResolveSkyrimSeAsync_ReturnsNullWithoutMarkerFile()
    {
        var vortexRoot = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(vortexRoot, "skyrimse", "mods"));
        var resolver = new VortexPathResolver(vortexRoot);

        var result = await resolver.TryResolveSkyrimSeAsync();

        Assert.Null(result);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "skyrim-lighting-vortex-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
