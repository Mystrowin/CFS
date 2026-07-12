using System.Runtime.InteropServices;

namespace Cfs.Core;

public enum CfsMountMode
{
    ProjFs,
    CompatibilityFullExtraction
}

public sealed record CfsProjFsAvailability(bool IsAvailable, string Message);

public static class CfsProjFsPrerequisite
{
    public static CfsProjFsAvailability Check()
    {
        var libraryAvailable = false;
        if (OperatingSystem.IsWindows() && NativeLibrary.TryLoad("ProjectedFSLib.dll", out var library))
        {
            libraryAvailable = true;
            NativeLibrary.Free(library);
        }
        return Evaluate(OperatingSystem.IsWindows(), libraryAvailable);
    }

    public static CfsProjFsAvailability Evaluate(bool isSupportedWindows, bool projectedFsLibraryAvailable)
    {
        if (!isSupportedWindows)
            return new(false, "ProjFS mounting requires Windows 10 version 1809 or newer, or Windows 11.");
        if (!projectedFsLibraryAvailable)
            return new(false, "Windows Projected File System (Client-ProjFS) is unavailable. Enable 'Windows Projected File System' in Windows Features, restart if requested, then try Open in Explorer again. CFS did not fall back to extraction. To proceed without ProjFS, choose 'Compatibility Mode (Full Extraction)' explicitly.");
        return new(true, "Windows Projected File System is available.");
    }
}

public sealed record CfsMountDecision(bool CanMount, CfsMountMode? Mode, string Message);

public static class CfsMountPolicy
{
    public static CfsMountDecision Decide(CfsProjFsAvailability availability, bool compatibilityModeExplicitlySelected)
    {
        ArgumentNullException.ThrowIfNull(availability);
        if (compatibilityModeExplicitlySelected)
            return new(true, CfsMountMode.CompatibilityFullExtraction, "Compatibility Mode was explicitly selected. The archive will be fully extracted; this is not an on-demand ProjFS mount.");
        if (availability.IsAvailable)
            return new(true, CfsMountMode.ProjFs, availability.Message);
        return new(false, null, availability.Message);
    }
}

public enum CfsUiLifecycleState
{
    Unmounted,
    Mounting,
    Mounted,
    Saving,
    Validating,
    CleanupFailed
}

public sealed class CfsUiStateModel
{
    public CfsUiLifecycleState State { get; private set; } = CfsUiLifecycleState.Unmounted;
    public string? MountPath { get; private set; }
    public string DisplayText { get; private set; } = "Unmounted";

    public void Set(CfsUiLifecycleState state, string? mountPath = null)
    {
        if (state is CfsUiLifecycleState.Mounted or CfsUiLifecycleState.CleanupFailed)
            ArgumentException.ThrowIfNullOrWhiteSpace(mountPath);
        State = state;
        MountPath = mountPath;
        DisplayText = state switch
        {
            CfsUiLifecycleState.Unmounted => "Unmounted",
            CfsUiLifecycleState.Mounting => "Mounting…",
            CfsUiLifecycleState.Mounted => "Mounted: " + Path.GetFullPath(mountPath!),
            CfsUiLifecycleState.Saving => "Saving mounted changes…",
            CfsUiLifecycleState.Validating => "Validating archive…",
            CfsUiLifecycleState.CleanupFailed => "Cleanup failed — mounted folder preserved at: " + Path.GetFullPath(mountPath!),
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
    }
}
