using Cfs.Core;

namespace Cfs.Broker;

public enum CfsPersistenceState
{
    Clean,
    Dirty,
    WaitingForQuietPeriod,
    Committing,
    Failed,
    Stopped
}

public sealed record CfsPersistenceStatus(
    CfsPersistenceState State,
    bool IsDirty,
    long DirtyGeneration,
    long CommittedGeneration,
    DateTimeOffset? LastCommitUtc,
    string? LastError);

/// <summary>
/// Debounces filesystem changes and serializes transactional commit attempts. A failed
/// commit never advances the committed generation, so the caller can preserve and retry
/// the recoverable mount instead of treating the dirty data as saved.
/// </summary>
public sealed class CfsAutomaticPersistence : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _commitGate = new(1, 1);
    private readonly Func<CancellationToken, Task> _commit;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _quietPeriod;
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private long _dirtyGeneration;
    private long _committedGeneration;
    private CfsPersistenceState _state = CfsPersistenceState.Clean;
    private DateTimeOffset? _lastCommitUtc;
    private string? _lastError;
    private bool _disposed;

    public CfsAutomaticPersistence(
        Func<CancellationToken, Task> commit,
        TimeSpan quietPeriod,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _commit = commit ?? throw new ArgumentNullException(nameof(commit));
        if (quietPeriod <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(quietPeriod));
        _quietPeriod = quietPeriod;
        _delay = delay ?? Task.Delay;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public CfsPersistenceStatus Status
    {
        get
        {
            lock (_sync)
                return new(_state, _dirtyGeneration > _committedGeneration, _dirtyGeneration,
                    _committedGeneration, _lastCommitUtc, _lastError);
        }
    }

    public long MarkDirty()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var generation = ++_dirtyGeneration;
            _state = CfsPersistenceState.Dirty;
            if (_worker is null || _worker.IsCompleted)
                _worker = Task.Run(RunWorkerAsync);
            return generation;
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        long target;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            target = _dirtyGeneration;
            if (target <= _committedGeneration) return;
        }
        await CommitThroughAsync(target, cancellationToken).ConfigureAwait(false);
    }

    public async Task WaitForIdleAsync()
    {
        Task? worker;
        lock (_sync) worker = _worker;
        if (worker is not null) await worker.ConfigureAwait(false);
    }

    private async Task RunWorkerAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                long target;
                lock (_sync)
                {
                    target = _dirtyGeneration;
                    if (target <= _committedGeneration)
                    {
                        _state = CfsPersistenceState.Clean;
                        return;
                    }
                    _state = CfsPersistenceState.WaitingForQuietPeriod;
                }

                await _delay(_quietPeriod, _stop.Token).ConfigureAwait(false);
                lock (_sync)
                {
                    if (_dirtyGeneration != target) continue;
                }

                try { await CommitThroughAsync(target, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    try { CfsDiagnostics.Logger.WriteException("broker.persistence", ex); } catch { }
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task CommitThroughAsync(long target, CancellationToken cancellationToken)
    {
        await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (target <= _committedGeneration) return;
                _state = CfsPersistenceState.Committing;
            }

            try
            {
                await _commit(cancellationToken).ConfigureAwait(false);
                lock (_sync)
                {
                    _committedGeneration = Math.Max(_committedGeneration, target);
                    _lastCommitUtc = _utcNow();
                    _lastError = null;
                    _state = _dirtyGeneration > _committedGeneration ? CfsPersistenceState.Dirty : CfsPersistenceState.Clean;
                }
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    _lastError = $"{ex.GetType().Name}: automatic commit failed; see the CFS diagnostic log.";
                    _state = CfsPersistenceState.Failed;
                }
                throw;
            }
        }
        finally { _commitGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        // A dirty generation is never abandoned by teardown. If the flush fails, leave
        // this object active, failed, and retryable so its owning session can preserve
        // the mount and report the failure rather than deleting recoverable data.
        lock (_sync) { if (_disposed) return; }
        await FlushAsync().ConfigureAwait(false);
        Task? worker;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            worker = _worker;
        }
        if (worker is not null)
        {
            try { await worker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        lock (_sync) _state = CfsPersistenceState.Stopped;
        _stop.Dispose();
        _commitGate.Dispose();
    }
}
