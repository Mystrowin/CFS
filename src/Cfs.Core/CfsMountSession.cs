namespace Cfs.Core;

public sealed class CfsMountSession
{
    private readonly string _markerValue;
    private readonly string _archivePath;
    private CfsProjFsMount? _projFsMount;
    private FileSystemWatcher? _changeWatcher;
    private readonly HashSet<string> _deletedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _changeLock = new();
    private readonly object _saveLock = new();
    private int _internalCommitDepth;
    private int _changeObservedDuringCommit;
    private bool _providerStopped;

    private CfsMountSession(string archivePath, string folderPath, string markerValue, CfsMountMode mode, CfsProjFsMount? projFsMount = null, FileSystemWatcher? changeWatcher = null)
    {
        _archivePath = Path.GetFullPath(archivePath);
        FolderPath = folderPath;
        _markerValue = markerValue;
        Mode = mode;
        _projFsMount = projFsMount;
        _changeWatcher = changeWatcher;
    }

    public string FolderPath { get; }
    public CfsMountMode Mode { get; }
    public event EventHandler? ContentChanged;

    /// <summary>
    /// Bounded metadata-only fallback for missed FileSystemWatcher delivery. It never reads
    /// file content or asks ProjFS to hydrate a placeholder; it compares the projected
    /// namespace's names, sizes, and timestamps to the committed manifest.
    /// </summary>
    public bool DetectUnobservedChanges(int maximumEntries = 1_000_000)
    {
        EnsureOwnedMountFolder();
        var manifest = CfsArchive.LoadManifestEntries(_archivePath);
        if (manifest.Count > maximumEntries)
            throw new CfsArchiveException("CFS refused an unbounded mounted-session reconciliation scan.");

        var expectedFiles = manifest.Where(entry => entry.Type == ArchiveEntryType.File)
            .ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
        var expectedDirectories = manifest.Where(entry => entry.Type == ArchiveEntryType.Directory)
            .Select(entry => entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var observedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var observedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in Directory.EnumerateDirectories(FolderPath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(FolderPath, directory).Replace('\\', '/');
            if (!expectedDirectories.Contains(relative)) return true;
            observedDirectories.Add(relative);
        }
        if (!expectedDirectories.SetEquals(observedDirectories)) return true;

        foreach (var path in Directory.EnumerateFiles(FolderPath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(FolderPath, path).Replace('\\', '/');
            if (relative.Equals(CfsFolderSync.MountMarkerFileName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!expectedFiles.TryGetValue(relative, out var expected)) return true;
            var info = new FileInfo(path);
            if (info.Length != expected.OriginalSize || info.LastWriteTimeUtc != expected.LastWriteTimeUtc.UtcDateTime) return true;
            observedFiles.Add(relative);
        }

        return !expectedFiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(observedFiles);
    }

    public static CfsMountSession Create(CfsArchive archive, string mountFolder, IProgress<CfsProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return Create(archive.ArchivePath, mountFolder, progress, cancellationToken);
    }

    /// <summary>
    /// Creates an on-demand session from archive metadata. The ProjFS provider reads the
    /// manifest here and does not hydrate archive payloads until Windows requests a file.
    /// </summary>
    public static CfsMountSession Create(string archivePath, string mountFolder, IProgress<CfsProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(mountFolder);

        var fullPath = Path.GetFullPath(mountFolder);
        if (Directory.Exists(fullPath) || File.Exists(fullPath))
        {
            throw new CfsArchiveException($"Mount folder already exists: '{fullPath}'.");
        }

        CfsDiagnostics.Logger.WritePathEvent("mount", archivePath, "starting");
        Directory.CreateDirectory(fullPath);
        var markerValue = Guid.NewGuid().ToString("N");
        var markerPath = Path.Combine(fullPath, CfsFolderSync.MountMarkerFileName);

        try
        {
            CfsProgressReporter.Report(progress, "Preparing mount", "Reading CFS manifest", null, 0, null, 0, null);
            cancellationToken.ThrowIfCancellationRequested();
            var projFsMount = CfsProjFsMount.Create(archivePath, fullPath);
            File.WriteAllText(markerPath, markerValue);
            File.SetAttributes(markerPath, FileAttributes.Hidden | FileAttributes.NotContentIndexed);
            FileSystemWatcher? watcher = new(fullPath) { IncludeSubdirectories = true };
            var session = new CfsMountSession(archivePath, fullPath, markerValue, CfsMountMode.ProjFs, projFsMount, watcher);
            session.AttachProjFsChangeNotifications(projFsMount);
            session.AttachChangeWatcher(watcher);
            watcher.EnableRaisingEvents = true;
            CfsDiagnostics.Logger.WritePathEvent("mount", archivePath, "success");
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
            FileSystemWatcher watcher = new(fullPath) { IncludeSubdirectories = true };
            var session = new CfsMountSession(archive.ArchivePath, fullPath, markerValue, CfsMountMode.CompatibilityFullExtraction, changeWatcher: watcher);
            session.AttachChangeWatcher(watcher);
            watcher.EnableRaisingEvents = true;
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
        => SaveCore(archive, stopProvider: true, progress, cancellationToken);

    /// <summary>
    /// Transactionally commits the current mounted namespace while keeping the ProjFS
    /// provider alive. Files are materialized before the archive append, so the live
    /// namespace remains usable; unchanged archive blocks retain their existing offsets.
    /// </summary>
    public void CommitChanges(CfsArchive archive, IProgress<CfsProgress>? progress = null, CancellationToken cancellationToken = default)
        => SaveCore(archive, stopProvider: false, progress, cancellationToken);

    /// <summary>
    /// Materializes the mounted namespace and writes a validated candidate without touching
    /// the authoritative archive. The broker must atomically promote the candidate first.
    /// </summary>
    public bool PrepareCommitCandidate(CfsArchive archive, string candidatePath, IProgress<CfsProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        lock (_saveLock)
        {
            Interlocked.Increment(ref _internalCommitDepth);
            try
            {
                EnsureOwnedMountFolder();
                Thread.Sleep(50);
                var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                lock (_changeLock) deleted.UnionWith(_deletedPaths);
                foreach (var entry in CfsArchive.LoadManifestEntries(archive.ArchivePath).Where(entry => entry.Type == ArchiveEntryType.File))
                {
                    var path = Path.Combine(FolderPath, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(path)) deleted.Add(entry.Path.Replace('\\', '/'));
                }
                _projFsMount?.MaterializeForSave(deleted);
                var changed = CfsFolderSync.ApplyFolderChanges(archive, FolderPath, progress, cancellationToken, deleted, persist: false);
                if (!changed) return false;
                archive.WriteValidatedCandidate(candidatePath, cancellationToken);
                lock (_changeLock)
                {
                    foreach (var path in deleted) _deletedPaths.Remove(path);
                }
            }
            finally
            {
                if (Interlocked.Decrement(ref _internalCommitDepth) == 0
                    && Interlocked.Exchange(ref _changeObservedDuringCommit, 0) != 0)
                    ContentChanged?.Invoke(this, EventArgs.Empty);
            }
            return true;
        }
    }

    /// <summary>Refreshes ProjFS only after the broker has verified the replacement archive.</summary>
    public void FinalizeCommittedCandidate()
    {
        if (_providerStopped || _projFsMount is null) return;
        // Refreshing provider metadata after CFS's own verified replacement can emit
        // synchronous ProjFS notifications. They describe our committed state, not a
        // new user mutation, and must not schedule a redundant follow-up commit.
        Interlocked.Increment(ref _internalCommitDepth);
        try { _projFsMount.RefreshManifest(); }
        finally
        {
            if (Interlocked.Decrement(ref _internalCommitDepth) == 0)
                Interlocked.Exchange(ref _changeObservedDuringCommit, 0);
        }
    }

    private void SaveCore(CfsArchive archive, bool stopProvider, IProgress<CfsProgress>? progress, CancellationToken cancellationToken)
    {
        lock (_saveLock)
        {
            var coalesceWatcherEvents = !stopProvider && _changeWatcher is not null;
            if (coalesceWatcherEvents) Interlocked.Increment(ref _internalCommitDepth);
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
                    if (stopProvider)
                    {
                        _projFsMount?.Dispose();
                        _changeWatcher?.Dispose();
                        _providerStopped = true;
                    }
                }
                CfsProgressReporter.Report(progress, "Saving changes", "Scanning mounted folder", null, 0, null, 0, null);
                cancellationToken.ThrowIfCancellationRequested();
                CfsFolderSync.ApplyFolderChanges(archive, FolderPath, progress, cancellationToken, deleted);
                if (!stopProvider && !_providerStopped) _projFsMount?.RefreshManifest();
                lock (_changeLock)
                {
                    foreach (var path in deleted) _deletedPaths.Remove(path);
                }
                CfsDiagnostics.Logger.WritePathEvent("archive.save", archive.ArchivePath, "success");
            }
            catch (Exception ex)
            {
                CfsDiagnostics.Logger.WriteException("archive.save", ex);
                throw;
            }
            finally
            {
                if (coalesceWatcherEvents && Interlocked.Decrement(ref _internalCommitDepth) == 0
                    && Interlocked.Exchange(ref _changeObservedDuringCommit, 0) != 0)
                    ContentChanged?.Invoke(this, EventArgs.Empty);
            }
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
        var providerStoppedForCleanup = false;
        var cleanupSucceeded = false;
        Interlocked.Increment(ref _internalCommitDepth);
        try
        {
            EnsureMountFilesCanClose();
            _changeWatcher?.Dispose();
            _projFsMount?.Dispose();
            _providerStopped = true;
            providerStoppedForCleanup = true;
            if (DeleteDirectory is not null)
            {
                DeleteDirectory(FolderPath);
                cleanupSucceeded = true;
                CfsDiagnostics.Logger.Write("mount.cleanup", "outcome=success");
                CfsProgressReporter.Report(progress, "Cleaning temporary mount", "Verifying cleanup", FolderPath, 1, 1, 0, 0);
                return;
            }

            var markerPath = Path.Combine(FolderPath, CfsFolderSync.MountMarkerFileName);
            var files = Directory.EnumerateFiles(FolderPath, "*", SearchOption.AllDirectories)
                .Where(path => !string.Equals(Path.GetFullPath(path), Path.GetFullPath(markerPath), StringComparison.OrdinalIgnoreCase)).ToList();
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
            File.Delete(markerPath);
            Directory.Delete(FolderPath);
            cleanupSucceeded = true;
            CfsDiagnostics.Logger.Write("mount.cleanup", "outcome=success");
            CfsProgressReporter.Report(progress, "Cleaning temporary mount", "Verifying cleanup", FolderPath, completedItems + 1, files.Count + directories.Count + 1, completedBytes, totalBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            CfsDiagnostics.Logger.WriteMountCleanupFailure(FolderPath, ex);
            Exception? restartFailure = null;
            if (providerStoppedForCleanup)
            {
                try { RestoreAfterFailedCleanup(); }
                catch (Exception restartException) { restartFailure = restartException; CfsDiagnostics.Logger.WriteException("mount.cleanup.restart", restartException); }
            }
            throw new CfsArchiveException(
                restartFailure is null
                    ? $"Could not permanently remove CFS mounted folder '{FolderPath}'. Close any open files or Explorer windows and try again. The session and folder were preserved at '{FolderPath}'."
                    : $"Could not permanently remove CFS mounted folder '{FolderPath}', and its provider could not be restarted. The marked recovery folder remains at '{FolderPath}'. See the CFS diagnostic log.",
                restartFailure ?? ex);
        }
        finally
        {
            Interlocked.Decrement(ref _internalCommitDepth);
            var changedDuringCleanup = Interlocked.Exchange(ref _changeObservedDuringCommit, 0) != 0;
            if (!cleanupSucceeded && changedDuringCleanup && Directory.Exists(FolderPath))
                ContentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void EnsureMountFilesCanClose()
    {
        var markerPath = Path.Combine(FolderPath, CfsFolderSync.MountMarkerFileName);
        foreach (var file in Directory.EnumerateFiles(FolderPath, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(markerPath), StringComparison.OrdinalIgnoreCase)) continue;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        }
    }

    private void RestoreAfterFailedCleanup()
    {
        EnsureOwnedMountFolder();
        if (Mode == CfsMountMode.ProjFs) _projFsMount = CfsProjFsMount.Resume(_archivePath, FolderPath);
        _changeWatcher = new FileSystemWatcher(FolderPath) { IncludeSubdirectories = true };
        AttachChangeWatcher(_changeWatcher);
        _changeWatcher.EnableRaisingEvents = true;
        _providerStopped = false;
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

    private void AttachChangeWatcher(FileSystemWatcher watcher)
    {
        watcher.Changed += (_, args) => RecordChanged(args.FullPath);
        watcher.Created += (_, args) => RecordChanged(args.FullPath);
        watcher.Deleted += (_, args) => { RecordDeleted(args.FullPath); RecordChanged(args.FullPath); };
        watcher.Renamed += (_, args) => { RecordDeleted(args.OldFullPath); RecordChanged(args.FullPath); };
        watcher.Error += (_, args) =>
        {
            try { CfsDiagnostics.Logger.WriteException("mount.watcher", args.GetException()); } catch { }
            ContentChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    private void AttachProjFsChangeNotifications(CfsProjFsMount mount)
    {
        mount.MutationObserved += (_, _) => RecordProjectedMutation();
    }

    private void RecordProjectedMutation()
    {
        if (Volatile.Read(ref _internalCommitDepth) != 0)
        {
            Interlocked.Exchange(ref _changeObservedDuringCommit, 1);
            return;
        }
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RecordChanged(string fullPath)
    {
        var relative = Path.GetRelativePath(FolderPath, fullPath).Replace('\\', '/');
        if (relative.Equals(CfsFolderSync.MountMarkerFileName, StringComparison.OrdinalIgnoreCase)) return;
        // Atomic-save patterns can report Deleted(target) followed by Created/Renamed(target).
        // A path that exists again is no longer a deletion tombstone and must be included
        // in the next folder snapshot.
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            lock (_changeLock) _deletedPaths.Remove(relative);
        }
        if (Volatile.Read(ref _internalCommitDepth) != 0)
        {
            Interlocked.Exchange(ref _changeObservedDuringCommit, 1);
            return;
        }
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    // Test-only fault injection. Production cleanup deletes items individually for progress reporting.
    internal static Action<string>? DeleteDirectory { get; set; }
}
