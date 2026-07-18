using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Cfs.Broker;
using Cfs.Core;

var tests = new (string Name, Func<Task> Body)[]
{
    ("canonical archive identity converges and rejects invalid paths", CanonicalArchiveIdentityConvergesAndRejects),
    ("IPC protocol rejects oversized malformed version and command requests", IpcProtocolRejectsInvalidRequests),
    ("stalled IPC client times out without blocking the next request", StalledIpcClientDoesNotBlockServer),
    ("IPC client deadline bounds connected response waits", IpcClientDeadlineBoundsResponseWait),
    ("handler timeout is bounded and server accepts the next request", HandlerTimeoutRecovers),
    ("concurrent registry opens create exactly one session and mount path", ConcurrentRegistryOpenCreatesOneSession),
    ("shell commands use the command client and reject broker App CLI and injection", ShellCommandsUseCommandClientAndAreQuoted),
    ("path-based broker mount is manifest-only and archive-nonmutating", BrokerMountIsManifestOnlyAndNonMutating),
    ("cross-process broker reuses one provider and controlled shutdown cleans", CrossProcessBrokerReusesAndCleans),
    ("controlled shutdown without a broker exits promptly", OwnerlessControlledShutdownExitsPromptly),
    ("failed first-owner open exits promptly", FailedFirstOwnerOpenExitsPromptly)
};

var harnessLogRoot = Path.Combine(Path.GetTempPath(), "cfs-broker-tests", "harness-" + Environment.ProcessId);
Directory.CreateDirectory(harnessLogRoot);
CfsDiagnostics.Logger = new CfsDiagnosticLogger(harnessLogRoot);

if (args.Length > 0)
{
    tests = tests.Where(test => args.Any(filter => test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();
    if (tests.Length == 0) { Console.WriteLine("No tests matched the supplied filters."); return 2; }
}

var failed = 0;
foreach (var test in tests)
{
    try { await test.Body(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {test.Name}: {ex}"); }
}
Console.WriteLine($"TOTAL {tests.Length} PASS {tests.Length - failed} FAIL {failed}");
if (Directory.Exists(harnessLogRoot)) Directory.Delete(harnessLogRoot, recursive: true);
return failed == 0 ? 0 : 1;

static Task CanonicalArchiveIdentityConvergesAndRejects()
{
    using var workspace = new TestWorkspace();
    var archivePath = Path.Combine(workspace.Root, "Mixed Case Archive.cfs");
    File.WriteAllBytes(archivePath, [1]);
    var leadingSpacePath = Path.Combine(workspace.Root, " Leading Archive.cfs");
    File.WriteAllBytes(leadingSpacePath, [2]);
    var relative = CfsArchiveIdentity.Create("Mixed Case Archive.cfs", workspace.Root);
    var absolute = CfsArchiveIdentity.Create(archivePath);
    var differentCase = CfsArchiveIdentity.Create(archivePath.ToUpperInvariant());
    var trailing = CfsArchiveIdentity.Create(archivePath + Path.DirectorySeparatorChar);
    Assert(StringComparer.OrdinalIgnoreCase.Equals(relative.Key, absolute.Key), "relative and absolute identities diverged");
    Assert(StringComparer.OrdinalIgnoreCase.Equals(absolute.Key, differentCase.Key), "case variants diverged");
    Assert(StringComparer.OrdinalIgnoreCase.Equals(absolute.Key, trailing.Key), "trailing separator was not normalized");
    Assert(absolute.MountKey.Length == 32, "mount hash is not collision-resistant length");
    Assert(!string.IsNullOrWhiteSpace(absolute.HeaderFingerprint) && absolute.HeaderFingerprint.Length == 64, "archive header fingerprint is missing");
    if (absolute.FileId is not null)
    {
        var candidate = Path.Combine(workspace.Root, "replacement.cfs");
        File.WriteAllBytes(candidate, [9, 8, 7]);
        File.Replace(candidate, archivePath, null);
        var replacement = CfsArchiveIdentity.Create(archivePath);
        Assert(!absolute.RepresentsSameFile(replacement), "external replacement reused the previous filesystem identity");
    }
    Assert(CfsArchiveIdentity.Create(leadingSpacePath).FullPath == Path.GetFullPath(leadingSpacePath), "legal leading-space archive name was mutated");

    var wrongExtension = Path.Combine(workspace.Root, "archive.zip");
    File.WriteAllBytes(wrongExtension, [1]);
    AssertThrows<BrokerRequestException>(() => CfsArchiveIdentity.Create(wrongExtension), ex => ex.ErrorCode == "invalid-extension");
    AssertThrows<BrokerRequestException>(() => CfsArchiveIdentity.Create(Path.Combine(workspace.Root, "missing.cfs")), ex => ex.ErrorCode == "archive-not-found");
    return Task.CompletedTask;
}

static async Task IpcProtocolRejectsInvalidRequests()
{
    await using var oversized = new MemoryStream();
    await AssertThrowsAsync<BrokerProtocolException>(
        () => CfsBrokerProtocol.WriteAsync(oversized, new BrokerRequest(1, "open", new string('x', CfsBrokerProtocol.MaximumPayloadBytes))),
        ex => ex.ErrorCode == "payload-too-large");

    await using var malformed = new MemoryStream();
    var invalidJson = Encoding.UTF8.GetBytes("{");
    var prefix = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(prefix, invalidJson.Length);
    await malformed.WriteAsync(prefix); await malformed.WriteAsync(invalidJson); malformed.Position = 0;
    await AssertThrowsAsync<BrokerProtocolException>(() => CfsBrokerProtocol.ReadRequestAsync(malformed), ex => ex.ErrorCode == "malformed-request");

    using var workspace = new TestWorkspace();
    await using var registry = CreateFakeRegistry(Path.Combine(workspace.Root, "mounts"));
    var handler = new CfsBrokerRequestHandler(registry, new NoOpExplorer(), false, () => { });
    var version = await handler.HandleAsync(new BrokerRequest(999, "status"));
    Assert(!version.Success && version.ErrorCode == CfsBrokerErrorCodes.ProtocolUnsupported, "unknown protocol version lacked a clear response");
    var command = await handler.HandleAsync(new BrokerRequest(1, "format-disk"));
    Assert(!command.Success && command.ErrorCode == CfsBrokerErrorCodes.InvalidRequest, "unknown command lacked a clear response");
    var shutdown = await handler.HandleAsync(new BrokerRequest(1, "shutdown"));
    Assert(!shutdown.Success && shutdown.ErrorCode == "shutdown-not-allowed", "production handler accepted test shutdown");
}

static async Task StalledIpcClientDoesNotBlockServer()
{
    using var workspace = new TestWorkspace();
    var archive = Path.Combine(workspace.Root, "slow.cfs"); File.WriteAllBytes(archive, [1]);
    await using var registry = new CfsBrokerSessionRegistry(Path.Combine(workspace.Root, "mounts"), async (_, mountPath, _) =>
    {
        await Task.Delay(500);
        Directory.CreateDirectory(mountPath);
        return new FakeSession(mountPath);
    });
    using var stop = new CancellationTokenSource();
    var handler = new CfsBrokerRequestHandler(registry, new NoOpExplorer(), false, () => { });
    var pipeName = "CFS.Broker.StallTest." + Guid.NewGuid().ToString("N");
    var server = new CfsBrokerPipeServer(pipeName, handler, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(200));
    var serverTask = server.RunAsync(stop.Token);
    try
    {
        await using var stalled = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await stalled.ConnectAsync(2000);
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, 200);
        await stalled.WriteAsync(prefix); await stalled.WriteAsync(new byte[] { (byte)'{' }); await stalled.FlushAsync();
        await Task.Delay(500);

        var valid = await CfsBrokerPipeClient.SendAsync(pipeName, new BrokerRequest(1, "open", archive), TimeSpan.FromSeconds(3));
        Assert(valid.Success && valid.SessionCount == 1, "valid slow handler request did not recover after stalled frame");
    }
    finally { stop.Cancel(); try { await serverTask; } catch (OperationCanceledException) { } }
}

static async Task IpcClientDeadlineBoundsResponseWait()
{
    var pipeName = "CFS.Broker.NoResponseTest." + Guid.NewGuid().ToString("N");
    using var stop = new CancellationTokenSource();
    var serverTask = Task.Run(async () =>
    {
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.WaitForConnectionAsync(stop.Token);
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token); }
        catch (OperationCanceledException) { }
    });
    try
    {
        var watch = Stopwatch.StartNew();
        await AssertThrowsAsync<BrokerRequestException>(
            () => CfsBrokerPipeClient.SendAsync(pipeName, new BrokerRequest(1, "status"), TimeSpan.FromMilliseconds(350)),
            ex => ex.ErrorCode == "broker-timeout");
        watch.Stop();
        Assert(watch.Elapsed < TimeSpan.FromSeconds(2), "connected client response read exceeded its overall deadline");
    }
    finally { stop.Cancel(); try { await serverTask; } catch (OperationCanceledException) { } }
}

static async Task HandlerTimeoutRecovers()
{
    using var workspace = new TestWorkspace();
    CfsDiagnostics.Logger = new CfsDiagnosticLogger(Path.Combine(workspace.Root, "logs"));
    var archive = Path.Combine(workspace.Root, "too-slow.cfs"); File.WriteAllBytes(archive, [1]);
    await using var registry = new CfsBrokerSessionRegistry(Path.Combine(workspace.Root, "mounts"), async (_, mountPath, cancellationToken) =>
    {
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        Directory.CreateDirectory(mountPath); return new FakeSession(mountPath);
    });
    using var stop = new CancellationTokenSource();
    var handler = new CfsBrokerRequestHandler(registry, new NoOpExplorer(), false, () => { });
    var pipeName = "CFS.Broker.HandlerTimeout." + Guid.NewGuid().ToString("N");
    var server = new CfsBrokerPipeServer(pipeName, handler, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1));
    var serverTask = server.RunAsync(stop.Token);
    try
    {
        var timedOut = await CfsBrokerPipeClient.SendAsync(pipeName, new BrokerRequest(1, "open", archive), TimeSpan.FromSeconds(3));
        Assert(!timedOut.Success && timedOut.ErrorCode == "request-timeout", "handler timeout did not return a bounded error");
        var recovered = await CfsBrokerPipeClient.SendAsync(pipeName, new BrokerRequest(1, "status"), TimeSpan.FromSeconds(3));
        Assert(recovered.Success, "server did not accept a request after handler timeout");
    }
    finally { stop.Cancel(); try { await serverTask; } catch (OperationCanceledException) { } }
}

static async Task ConcurrentRegistryOpenCreatesOneSession()
{
    using var workspace = new TestWorkspace();
    var archive = Path.Combine(workspace.Root, "same.cfs"); File.WriteAllBytes(archive, [1]);
    var identity = CfsArchiveIdentity.Create(archive);
    var factoryCalls = 0;
    await using var registry = new CfsBrokerSessionRegistry(Path.Combine(workspace.Root, "mounts"), async (_, mountPath, _) =>
    {
        Interlocked.Increment(ref factoryCalls);
        await Task.Delay(100);
        Directory.CreateDirectory(mountPath);
        return new FakeSession(mountPath);
    });
    var opens = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => registry.OpenAsync(identity)));
    Assert(factoryCalls == 1 && registry.CreatedSessionCount == 1 && registry.SessionCount == 1, "concurrent opens created duplicate sessions");
    Assert(opens.Select(open => open.MountPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1, "concurrent opens returned different mount paths");
}

static Task ShellCommandsUseCommandClientAndAreQuoted()
{
    using var workspace = new TestWorkspace();
    var broker = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Program Files", "CFS & Tools; Beta", "Cfs.CommandClient.exe");
    var command = CfsShellRegistration.BuildOpenCommand(broker);
    Assert(command == $"\"{Path.GetFullPath(broker)}\" open \"%1\"", "command-client shell command quoting changed");
    Assert(command.Count(ch => ch == '"') == 4 && command.Contains(" open ", StringComparison.Ordinal), "command is not one quoted command-client invocation");
    AssertThrows<ArgumentException>(() => CfsShellRegistration.BuildOpenCommand(broker.Replace("Cfs.CommandClient.exe", "Cfs.Broker.exe")), _ => true);
    AssertThrows<ArgumentException>(() => CfsShellRegistration.BuildOpenCommand(broker.Replace("Cfs.CommandClient.exe", "Cfs.App.exe")), _ => true);
    AssertThrows<ArgumentException>(() => CfsShellRegistration.BuildOpenCommand(broker.Replace("Cfs.CommandClient.exe", "Cfs.Cli.exe")), _ => true);
    AssertThrows<ArgumentException>(() => CfsShellRegistration.BuildOpenCommand(broker.Replace("Cfs.CommandClient.exe", "bad\"name.exe")), _ => true);

    var root = FindRepositoryRoot();
    var builtBroker = Path.Combine(root, "src", "Cfs.Broker", "bin", "Release", "net8.0-windows", "Cfs.Broker.exe");
    var dryRunBroker = Path.Combine(workspace.Root, "CFS & Tools", "Cfs.CommandClient.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(dryRunBroker)!); File.Copy(builtBroker, dryRunBroker);
    var template = Path.Combine(workspace.Root, "ShellNew", "CFS-Empty.cfs");
    Directory.CreateDirectory(Path.GetDirectoryName(template)!); CfsArchive.CreateEmpty(template);
    var dryRun = RunAssociationScript(root, dryRunBroker, template);
    Assert(dryRun.ExitCode == 0 && dryRun.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Contains("OPEN_COMMAND=" + CfsShellRegistration.BuildOpenCommand(dryRunBroker)), "association dry-run did not emit the exact quoted broker open command");
    foreach (var forbidden in new[] { "Cfs.Broker.exe", "Cfs.App.exe", "Cfs.Cli.exe" })
    {
        var forbiddenPath = Path.Combine(workspace.Root, forbidden); File.Copy(builtBroker, forbiddenPath);
        var rejected = RunAssociationScript(root, forbiddenPath, template);
        Assert(rejected.ExitCode != 0 && rejected.Error.Contains("Expected Cfs.CommandClient.exe", StringComparison.Ordinal), $"association script accepted {forbidden}");
    }

    var mainForm = File.ReadAllText(Path.Combine(root, "src", "Cfs.App", "MainForm.cs"));
    Assert(mainForm.Contains("Path.Combine(AppContext.BaseDirectory, CfsShellRegistration.CommandClientExecutableName)", StringComparison.Ordinal)
        && mainForm.Contains("CfsShellRegistration.BuildOpenCommand(brokerPath)", StringComparison.Ordinal), "MainForm does not build the exact sibling command-client command");
    var installer = File.ReadAllText(Path.Combine(root, "packaging", "CFS-Setup.iss"));
    Assert(installer.Contains("ValueData: \"\"\"{app}\\{#CommandClientExe}\"\" open \"\"%1\"\"\"", StringComparison.Ordinal), "installer open command is not exact command-client quoting");
    Assert(installer.Contains("InstalledCommand := '\"' + ExpandConstant('{app}\\{#CommandClientExe}') + '\" open \"%1\"';", StringComparison.Ordinal), "installer uninstall ownership check is not the same exact command-client command");
    Assert(!mainForm.Contains("Cfs.Broker.exe\" \"%1", StringComparison.OrdinalIgnoreCase) && !installer.Contains("Cfs.Broker.exe\" \"%1", StringComparison.OrdinalIgnoreCase), "a first-party registration path still assigns open to Cfs.Broker");
    return Task.CompletedTask;
}

static (int ExitCode, string Output, string Error) RunAssociationScript(string root, string handlerPath, string templatePath)
{
    var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
    };
    foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "tools", "Register-CfsFileAssociation.ps1"), "-CommandClientPath", handlerPath, "-EmptyTemplatePath", templatePath, "-DryRun" })
        info.ArgumentList.Add(argument);
    using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not run association dry-run.");
    var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
    return (process.ExitCode, output, error);
}

static Task BrokerMountIsManifestOnlyAndNonMutating()
{
    if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
    using var workspace = new TestWorkspace();
    var source = Path.Combine(workspace.Root, "source"); Directory.CreateDirectory(source);
    File.WriteAllBytes(Path.Combine(source, "payload.bin"), Enumerable.Range(0, 65536).Select(i => (byte)(i % 251)).ToArray());
    var archive = Path.Combine(workspace.Root, "corrupt-payload.cfs"); CfsArchive.CreateFromFolder(source, archive);
    var entry = CfsArchive.LoadManifestEntries(archive).Single(item => item.Type == ArchiveEntryType.File);
    using (var stream = new FileStream(archive, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        stream.Position = entry.Offset + Math.Max(0, entry.CompressedSize / 2);
        var value = stream.ReadByte(); stream.Position--; stream.WriteByte((byte)(value ^ 0xFF));
    }
    Assert(!CfsArchive.Validate(archive).IsValid, "payload corruption fixture did not fail full hydration validation");
    var before = Hash(archive);
    var mount = Path.Combine(workspace.Root, "metadata-mount");
    var session = CfsMountSession.Create(archive, mount);
    try { Assert(Hash(archive) == before, "metadata-only broker mount rewrote archive bytes"); }
    finally { session.PermanentlyDelete(); }
    Assert(!Directory.Exists(mount), "metadata-only mount cleanup failed");
    return Task.CompletedTask;
}

static async Task CrossProcessBrokerReusesAndCleans()
{
    if (!OperatingSystem.IsWindows()) return;
    using var workspace = new TestWorkspace();
    var archive = Path.Combine(workspace.Root, "Legacy 0.1 Archive.cfs");
    LegacyCfs1Writer.Write(archive, "nested folder/hello.txt", Encoding.UTF8.GetBytes("0.1-format-compatible"));
    var bytesBefore = File.ReadAllBytes(archive); var hashBefore = Hash(archive);
    Assert(Encoding.ASCII.GetString(bytesBefore, 0, 4) == "CFS1" && BitConverter.ToInt32(bytesBefore, 4) == CfsArchive.FormatVersion,
        "compatibility fixture is not CFS1 format version 1");

    var brokerExe = Path.Combine(FindRepositoryRoot(), "src", "Cfs.Broker", "bin", "Release", "net8.0-windows", "Cfs.Broker.exe");
    Assert(File.Exists(brokerExe), "Release Cfs.Broker.exe was not built before integration test");
    var suffix = "Integration-" + Guid.NewGuid().ToString("N");
    var existingApps = Process.GetProcessesByName("Cfs.App").Select(process => process.Id).ToHashSet();
    var spawned = new List<Process>();
    Process Launch(string command, string? archivePath, string responsePath)
    {
        var process = StartBroker(brokerExe, suffix, command, archivePath, responsePath);
        spawned.Add(process);
        return process;
    }
    Process? owner = null;
    string? mountPath = null;
    try
    {
        var firstResponsePath = Path.Combine(workspace.Root, "first.json");
        var secondResponsePath = Path.Combine(workspace.Root, "second.json");
        var firstCandidate = Launch("open", archive, firstResponsePath);
        var secondCandidate = Launch("open", archive, secondResponsePath);
        var responses = await Task.WhenAll(ReadResponseFile(firstResponsePath), ReadResponseFile(secondResponsePath));
        var firstResponse = responses[0]; var secondResponse = responses[1];
        Assert(firstResponse.Success && secondResponse.Success, "one startup-race open failed");
        Assert(firstResponse.BrokerProcessId == secondResponse.BrokerProcessId, "startup-race clients contacted different broker owners");
        Assert(StringComparer.OrdinalIgnoreCase.Equals(firstResponse.MountPath, secondResponse.MountPath), "independent opens returned different mounts");
        owner = firstCandidate.Id == firstResponse.BrokerProcessId ? firstCandidate : secondCandidate.Id == firstResponse.BrokerProcessId ? secondCandidate : null;
        var activeOwner = owner ?? throw new InvalidOperationException("neither startup-race process owns the reported broker PID");
        var forwarder = ReferenceEquals(activeOwner, firstCandidate) ? secondCandidate : firstCandidate;
        Assert(forwarder.WaitForExit(15000) && forwarder.ExitCode == 0, "startup-race forwarding invocation did not exit successfully");
        Assert(new[] { firstCandidate, secondCandidate }.Count(process => !process.HasExited) == 1, "startup race left other than exactly one owner process");
        mountPath = firstResponse.MountPath;

        var statusPath = Path.Combine(workspace.Root, "status.json");
        var statusProcess = Launch("status", null, statusPath);
        Assert(statusProcess.WaitForExit(15000) && statusProcess.ExitCode == 0, "status client did not exit");
        var status = await ReadResponseFile(statusPath);
        Assert(status.BrokerProcessId == activeOwner.Id && status.SessionCount == 1 && status.CreatedSessionCount == 1,
            "cross-process requests did not converge on one broker/provider/session");
        Assert(!activeOwner.HasExited, "owning broker exited while session was live");
        Assert(!Process.GetProcessesByName("Cfs.App").Any(process => !existingApps.Contains(process.Id)), "broker open launched Cfs.App");
        Assert(File.ReadAllText(Path.Combine(mountPath!, "nested folder", "hello.txt"), Encoding.UTF8) == "0.1-format-compatible",
            "real broker ProjFS session did not hydrate the independently written legacy archive");
        Assert(Hash(archive) == hashBefore && File.ReadAllBytes(archive).SequenceEqual(bytesBefore), "broker open or hydration rewrote the legacy CFS1 archive");
        Console.WriteLine($"EVIDENCE brokerPid={activeOwner.Id} sessionCount={status.SessionCount} providerCreates={status.CreatedSessionCount} identicalMount=True startupRaceOwners=1 noCfsApp=True mount={mountPath}");

        var shutdownPath = Path.Combine(workspace.Root, "shutdown.json");
        var shutdown = Launch("shutdown", null, shutdownPath);
        Assert(shutdown.WaitForExit(15000) && shutdown.ExitCode == 0, "shutdown client did not exit");
        var shutdownResponse = await ReadResponseFile(shutdownPath);
        Assert(shutdownResponse.Success, "controlled shutdown was rejected");
        Assert(activeOwner.WaitForExit(15000), "broker owner survived controlled shutdown");
        Assert(!Directory.Exists(mountPath), "controlled shutdown left its test-owned mount folder");
        Assert(!Process.GetProcessesByName("Cfs.Broker").Any(process => process.Id == activeOwner.Id), "broker process survived controlled shutdown");
    }
    finally
    {
        if (owner is not null && !owner.HasExited)
        {
            try
            {
                var cleanupResponse = Path.Combine(workspace.Root, "finally-shutdown.json");
                var cleanup = Launch("shutdown", null, cleanupResponse);
                cleanup.WaitForExit(5000); owner.WaitForExit(5000);
            }
            catch { }
            if (!owner.HasExited) { owner.Kill(entireProcessTree: true); owner.WaitForExit(5000); }
        }
        foreach (var process in spawned)
        {
            try
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
            }
            finally { process.Dispose(); }
        }
        if (mountPath is not null && Directory.Exists(mountPath))
        {
            var expectedRoot = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CFS", "Sessions"));
            var expectedMount = Path.Combine(expectedRoot, CfsArchiveIdentity.Create(archive).MountKey);
            if (string.Equals(Path.GetFullPath(mountPath), expectedMount, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(mountPath, ".cfs-mount-session")))
                Directory.Delete(mountPath, recursive: true);
        }
    }
}

static async Task OwnerlessControlledShutdownExitsPromptly()
{
    using var workspace = new TestWorkspace();
    var brokerExe = Path.Combine(FindRepositoryRoot(), "src", "Cfs.Broker", "bin", "Release", "net8.0-windows", "Cfs.Broker.exe");
    var suffix = "OwnerlessShutdown-" + Guid.NewGuid().ToString("N");
    var responsePath = Path.Combine(workspace.Root, "shutdown.json");
    using var process = StartBroker(brokerExe, suffix, "shutdown", null, responsePath);
    try
    {
        Assert(process.WaitForExit(5000) && process.ExitCode == 0, "first-invocation controlled shutdown remained resident");
        var response = await ReadResponseFile(responsePath);
        Assert(response.Success && response.Message!.Contains("No broker session was running", StringComparison.Ordinal), "ownerless shutdown response was not explicit");
    }
    finally
    {
        if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
    }
}

static async Task FailedFirstOwnerOpenExitsPromptly()
{
    using var workspace = new TestWorkspace();
    var brokerExe = Path.Combine(FindRepositoryRoot(), "src", "Cfs.Broker", "bin", "Release", "net8.0-windows", "Cfs.Broker.exe");
    var suffix = "FailedOwner-" + Guid.NewGuid().ToString("N");
    var responsePath = Path.Combine(workspace.Root, "failed-open.json");
    var missingArchive = Path.Combine(workspace.Root, "definitely missing.cfs");
    using var process = StartBroker(brokerExe, suffix, "open", missingArchive, responsePath);
    try
    {
        Assert(process.WaitForExit(5000) && process.ExitCode == 2, "failed first-owner open remained resident");
        var response = await ReadResponseFile(responsePath);
        Assert(!response.Success && response.ErrorCode == "archive-not-found", "failed owner open did not return the canonical missing-archive error");
    }
    finally
    {
        if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
    }
}

static CfsBrokerSessionRegistry CreateFakeRegistry(string root) => new(root, (_, mountPath, _) =>
{
    Directory.CreateDirectory(mountPath);
    return Task.FromResult<ICfsBrokerSession>(new FakeSession(mountPath));
});

static Process StartBroker(string executable, string suffix, string command, string? archive, string responsePath)
{
    var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
    info.ArgumentList.Add(command); if (archive is not null) info.ArgumentList.Add(archive);
    info.ArgumentList.Add("--response-file"); info.ArgumentList.Add(responsePath);
    info.Environment["CFS_BROKER_INSTANCE_SUFFIX"] = suffix;
    info.Environment["CFS_BROKER_ALLOW_SHUTDOWN"] = "1";
    info.Environment["CFS_BROKER_DISABLE_EXPLORER"] = "1";
    info.Environment["CFS_BROKER_TEST_LOG_DIRECTORY"] = Path.Combine(Path.GetDirectoryName(responsePath)!, "broker-logs");
    info.Environment["DOTNET_ROOT"] = @"C:\Program Files\dotnet";
    info.Environment.Remove("MSBuildSDKsPath");
    return Process.Start(info) ?? throw new InvalidOperationException("Could not start Cfs.Broker.exe");
}

static async Task<BrokerResponse> ReadResponseFile(string path)
{
    var deadline = DateTime.UtcNow.AddSeconds(15);
    while (DateTime.UtcNow < deadline)
    {
        try
        {
            if (File.Exists(path))
            {
                var value = JsonSerializer.Deserialize<BrokerResponse>(await File.ReadAllTextAsync(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (value is not null) return value;
            }
        }
        catch (IOException) { }
        catch (JsonException) { }
        await Task.Delay(50);
    }
    throw new TimeoutException("Broker response file was not produced: " + path);
}

static string FindRepositoryRoot()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Build.props"))) current = current.Parent;
    return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
}

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void AssertThrows<T>(Action action, Func<T, bool> predicate) where T : Exception
{
    try { action(); }
    catch (T ex) when (predicate(ex)) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name} was not thrown with the required result.");
}
static async Task AssertThrowsAsync<T>(Func<Task> action, Func<T, bool> predicate) where T : Exception
{
    try { await action(); }
    catch (T ex) when (predicate(ex)) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name} was not thrown with the required result.");
}

sealed class FakeSession : ICfsBrokerSession
{
    public FakeSession(string mountPath) => MountPath = mountPath;
    public string MountPath { get; }
    public ValueTask DisposeAsync() { if (Directory.Exists(MountPath)) Directory.Delete(MountPath, true); return ValueTask.CompletedTask; }
}
sealed class NoOpExplorer : ICfsExplorerLauncher { public void OpenFolder(string folderPath) { } }
static class LegacyCfs1Writer
{
    public static void Write(string archivePath, string entryPath, byte[] bytes)
    {
        var compressed = Compress(bytes);
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1,
            Entries = new object[]
            {
                new { Path = "nested folder", Type = 1, OriginalSize = 0L, CompressedSize = 0L, Offset = 0L, CompressionMethod = "none", Sha256 = "", LastWriteTimeUtc = DateTimeOffset.UnixEpoch },
                new { Path = entryPath, Type = 0, OriginalSize = (long)bytes.Length, CompressedSize = (long)compressed.Length, Offset = 24L, CompressionMethod = "lzma2-raw-v2", Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), LastWriteTimeUtc = DateTimeOffset.Parse("2026-07-01T00:00:00Z") }
            }
        });
        using var stream = File.Create(archivePath);
        stream.Write(Encoding.ASCII.GetBytes("CFS1"));
        stream.Write(BitConverter.GetBytes(1));
        stream.Write(BitConverter.GetBytes(24L + compressed.Length));
        stream.Write(BitConverter.GetBytes((long)manifest.Length));
        stream.Write(compressed);
        stream.Write(manifest);
    }

    private static byte[] Compress(byte[] input)
    {
        var result = NativeCompress(input, (nuint)input.Length, out var output, out var outputSize);
        if (result != 0) throw new InvalidOperationException("Legacy fixture compression failed: " + result);
        try { var bytes = new byte[(int)outputSize]; Marshal.Copy(output, bytes, 0, bytes.Length); return bytes; }
        finally { NativeFree(output); }
    }

    [DllImport("cfs-lzma.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "cfs_lzma2_compress")]
    private static extern int NativeCompress(byte[] input, nuint inputSize, out IntPtr output, out nuint outputSize);
    [DllImport("cfs-lzma.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "cfs_lzma_free")]
    private static extern void NativeFree(IntPtr output);
}
sealed class TestWorkspace : IDisposable
{
    public TestWorkspace() { Root = Path.Combine(Path.GetTempPath(), "cfs-broker-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); }
    public string Root { get; }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}
