using SkyrimGlowingMeshPatcher.Core.Services;

namespace SkyrimGlowingMeshPatcher.Tests;

public sealed class ModOrganizer2PathResolverTests
{
    [Fact]
    public async Task TryResolveSkyrimSeAsync_ReturnsMatchingGlobalInstance()
    {
        var globalRoot = CreateTempDirectory();
        CreateMo2Instance(globalRoot, "Cyberpunk 2077", "Cyberpunk 2077", "Default");
        var skyrimInstance = CreateMo2Instance(globalRoot, "Skyrim Special Edition", "Skyrim Special Edition", "TestProfile");

        var resolver = new ModOrganizer2PathResolver(
            globalRoot,
            new[] { @"Z:\__mo2-not-present" },
            includeSystemPortableDiscovery: false);

        var result = await resolver.TryResolveSkyrimSeAsync();

        Assert.NotNull(result);
        Assert.Equal(skyrimInstance, result!.InstancePath);
        Assert.Equal(Path.Combine(skyrimInstance, "mods"), result.ModsPath);
        Assert.Equal(Path.Combine(skyrimInstance, "profiles"), result.ProfilesPath);
        Assert.Equal("TestProfile", result.SelectedProfileName);
        Assert.Equal(SkyrimGlowingMeshPatcher.Core.Models.ModOrganizer2InstanceKind.Global, result.InstanceKind);
    }

    [Fact]
    public async Task TryResolveSkyrimSeAsync_ReturnsNullWithoutSkyrimInstance()
    {
        var globalRoot = CreateTempDirectory();
        CreateMo2Instance(globalRoot, "Cyberpunk 2077", "Cyberpunk 2077", "Default");

        var resolver = new ModOrganizer2PathResolver(
            globalRoot,
            new[] { @"Z:\__mo2-not-present" },
            includeSystemPortableDiscovery: false);

        var result = await resolver.TryResolveSkyrimSeAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task TryResolveSkyrimSeAsync_ResolvesPortableCandidateFromProfilePath()
    {
        var globalRoot = CreateTempDirectory();
        var portableRoot = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(portableRoot, "mods"));
        Directory.CreateDirectory(Path.Combine(portableRoot, "profiles", "portableTestProfile"));
        File.WriteAllText(
            Path.Combine(portableRoot, "ModOrganizer.ini"),
            """
            [General]
            gameName=Skyrim Special Edition
            selected_profile=@ByteArray(portableTestProfile)
            """);

        var profilePathCandidate = Path.Combine(portableRoot, "profiles", "portableTestProfile");
        var resolver = new ModOrganizer2PathResolver(
            globalRoot,
            new[] { profilePathCandidate },
            includeSystemPortableDiscovery: false);

        var result = await resolver.TryResolveSkyrimSeAsync();

        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(portableRoot), result!.InstancePath);
        Assert.Equal("portableTestProfile", result.SelectedProfileName);
        Assert.Equal(SkyrimGlowingMeshPatcher.Core.Models.ModOrganizer2InstanceKind.Portable, result.InstanceKind);
    }

    [Fact]
    public async Task TryResolveSkyrimSeAsync_PrefersPortableWhenRequested()
    {
        var globalRoot = CreateTempDirectory();
        CreateMo2Instance(globalRoot, "Skyrim Special Edition", "Skyrim Special Edition", "GlobalProfile");

        var portableRoot = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(portableRoot, "mods"));
        Directory.CreateDirectory(Path.Combine(portableRoot, "profiles", "portableTestProfile"));
        File.WriteAllText(
            Path.Combine(portableRoot, "ModOrganizer.ini"),
            """
            [General]
            gameName=Skyrim Special Edition
            selected_profile=@ByteArray(portableTestProfile)
            """);

        var resolver = new ModOrganizer2PathResolver(
            globalRoot,
            new[] { portableRoot },
            includeSystemPortableDiscovery: false);

        var result = await resolver.TryResolveSkyrimSeAsync(SkyrimGlowingMeshPatcher.Core.Models.ModOrganizer2InstanceKind.Portable);

        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(portableRoot), result!.InstancePath);
        Assert.Equal(SkyrimGlowingMeshPatcher.Core.Models.ModOrganizer2InstanceKind.Portable, result.InstanceKind);
    }

    [Fact]
    public async Task TryResolveSkyrimSeAsync_UsesSystemPortableDiscoveryProvider_WhenEnabled()
    {
        var globalRoot = CreateTempDirectory();
        var portableRoot = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(portableRoot, "mods"));
        Directory.CreateDirectory(Path.Combine(portableRoot, "profiles", "portableTestProfile"));
        File.WriteAllText(
            Path.Combine(portableRoot, "ModOrganizer.ini"),
            """
            [General]
            gameName=Skyrim Special Edition
            selected_profile=@ByteArray(portableTestProfile)
            """);

        var resolver = new ModOrganizer2PathResolver(
            globalRoot,
            new[] { @"Z:\__mo2-not-present" },
            includeSystemPortableDiscovery: true,
            systemPortableCandidatesProvider: () => new[] { portableRoot });

        var result = await resolver.TryResolveSkyrimSeAsync(SkyrimGlowingMeshPatcher.Core.Models.ModOrganizer2InstanceKind.Portable);

        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(portableRoot), result!.InstancePath);
        Assert.Equal(SkyrimGlowingMeshPatcher.Core.Models.ModOrganizer2InstanceKind.Portable, result.InstanceKind);
    }

    [Fact]
    public async Task TryResolveSkyrimSeAsync_DoesNotUsePortableDiscovery_WhenGlobalRequested()
    {
        var globalRoot = CreateTempDirectory();
        var portableRoot = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(portableRoot, "mods"));
        Directory.CreateDirectory(Path.Combine(portableRoot, "profiles", "portableTestProfile"));
        File.WriteAllText(
            Path.Combine(portableRoot, "ModOrganizer.ini"),
            """
            [General]
            gameName=Skyrim Special Edition
            selected_profile=@ByteArray(portableTestProfile)
            """);

        var resolver = new ModOrganizer2PathResolver(
            globalRoot,
            new[] { @"Z:\__mo2-not-present" },
            includeSystemPortableDiscovery: true,
            systemPortableCandidatesProvider: () => new[] { portableRoot });

        var result = await resolver.TryResolveSkyrimSeAsync(SkyrimGlowingMeshPatcher.Core.Models.ModOrganizer2InstanceKind.Global);

        Assert.Null(result);
    }

    private static string CreateMo2Instance(string globalRoot, string instanceName, string gameName, string profileName)
    {
        var instanceRoot = Path.Combine(globalRoot, instanceName);
        Directory.CreateDirectory(Path.Combine(instanceRoot, "mods"));
        Directory.CreateDirectory(Path.Combine(instanceRoot, "profiles", profileName));
        File.WriteAllText(
            Path.Combine(instanceRoot, "ModOrganizer.ini"),
            $"""
            [General]
            gameName={gameName}
            selected_profile=@ByteArray({profileName})
            """);
        return instanceRoot;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "skyrim-lighting-mo2-resolver-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
