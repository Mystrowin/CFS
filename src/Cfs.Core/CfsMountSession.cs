namespace Cfs.Core;

public sealed class CfsMountSession
{
    private readonly string _markerValue;
    private readonly CfsProjFsMount? _projFsMount;
    private readonly FileSystemWatcher? _changeWatcher;
    private readonly HashSet<string> _deletedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _changeLock = new();
    private bool _providerStopped;

    private CfsMountSession(string folderPath, string markerValue, CfsMountMode mode, CfsProjFsMount? projFsMount = null, FileSystemWatcher? changeWatcher = null)
    {
        FolderPath = folderPath;
        _markerValue = markerValue;
        Mode = mode;
        _projFsMount = projFsMount;
        _changeWatcher = changeWatcher;
    }

    public string FolderPath { get; }
    public CfsMountMode Mode { get; }

    public static CfsMountSession Create(CfsArchive archive, string mountFolder, IProgress<CfsProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mountFolder);

        var fullPath = Path.GetFullPath(mountFolder);
        if (Directory.Exists(fullPath) || File.Exists(fullPath))
        {
            throw new CfsArchiveException($"Mount folder already exists: '{fullPath}'.");
        }

        CfsDiagnostics.Logger.WritePathEvent("mount", archive.ArchivePath, "starting");
        Directory.CreateDirectory(fullPath);
        var markerValue = Guid.NewGuid().ToString("N");
        var markerPath = Path.Combine(fullPath, CfsFolderSync.MountMarkerFileName);

        try
        {
            CfsProgressReporter.Report(progress, "Preparing mount", "Reading CFS manifest", null, 0, null, 0, null);
            cancellationToken.ThrowIfCancellationRequested();
            var projFsMount = CfsProjFsMount.Create(archive.ArchivePath, fullPath);
            File.WriteAllText(markerPath, markerValue);
            File.SetAttributes(markerPath, FileAttributes.Hidden | FileAttributes.NotContentIndexed);
            FileSystemWatcher? watcher = new(fullPath) { IncludeSubdirectories = true, EnableRaisingEvents = true };
            var session = new CfsMountSession(fullPath, markerValue, CfsMountMode.ProjFs, projFsMount, watcher);
            watcher.Deleted += (_, args) => session.RecordDeleted(args.FullPath);
            watcher.Renamed += (_, args) => session.RecordDeleted(args.OldFullPath);
            CfsDiagnostics.Logger.WritePathEvent("mount", archive.ArchivePath, "success");
            return session;
        }
        catch (Exception ex)
        {
            CfsDiagnostics.Logger.WriteException("mount", ex);
            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }

            throw;
        }
    }

    public static CfsMountSession CreateCompatibility(CfsArchive archive, string mountFolder, IProgress<CfsProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mountFolder);
        var fullPath = Path.GetFullPath(mountFolder);
        if (Directory.Exists(fullPath) || File.Exists(fullPath))
            throw new CfsArchiveException($"Compatibility Mode folder already exists: '{fullPath}'. Choose a new location or remove it safely first.");

        CfsDiagnostics.Logger.WritePathEvent("mount.compatibility", archive.ArchivePath, "starting-explicit-full-extraction");
        Directory.CreateDirectory(fullPath);
        var markerValue = Guid.NewGuid().ToString("N");
        var markerPath = Path.Combine(fullPath, CfsFolderSync.MountMarkerFileName);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            archive.ExtractAll(fullPath, progress, cancellationToken);
            File.WriteAllText(markerPath, markerValue);
            File.SetAttributes(markerPath, FileAttributes.Hidden | FileAttributes.NotContentIndexed);
            FileSystemWatcher watcher = new(fullPath) { IncludeSubdirectories = true, EnableRaisingEvents = true };
            var session = new CfsMountSession(fullPath, markerValue, CfsMountMode.CompatibilityFullExtraction, changeWatcher: watcher);
            watcher.Deleted += (_, args) => session.RecordDeleted(args.FullPath);
            watcher.Renamed += (_, args) => session.RecordDeleted(args.OldFullPath);
            CfsDiagnostics.Logger.WritePathEvent("mount.compatibility", archive.ArchivePath, "success-explicit-full-extraction");
            return session;
        }
        catch (Exception ex)
        {
            CfsDiagnostics.Logger.WriteException("mount.compatibility", ex);
            if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
            throw;
        }
    }

    public void Save(CfsArchive archive, IProgress<CfsProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        CfsDiagnostics.Logger.WritePathEvent("archive.save", archive.ArchivePath, "starting");
        try
        {
            EnsureOwnedMountFolder();
            // FileSystemWatcher events are delivered asynchronously; yield once so the final
            // Explorer delete/rename close is recorded before we materialize placeholders.
            Thread.Sleep(50);
            var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (_changeLock) deleted.UnionWith(_deletedPaths);
            foreach (var entry in CfsArchive.LoadManifestEntries(archive.ArchivePath).Where(entry => entry.Type == ArchiveEntryType.File))
            {
                var path = Path.Combine(FolderPath, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) deleted.Add(entry.Path.Replace('\\', '/'));
            }
            if (!_providerStopped)
            {
                _projFsMount?.MaterializeForSave(deleted);
                _projFsMount?.Dispose();
                _changeWatcher?.Dispose();
                _providerStopped = true;
            }
            CfsProgressReporter.Report(progress, "Saving changes", "Scanning mounted folder", null, 0, null, 0, null);
            cancellationToken.ThrowIfCancellationRequested();
            CfsFolderSync.ApplyFolderChanges(archive, FolderPath, progress, cancellationToken, deleted);
            CfsDiagnostics.Logger.WritePathEvent("archive.save", archive.ArchivePath, "success");
        }
        catch (Exception ex)
        {
            CfsDiagnostics.Logger.WriteException("archive.save", ex);
            throw;
        }
    }

    public void SaveAndUnmount(CfsArchive archive)
    {
        Save(archive);
        PermanentlyDelete();
    }

    public void PermanentlyDelete(IProgress<CfsProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        CfsDiagnostics.Logger.Write("mount.cleanup", $"target={CfsDiagnosticLogger.DescribePath(FolderPath)} outcome=starting");
        EnsureOwnedMountFolder();
        _projFsMount?.Dispose();

        try
        {
            if (DeleteDirectory is not null)
            {
                DeleteDirectory(FolderPath);
                CfsDiagnostics.Logger.Write("mount.cleanup", "outcome=success");
                CfsProgressReporter.Report(progress, "Cleaning temporary mount", "Verifying cleanup", FolderPath, 1, 1, 0, 0);
                return;
            }

            var files = Directory.EnumerateFiles(FolderPath, "*", SearchOption.AllDirectories).ToList();
            var directories = Directory.EnumerateDirectories(FolderPath, "*", SearchOption.AllDirectories)
                .OrderByDescending(path => path.Length).ToList();
            var totalBytes = files.Sum(file => new FileInfo(file).Length);
            long completedItems = 0, completedBytes = 0;
            CfsProgressReporter.Report(progress, "Cleaning temporary mount", "Scanning temporary files", null, 0, files.Count + directories.Count + 1, 0, totalBytes);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = new FileInfo(file).Length;
                File.Delete(file);
                completedItems++;
                completedBytes += length;
                CfsProgressReporter.Report(progress, "Cleaning temporary mount", "Deleting files", file, completedItems, files.Count + directories.Count + 1, completedBytes, totalBytes);
            }
            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Delete(directory);
                completedItems++;
                CfsProgressReporter.Report(progress, "Cleaning temporary mount", "Deleting folders", directory, completedItems, files.Count + directories.Count + 1, completedBytes, totalBytes);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Delete(FolderPath);
            CfsDiagnostics.Logger.Write("mount.cleanup", "outcome=success");
            CfsProgressReporter.Report(progress, "Cleaning temporary mount", "Verifying cleanup", FolderPath, completedItems + 1, files.Count + directories.Count + 1, completedBytes, totalBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CfsDiagnostics.Logger.WriteMountCleanupFailure(FolderPath, ex);
            throw new CfsArchiveException(
                $"Could not permanently remove CFS mounted folder '{FolderPath}'. Close any open files or Explorer windows and try again. The folder has been preserved at '{FolderPath}'.",
                ex);
        }
    }

    private void EnsureOwnedMountFolder()
    {
        var markerPath = Path.Combine(FolderPath, CfsFolderSync.MountMarkerFileName);
        if (!Directory.Exists(FolderPath) || !File.Exists(markerPath) || File.ReadAllText(markerPath) != _markerValue)
        {
            throw new CfsArchiveException($"Refusing to remove unverified mounted folder '{FolderPath}'. The folder has been preserved.");
        }
    }

    private void RecordDeleted(string fullPath)
    {
        var relative = Path.GetRelativePath(FolderPath, fullPath).Replace('\\', '/');
        if (relative.Equals(CfsFolderSync.MountMarkerFileName, StringComparison.OrdinalIgnoreCase)) return;
        lock (_changeLock) _deletedPaths.Add(relative);
    }

    // Test-only fault injection. Production cleanup deletes items individually for progress reporting.
    internal static Action<string>? DeleteDirectory { get; set; }
}
