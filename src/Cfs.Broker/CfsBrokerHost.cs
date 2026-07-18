using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Cfs.Core;

namespace Cfs.Broker;

public interface ICfsExplorerLauncher
{
    void OpenFolder(string folderPath);
}

public sealed class CfsExplorerLauncher : ICfsExplorerLauncher
{
    public void OpenFolder(string folderPath) => Process.Start(new ProcessStartInfo
    {
        FileName = "explorer.exe",
        ArgumentList = { folderPath },
        UseShellExecute = false
    });
}

public sealed class CfsBrokerRequestHandler
{
    private readonly CfsBrokerSessionRegistry _sessions;
    private readonly ICfsExplorerLauncher _explorer;
    private readonly bool _allowControlledShutdown;
    private readonly Action _requestShutdown;
    private readonly CfsCreationOperations? _creationOperations;
    private readonly CfsBrokerOperationRegistry _operations;
    private readonly string _recoveryRoot;
    private readonly CfsBrokerSessionRegistry? _readOnlySessions;
    private readonly CfsMutationConcurrencyGate _mutationGate;

    public CfsBrokerRequestHandler(CfsBrokerSessionRegistry sessions, ICfsExplorerLauncher explorer, bool allowControlledShutdown, Action requestShutdown, CfsCreationOperations? creationOperations = null, CfsBrokerOperationRegistry? operations = null, string? recoveryRoot = null, CfsBrokerSessionRegistry? readOnlySessions = null, CfsMutationConcurrencyGate? mutationGate = null)
    {
        _sessions = sessions;
        _explorer = explorer;
        _allowControlledShutdown = allowControlledShutdown;
        _requestShutdown = requestShutdown;
        _creationOperations = creationOperations;
        _operations = operations ?? new CfsBrokerOperationRegistry();
        _recoveryRoot = Path.GetFullPath(recoveryRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CFS", "Sessions"));
        _readOnlySessions = readOnlySessions;
        _mutationGate = mutationGate ?? new CfsMutationConcurrencyGate();
    }

    public async Task<BrokerResponse> HandleAsync(BrokerRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Version < CfsBrokerProtocol.MinimumSupportedVersion || request.Version > CfsBrokerProtocol.CurrentVersion)
            return Error(CfsBrokerErrorCodes.ProtocolUnsupported, $"Broker protocol version {request.Version} is not supported.", request);

        if (request.Version >= 2 && string.IsNullOrWhiteSpace(request.RequestId))
            return Error(CfsBrokerErrorCodes.InvalidRequest, "Protocol v2 requests require a request ID.", request);

        var command = request.Command?.Trim().ToLowerInvariant();
        using var mutationLease = IsMutatingCommand(command)
            ? await _mutationGate.AcquireAsync(cancellationToken).ConfigureAwait(false)
            : null;
        switch (command)
        {
            case "open":
                try
                {
                    var identity = CfsArchiveIdentity.Create(request.ArchivePath ?? string.Empty);
                    CfsDiagnostics.Logger.WritePathEvent("broker.open", identity.FullPath, "starting");
                    var result = await _sessions.OpenAsync(identity, cancellationToken).ConfigureAwait(false);
                    _explorer.OpenFolder(result.MountPath);
                    CfsDiagnostics.Logger.WritePathEvent("broker.open", identity.FullPath, "success");
                    return Success("Archive session is ready.", result.CanonicalArchiveKey, result.MountPath, request: request);
                }
                catch (BrokerRequestException ex) { return Error(ex.ErrorCode, ex.Message, request); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    CfsDiagnostics.Logger.WriteException("broker.open", ex);
                    return Error("open-failed", "CFS could not open the archive on demand. Check ProjFS availability and the CFS diagnostic log.", request);
                }
            case "open-readonly":
                try
                {
                    if (_readOnlySessions is null)
                        return Error(CfsBrokerErrorCodes.InvalidRequest, "Read-only compatibility mode is not configured.", request);
                    var identity = CfsArchiveIdentity.Create(request.ArchivePath ?? string.Empty);
                    var result = await _readOnlySessions.OpenAsync(identity, cancellationToken).ConfigureAwait(false);
                    _explorer.OpenFolder(result.MountPath);
                    return Success(
                        "Archive opened in read-only compatibility mode. This is a full extraction; changes are discarded on Close CFS and never replace the source archive.",
                        result.CanonicalArchiveKey, result.MountPath,
                        warning: "Read-only compatibility mode: no changes are written back to the source archive.",
                        request: request);
                }
                catch (BrokerRequestException ex) { return Error(ex.ErrorCode, ex.Message, request); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CfsArchiveException)
                {
                    return Error("open-readonly-failed", ex.Message, request);
                }
            case "status":
            case "query":
                try
                {
                    string? key = null;
                    if (!string.IsNullOrWhiteSpace(request.ArchivePath)) key = CfsArchiveIdentity.Create(request.ArchivePath).Key;
                    var persistence = await _sessions.GetPersistenceStatusAsync(key).ConfigureAwait(false);
                    return Success("Broker is running.", persistence: persistence, request: request);
                }
                catch (BrokerRequestException ex) { return Error(ex.ErrorCode, ex.Message, request); }
            case "recovery-status":
            case "recover":
            case "discard-recovery":
                try
                {
                    var identity = CfsArchiveIdentity.Create(request.ArchivePath ?? string.Empty);
                    var mountPath = Path.Combine(_recoveryRoot, identity.MountKey);
                    if (command == "discard-recovery")
                    {
                        var discarded = CfsSessionTransaction.DiscardPendingRecovery(identity, mountPath);
                        return Success(discarded.Message, identity.Key, recovery: discarded, request: request);
                    }
                    var recovery = CfsSessionTransaction.InspectPendingRecovery(identity, mountPath);
                    if (!recovery.Found)
                        return Error(CfsBrokerErrorCodes.SessionNotFound, recovery.Message, request);
                    if (!recovery.OwnershipVerified)
                        return Error(CfsBrokerErrorCodes.RecoveryRequired, recovery.Message, request, mountPath: recovery.MountPath);
                    if (command == "recover")
                        _explorer.OpenFolder(recovery.MountPath!);
                    return Success(
                        command == "recover"
                            ? "The verified recovery workspace was opened for review. The original archive was not replaced."
                            : recovery.Message,
                        identity.Key, recovery.MountPath, recovery: recovery, request: request);
                }
                catch (BrokerRequestException ex) { return Error(ex.ErrorCode, ex.Message, request); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CfsArchiveException)
                {
                    return Error(CfsBrokerErrorCodes.RecoveryRequired, ex.Message, request);
                }
            case "operation-status":
                if (string.IsNullOrWhiteSpace(request.OperationId) || !_operations.TryGet(request.OperationId, out var operation))
                    return Error(CfsBrokerErrorCodes.InvalidRequest, "The requested CFS operation was not found.", request);
                return Success("Operation status is available.", request: request, operation: operation);
            case "cancel":
                if (string.IsNullOrWhiteSpace(request.OperationId) || string.IsNullOrWhiteSpace(request.CancellationId) || !_operations.Cancel(request.OperationId, request.CancellationId))
                    return Error(CfsBrokerErrorCodes.AccessDenied, "The CFS operation cancellation capability is invalid or expired.", request);
                return Success("Cancellation was requested.", request: request, operationState: CfsBrokerOperationState.Cancelling.ToString(), operationId: request.OperationId, cancellationId: request.CancellationId);
            case "close":
                try
                {
                    var identity = CfsArchiveIdentity.Create(request.ArchivePath ?? string.Empty);
                    var result = await _sessions.CloseAsync(identity, cancellationToken).ConfigureAwait(false);
                    if (!result.Found && _readOnlySessions is not null)
                        result = await _readOnlySessions.CloseAsync(identity, cancellationToken).ConfigureAwait(false);
                    if (!result.Found) return Error(CfsBrokerErrorCodes.SessionNotFound, result.Error!, request);
                    if (!result.Success) return Error(result.ErrorCode ?? "close-failed", result.Error!, request: request, persistence: result.Status, mountPath: result.MountPath);
                    return Success("Close CFS completed. Pending edits were committed, the archive validated, and the mounted folder was removed.",
                        identity.Key, result.MountPath, persistence: result.Status, request: request);
                }
                catch (BrokerRequestException ex) { return Error(ex.ErrorCode, ex.Message, request); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            case "commit":
            case "discard":
                try
                {
                    var identity = CfsArchiveIdentity.Create(request.ArchivePath ?? string.Empty);
                    var result = command == "commit"
                        ? await _sessions.CommitAsync(identity, cancellationToken).ConfigureAwait(false)
                        : await _sessions.DiscardAsync(identity, cancellationToken).ConfigureAwait(false);
                    if (!result.Found) return Error(CfsBrokerErrorCodes.SessionNotFound, result.Error!, request);
                    if (!result.Success) return Error(
                        result.ErrorCode ?? (command == "commit" ? CfsBrokerErrorCodes.CommitFailed : CfsBrokerErrorCodes.DiscardFailed),
                        result.Error!, request: request, persistence: result.Status, mountPath: result.MountPath);
                    return Success(
                        command == "commit"
                            ? "Pending CFS changes were committed and the workspace remains mounted."
                            : "Pending CFS changes were discarded and the workspace was unmounted.",
                        identity.Key, result.MountPath, persistence: result.Status, request: request);
                }
                catch (BrokerRequestException ex) { return Error(ex.ErrorCode, ex.Message, request); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            case "create-empty":
                try
                {
                    if (_creationOperations is null) return Error("operation-unavailable", "Archive creation is not configured in this broker host.");
                    var output = _creationOperations.CreateEmpty(request.TargetPath ?? string.Empty);
                    return Success("Empty CFS archive created.", outputPath: output, request: request);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or CfsArchiveException)
                {
                    return Error("create-empty-failed", ex.Message, request);
                }
            case "compress":
                CfsBrokerOperationStatus? trackedOperation = null;
                try
                {
                    if (_creationOperations is null) return Error("operation-unavailable", "Folder compression is not configured in this broker host.");
                    trackedOperation = _operations.Start(request.OperationId, request.CancellationId);
                    if (!_operations.TryGetCancellationToken(trackedOperation.OperationId, out var operationToken)) throw new InvalidOperationException("CFS operation registration was lost.");
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operationToken);
                    var result = await _creationOperations.CompressFolderAsync(
                        request.SourcePath ?? string.Empty,
                        _operations.CreateProgressReporter(trackedOperation.OperationId),
                        linked.Token).ConfigureAwait(false);
                    _operations.Complete(trackedOperation.OperationId);
                    return Success(result.Warning is null ? "Folder compressed to CFS." : "Folder compressed to CFS with a cleanup warning.",
                        outputPath: result.OutputPath, warning: result.Warning, request: request, operationState: CfsBrokerOperationState.Completed.ToString(), operationId: trackedOperation.OperationId, cancellationId: trackedOperation.CancellationId);
                }
                catch (OperationCanceledException)
                {
                    // A broker-request deadline is owned by the pipe/direct host,
                    // which must return its stable request-timeout contract. A
                    // separate operation cancellation remains CFS_E_CANCELLED.
                    if (cancellationToken.IsCancellationRequested) throw;
                    if (trackedOperation is not null)
                        _operations.Complete(trackedOperation.OperationId, CfsBrokerOperationState.Cancelled, CfsBrokerErrorCodes.Cancelled);
                    return Error(CfsBrokerErrorCodes.Cancelled, "The CFS compression operation was cancelled.", request,
                        operationState: CfsBrokerOperationState.Cancelled.ToString(), operationId: trackedOperation?.OperationId, cancellationId: trackedOperation?.CancellationId);
                }
                catch (BrokerRequestException ex)
                {
                    if (trackedOperation is not null)
                        _operations.Complete(trackedOperation.OperationId, CfsBrokerOperationState.Failed, ex.ErrorCode);
                    return Error(ex.ErrorCode, ex.Message, request, operationState: CfsBrokerOperationState.Failed.ToString(), operationId: trackedOperation?.OperationId, cancellationId: trackedOperation?.CancellationId);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or CfsArchiveException)
                {
                    if (trackedOperation is not null)
                        _operations.Complete(trackedOperation.OperationId, CfsBrokerOperationState.Failed, "compress-failed");
                    return Error("compress-failed", ex.Message, request, operationState: CfsBrokerOperationState.Failed.ToString(), operationId: trackedOperation?.OperationId, cancellationId: trackedOperation?.CancellationId);
                }
            case "extract":
                CfsBrokerOperationStatus? trackedExtraction = null;
                try
                {
                    if (_creationOperations is null) return Error("operation-unavailable", "Archive extraction is not configured in this broker host.");
                    trackedExtraction = _operations.Start(request.OperationId, request.CancellationId);
                    if (!_operations.TryGetCancellationToken(trackedExtraction.OperationId, out var operationToken)) throw new InvalidOperationException("CFS operation registration was lost.");
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operationToken);
                    var result = await _creationOperations.ExtractArchiveAsync(
                        request.ArchivePath ?? string.Empty,
                        _operations.CreateProgressReporter(trackedExtraction.OperationId),
                        linked.Token).ConfigureAwait(false);
                    _operations.Complete(trackedExtraction.OperationId);
                    return Success("Archive extracted to a new folder.", outputPath: result.OutputPath, request: request,
                        operationState: CfsBrokerOperationState.Completed.ToString(), operationId: trackedExtraction.OperationId, cancellationId: trackedExtraction.CancellationId);
                }
                catch (CfsPartialExtractionException ex)
                {
                    if (trackedExtraction is not null)
                        _operations.Complete(trackedExtraction.OperationId, CfsBrokerOperationState.Cancelled, CfsBrokerErrorCodes.Cancelled);
                    return Error(CfsBrokerErrorCodes.Cancelled, ex.Message, request, outputPath: ex.OutputPath,
                        operationState: CfsBrokerOperationState.Cancelled.ToString(), operationId: trackedExtraction?.OperationId, cancellationId: trackedExtraction?.CancellationId);
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested) throw;
                    if (trackedExtraction is not null)
                        _operations.Complete(trackedExtraction.OperationId, CfsBrokerOperationState.Cancelled, CfsBrokerErrorCodes.Cancelled);
                    return Error(CfsBrokerErrorCodes.Cancelled, "The CFS extraction operation was cancelled.", request,
                        operationState: CfsBrokerOperationState.Cancelled.ToString(), operationId: trackedExtraction?.OperationId, cancellationId: trackedExtraction?.CancellationId);
                }
                catch (BrokerRequestException ex)
                {
                    if (trackedExtraction is not null)
                        _operations.Complete(trackedExtraction.OperationId, CfsBrokerOperationState.Failed, ex.ErrorCode);
                    return Error(ex.ErrorCode, ex.Message, request, operationState: CfsBrokerOperationState.Failed.ToString(), operationId: trackedExtraction?.OperationId, cancellationId: trackedExtraction?.CancellationId);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or CfsArchiveException)
                {
                    if (trackedExtraction is not null)
                        _operations.Complete(trackedExtraction.OperationId, CfsBrokerOperationState.Failed, "extract-failed");
                    return Error("extract-failed", ex.Message, request, operationState: CfsBrokerOperationState.Failed.ToString(), operationId: trackedExtraction?.OperationId, cancellationId: trackedExtraction?.CancellationId);
                }
            case "shutdown":
                if (!_allowControlledShutdown)
                    return Error("shutdown-not-allowed", "Broker shutdown is available only to controlled test teardown.", request);
                var flush = await _sessions.FlushAllAsync(cancellationToken).ConfigureAwait(false);
                if (!flush.Success)
                    return Error(flush.ErrorCode ?? CfsBrokerErrorCodes.CommitFailed, flush.Error!, request: request, persistence: flush.Status, mountPath: flush.MountPath);
                var response = Success("Controlled broker shutdown accepted.", persistence: flush.Status, request: request);
                _requestShutdown();
                return response;
            default:
            return Error(CfsBrokerErrorCodes.InvalidRequest, "Unknown broker command. Supported commands are open, open-readonly, close, commit, discard, recover, recovery-status, discard-recovery, create-empty, compress, extract, status, query, operation-status, cancel, and controlled test shutdown.", request);
        }
    }

    internal static bool IsMutatingCommand(string? command) => command is
        "open" or "close" or "commit" or "discard" or "discard-recovery" or
        "create-empty" or "compress" or "extract" or "shutdown";

    private BrokerResponse Success(string message, string? key = null, string? mountPath = null, string? outputPath = null, string? warning = null, CfsPersistenceStatus? persistence = null, BrokerRequest? request = null, string? operationState = null, string? operationResultCode = null, string? operationId = null, string? cancellationId = null, CfsBrokerOperationStatus? operation = null, CfsPendingRecoveryInfo? recovery = null) =>
        new(CfsBrokerProtocol.CurrentVersion, true, Message: message, CanonicalArchiveKey: key, MountPath: mountPath, OutputPath: outputPath, Warning: warning,
            PersistenceState: persistence?.State.ToString(), IsDirty: persistence?.IsDirty ?? false,
            DirtyGeneration: persistence?.DirtyGeneration ?? 0, CommittedGeneration: persistence?.CommittedGeneration ?? 0,
            MutationSequence: persistence?.MutationSequence ?? 0,
            LastCommitUtc: persistence?.LastCommitUtc, LastCommitError: persistence?.LastError,
            SessionCount: _sessions.SessionCount + (_readOnlySessions?.SessionCount ?? 0),
            CreatedSessionCount: _sessions.CreatedSessionCount + (_readOnlySessions?.CreatedSessionCount ?? 0), BrokerProcessId: Environment.ProcessId,
            RequestId: request?.RequestId, SessionId: request?.SessionId, OperationId: operationId ?? request?.OperationId,
            CancellationId: operation?.CancellationId ?? cancellationId ?? request?.CancellationId,
            OperationState: operation?.State.ToString() ?? operationState,
            OperationResultCode: operation?.ResultCode ?? operationResultCode,
            OperationPhase: operation?.Phase, CurrentItem: operation?.CurrentItem,
            CompletedItems: operation?.CompletedItems ?? 0, TotalItems: operation?.TotalItems,
            CompletedBytes: operation?.CompletedBytes ?? 0, TotalBytes: operation?.TotalBytes,
            Percent: operation?.Percent, CanCancel: operation?.CanCancel ?? false,
            RecoveryFound: recovery?.Found ?? false,
            RecoveryOwnershipVerified: recovery?.OwnershipVerified ?? false,
            OriginalArchiveValid: recovery?.OriginalArchiveValid ?? false,
            RecoveryState: recovery?.State?.ToString(), RecoveryPhase: recovery?.CommitPhase?.ToString(),
            RecoveryDirtyGeneration: recovery?.DirtyGeneration ?? 0,
            RecoveryCommittedGeneration: recovery?.CommittedGeneration ?? 0,
            RecoveryMutationSequence: recovery?.MutationSequence ?? 0,
            ProtocolCapabilities: "v2;request-id;session-id;expected-generation;cancellation-id;operation-status-polling;structured-progress;recovery-preview");

    private BrokerResponse Error(string code, string message, BrokerRequest? request = null, CfsPersistenceStatus? persistence = null, string? mountPath = null, string? outputPath = null, string? operationState = null, string? operationId = null, string? cancellationId = null) =>
        new(CfsBrokerProtocol.CurrentVersion, false, code, message, MountPath: mountPath, OutputPath: outputPath,
            PersistenceState: persistence?.State.ToString(), IsDirty: persistence?.IsDirty ?? false,
            DirtyGeneration: persistence?.DirtyGeneration ?? 0, CommittedGeneration: persistence?.CommittedGeneration ?? 0,
            MutationSequence: persistence?.MutationSequence ?? 0,
            LastCommitUtc: persistence?.LastCommitUtc, LastCommitError: persistence?.LastError,
            SessionCount: _sessions.SessionCount + (_readOnlySessions?.SessionCount ?? 0),
            CreatedSessionCount: _sessions.CreatedSessionCount + (_readOnlySessions?.CreatedSessionCount ?? 0), BrokerProcessId: Environment.ProcessId,
            RequestId: request?.RequestId, SessionId: request?.SessionId, OperationId: operationId ?? request?.OperationId,
            CancellationId: cancellationId ?? request?.CancellationId, OperationState: operationState);
}

/// <summary>Caps broker-owned filesystem and archive mutations to four per user/logon-session broker.</summary>
public sealed class CfsMutationConcurrencyGate
{
    public const int DefaultLimit = 4;
    private readonly SemaphoreSlim _slots;
    private int _active;
    private int _maximumObserved;

    public CfsMutationConcurrencyGate(int limit = DefaultLimit)
    {
        if (limit <= 0 || limit > DefaultLimit) throw new ArgumentOutOfRangeException(nameof(limit));
        Limit = limit;
        _slots = new SemaphoreSlim(limit, limit);
    }

    public int Limit { get; }
    public int Active => Volatile.Read(ref _active);
    public int MaximumObserved => Volatile.Read(ref _maximumObserved);

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        var active = Interlocked.Increment(ref _active);
        UpdateMaximum(active);
        return new Lease(this);
    }

    private void Release()
    {
        Interlocked.Decrement(ref _active);
        _slots.Release();
    }

    private void UpdateMaximum(int value)
    {
        var current = Volatile.Read(ref _maximumObserved);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref _maximumObserved, value, current);
            if (observed == current) return;
            current = observed;
        }
    }

    private sealed class Lease(CfsMutationConcurrencyGate owner) : IDisposable
    {
        private CfsMutationConcurrencyGate? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}

public sealed record CfsBrokerNames(string MutexName, string PipeName)
{
    public static CfsBrokerNames ForCurrentUser(string? instanceSuffix = null)
    {
        var userIdentity = OperatingSystem.IsWindows()
            ? WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName
            : Environment.UserName;
        var sessionId = Process.GetCurrentProcess().SessionId;
        var suffix = string.IsNullOrWhiteSpace(instanceSuffix) ? string.Empty : ":" + instanceSuffix.Trim();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{userIdentity}:{sessionId}{suffix}"))).ToLowerInvariant()[..24];
        return new($"Global\\CFS.Broker.v2.{hash}", $"CFS.Broker.v2.{hash}");
    }
}

public sealed class CfsBrokerPipeServer
{
    private const int MaximumConcurrentConnections = 16;
    private readonly string _pipeName;
    private readonly CfsBrokerRequestHandler _handler;
    private readonly TimeSpan _framingTimeout;
    private readonly TimeSpan _handlerTimeout;
    private readonly TimeSpan _compressHandlerTimeout;
    private readonly TimeSpan _responseTimeout;
    private readonly CfsBrokerDeadlinePolicy _deadlinePolicy;

    public CfsBrokerPipeServer(string pipeName, CfsBrokerRequestHandler handler, TimeSpan? framingTimeout = null, TimeSpan? handlerTimeout = null, TimeSpan? responseTimeout = null, TimeSpan? compressHandlerTimeout = null, CfsBrokerDeadlinePolicy? deadlinePolicy = null)
    {
        _pipeName = pipeName;
        _handler = handler;
        _deadlinePolicy = deadlinePolicy ?? CfsBrokerDeadlinePolicy.Default;
        _framingTimeout = framingTimeout ?? _deadlinePolicy.Framing;
        _handlerTimeout = handlerTimeout ?? _deadlinePolicy.StandardHandler;
        _compressHandlerTimeout = compressHandlerTimeout ?? _deadlinePolicy.CompressionHandler;
        _responseTimeout = responseTimeout ?? _deadlinePolicy.ResponseWrite;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var slots = new SemaphoreSlim(MaximumConcurrentConnections, MaximumConcurrentConnections);
        var inFlight = new HashSet<Task>();
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await slots.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, MaximumConcurrentConnections,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                using var cancellationRegistration = cancellationToken.Register(static state => ((NamedPipeServerStream)state!).Dispose(), pipe);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is OperationCanceledException or ObjectDisposedException or IOException)
            {
                pipe?.Dispose(); slots.Release(); break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                slots.Release();
                try { CfsDiagnostics.Logger.WriteException("broker.ipc.accept", ex); } catch { }
                try { await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }
            var task = ProcessAndDisposeAsync(pipe!, slots, cancellationToken);
            lock (inFlight) inFlight.Add(task);
            _ = task.ContinueWith(completed => { lock (inFlight) inFlight.Remove(completed); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        Task[] remaining; lock (inFlight) remaining = inFlight.ToArray();
        await Task.WhenAll(remaining).ConfigureAwait(false);
    }

    private async Task ProcessAndDisposeAsync(NamedPipeServerStream pipe, SemaphoreSlim slots, CancellationToken cancellationToken)
    {
        try { await ProcessConnectionAsync(pipe, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex)
        {
            try { CfsDiagnostics.Logger.WriteException("broker.ipc.connection", ex); } catch { }
        }
        finally
        {
            try { await pipe.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                try { CfsDiagnostics.Logger.WriteException("broker.ipc.dispose", ex); } catch { }
            }
            finally { slots.Release(); }
        }
    }

    private async Task ProcessConnectionAsync(Stream pipe, CancellationToken brokerShutdown)
    {
        BrokerResponse response;
        BrokerRequest? request = null;
        try
        {
            using (var framing = CancellationTokenSource.CreateLinkedTokenSource(brokerShutdown))
            {
                framing.CancelAfter(_framingTimeout);
                request = await CfsBrokerProtocol.ReadRequestAsync(pipe, framing.Token).ConfigureAwait(false);
            }
            using var handling = CancellationTokenSource.CreateLinkedTokenSource(brokerShutdown);
            handling.CancelAfter(string.Equals(request.Command, "compress", StringComparison.OrdinalIgnoreCase) ? _compressHandlerTimeout : _handlerTimeout);
            response = await _handler.HandleAsync(request, handling.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!brokerShutdown.IsCancellationRequested)
        {
            response = request is null
                ? new(CfsBrokerProtocol.CurrentVersion, false, "request-timeout", "The broker request exceeded its bounded framing deadline.", BrokerProcessId: Environment.ProcessId)
                : _deadlinePolicy.TimeoutResponse(request.Command);
        }
        catch (BrokerProtocolException ex)
        {
            response = new(CfsBrokerProtocol.CurrentVersion, false, ex.ErrorCode, ex.Message, BrokerProcessId: Environment.ProcessId);
        }
        catch (Exception ex)
        {
            CfsDiagnostics.Logger.WriteException("broker.ipc", ex);
            response = new(CfsBrokerProtocol.CurrentVersion, false, "internal-error", "The broker could not process the request. See the CFS diagnostic log.", BrokerProcessId: Environment.ProcessId);
        }

        using var responseTimeout = new CancellationTokenSource(_responseTimeout);
        try { await CfsBrokerProtocol.WriteAsync(pipe, response, responseTimeout.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
    }
}

public static class CfsBrokerPipeClient
{
    public static async Task<BrokerResponse> SendAsync(string pipeName, BrokerRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadlineToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineToken.CancelAfter(timeout);
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                var remaining = deadline - DateTime.UtcNow;
                await pipe.ConnectAsync((int)Math.Clamp(remaining.TotalMilliseconds, 1, 500), deadlineToken.Token).ConfigureAwait(false);
                await CfsBrokerProtocol.WriteAsync(pipe, request, deadlineToken.Token).ConfigureAwait(false);
                return await CfsBrokerProtocol.ReadResponseAsync(pipe, deadlineToken.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                lastError = ex;
                try { await Task.Delay(50, deadlineToken.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new BrokerRequestException("broker-timeout", "The CFS broker did not complete the request before the overall deadline.", lastError);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new BrokerRequestException("broker-timeout", "The CFS broker did not complete the request before the overall deadline.");
            }
        }
        throw new BrokerRequestException("broker-unavailable", "The CFS broker did not become ready within the startup timeout. Try opening the archive again or inspect the CFS diagnostic log.", lastError);
    }
}
