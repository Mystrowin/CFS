using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cfs.Broker;
using Cfs.Core;

var tests = new (string Name, Func<Task> Body)[]
{
    ("quiet period coalesces many dirty generations into one commit", QuietPeriodCoalesces),
    ("commit gate serializes debounce and explicit flush", CommitGateSerializes),
    ("failed commit preserves dirty generation status and retries", FailedCommitPreservesDirtyState),
    ("disposal flushes dirty state and cancels the pending quiet period", DisposalFlushesPendingCommit),
    ("persistent failure blocks disposal and status redacts private details", PersistentFailureBlocksDisposal),
    ("real broker automatically commits all edit patterns and keeps one live mount", RealBrokerAutomaticallyPersistsEdits),
    ("real broker commit failure preserves archive provider and marked mount", RealBrokerFailurePreservesRecoveryState),
    ("legacy CFS1 v1 archive automatically commits and reopens", LegacyArchiveAutomaticallyPersists)
};
if (args.Length > 0)
{
    tests = tests.Where(test => test.Name.Contains(args[0], StringComparison.OrdinalIgnoreCase)).ToArray();
    if (tests.Length == 0) throw new ArgumentException($"No persistence test matched '{args[0]}'.");
}
var logRoot = Path.Combine(Path.GetTempPath(), "cfs-persistence-tests", "logs-" + Environment.ProcessId);
Directory.CreateDirectory(logRoot); CfsDiagnostics.Logger = new CfsDiagnosticLogger(logRoot);
var failed = 0;
foreach (var test in tests)
{
    try { await test.Body(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {test.Name}: {ex}"); }
}
Console.WriteLine($"TOTAL {tests.Length} PASS {tests.Length - failed} FAIL {failed}");
if (Directory.Exists(logRoot)) Directory.Delete(logRoot, true);
return failed == 0 ? 0 : 1;

static async Task QuietPeriodCoalesces()
{
    var delay = new ManualDelay(); var commits = 0; var instant = DateTimeOffset.Parse("2026-07-14T12:00:00Z");
    await using var persistence = new CfsAutomaticPersistence(_ => { Interlocked.Increment(ref commits); return Task.CompletedTask; },
        TimeSpan.FromSeconds(1), delay.WaitAsync, () => instant);
    persistence.MarkDirty(); persistence.MarkDirty(); persistence.MarkDirty();
    await delay.WaitForCallsAsync(1);
    var waiting = persistence.Status;
    Assert(waiting.State == CfsPersistenceState.WaitingForQuietPeriod && waiting.DirtyGeneration == 3 && waiting.CommittedGeneration == 0, "waiting status/generation is wrong");
    delay.ReleaseNext(); await persistence.WaitForIdleAsync();
    var clean = persistence.Status;
    Assert(commits == 1 && !clean.IsDirty && clean.State == CfsPersistenceState.Clean && clean.CommittedGeneration == 3, "quiet period did not coalesce generations");
    Assert(clean.LastCommitUtc == instant && clean.LastError is null, "successful status fields are wrong");
}

static async Task CommitGateSerializes()
{
    var delay = new ManualDelay(); var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var active = 0; var maximum = 0; var calls = 0;
    await using var persistence = new CfsAutomaticPersistence(async token =>
    {
        var now = Interlocked.Increment(ref active); InterlockedExtensions.Max(ref maximum, now);
        var call = Interlocked.Increment(ref calls);
        try { if (call == 1) { firstEntered.SetResult(); await releaseFirst.Task.WaitAsync(token); } }
        finally { Interlocked.Decrement(ref active); }
    }, TimeSpan.FromSeconds(1), delay.WaitAsync);
    persistence.MarkDirty(); await delay.WaitForCallsAsync(1); delay.ReleaseNext(); await firstEntered.Task;
    persistence.MarkDirty(); var flush = persistence.FlushAsync();
    await Task.Delay(50);
    Assert(calls == 1, "explicit flush entered while debounce commit held the gate");
    Assert(persistence.Status.DirtyGeneration == 2 && persistence.Status.CommittedGeneration == 0 && persistence.Status.IsDirty,
        "new in-flight generation was marked committed before its durable commit");
    releaseFirst.SetResult(); await flush;
    Assert(maximum == 1 && calls == 2, "commit gate did not serialize both commit attempts");
    Assert(!persistence.Status.IsDirty && persistence.Status.CommittedGeneration == 2, "flush did not commit the latest generation");
}

static async Task FailedCommitPreservesDirtyState()
{
    var delay = new ManualDelay(); var attempts = 0; var archiveVersion = 1;
    await using var persistence = new CfsAutomaticPersistence(_ =>
    {
        if (Interlocked.Increment(ref attempts) == 1) throw new IOException(@"injected commit failure at C:\Users\Private\archive.cfs");
        archiveVersion = 2; return Task.CompletedTask;
    }, TimeSpan.FromSeconds(1), delay.WaitAsync);
    persistence.MarkDirty(); await delay.WaitForCallsAsync(1); delay.ReleaseNext(); await persistence.WaitForIdleAsync();
    var failed = persistence.Status;
    Assert(failed.State == CfsPersistenceState.Failed && failed.IsDirty && failed.CommittedGeneration == 0, "failure incorrectly advanced the committed generation");
    Assert(failed.LastError!.Contains("IOException") && !failed.LastError.Contains("Private") && archiveVersion == 1, "failure status privacy or prior archive preservation is wrong");
    await persistence.FlushAsync();
    Assert(attempts == 2 && archiveVersion == 2 && !persistence.Status.IsDirty && persistence.Status.LastError is null, "explicit retry did not recover the dirty generation");
}

static async Task DisposalFlushesPendingCommit()
{
    var delay = new ManualDelay(); var commits = 0;
    var persistence = new CfsAutomaticPersistence(_ => { commits++; return Task.CompletedTask; }, TimeSpan.FromSeconds(1), delay.WaitAsync);
    persistence.MarkDirty(); await delay.WaitForCallsAsync(1); await persistence.DisposeAsync();
    Assert(commits == 1 && persistence.Status.State == CfsPersistenceState.Stopped && !persistence.Status.IsDirty, "stop did not durably flush pending data before cancellation");
}

static async Task PersistentFailureBlocksDisposal()
{
    var delay = new ManualDelay(); var fail = true;
    var persistence = new CfsAutomaticPersistence(_ => fail
        ? Task.FromException(new IOException(@"cannot save C:\Users\Private\secret.cfs"))
        : Task.CompletedTask, TimeSpan.FromSeconds(1), delay.WaitAsync);
    persistence.MarkDirty(); await delay.WaitForCallsAsync(1); delay.ReleaseNext(); await persistence.WaitForIdleAsync();
    await AssertThrowsAsync<IOException>(() => persistence.DisposeAsync().AsTask());
    var status = persistence.Status;
    Assert(status.State == CfsPersistenceState.Failed && status.IsDirty && status.CommittedGeneration == 0, "failed disposal discarded dirty state");
    Assert(status.LastError!.Contains("IOException") && !status.LastError.Contains("Private") && !status.LastError.Contains("secret.cfs"), "status leaked a private path");
    fail = false; await persistence.DisposeAsync();
}

static async Task RealBrokerAutomaticallyPersistsEdits()
{
    if (!OperatingSystem.IsWindows()) return;
    using var workspace = new ProcessWorkspace();
    var source = Path.Combine(workspace.Root, "source"); Directory.CreateDirectory(Path.Combine(source, "folder"));
    Directory.CreateDirectory(Path.Combine(source, "delete-tree", "nested"));
    var unchangedBytes = Enumerable.Range(0, 32768).Select(i => (byte)(i % 251)).ToArray();
    File.WriteAllBytes(Path.Combine(source, "unchanged.bin"), unchangedBytes);
    File.WriteAllText(Path.Combine(source, "overwrite.txt"), "before-overwrite");
    File.WriteAllText(Path.Combine(source, "atomic.txt"), "before-atomic");
    File.WriteAllText(Path.Combine(source, "truncate.txt"), "before-truncate-long");
    File.WriteAllText(Path.Combine(source, "delete.txt"), "delete-me");
    File.WriteAllText(Path.Combine(source, "folder", "move.txt"), "move-me");
    File.WriteAllText(Path.Combine(source, "delete-tree", "nested", "remove.txt"), "remove-tree");
    var archivePath = Path.Combine(workspace.Root, "Automatic Persistence.cfs"); CfsArchive.CreateFromFolder(source, archivePath);
    var unchangedOffset = CfsArchive.LoadManifestEntries(archivePath).Single(entry => entry.Path == "unchanged.bin").Offset;
    var broker = BrokerHarness.Start(archivePath, workspace.Root, quietMilliseconds: 150);
    try
    {
        var opened = await broker.OpenAsync(); Assert(opened.Success, "real broker open failed: " + opened.Message);
        var mount = opened.MountPath!; Assert(Directory.Exists(mount), "broker mount does not exist");
        var baseline = await broker.WaitForCleanAsync(minimumCommittedGeneration: 0);

        using var readStop = new CancellationTokenSource();
        var readErrors = new List<Exception>();
        var reader = Task.Run(async () =>
        {
            while (!readStop.IsCancellationRequested)
            {
                try { Assert(File.ReadAllBytes(Path.Combine(mount, "unchanged.bin")).SequenceEqual(unchangedBytes), "concurrent projected read returned wrong bytes"); }
                catch (Exception ex) { lock (readErrors) readErrors.Add(ex); }
                await Task.Delay(10);
            }
        });

        File.WriteAllText(Path.Combine(mount, "overwrite.txt"), "after-overwrite");
        var atomicTemp = Path.Combine(mount, "atomic.tmp"); File.WriteAllText(atomicTemp, "after-atomic"); File.Move(atomicTemp, Path.Combine(mount, "atomic.txt"), true);
        using (var stream = new FileStream(Path.Combine(mount, "truncate.txt"), FileMode.Open, FileAccess.Write, FileShare.Read))
        { stream.SetLength(0); stream.Write("short"u8); stream.Flush(true); }
        File.WriteAllBytes(Path.Combine(mount, "created.bin"), [9, 8, 7, 6]);
        Directory.CreateDirectory(Path.Combine(mount, "moved"));
        File.Move(Path.Combine(mount, "folder", "move.txt"), Path.Combine(mount, "moved", "renamed.txt"));
        File.Delete(Path.Combine(mount, "delete.txt"));
        Directory.Delete(Path.Combine(mount, "delete-tree"), recursive: true);

        var committed = await broker.WaitForCleanAsync(baseline.CommittedGeneration + 1);
        readStop.Cancel(); await reader;
        Assert(readErrors.Count == 0, "provider callback failed during concurrent refresh: " + readErrors.FirstOrDefault());
        Assert(committed.LastCommitError is null && !committed.IsDirty, "successful status is dirty or exposes an error");
        Assert(File.ReadAllText(Path.Combine(mount, "overwrite.txt")) == "after-overwrite", "same-session overwrite view is stale");
        Assert(File.ReadAllText(Path.Combine(mount, "atomic.txt")) == "after-atomic", "same-session atomic replacement view is stale");
        Assert(File.ReadAllText(Path.Combine(mount, "truncate.txt")) == "short", "same-session truncate view is stale");
        Assert(File.ReadAllBytes(Path.Combine(mount, "created.bin")).SequenceEqual(new byte[] { 9, 8, 7, 6 }), "same-session create view is stale");
        Assert(File.Exists(Path.Combine(mount, "moved", "renamed.txt")) && !File.Exists(Path.Combine(mount, "folder", "move.txt")), "same-session rename/move view is stale");
        Assert(!File.Exists(Path.Combine(mount, "delete.txt")), "same-session delete view is stale");
        Assert(!Directory.Exists(Path.Combine(mount, "delete-tree")), "same-session recursive delete view is stale");
        Assert(CfsArchive.LoadManifestEntries(archivePath).Single(entry => entry.Path == "unchanged.bin").Offset == unchangedOffset,
            "automatic commit rewrote the unchanged compressed block");

        var stableWrite = File.GetLastWriteTimeUtc(archivePath); var stableGeneration = committed.CommittedGeneration;
        await Task.Delay(750); // greater than two 150 ms quiet periods
        var stable = await broker.QueryAsync();
        Assert(!stable.IsDirty && stable.CommittedGeneration == stableGeneration && File.GetLastWriteTimeUtc(archivePath) == stableWrite,
            "internal materialization events caused an automatic commit loop");

        File.WriteAllText(Path.Combine(mount, "after-refresh.txt"), "post-refresh");
        await broker.WaitForCleanAsync(stableGeneration + 1);
        Assert(File.ReadAllText(Path.Combine(mount, "after-refresh.txt")) == "post-refresh", "provider metadata refresh broke later same-session edits");

        var shutdown = await broker.ShutdownAsync(); Assert(shutdown.Success, "controlled flush/shutdown failed: " + shutdown.Message);
        Assert(!Directory.Exists(mount), "successful shutdown left the mount folder");
        var reopenedArchive = CfsArchive.Load(archivePath);
        Assert(Encoding.UTF8.GetString(reopenedArchive.ReadFile("overwrite.txt")) == "after-overwrite", "overwrite did not persist");
        Assert(Encoding.UTF8.GetString(reopenedArchive.ReadFile("atomic.txt")) == "after-atomic", "atomic replace did not persist");
        Assert(Encoding.UTF8.GetString(reopenedArchive.ReadFile("truncate.txt")) == "short", "truncate did not persist");
        Assert(reopenedArchive.ReadFile("created.bin").SequenceEqual(new byte[] { 9, 8, 7, 6 }), "create did not persist");
        Assert(reopenedArchive.ListEntries().Any(entry => entry.Path == "moved/renamed.txt")
            && !reopenedArchive.ListEntries().Any(entry => entry.Path is "folder/move.txt" or "delete.txt" || entry.Path.StartsWith("delete-tree", StringComparison.OrdinalIgnoreCase)),
            "rename/move/file-delete/recursive-delete did not persist");

        await using var reopen = BrokerHarness.Start(archivePath, workspace.Root, quietMilliseconds: 150);
        var reopened = await reopen.OpenAsync();
        Assert(reopened.Success && File.ReadAllText(Path.Combine(reopened.MountPath!, "after-refresh.txt")) == "post-refresh", "explicit broker reopen did not project committed state");
        Assert((await reopen.ShutdownAsync()).Success, "reopened broker did not shut down cleanly");
        Console.WriteLine($"EVIDENCE autoCommitState={committed.PersistenceState} unchangedOffset={unchangedOffset} stableNoLoop=True noCfsApp=True");
    }
    finally { await broker.DisposeAsync(); }
}

static async Task RealBrokerFailurePreservesRecoveryState()
{
    if (!OperatingSystem.IsWindows()) return;
    using var workspace = new ProcessWorkspace(); var source = Path.Combine(workspace.Root, "source"); Directory.CreateDirectory(source);
    File.WriteAllText(Path.Combine(source, "original.txt"), "original");
    var archivePath = Path.Combine(workspace.Root, "Private Failure Archive.cfs"); CfsArchive.CreateFromFolder(source, archivePath);
    var originalHash = Hash(archivePath);
    var broker = BrokerHarness.Start(archivePath, workspace.Root, quietMilliseconds: 100, commitFailureCount: 100);
    try
    {
        var opened = await broker.OpenAsync(); Assert(opened.Success, "failure fixture broker did not open");
        File.WriteAllText(Path.Combine(opened.MountPath!, "original.txt"), "unsaved-recoverable-edit");
        var failed = await broker.WaitForStateAsync("Failed");
        Assert(failed.IsDirty && failed.CommittedGeneration < failed.DirtyGeneration, "failure status did not preserve dirty generation");
        Assert(failed.LastCommitError!.Contains("IOException") && !failed.LastCommitError.Contains("Private Failure Archive"), "failure status leaked archive path");
        Assert(Hash(archivePath) == originalHash && CfsArchive.Validate(archivePath).IsValid, "failed commit changed or invalidated the original archive");
        Assert(Directory.Exists(opened.MountPath) && File.Exists(Path.Combine(opened.MountPath!, ".cfs-mount-session"))
            && File.ReadAllText(Path.Combine(opened.MountPath, "original.txt")) == "unsaved-recoverable-edit", "failed commit did not preserve recoverable marked mount/provider view");
        var shutdown = await broker.ShutdownAsync(expectSuccess: false);
        Assert(!shutdown.Success && shutdown.ErrorCode == "commit-failed" && shutdown.IsDirty && Directory.Exists(opened.MountPath), "controlled shutdown discarded or hid commit failure");
        Assert(broker.OwnerIsRunning, "broker exited after failed controlled shutdown");
        Assert(!broker.NewCfsAppWasLaunched, "failure workflow launched Cfs.App");
        Console.WriteLine($"EVIDENCE failureState={failed.PersistenceState} archiveValid=True archiveUnchanged=True markedMountPreserved=True shutdownError={shutdown.ErrorCode}");
    }
    finally { await broker.DisposeAsync(force: true); }
}

static async Task LegacyArchiveAutomaticallyPersists()
{
    if (!OperatingSystem.IsWindows()) return;
    using var workspace = new ProcessWorkspace(); var archivePath = Path.Combine(workspace.Root, "Legacy 0.1.cfs");
    LegacyCfs1Writer.Write(archivePath, "legacy.txt", Encoding.UTF8.GetBytes("legacy-before"));
    await using var broker = BrokerHarness.Start(archivePath, workspace.Root, quietMilliseconds: 100);
    var opened = await broker.OpenAsync(); Assert(opened.Success, "legacy archive did not open");
    var baseline = await broker.WaitForCleanAsync(0);
    File.WriteAllText(Path.Combine(opened.MountPath!, "legacy.txt"), "legacy-after");
    await broker.WaitForCleanAsync(baseline.CommittedGeneration + 1);
    Assert((await broker.ShutdownAsync()).Success, "legacy shutdown failed");
    Assert(Encoding.UTF8.GetString(CfsArchive.Load(archivePath).ReadFile("legacy.txt")) == "legacy-after", "legacy edit did not persist");
    await using var reopen = BrokerHarness.Start(archivePath, workspace.Root, quietMilliseconds: 100);
    var reopened = await reopen.OpenAsync(); Assert(File.ReadAllText(Path.Combine(reopened.MountPath!, "legacy.txt")) == "legacy-after", "legacy reopen view is stale");
    Assert((await reopen.ShutdownAsync()).Success, "legacy reopen shutdown failed");
}

static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static async Task AssertThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

sealed class BrokerHarness : IAsyncDisposable
{
    private readonly string _archivePath;
    private readonly string _workspace;
    private readonly string _suffix = "Persistence-" + Guid.NewGuid().ToString("N");
    private readonly int _quietMilliseconds;
    private readonly int _commitFailureCount;
    private readonly HashSet<int> _existingApps = Process.GetProcessesByName("Cfs.App").Select(process => process.Id).ToHashSet();
    private readonly List<Process> _spawned = [];
    private Process? _owner;
    private string? _mountPath;

    private BrokerHarness(string archivePath, string workspace, int quietMilliseconds, int commitFailureCount)
    { _archivePath = archivePath; _workspace = workspace; _quietMilliseconds = quietMilliseconds; _commitFailureCount = commitFailureCount; }

    public static BrokerHarness Start(string archivePath, string workspace, int quietMilliseconds, int commitFailureCount = 0) =>
        new(archivePath, workspace, quietMilliseconds, commitFailureCount);
    public bool OwnerIsRunning => _owner is { HasExited: false };
    public bool NewCfsAppWasLaunched => Process.GetProcessesByName("Cfs.App").Any(process => !_existingApps.Contains(process.Id));

    public async Task<BrokerResponse> OpenAsync()
    {
        var responsePath = NewResponsePath("open");
        _owner = Launch("open", _archivePath, responsePath);
        var response = await ReadResponseAsync(responsePath); _mountPath = response.MountPath;
        return response;
    }

    public async Task<BrokerResponse> QueryAsync()
    {
        var responsePath = NewResponsePath("status"); var process = Launch("status", _archivePath, responsePath);
        var response = await ReadResponseAsync(responsePath);
        Ensure(process.WaitForExit(15000), "status client remained resident");
        return response;
    }

    public async Task<BrokerResponse> WaitForCleanAsync(long minimumCommittedGeneration)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20); BrokerResponse? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await QueryAsync();
            if (last.Success && last.PersistenceState == nameof(CfsPersistenceState.Clean) && !last.IsDirty
                && last.CommittedGeneration >= minimumCommittedGeneration
                && (minimumCommittedGeneration == 0 || last.LastCommitUtc is not null)) return last;
            await Task.Delay(50);
        }
        throw new TimeoutException($"persistence did not become clean: state={last?.PersistenceState} dirty={last?.IsDirty} generation={last?.CommittedGeneration}/{last?.DirtyGeneration} error={last?.LastCommitError}");
    }

    public async Task<BrokerResponse> WaitForStateAsync(string state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20); BrokerResponse? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await QueryAsync(); if (last.PersistenceState == state) return last; await Task.Delay(50);
        }
        throw new TimeoutException("persistence state did not reach " + state + ": " + last?.PersistenceState);
    }

    public async Task<BrokerResponse> ShutdownAsync(bool expectSuccess = true)
    {
        var responsePath = NewResponsePath("shutdown"); var process = Launch("shutdown", null, responsePath);
        var response = await ReadResponseAsync(responsePath); Ensure(process.WaitForExit(15000), "shutdown client remained resident");
        if (expectSuccess)
        {
            Ensure(response.Success, "controlled shutdown response failed");
            Ensure(_owner is not null && _owner.WaitForExit(15000), "broker owner survived successful shutdown");
        }
        return response;
    }

    private Process Launch(string command, string? input, string responsePath)
    {
        var root = FindRepositoryRoot(); var executable = Path.Combine(root, "src", "Cfs.Broker", "bin", "Release", "net8.0-windows", "Cfs.Broker.exe");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(command); if (input is not null) info.ArgumentList.Add(input); info.ArgumentList.Add("--response-file"); info.ArgumentList.Add(responsePath);
        info.Environment["CFS_BROKER_INSTANCE_SUFFIX"] = _suffix; info.Environment["CFS_BROKER_ALLOW_SHUTDOWN"] = "1"; info.Environment["CFS_BROKER_DISABLE_EXPLORER"] = "1";
        info.Environment["CFS_BROKER_TEST_LOG_DIRECTORY"] = Path.Combine(_workspace, "broker-logs"); info.Environment["CFS_BROKER_TEST_QUIET_PERIOD_MS"] = _quietMilliseconds.ToString();
        info.Environment["CFS_BROKER_TEST_COMMIT_FAILURE_COUNT"] = _commitFailureCount.ToString(); info.Environment["DOTNET_ROOT"] = @"C:\Program Files\dotnet"; info.Environment.Remove("MSBuildSDKsPath");
        var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start Cfs.Broker.exe"); _spawned.Add(process); return process;
    }

    private string NewResponsePath(string name) => Path.Combine(_workspace, $"{name}-{Guid.NewGuid():N}.json");
    private static async Task<BrokerResponse> ReadResponseAsync(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            try { if (File.Exists(path)) { var value = JsonSerializer.Deserialize<BrokerResponse>(await File.ReadAllTextAsync(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)); if (value is not null) return value; } }
            catch (IOException) { } catch (JsonException) { }
            await Task.Delay(40);
        }
        throw new TimeoutException("broker response was not produced: " + path);
    }

    public ValueTask DisposeAsync() => DisposeAsync(force: false);
    public async ValueTask DisposeAsync(bool force)
    {
        if (_owner is { HasExited: false })
        {
            try { await ShutdownAsync(expectSuccess: false); } catch { }
            if (!_owner.HasExited) { try { _owner.Kill(true); _owner.WaitForExit(5000); } catch { } }
        }
        foreach (var process in _spawned)
        {
            try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); } } catch { }
            process.Dispose();
        }
        if (_mountPath is not null && Directory.Exists(_mountPath) && File.Exists(Path.Combine(_mountPath, ".cfs-mount-session")))
        {
            try { Directory.Delete(_mountPath, true); } catch when (force) { }
        }
        if (_mountPath is not null)
        {
            try { if (File.Exists(CfsSessionTransaction.CandidateFor(_mountPath))) File.Delete(CfsSessionTransaction.CandidateFor(_mountPath)); } catch when (force) { }
            try { if (File.Exists(CfsSessionTransaction.SidecarFor(_mountPath))) File.Delete(CfsSessionTransaction.SidecarFor(_mountPath)); } catch when (force) { }
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Build.props"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("repository root not found");
    }
    private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

sealed class ProcessWorkspace : IDisposable
{
    public ProcessWorkspace() { Root = Path.Combine(Path.GetTempPath(), "cfs-persistence-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); }
    public string Root { get; }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}

static class LegacyCfs1Writer
{
    public static void Write(string archivePath, string entryPath, byte[] bytes)
    {
        var compressed = Compress(bytes);
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1,
            Entries = new object[] { new { Path = entryPath, Type = 0, OriginalSize = (long)bytes.Length, CompressedSize = (long)compressed.Length,
                Offset = 24L, CompressionMethod = "lzma2-raw-v2", Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), LastWriteTimeUtc = DateTimeOffset.UnixEpoch } }
        });
        using var stream = File.Create(archivePath); stream.Write("CFS1"u8); stream.Write(BitConverter.GetBytes(1));
        stream.Write(BitConverter.GetBytes(24L + compressed.Length)); stream.Write(BitConverter.GetBytes((long)manifest.Length)); stream.Write(compressed); stream.Write(manifest); stream.Flush(true);
    }
    private static byte[] Compress(byte[] input)
    {
        var result = NativeCompress(input, (nuint)input.Length, out var output, out var size); if (result != 0) throw new InvalidOperationException("legacy compression failed: " + result);
        try { var bytes = new byte[(int)size]; Marshal.Copy(output, bytes, 0, bytes.Length); return bytes; } finally { NativeFree(output); }
    }
    [DllImport("cfs-lzma.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "cfs_lzma2_compress")]
    private static extern int NativeCompress(byte[] input, nuint inputSize, out IntPtr output, out nuint outputSize);
    [DllImport("cfs-lzma.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "cfs_lzma_free")]
    private static extern void NativeFree(IntPtr output);
}

sealed class ManualDelay
{
    private readonly object _sync = new();
    private readonly Queue<TaskCompletionSource> _pending = new();
    private int _calls;
    public Task WaitAsync(TimeSpan _, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(() => completion.TrySetCanceled(token));
        lock (_sync) { _pending.Enqueue(completion); _calls++; Monitor.PulseAll(_sync); }
        return completion.Task;
    }
    public Task WaitForCallsAsync(int count) => Task.Run(() =>
    {
        lock (_sync)
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (_calls < count)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(_sync, remaining)) throw new TimeoutException("manual delay was not scheduled");
            }
        }
    });
    public void ReleaseNext()
    {
        TaskCompletionSource completion;
        lock (_sync) completion = _pending.Dequeue();
        completion.TrySetResult();
    }
}

static class InterlockedExtensions
{
    public static void Max(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }
}
