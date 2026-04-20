using System.Security.Cryptography;
using System.Text;
using SkyrimLightingPatcher.Core.Interfaces;
using SkyrimLightingPatcher.Core.Models;
using SkyrimLightingPatcher.Core.Utilities;

namespace SkyrimLightingPatcher.Core.Services;

/// <summary>
/// Resolves which mesh sources should be scanned and patched.
/// It merges loose files and archive entries and applies conflict winner rules.
/// </summary>
public sealed partial class ScanFileResolver : IScanFileResolver
{
    private const string StagingMarkerFileName = "__vortex_staging_folder";
    private const string DeploymentFileName = "vortex.deployment.msgpack";
    private readonly BsaArchiveReader bsaArchiveReader = new();
    private readonly Dictionary<string, BsaArchiveIndex> archiveIndexCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim archiveCacheLock = new(1, 1);

    /// <summary>
    /// Finds candidate mesh sources under the selected root, then chooses winners by output-relative path.
    /// Loose files are applied after archives so they override archive-backed entries.
    /// </summary>
    public async Task<IReadOnlyList<MeshSource>> ResolveFilePathsAsync(
        string rootPath,
        string? skyrimDataPath = null,
        ModManagerKind modManager = ModManagerKind.Vortex,
        CancellationToken cancellationToken = default)
    {
        ModOrganizer2Paths? modOrganizer2Paths = null;
        if (modManager == ModManagerKind.ModOrganizer2)
        {
            _ = ModOrganizer2Support.TryLoadFromRootPath(rootPath, out modOrganizer2Paths);
        }

        var winners = new Dictionary<string, MeshSource>(StringComparer.OrdinalIgnoreCase);

        foreach (var archiveSource in await ResolveArchiveSourcesAsync(rootPath, skyrimDataPath, modManager, modOrganizer2Paths, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            winners[PathUtility.NormalizeForComparison(archiveSource.OutputRelativePath)] = archiveSource;
        }

        foreach (var looseSource in await ResolveLooseSourcesAsync(rootPath, modManager, modOrganizer2Paths, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            winners[PathUtility.NormalizeForComparison(looseSource.OutputRelativePath)] = looseSource;
        }

        return winners.Values
            .OrderBy(static source => source.DisplayPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Returns a readable local path for a source.
    /// Loose sources return their original path; archive sources are extracted to a cache folder.
    /// </summary>
    public async Task<string> MaterializeSourceAsync(MeshSource source, CancellationToken cancellationToken = default)
    {
        if (source.Kind == MeshSourceKind.Loose)
        {
            return source.LocalPath;
        }

        if (string.IsNullOrWhiteSpace(source.ArchivePath) || string.IsNullOrWhiteSpace(source.ArchiveEntryPath))
        {
            throw new InvalidOperationException("Archive-backed mesh source is missing archive metadata.");
        }

        var cacheRoot = Path.Combine(PatchOutputPaths.GetApplicationHomeDirectory(), "ExtractedSources");
        var sourceHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{source.ArchivePath}|{File.GetLastWriteTimeUtc(source.ArchivePath).Ticks}|{source.ArchiveEntryPath}")));
        var extractedPath = Path.Combine(cacheRoot, sourceHash, source.OutputRelativePath);
        if (File.Exists(extractedPath))
        {
            return extractedPath;
        }

        var archiveIndex = await GetArchiveIndexAsync(source.ArchivePath, cancellationToken).ConfigureAwait(false);
        var entry = archiveIndex.Entries.FirstOrDefault(entry =>
            string.Equals(entry.EntryPath, source.ArchiveEntryPath, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException(
                $"Entry '{source.ArchiveEntryPath}' was not found in archive '{source.ArchivePath}'.",
                source.ArchivePath);

        var bytes = bsaArchiveReader.Extract(entry);
        Directory.CreateDirectory(Path.GetDirectoryName(extractedPath)!);
        await File.WriteAllBytesAsync(extractedPath, bytes, cancellationToken).ConfigureAwait(false);
        return extractedPath;
    }

    private async Task<IReadOnlyList<MeshSource>> ResolveLooseSourcesAsync(
        string rootPath,
        ModManagerKind modManager,
        ModOrganizer2Paths? modOrganizer2Paths,
        CancellationToken cancellationToken)
    {
        if (modManager == ModManagerKind.ModOrganizer2 && modOrganizer2Paths is not null)
        {
            return await ResolveMo2LooseSourcesAsync(rootPath, modOrganizer2Paths, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<string> filePaths;
        if (IsVortexStagingFolder(rootPath))
        {
            var deployedFiles = await TryResolveFromVortexDeploymentAsync(rootPath, cancellationToken).ConfigureAwait(false);
            filePaths = deployedFiles ?? ResolveLooseFilesByEnumeration(rootPath);
        }
        else
        {
            filePaths = ResolveLooseFilesByEnumeration(rootPath);
        }

        return filePaths
            .Select(path => CreateLooseSource(rootPath, path))
            .OrderBy(static source => source.DisplayPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private Task<IReadOnlyList<MeshSource>> ResolveMo2LooseSourcesAsync(
        string rootPath,
        ModOrganizer2Paths modOrganizer2Paths,
        CancellationToken cancellationToken)
    {
        var enabledMods = ModOrganizer2Support.ReadEnabledManagedMods(modOrganizer2Paths);
        if (enabledMods.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<MeshSource>>([]);
        }

        var sources = new List<MeshSource>();
        foreach (var modName in enabledMods)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var modRoot = Path.Combine(modOrganizer2Paths.ModsPath, modName);
            if (!Directory.Exists(modRoot) || !PathUtility.IsUnderRoot(modOrganizer2Paths.ModsPath, modRoot))
            {
                continue;
            }

            foreach (var filePath in Directory.EnumerateFiles(modRoot, "*.nif", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (PatchOutputPaths.IsFileInsideManagedOutputRoot(rootPath, filePath))
                {
                    continue;
                }

                sources.Add(CreateMo2LooseSource(rootPath, modOrganizer2Paths.ModsPath, filePath));
            }
        }

        var resolvedSources = sources
            .OrderBy(static source => source.DisplayPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Task.FromResult<IReadOnlyList<MeshSource>>(resolvedSources);
    }

    private async Task<IReadOnlyList<MeshSource>> ResolveArchiveSourcesAsync(
        string rootPath,
        string? skyrimDataPath,
        ModManagerKind modManager,
        ModOrganizer2Paths? modOrganizer2Paths,
        CancellationToken cancellationToken)
    {
        HashSet<string>? activeVortexSources = null;
        IReadOnlyDictionary<string, int>? vortexSourceOrder = null;
        Dictionary<string, int>? mo2EnabledModOrder = null;
        var archiveCandidates = new List<ArchiveCandidate>();

        if (modManager == ModManagerKind.ModOrganizer2 && modOrganizer2Paths is not null)
        {
            var enabledMods = ModOrganizer2Support.ReadEnabledManagedMods(modOrganizer2Paths);
            mo2EnabledModOrder = enabledMods
                .Select((name, index) => new KeyValuePair<string, int>(name, index))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var modName in enabledMods)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var modRoot = Path.Combine(modOrganizer2Paths.ModsPath, modName);
                if (!Directory.Exists(modRoot) || !PathUtility.IsUnderRoot(modOrganizer2Paths.ModsPath, modRoot))
                {
                    continue;
                }

                archiveCandidates.AddRange(
                    Directory.EnumerateFiles(modRoot, "*.bsa", SearchOption.AllDirectories)
                        .Select(path => new ArchiveCandidate(path, modOrganizer2Paths.ModsPath, ArchiveCandidateOrigin.Mo2ManagedMod)));
            }
        }
        else
        {
            activeVortexSources = await TryResolveActiveVortexSourcesAsync(rootPath, cancellationToken).ConfigureAwait(false);
            vortexSourceOrder = await TryResolveVortexSourceOrderAsync(rootPath, cancellationToken).ConfigureAwait(false);
            archiveCandidates.AddRange(
                Directory.EnumerateFiles(rootPath, "*.bsa", SearchOption.AllDirectories)
                    .Select(path => new ArchiveCandidate(path, rootPath, ArchiveCandidateOrigin.Staging)));

            foreach (var deploymentRoot in await TryResolveDeployedDataRootsAsync(rootPath, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                archiveCandidates.AddRange(
                    Directory.EnumerateFiles(deploymentRoot, "*.bsa", SearchOption.TopDirectoryOnly)
                        .Select(path => new ArchiveCandidate(path, deploymentRoot, ArchiveCandidateOrigin.DeployedData)));
            }

            if (!string.IsNullOrWhiteSpace(skyrimDataPath) && Directory.Exists(skyrimDataPath))
            {
                archiveCandidates.AddRange(
                    Directory.EnumerateFiles(skyrimDataPath, "*.bsa", SearchOption.TopDirectoryOnly)
                        .Where(static path => Path.GetFileName(path).Contains("meshes", StringComparison.OrdinalIgnoreCase))
                        .Select(path => new ArchiveCandidate(path, skyrimDataPath, ArchiveCandidateOrigin.GameData)));
            }
        }

        var archives = archiveCandidates
            .Distinct(ArchiveCandidatePathComparer.Instance)
            .OrderBy(
                candidate => GetArchivePriority(rootPath, candidate, modManager, modOrganizer2Paths, mo2EnabledModOrder, vortexSourceOrder),
                Comparer<(int Group, int PluginIndex, string RelativePath)>.Default)
            .ThenBy(static candidate => candidate.ArchivePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var sources = new List<MeshSource>();
        foreach (var archive in archives)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var archivePath = archive.ArchivePath;
            var archiveDisplayPath = BuildArchiveDisplayPath(rootPath, archive);
            var sourceModName = archive.Origin switch
            {
                ArchiveCandidateOrigin.Staging => GetSourceModNameForVortex(rootPath, archiveDisplayPath),
                ArchiveCandidateOrigin.Mo2ManagedMod => GetSourceModNameFromRelativePath(archiveDisplayPath),
                _ => null,
            };

            if (archive.Origin == ArchiveCandidateOrigin.Staging &&
                activeVortexSources is not null &&
                !string.IsNullOrWhiteSpace(sourceModName) &&
                !activeVortexSources.Contains(sourceModName))
            {
                continue;
            }

            var archiveIndex = await GetArchiveIndexAsync(archivePath, cancellationToken).ConfigureAwait(false);
            if (!archiveIndex.ContainsMeshes)
            {
                continue;
            }

            foreach (var entry in archiveIndex.MeshEntries)
            {
                sources.Add(CreateArchiveSource(archive, archiveDisplayPath, entry.EntryPath, sourceModName));
            }
        }

        return sources;
    }

    private static string BuildArchiveDisplayPath(string rootPath, ArchiveCandidate archive)
    {
        return archive.Origin switch
        {
            ArchiveCandidateOrigin.DeployedData => BuildDeployedArchiveDisplayPath(archive.DisplayRootPath, archive.ArchivePath),
            ArchiveCandidateOrigin.GameData => BuildDeployedArchiveDisplayPath(archive.DisplayRootPath, archive.ArchivePath),
            ArchiveCandidateOrigin.Mo2ManagedMod => PathUtility.GetRelativeOrFileName(archive.DisplayRootPath, archive.ArchivePath),
            _ => PathUtility.GetRelativeOrFileName(rootPath, archive.ArchivePath),
        };
    }

    private static async Task<HashSet<string>?> TryResolveActiveVortexSourcesAsync(string rootPath, CancellationToken cancellationToken)
    {
        var deployment = await TryLoadVortexDeploymentAsync(rootPath, cancellationToken).ConfigureAwait(false);
        if (deployment is null)
        {
            return null;
        }

        return deployment.Files
            .Where(static file => !string.IsNullOrWhiteSpace(file.Source))
            .Select(static file => file.Source!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyDictionary<string, int>?> TryResolveVortexSourceOrderAsync(string rootPath, CancellationToken cancellationToken)
    {
        var deployment = await TryLoadVortexDeploymentAsync(rootPath, cancellationToken).ConfigureAwait(false);
        if (deployment is null)
        {
            return null;
        }

        var sourceEntries = deployment.Files
            .Select((file, index) => new { File = file, Index = index })
            .Where(static item => !string.IsNullOrWhiteSpace(item.File.Source))
            .ToArray();
        if (sourceEntries.Length == 0)
        {
            return null;
        }

        var groupedSources = sourceEntries
            .GroupBy(static item => item.File.Source!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Source = group.Key,
                MaxDeploymentTime = group.Max(static item => item.File.DeploymentTime ?? long.MinValue),
                HasDeploymentTime = group.Any(static item => item.File.DeploymentTime.HasValue),
                LastIndex = group.Max(static item => item.Index),
            })
            .ToArray();

        var useDeploymentTime = groupedSources.Any(static source => source.HasDeploymentTime);
        var orderedSources = useDeploymentTime
            ? groupedSources
                .OrderBy(static source => source.MaxDeploymentTime)
                .ThenBy(static source => source.LastIndex)
                .ThenBy(static source => source.Source, StringComparer.OrdinalIgnoreCase)
            : groupedSources
                .OrderBy(static source => source.LastIndex)
                .ThenBy(static source => source.Source, StringComparer.OrdinalIgnoreCase);

        return orderedSources
            .Select((source, index) => new KeyValuePair<string, int>(source.Source, index))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyList<string>> TryResolveDeployedDataRootsAsync(string rootPath, CancellationToken cancellationToken)
    {
        var deployment = await TryLoadVortexDeploymentAsync(rootPath, cancellationToken).ConfigureAwait(false);
        if (deployment is null)
        {
            return [];
        }

        return deployment.Files
            .Select(static file => TryResolveDeployedDataRoot(file))
            .Where(static root => !string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            .Select(static root => root!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static root => root, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<string> ResolveLooseFilesByEnumeration(string rootPath)
    {
        return Directory.EnumerateFiles(rootPath, "*.nif", SearchOption.AllDirectories)
            .Where(path => !PatchOutputPaths.IsFileInsideManagedOutputRoot(rootPath, path))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<BsaArchiveIndex> GetArchiveIndexAsync(string archivePath, CancellationToken cancellationToken)
    {
        await archiveCacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (archiveIndexCache.TryGetValue(archivePath, out var cached))
            {
                return cached;
            }

            var index = bsaArchiveReader.ReadIndex(archivePath);
            archiveIndexCache[archivePath] = index;
            return index;
        }
        finally
        {
            archiveCacheLock.Release();
        }
    }

    private static MeshSource CreateLooseSource(string rootPath, string filePath)
    {
        var displayPath = PathUtility.GetRelativeOrFileName(rootPath, filePath);
        return new MeshSource(
            $"loose|{Path.GetFullPath(filePath)}",
            displayPath,
            PatchOutputPaths.GetOutputRelativePath(rootPath, filePath),
            filePath,
            MeshSourceKind.Loose,
            GetSourceModNameForVortex(rootPath, displayPath));
    }

    private static MeshSource CreateMo2LooseSource(string rootPath, string modsPath, string filePath)
    {
        var displayPath = PathUtility.GetRelativeOrFileName(modsPath, filePath);
        return new MeshSource(
            $"loose|{Path.GetFullPath(filePath)}",
            displayPath,
            PatchOutputPaths.GetOutputRelativePath(rootPath, filePath),
            filePath,
            MeshSourceKind.Loose,
            GetSourceModNameFromRelativePath(displayPath));
    }

    private static MeshSource CreateArchiveSource(
        ArchiveCandidate archive,
        string archiveDisplayPath,
        string entryPath,
        string? sourceModName)
    {
        var displayPath = $"{archiveDisplayPath} -> {entryPath}";
        return new MeshSource(
            $"archive|{Path.GetFullPath(archive.ArchivePath)}|{entryPath}",
            displayPath,
            NormalizeRelativePath(entryPath),
            string.Empty,
            MeshSourceKind.Archive,
            sourceModName,
            archive.ArchivePath,
            NormalizeRelativePath(entryPath));
    }

    private static string BuildDeployedArchiveDisplayPath(string deploymentRoot, string archivePath)
    {
        var rootLabel = Path.GetFileName(Path.TrimEndingDirectorySeparator(deploymentRoot));
        if (string.IsNullOrWhiteSpace(rootLabel))
        {
            rootLabel = deploymentRoot;
        }

        return Path.Combine(rootLabel, Path.GetFileName(archivePath));
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
    }

    private static string? GetSourceModNameFromRelativePath(string relativePath)
    {
        var normalized = PathUtility.NormalizeSlashes(relativePath);
        var segments = normalized.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 1 ? segments[0] : null;
    }

    private static string? GetSourceModNameForVortex(string rootPath, string relativePath)
    {
        if (!IsVortexStagingFolder(rootPath))
        {
            return null;
        }

        return GetSourceModNameFromRelativePath(relativePath);
    }

    private static bool IsVortexStagingFolder(string rootPath)
    {
        return Directory.Exists(rootPath) &&
               File.Exists(Path.Combine(rootPath, StagingMarkerFileName));
    }

    private static async Task<IReadOnlyList<string>?> TryResolveFromVortexDeploymentAsync(string rootPath, CancellationToken cancellationToken)
    {
        var deployment = await TryLoadVortexDeploymentAsync(rootPath, cancellationToken).ConfigureAwait(false);
        if (deployment is null)
        {
            return null;
        }

        try
        {
            return deployment.Files
                .Where(static file => file.IsMeshNif)
                .Select(file => BuildSourcePath(rootPath, file))
                .Where(static path => path is not null && File.Exists(path))
                .Select(static path => path!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<VortexDeployment?> TryLoadVortexDeploymentAsync(string rootPath, CancellationToken cancellationToken)
    {
        if (!IsVortexStagingFolder(rootPath))
        {
            return null;
        }

        var deploymentPath = Path.Combine(rootPath, DeploymentFileName);
        if (!File.Exists(deploymentPath))
        {
            return null;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(deploymentPath, cancellationToken).ConfigureAwait(false);
            var deployment = VortexDeploymentMessagePackParser.Parse(bytes);
            if (!string.IsNullOrWhiteSpace(deployment.StagingPath) &&
                !string.Equals(
                    Path.GetFullPath(deployment.StagingPath),
                    Path.GetFullPath(rootPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return deployment;
        }
        catch
        {
            return null;
        }
    }

    private static string? BuildSourcePath(string rootPath, VortexDeploymentFileEntry file)
    {
        if (string.IsNullOrWhiteSpace(file.Source) || string.IsNullOrWhiteSpace(file.RelativePath))
        {
            return null;
        }

        var relativePath = file.RelativePath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        var sourcePath = Path.Combine(rootPath, file.Source, relativePath);
        return PathUtility.IsUnderRoot(rootPath, sourcePath)
            ? sourcePath
            : null;
    }

    private static string? TryResolveDeployedDataRoot(VortexDeploymentFileEntry file)
    {
        if (string.IsNullOrWhiteSpace(file.RelativePath) ||
            string.IsNullOrWhiteSpace(file.TargetPath) ||
            !Path.IsPathRooted(file.TargetPath))
        {
            return null;
        }

        var normalizedRelativePath = NormalizeRelativePath(file.RelativePath);
        var segments = normalizedRelativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        var currentPath = Path.GetFullPath(file.TargetPath);
        for (var index = 0; index < segments.Length; index++)
        {
            currentPath = Path.GetDirectoryName(currentPath) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(currentPath))
            {
                return null;
            }
        }

        return currentPath;
    }
}
