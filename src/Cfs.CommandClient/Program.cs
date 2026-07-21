using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) => CfsCommandClient.Run(args);
}

internal static class CfsCommandClient
{
    private const int ProtocolVersion = 2;
    private const int MaximumPayloadBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static int Run(string[] args)
    {
        try
        {
            if (args.Length > 0 && string.Equals(args[0], "create-here", StringComparison.OrdinalIgnoreCase))
                return RunCreateHere(args);
            var request = Parse(args);
            var pipeName = BrokerPipeName();
            if (request.Command is "compress" or "extract")
                return RunTrackedOperation(pipeName, request);
            if (request.Command == "open-readonly")
            {
                var decision = MessageBox.Show(
                    "Read-only compatibility mode fully extracts the archive to a controlled temporary workspace. Changes in that workspace are discarded on Close CFS and never replace the source archive. Continue?",
                    "Open CFS read-only",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information,
                    MessageBoxDefaultButton.Button2);
                if (decision != DialogResult.Yes) return 0;
            }
            if (request.Command == "close")
                return RunCloseWorkflow(pipeName, request);
            if (request.Command == "discard")
            {
                var decision = MessageBox.Show(
                    "Discard all pending changes and unmount this CFS workspace? The last committed archive will be preserved.",
                    "Discard pending CFS changes",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (decision != DialogResult.Yes) return 0;
            }
            if (request.Command == "discard-recovery")
            {
                var decision = MessageBox.Show(
                    "Permanently discard the verified interrupted-session workspace? The valid original archive will not be changed.",
                    "Discard CFS recovery data",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (decision != DialogResult.Yes) return 0;
            }
            if (request.Command is "status" or "query")
                return RunStatusWorkflow(pipeName, request);
            if (request.Command == "recovery-status")
                return RunRecoveryStatusWorkflow(pipeName, request);
            return RunAsync(pipeName, request).GetAwaiter().GetResult();
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

    private static int RunCreateHere(string[] args)
    {
        if (args.Length != 2)
            throw new CommandClientException("Create CFS Archive requires one destination folder.");

        var destinationFolder = ValidateExistingFolder(args[1]);
        ApplicationConfiguration.Initialize();
        using var dialog = new SaveFileDialog
        {
            AddExtension = true,
            CheckPathExists = true,
            DefaultExt = "cfs",
            DereferenceLinks = true,
            FileName = NextAvailableArchiveName(destinationFolder),
            Filter = "CFS archive (*.cfs)|*.cfs",
            InitialDirectory = destinationFolder,
            OverwritePrompt = true,
            RestoreDirectory = true,
            Title = "Create CFS Archive"
        };

        while (dialog.ShowDialog() == DialogResult.OK)
        {
            var archivePath = Path.GetFullPath(dialog.FileName);
            if (!string.Equals(Path.GetExtension(archivePath), ".cfs", StringComparison.OrdinalIgnoreCase))
            {
                ShowError("A CFS archive name must end in .cfs.");
                continue;
            }
            if (File.Exists(archivePath) || Directory.Exists(archivePath))
            {
                ShowError("CFS will not replace an existing file or folder. Choose a new archive name.");
                continue;
            }

            var pipeName = BrokerPipeName();
            var create = new ClientRequest(ProtocolVersion, "create-empty", null, null, archivePath,
                Guid.NewGuid().ToString("N"), null, null, null, null);
            var created = SendOrStartAsync(pipeName, create).GetAwaiter().GetResult();
            if (!created.Success) return 2;

            var open = new ClientRequest(ProtocolVersion, "open", archivePath, null, null,
                Guid.NewGuid().ToString("N"), null, null, null, null);
            return RunAsync(pipeName, open).GetAwaiter().GetResult();
        }

        return 0;
    }

    private static string ValidateExistingFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("-", StringComparison.Ordinal)
            || Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile)
            throw new CommandClientException("Create CFS Archive requires a local destination folder.");
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\.\", StringComparison.Ordinal) || full.StartsWith(@"\\?\", StringComparison.Ordinal))
            throw new CommandClientException("CFS does not accept device or shell namespace paths.");
        if (!Directory.Exists(full))
            throw new CommandClientException("The selected CFS destination folder no longer exists.");
        return full;
    }

    private static string NextAvailableArchiveName(string destinationFolder)
    {
        const string baseName = "New CFS Archive";
        for (var suffix = 0; suffix < 10_000; suffix++)
        {
            var name = suffix == 0 ? $"{baseName}.cfs" : $"{baseName} ({suffix}).cfs";
            if (!File.Exists(Path.Combine(destinationFolder, name))
                && !Directory.Exists(Path.Combine(destinationFolder, name)))
                return name;
        }
        throw new CommandClientException("This folder already contains too many default CFS archive names. Enter a different name.");
    }

    private static int RunCloseWorkflow(string pipeName, ClientRequest closeRequest)
    {
        var statusRequest = closeRequest with { Command = "status", RequestId = Guid.NewGuid().ToString("N") };
        var status = SendOrStartAsync(pipeName, statusRequest).GetAwaiter().GetResult();
        if (!status.Success) return 2;
        if (status.IsDirty)
        {
            var choice = MessageBox.Show(
                $"This CFS workspace has pending changes (generation {status.DirtyGeneration}, last committed {status.CommittedGeneration}).\n\nYes: commit and close\nNo: discard and close\nCancel: keep it mounted",
                "Close CFS",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1);
            if (choice == DialogResult.Cancel) return 0;
            if (choice == DialogResult.No)
                closeRequest = closeRequest with { Command = "discard", RequestId = Guid.NewGuid().ToString("N") };
        }
        return RunAsync(pipeName, closeRequest).GetAwaiter().GetResult();
    }

    private static int RunStatusWorkflow(string pipeName, ClientRequest request)
    {
        var response = SendOrStartAsync(pipeName, request).GetAwaiter().GetResult();
        if (!response.Success) return 2;
        var state = response.PersistenceState ?? "Clean";
        var detail = response.IsDirty
            ? $"Mutation sequence: {response.MutationSequence}\nPending generation: {response.DirtyGeneration}\nLast committed generation: {response.CommittedGeneration}"
            : $"Mutation sequence: {response.MutationSequence}\nCommitted generation: {response.CommittedGeneration}\nNo pending changes.";
        MessageBox.Show($"{state}\n\n{detail}", "CFS archive status", MessageBoxButtons.OK,
            response.IsDirty ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        return 0;
    }

    private static int RunRecoveryStatusWorkflow(string pipeName, ClientRequest request)
    {
        var response = SendOrStartAsync(pipeName, request).GetAwaiter().GetResult();
        if (!response.Success) return 2;
        MessageBox.Show(
            $"State: {response.RecoveryState ?? "Unknown"}\nPhase: {response.RecoveryPhase ?? "Unknown"}\nMutation sequence: {response.RecoveryMutationSequence}\nPending generation: {response.RecoveryDirtyGeneration}\nLast committed generation: {response.RecoveryCommittedGeneration}\nOriginal archive valid: {(response.OriginalArchiveValid ? "Yes" : "No")}\n\n{response.Message}",
            "CFS recovery status",
            MessageBoxButtons.OK,
            response.OriginalArchiveValid ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        return 0;
    }

    private static async Task<int> RunAsync(string pipeName, ClientRequest request)
    {
        try
        {
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

    private static int RunTrackedOperation(string pipeName, ClientRequest request)
    {
        EnsureBrokerAsync(pipeName).GetAwaiter().GetResult();
        using var dialog = new CfsOperationProgressDialog(
            request.Command == "compress" ? "Compressing to CFS" : "Extracting CFS archive",
            () => SendAsync(pipeName, request, TimeSpan.FromHours(24), showFailure: false),
            () => SendAsync(pipeName, new ClientRequest(ProtocolVersion, "operation-status", null, null, null,
                Guid.NewGuid().ToString("N"), null, null, null, request.OperationId), TimeSpan.FromSeconds(10), showFailure: false),
            () => SendAsync(pipeName, new ClientRequest(ProtocolVersion, "cancel", null, null, null,
                Guid.NewGuid().ToString("N"), null, null, request.CancellationId, request.OperationId), TimeSpan.FromSeconds(10), showFailure: false));
        ApplicationConfiguration.Initialize();
        Application.Run(dialog);
        var response = dialog.Result;
        if (response is null) throw new CommandClientException("CFS operation ended without a broker result.");
        if (!response.Success && response.ErrorCode != "CFS_E_CANCELLED")
            ShowError(response.Message ?? response.ErrorCode ?? "CFS action failed.");
        return response.Success ? 0 : 2;
    }

    private static ClientRequest Parse(string[] args)
    {
        if (args.Length is < 1 or > 3) throw new CommandClientException("CFS command arguments are invalid.");
        var command = args[0].Trim().ToLowerInvariant();
        if (command is not ("open" or "open-readonly" or "close" or "commit" or "discard" or "recover" or "recovery-status" or "discard-recovery" or "create-empty" or "compress" or "extract" or "status" or "query" or "operation-status" or "cancel"))
            throw new CommandClientException("CFS does not support this Explorer action.");
        if (command == "operation-status")
        {
            if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1])) throw new CommandClientException("CFS operation status requires one operation ID.");
            return new(ProtocolVersion, command, null, null, null, Guid.NewGuid().ToString("N"), null, null, null, args[1]);
        }
        if (command == "cancel")
        {
            if (args.Length != 3 || string.IsNullOrWhiteSpace(args[1]) || string.IsNullOrWhiteSpace(args[2])) throw new CommandClientException("CFS cancellation requires an operation ID and cancellation ID.");
            return new(ProtocolVersion, command, null, null, null, Guid.NewGuid().ToString("N"), null, null, args[2], args[1]);
        }
        if (args.Length > 2) throw new CommandClientException("CFS command arguments are invalid.");
        var path = args.Length == 2 ? ValidatePath(args[1], command) : null;
        if (command is "open" or "open-readonly" or "close" or "commit" or "discard" or "recover" or "recovery-status" or "discard-recovery" or "create-empty" or "compress" or "extract" && path is null)
            throw new CommandClientException($"CFS {command} requires one filesystem path.");
        var operationId = command is "compress" or "extract" ? Guid.NewGuid().ToString("N") : null;
        var cancellationId = command is "compress" or "extract" ? Guid.NewGuid().ToString("N") : null;
        return new(ProtocolVersion, command, command is "open" or "open-readonly" or "close" or "commit" or "discard" or "recover" or "recovery-status" or "discard-recovery" or "extract" or "status" or "query" ? path : null,
            command == "compress" ? path : null, command == "create-empty" ? path : null,
            Guid.NewGuid().ToString("N"), null, null, cancellationId, operationId);
    }

    private static string ValidatePath(string path, string command)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("-", StringComparison.Ordinal) || Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile)
            throw new CommandClientException("CFS requires a local filesystem path.");
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.StartsWith(@"\\.\", StringComparison.Ordinal) || full.StartsWith(@"\\?\", StringComparison.Ordinal))
            throw new CommandClientException("CFS does not accept device or shell namespace paths.");
        if (command is "open" or "open-readonly" or "close" or "commit" or "discard" or "recover" or "recovery-status" or "discard-recovery" or "extract" && !full.EndsWith(".cfs", StringComparison.OrdinalIgnoreCase))
            throw new CommandClientException("This CFS action requires a .cfs archive.");
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

    private static async Task EnsureBrokerAsync(string pipeName)
    {
        var status = new ClientRequest(ProtocolVersion, "status", null, null, null, Guid.NewGuid().ToString("N"), null, null, null, null);
        try
        {
            await SendAsync(pipeName, status, TimeSpan.FromSeconds(2), showFailure: false).ConfigureAwait(false);
        }
        catch (CommandClientException)
        {
            StartBroker();
            await SendAsync(pipeName, status with { RequestId = Guid.NewGuid().ToString("N") }, TimeSpan.FromSeconds(20), showFailure: false).ConfigureAwait(false);
        }
    }

    private static void StartBroker()
    {
        var brokerPath = Path.Combine(AppContext.BaseDirectory, "Cfs.Broker.exe");
        if (!File.Exists(brokerPath)) throw new CommandClientException("CFS installation is damaged: Cfs.Broker.exe is missing.");
        var process = Process.Start(new ProcessStartInfo { FileName = brokerPath, ArgumentList = { "status" }, UseShellExecute = false, CreateNoWindow = true });
        if (process is null) throw new CommandClientException("CFS broker could not be started.");
    }

    internal static async Task<ClientResponse> SendAsync(string pipeName, ClientRequest request, TimeSpan timeout, bool showFailure = true)
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
                if (!response.Success && showFailure) ShowError(response.Message ?? response.ErrorCode ?? "CFS action failed.");
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

    private static void ShowError(string message)
    {
        try { MessageBox.Show(message, "CFS", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        catch { Console.Error.WriteLine(message); }
    }

    internal sealed record ClientRequest(int Version, string Command, string? ArchivePath, string? SourcePath, string? TargetPath, string RequestId, string? SessionId, ulong? ExpectedGeneration, string? CancellationId, string? OperationId);
    internal sealed record ClientResponse(
        int Version,
        bool Success,
        string? ErrorCode,
        string? Message,
        string? OperationId = null,
        string? CancellationId = null,
        string? OperationState = null,
        string? OperationResultCode = null,
        string? PersistenceState = null,
        bool IsDirty = false,
        ulong DirtyGeneration = 0,
        ulong CommittedGeneration = 0,
        ulong MutationSequence = 0,
        string? OperationPhase = null,
        string? CurrentItem = null,
        long CompletedItems = 0,
        long? TotalItems = null,
        long CompletedBytes = 0,
        long? TotalBytes = null,
        double? Percent = null,
        bool CanCancel = false,
        bool RecoveryFound = false,
        bool RecoveryOwnershipVerified = false,
        bool OriginalArchiveValid = false,
        string? RecoveryState = null,
        string? RecoveryPhase = null,
        ulong RecoveryDirtyGeneration = 0,
        ulong RecoveryCommittedGeneration = 0,
        ulong RecoveryMutationSequence = 0);
    private sealed class CommandClientException(string message) : Exception(message);
}
