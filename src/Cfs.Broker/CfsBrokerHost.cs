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

    public CfsBrokerRequestHandler(CfsBrokerSessionRegistry sessions, ICfsExplorerLauncher explorer, bool allowControlledShutdown, Action requestShutdown, CfsCreationOperations? creationOperations = null)
    {
        _sessions = sessions;
        _explorer = explorer;
        _allowControlledShutdown = allowControlledShutdown;
        _requestShutdown = requestShutdown;
        _creationOperations = creationOperations;
    }

    public async Task<BrokerResponse> HandleAsync(BrokerRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Version != CfsBrokerProtocol.CurrentVersion)
            return Error("unsupported-version", $"Broker protocol version {request.Version} is not supported; expected {CfsBrokerProtocol.CurrentVersion}.");

        switch (request.Command?.Trim().ToLowerInvariant())
        {
            case "open":
                try
                {
                    var identity = CfsArchiveIdentity.Create(request.ArchivePath ?? string.Empty);
                    CfsDiagnostics.Logger.WritePathEvent("broker.open", identity.FullPath, "starting");
                    var result = await _sessions.OpenAsync(identity, cancellationToken).ConfigureAwait(false);
                    _explorer.OpenFolder(result.MountPath);
                    CfsDiagnostics.Logger.WritePathEvent("broker.open", identity.FullPath, "success");
                    return Success("Archive session is ready.", result.CanonicalArchiveKey, result.MountPath);
                }
                catch (BrokerRequestException ex) { return Error(ex.ErrorCode, ex.Message); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    CfsDiagnostics.Logger.WriteException("broker.open", ex);
                    return Error("open-failed", "CFS could not open the archive on demand. Check ProjFS availability and the CFS diagnostic log.");
                }
            case "status":
            case "query":
                try
                {
                    string? key = null;
                    if (!string.IsNullOrWhiteSpace(request.ArchivePath)) key = CfsArchiveIdentity.Create(request.ArchivePath).Key;
                    var persistence = await _sessions.GetPersistenceStatusAsync(key).ConfigureAwait(false);
                    return Success("Broker is running.", persistence: persistence);
                }
                catch (BrokerRequestException ex) { return Error(ex.ErrorCode, ex.Message); }
            case "close":
                try
                {
                    var identity = CfsArchiveIdentity.Create(request.ArchivePath ?? string.Empty);
                    var result = await _sessions.CloseAsync(identity, cancellationToken).ConfigureAwait(false);
                    if (!result.Found) return Error("close-no-session", result.Error!);
                    if (!result.Success) return Error("close-failed", result.Error!, result.Status, result.MountPath);
                    return Success("Close CFS completed. Pending edits were committed, the archive validated, and the mounted folder was removed.",
                        identity.Key, result.MountPath, persistence: result.Status);
                }
                catch (BrokerRequestException ex) { return Error(ex.ErrorCode, ex.Message); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            case "create-empty":
                try
                {
                    if (_creationOperations is null) return Error("operation-unavailable", "Archive creation is not configured in this broker host.");
                    var output = _creationOperations.CreateEmpty(request.TargetPath ?? string.Empty);
                    return Success("Empty CFS archive created.", outputPath: output);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or CfsArchiveException)
                {
                    return Error("create-empty-failed", ex.Message);
                }
            case "compress":
                try
                {
                    if (_creationOperations is null) return Error("operation-unavailable", "Folder compression is not configured in this broker host.");
                    var result = await _creationOperations.CompressFolderAsync(request.SourcePath ?? string.Empty, cancellationToken).ConfigureAwait(false);
                    return Success(result.Warning is null ? "Folder compressed to CFS." : "Folder compressed to CFS with a cleanup warning.",
                        outputPath: result.OutputPath, warning: result.Warning);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or CfsArchiveException)
                {
                    return Error("compress-failed", ex.Message);
                }
            case "shutdown":
                if (!_allowControlledShutdown)
                    return Error("shutdown-not-allowed", "Broker shutdown is available only to controlled test teardown.");
                var flush = await _sessions.FlushAllAsync(cancellationToken).ConfigureAwait(false);
                if (!flush.Success)
                    return Error("commit-failed", flush.Error!, flush.Status, flush.MountPath);
                var response = Success("Controlled broker shutdown accepted.", persistence: flush.Status);
                _requestShutdown();
                return response;
            default:
                return Error("unknown-command", "Unknown broker command. Supported commands are open, close, create-empty, compress, status, and controlled test shutdown.");
        }
    }

    private BrokerResponse Success(string message, string? key = null, string? mountPath = null, string? outputPath = null, string? warning = null, CfsPersistenceStatus? persistence = null) =>
        new(CfsBrokerProtocol.CurrentVersion, true, Message: message, CanonicalArchiveKey: key, MountPath: mountPath, OutputPath: outputPath, Warning: warning,
            PersistenceState: persistence?.State.ToString(), IsDirty: persistence?.IsDirty ?? false,
            DirtyGeneration: persistence?.DirtyGeneration ?? 0, CommittedGeneration: persistence?.CommittedGeneration ?? 0,
            LastCommitUtc: persistence?.LastCommitUtc, LastCommitError: persistence?.LastError,
            SessionCount: _sessions.SessionCount, CreatedSessionCount: _sessions.CreatedSessionCount, BrokerProcessId: Environment.ProcessId);

    private BrokerResponse Error(string code, string message, CfsPersistenceStatus? persistence = null, string? mountPath = null) =>
        new(CfsBrokerProtocol.CurrentVersion, false, code, message, MountPath: mountPath,
            PersistenceState: persistence?.State.ToString(), IsDirty: persistence?.IsDirty ?? false,
            DirtyGeneration: persistence?.DirtyGeneration ?? 0, CommittedGeneration: persistence?.CommittedGeneration ?? 0,
            LastCommitUtc: persistence?.LastCommitUtc, LastCommitError: persistence?.LastError, SessionCount: _sessions.SessionCount,
            CreatedSessionCount: _sessions.CreatedSessionCount, BrokerProcessId: Environment.ProcessId);
}

public sealed record CfsBrokerNames(string MutexName, string PipeName)
{
    public static CfsBrokerNames ForCurrentUser(string? instanceSuffix = null)
    {
        var userIdentity = OperatingSystem.IsWindows()
            ? WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName
            : Environment.UserName;
        var suffix = string.IsNullOrWhiteSpace(instanceSuffix) ? string.Empty : ":" + instanceSuffix.Trim();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userIdentity + suffix))).ToLowerInvariant()[..24];
        return new($"Global\\CFS.Broker.v1.{hash}", $"CFS.Broker.v1.{hash}");
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
