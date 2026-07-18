namespace Cfs.Core;

public static class CfsFolderSync
{
    internal const string MountMarkerFileName = ".cfs-mount-session";

    public static void PrepareMountFolder(CfsArchive archive, string mountFolder)
    {
        if (Directory.Exists(mountFolder) && Directory.EnumerateFileSystemEntries(mountFolder).Any())
        {
            throw new CfsArchiveException("Mount folder must be empty.");
        }

        Directory.CreateDirectory(mountFolder);
        archive.ExtractAll(mountFolder);
    }

    public static bool ApplyFolderChanges(CfsArchive archive, string mountedFolder, IProgress<CfsProgress>? progress = null, CancellationToken cancellationToken = default, IReadOnlySet<string>? excludedEntryPaths = null, bool persist = true)
    {
        if (!Directory.Exists(mountedFolder))
        {
            throw new DirectoryNotFoundException(mountedFolder);
        }

        return archive.ReplaceWithFolderSnapshot(mountedFolder, MountMarkerFileName, progress, cancellationToken, excludedEntryPaths, persist);
    }
}
