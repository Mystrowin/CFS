using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

return await CfsCommandClient.RunAsync(args);

internal static class CfsCommandClient
{
    private const int ProtocolVersion = 2;
    private const int MaximumPayloadBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var request = Parse(args);
            var pipeName = BrokerPipeName();
            var response = await SendOrStartAsync(pipeName, request).ConfigureAwait(false);
            return response.Success ? 0 : 2;
        }
        catch (CommandClientException ex)
        {
            ShowError(ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            ShowError($"CFS command client failed: {ex.Message}");
            return 2;
        }
    }

    private static ClientRequest Parse(string[] args)
    {
        if (args.Length is < 1 or > 2) throw new CommandClientException("CFS command arguments are invalid.");
        var command = args[0].Trim().ToLowerInvariant();
        if (command is not ("open" or "close" or "create-empty" or "compress" or "status" or "query"))
            throw new CommandClientException("CFS does not support this Explorer action.");
        var path = args.Length == 2 ? ValidatePath(args[1], command) : null;
        if (command is "open" or "close" or "create-empty" or "compress" && path is null)
            throw new CommandClientException($"CFS {command} requires one filesystem path.");
        return new(ProtocolVersion, command, command is "open" or "close" or "status" or "query" ? path : null,
            command == "compress" ? path : null, command == "create-empty" ? path : null,
            Guid.NewGuid().ToString("N"), null, null, Guid.NewGuid().ToString("N"), null);
    }

    private static string ValidatePath(string path, string command)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("-", StringComparison.Ordinal) || Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile)
            throw new CommandClientException("CFS requires a local filesystem path.");
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.StartsWith(@"\\.\", StringComparison.Ordinal) || full.StartsWith(@"\\?\", StringComparison.Ordinal))
            throw new CommandClientException("CFS does not accept device or shell namespace paths.");
        if (command is "open" or "close" && !full.EndsWith(".cfs", StringComparison.OrdinalIgnoreCase))
            throw new CommandClientException("CFS open and close actions require a .cfs archive.");
        return full;
    }

    private static async Task<ClientResponse> SendOrStartAsync(string pipeName, ClientRequest request)
    {
        try { return await SendAsync(pipeName, request, TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (CommandClientException)
        {
            StartBroker();
            return await SendAsync(pipeName, request, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
    }

    private static void StartBroker()
    {
        var brokerPath = Path.Combine(AppContext.BaseDirectory, "Cfs.Broker.exe");
        if (!File.Exists(brokerPath)) throw new CommandClientException("CFS installation is damaged: Cfs.Broker.exe is missing.");
        var process = Process.Start(new ProcessStartInfo { FileName = brokerPath, ArgumentList = { "status" }, UseShellExecute = false, CreateNoWindow = true });
        if (process is null) throw new CommandClientException("CFS broker could not be started.");
    }

    private static async Task<ClientResponse> SendAsync(string pipeName, ClientRequest request, TimeSpan timeout)
    {
        using var cancel = new CancellationTokenSource(timeout);
        Exception? last = null;
        while (!cancel.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(500, cancel.Token).ConfigureAwait(false);
                await WriteAsync(pipe, request, cancel.Token).ConfigureAwait(false);
                var response = await ReadAsync<ClientResponse>(pipe, cancel.Token).ConfigureAwait(false);
                if (!response.Success) ShowError(response.Message ?? response.ErrorCode ?? "CFS action failed.");
                return response;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
            {
                last = ex;
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
            }
        }
        throw new CommandClientException($"CFS broker did not become ready within the startup timeout. {last?.Message}");
    }

    private static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (payload.Length is <= 0 or > MaximumPayloadBytes) throw new CommandClientException("CFS request is outside the protocol size limit.");
        var prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > MaximumPayloadBytes) throw new CommandClientException("CFS broker returned an invalid protocol frame.");
        var payload = new byte[length]; await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, JsonOptions) ?? throw new CommandClientException("CFS broker returned an empty response.");
    }

    private static string BrokerPipeName()
    {
        var identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var sessionId = Process.GetCurrentProcess().SessionId;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{identity}:{sessionId}"))).ToLowerInvariant()[..24];
        return $"CFS.Broker.v2.{hash}";
    }

    private static void ShowError(string message) => Console.Error.WriteLine(message);

    private sealed record ClientRequest(int Version, string Command, string? ArchivePath, string? SourcePath, string? TargetPath, string RequestId, string? SessionId, ulong? ExpectedGeneration, string CancellationId, string? OperationId);
    private sealed record ClientResponse(int Version, bool Success, string? ErrorCode, string? Message);
    private sealed class CommandClientException(string message) : Exception(message);
}
