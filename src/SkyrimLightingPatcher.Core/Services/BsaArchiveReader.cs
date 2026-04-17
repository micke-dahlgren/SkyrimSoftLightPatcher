using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SkyrimLightingPatcher.Core.Services;

/// <summary>
/// Reads BSA metadata and extracts entries (including compressed payloads) needed by the patcher.
/// </summary>
internal sealed partial class BsaArchiveReader
{
    private const uint DefaultCompressionToggleBit = 1u << 30;
    private const uint MeshFileFlag = 0x1;
    private const uint Lz4FrameMagic = 0x184D2204;

    /// <summary>
    /// Builds an index of archive entries and flags whether the archive contains mesh content.
    /// </summary>
    public BsaArchiveIndex ReadIndex(string archivePath)
    {
        using var stream = File.OpenRead(archivePath);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        var magic = reader.ReadBytes(4);
        if (magic.Length != 4 || magic[0] != (byte)'B' || magic[1] != (byte)'S' || magic[2] != (byte)'A' || magic[3] != 0)
        {
            throw new InvalidDataException($"'{archivePath}' is not a supported BSA archive.");
        }

        var version = reader.ReadUInt32();
        if (version is not 104 and not 105)
        {
            throw new InvalidDataException($"BSA version {version} is not supported.");
        }

        var offset = reader.ReadUInt32();
        var archiveFlags = reader.ReadUInt32();
        var folderCount = reader.ReadUInt32();
        var fileCount = reader.ReadUInt32();
        _ = reader.ReadUInt32(); // totalFolderNameLength
        _ = reader.ReadUInt32(); // totalFileNameLength
        var fileFlags = reader.ReadUInt32();

        stream.Position = offset;

        var folderRecords = new List<FolderRecord>(checked((int)folderCount));
        for (var folderIndex = 0; folderIndex < folderCount; folderIndex++)
        {
            _ = reader.ReadUInt64(); // nameHash
            var count = reader.ReadUInt32();
            if (version == 105)
            {
                _ = reader.ReadUInt32(); // padding
            }

            var folderOffset = reader.ReadUInt32();
            if (version == 105)
            {
                _ = reader.ReadUInt32(); // padding
            }

            folderRecords.Add(new FolderRecord(count, folderOffset));
        }

        var includeDirectoryNames = (archiveFlags & 0x1) != 0;
        var includeFileNames = (archiveFlags & 0x2) != 0;
        var defaultCompressed = (archiveFlags & 0x4) != 0;
        var embedFileNames = (archiveFlags & 0x100) != 0;

        if (!includeFileNames)
        {
            throw new InvalidDataException("BSA archives without file names are not supported.");
        }

        var fileRecordsByFolder = new List<(string FolderName, List<FileRecord> FileRecords)>(folderRecords.Count);
        var flatFileRecords = new List<(string FolderName, FileRecord FileRecord)>(checked((int)fileCount));

        foreach (var folderRecord in folderRecords)
        {
            var folderName = includeDirectoryNames ? ReadBzString(reader) : string.Empty;
            var normalizedFolderName = NormalizeArchivePath(folderName);
            var fileRecords = new List<FileRecord>(checked((int)folderRecord.FileCount));

            for (var fileIndex = 0; fileIndex < folderRecord.FileCount; fileIndex++)
            {
                _ = reader.ReadUInt64(); // nameHash
                var size = reader.ReadUInt32();
                var offsetToData = reader.ReadUInt32();
                var isCompressed = defaultCompressed;
                if ((size & DefaultCompressionToggleBit) != 0)
                {
                    isCompressed = !isCompressed;
                    size &= ~DefaultCompressionToggleBit;
                }

                var record = new FileRecord(size, offsetToData, isCompressed);
                fileRecords.Add(record);
                flatFileRecords.Add((normalizedFolderName, record));
            }

            fileRecordsByFolder.Add((normalizedFolderName, fileRecords));
        }

        var fileNames = new List<string>(flatFileRecords.Count);
        for (var fileIndex = 0; fileIndex < flatFileRecords.Count; fileIndex++)
        {
            fileNames.Add(NormalizeArchivePath(ReadNullTerminatedString(reader)));
        }

        var entries = new List<BsaArchiveEntry>(flatFileRecords.Count);
        for (var fileIndex = 0; fileIndex < flatFileRecords.Count; fileIndex++)
        {
            var (folderName, record) = flatFileRecords[fileIndex];
            var fileName = fileNames[fileIndex];
            var entryPath = string.IsNullOrWhiteSpace(folderName)
                ? fileName
                : $"{folderName}\\{fileName}";

            entries.Add(new BsaArchiveEntry(
                archivePath,
                entryPath,
                record.Size,
                record.Offset,
                record.IsCompressed,
                checked((int)version),
                embedFileNames));
        }

        var containsMeshes = (fileFlags & MeshFileFlag) != 0 ||
                             entries.Any(static entry => entry.EntryPath.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase));

        return new BsaArchiveIndex(archivePath, checked((int)version), containsMeshes, entries);
    }

    /// <summary>
    /// Extracts the raw bytes for one archive entry, decompressing when required.
    /// </summary>
    public byte[] Extract(BsaArchiveEntry entry)
    {
        using var stream = File.OpenRead(entry.ArchivePath);
        stream.Position = entry.Offset;

        if (entry.EmbedFileNames)
        {
            _ = ReadBString(stream);
        }

        if (!entry.IsCompressed)
        {
            return ReadExactBytes(stream, checked((int)entry.Size));
        }

        Span<byte> sizeBytes = stackalloc byte[4];
        ReadExact(stream, sizeBytes);
        var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(sizeBytes);
        var compressedBytes = ReadExactBytes(stream, checked((int)entry.Size - 4));

        return entry.Version == 105
            ? DecompressLz4(compressedBytes, checked((int)uncompressedSize))
            : DecompressZlib(compressedBytes, checked((int)uncompressedSize));
    }

    private static byte[] DecompressLz4(byte[] compressedBytes, int expectedLength)
    {
        if (TryFindLz4Frame(compressedBytes, out var frameOffset))
        {
            var framePayload = frameOffset == 0
                ? compressedBytes
                : compressedBytes.AsSpan(frameOffset).ToArray();

            try
            {
                return Lz4FrameDecoder.Decode(framePayload, expectedLength);
            }
            catch (Exception frameException)
            {
                try
                {
                    return Lz4BlockDecoder.Decode(compressedBytes, expectedLength);
                }
                catch (Exception rawException)
                {
                    throw new InvalidDataException(
                        $"LZ4 decode failed [decoder=v2 frame-first] (frameOffset={frameOffset}). Frame error: {frameException.Message} Raw error: {rawException.Message}",
                        rawException);
                }
            }
        }

        try
        {
            return Lz4BlockDecoder.Decode(compressedBytes, expectedLength);
        }
        catch (Exception rawException)
        {
            throw new InvalidDataException(
                $"LZ4 decode failed [decoder=v2 raw-only] (frameOffset=none). Raw error: {rawException.Message}",
                rawException);
        }
    }

    private static bool TryFindLz4Frame(ReadOnlySpan<byte> compressedBytes, out int frameOffset)
    {
        frameOffset = -1;
        if (compressedBytes.Length < 4)
        {
            return false;
        }

        const int maxProbeOffset = 32;
        var probeLimit = Math.Min(maxProbeOffset, compressedBytes.Length - 4);
        for (var offset = 0; offset <= probeLimit; offset++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(compressedBytes.Slice(offset, 4)) == Lz4FrameMagic)
            {
                frameOffset = offset;
                return true;
            }
        }

        return false;
    }

    private static byte[] DecompressZlib(byte[] compressedBytes, int expectedLength)
    {
        using var input = new MemoryStream(compressedBytes, writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(expectedLength);
        zlib.CopyTo(output);

        var result = output.ToArray();
        if (result.Length != expectedLength)
        {
            Array.Resize(ref result, expectedLength);
        }

        return result;
    }

    private static string ReadBzString(BinaryReader reader)
    {
        var length = reader.ReadByte();
        if (length == 0)
        {
            return string.Empty;
        }

        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException("Unexpected end of BSA folder-name block.");
        }

        var text = Encoding.UTF8.GetString(bytes);
        return text.TrimEnd('\0');
    }

    private static string ReadNullTerminatedString(BinaryReader reader)
    {
        using var buffer = new MemoryStream();
        while (true)
        {
            var next = reader.ReadByte();
            if (next == 0)
            {
                break;
            }

            buffer.WriteByte(next);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string ReadBString(Stream stream)
    {
        var length = stream.ReadByte();
        if (length < 0)
        {
            throw new EndOfStreamException("Unexpected end of BSA embedded filename block.");
        }

        var bytes = ReadExactBytes(stream, length);
        return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }

    private static byte[] ReadExactBytes(Stream stream, int length)
    {
        var bytes = new byte[length];
        ReadExact(stream, bytes);
        return bytes;
    }

    private static void ReadExact(Stream stream, Span<byte> buffer)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = stream.Read(buffer[totalRead..]);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of BSA data stream.");
            }

            totalRead += read;
        }
    }

    private static string NormalizeArchivePath(string path)
    {
        return path
            .Replace('/', '\\')
            .TrimStart('\\')
            .Trim();
    }

    private sealed record FolderRecord(uint FileCount, uint Offset);

    private sealed record FileRecord(uint Size, uint Offset, bool IsCompressed);

}
