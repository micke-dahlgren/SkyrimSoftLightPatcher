using System.Threading;

namespace SkyrimGlowingMeshPatcher.App.Models;

public enum DebugPatchFailureMode
{
    None = 0,
    PatchLowDiskPreparingOutput = 1,
    PatchLowDiskWritingPatchedFiles = 2,
    PatchLowDiskWritingOutputManifest = 3,
    PatchLowDiskCreatingArchive = 4,
    PatchLowDiskWritingRunManifest = 5,
    PatchArchiveCreationFailure = 6,
    PatchUnexpectedFailure = 7,
    PatchInjectSingleFileFailure = 8,
    ScanUnexpectedFailure = 9,
    ScanInjectSingleErrorFile = 10,
}

public sealed class DebugFaultOption(DebugPatchFailureMode mode, string label)
{
    public DebugPatchFailureMode Mode { get; } = mode;

    public string Label { get; } = label;

    public override string ToString()
    {
        return Label;
    }
}

public sealed class DebugFaultState
{
    private int patchFailureMode;
    private int scanFailureMode;
    private int remainingSingleFilePatchFailures;
    private int remainingLowDiskPatchedWriteFailures;

    public DebugPatchFailureMode PatchFailureMode
    {
        get => (DebugPatchFailureMode)Volatile.Read(ref patchFailureMode);
        set => Volatile.Write(ref patchFailureMode, (int)value);
    }

    public DebugPatchFailureMode ScanFailureMode
    {
        get => (DebugPatchFailureMode)Volatile.Read(ref scanFailureMode);
        set => Volatile.Write(ref scanFailureMode, (int)value);
    }

    public void BeginPatchRun()
    {
        var shouldInjectSingleFileFailure = PatchFailureMode == DebugPatchFailureMode.PatchInjectSingleFileFailure;
        var shouldInjectLowDiskPatchedWriteFailure = PatchFailureMode == DebugPatchFailureMode.PatchLowDiskWritingPatchedFiles;
        Volatile.Write(ref remainingSingleFilePatchFailures, shouldInjectSingleFileFailure ? 1 : 0);
        Volatile.Write(ref remainingLowDiskPatchedWriteFailures, shouldInjectLowDiskPatchedWriteFailure ? 1 : 0);
    }

    public bool TryConsumeSingleFilePatchFailure()
    {
        return Interlocked.CompareExchange(ref remainingSingleFilePatchFailures, 0, 1) == 1;
    }

    public bool TryConsumeLowDiskPatchedWriteFailure()
    {
        return Interlocked.CompareExchange(ref remainingLowDiskPatchedWriteFailures, 0, 1) == 1;
    }
}
