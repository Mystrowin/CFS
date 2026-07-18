using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Cfs.Core;

namespace Cfs.Broker;

public interface ICfsBrokerSession : IAsyncDisposable
{
    string MountPath { get; }
}

public interface ICfsPersistentBrokerSession
{
    CfsPersistenceStatus PersistenceStatus { get; }
    Task FlushAsync(CancellationToken cancellationToken = default);
}

public interface ICfsClosableBrokerSession
{
    Task CloseAsync(CancellationToken cancellationToken = default);
}

public interface ICfsDiscardableBrokerSession
{
    Task DiscardAsync(CancellationToken cancellationToken = default);
}

public interface ICfsChangeReconciliationSession
{
    void ReconcileChanges();
}

public sealed record CfsBrokerFlushResult(bool Success, string? MountPath = null, string? Error = null, CfsPersistenceStatus? Status = null, string? ErrorCode = null);
public sealed record CfsBrokerCloseResult(bool Success, bool Found, string? MountPath = null, string? Error = null, CfsPersistenceStatus? Status = null, string? ErrorCode = null);

public sealed record BrokerOpenResult(string CanonicalArchiveKey, string MountPath);

public sealed class CfsBrokerSessionRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<ICfsBrokerSession>>> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<CfsArchiveIdentity, string, CancellationToken, Task<ICfsBrokerSession>> _sessionFactory;
    private readonly string _mountRoot;
    private readonly long? _storageLimitBytes;
    private int _createdSessionCount;

    public CfsBrokerSessionRegistry(
        string mountRoot,
        Func<CfsArchiveIdentity, string, CancellationToken, Task<ICfsBrokerSession>> sessionFactory,
        long? storageLimitBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mountRoot);
        _mountRoot = Path.GetFullPath(mountRoot);
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        if (storageLimitBytes is <= 0) throw new ArgumentOutOfRangeException(nameof(storageLimitBytes));
        _storageLimitBytes = storageLimitBytes;
    }

    public int SessionCount => _sessions.Count;
    public int CreatedSessionCount => Volatile.Read(ref _createdSessionCount);

    public async Task<CfsPersistenceStatus> GetPersistenceStatusAsync(string? canonicalArchiveKey = null)
    {
        var selected = canonicalArchiveKey is null
            ? _sessions.ToArray()
            : _sessions.Where(pair => StringComparer.OrdinalIgnoreCase.Equals(pair.Key, canonicalArchiveKey)).ToArray();
        var statuses = new List<CfsPersistenceStatus>();
        foreach (var pair in selected)
        {
            if (!pair.Value.IsValueCreated) continue;
            try
            {
                var session = await pair.Value.Value.ConfigureAwait(false);
                if (session is ICfsChangeReconciliationSession reconciliation) reconciliation.ReconcileChanges();
                if (session is ICfsPersistentBrokerSession persistent)
                    statuses.Add(persistent.PersistenceStatus);
            }
            catch (Exception ex)
            {
                CfsDiagnostics.Logger.WriteException("broker.persistence.status", ex);
            }
        }
        if (statuses.Count == 0) return new(CfsPersistenceState.Clean, false, 0, 0, null, null);
        return statuses.OrderByDescending(status => PersistenceSeverity(status.State)).First() with
        {
            IsDirty = statuses.Any(status => status.IsDirty),
            DirtyGeneration = statuses.Max(status => status.DirtyGeneration),
            CommittedGeneration = statuses.Max(status => status.CommittedGeneration),
            MutationSequence = statuses.Max(status => status.MutationSequence)
        };
    }

    public async Task<CfsBrokerFlushResult> FlushAllAsync(CancellationToken cancellationToken = default)
    {
        var sessions = _sessions.Values.Where(value => value.IsValueCreated).Select(value => value.Value).ToArray();
        foreach (var sessionTask in sessions)
        {
            var session = await sessionTask.ConfigureAwait(false);
            if (session is not ICfsPersistentBrokerSession persistent) continue;
            try { await persistent.FlushAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex)
            {
                CfsDiagnostics.Logger.WriteException("broker.persistence.flush", ex);
                return new(false, session.MountPath, SessionFailureMessage(ex,
                    "Automatic commit failed; the recoverable mount was preserved. See the CFS diagnostic log."),
                    persistent.PersistenceStatus, SessionFailureCode(ex));
            }
        }
        return new(true, Status: await GetPersistenceStatusAsync().ConfigureAwait(false));
    }

    public async Task<BrokerOpenResult> OpenAsync(CfsArchiveIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var gate = _sessionGates.GetOrAdd(identity.Key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lazy = _sessions.GetOrAdd(identity.Key, _ => new Lazy<Task<ICfsBrokerSession>>(
                () => CreateSessionAsync(identity, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication));
            try
            {
                var session = await lazy.Value.ConfigureAwait(false);
                return new BrokerOpenResult(identity.Key, session.MountPath);
            }
            catch
            {
                _sessions.TryRemove(identity.Key, out _);
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async Task<CfsBrokerCloseResult> CloseAsync(CfsArchiveIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var gate = _sessionGates.GetOrAdd(identity.Key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sessions.TryGetValue(identity.Key, out var lazy) || !lazy.IsValueCreated)
                return new(false, false, Error: "No live CFS session exists for the requested archive.");
            var session = await lazy.Value.ConfigureAwait(false);
            if (session is not ICfsClosableBrokerSession closable)
                return new(false, true, session.MountPath, "The live session does not support Close CFS.");
            try
            {
                await closable.CloseAsync(cancellationToken).ConfigureAwait(false);
                _sessions.TryRemove(identity.Key, out _);
                return new(true, true, session.MountPath, Status: session is ICfsPersistentBrokerSession persistent ? persistent.PersistenceStatus : null);
            }
            catch (Exception ex)
            {
                CfsDiagnostics.Logger.WriteException("broker.session.close", ex);
                return new(false, true, session.MountPath,
                    SessionFailureMessage(ex,
                        "Close CFS could not complete. The session and marked mount were preserved; close open files and inspect the CFS diagnostic log before retrying."),
                    session is ICfsPersistentBrokerSession persistent ? persistent.PersistenceStatus : null,
                    SessionFailureCode(ex));
            }
        }
        finally { gate.Release(); }
    }

    public Task<CfsBrokerCloseResult> CommitAsync(CfsArchiveIdentity identity, CancellationToken cancellationToken = default) =>
        RunSessionActionAsync(identity, "Commit changes", removeOnSuccess: false,
            static (session, token) => session is ICfsPersistentBrokerSession persistent
                ? persistent.FlushAsync(token)
                : throw new BrokerRequestException(CfsBrokerErrorCodes.InvalidRequest, "The live session does not support commit."), cancellationToken);

    public Task<CfsBrokerCloseResult> DiscardAsync(CfsArchiveIdentity identity, CancellationToken cancellationToken = default) =>
        RunSessionActionAsync(identity, "Discard pending changes", removeOnSuccess: true,
            static (session, token) => session is ICfsDiscardableBrokerSession discardable
                ? discardable.DiscardAsync(token)
                : throw new BrokerRequestException(CfsBrokerErrorCodes.InvalidRequest, "The live session does not support discard."), cancellationToken);

    private async Task<CfsBrokerCloseResult> RunSessionActionAsync(
        CfsArchiveIdentity identity,
        string action,
        bool removeOnSuccess,
        Func<ICfsBrokerSession, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var gate = _sessionGates.GetOrAdd(identity.Key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sessions.TryGetValue(identity.Key, out var lazy) || !lazy.IsValueCreated)
                return new(false, false, Error: "No live CFS session exists for the requested archive.");
            var session = await lazy.Value.ConfigureAwait(false);
            try
            {
                await operation(session, cancellationToken).ConfigureAwait(false);
                if (removeOnSuccess) _sessions.TryRemove(identity.Key, out _);
                return new(true, true, session.MountPath,
                    Status: session is ICfsPersistentBrokerSession persistent ? persistent.PersistenceStatus : null);
            }
            catch (Exception ex)
            {
                CfsDiagnostics.Logger.WriteException("broker.session.action", ex);
                return new(false, true, session.MountPath,
                    SessionFailureMessage(ex, $"{action} could not complete. The live session and marked mount were preserved."),
                    session is ICfsPersistentBrokerSession persistent ? persistent.PersistenceStatus : null,
                    SessionFailureCode(ex));
            }
        }
        finally { gate.Release(); }
    }

    private async Task<ICfsBrokerSession> CreateSessionAsync(CfsArchiveIdentity identity, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_mountRoot);
        if (_storageLimitBytes is { } storageLimit)
            CfsRecoveryStoragePolicy.EnsureWithinLimit(_mountRoot, storageLimit);
        var mountPath = Path.Combine(_mountRoot, identity.MountKey);
        Interlocked.Increment(ref _createdSessionCount);
        return await _sessionFactory(identity, mountPath, cancellationToken).ConfigureAwait(false);
    }

    private static int PersistenceSeverity(CfsPersistenceState state) => state switch
    {
        CfsPersistenceState.Failed => 5,
        CfsPersistenceState.Committing => 4,
        CfsPersistenceState.Dirty => 3,
        CfsPersistenceState.WaitingForQuietPeriod => 2,
        CfsPersistenceState.Clean => 1,
        _ => 0
    };

    private static string? SessionFailureCode(Exception exception) => exception switch
    {
        CfsFileInUseException => CfsBrokerErrorCodes.FileInUse,
        BrokerRequestException broker => broker.ErrorCode,
        _ => null
    };

    private static string SessionFailureMessage(Exception exception, string fallback) =>
        exception is CfsFileInUseException ? exception.Message : fallback;

    public async ValueTask DisposeAsync()
    {
        var sessions = _sessions.Where(pair => pair.Value.IsValueCreated).ToArray();
        foreach (var pair in sessions)
        {
            try
            {
                await (await pair.Value.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
                _sessions.TryRemove(pair.Key, out _);
            }
            catch (Exception ex)
            {
                CfsDiagnostics.Logger.WriteException("broker.session.cleanup", ex);
                throw;
            }
        }
    }
}

internal sealed class CfsBrokerMountedSession : ICfsBrokerSession, ICfsPersistentBrokerSession, ICfsClosableBrokerSession, ICfsDiscardableBrokerSession, ICfsChangeReconciliationSession
{
    private readonly string _archivePath;
    private readonly CfsMountSession _session;
    private readonly CfsAutomaticPersistence _persistence;
    private readonly Func<CancellationToken, Task> _commitOperation;
    private readonly CfsSessionTransaction? _transaction;
    private readonly bool _usesDefaultCommitOperation;
    private readonly Func<string, long> _availableFreeSpace;
    private readonly Action<CfsCommitPhase>? _commitPhaseObserver;
    private readonly bool _failReplacementValidation;
    private readonly bool _failBackupRestore;
    private readonly Func<bool>? _failCloseValidation;
    private readonly long _recoveryStorageLimitBytes;
    private readonly SemaphoreSlim _closeGate = new(1, 1);
    private CfsArchiveIdentity? _authoritativeIdentity;
    private FileStream? _archiveLock;
    private long _lastReconciliationTick;
    private bool _disposed;

    public CfsBrokerMountedSession(string archivePath, CfsMountSession session, TimeSpan? quietPeriod = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<CancellationToken, Task>? commit = null,
        CfsSessionTransaction? transaction = null, CfsArchiveIdentity? authoritativeIdentity = null,
        Func<string, long>? availableFreeSpace = null,
        Action<CfsCommitPhase>? commitPhaseObserver = null,
        bool failReplacementValidation = false,
        bool failBackupRestore = false,
        Func<bool>? failCloseValidation = null,
        long recoveryStorageLimitBytes = CfsRecoveryStoragePolicy.DefaultLimitBytes)
    {
        _archivePath = Path.GetFullPath(archivePath);
        _session = session;
        _transaction = transaction;
        _authoritativeIdentity = authoritativeIdentity;
        _usesDefaultCommitOperation = commit is null;
        _commitOperation = commit ?? CommitCoreAsync;
        _availableFreeSpace = availableFreeSpace ?? AvailableFreeSpace;
        _commitPhaseObserver = commitPhaseObserver;
        _failReplacementValidation = failReplacementValidation;
        _failBackupRestore = failBackupRestore;
        _failCloseValidation = failCloseValidation;
        if (recoveryStorageLimitBytes <= 0 || recoveryStorageLimitBytes > CfsRecoveryStoragePolicy.DefaultLimitBytes)
            throw new ArgumentOutOfRangeException(nameof(recoveryStorageLimitBytes));
        _recoveryStorageLimitBytes = recoveryStorageLimitBytes;
        _archiveLock = AcquireArchiveLock(_archivePath);
        try
        {
            _persistence = new CfsAutomaticPersistence(CommitWithTransactionAsync, quietPeriod ?? TimeSpan.FromMilliseconds(750), delay);
            _session.ContentChanged += OnContentChanged;
        }
        catch
        {
            _archiveLock.Dispose();
            _archiveLock = null;
            throw;
        }
    }
    public string MountPath => _session.FolderPath;
    public CfsPersistenceStatus PersistenceStatus => _persistence.Status;

    public void ReconcileChanges()
    {
        // Status polling normally happens at 500 ms. Limit metadata reconciliation to one
        // attempt per second so a client cannot turn it into a tight recursive scan loop.
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastReconciliationTick) < 1_000) return;
        Interlocked.Exchange(ref _lastReconciliationTick, now);
        if (!_persistence.Status.IsDirty && _session.DetectUnobservedChanges()) OnContentChanged(this, EventArgs.Empty);
    }

    private void OnContentChanged(object? sender, EventArgs args)
    {
        try
        {
            var generation = _persistence.MarkDirty();
            _transaction?.MarkCommitPending(generation);
        }
        catch (ObjectDisposedException) { }
    }

    private Task CommitCoreAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var archive = CfsArchive.Load(_archivePath, cancellationToken: cancellationToken);
        _session.CommitChanges(archive, cancellationToken: cancellationToken);
    }, cancellationToken);

    private async Task CommitWithTransactionAsync(CancellationToken cancellationToken)
    {
        var generation = _persistence.Status.DirtyGeneration;
        EnsureAuthoritativeIdentity();
        _transaction?.MarkCommitPending(generation);
        SetCommitPhase(CfsCommitPhase.Preparing);
        EnsureCommitSpace();
        if (_usesDefaultCommitOperation)
            await CommitDefaultTransactionAsync(cancellationToken).ConfigureAwait(false);
        else
        {
            await _commitOperation(cancellationToken).ConfigureAwait(false);
            if (!CfsArchive.Validate(_archivePath, cancellationToken: cancellationToken).IsValid)
                throw new CfsArchiveException("The automatic commit did not produce a valid CFS archive.");
            _authoritativeIdentity = CfsArchiveIdentity.Create(_archivePath);
        }
        _transaction?.MarkCommitted(generation);
        _ = _transaction?.FinalizeCommittedArtifacts();
    }

    private Task CommitDefaultTransactionAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var archiveDirectory = Path.GetDirectoryName(_archivePath)
            ?? throw new CfsArchiveException("The CFS archive has no parent directory.");
        var token = Guid.NewGuid().ToString("N");
        var candidate = Path.Combine(archiveDirectory, $".{Path.GetFileName(_archivePath)}.{token}.cfs-candidate");
        var backup = Path.Combine(archiveDirectory, $".{Path.GetFileName(_archivePath)}.{token}.cfs-backup");
        var replacementSucceeded = false;
        FileStream? replacementLock = null;

        try
        {
            SetCommitPhase(CfsCommitPhase.WritingCandidate, candidate, backup);
            var archive = CfsArchive.Load(_archivePath, cancellationToken: cancellationToken);
            if (!_session.PrepareCommitCandidate(archive, candidate, cancellationToken: cancellationToken))
                return;
            cancellationToken.ThrowIfCancellationRequested();
            SetCommitPhase(CfsCommitPhase.FlushingCandidate, candidate, backup);
            SetCommitPhase(CfsCommitPhase.ValidatingCandidate, candidate, backup);
            if (!CfsArchive.Validate(candidate, cancellationToken: cancellationToken).IsValid)
                throw new CfsArchiveException("The CFS commit candidate failed validation.");
            EnsureCommitSpace(candidate);

            // File.Replace is same-volume and gives us a backup of the authoritative file.
            // The candidate and backup deliberately live beside the archive, never under a mount.
            SetCommitPhase(CfsCommitPhase.ReadyToReplace, candidate, backup);
            SetCommitPhase(CfsCommitPhase.Replacing, candidate, backup);
            File.Replace(candidate, _archivePath, backup, ignoreMetadataErrors: true);
            replacementSucceeded = true;
            replacementLock = AcquireArchiveLock(_archivePath);

            SetCommitPhase(CfsCommitPhase.VerifyingReplacement, candidate, backup);
            if (_failReplacementValidation)
                throw new CfsArchiveException("Injected post-replacement validation failure.");
            if (!CfsArchive.Validate(_archivePath, cancellationToken: cancellationToken).IsValid)
                throw new CfsArchiveException("The replacement CFS archive failed validation.");

            AdoptArchiveLock(replacementLock);
            replacementLock = null;
            _authoritativeIdentity = CfsArchiveIdentity.Create(_archivePath);
            _session.FinalizeCommittedCandidate();
            SetCommitPhase(CfsCommitPhase.Committed, candidate, backup);
            if (_transaction is null && File.Exists(backup)) File.Delete(backup);
        }
        catch
        {
            if (replacementSucceeded && File.Exists(backup))
            {
                try
                {
                    SetCommitPhase(CfsCommitPhase.RestoringBackup, candidate, backup);
                    if (_failBackupRestore)
                        throw new IOException("Injected replacement-backup restore failure.");
                    File.Replace(backup, _archivePath, null, ignoreMetadataErrors: true);
                    var restoredLock = AcquireArchiveLock(_archivePath);
                    if (!CfsArchive.Validate(_archivePath, cancellationToken: cancellationToken).IsValid)
                    {
                        restoredLock.Dispose();
                        throw new CfsArchiveException("The restored CFS backup failed validation.");
                    }
                    AdoptArchiveLock(restoredLock);
                    _authoritativeIdentity = CfsArchiveIdentity.Create(_archivePath);
                }
                catch
                {
                    SetCommitPhase(CfsCommitPhase.RecoveryRequired, candidate, backup);
                    throw new BrokerRequestException(CfsBrokerErrorCodes.RecoveryRequired,
                        "CFS could not prove restoration after a failed archive replacement. The candidate, backup, and recovery record were preserved.");
                }
            }
            else
            {
                SetCommitPhase(CfsCommitPhase.RecoveryRequired, candidate, backup);
            }
            throw;
        }
        finally
        {
            replacementLock?.Dispose();
            // A failed candidate is evidence for recovery; only remove it after a fully
            // verified commit. File.Replace consumes it on the successful path.
            if (!replacementSucceeded && File.Exists(candidate)) { }
        }
    }, cancellationToken);

    private void SetCommitPhase(CfsCommitPhase phase, string? candidatePath = null, string? backupPath = null)
    {
        _transaction?.MarkCommitPhase(phase, candidatePath, backupPath);
        _commitPhaseObserver?.Invoke(phase);
    }

    private static FileStream AcquireArchiveLock(string archivePath)
    {
        try
        {
            // Permit readers and CFS's same-volume atomic replacement, but deny any
            // writer for the lifetime of the writable session.
            return new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 4096,
                options: FileOptions.None);
        }
        catch (IOException ex) when (CfsFileInUseException.IsSharingOrLockViolation(ex))
        {
            throw new BrokerRequestException(CfsBrokerErrorCodes.FileInUse,
                "The CFS archive is already open for writing by another process. Close that writer and retry.", ex);
        }
    }

    private void EnsureCommitSpace(string? existingCandidate = null)
    {
        try
        {
            const long metadataAllowance = 1024L * 1024;
            var archiveBytes = new FileInfo(_archivePath).Length;
            long mountedBytes = 0;
            foreach (var path in Directory.EnumerateFiles(MountPath, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(path), CfsFolderSync.MountMarkerFileName, StringComparison.OrdinalIgnoreCase))
                    continue;
                mountedBytes = checked(mountedBytes + new FileInfo(path).Length);
            }
            var allocatedRecoveryBytes = CfsRecoveryStoragePolicy.MeasureAllocatedBytes(MountPath);
            if (allocatedRecoveryBytes > _recoveryStorageLimitBytes)
                throw new BrokerRequestException(CfsBrokerErrorCodes.InsufficientSpace,
                    $"CFS recovery storage exceeds its configured {FormatGiB(_recoveryStorageLimitBytes)} GiB ceiling. The original archive and pending edits were preserved.");

            var candidateBytes = existingCandidate is not null && File.Exists(existingCandidate)
                ? new FileInfo(existingCandidate).Length
                : checked(archiveBytes + mountedBytes + metadataAllowance);
            var compressionOverhead = existingCandidate is null ? mountedBytes : 0;
            var estimatedFootprint = checked(candidateBytes + compressionOverhead + archiveBytes + metadataAllowance);
            var safetyMargin = Math.Max(1024L * 1024 * 1024, checked(estimatedFootprint / 10));
            var stillRequired = checked(
                (existingCandidate is null ? candidateBytes : 0)
                + compressionOverhead
                + archiveBytes
                + metadataAllowance
                + safetyMargin);
            var available = _availableFreeSpace(_archivePath);
            if (available < 0 || available < stillRequired)
                throw new BrokerRequestException(CfsBrokerErrorCodes.InsufficientSpace,
                    $"CFS needs at least {FormatGiB(stillRequired)} GiB of free space for a transactional commit, but only {FormatGiB(Math.Max(0, available))} GiB is available. The original archive and pending edits were preserved.");
        }
        catch (OverflowException ex)
        {
            throw new BrokerRequestException(CfsBrokerErrorCodes.InsufficientSpace,
                "CFS could not represent the transactional disk-space estimate safely. The original archive and pending edits were preserved.", ex);
        }
    }

    private static long AvailableFreeSpace(string archivePath)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(archivePath))
            ?? throw new BrokerRequestException(CfsBrokerErrorCodes.InsufficientSpace, "CFS could not identify the archive volume for disk-space preflight.");
        return new DriveInfo(root).AvailableFreeSpace;
    }

    private static string FormatGiB(long bytes) =>
        (bytes / (1024d * 1024d * 1024d)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    private void AdoptArchiveLock(FileStream replacement)
    {
        var previous = _archiveLock;
        _archiveLock = replacement;
        previous?.Dispose();
    }

    private void EnsureAuthoritativeIdentity()
    {
        if (_authoritativeIdentity is null) return;
        var current = CfsArchiveIdentity.Create(_archivePath);
        if (!_authoritativeIdentity.MatchesAuthoritativeState(current))
            throw new BrokerRequestException(CfsBrokerErrorCodes.ExternalModification,
                "The archive was replaced or modified outside CFS while this writable session was active. Pending edits were preserved and were not committed.");
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) => _persistence.FlushAsync(cancellationToken);

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await _closeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            // FileSystemWatcher delivery is asynchronous. A shell close can arrive before
            // the final write/rename notification, so force a generation that scans the
            // mounted namespace instead of treating the current generation as already clean.
            var closeGeneration = _persistence.MarkDirty();
            _transaction?.MarkCommitPending(closeGeneration);
            for (var attempt = 0; ; attempt++)
            {
                await _persistence.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (!_persistence.Status.IsDirty) break;
                if (attempt >= 15)
                    throw new CfsArchiveException("Close CFS could not reach a stable committed generation. The live session and mount were preserved.");
            }
            if (_failCloseValidation?.Invoke() == true)
                throw new CfsArchiveException("Injected close validation failure.");
            var validation = CfsArchive.Validate(_archivePath, cancellationToken: cancellationToken);
            if (!validation.IsValid) throw new CfsArchiveException("Close CFS validation failed. The live session and mount were preserved.");
            _session.PermanentlyDelete(cancellationToken: cancellationToken);
            _transaction?.Delete();
            _session.ContentChanged -= OnContentChanged;
            await _persistence.DisposeAsync().ConfigureAwait(false);
            _archiveLock?.Dispose();
            _archiveLock = null;
            _disposed = true;
        }
        finally { _closeGate.Release(); }
    }

    public async Task DiscardAsync(CancellationToken cancellationToken = default)
    {
        await _closeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            await _persistence.DiscardAsync().ConfigureAwait(false);
            _session.PermanentlyDelete(cancellationToken: cancellationToken);
            _transaction?.Delete();
            _session.ContentChanged -= OnContentChanged;
            _archiveLock?.Dispose();
            _archiveLock = null;
            _disposed = true;
        }
        finally { _closeGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
    }
}

public static class CfsRecoveryStoragePolicy
{
    public const long DefaultLimitBytes = 100L * 1024 * 1024 * 1024;

    public static long ResolveLimit(string? configuredValue)
    {
        if (string.IsNullOrWhiteSpace(configuredValue)) return DefaultLimitBytes;
        if (!long.TryParse(configuredValue, out var parsed) || parsed <= 0 || parsed > DefaultLimitBytes)
            throw new BrokerRequestException(CfsBrokerErrorCodes.InvalidRequest,
                $"CFS recovery storage must be configured between 1 byte and {DefaultLimitBytes} bytes.");
        return parsed;
    }

    public static void EnsureWithinLimit(string rootPath, long limitBytes)
    {
        var used = MeasureAllocatedBytes(rootPath);
        if (used >= limitBytes)
            throw new BrokerRequestException(CfsBrokerErrorCodes.InsufficientSpace,
                "CFS recovery storage has reached its configured ceiling. Resolve or discard verified recovery sessions before opening another writable archive.");
    }

    public static long MeasureAllocatedBytes(string rootPath)
    {
        if (!Directory.Exists(rootPath)) return 0;
        long total = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };
        foreach (var path in Directory.EnumerateFiles(rootPath, "*", options))
            total = checked(total + AllocatedFileBytes(path));
        return total;
    }

    private static long AllocatedFileBytes(string path)
    {
        if (!OperatingSystem.IsWindows()) return new FileInfo(path).Length;
        var low = GetCompressedFileSize(path, out var high);
        if (low == uint.MaxValue && Marshal.GetLastWin32Error() != 0)
            return new FileInfo(path).Length;
        return checked((long)(((ulong)high << 32) | low));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSize(string fileName, out uint fileSizeHigh);
}

public sealed class CfsReadOnlyBrokerSession(CfsMountSession session) : ICfsBrokerSession, ICfsClosableBrokerSession
{
    private readonly SemaphoreSlim _closeGate = new(1, 1);
    private bool _closed;

    public string MountPath => session.FolderPath;

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await _closeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_closed) return;
            session.PermanentlyDelete(cancellationToken: cancellationToken);
            _closed = true;
        }
        finally { _closeGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _closeGate.Dispose();
    }
}
