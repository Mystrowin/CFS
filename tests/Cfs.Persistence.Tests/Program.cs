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
    ("mutation sequence overflow is rejected before mutation", MutationSequenceOverflowIsRejected),
    ("discard clears dirty state without reusing mutation sequence", DiscardDoesNotReuseMutationSequence),
    ("disposal flushes dirty state and cancels the pending quiet period", DisposalFlushesPendingCommit),
    ("persistent failure blocks disposal and status redacts private details", PersistentFailureBlocksDisposal),
    ("real broker automatically commits all edit patterns and keeps one live mount", RealBrokerAutomaticallyPersistsEdits),
    ("real broker commit failure preserves archive provider and marked mount", RealBrokerFailurePreservesRecoveryState),
    ("real broker holds its archive lock across atomic commit replacement", RealBrokerHoldsArchiveLockAcrossCommit),
    ("real broker reports file-in-use and preserves dirty state", RealBrokerReportsFileInUse),
    ("real broker refuses commit before insufficient-space mutation", RealBrokerRefusesInsufficientSpace),
    ("real broker preserves dirty edits when recovery storage exceeds its ceiling", RealBrokerPreservesRecoveryStorageCeiling),
    ("real broker forced termination preserves recovery at every commit phase", RealBrokerPreservesEveryCommitPhase),
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

static async Task MutationSequenceOverflowIsRejected()
{
    await using var persistence = new CfsAutomaticPersistence(
        _ => Task.CompletedTask,
        TimeSpan.FromSeconds(1),
        initialMutationSequence: ulong.MaxValue,
        initialDirtyGeneration: ulong.MaxValue,
        initialCommittedGeneration: ulong.MaxValue);
    try
    {
        _ = persistence.MarkDirty();
        throw new InvalidOperationException("overflowing mutation sequence was accepted");
    }
    catch (BrokerRequestException ex)
    {
        Assert(ex.ErrorCode == CfsBrokerErrorCodes.GenerationConflict,
            "mutation-sequence overflow did not use the stable generation-conflict code");
    }
    var status = persistence.Status;
    Assert(!status.IsDirty && status.MutationSequence == ulong.MaxValue
        && status.DirtyGeneration == ulong.MaxValue && status.CommittedGeneration == ulong.MaxValue,
        "overflow rejection changed a generation before failing");
}

static async Task DiscardDoesNotReuseMutationSequence()
{
    var delay = new ManualDelay();
    var persistence = new CfsAutomaticPersistence(_ => Task.CompletedTask, TimeSpan.FromSeconds(1), delay.WaitAsync);
    persistence.MarkDirty();
    persistence.MarkDirty();
    await delay.WaitForCallsAsync(1);
    await persistence.DiscardAsync();
    var status = persistence.Status;
    Assert(!status.IsDirty && status.MutationSequence == 2
        && status.DirtyGeneration == 0 && status.CommittedGeneration == 0,
        "discard reused a mutation number or represented discarded content as committed");
    try
    {
        _ = persistence.MarkDirty();
        throw new InvalidOperationException("discarded persistence accepted another mutation");
    }
    catch (ObjectDisposedException) { }
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
        Assert(!shutdown.Success && shutdown.ErrorCode == CfsBrokerErrorCodes.CommitFailed
            && shutdown.IsDirty && Directory.Exists(opened.MountPath),
            "controlled shutdown discarded or hid commit failure");
        Assert(broker.OwnerIsRunning, "broker exited after failed controlled shutdown");
        Assert(!broker.NewCfsAppWasLaunched, "failure workflow launched Cfs.App");
        Console.WriteLine($"EVIDENCE failureState={failed.PersistenceState} archiveValid=True archiveUnchanged=True markedMountPreserved=True shutdownError={shutdown.ErrorCode}");
    }
    finally { await broker.DisposeAsync(force: true); }
}

static async Task RealBrokerHoldsArchiveLockAcrossCommit()
{
    if (!OperatingSystem.IsWindows()) return;
    using var workspace = new ProcessWorkspace();
    var source = Path.Combine(workspace.Root, "source");
    Directory.CreateDirectory(source);
    File.WriteAllText(Path.Combine(source, "original.txt"), "original");
    var archivePath = Path.Combine(workspace.Root, "External Modification.cfs");
    CfsArchive.CreateFromFolder(source, archivePath);
    var broker = BrokerHarness.Start(archivePath, workspace.Root, quietMilliseconds: 100);
    try
    {
        var opened = await broker.OpenAsync();
        Assert(opened.Success, "archive-lock fixture broker did not open");
        var mountPath = opened.MountPath ?? throw new InvalidOperationException("archive-lock fixture did not return a mount path");
        _ = await broker.WaitForCleanAsync(0);

        AssertThrowsSharingViolation(() =>
        {
            using var _ = new FileStream(archivePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        });
        var copy = Path.Combine(workspace.Root, "copy-while-mounted.cfs");
        File.Copy(archivePath, copy);
        Assert(CfsArchive.Validate(copy).IsValid, "archive lock prevented the supported copy-while-mounted behavior");

        File.WriteAllText(Path.Combine(mountPath, "original.txt"), "committed-through-replacement");
        var clean = await broker.WaitForCleanAsync(1);
        Assert(!clean.IsDirty, "archive-lock fixture did not commit its mounted edit");
        AssertThrowsSharingViolation(() =>
        {
            using var _ = new FileStream(archivePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        });

        Assert((await broker.ShutdownAsync()).Success, "archive-lock fixture did not shut down cleanly");
        using (var writer = new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            Assert(writer.CanWrite, "archive lock survived after the writable session closed");
        }
        Assert(Encoding.UTF8.GetString(CfsArchive.Load(archivePath).ReadFile("original.txt")) == "committed-through-replacement",
            "archive lock transfer corrupted or lost the committed edit");
        Console.WriteLine($"EVIDENCE writeDeniedBeforeCommit=True writeDeniedAfterReplacement=True copyAllowed=True releasedAfterClose=True committedGeneration={clean.CommittedGeneration}");
    }
    finally { await broker.DisposeAsync(force: true); }
}

static async Task RealBrokerReportsFileInUse()
{
    if (!OperatingSystem.IsWindows()) return;
    using var workspace = new ProcessWorkspace();
    var source = Path.Combine(workspace.Root, "source");
    Directory.CreateDirectory(source);
    File.WriteAllText(Path.Combine(source, "locked.txt"), "before");
    var archivePath = Path.Combine(workspace.Root, "Open File.cfs");
    CfsArchive.CreateFromFolder(source, archivePath);
    var archiveHash = Hash(archivePath);
    var broker = BrokerHarness.Start(archivePath, workspace.Root, quietMilliseconds: 100);
    try
    {
        var opened = await broker.OpenAsync();
        var mountPath = opened.MountPath ?? throw new InvalidOperationException("file-in-use fixture did not return a mount path");
        _ = await broker.WaitForCleanAsync(0);
        var mountedFile = Path.Combine(mountPath, "locked.txt");

        using (var writer = new FileStream(mountedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            writer.SetLength(0);
            writer.Write("pending-while-open"u8);
            writer.Flush(true);
            var failed = await broker.WaitForStateAsync(nameof(CfsPersistenceState.Failed));
            var rejected = await broker.CommitAsync();
            Assert(!rejected.Success && rejected.ErrorCode == CfsBrokerErrorCodes.FileInUse
                && rejected.Message!.Contains("locked.txt", StringComparison.Ordinal),
                $"open mounted writer did not return CFS_E_FILE_IN_USE with the conflicting entry: code={rejected.ErrorCode} message={rejected.Message}");
            Assert(failed.IsDirty && failed.DirtyGeneration > failed.CommittedGeneration
                && Hash(archivePath) == archiveHash,
                "file-in-use failure advanced the generation or changed the archive");
        }

        var committed = await broker.CommitAsync();
        Assert(committed.Success, "commit did not recover after the conflicting writer closed: " + committed.Message);
        var clean = await broker.WaitForCleanAsync(1);
        Assert(!clean.IsDirty
            && Encoding.UTF8.GetString(CfsArchive.Load(archivePath).ReadFile("locked.txt")) == "pending-while-open",
            "retry after closing the writer did not commit the complete mounted file");
        Assert((await broker.ShutdownAsync()).Success, "file-in-use fixture did not shut down cleanly");
        Console.WriteLine($"EVIDENCE firstError={CfsBrokerErrorCodes.FileInUse} dirtyPreserved=True retryCommitted=True committedGeneration={clean.CommittedGeneration}");
    }
    finally { await broker.DisposeAsync(force: true); }
}

static async Task RealBrokerRefusesInsufficientSpace()
{
    if (!OperatingSystem.IsWindows()) return;
    using var workspace = new ProcessWorkspace();
    var source = Path.Combine(workspace.Root, "source");
    Directory.CreateDirectory(source);
    File.WriteAllText(Path.Combine(source, "entry.txt"), "before");
    var archivePath = Path.Combine(workspace.Root, "Insufficient Space.cfs");
    CfsArchive.CreateFromFolder(source, archivePath);
    var beforeHash = Hash(archivePath);
    var broker = BrokerHarness.Start(archivePath, workspace.Root, quietMilliseconds: 100, availableSpaceBytes: 0);
    try
    {
        var opened = await broker.OpenAsync();
        var mountPath = opened.MountPath ?? throw new InvalidOperationException("insufficient-space fixture did not return a mount path");
        File.WriteAllText(Path.Combine(mountPath, "entry.txt"), "pending-space-limited-edit");
        var failed = await broker.WaitForStateAsync(nameof(CfsPersistenceState.Failed));
        var rejected = await broker.CommitAsync();
        Assert(!rejected.Success && rejected.ErrorCode == CfsBrokerErrorCodes.InsufficientSpace
            && failed.IsDirty && failed.DirtyGeneration > failed.CommittedGeneration,
            $"insufficient-space commit did not preserve a dirty generation with the stable code: {rejected.ErrorCode}");
        Assert(Hash(archivePath) == beforeHash && CfsArchive.Validate(archivePath).IsValid
            && !Directory.EnumerateFiles(workspace.Root, "*.cfs-candidate", SearchOption.TopDirectoryOnly).Any(),
            "insufficient-space preflight changed the archive or created a candidate");
        var shutdown = await broker.ShutdownAsync(expectSuccess: false);
        Assert(!shutdown.Success && shutdown.ErrorCode == CfsBrokerErrorCodes.InsufficientSpace
            && broker.OwnerIsRunning && Directory.Exists(mountPath),
            "shutdown hid insufficient space or discarded the recoverable session");
        Console.WriteLine($"EVIDENCE error={rejected.ErrorCode} archiveUnchanged=True candidateCreated=False dirtyGeneration={failed.DirtyGeneration} committedGeneration={failed.CommittedGeneration}");
    }
    finally { await broker.DisposeAsync(force: true); }
}

static async Task RealBrokerPreservesRecoveryStorageCeiling()
{
    if (!OperatingSystem.IsWindows()) return;
    using var workspace = new ProcessWorkspace();
    var source = Path.Combine(workspace.Root, "source");
    Directory.CreateDirectory(source);
    File.WriteAllText(Path.Combine(source, "entry.txt"), "before");
    var archivePath = Path.Combine(workspace.Root, "Recovery Ceiling.cfs");
    CfsArchive.CreateFromFolder(source, archivePath);
    var beforeHash = Hash(archivePath);
    const long recoveryLimit = 1024 * 1024;
    var broker = BrokerHarness.Start(
        archivePath,
        workspace.Root,
        quietMilliseconds: 100,
        recoveryStorageLimitBytes: recoveryLimit);
    try
    {
        var opened = await broker.OpenAsync();
        Assert(opened.Success, "recovery-ceiling fixture did not open: " + opened.Message);
        var mountPath = opened.MountPath ?? throw new InvalidOperationException("recovery-ceiling fixture returned no mount");
        File.WriteAllBytes(Path.Combine(mountPath, "large-pending.bin"), new byte[2 * 1024 * 1024]);
        var failed = await broker.WaitForStateAsync(nameof(CfsPersistenceState.Failed));
        var rejected = await broker.CommitAsync();
        Assert(!rejected.Success && rejected.ErrorCode == CfsBrokerErrorCodes.InsufficientSpace,
            $"recovery ceiling did not return the stable insufficient-space code: {rejected.ErrorCode}");
        Assert(failed.IsDirty && failed.DirtyGeneration > failed.CommittedGeneration
            && File.Exists(Path.Combine(mountPath, "large-pending.bin")),
            "recovery ceiling did not preserve the dirty generation and pending file");
        Assert(Hash(archivePath) == beforeHash && CfsArchive.Validate(archivePath).IsValid,
            "recovery ceiling changed or damaged the authoritative archive");
        Console.WriteLine($"EVIDENCE error={rejected.ErrorCode} archiveUnchanged=True pendingPreserved=True limitBytes={recoveryLimit}");
    }
    finally { await broker.DisposeAsync(force: true); }
}

static async Task RealBrokerPreservesEveryCommitPhase()
{
    if (!OperatingSystem.IsWindows()) return;
    var phases = new[]
    {
        CfsCommitPhase.Preparing,
        CfsCommitPhase.WritingCandidate,
        CfsCommitPhase.FlushingCandidate,
        CfsCommitPhase.ValidatingCandidate,
        CfsCommitPhase.ReadyToReplace,
        CfsCommitPhase.Replacing,
        CfsCommitPhase.VerifyingReplacement,
        CfsCommitPhase.Committed,
        CfsCommitPhase.RestoringBackup,
        CfsCommitPhase.RecoveryRequired
    };

    foreach (var phase in phases)
    {
        using var workspace = new ProcessWorkspace();
        var source = Path.Combine(workspace.Root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "entry.txt"), "last-valid");
        var archivePath = Path.Combine(workspace.Root, $"Phase {phase}.cfs");
        CfsArchive.CreateFromFolder(source, archivePath);
        var baselineHash = Hash(archivePath);
        var failValidation = phase is CfsCommitPhase.RestoringBackup or CfsCommitPhase.RecoveryRequired;
        var failRestore = phase is CfsCommitPhase.RecoveryRequired;
        var broker = BrokerHarness.Start(archivePath, workspace.Root, quietMilliseconds: 100,
            pauseCommitPhase: phase, failReplacementValidation: failValidation, failBackupRestore: failRestore);
        try
        {
            var opened = await broker.OpenAsync();
            var mountPath = opened.MountPath ?? throw new InvalidOperationException($"{phase} fixture did not return a mount path");
            File.WriteAllText(Path.Combine(mountPath, "entry.txt"), "pending-" + phase);
            await broker.KillAtCommitPhaseAsync();

            var sidecar = CfsSessionTransaction.SidecarFor(mountPath);
            var record = JsonSerializer.Deserialize<CfsSessionTransactionRecord>(
                File.ReadAllBytes(sidecar),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException($"{phase} recovery record was empty");
            Assert(record.CommitPhase == phase
                && record.State == CfsSessionTransactionState.CommitPending
                && record.DirtyGeneration > record.LastCommittedGeneration
                && record.MutationSequence >= record.DirtyGeneration,
                $"{phase} did not durably record its exact phase and dirty generation before termination");

            var archiveValid = CfsArchive.Validate(archivePath).IsValid;
            var originalAtDestination = archiveValid && Hash(archivePath) == baselineHash;
            var originalInBackup = !string.IsNullOrWhiteSpace(record.BackupPath)
                && File.Exists(record.BackupPath)
                && CfsArchive.Validate(record.BackupPath).IsValid
                && Hash(record.BackupPath) == baselineHash;
            Assert(archiveValid && (originalAtDestination || originalInBackup),
                $"{phase} lost the last valid archive instead of retaining it at the destination or recorded backup");
            Assert(File.ReadAllText(Path.Combine(mountPath, "entry.txt")) == "pending-" + phase,
                $"{phase} did not preserve the pending mounted data");

            var reopen = await broker.ReopenAfterKillAsync();
            Assert(!reopen.Success && reopen.ErrorCode == CfsBrokerErrorCodes.RecoveryRequired
                && Directory.Exists(mountPath),
                $"{phase} reopen did not detect and preserve recovery-required state");

            var discarded = CfsSessionTransaction.DiscardPendingRecovery(CfsArchiveIdentity.Create(archivePath), mountPath);
            Assert(!discarded.Found && !Directory.Exists(mountPath)
                && (string.IsNullOrWhiteSpace(record.CandidatePath) || !File.Exists(record.CandidatePath))
                && (string.IsNullOrWhiteSpace(record.BackupPath) || !File.Exists(record.BackupPath)),
                $"{phase} controlled cleanup retained owned recovery artifacts");
            Console.WriteLine($"EVIDENCE phase={phase} archiveValid=True originalAtDestination={originalAtDestination} originalInBackup={originalInBackup} recoveryDetected=True cleanup=True");
        }
        finally { await broker.DisposeAsync(force: true); }
    }
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
static void AssertThrowsSharingViolation(Action action)
{
    try { action(); }
    catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) { return; }
    throw new InvalidOperationException("Expected a Windows sharing or lock violation.");
}
static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

sealed class BrokerHarness : IAsyncDisposable
{
    private readonly string _archivePath;
    private readonly string _workspace;
    private readonly string _suffix = "Persistence-" + Guid.NewGuid().ToString("N");
    private readonly int _quietMilliseconds;
    private readonly int _commitFailureCount;
    private readonly long? _availableSpaceBytes;
    private readonly CfsCommitPhase? _pauseCommitPhase;
    private readonly bool _failReplacementValidation;
    private readonly bool _failBackupRestore;
    private readonly long? _recoveryStorageLimitBytes;
    private readonly string _phaseSignalPath;
    private readonly HashSet<int> _existingApps = Process.GetProcessesByName("Cfs.App").Select(process => process.Id).ToHashSet();
    private readonly List<Process> _spawned = [];
    private Process? _owner;
    private string? _mountPath;

    private BrokerHarness(string archivePath, string workspace, int quietMilliseconds, int commitFailureCount, long? availableSpaceBytes,
        CfsCommitPhase? pauseCommitPhase, bool failReplacementValidation, bool failBackupRestore, long? recoveryStorageLimitBytes)
    {
        _archivePath = archivePath;
        _workspace = workspace;
        _quietMilliseconds = quietMilliseconds;
        _commitFailureCount = commitFailureCount;
        _availableSpaceBytes = availableSpaceBytes;
        _pauseCommitPhase = pauseCommitPhase;
        _failReplacementValidation = failReplacementValidation;
        _failBackupRestore = failBackupRestore;
        _recoveryStorageLimitBytes = recoveryStorageLimitBytes;
        _phaseSignalPath = Path.Combine(_workspace, "broker-logs", $"phase-{Guid.NewGuid():N}.json");
    }

    public static BrokerHarness Start(string archivePath, string workspace, int quietMilliseconds, int commitFailureCount = 0,
        long? availableSpaceBytes = null, CfsCommitPhase? pauseCommitPhase = null,
        bool failReplacementValidation = false, bool failBackupRestore = false, long? recoveryStorageLimitBytes = null) =>
        new(archivePath, workspace, quietMilliseconds, commitFailureCount, availableSpaceBytes,
            pauseCommitPhase, failReplacementValidation, failBackupRestore, recoveryStorageLimitBytes);
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

    public async Task<BrokerResponse> CommitAsync()
    {
        var responsePath = NewResponsePath("commit");
        var process = Launch("commit", _archivePath, responsePath);
        var response = await ReadResponseAsync(responsePath);
        Ensure(process.WaitForExit(15000), "commit client remained resident");
        return response;
    }

    public async Task KillAtCommitPhaseAsync()
    {
        if (_pauseCommitPhase is null || _owner is null)
            throw new InvalidOperationException("No controlled commit phase was configured.");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !File.Exists(_phaseSignalPath))
        {
            if (_owner.HasExited)
                throw new InvalidOperationException($"broker exited before reaching {_pauseCommitPhase}: {_owner.ExitCode}");
            await Task.Delay(25);
        }
        if (!File.Exists(_phaseSignalPath))
            throw new TimeoutException($"broker did not reach controlled phase {_pauseCommitPhase}");
        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(_phaseSignalPath)))
        {
            var signalledPhase = document.RootElement.GetProperty("phase").GetString();
            var signalledPid = document.RootElement.GetProperty("brokerProcessId").GetInt32();
            Ensure(string.Equals(signalledPhase, _pauseCommitPhase.ToString(), StringComparison.Ordinal)
                && signalledPid == _owner.Id,
                "commit-phase signal did not belong to the expected broker and phase");
        }
        _owner.Kill(entireProcessTree: true);
        Ensure(_owner.WaitForExit(10000), "broker did not terminate at the controlled commit phase");
    }

    public async Task<BrokerResponse> ReopenAfterKillAsync()
    {
        if (_owner is { HasExited: false })
            throw new InvalidOperationException("controlled broker is still running");
        var responsePath = NewResponsePath("reopen");
        _owner = Launch("open", _archivePath, responsePath);
        var response = await ReadResponseAsync(responsePath);
        if (!response.Success) Ensure(_owner.WaitForExit(15000), "recovery-required reopen owner remained resident");
        return response;
    }

    public async Task<BrokerResponse> WaitForCleanAsync(ulong minimumCommittedGeneration)
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
        info.Environment["CFS_BROKER_TEST_SESSION_ROOT"] = Path.Combine(_workspace, "broker-logs", "sessions");
        info.Environment["CFS_BROKER_TEST_COMMIT_FAILURE_COUNT"] = _commitFailureCount.ToString(); info.Environment["DOTNET_ROOT"] = @"C:\Program Files\dotnet"; info.Environment.Remove("MSBuildSDKsPath");
        if (_availableSpaceBytes is { } availableSpace) info.Environment["CFS_BROKER_TEST_AVAILABLE_SPACE_BYTES"] = availableSpace.ToString();
        if (_recoveryStorageLimitBytes is { } recoveryStorageLimit) info.Environment["CFS_BROKER_TEST_RECOVERY_STORAGE_LIMIT_BYTES"] = recoveryStorageLimit.ToString();
        if (_pauseCommitPhase is { } pausePhase)
        {
            info.Environment["CFS_BROKER_TEST_PAUSE_COMMIT_PHASE"] = pausePhase.ToString();
            info.Environment["CFS_BROKER_TEST_PHASE_SIGNAL_FILE"] = _phaseSignalPath;
        }
        if (_failReplacementValidation) info.Environment["CFS_BROKER_TEST_FAIL_REPLACEMENT_VALIDATION"] = "1";
        if (_failBackupRestore) info.Environment["CFS_BROKER_TEST_FAIL_BACKUP_RESTORE"] = "1";
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
    public void Dispose()
    {
        // Keep exact broker logs and response files when diagnosing a live-process failure.
        // Normal test runs retain the original cleanup behavior.
        if (Environment.GetEnvironmentVariable("CFS_KEEP_TEST_WORKSPACE") != "1" && Directory.Exists(Root))
            Directory.Delete(Root, true);
    }
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
