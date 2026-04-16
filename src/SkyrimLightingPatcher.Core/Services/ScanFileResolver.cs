using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SkyrimLightingPatcher.Core.Interfaces;
using SkyrimLightingPatcher.Core.Models;
using SkyrimLightingPatcher.Core.Utilities;

namespace SkyrimLightingPatcher.Core.Services;

public sealed class ScanFileResolver : IScanFileResolver
{
    private const string StagingMarkerFileName = "__vortex_staging_folder";
    private const string DeploymentFileName = "vortex.deployment.msgpack";
    private readonly BsaArchiveReader bsaArchiveReader = new();
    private readonly Dictionary<string, BsaArchiveIndex> archiveIndexCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim archiveCacheLock = new(1, 1);

    public async Task<IReadOnlyList<MeshSource>> ResolveFilePathsAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        var winners = new Dictionary<string, MeshSource>(StringComparer.OrdinalIgnoreCase);

        foreach (var archiveSource in await ResolveArchiveSourcesAsync(rootPath, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            winners[PathUtility.NormalizeForComparison(archiveSource.OutputRelativePath)] = archiveSource;
        }

        foreach (var looseSource in await ResolveLooseSourcesAsync(rootPath, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            winners[PathUtility.NormalizeForComparison(looseSource.OutputRelativePath)] = looseSource;
        }

        return winners.Values
            .OrderBy(static source => source.DisplayPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

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

    private async Task<IReadOnlyList<MeshSource>> ResolveLooseSourcesAsync(string rootPath, CancellationToken cancellationToken)
    {
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

    private async Task<IReadOnlyList<MeshSource>> ResolveArchiveSourcesAsync(string rootPath, CancellationToken cancellationToken)
    {
        var activeVortexSources = await TryResolveActiveVortexSourcesAsync(rootPath, cancellationToken).ConfigureAwait(false);
        var archiveCandidates = new List<ArchiveCandidate>();
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

        var archives = archiveCandidates
            .Distinct(ArchiveCandidatePathComparer.Instance)
            .OrderBy(candidate => GetArchivePriority(rootPath, candidate.ArchivePath), Comparer<(int Group, int PluginIndex, string RelativePath)>.Default)
            .ThenBy(static candidate => candidate.ArchivePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var sources = new List<MeshSource>();
        foreach (var archive in archives)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var archivePath = archive.ArchivePath;
            var archiveDisplayPath = PathUtility.GetRelativeOrFileName(rootPath, archivePath);
            var sourceModName = GetSourceModName(rootPath, archiveDisplayPath);
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
                sources.Add(CreateArchiveSource(rootPath, archive, entry.EntryPath));
            }
        }

        return sources;
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
            GetSourceModName(rootPath, displayPath));
    }

    private static MeshSource CreateArchiveSource(string rootPath, ArchiveCandidate archive, string entryPath)
    {
        var archiveDisplayPath = archive.Origin switch
        {
            ArchiveCandidateOrigin.DeployedData => BuildDeployedArchiveDisplayPath(archive.DisplayRootPath, archive.ArchivePath),
            _ => PathUtility.GetRelativeOrFileName(rootPath, archive.ArchivePath),
        };
        var displayPath = $"{archiveDisplayPath} -> {entryPath}";
        var sourceModName = archive.Origin == ArchiveCandidateOrigin.Staging
            ? GetSourceModName(rootPath, archiveDisplayPath)
            : null;
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

    private static string? GetSourceModName(string rootPath, string relativePath)
    {
        if (!IsVortexStagingFolder(rootPath))
        {
            return null;
        }

        var normalized = PathUtility.NormalizeSlashes(relativePath);
        var segments = normalized.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 1 ? segments[0] : null;
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

    private (int Group, int PluginIndex, string RelativePath) GetArchivePriority(string rootPath, string archivePath)
    {
        var relativePath = PathUtility.GetRelativeOrFileName(rootPath, archivePath);
        var pluginOrder = GetPluginLoadOrder();
        var baseName = Path.GetFileNameWithoutExtension(archivePath);
        var index = pluginOrder.FindIndex(plugin =>
            string.Equals(plugin, baseName, StringComparison.OrdinalIgnoreCase));

        return index >= 0
            ? (1, index, relativePath)
            : (0, int.MaxValue, relativePath);
    }

    private List<string> GetPluginLoadOrder()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Skyrim Special Edition", "loadorder.txt"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Skyrim Special Edition", "plugins.txt"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Skyrim Special Edition", "loadorder.txt"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Skyrim Special Edition", "plugins.txt"),
        };

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            return File.ReadAllLines(candidate)
                .Select(static line => line.Trim())
                .Where(static line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
                .Select(static line => line.TrimStart('*'))
                .Where(static line => line.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) ||
                                      line.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ||
                                      line.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
                .Select(static line => Path.GetFileNameWithoutExtension(line))
                .ToList();
        }

        return [];
    }

    private sealed record VortexDeployment(string? StagingPath, IReadOnlyList<VortexDeploymentFileEntry> Files);

    private sealed record VortexDeploymentFileEntry(string? Source, string? RelativePath, string? TargetPath)
    {
        public bool IsMeshNif =>
            !string.IsNullOrWhiteSpace(Source) &&
            !string.IsNullOrWhiteSpace(RelativePath) &&
            RelativePath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase);
    }

    private static class VortexDeploymentMessagePackParser
    {
        public static VortexDeployment Parse(ReadOnlyMemory<byte> data)
        {
            var reader = new MessagePackReader(data);
            var root = reader.Read();
            if (root is not Dictionary<string, object?> rootMap)
            {
                throw new InvalidDataException("Unexpected deployment payload.");
            }

            var stagingPath = rootMap.TryGetValue("stagingPath", out var stagingPathValue)
                ? stagingPathValue as string
                : null;
            var files = new List<VortexDeploymentFileEntry>();

            if (rootMap.TryGetValue("files", out var filesValue) && filesValue is List<object?> fileItems)
            {
                foreach (var fileItem in fileItems)
                {
                    if (fileItem is not Dictionary<string, object?> fileMap)
                    {
                        continue;
                    }

                    files.Add(new VortexDeploymentFileEntry(
                        fileMap.TryGetValue("source", out var sourceValue) ? sourceValue as string : null,
                        fileMap.TryGetValue("relPath", out var relativePathValue) ? relativePathValue as string : null,
                        fileMap.TryGetValue("target", out var targetValue) ? targetValue as string : null));
                }
            }

            return new VortexDeployment(stagingPath, files);
        }
    }

    private ref struct MessagePackReader
    {
        private ReadOnlySpan<byte> data;
        private int index;

        public MessagePackReader(ReadOnlyMemory<byte> data)
        {
            this.data = data.Span;
            index = 0;
        }

        public object? Read()
        {
            var prefix = ReadByte();

            if (prefix <= 0x7f)
            {
                return (long)prefix;
            }

            if (prefix >= 0xe0)
            {
                return (long)(sbyte)prefix;
            }

            if (prefix is >= 0xa0 and <= 0xbf)
            {
                return ReadString(prefix & 0x1f);
            }

            if (prefix is >= 0x90 and <= 0x9f)
            {
                return ReadArray(prefix & 0x0f);
            }

            if (prefix is >= 0x80 and <= 0x8f)
            {
                return ReadMap(prefix & 0x0f);
            }

            return prefix switch
            {
                0xc0 => null,
                0xc2 => false,
                0xc3 => true,
                0xcc => (long)ReadUInt8(),
                0xcd => (long)ReadUInt16(),
                0xce => (long)ReadUInt32(),
                0xcf => unchecked((long)ReadUInt64()),
                0xd0 => (long)ReadInt8(),
                0xd1 => (long)ReadInt16(),
                0xd2 => (long)ReadInt32(),
                0xd3 => ReadInt64(),
                0xd9 => ReadString(ReadUInt8()),
                0xda => ReadString(ReadUInt16()),
                0xdb => ReadString(checked((int)ReadUInt32())),
                0xdc => ReadArray(ReadUInt16()),
                0xdd => ReadArray(checked((int)ReadUInt32())),
                0xde => ReadMap(ReadUInt16()),
                0xdf => ReadMap(checked((int)ReadUInt32())),
                0xc4 => ReadBinary(ReadUInt8()),
                0xc5 => ReadBinary(ReadUInt16()),
                0xc6 => ReadBinary(checked((int)ReadUInt32())),
                0xca => ReadFloat32(),
                0xcb => ReadFloat64(),
                0xd4 => ReadExtension(1),
                0xd5 => ReadExtension(2),
                0xd6 => ReadExtension(4),
                0xd7 => ReadExtension(8),
                0xd8 => ReadExtension(16),
                0xc7 => ReadExtension(ReadUInt8()),
                0xc8 => ReadExtension(ReadUInt16()),
                0xc9 => ReadExtension(checked((int)ReadUInt32())),
                _ => throw new InvalidDataException($"Unsupported MessagePack prefix 0x{prefix:x2}."),
            };
        }

        private Dictionary<string, object?> ReadMap(int count)
        {
            var result = new Dictionary<string, object?>(count, StringComparer.Ordinal);
            for (var itemIndex = 0; itemIndex < count; itemIndex++)
            {
                var key = Read() as string
                          ?? throw new InvalidDataException("Expected string key in MessagePack map.");
                result[key] = Read();
            }

            return result;
        }

        private List<object?> ReadArray(int count)
        {
            var result = new List<object?>(count);
            for (var itemIndex = 0; itemIndex < count; itemIndex++)
            {
                result.Add(Read());
            }

            return result;
        }

        private string ReadString(int length)
        {
            var span = ReadSpan(length);
            return Encoding.UTF8.GetString(span);
        }

        private byte[] ReadBinary(int length)
        {
            return ReadSpan(length).ToArray();
        }

        private object ReadExtension(int length)
        {
            _ = ReadInt8();
            _ = ReadSpan(length);
            return new object();
        }

        private float ReadFloat32()
        {
            var value = ReadUInt32();
            return BitConverter.Int32BitsToSingle(unchecked((int)value));
        }

        private double ReadFloat64()
        {
            var value = ReadUInt64();
            return BitConverter.Int64BitsToDouble(unchecked((long)value));
        }

        private byte ReadByte()
        {
            EnsureAvailable(1);
            return data[index++];
        }

        private byte ReadUInt8() => ReadByte();

        private sbyte ReadInt8() => unchecked((sbyte)ReadByte());

        private ushort ReadUInt16()
        {
            var span = ReadSpan(2);
            return BinaryPrimitives.ReadUInt16BigEndian(span);
        }

        private short ReadInt16()
        {
            var span = ReadSpan(2);
            return BinaryPrimitives.ReadInt16BigEndian(span);
        }

        private uint ReadUInt32()
        {
            var span = ReadSpan(4);
            return BinaryPrimitives.ReadUInt32BigEndian(span);
        }

        private int ReadInt32()
        {
            var span = ReadSpan(4);
            return BinaryPrimitives.ReadInt32BigEndian(span);
        }

        private ulong ReadUInt64()
        {
            var span = ReadSpan(8);
            return BinaryPrimitives.ReadUInt64BigEndian(span);
        }

        private long ReadInt64()
        {
            var span = ReadSpan(8);
            return BinaryPrimitives.ReadInt64BigEndian(span);
        }

        private ReadOnlySpan<byte> ReadSpan(int length)
        {
            EnsureAvailable(length);
            var span = data.Slice(index, length);
            index += length;
            return span;
        }

        private void EnsureAvailable(int length)
        {
            if (index + length > data.Length)
            {
                throw new EndOfStreamException("Unexpected end of MessagePack payload.");
            }
        }
    }

    private enum ArchiveCandidateOrigin
    {
        Staging = 0,
        DeployedData = 1,
    }

    private sealed record ArchiveCandidate(string ArchivePath, string DisplayRootPath, ArchiveCandidateOrigin Origin);

    private sealed class ArchiveCandidatePathComparer : IEqualityComparer<ArchiveCandidate>
    {
        public static ArchiveCandidatePathComparer Instance { get; } = new();

        public bool Equals(ArchiveCandidate? x, ArchiveCandidate? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || y is null)
            {
                return false;
            }

            return string.Equals(x.ArchivePath, y.ArchivePath, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(ArchiveCandidate obj)
        {
            return StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ArchivePath);
        }
    }
}
