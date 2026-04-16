using System.Text;

namespace SkyrimLightingPatcher.Tests;

internal static class TestBsaArchiveBuilder
{
    private const uint ArchiveVersion = 104;
    private const uint ArchiveFlags = 0x3;
    private const uint MeshFileFlag = 0x1;
    private const int HeaderSize = 36;
    private const int FolderRecordSize = 16;
    private const int FileRecordSize = 16;

    public static async Task CreateFromFilesAsync(string archivePath, params (string EntryPath, string SourcePath)[] entries)
    {
        var archiveEntries = new List<ArchiveEntrySpec>(entries.Length);
        foreach (var (entryPath, sourcePath) in entries)
        {
            archiveEntries.Add(new ArchiveEntrySpec(
                NormalizeEntryPath(entryPath),
                await File.ReadAllBytesAsync(sourcePath).ConfigureAwait(false)));
        }

        Create(archivePath, archiveEntries);
    }

    public static void Create(string archivePath, IReadOnlyList<ArchiveEntrySpec> entries)
    {
        if (entries.Count == 0)
        {
            throw new ArgumentException("At least one archive entry is required.", nameof(entries));
        }

        var folders = entries
            .Select(static entry => new
            {
                FolderName = Path.GetDirectoryName(entry.EntryPath)?.Replace('/', '\\') ?? string.Empty,
                FileName = Path.GetFileName(entry.EntryPath),
                entry.Data,
            })
            .GroupBy(static entry => entry.FolderName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new FolderSpec(
                group.Key,
                GetFolderNameBytes(group.Key),
                group.OrderBy(static entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
                    .Select(static entry => new FileSpec(entry.FileName, entry.Data))
                    .ToArray()))
            .ToArray();

        var totalFolderNameLength = folders.Sum(static folder => folder.FolderNameBytes.Length);
        var totalFileNameLength = folders.Sum(static folder => folder.Files.Sum(file => Encoding.UTF8.GetByteCount(file.FileName) + 1));
        var folderSectionStart = HeaderSize + (folders.Length * FolderRecordSize);
        var currentSectionOffset = folderSectionStart;
        var folderOffsets = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            folderOffsets[folder.FolderName] = checked((uint)currentSectionOffset);
            currentSectionOffset += 1 + folder.FolderNameBytes.Length + (folder.Files.Length * FileRecordSize);
        }

        var currentDataOffset = currentSectionOffset + totalFileNameLength;
        var fileOffsets = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            foreach (var file in folder.Files)
            {
                var fullPath = CombineArchivePath(folder.FolderName, file.FileName);
                fileOffsets[fullPath] = checked((uint)currentDataOffset);
                currentDataOffset += file.Data.Length;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);

        using var stream = File.Open(archivePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(new byte[] { (byte)'B', (byte)'S', (byte)'A', 0 });
        writer.Write(ArchiveVersion);
        writer.Write((uint)HeaderSize);
        writer.Write(ArchiveFlags);
        writer.Write(checked((uint)folders.Length));
        writer.Write(checked((uint)entries.Count));
        writer.Write(checked((uint)totalFolderNameLength));
        writer.Write(checked((uint)totalFileNameLength));
        writer.Write(MeshFileFlag);

        foreach (var folder in folders)
        {
            writer.Write(0UL);
            writer.Write(checked((uint)folder.Files.Length));
            writer.Write(folderOffsets[folder.FolderName]);
        }

        foreach (var folder in folders)
        {
            writer.Write(checked((byte)folder.FolderNameBytes.Length));
            writer.Write(folder.FolderNameBytes);

            foreach (var file in folder.Files)
            {
                writer.Write(0UL);
                writer.Write(checked((uint)file.Data.Length));
                writer.Write(fileOffsets[CombineArchivePath(folder.FolderName, file.FileName)]);
            }
        }

        foreach (var folder in folders)
        {
            foreach (var file in folder.Files)
            {
                var fileNameBytes = Encoding.UTF8.GetBytes(file.FileName);
                writer.Write(fileNameBytes);
                writer.Write((byte)0);
            }
        }

        foreach (var folder in folders)
        {
            foreach (var file in folder.Files)
            {
                writer.Write(file.Data);
            }
        }
    }

    private static byte[] GetFolderNameBytes(string folderName)
    {
        return string.IsNullOrWhiteSpace(folderName)
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(folderName.Trim('\\') + '\0');
    }

    private static string NormalizeEntryPath(string entryPath)
    {
        return entryPath.Replace('/', '\\').TrimStart('\\');
    }

    private static string CombineArchivePath(string folderName, string fileName)
    {
        return string.IsNullOrWhiteSpace(folderName)
            ? fileName
            : $"{folderName}\\{fileName}";
    }

    public sealed record ArchiveEntrySpec(string EntryPath, byte[] Data);

    private sealed record FolderSpec(string FolderName, byte[] FolderNameBytes, FileSpec[] Files);

    private sealed record FileSpec(string FileName, byte[] Data);
}
