using SkyrimLightingPatcher.Core.Interfaces;

namespace SkyrimLightingPatcher.Core.Services;

public sealed class DiskSpaceMonitor : IDiskSpaceMonitor
{
    public long GetAvailableBytes(string stageName, string targetPath)
    {
        var drive = ResolveDrive(targetPath);
        return drive.AvailableFreeSpace;
    }

    public IDisposable ReserveSpace(string stageName, string targetPath, string reservationName, long bytes)
    {
        if (bytes <= 0)
        {
            return NoOpReservation.Instance;
        }

        var reservationDirectory = ResolveReservationDirectory(targetPath);
        Directory.CreateDirectory(reservationDirectory);

        var reservationFileName =
            $".skyrim-lighting-patcher-reserve-{SanitizeFileName(reservationName)}-{Guid.NewGuid():N}.tmp";
        var reservationPath = Path.Combine(reservationDirectory, reservationFileName);
        var reservationStream = new FileStream(
            reservationPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.DeleteOnClose);
        reservationStream.SetLength(bytes);
        return new FileReservation(reservationPath, reservationStream);
    }

    private static DriveInfo ResolveDrive(string targetPath)
    {
        var fullPath = Path.GetFullPath(targetPath);
        var location = fullPath;
        if (!Directory.Exists(fullPath) && Path.HasExtension(fullPath))
        {
            location = Path.GetDirectoryName(fullPath) ?? fullPath;
        }

        var root = Path.GetPathRoot(location);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException($"Unable to resolve disk root for '{targetPath}'.");
        }

        return new DriveInfo(root);
    }

    private static string ResolveReservationDirectory(string targetPath)
    {
        var fullPath = Path.GetFullPath(targetPath);
        if (Directory.Exists(fullPath))
        {
            return fullPath;
        }

        if (Path.HasExtension(fullPath))
        {
            return Path.GetDirectoryName(fullPath) ?? Path.GetPathRoot(fullPath) ?? fullPath;
        }

        return fullPath;
    }

    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "reservation";
        }

        var invalidCharacters = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalidCharacters.Contains(character) ? '_' : character));
    }

    private sealed class FileReservation(string reservationPath, FileStream reservationStream) : IDisposable
    {
        public void Dispose()
        {
            reservationStream.Dispose();
            try
            {
                if (File.Exists(reservationPath))
                {
                    File.Delete(reservationPath);
                }
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    private sealed class NoOpReservation : IDisposable
    {
        public static NoOpReservation Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
