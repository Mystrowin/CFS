using System.Collections.Concurrent;
using Cfs.Core;

namespace Cfs.Broker;

public enum CfsBrokerOperationState { Queued, Running, Cancelling, Completed, Failed, Cancelled, RecoveryRequired }

public sealed record CfsBrokerOperationStatus(
    string OperationId,
    string CancellationId,
    CfsBrokerOperationState State,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string? ResultCode = null,
    string? Phase = null,
    string? CurrentItem = null,
    long CompletedItems = 0,
    long? TotalItems = null,
    long CompletedBytes = 0,
    long? TotalBytes = null,
    double? Percent = null,
    bool CanCancel = true);

/// <summary>Bounded per-broker operation status and cancellation capability store.</summary>
public sealed class CfsBrokerOperationRegistry
{
    private sealed class Entry(CfsBrokerOperationStatus status)
    {
        public object Gate { get; } = new();
        public CfsBrokerOperationStatus Status { get; set; } = status;
        public CancellationTokenSource Cancellation { get; } = new();
    }

    private readonly ConcurrentDictionary<string, Entry> _operations = new(StringComparer.Ordinal);
    private readonly TimeSpan _retention = TimeSpan.FromHours(24);

    public CfsBrokerOperationStatus Start(string? operationId = null, string? cancellationId = null)
    {
        Prune();
        var now = DateTimeOffset.UtcNow;
        var status = new CfsBrokerOperationStatus(operationId ?? Guid.NewGuid().ToString("N"), cancellationId ?? Guid.NewGuid().ToString("N"), CfsBrokerOperationState.Running, now, now);
        if (!_operations.TryAdd(status.OperationId, new Entry(status))) throw new BrokerRequestException(CfsBrokerErrorCodes.InvalidRequest, "Operation ID is already active.");
        return status;
    }

    public bool TryGet(string operationId, out CfsBrokerOperationStatus? status)
    {
        Prune();
        if (_operations.TryGetValue(operationId, out var value)) { lock (value.Gate) status = value.Status; return true; }
        status = null; return false;
    }

    public bool TryGetCancellationToken(string operationId, out CancellationToken token)
    {
        if (_operations.TryGetValue(operationId, out var value)) { token = value.Cancellation.Token; return true; }
        token = default; return false;
    }

    public IProgress<CfsProgress> CreateProgressReporter(string operationId) =>
        new InlineProgress(value => Report(operationId, value));

    public void Report(string operationId, CfsProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (!_operations.TryGetValue(operationId, out var value)) return;
        lock (value.Gate)
        {
            if (IsTerminal(value.Status.State)) return;
            value.Status = value.Status with
            {
                Phase = progress.Phase,
                CurrentItem = progress.CurrentPath,
                CompletedItems = progress.CompletedItems,
                TotalItems = progress.TotalItems,
                CompletedBytes = progress.CompletedBytes,
                TotalBytes = progress.TotalBytes,
                Percent = CalculatePercent(progress),
                UpdatedUtc = DateTimeOffset.UtcNow
            };
        }
    }

    public bool Cancel(string operationId, string cancellationId)
    {
        if (!_operations.TryGetValue(operationId, out var value)) return false;
        lock (value.Gate)
        {
            if (!string.Equals(value.Status.CancellationId, cancellationId, StringComparison.Ordinal) || IsTerminal(value.Status.State)) return false;
            value.Cancellation.Cancel();
            value.Status = value.Status with { State = CfsBrokerOperationState.Cancelling, UpdatedUtc = DateTimeOffset.UtcNow };
            return true;
        }
    }

    public void Complete(string operationId, CfsBrokerOperationState state = CfsBrokerOperationState.Completed, string? resultCode = null)
    {
        if (_operations.TryGetValue(operationId, out var value))
            lock (value.Gate) value.Status = value.Status with
            {
                State = state,
                UpdatedUtc = DateTimeOffset.UtcNow,
                ResultCode = resultCode,
                CanCancel = false,
                Percent = state == CfsBrokerOperationState.Completed ? 100 : value.Status.Percent
            };
    }

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - _retention;
        foreach (var pair in _operations.Where(pair => IsExpired(pair.Value, cutoff)))
            if (_operations.TryRemove(pair.Key, out var removed)) removed.Cancellation.Dispose();
    }

    private static bool IsTerminal(CfsBrokerOperationState state) => state is CfsBrokerOperationState.Completed or CfsBrokerOperationState.Failed or CfsBrokerOperationState.Cancelled or CfsBrokerOperationState.RecoveryRequired;

    private static bool IsExpired(Entry entry, DateTimeOffset cutoff)
    {
        lock (entry.Gate) return entry.Status.UpdatedUtc < cutoff;
    }

    private static double? CalculatePercent(CfsProgress progress)
    {
        if (progress.TotalBytes is > 0)
            return Math.Clamp(progress.CompletedBytes * 100d / progress.TotalBytes.Value, 0, 100);
        if (progress.TotalItems is > 0)
            return Math.Clamp(progress.CompletedItems * 100d / progress.TotalItems.Value, 0, 100);
        return null;
    }

    private sealed class InlineProgress(Action<CfsProgress> report) : IProgress<CfsProgress>
    {
        public void Report(CfsProgress value) => report(value);
    }
}
