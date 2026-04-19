using SkyrimLightingPatcher.Core.Interfaces;
using SkyrimLightingPatcher.Core.Models;
using Microsoft.Win32;

namespace SkyrimLightingPatcher.Core.Services;

public sealed class ModOrganizer2PathResolver : IModOrganizer2PathResolver
{
    private readonly string globalInstancesRoot;
    private readonly string[] portableInstanceCandidates;
    private readonly bool includeSystemPortableDiscovery;
    private readonly Func<IEnumerable<string>> systemPortableCandidatesProvider;

    public ModOrganizer2PathResolver(
        string? globalInstancesRoot = null,
        string[]? portableInstanceCandidates = null,
        bool includeSystemPortableDiscovery = true,
        Func<IEnumerable<string>>? systemPortableCandidatesProvider = null)
    {
        this.globalInstancesRoot = string.IsNullOrWhiteSpace(globalInstancesRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModOrganizer")
            : Path.GetFullPath(globalInstancesRoot);
        this.includeSystemPortableDiscovery = includeSystemPortableDiscovery;
        this.systemPortableCandidatesProvider = systemPortableCandidatesProvider ?? EnumerateWindowsPortableCandidates;

        this.portableInstanceCandidates = (portableInstanceCandidates ?? Array.Empty<string>())
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (this.portableInstanceCandidates.Length == 0)
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            this.portableInstanceCandidates = new[]
            {
                Path.Combine("C:", "Modding", "MO2"),
                Path.Combine(programFiles, "Mod Organizer 2"),
                Path.Combine(programFilesX86, "Mod Organizer 2"),
                Path.Combine(localPrograms, "Mod Organizer 2"),
                Path.Combine(userProfile, "Modding", "MO2"),
                Path.Combine(userProfile, "Downloads", "Mod Organizer 2"),
            }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        }
    }

    public Task<ModOrganizer2Instance?> TryResolveSkyrimSeAsync(
        ModOrganizer2InstanceKind? preferredKind = null,
        CancellationToken cancellationToken = default)
    {
        if (preferredKind is null or ModOrganizer2InstanceKind.Global)
        {
            foreach (var instancePath in EnumerateGlobalInstanceRoots())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ModOrganizer2Support.TryLoadFromRootPath(instancePath, out var paths) || paths is null)
                {
                    continue;
                }

                if (!ModOrganizer2Support.IsSkyrimSpecialEdition(paths))
                {
                    continue;
                }

                return Task.FromResult<ModOrganizer2Instance?>(new ModOrganizer2Instance(
                    paths.InstancePath,
                    paths.ModsPath,
                    paths.ProfilesPath,
                    paths.SelectedProfileName,
                    "Detected Mod Organizer 2 Skyrim SE global instance.",
                    ModOrganizer2InstanceKind.Global));
            }
        }

        if (preferredKind is null or ModOrganizer2InstanceKind.Portable)
        {
            foreach (var instancePath in portableInstanceCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ModOrganizer2Support.TryLoadFromRootPath(instancePath, out var paths) || paths is null)
                {
                    continue;
                }

                if (!ModOrganizer2Support.IsSkyrimSpecialEdition(paths))
                {
                    continue;
                }

                return Task.FromResult<ModOrganizer2Instance?>(new ModOrganizer2Instance(
                    paths.InstancePath,
                    paths.ModsPath,
                    paths.ProfilesPath,
                    paths.SelectedProfileName,
                    "Detected Mod Organizer 2 Skyrim SE portable instance.",
                    ModOrganizer2InstanceKind.Portable));
            }

            if (!includeSystemPortableDiscovery)
            {
                return Task.FromResult<ModOrganizer2Instance?>(null);
            }

            foreach (var instancePath in this.systemPortableCandidatesProvider())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ModOrganizer2Support.TryLoadFromRootPath(instancePath, out var paths) || paths is null)
                {
                    continue;
                }

                if (!ModOrganizer2Support.IsSkyrimSpecialEdition(paths))
                {
                    continue;
                }

                return Task.FromResult<ModOrganizer2Instance?>(new ModOrganizer2Instance(
                    paths.InstancePath,
                    paths.ModsPath,
                    paths.ProfilesPath,
                    paths.SelectedProfileName,
                    "Detected Mod Organizer 2 Skyrim SE portable instance.",
                    ModOrganizer2InstanceKind.Portable));
            }
        }

        return Task.FromResult<ModOrganizer2Instance?>(null);
    }

    private IEnumerable<string> EnumerateGlobalInstanceRoots()
    {
        if (!Directory.Exists(globalInstancesRoot))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateDirectories(globalInstancesRoot)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IEnumerable<string> EnumerateWindowsPortableCandidates()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<string>();
        }

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumerateUninstallInstallLocations())
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                candidates.Add(path);
            }
        }

        foreach (var path in EnumerateAppPathCandidates())
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                candidates.Add(path);
            }
        }

        foreach (var process in System.Diagnostics.Process.GetProcessesByName("ModOrganizer"))
        {
            try
            {
                var location = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(location))
                {
                    continue;
                }

                var directory = Path.GetDirectoryName(location);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    candidates.Add(directory);
                }
            }
            catch
            {
                // Ignore process access failures.
            }
            finally
            {
                process.Dispose();
            }
        }

        return candidates
            .Where(Directory.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> EnumerateAppPathCandidates()
    {
        const string appPathSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\ModOrganizer.exe";
        var roots = new[] { Registry.CurrentUser, Registry.LocalMachine };
        foreach (var root in roots)
        {
            using var key = root.OpenSubKey(appPathSubKey, writable: false);
            if (key is null)
            {
                continue;
            }

            var defaultValue = key.GetValue(null) as string;
            if (!string.IsNullOrWhiteSpace(defaultValue))
            {
                var normalized = Unquote(defaultValue);
                var directory = Path.GetDirectoryName(normalized);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    yield return directory;
                }
            }

            var explicitPath = key.GetValue("Path") as string;
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                yield return Unquote(explicitPath);
            }
        }
    }

    private static IEnumerable<string> EnumerateUninstallInstallLocations()
    {
        const string uninstallSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        var hives = new[]
        {
            Registry.CurrentUser,
            Registry.LocalMachine,
        };

        foreach (var hive in hives)
        {
            foreach (var location in EnumerateUninstallInstallLocationsFromHive(hive, uninstallSubKey))
            {
                yield return location;
            }

            foreach (var location in EnumerateUninstallInstallLocationsFromHive(hive, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"))
            {
                yield return location;
            }
        }
    }

    private static IEnumerable<string> EnumerateUninstallInstallLocationsFromHive(RegistryKey hive, string subKeyPath)
    {
        using var uninstallRoot = hive.OpenSubKey(subKeyPath, writable: false);
        if (uninstallRoot is null)
        {
            yield break;
        }

        foreach (var childName in uninstallRoot.GetSubKeyNames())
        {
            using var child = uninstallRoot.OpenSubKey(childName, writable: false);
            if (child is null)
            {
                continue;
            }

            var displayName = child.GetValue("DisplayName") as string;
            if (string.IsNullOrWhiteSpace(displayName) ||
                !displayName.Contains("Mod Organizer", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var installLocation = child.GetValue("InstallLocation") as string;
            if (!string.IsNullOrWhiteSpace(installLocation))
            {
                yield return Unquote(installLocation);
            }

            var uninstallString = child.GetValue("UninstallString") as string;
            if (!string.IsNullOrWhiteSpace(uninstallString))
            {
                var uninstallExePath = ExtractExecutablePath(uninstallString);
                if (!string.IsNullOrWhiteSpace(uninstallExePath))
                {
                    var directory = Path.GetDirectoryName(uninstallExePath);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        yield return directory;
                    }
                }
            }
        }
    }

    private static string ExtractExecutablePath(string commandLine)
    {
        var trimmed = commandLine.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote > 1)
            {
                return Unquote(trimmed[..(closingQuote + 1)]);
            }
        }

        var firstSpace = trimmed.IndexOf(' ');
        var token = firstSpace > 0 ? trimmed[..firstSpace] : trimmed;
        return Unquote(token);
    }

    private static string Unquote(string value)
    {
        return value.Trim().Trim('"');
    }
}
