namespace SkyrimLightingPatcher.Core.Models;

public sealed class LowDiskSpaceException : IOException
{
    public LowDiskSpaceException(
        string stageName,
        string targetPath,
        long requiredBytes,
        long availableBytes,
        string recoveryHint,
        string? quickCleanupCommand = null,
        Exception? innerException = null)
        : base(
            BuildMessage(stageName, targetPath, requiredBytes, availableBytes, recoveryHint, quickCleanupCommand),
            innerException)
    {
        StageName = stageName;
        TargetPath = targetPath;
        RequiredBytes = requiredBytes;
        AvailableBytes = availableBytes;
        RecoveryHint = recoveryHint;
        QuickCleanupCommand = quickCleanupCommand;
    }

    public string StageName { get; }

    public string TargetPath { get; }

    public long RequiredBytes { get; }

    public long AvailableBytes { get; }

    public string RecoveryHint { get; }

    public string? QuickCleanupCommand { get; }

    private static string BuildMessage(
        string stageName,
        string targetPath,
        long requiredBytes,
        long availableBytes,
        string recoveryHint,
        string? quickCleanupCommand)
    {
        var missingBytes = Math.Max(0, requiredBytes - availableBytes);
        var message =
            $"Not enough disk space while {stageName}. Need about {FormatBytes(requiredBytes)} but only {FormatBytes(availableBytes)} is available at '{targetPath}'. Free at least {FormatBytes(missingBytes)} and run Patch again. {recoveryHint}";

        if (!string.IsNullOrWhiteSpace(quickCleanupCommand))
        {
            message += $" Quick cleanup command: {quickCleanupCommand}";
        }

        return message;
    }

    private static string FormatBytes(long bytes)
    {
        var value = Math.Max(0, bytes);
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var unitIndex = 0;
        var size = (double)value;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.##} {units[unitIndex]}";
    }
}
