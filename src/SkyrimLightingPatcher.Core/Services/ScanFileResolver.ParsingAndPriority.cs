using System.Buffers.Binary;
using System.Text;
using SkyrimLightingPatcher.Core.Models;
using SkyrimLightingPatcher.Core.Utilities;

namespace SkyrimLightingPatcher.Core.Services;

public sealed partial class ScanFileResolver
{
    // Mirrors game load-order precedence for archive selection when plugin names align.
    private (int Group, int PluginIndex, string RelativePath) GetArchivePriority(
        string rootPath,
        ArchiveCandidate archiveCandidate,
        ModManagerKind modManager,
        ModOrganizer2Paths? modOrganizer2Paths,
        IReadOnlyDictionary<string, int>? mo2EnabledModOrder,
        IReadOnlyDictionary<string, int>? vortexSourceOrder)
    {
        var archivePath = archiveCandidate.ArchivePath;
        var relativePath = archiveCandidate.Origin == ArchiveCandidateOrigin.Mo2ManagedMod
            ? PathUtility.GetRelativeOrFileName(archiveCandidate.DisplayRootPath, archivePath)
            : PathUtility.GetRelativeOrFileName(rootPath, archivePath);
        var pluginOrder = GetPluginLoadOrder(modManager, modOrganizer2Paths);
        var baseName = Path.GetFileNameWithoutExtension(archivePath);
        var pluginIndex = pluginOrder.FindIndex(plugin =>
            string.Equals(plugin, baseName, StringComparison.OrdinalIgnoreCase));

        if (pluginIndex >= 0)
        {
            return (4, pluginIndex, relativePath);
        }

        if (archiveCandidate.Origin == ArchiveCandidateOrigin.Mo2ManagedMod &&
            mo2EnabledModOrder is not null)
        {
            var sourceModName = GetSourceModNameFromRelativePath(relativePath);
            if (!string.IsNullOrWhiteSpace(sourceModName) &&
                mo2EnabledModOrder.TryGetValue(sourceModName, out var modIndex))
            {
                return (3, modIndex, relativePath);
            }
        }

        if (modManager == ModManagerKind.Vortex &&
            archiveCandidate.Origin == ArchiveCandidateOrigin.Staging &&
            vortexSourceOrder is not null)
        {
            var sourceModName = GetSourceModNameFromRelativePath(relativePath);
            if (!string.IsNullOrWhiteSpace(sourceModName) &&
                vortexSourceOrder.TryGetValue(sourceModName, out var sourceIndex))
            {
                return (3, sourceIndex, relativePath);
            }
        }

        return archiveCandidate.Origin switch
        {
            ArchiveCandidateOrigin.Staging or ArchiveCandidateOrigin.Mo2ManagedMod => (2, int.MaxValue, relativePath),
            ArchiveCandidateOrigin.DeployedData or ArchiveCandidateOrigin.GameData => (1, int.MaxValue, relativePath),
            _ => (0, int.MaxValue, relativePath),
        };
    }

    private List<string> GetPluginLoadOrder(ModManagerKind modManager, ModOrganizer2Paths? modOrganizer2Paths)
    {
        if (modManager == ModManagerKind.ModOrganizer2 && modOrganizer2Paths is not null)
        {
            return ModOrganizer2Support.ReadPluginLoadOrder(modOrganizer2Paths).ToList();
        }

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

    private sealed record VortexDeploymentFileEntry(string? Source, string? RelativePath, string? TargetPath, long? DeploymentTime)
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
                        fileMap.TryGetValue("target", out var targetValue) ? targetValue as string : null,
                        fileMap.TryGetValue("time", out var timeValue) ? TryReadInt64(timeValue) : null));
                }
            }

            return new VortexDeployment(stagingPath, files);
        }

        private static long? TryReadInt64(object? value)
        {
            return value switch
            {
                null => null,
                long longValue => longValue,
                int intValue => intValue,
                short shortValue => shortValue,
                sbyte sbyteValue => sbyteValue,
                byte byteValue => byteValue,
                ushort ushortValue => ushortValue,
                uint uintValue => uintValue,
                ulong ulongValue when ulongValue <= long.MaxValue => (long)ulongValue,
                _ => null,
            };
        }
    }

    // Minimal parser for the subset of Vortex deployment MessagePack fields we need.
    // Keeping it local avoids taking an external dependency in the runtime patcher.
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
        GameData = 2,
        Mo2ManagedMod = 3,
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
