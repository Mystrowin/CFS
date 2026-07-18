using System.Collections.Concurrent;
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

public sealed record CfsBrokerFlushResult(bool Success, string? MountPath = null, string? Error = null, CfsPersistenceStatus? Status = null);
public sealed record CfsBrokerCloseResult(bool Success, bool Found, string? MountPath = null, string? Error = null, CfsPersistenceStatus? Status = null);

public sealed record BrokerOpenResult(string CanonicalArchiveKey, string MountPath);

public sealed class CfsBrokerSessionRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<ICfsBrokerSession>>> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<CfsArchiveIdentity, string, CancellationToken, Task<ICfsBrokerSession>> _sessionFactory;
    private readonly string _mountRoot;
    private int _createdSessionCount;

    public CfsBrokerSessionRegistry(
        string mountRoot,
        Func<CfsArchiveIdentity, string, CancellationToken, Task<ICfsBrokerSession>> sessionFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mountRoot);
        _mountRoot = Path.GetFullPath(mountRoot);
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
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
                if (await pair.Value.Value.ConfigureAwait(false) is ICfsPersistentBrokerSession persistent)
                    statuses.Add(persistent.PersistenceStatus);
            }
            catch { }
        }
        if (statuses.Count == 0) return new(CfsPersistenceState.Clean, false, 0, 0, null, null);
        return statuses.OrderByDescending(status => PersistenceSeverity(status.State)).First() with
        {
            IsDirty = statuses.Any(status => status.IsDirty),
            DirtyGeneration = statuses.Sum(status => status.DirtyGeneration),
            CommittedGeneration = statuses.Sum(status => status.CommittedGeneration)
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
                return new(false, session.MountPath, "Automatic commit failed; the recoverable mount was preserved. See the CFS diagnostic log.", persistent.PersistenceStatus);
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
                    "Close CFS could not complete. The session and marked mount were preserved; close open files and inspect the CFS diagnostic log before retrying.",
                    session is ICfsPersistentBrokerSession persistent ? persistent.PersistenceStatus : null);
            }
        }
        finally { gate.Release(); }
    }

    private async Task<ICfsBrokerSession> CreateSessionAsync(CfsArchiveIdentity identity, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_mountRoot);
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

internal sealed class CfsBrokerMountedSession : ICfsBrokerSession, ICfsPersistentBrokerSession, ICfsClosableBrokerSession
{
    private readonly string _archivePath;
    private readonly CfsMountSession _session;
    private readonly CfsAutomaticPersistence _persistence;
    private readonly Func<CancellationToken, Task> _commitOperation;
    private readonly CfsSessionTransaction? _transaction;
    private readonly SemaphoreSlim _closeGate = new(1, 1);
    private CfsArchiveIdentity? _authoritativeIdentity;
    private bool _disposed;

    public CfsBrokerMountedSession(string archivePath, CfsMountSession session, TimeSpan? quietPeriod = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<CancellationToken, Task>? commit = null,
        CfsSessionTransaction? transaction = null, CfsArchiveIdentity? authoritativeIdentity = null)
    {
        _archivePath = Path.GetFullPath(archivePath);
        _session = session;
        _transaction = transaction;
        _authoritativeIdentity = authoritativeIdentity;
        _commitOperation = commit ?? CommitCoreAsync;
        _persistence = new CfsAutomaticPersistence(CommitWithTransactionAsync, quietPeriod ?? TimeSpan.FromMilliseconds(750), delay);
        _session.ContentChanged += OnContentChanged;
    }
    public string MountPath => _session.FolderPath;
    public CfsPersistenceStatus PersistenceStatus => _persistence.Status;

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
        await _commitOperation(cancellationToken).ConfigureAwait(false);
        if (!CfsArchive.Validate(_archivePath, cancellationToken: cancellationToken).IsValid)
            throw new CfsArchiveException("The automatic commit did not produce a valid CFS archive.");
        _authoritativeIdentity = CfsArchiveIdentity.Create(_archivePath);
        _transaction?.MarkCommitted(generation);
    }

    private void EnsureAuthoritativeIdentity()
    {
        if (_authoritativeIdentity is null) return;
        var current = CfsArchiveIdentity.Create(_archivePath);
        if (!_authoritativeIdentity.RepresentsSameFile(current))
            throw new BrokerRequestException(CfsBrokerErrorCodes.ExternalModification,
                "The archive was replaced outside CFS while this writable session was active. Pending edits were preserved and were not committed.");
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
            var validation = CfsArchive.Validate(_archivePath, cancellationToken: cancellationToken);
            if (!validation.IsValid) throw new CfsArchiveException("Close CFS validation failed. The live session and mount were preserved.");
            _session.PermanentlyDelete(cancellationToken: cancellationToken);
            _transaction?.Delete();
            _session.ContentChanged -= OnContentChanged;
            await _persistence.DisposeAsync().ConfigureAwait(false);
            _disposed = true;
        }
        finally { _closeGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
    }
}
