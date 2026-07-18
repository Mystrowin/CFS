namespace Cfs.Core;

public sealed record CfsStorageDescriptor(DriveType DriveType, string DriveFormat, bool IsReady = true);
public sealed record CfsWritableStorageAvailability(bool IsSupported, string Message);

/// <summary>Defines the deliberately narrow writable-storage support boundary for CFS 0.3.</summary>
public static class CfsWritableStoragePolicy
{
    public static CfsWritableStorageAvailability Evaluate(string path) =>
        Evaluate(path, OperatingSystem.IsWindows(), DescribeRoot, GetConfiguredCloudRoots());

    public static CfsWritableStorageAvailability Evaluate(
        string path,
        bool isWindows,
        Func<string, CfsStorageDescriptor> describeRoot,
        IEnumerable<string>? cloudRoots = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(describeRoot);
        if (!isWindows)
            return new(false, "CFS writable archives require local NTFS on supported Windows versions.");

        var fullPath = Path.GetFullPath(path);
        if (IsWithinAnyCloudRoot(fullPath, cloudRoots ?? []))
            return new(false, "CFS does not open writable archives from cloud-synchronized folders. Move the archive to a local non-synchronized NTFS folder and try again.");
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
            return new(false, "CFS could not identify the archive volume.");

        CfsStorageDescriptor descriptor;
        try { descriptor = describeRoot(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(false, "CFS could not inspect the archive volume. Writable archives require a ready local NTFS drive.");
        }
        if (!descriptor.IsReady || descriptor.DriveType != DriveType.Fixed || !string.Equals(descriptor.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            return new(false, "CFS writable archives require a ready local NTFS fixed drive. Network, removable, FAT/exFAT, and other locations are not supported for writable sessions.");
        return new(true, "Writable local NTFS storage is available.");
    }

    public static void EnsureSupported(string path)
    {
        var result = Evaluate(path);
        if (!result.IsSupported) throw new CfsArchiveException(result.Message);
    }

    private static CfsStorageDescriptor DescribeRoot(string root)
    {
        var drive = new DriveInfo(root);
        return new(drive.DriveType, drive.DriveFormat, drive.IsReady);
    }

    private static IEnumerable<string> GetConfiguredCloudRoots()
    {
        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value)) yield return value;
        }
    }

    private static bool IsWithinAnyCloudRoot(string path, IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string fullRoot;
            try { fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); }
            catch (Exception) { continue; }
            if (string.Equals(path, fullRoot, StringComparison.OrdinalIgnoreCase)) return true;
            var prefix = fullRoot + Path.DirectorySeparatorChar;
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
