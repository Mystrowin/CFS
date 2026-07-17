using System.Text.Json;
using Cfs.Core;

namespace Cfs.Broker;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        var controlledTestLogDirectory = Environment.GetEnvironmentVariable("CFS_BROKER_TEST_LOG_DIRECTORY");
        if (Environment.GetEnvironmentVariable("CFS_BROKER_ALLOW_SHUTDOWN") == "1" && !string.IsNullOrWhiteSpace(controlledTestLogDirectory))
            CfsDiagnostics.Logger = new CfsDiagnosticLogger(controlledTestLogDirectory);
        CfsDiagnostics.Logger.WriteStartup();
        BrokerResponse response;
        string? responseFile = GetOption(args, "--response-file");
        try
        {
            var request = ParseRequest(args);
            var deadlines = CfsBrokerDeadlinePolicy.Default;
            var names = CfsBrokerNames.ForCurrentUser(Environment.GetEnvironmentVariable("CFS_BROKER_INSTANCE_SUFFIX"));
            using var singleton = new Mutex(initiallyOwned: true, names.MutexName, out var isOwner);
            if (!isOwner)
            {
                response = await CfsBrokerPipeClient.SendAsync(names.PipeName, request, deadlines.ClientFor(request.Command)).ConfigureAwait(false);
                WriteResponse(responseFile, response);
                return response.Success ? 0 : 2;
            }

            if (request.Command.Equals("shutdown", StringComparison.OrdinalIgnoreCase))
            {
                var allowed = Environment.GetEnvironmentVariable("CFS_BROKER_ALLOW_SHUTDOWN") == "1";
                response = allowed
                    ? new(CfsBrokerProtocol.CurrentVersion, true, Message: "No broker session was running; controlled shutdown is complete.", BrokerProcessId: Environment.ProcessId)
                    : new(CfsBrokerProtocol.CurrentVersion, false, "shutdown-not-allowed", "Broker shutdown is available only to controlled test teardown.", BrokerProcessId: Environment.ProcessId);
                WriteResponse(responseFile, response);
                return response.Success ? 0 : 2;
            }

            using var shutdown = new CancellationTokenSource();
            var mountRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CFS", "Sessions");
            var controlledTests = Environment.GetEnvironmentVariable("CFS_BROKER_ALLOW_SHUTDOWN") == "1";
            var quietPeriod = controlledTests && int.TryParse(Environment.GetEnvironmentVariable("CFS_BROKER_TEST_QUIET_PERIOD_MS"), out var quietMilliseconds)
                ? TimeSpan.FromMilliseconds(Math.Clamp(quietMilliseconds, 10, 60_000))
                : TimeSpan.FromMilliseconds(750);
            var injectedCommitFailures = controlledTests && int.TryParse(Environment.GetEnvironmentVariable("CFS_BROKER_TEST_COMMIT_FAILURE_COUNT"), out var failureCount)
                ? Math.Max(0, failureCount)
                : 0;
            if (controlledTests && Environment.GetEnvironmentVariable("CFS_BROKER_TEST_CLEANUP_FAILURE") == "1")
                CfsMountSession.DeleteDirectory = _ => throw new IOException("Injected locked mount cleanup failure.");
            await using var sessions = new CfsBrokerSessionRegistry(mountRoot, (identity, mountPath, cancellationToken) =>
            {
                var recovery = CfsSessionTransaction.RecoverBeforeOpen(identity, mountPath);
                if (recovery.RecoveryNeeded) throw new BrokerRequestException("recovery-needed", recovery.Message);
                var session = CfsMountSession.Create(identity.FullPath, mountPath, cancellationToken: cancellationToken);
                try
                {
                    var transaction = CfsSessionTransaction.Create(identity, mountPath);
                    Func<CancellationToken, Task>? commit = injectedCommitFailures == 0 ? null : token =>
                    {
                        if (Interlocked.Decrement(ref injectedCommitFailures) >= 0)
                            return Task.FromException(new IOException("Injected automatic commit failure."));
                        return Task.Run(() =>
                        {
                            var archive = CfsArchive.Load(identity.FullPath, cancellationToken: token);
                            session.CommitChanges(archive, cancellationToken: token);
                        }, token);
                    };
                    return Task.FromResult<ICfsBrokerSession>(new CfsBrokerMountedSession(identity.FullPath, session, quietPeriod, commit: commit, transaction: transaction));
                }
                catch
                {
                    try { session.PermanentlyDelete(cancellationToken: cancellationToken); } catch { }
                    throw;
                }
            });
            ICfsExplorerLauncher explorer = Environment.GetEnvironmentVariable("CFS_BROKER_DISABLE_EXPLORER") == "1"
                ? new NoOpExplorerLauncher()
                : new CfsExplorerLauncher();
            var handler = new CfsBrokerRequestHandler(sessions, explorer,
                Environment.GetEnvironmentVariable("CFS_BROKER_ALLOW_SHUTDOWN") == "1", shutdown.Cancel,
                new CfsCreationOperations(() => new CfsNativeProgressSurface()));
            var server = new CfsBrokerPipeServer(names.PipeName, handler, deadlinePolicy: deadlines);
            var serverTask = server.RunAsync(shutdown.Token);

            using var directRequestTimeout = new CancellationTokenSource(deadlines.HandlerFor(request.Command));
            try
            {
                response = await handler.HandleAsync(request, directRequestTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (directRequestTimeout.IsCancellationRequested)
            {
                response = deadlines.TimeoutResponse(request.Command);
            }
            WriteResponse(responseFile, response);
            if (!response.Success)
            {
                shutdown.Cancel();
                await serverTask.ConfigureAwait(false);
                return 2;
            }

            await serverTask.ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            CfsDiagnostics.Logger.WriteException("broker.startup", ex);
            response = new(CfsBrokerProtocol.CurrentVersion, false,
                ex is BrokerRequestException requestException ? requestException.ErrorCode : "startup-failed",
                ex.Message, BrokerProcessId: Environment.ProcessId);
            WriteResponse(responseFile, response);
            return 2;
        }
    }

    private static BrokerRequest ParseRequest(string[] args)
    {
        if (args.Length == 0) throw new BrokerRequestException("missing-command", "Use 'Cfs.Broker.exe open <archive.cfs>'.");
        var command = args[0].Trim().ToLowerInvariant();
        return command switch
        {
            "open" when args.Length >= 2 => new(CfsBrokerProtocol.CurrentVersion, command, args[1]),
            "close" when args.Length >= 2 => new(CfsBrokerProtocol.CurrentVersion, command, args[1]),
            "create-empty" when args.Length >= 2 => new(CfsBrokerProtocol.CurrentVersion, command, TargetPath: args[1]),
            "compress" when args.Length >= 2 => new(CfsBrokerProtocol.CurrentVersion, command, SourcePath: args[1]),
            "status" or "query" => new(CfsBrokerProtocol.CurrentVersion, command,
                ArchivePath: args.Length >= 2 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null),
            "shutdown" => new(CfsBrokerProtocol.CurrentVersion, command),
            "open" or "close" or "create-empty" or "compress" => throw new BrokerRequestException("invalid-path", $"The {command} command requires a path."),
            _ => new(CfsBrokerProtocol.CurrentVersion, command)
        };
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        return null;
    }

    private static void WriteResponse(string? responseFile, BrokerResponse response)
    {
        if (string.IsNullOrWhiteSpace(responseFile)) return;
        var fullPath = Path.GetFullPath(responseFile);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(response, JsonOptions));
    }

    private sealed class NoOpExplorerLauncher : ICfsExplorerLauncher
    {
        public void OpenFolder(string folderPath) { }
    }
}
