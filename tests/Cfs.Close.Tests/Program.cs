using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Cfs.Broker;
using Cfs.Core;

var tests = new (string Name, Func<Task> Body)[]
{
    ("registry close serializes same-archive open and reports no session", RegistryCloseSerializesOpen),
    ("handler close no-session is distinct and never shuts down broker", HandlerCloseNoSessionIsSafe),
    ("real broker closes one of two sessions and reopens it fresh", RealBrokerClosesOnlyTargetSession),
    ("immediate close force-flushes atomic writes before watcher debounce", ImmediateCloseFlushesAtomicWrites),
    ("flush failure preserves dirty session archive and marked mount", FlushFailurePreservesSession),
    ("cleanup failure preserves registry session and marked recovery mount", CleanupFailurePreservesSession),
    ("locked mount close fails safely and succeeds after release", LockedMountCloseIsRetryable),
    ("close validates archive before mount cleanup", ValidationPrecedesCleanup),
    ("Close CFS registration is exact quoted and ownership safe", CloseShellRegistrationIsExactAndSafe)
};
if (args.Length == 2 && args[0] == "--filter")
    tests = tests.Where(test => test.Name.Contains(args[1], StringComparison.OrdinalIgnoreCase)).ToArray();
if (tests.Length == 0) throw new InvalidOperationException("No close tests matched the requested filter.");
var logRoot = Path.Combine(Path.GetTempPath(), "cfs-close-tests", "logs-" + Environment.ProcessId); Directory.CreateDirectory(logRoot);
CfsDiagnostics.Logger = new CfsDiagnosticLogger(logRoot); var failed = 0;
foreach (var test in tests) { try { await test.Body(); Console.WriteLine($"PASS {test.Name}"); } catch (Exception ex) { failed++; Console.WriteLine($"FAIL {test.Name}: {ex}"); } }
Console.WriteLine($"TOTAL {tests.Length} PASS {tests.Length - failed} FAIL {failed}");
if (Directory.Exists(logRoot)) Directory.Delete(logRoot, true);
return failed == 0 ? 0 : 1;

static async Task RegistryCloseSerializesOpen()
{
    using var workspace = new Workspace(); var archive = Path.Combine(workspace.Root, "one.cfs"); var other = Path.Combine(workspace.Root, "other.cfs");
    CfsArchive.CreateEmpty(archive); CfsArchive.CreateEmpty(other); var identity = CfsArchiveIdentity.Create(archive); var created = 0;
    ControlledSession? first = null;
    await using var registry = new CfsBrokerSessionRegistry(Path.Combine(workspace.Root, "mounts"), (_, mount, _) =>
    {
        Directory.CreateDirectory(mount); var session = new ControlledSession(mount, Interlocked.Increment(ref created) == 1); first ??= session;
        return Task.FromResult<ICfsBrokerSession>(session);
    });
    await registry.OpenAsync(identity);
    var closing = registry.CloseAsync(identity); await first!.CloseStarted.Task;
    var racingOpen = registry.OpenAsync(identity); await Task.Delay(75); Assert(!racingOpen.IsCompleted, "same-archive open raced through active close");
    first.ReleaseClose.SetResult(); var closed = await closing; var reopened = await racingOpen;
    Assert(closed.Success && created == 2 && reopened.MountPath == closed.MountPath, "successful close did not remove and recreate exactly one session");
    var missing = await registry.CloseAsync(CfsArchiveIdentity.Create(other));
    Assert(!missing.Success && !missing.Found && missing.Error!.Contains("No live CFS session"), "no-session registry result is ambiguous");
}

static async Task HandlerCloseNoSessionIsSafe()
{
    using var workspace = new Workspace(); var archive = Path.Combine(workspace.Root, "none.cfs"); CfsArchive.CreateEmpty(archive);
    await using var registry = new CfsBrokerSessionRegistry(Path.Combine(workspace.Root, "mounts"), (_, mount, _) => Task.FromResult<ICfsBrokerSession>(new ControlledSession(mount, false)));
    var shutdownRequested = false; var handler = new CfsBrokerRequestHandler(registry, new NoOpExplorer(), true, () => shutdownRequested = true);
    var response = await handler.HandleAsync(new BrokerRequest(1, "close", archive));
    Assert(!response.Success && response.ErrorCode == CfsBrokerErrorCodes.SessionNotFound && !shutdownRequested && response.SessionCount == 0, "user close became global shutdown or false success");
}

static async Task RealBrokerClosesOnlyTargetSession()
{
    if (!OperatingSystem.IsWindows()) return; using var workspace = new Workspace();
    var archiveA = CreateArchive(workspace.Root, "A", "a.txt", "A-before"); var archiveB = CreateArchive(workspace.Root, "B", "b.txt", "B-before");
    await using var broker = new CloseBrokerHarness(workspace.Root);
    var openA = await broker.CommandAsync("open", archiveA); var openB = await broker.CommandAsync("open", archiveB);
    Assert(openA.Success && openB.Success && openA.MountPath != openB.MountPath,
        $"two independent sessions did not open: A={Describe(openA)} B={Describe(openB)}");
    File.WriteAllText(Path.Combine(openA.MountPath!, "a.txt"), "A-after"); File.WriteAllText(Path.Combine(openB.MountPath!, "b.txt"), "B-after");
    await broker.WaitCleanAsync(archiveA, 1); await broker.WaitCleanAsync(archiveB, 1);
    var closeA = await broker.CommandAsync("close", archiveA);
    Assert(closeA.Success && !Directory.Exists(openA.MountPath) && Directory.Exists(openB.MountPath), "Close CFS affected the wrong live session");
    Assert(Encoding.UTF8.GetString(CfsArchive.Load(archiveA).ReadFile("a.txt")) == "A-after" && CfsArchive.Validate(archiveA).IsValid, "target edit/validation did not survive close");
    Assert(File.ReadAllText(Path.Combine(openB.MountPath!, "b.txt")) == "B-after", "other session stopped persisting or projecting");
    var reopenedA = await broker.CommandAsync("open", archiveA);
    Assert(reopenedA.Success && reopenedA.MountPath == openA.MountPath && File.ReadAllText(Path.Combine(reopenedA.MountPath!, "a.txt")) == "A-after", "opening after close did not create a fresh projected session");
    Assert((await broker.CommandAsync("close", archiveA)).Success && (await broker.CommandAsync("close", archiveB)).Success, "final per-session closes failed");
    Assert(broker.OwnerRunning && !broker.NewCfsAppLaunched, "Close CFS shut down broker or launched Cfs.App");
    Assert((await broker.CommandAsync("shutdown", null)).Success, "controlled teardown failed");
}

static async Task ImmediateCloseFlushesAtomicWrites()
{
    if (!OperatingSystem.IsWindows()) return;
    using var workspace = new Workspace();
    var archive = CreateArchive(workspace.Root, "immediate", "replace.txt", "replace-before");
    await using var broker = new CloseBrokerHarness(workspace.Root);
    var opened = await broker.CommandAsync("open", archive);
    Assert(opened.Success, "immediate-close fixture open failed: " + Describe(opened));

    var replaceTarget = Path.Combine(opened.MountPath!, "replace.txt");
    var replaceTemp = Path.Combine(opened.MountPath!, "replace.tmp");
    File.WriteAllText(replaceTemp, "replace-after");
    File.Replace(replaceTemp, replaceTarget, null);
    var moveTarget = Path.Combine(opened.MountPath!, "move.txt");
    var moveTemp = Path.Combine(opened.MountPath!, "move.tmp");
    File.WriteAllText(moveTarget, "move-before");
    File.WriteAllText(moveTemp, "move-after");
    File.Move(moveTemp, moveTarget, overwrite: true);
    await Task.Delay(300); // Allow atomic-replace watcher delete/create events to be delivered before Close.

    var closed = await broker.CommandAsync("close", archive);
    Assert(closed.Success && !Directory.Exists(opened.MountPath), "immediate Close CFS did not commit and remove the mount: " + Describe(closed));
    var saved = CfsArchive.Load(archive);
    Assert(Encoding.UTF8.GetString(saved.ReadFile("replace.txt")) == "replace-after" &&
        Encoding.UTF8.GetString(saved.ReadFile("move.txt")) == "move-after" && CfsArchive.Validate(archive).IsValid,
        "immediate Close CFS lost File.Replace or overwrite File.Move content");

    var reopened = await broker.CommandAsync("open", archive);
    Assert(reopened.Success && File.ReadAllText(Path.Combine(reopened.MountPath!, "replace.txt")) == "replace-after" &&
        File.ReadAllText(Path.Combine(reopened.MountPath!, "move.txt")) == "move-after", "reopen did not project immediate-close content");
    Assert((await broker.CommandAsync("close", archive)).Success, "final immediate-close fixture cleanup failed");
}

static async Task FlushFailurePreservesSession()
{
    if (!OperatingSystem.IsWindows()) return; using var workspace = new Workspace(); var archive = CreateArchive(workspace.Root, "flush", "x.txt", "before"); var before = Hash(archive);
    await using var broker = new CloseBrokerHarness(workspace.Root, commitFailures: 100);
    var opened = await broker.CommandAsync("open", archive); Assert(opened.Success, "flush fixture open failed: " + Describe(opened)); File.WriteAllText(Path.Combine(opened.MountPath!, "x.txt"), "recoverable");
    await broker.WaitStateAsync(archive, "Failed"); var close = await broker.CommandAsync("close", archive);
    Assert(!close.Success && close.ErrorCode == "close-failed" && close.IsDirty && close.SessionCount == 1, "flush failure returned false close success or removed session");
    Assert(Hash(archive) == before && CfsArchive.Validate(archive).IsValid && Directory.Exists(opened.MountPath)
        && File.Exists(Path.Combine(opened.MountPath!, ".cfs-mount-session")), "flush failure changed archive or removed recovery mount");
    Assert(broker.OwnerRunning && !broker.NewCfsAppLaunched, "flush failure stopped broker or launched App");
}

static async Task CleanupFailurePreservesSession()
{
    if (!OperatingSystem.IsWindows()) return; using var workspace = new Workspace(); var archive = CreateArchive(workspace.Root, "cleanup", "x.txt", "before");
    await using var broker = new CloseBrokerHarness(workspace.Root, cleanupFailure: true);
    var opened = await broker.CommandAsync("open", archive); Assert(opened.Success, "cleanup fixture open failed: " + Describe(opened)); File.WriteAllText(Path.Combine(opened.MountPath!, "x.txt"), "committed"); await broker.WaitCleanAsync(archive, 1);
    var close = await broker.CommandAsync("close", archive);
    Assert(!close.Success && close.ErrorCode == "close-failed" && close.SessionCount == 1, "cleanup failure removed session or returned success");
    Assert(CfsArchive.Validate(archive).IsValid && Encoding.UTF8.GetString(CfsArchive.Load(archive).ReadFile("x.txt")) == "committed", "cleanup failure lost committed archive state");
    Assert(Directory.Exists(opened.MountPath) && File.Exists(Path.Combine(opened.MountPath!, ".cfs-mount-session")), "cleanup failure removed marked recovery mount");
}

static async Task LockedMountCloseIsRetryable()
{
    if (!OperatingSystem.IsWindows()) return; using var workspace = new Workspace(); var archive = CreateArchive(workspace.Root, "locked", "x.txt", "value");
    await using var broker = new CloseBrokerHarness(workspace.Root); var opened = await broker.CommandAsync("open", archive); Assert(opened.Success, "locked fixture open failed: " + Describe(opened));
    using (var locked = new FileStream(Path.Combine(opened.MountPath!, "x.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        var failed = await broker.CommandAsync("close", archive);
        Assert(!failed.Success && failed.ErrorCode == "close-failed" && Directory.Exists(opened.MountPath) && failed.SessionCount == 1, "locked file produced false success or removed mount/session");
    }
    var retry = await broker.CommandAsync("close", archive); Assert(retry.Success && !Directory.Exists(opened.MountPath), "close did not recover after locked file was released");
    Assert((await broker.CommandAsync("shutdown", null)).Success, "locked-file test teardown failed");
}

static async Task ValidationPrecedesCleanup()
{
    if (!OperatingSystem.IsWindows()) return; using var workspace = new Workspace(); var archive = CreateArchive(workspace.Root, "validate", "x.txt", "value");
    await using var broker = new CloseBrokerHarness(workspace.Root); var opened = await broker.CommandAsync("open", archive); Assert(opened.Success, "validation fixture open failed: " + Describe(opened)); _ = File.ReadAllText(Path.Combine(opened.MountPath!, "x.txt"));
    var validBytes = File.ReadAllBytes(archive); using (var stream = new FileStream(archive, FileMode.Open, FileAccess.Write, FileShare.Read)) { stream.Write("BAD!"u8); stream.Flush(true); }
    var failed = await broker.CommandAsync("close", archive);
    Assert(!failed.Success && failed.ErrorCode == "close-failed" && Directory.Exists(opened.MountPath) && File.Exists(Path.Combine(opened.MountPath!, ".cfs-mount-session")), "invalid archive was cleaned before validation");
    File.WriteAllBytes(archive, validBytes);
    Assert((await broker.CommandAsync("close", archive)).Success, "close did not succeed after valid archive was restored");
    Assert((await broker.CommandAsync("shutdown", null)).Success, "validation test teardown failed");
}

static Task CloseShellRegistrationIsExactAndSafe()
{
    if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
    using var workspace = new Workspace();
    var brokerDirectory = Path.Combine(workspace.Root, "handler with spaces"); Directory.CreateDirectory(brokerDirectory);
    var brokerPath = Path.Combine(brokerDirectory, CfsShellRegistration.CommandClientExecutableName); File.WriteAllBytes(brokerPath, []);
    var templatePath = Path.Combine(workspace.Root, "empty template.cfs"); CfsArchive.CreateEmpty(templatePath);
    var expected = CfsShellRegistration.BuildCloseCommand(brokerPath);
    Assert(expected == $"\"{Path.GetFullPath(brokerPath)}\" close \"%1\"", "close command quoting is not exact");
    AssertThrows<ArgumentException>(() => CfsShellRegistration.BuildCloseCommand(Path.Combine(brokerDirectory, "Cfs.App.exe")), "App was accepted as close handler");
    AssertThrows<ArgumentException>(() => CfsShellRegistration.BuildCloseCommand(Path.Combine(brokerDirectory, "Cfs.Cli.exe")), "CLI was accepted as close handler");

    var script = Path.Combine(Directory.GetCurrentDirectory(), "tools", "Register-CfsFileAssociation.ps1");
    var installer = Path.Combine(Directory.GetCurrentDirectory(), "packaging", "CFS-Setup.iss");
    var mainForm = Path.Combine(Directory.GetCurrentDirectory(), "src", "Cfs.App", "MainForm.cs");
    Assert(File.Exists(script) && File.Exists(installer) && File.Exists(mainForm), "shell integration sources were not found from repository root");
    var dry = RunPowerShell(script, brokerPath, templatePath, "Software\\CFS.Close.Tests.Dry", "-DryRun");
    Assert(dry.ExitCode == 0 && dry.Output.Contains("CLOSE_VERB_LABEL=Close CFS", StringComparison.Ordinal)
        && dry.Output.Contains("CLOSE_VERB_COMMAND=" + expected, StringComparison.Ordinal), "portable dry-run did not emit the exact close verb");

    var basePath = "Software\\CFS.Close.Tests." + Guid.NewGuid().ToString("N");
    try
    {
        var registered = RunPowerShell(script, brokerPath, templatePath, basePath, null); Assert(registered.ExitCode == 0, "portable registration failed: " + registered.Output);
        using (var key = Registry.CurrentUser.OpenSubKey(basePath + @"\CFS.Archive\shell\CFS.Close"))
        using (var command = Registry.CurrentUser.OpenSubKey(basePath + @"\CFS.Archive\shell\CFS.Close\command"))
            Assert((string?)key?.GetValue(null) == "Close CFS" && (string?)key?.GetValue("Icon") == brokerPath + ",0" && (string?)command?.GetValue(null) == expected,
                "registered Close CFS values differ from the exact broker command");
        using (var key = Registry.CurrentUser.OpenSubKey(basePath + @"\CFS.Archive\shell\CFS.Close", true))
        {
            key!.SetValue("Foreign", "keep"); using var child = key.CreateSubKey("Foreign.Child"); child.SetValue(null, "keep");
        }
        var removed = RunPowerShell(script, brokerPath, templatePath, basePath, "-Unregister"); Assert(removed.ExitCode == 0, "portable unregister failed: " + removed.Output);
        using (var key = Registry.CurrentUser.OpenSubKey(basePath + @"\CFS.Archive\shell\CFS.Close"))
            Assert(key is not null && key.GetValue(null) is null && key.GetValue("Icon") is null && (string?)key.GetValue("Foreign") == "keep"
                && key.OpenSubKey("Foreign.Child") is not null, "owned cleanup removed foreign Close CFS data or retained owned values");
        Assert(Registry.CurrentUser.OpenSubKey(basePath + @"\.cfs") is null
            && Registry.CurrentUser.OpenSubKey(basePath + @"\Directory") is null
            && Registry.CurrentUser.OpenSubKey(basePath + @"\CFS.Archive\shell\open") is null,
            "owned extension/open/folder entries were not fully pruned");
    }
    finally { Registry.CurrentUser.DeleteSubKeyTree(basePath, false); }

    var installerText = File.ReadAllText(installer); var mainText = File.ReadAllText(mainForm);
    Assert(installerText.Contains("ValueData: \"Close CFS\"", StringComparison.Ordinal)
        && installerText.Contains("\"\" close \"\"%1\"\"\"", StringComparison.Ordinal)
        && !installerText.Contains("uninsneveruninstall", StringComparison.Ordinal)
        && installerText.Contains("RegDeleteValue(HKLM, 'Software\\Classes\\CFS.Archive\\shell\\CFS.Close\\command', '')", StringComparison.Ordinal)
        && !installerText.Contains("RegDeleteKeyIncludingSubkeys(HKLM, 'Software\\Classes\\CFS.Archive\\shell\\CFS.Close", StringComparison.Ordinal),
        "installer close registration/uninstall ownership contract is incomplete");
    Assert(mainText.Contains("closeVerb.SetValue(null, \"Close CFS\")", StringComparison.Ordinal)
        && mainText.Contains("CfsShellRegistration.BuildCloseCommand(brokerPath)", StringComparison.Ordinal),
        "MainForm registration path can omit or redirect Close CFS");
    return Task.CompletedTask;
}

static string CreateArchive(string root, string name, string fileName, string contents) { var source = Path.Combine(root, name + "-source"); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, fileName), contents); var archive = Path.Combine(root, name + ".cfs"); CfsArchive.CreateFromFolder(source, archive); return archive; }
static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
static string Describe(BrokerResponse response) => $"success={response.Success}, code={response.ErrorCode ?? "<none>"}, message={response.Message ?? "<none>"}";
static (int ExitCode, string Output) RunPowerShell(string script, string broker, string template, string registryBase, string? option)
{
    var arguments = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
        "-BrokerPath", broker, "-EmptyTemplatePath", template, "-RegistryBasePath", registryBase };
    if (option is not null) arguments.Add(option);
    var startInfo = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
    using var process = Process.Start(startInfo)!;
    var stdout = process.StandardOutput.ReadToEnd(); var stderr = process.StandardError.ReadToEnd(); process.WaitForExit();
    return (process.ExitCode, stdout + stderr);
}
static void AssertThrows<T>(Action action, string message) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException(message); }
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

sealed class ControlledSession : ICfsBrokerSession, ICfsClosableBrokerSession, ICfsPersistentBrokerSession
{
    private readonly bool _blockClose;
    public ControlledSession(string mountPath, bool blockClose) { MountPath = mountPath; _blockClose = blockClose; }
    public string MountPath { get; }
    public TaskCompletionSource CloseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseClose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CfsPersistenceStatus PersistenceStatus => new(CfsPersistenceState.Clean, false, 0, 0, null, null);
    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public async Task CloseAsync(CancellationToken cancellationToken = default) { CloseStarted.TrySetResult(); if (_blockClose) await ReleaseClose.Task.WaitAsync(cancellationToken); if (Directory.Exists(MountPath)) Directory.Delete(MountPath, true); }
    public ValueTask DisposeAsync() { if (Directory.Exists(MountPath)) Directory.Delete(MountPath, true); return ValueTask.CompletedTask; }
}
sealed class NoOpExplorer : ICfsExplorerLauncher { public void OpenFolder(string folderPath) { } }
sealed class Workspace : IDisposable { public Workspace() { Root = Path.Combine(Path.GetTempPath(), "cfs-close-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); } public string Root { get; } public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); } }

sealed class CloseBrokerHarness : IAsyncDisposable
{
    private readonly string _workspace; private readonly string _suffix = "Close-" + Guid.NewGuid().ToString("N"); private readonly int _commitFailures; private readonly bool _cleanupFailure;
    private readonly List<Process> _processes = []; private readonly HashSet<int> _apps = Process.GetProcessesByName("Cfs.App").Select(p => p.Id).ToHashSet(); private Process? _owner; private readonly HashSet<string> _mounts = new(StringComparer.OrdinalIgnoreCase);
    public CloseBrokerHarness(string workspace, int commitFailures = 0, bool cleanupFailure = false) { _workspace = workspace; _commitFailures = commitFailures; _cleanupFailure = cleanupFailure; }
    public bool OwnerRunning => _owner is { HasExited: false }; public bool NewCfsAppLaunched => Process.GetProcessesByName("Cfs.App").Any(p => !_apps.Contains(p.Id));
    public async Task<BrokerResponse> CommandAsync(string command, string? archive)
    {
        var responsePath = Path.Combine(_workspace, $"{command}-{Guid.NewGuid():N}.json"); var process = Launch(command, archive, responsePath); var response = await ReadResponse(responsePath);
        if (_owner is null && command == "open" && response.Success) _owner = process;
        else if (!process.WaitForExit(15000)) throw new InvalidOperationException(command + " client remained resident");
        if (response.MountPath is not null) _mounts.Add(response.MountPath);
        return response;
    }
    public async Task<BrokerResponse> WaitCleanAsync(string archive, long minimum)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20); BrokerResponse? last = null;
        while (DateTime.UtcNow < deadline) { last = await CommandAsync("status", archive); if (last.PersistenceState == "Clean" && !last.IsDirty && last.CommittedGeneration >= minimum) return last; await Task.Delay(50); }
        throw new TimeoutException("session did not become clean: " + last?.PersistenceState);
    }
    public async Task<BrokerResponse> WaitStateAsync(string archive, string state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20); BrokerResponse? last = null;
        while (DateTime.UtcNow < deadline) { last = await CommandAsync("status", archive); if (last.PersistenceState == state) return last; await Task.Delay(50); }
        throw new TimeoutException("session did not reach " + state);
    }
    private Process Launch(string command, string? archive, string response)
    {
        var root = FindRoot(); var info = new ProcessStartInfo(Path.Combine(root, "src", "Cfs.Broker", "bin", "Release", "net8.0-windows", "Cfs.Broker.exe")) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(command); if (archive is not null) info.ArgumentList.Add(archive); info.ArgumentList.Add("--response-file"); info.ArgumentList.Add(response);
        info.Environment["CFS_BROKER_INSTANCE_SUFFIX"] = _suffix; info.Environment["CFS_BROKER_ALLOW_SHUTDOWN"] = "1"; info.Environment["CFS_BROKER_DISABLE_EXPLORER"] = "1"; info.Environment["CFS_BROKER_TEST_LOG_DIRECTORY"] = Path.Combine(_workspace, "logs");
        info.Environment["CFS_BROKER_TEST_QUIET_PERIOD_MS"] = "100"; info.Environment["CFS_BROKER_TEST_COMMIT_FAILURE_COUNT"] = _commitFailures.ToString(); if (_cleanupFailure) info.Environment["CFS_BROKER_TEST_CLEANUP_FAILURE"] = "1";
        info.Environment["DOTNET_ROOT"] = @"C:\Program Files\dotnet"; info.Environment.Remove("MSBuildSDKsPath"); var process = Process.Start(info)!; _processes.Add(process); return process;
    }
    private static async Task<BrokerResponse> ReadResponse(string path) { var deadline = DateTime.UtcNow.AddSeconds(15); while (DateTime.UtcNow < deadline) { try { if (File.Exists(path)) { var response = JsonSerializer.Deserialize<BrokerResponse>(await File.ReadAllTextAsync(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)); if (response is not null) return response; } } catch (IOException) { } catch (JsonException) { } await Task.Delay(40); } throw new TimeoutException("response missing: " + path); }
    public async ValueTask DisposeAsync()
    {
        if (_owner is { HasExited: false }) { try { await CommandAsync("shutdown", null); } catch { } if (!_owner.HasExited) { try { _owner.Kill(true); _owner.WaitForExit(5000); } catch { } } }
        foreach (var process in _processes) { try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); } } catch { } process.Dispose(); }
        foreach (var mount in _mounts)
        {
            if (Directory.Exists(mount) && File.Exists(Path.Combine(mount, ".cfs-mount-session"))) { try { Directory.Delete(mount, true); } catch { } }
            try { if (File.Exists(CfsSessionTransaction.CandidateFor(mount))) File.Delete(CfsSessionTransaction.CandidateFor(mount)); } catch { }
            try { if (File.Exists(CfsSessionTransaction.SidecarFor(mount))) File.Delete(CfsSessionTransaction.SidecarFor(mount)); } catch { }
        }
    }
    private static string FindRoot() { var current = new DirectoryInfo(AppContext.BaseDirectory); while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Build.props"))) current = current.Parent; return current?.FullName ?? throw new DirectoryNotFoundException(); }
}
