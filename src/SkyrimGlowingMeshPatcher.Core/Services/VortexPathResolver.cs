using SkyrimGlowingMeshPatcher.Core.Interfaces;
using SkyrimGlowingMeshPatcher.Core.Models;
using Microsoft.Win32;

namespace SkyrimGlowingMeshPatcher.Core.Services;

public sealed class VortexPathResolver : IVortexPathResolver
{
    private const string StagingMarkerFileName = "__vortex_staging_folder";
    private readonly string[] vortexRootDirectories;

    public VortexPathResolver(params string[]? vortexRootDirectories)
    {
        this.vortexRootDirectories = (vortexRootDirectories ?? [])
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (this.vortexRootDirectories.Length == 0)
        {
            this.vortexRootDirectories =
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vortex"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vortex"),
            ];
        }
    }

    public Task<VortexStagingFolder?> TryResolveSkyrimSeAsync(CancellationToken cancellationToken = default)
    {
        foreach (var vortexRoot in vortexRootDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var resolved = TryResolveFromRoot(vortexRoot, cancellationToken);
            if (resolved is not null)
            {
                return Task.FromResult<VortexStagingFolder?>(resolved);
            }
        }

        return Task.FromResult<VortexStagingFolder?>(null);
    }

    public Task<string?> TryResolveSkyrimDataPathAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            var registryPaths = new[]
            {
                @"SOFTWARE\WOW6432Node\Bethesda Softworks\Skyrim Special Edition",
                @"SOFTWARE\Bethesda Softworks\Skyrim Special Edition",
            };

            foreach (var keyPath in registryPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var installPath = Registry.GetValue($@"HKEY_LOCAL_MACHINE\{keyPath}", "Installed Path", null) as string;
                if (!string.IsNullOrWhiteSpace(installPath))
                {
                    var dataPath = Path.Combine(installPath, "Data");
                    if (Directory.Exists(dataPath))
                    {
                        return Task.FromResult<string?>(dataPath);
                    }
                }
            }
        }

        var commonCandidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "Skyrim Special Edition", "Data"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steamapps", "common", "Skyrim Special Edition", "Data"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "SteamLibrary", "steamapps", "common", "Skyrim Special Edition", "Data"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SteamLibrary", "steamapps", "common", "Skyrim Special Edition", "Data"),
        };

        var detected = commonCandidates.FirstOrDefault(Directory.Exists);
        return Task.FromResult<string?>(detected);
    }

    private static VortexStagingFolder? TryResolveFromRoot(string vortexRoot, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(vortexRoot))
        {
            return null;
        }

        foreach (var directCandidate in EnumerateDirectCandidates(vortexRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsVortexStagingFolder(directCandidate))
            {
                return new VortexStagingFolder(directCandidate, "Detected Vortex Skyrim SE staging folder.");
            }
        }

        var fallbackCandidate = Directory.EnumerateDirectories(vortexRoot)
            .Select(static gameDirectory => Path.Combine(gameDirectory, "mods"))
            .Where(IsVortexStagingFolder)
            .OrderByDescending(GetSkyrimSeScore)
            .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return fallbackCandidate is null
            ? null
            : new VortexStagingFolder(fallbackCandidate, "Detected Vortex staging folder from marker file.");
    }

    private static IEnumerable<string> EnumerateDirectCandidates(string vortexRoot)
    {
        yield return Path.Combine(vortexRoot, "skyrimse", "mods");
        yield return Path.Combine(vortexRoot, "SkyrimSE", "mods");
        yield return Path.Combine(vortexRoot, "Skyrim Special Edition", "mods");
        yield return Path.Combine(vortexRoot, "skyrimspecialedition", "mods");
    }

    private static bool IsVortexStagingFolder(string path)
    {
        return Directory.Exists(path) &&
               File.Exists(Path.Combine(path, StagingMarkerFileName));
    }

    private static int GetSkyrimSeScore(string path)
    {
        var normalized = path.Replace('/', '\\');
        var gameSegment = Path.GetFileName(Path.GetDirectoryName(normalized) ?? string.Empty);

        if (string.Equals(gameSegment, "skyrimse", StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        if (string.Equals(gameSegment, "skyrimspecialedition", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (gameSegment.Contains("skyrim", StringComparison.OrdinalIgnoreCase) &&
            gameSegment.Contains("special", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (gameSegment.Contains("skyrim", StringComparison.OrdinalIgnoreCase) &&
            gameSegment.Contains("se", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 0;
    }
}
