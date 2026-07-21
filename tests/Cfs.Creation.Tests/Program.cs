using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using Cfs.Broker;
using Cfs.Core;

var tests = new (string Name, Func<Task> Body)[]
{
    ("empty CFS1 archive is durable transactional and renameable", EmptyArchiveIsTransactional),
    ("folder compression round-trips source bytes and allocates collision suffixes", CompressionRoundTripsAndCollidesSafely),
    ("archive extraction validates input and allocates collision-free output folders", ArchiveExtractionIsValidatedAndCollisionSafe),
    ("cancelled extraction preserves partial output in a visible recovery folder", CancelledExtractionPreservesPartialOutput),
    ("source traversal rejects invalid roots files and reparse points", SourceTraversalIsSafe),
    ("progress covers scanning and remains best-effort for every lifecycle failure", ProgressCoversScanningAndIsBestEffort),
    ("post-commit cleanup warning preserves output for owner and forwarded requests", CleanupWarningPreservesCommittedOutput),
    ("deadline policy is validated and owner-forwarded timeout responses match", DeadlinePolicyAndTimeoutParity),
    ("held compression and aborted clients do not exhaust broker capacity", ConcurrentServerRemainsResponsive),
    ("creation IPC errors are bounded and actionable", CreationErrorsAreActionable),
    ("ShellNew and folder verb registration are exact and ownership-safe", ShellRegistrationIsExactAndSafe),
    ("real WinExe broker creates compresses opens and shuts down without Cfs.App", RealProcessCreationWorkflow)
};

var harnessLogRoot = Path.Combine(Path.GetTempPath(), "cfs-creation-tests", "harness-" + Environment.ProcessId);
Directory.CreateDirectory(harnessLogRoot);
CfsDiagnostics.Logger = new CfsDiagnosticLogger(harnessLogRoot);
if (args.Length > 0) tests = tests.Where(test => args.Any(filter => test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();
var failures = 0;
foreach (var test in tests)
{
    try { await test.Body(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {test.Name}: {ex}"); }
}
Console.WriteLine($"TOTAL {tests.Length} PASS {tests.Length - failures} FAIL {failures}");
if (Directory.Exists(harnessLogRoot)) Directory.Delete(harnessLogRoot, true);
return failures == 0 ? 0 : 1;

static async Task EmptyArchiveIsTransactional()
{
    using var workspace = new TestWorkspace();
    var archivePath = Path.Combine(workspace.Root, "New CFS Compressed Folder.cfs");
    var archive = CfsArchive.CreateEmpty(archivePath);
    Assert(CfsArchive.Validate(archivePath).IsValid, "empty archive did not validate");
    Assert(archive.ListEntries().Count == 0, "empty archive contains entries");
    Assert(File.ReadAllBytes(archivePath).AsSpan(0, 4).SequenceEqual("CFS1"u8), "empty archive lacks CFS1 header");
    Assert(BitConverter.ToInt32(File.ReadAllBytes(archivePath), 4) == CfsArchive.FormatVersion, "empty archive is not format v1");
    var renamed = Path.Combine(workspace.Root, "Renamed Empty.cfs");
    File.Move(archivePath, renamed);
    Assert(CfsArchive.Load(renamed).ListEntries().Count == 0, "renamed empty archive did not reopen");
    await AssertThrowsAsync<CfsArchiveException>(() => Task.Run(() => CfsArchive.CreateEmpty(renamed)));
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    var canceledTarget = Path.Combine(workspace.Root, "canceled-folder.cfs");
    await AssertThrowsAsync<OperationCanceledException>(() => Task.Run(() => CfsArchive.CreateFromFolder(workspace.Root, canceledTarget, cancellationToken: canceled.Token)));
    Assert(!File.Exists(canceledTarget), "canceled folder creation left a final archive");
    Assert(!Directory.EnumerateFiles(workspace.Root, ".*.tmp").Any(), "empty creation left a temporary archive");
}

static Task CompressionRoundTripsAndCollidesSafely()
{
    using var workspace = new TestWorkspace();
    var source = Path.Combine(workspace.Root, "Source ü space");
    Directory.CreateDirectory(Path.Combine(source, "nested", "deep"));
    File.WriteAllText(Path.Combine(source, "read me.txt"), "hello βeta");
    File.WriteAllBytes(Path.Combine(source, "nested", "deep", "payload.bin"), Enumerable.Range(0, 65536).Select(i => (byte)(i % 251)).ToArray());
    var before = Snapshot(source);
    var operations = NewOperations();
    var first = operations.CompressFolderAsync(source).GetAwaiter().GetResult();
    var second = operations.CompressFolderAsync(source).GetAwaiter().GetResult();
    var third = operations.CompressFolderAsync(source).GetAwaiter().GetResult();
    Assert(Path.GetFileName(first.OutputPath) == "Source ü space.cfs", "first collision name is wrong");
    Assert(Path.GetFileName(second.OutputPath) == "Source ü space (2).cfs", "second collision name is wrong");
    Assert(Path.GetFileName(third.OutputPath) == "Source ü space (3).cfs", "third collision name is wrong");
    Assert(new[] { first.OutputPath, second.OutputPath, third.OutputPath }.All(path => CfsArchive.Validate(path).IsValid), "a collision archive is invalid");
    var extracted = Path.Combine(workspace.Root, "extracted"); CfsArchive.Load(first.OutputPath).ExtractAll(extracted);
    Assert(Snapshot(extracted).SequenceEqual(before), "compressed archive did not round-trip byte-for-byte");
    Assert(Snapshot(source).SequenceEqual(before), "source folder changed during compression");
    Assert(!Directory.EnumerateDirectories(workspace.Root, ".cfs-work-*").Any(), "successful compression leaked a work folder");
    return Task.CompletedTask;
}

static async Task ArchiveExtractionIsValidatedAndCollisionSafe()
{
    using var workspace = new TestWorkspace();
    var source = Path.Combine(workspace.Root, "source");
    Directory.CreateDirectory(Path.Combine(source, "nested"));
    File.WriteAllText(Path.Combine(source, "nested", "payload.txt"), "extraction content");
    var archivePath = Path.Combine(workspace.Root, "archive.cfs");
    CfsArchive.CreateFromFolder(source, archivePath);
    var operations = NewOperations();

    Directory.CreateDirectory(Path.Combine(workspace.Root, "archive extracted"));
    var first = await operations.ExtractArchiveAsync(archivePath);
    var second = await operations.ExtractArchiveAsync(archivePath);
    Assert(Path.GetFileName(first.OutputPath) == "archive extracted (2)", "first extraction folder name is wrong");
    Assert(Path.GetFileName(second.OutputPath) == "archive extracted (3)", "second extraction folder collision suffix is wrong");
    Assert(Snapshot(first.OutputPath).SequenceEqual(Snapshot(source)), "first extraction did not round-trip source bytes");
    Assert(Snapshot(second.OutputPath).SequenceEqual(Snapshot(source)), "second extraction did not round-trip source bytes");

    var corrupt = Path.Combine(workspace.Root, "corrupt.cfs");
    File.WriteAllBytes(corrupt, [1, 2, 3]);
    await AssertThrowsAsync<CfsArchiveException>(() => operations.ExtractArchiveAsync(corrupt));
    Assert(!Directory.Exists(Path.Combine(workspace.Root, "corrupt extracted")), "invalid archive extraction created a destination folder");
}

static async Task CancelledExtractionPreservesPartialOutput()
{
    using var workspace = new TestWorkspace();
    var source = Path.Combine(workspace.Root, "source");
    Directory.CreateDirectory(source);
    File.WriteAllText(Path.Combine(source, "first.txt"), "first");
    File.WriteAllText(Path.Combine(source, "second.txt"), "second");
    var archivePath = Path.Combine(workspace.Root, "interrupted.cfs");
    CfsArchive.CreateFromFolder(source, archivePath);
    var operations = NewOperations();
    using var cancellation = new CancellationTokenSource();
    var progress = new CallbackProgress<CfsProgress>(_ => cancellation.Cancel());

    try
    {
        await operations.ExtractArchiveAsync(archivePath, progress, cancellation.Token);
        throw new InvalidOperationException("cancelled extraction unexpectedly completed");
    }
    catch (CfsPartialExtractionException ex)
    {
        Assert(Directory.Exists(ex.OutputPath), "cancelled extraction did not preserve its partial output folder");
        Assert(!Path.GetFileName(ex.OutputPath).StartsWith(".cfs-extract-", StringComparison.OrdinalIgnoreCase), "partial output was left only in a hidden staging folder");
        Assert(Directory.EnumerateFiles(ex.OutputPath, "*.txt", SearchOption.AllDirectories).Any(), "cancelled extraction did not preserve any completed file");
    }
    Assert(!Directory.EnumerateDirectories(workspace.Root, ".cfs-extract-*").Any(), "cancelled extraction leaked its hidden staging folder");
}

static Task SourceTraversalIsSafe()
{
    using var workspace = new TestWorkspace();
    var source = Path.Combine(workspace.Root, "normal"); Directory.CreateDirectory(source);
    File.WriteAllText(Path.Combine(source, "x.txt"), "x");
    var withSeparator = source + Path.DirectorySeparatorChar;
    Assert(CfsSourcePathSafety.ValidateFolderRoot(withSeparator) == Path.GetFullPath(source), "trailing separator normalization changed the folder");
    AssertThrows<CfsArchiveException>(() => CfsSourcePathSafety.ValidateFolderRoot(Path.GetPathRoot(source)!));
    AssertThrows<CfsArchiveException>(() => CfsSourcePathSafety.ValidateFolderRoot(Path.Combine(source, "x.txt")));
    AssertThrows<DirectoryNotFoundException>(() => CfsSourcePathSafety.ValidateFolderRoot(Path.Combine(workspace.Root, "missing")));

    try
    {
        var longParent = workspace.Root;
        while (longParent.Length < 285) longParent = Path.Combine(longParent, "supported-long-segment");
        var longSource = Path.Combine(longParent, "Long Source"); Directory.CreateDirectory(longSource);
        File.WriteAllText(Path.Combine(longSource, "long.txt"), "long-path-content");
        var longOperations = NewOperations();
        var longFirst = longOperations.CompressFolderAsync(longSource).GetAwaiter().GetResult();
        var longSecond = longOperations.CompressFolderAsync(longSource).GetAwaiter().GetResult();
        Assert(longFirst.OutputPath.Length > 260 && CfsArchive.Validate(longFirst.OutputPath).IsValid, "supported long path did not create a valid archive");
        Assert(Path.GetFileName(longSecond.OutputPath) == "Long Source (2).cfs", "long-path collision suffix changed");
    }
    catch (PathTooLongException) { Console.WriteLine("EVIDENCE hostLongPathsSupported=False"); }

    var external = Path.Combine(workspace.Root, "external"); Directory.CreateDirectory(external);
    var externalFile = Path.Combine(external, "outside.txt"); File.WriteAllText(externalFile, "outside");
    var fileLink = Path.Combine(source, "linked-file.txt");
    var fileLinkCreated = TryCreateFileSymlink(fileLink, externalFile);
    var nestedLink = Path.Combine(source, "linked");
    CreateJunction(nestedLink, external);
    var rootLink = Path.Combine(workspace.Root, "root-link"); CreateJunction(rootLink, source);
    try
    {
        if (fileLinkCreated)
            AssertThrows<CfsArchiveException>(() => CfsArchive.CreateFromFolder(source, Path.Combine(workspace.Root, "unsafe-file.cfs")));
        else Console.WriteLine("EVIDENCE fileSymlinkSupported=False");
        if (File.Exists(fileLink)) File.Delete(fileLink);
        AssertThrows<CfsArchiveException>(() => CfsArchive.CreateFromFolder(source, Path.Combine(workspace.Root, "unsafe.cfs")));
        AssertThrows<CfsArchiveException>(() => CfsArchive.CreateFromFolder(rootLink, Path.Combine(workspace.Root, "unsafe-root.cfs")));
        Assert(!File.Exists(Path.Combine(workspace.Root, "unsafe.cfs")) && !File.Exists(Path.Combine(workspace.Root, "unsafe-root.cfs")), "reparse rejection left a final archive");
        Assert(!Directory.EnumerateFiles(workspace.Root, ".*.tmp").Any(), "failed direct Core creation left a temporary file");
    }
    finally
    {
        if (File.Exists(fileLink)) File.Delete(fileLink);
        if (Directory.Exists(nestedLink)) Directory.Delete(nestedLink);
        if (Directory.Exists(rootLink)) Directory.Delete(rootLink);
    }
    return Task.CompletedTask;
}

static async Task ProgressCoversScanningAndIsBestEffort()
{
    using var workspace = new TestWorkspace();
    var source = Path.Combine(workspace.Root, "slow scan"); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "a.txt"), "a");
    var surface = new RecordingProgress();
    var operations = new CfsCreationOperations(() => surface, TimeSpan.FromMilliseconds(20), (s, t, token) =>
    {
        Thread.Sleep(180); // injected expensive scan seam
        CfsArchive.CreateFromFolder(s, t, cancellationToken: token);
    });
    var result = await operations.CompressFolderAsync(source);
    Assert(surface.ShowCount == 1 && surface.CloseCount == 1 && surface.ShownAt < surface.ClosedAt, "slow scan did not show and close progress");
    Assert(result.Warning is null && File.Exists(result.OutputPath), "slow scan compression failed");

    var fastSurface = new RecordingProgress();
    var fast = new CfsCreationOperations(() => fastSurface, TimeSpan.FromSeconds(2));
    var structured = new List<CfsProgress>();
    await fast.CompressFolderAsync(source, new CallbackProgress<CfsProgress>(structured.Add));
    Assert(fastSurface.ShowCount == 0 && fastSurface.CloseCount == 0, "fast operation displayed progress");
    Assert(structured.Any(value => value.Phase == "Scanning source folder")
        && structured.Any(value => value.Phase == "Compressing files" && value.TotalItems == 1 && value.TotalBytes == 1),
        "compression did not report real structured scanning and byte progress");

    var factoryThrows = new CfsCreationOperations(() => throw new InvalidOperationException("surface unavailable"), TimeSpan.Zero);
    var fallback = await factoryThrows.CompressFolderAsync(source);
    Assert(File.Exists(fallback.OutputPath), "optional progress factory failure made compression fatal");
    Assert(!Directory.EnumerateDirectories(workspace.Root, ".cfs-work-*").Any(), "progress factory failure leaked workspace");

    var faulty = new RecordingProgress { ThrowOnShow = true, ThrowOnClose = true };
    var faultTolerant = new CfsCreationOperations(() => faulty, TimeSpan.Zero, (s, t, token) => { Thread.Sleep(50); CfsArchive.CreateFromFolder(s, t, cancellationToken: token); });
    var tolerated = await faultTolerant.CompressFolderAsync(source);
    Assert(File.Exists(tolerated.OutputPath) && faulty.ShowCount == 1 && faulty.CloseCount == 1, "progress surface fault changed archive success or leaked lifecycle");
}

static async Task CleanupWarningPreservesCommittedOutput()
{
    using var workspace = new TestWorkspace();
    var sourceA = MakeSource(workspace.Root, "owner source");
    var sourceB = MakeSource(workspace.Root, "forwarded source");
    var operations = CleanupFailingOperations();
    await using var registry = FakeRegistry(Path.Combine(workspace.Root, "mounts"));
    var handler = new CfsBrokerRequestHandler(registry, new NoOpExplorer(), false, () => { }, operations);
    var owner = await handler.HandleAsync(new BrokerRequest(1, "compress", SourcePath: sourceA));
    Assert(owner.Success && owner.Warning is not null && File.Exists(owner.OutputPath), "direct owner lost committed output on cleanup warning");
    Assert(CfsArchive.Validate(owner.OutputPath!).IsValid, "direct cleanup-warning output is not usable");
    var preservedAfterOwner = Directory.EnumerateDirectories(workspace.Root, ".cfs-work-*").ToArray();
    Assert(preservedAfterOwner.Length == 1 && File.Exists(Path.Combine(preservedAfterOwner[0], ".cfs-work-owner")), "cleanup warning did not preserve the marked workspace");

    using var stop = new CancellationTokenSource();
    var pipeName = "CFS.Creation.Cleanup." + Guid.NewGuid().ToString("N");
    var policy = ShortPolicy(TimeSpan.FromSeconds(3));
    var serverTask = new CfsBrokerPipeServer(pipeName, handler, deadlinePolicy: policy).RunAsync(stop.Token);
    try
    {
        var forwarded = await CfsBrokerPipeClient.SendAsync(pipeName, new BrokerRequest(1, "compress", SourcePath: sourceB), policy.CompressionClient);
        Assert(forwarded.Success && forwarded.Warning is not null && File.Exists(forwarded.OutputPath), "forwarded request lost committed output on cleanup warning");
        Assert(CfsArchive.Validate(forwarded.OutputPath!).IsValid, "forwarded cleanup-warning output is not usable");
        Assert(Directory.EnumerateDirectories(workspace.Root, ".cfs-work-*").Count() == 2, "forwarded cleanup warning did not preserve its hidden workspace");
    }
    finally { stop.Cancel(); await serverTask; }

    var original = new InvalidOperationException("pre-commit failure");
    var precommit = new CfsCreationOperations(() => new RecordingProgress(), TimeSpan.Zero, (_, target, _) =>
    {
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(target)!, ".cfs-work-owner"), "foreign");
        throw original;
    });
    try { await precommit.CompressFolderAsync(MakeSource(workspace.Root, "precommit")); throw new InvalidOperationException("expected pre-commit failure"); }
    catch (InvalidOperationException ex) when (ReferenceEquals(ex, original)) { }
    Assert(!File.Exists(Path.Combine(workspace.Root, "precommit.cfs")), "pre-commit failure created a final archive");
    File.WriteAllText(Path.Combine(workspace.Root, "unrelated.txt"), "keep");
    Assert(File.ReadAllText(Path.Combine(workspace.Root, "unrelated.txt")) == "keep", "cleanup removed an unrelated file");
}

static async Task DeadlinePolicyAndTimeoutParity()
{
    AssertThrows<ArgumentOutOfRangeException>(() => new CfsBrokerDeadlinePolicy(TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)));
    AssertThrows<ArgumentException>(() => new CfsBrokerDeadlinePolicy(TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
    var policy = ShortPolicy(TimeSpan.FromMilliseconds(120));
    Assert(policy.CompressionClient > policy.CompressionHandler + policy.ResponseWrite, "client lacks server response margin");
    using var workspace = new TestWorkspace(); var source = MakeSource(workspace.Root, "timeout source");
    var operations = new CfsCreationOperations(() => new RecordingProgress(), TimeSpan.Zero, (_, _, token) => token.WaitHandle.WaitOne());
    await using var registry = FakeRegistry(Path.Combine(workspace.Root, "mounts"));
    var handler = new CfsBrokerRequestHandler(registry, new NoOpExplorer(), false, () => { }, operations);
    BrokerResponse direct;
    using (var deadline = new CancellationTokenSource(policy.HandlerFor("compress")))
    {
        try { direct = await handler.HandleAsync(new BrokerRequest(1, "compress", SourcePath: source), deadline.Token); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { direct = policy.TimeoutResponse("compress"); }
    }
    using var stop = new CancellationTokenSource(); var pipeName = "CFS.Creation.Deadline." + Guid.NewGuid().ToString("N");
    var serverTask = new CfsBrokerPipeServer(pipeName, handler, deadlinePolicy: policy).RunAsync(stop.Token);
    try
    {
        var forwarded = await CfsBrokerPipeClient.SendAsync(pipeName, new BrokerRequest(1, "compress", SourcePath: source), policy.CompressionClient);
        Assert(!forwarded.Success && forwarded.ErrorCode == "request-timeout", "server timeout did not arrive before client deadline");
        Assert(forwarded.ErrorCode == direct.ErrorCode && forwarded.Message == direct.Message, "owner and forwarded timeout mappings differ");
    }
    finally { stop.Cancel(); await serverTask; }
}

static async Task ConcurrentServerRemainsResponsive()
{
    using var workspace = new TestWorkspace(); var source = MakeSource(workspace.Root, "held source");
    using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
    var operations = new CfsCreationOperations(() => new RecordingProgress(), TimeSpan.Zero, (s, t, token) =>
    {
        entered.Set(); WaitHandle.WaitAny([release.WaitHandle, token.WaitHandle]); token.ThrowIfCancellationRequested();
        CfsArchive.CreateFromFolder(s, t, cancellationToken: token);
    });
    await using var registry = FakeRegistry(Path.Combine(workspace.Root, "mounts"));
    var handler = new CfsBrokerRequestHandler(registry, new NoOpExplorer(), false, () => { }, operations);
    var policy = ShortPolicy(TimeSpan.FromSeconds(5));
    using var stop = new CancellationTokenSource(); var pipeName = "CFS.Creation.Concurrent." + Guid.NewGuid().ToString("N");
    var serverTask = new CfsBrokerPipeServer(pipeName, handler, deadlinePolicy: policy).RunAsync(stop.Token);
    try
    {
        var compress = CfsBrokerPipeClient.SendAsync(pipeName, new BrokerRequest(1, "compress", SourcePath: source), policy.CompressionClient);
        Assert(entered.Wait(TimeSpan.FromSeconds(2)), "held compression did not start");
        var watch = Stopwatch.StartNew();
        var status = await CfsBrokerPipeClient.SendAsync(pipeName, new BrokerRequest(1, "status"), policy.StandardClient);
        Assert(status.Success && watch.Elapsed < TimeSpan.FromSeconds(1), "held compression monopolized status IPC");
        release.Set(); Assert((await compress).Success, "held compression failed after release");

        for (var i = 0; i < 24; i++)
        {
            await using var aborted = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await aborted.ConnectAsync(2000); // dispose without a frame
        }
        var recovered = await CfsBrokerPipeClient.SendAsync(pipeName, new BrokerRequest(1, "status"), policy.StandardClient);
        Assert(recovered.Success, "aborted connections permanently reduced server capacity");
    }
    finally { release.Set(); stop.Cancel(); await serverTask; }
}

static async Task CreationErrorsAreActionable()
{
    using var workspace = new TestWorkspace(); await using var registry = FakeRegistry(Path.Combine(workspace.Root, "mounts"));
    var handler = new CfsBrokerRequestHandler(registry, new NoOpExplorer(), false, () => { }, NewOperations());
    var missing = await handler.HandleAsync(new BrokerRequest(1, "compress", SourcePath: Path.Combine(workspace.Root, "missing")));
    Assert(!missing.Success && missing.ErrorCode == "compress-failed" && !string.IsNullOrWhiteSpace(missing.Message), "missing folder error is not actionable");
    var wrong = Path.Combine(workspace.Root, "not-cfs.zip");
    var create = await handler.HandleAsync(new BrokerRequest(1, "create-empty", TargetPath: wrong));
    Assert(!create.Success && create.ErrorCode == "create-empty-failed", "invalid empty target lacked a bounded error");
    var unknown = await handler.HandleAsync(new BrokerRequest(1, "erase"));
    Assert(unknown.ErrorCode == CfsBrokerErrorCodes.InvalidRequest && unknown.Message!.Contains("create-empty") && unknown.Message.Contains("compress"), "supported command message is stale");
}

static Task ShellRegistrationIsExactAndSafe()
{
    if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
    using var workspace = new TestWorkspace(); var root = FindRepositoryRoot();
    var built = Path.Combine(root, "src", "Cfs.CommandClient", "bin", "Release", "net8.0-windows", "Cfs.CommandClient.exe");
    var broker = Path.Combine(workspace.Root, "Program Files", "CFS Beta", "Cfs.CommandClient.exe"); Directory.CreateDirectory(Path.GetDirectoryName(broker)!); File.Copy(built, broker);
    var template = Path.Combine(workspace.Root, "ShellNew", "CFS-Empty.cfs"); Directory.CreateDirectory(Path.GetDirectoryName(template)!); CfsArchive.CreateEmpty(template);
    var basePath = $"Software\\CFS-Creation-Tests\\{Guid.NewGuid():N}\\Classes";
    var allOwnedBase = $"Software\\CFS-Creation-Tests\\{Guid.NewGuid():N}\\Classes";
    var stagePath = Path.Combine(root, "obj", "CfsCreationStage-" + Guid.NewGuid().ToString("N"));
    try
    {
        var dry = RunScript(root, broker, template, basePath, dryRun: true, unregister: false);
        Assert(dry.ExitCode == 0 && dry.Output.Contains("OPEN_COMMAND=" + CfsShellRegistration.BuildOpenCommand(broker))
            && dry.Output.Contains("SHELLNEW_FILENAME=" + Path.GetFullPath(template))
            && dry.Output.Contains("FOLDER_VERB_LABEL=Compress to CFS")
            && dry.Output.Contains("FOLDER_VERB_COMMAND=" + CfsShellRegistration.BuildCompressCommand(broker))
            && dry.Output.Contains("CREATE_HERE_VERB_LABEL=Create empty CFS archive here")
            && dry.Output.Contains("CREATE_HERE_VERB_COMMAND=" + CfsShellRegistration.BuildCreateHereCommand(broker))
            && dry.Output.Contains("CREATE_IN_FOLDER_VERB_LABEL=Create empty CFS archive inside")
            && dry.Output.Contains("CREATE_IN_FOLDER_VERB_COMMAND=" + CfsShellRegistration.BuildCreateInFolderCommand(broker))
            && dry.Output.Contains("EXTRACT_VERB_LABEL=Extract entire CFS archive")
            && dry.Output.Contains("EXTRACT_VERB_COMMAND=" + CfsShellRegistration.BuildExtractCommand(broker)), "dry-run registry values are not exact");
        Assert(RunScript(root, broker, template, basePath, false, false).ExitCode == 0, "isolated registration failed");
        using (var command = Registry.CurrentUser.CreateSubKey(basePath + @"\CFS.Archive\shell\open\command"))
        { command.SetValue("ForeignValue", "preserve"); command.CreateSubKey("ForeignChild")?.Dispose(); }
        File.Delete(broker); File.Delete(template);
        Assert(RunScript(root, broker, template, basePath, false, true).ExitCode == 0, "unregister required missing binaries");
        using var retained = Registry.CurrentUser.OpenSubKey(basePath + @"\CFS.Archive\shell\open\command");
        Assert(retained is not null, "unregister removed the foreign-owned command key");
        Assert(retained!.GetValue("ForeignValue")?.ToString() == "preserve" && retained.OpenSubKey("ForeignChild") is not null, "unregister removed foreign sibling data");
        Assert(retained.GetValue(null) is null, "unregister retained owned open command");
        Assert(Registry.CurrentUser.OpenSubKey(basePath + @"\.cfs") is null, "unregister retained owned extension/ShellNew keys");
        Assert(Registry.CurrentUser.OpenSubKey(basePath + @"\Directory\shell\CFS.Compress") is null, "unregister retained owned folder verb keys");
        Assert(Registry.CurrentUser.OpenSubKey(basePath + @"\Directory\Background\shell\CFS.Create") is null
            && Registry.CurrentUser.OpenSubKey(basePath + @"\Directory\shell\CFS.Create") is null,
            "unregister retained owned create verb keys");
        Assert(Registry.CurrentUser.OpenSubKey(basePath + @"\CFS.Archive\shell\CFS.Extract") is null, "unregister retained owned extract verb keys");

        // A tree containing only exact CFS-owned values must prune completely.
        var broker2 = Path.Combine(workspace.Root, "second", "Cfs.CommandClient.exe"); Directory.CreateDirectory(Path.GetDirectoryName(broker2)!); File.Copy(built, broker2);
        var template2 = Path.Combine(workspace.Root, "second", "ShellNew", "CFS-Empty.cfs"); Directory.CreateDirectory(Path.GetDirectoryName(template2)!); CfsArchive.CreateEmpty(template2);
        Assert(RunScript(root, broker2, template2, allOwnedBase, false, false).ExitCode == 0, "all-owned isolated registration failed");
        Assert(RunScript(root, broker2, template2, allOwnedBase, false, true).ExitCode == 0, "all-owned isolated unregister failed");
        Assert(Registry.CurrentUser.OpenSubKey(allOwnedBase + @"\.cfs") is null
            && Registry.CurrentUser.OpenSubKey(allOwnedBase + @"\CFS.Archive") is null
            && Registry.CurrentUser.OpenSubKey(allOwnedBase + @"\Directory") is null, "all-CFS-owned registry tree was not fully pruned");

        var installer = File.ReadAllText(Path.Combine(root, "packaging", "CFS-Setup.iss"));
        Assert(!installer.Contains("RegDeleteKeyIncludingSubkeys(HKLM, 'Software\\Classes\\CFS.Archive\\shell\\open\\command')")
            && !installer.Contains("RegDeleteKeyIncludingSubkeys(HKLM, 'Software\\Classes\\Directory\\shell\\CFS.Compress\\command')"), "installer still deletes foreign command subtrees");
        Assert(installer.Contains("RegDeleteValue(HKLM, 'Software\\Classes\\CFS.Archive\\shell\\open\\command', '')")
            && installer.Contains("RegDeleteKeyIfEmpty(HKLM, 'Software\\Classes\\CFS.Archive\\shell\\open\\command')"), "installer lacks value-level ownership cleanup");
        Assert(installer.Contains("ValueData: \"CFS Compressed Folder\"")
            && installer.Contains("ValueData: \"{app}\\ShellNew\\CFS-Empty.cfs\"")
            && !installer.Contains("Flags: uninsdelete", StringComparison.OrdinalIgnoreCase)
            && installer.Contains("InstalledCommitCommand :=", StringComparison.Ordinal)
            && installer.Contains("InstalledDiscardCommand :=", StringComparison.Ordinal)
            && installer.Contains("InstalledStatusCommand :=", StringComparison.Ordinal)
            && installer.Contains("RegDeleteValue(HKLM, 'Software\\Classes\\CFS.Archive\\shell\\CFS.Discard\\command', '')", StringComparison.Ordinal),
            "installer label/template or ownership-safe value-matching cleanup is incomplete");
        var mainForm = File.ReadAllText(Path.Combine(root, "src", "Cfs.App", "MainForm.cs"));
        Assert(mainForm.Contains("typeKey.SetValue(null, \"CFS Compressed Folder\")")
            && mainForm.Contains("Path.Combine(AppContext.BaseDirectory, \"ShellNew\", \"CFS-Empty.cfs\")"), "MainForm registration label/template is inconsistent");

        var staged = RunDeveloperStage(root, stagePath);
        Assert(staged.ExitCode == 0 && staged.Output.Contains("DEVELOPER_STAGE_SHELLNEW_VALID=True"), "developer staging did not verify ShellNew output: " + staged.Output + staged.Error);
        var stagedTemplate = Path.Combine(stagePath, "ShellNew", "CFS-Empty.cfs");
        Assert(File.Exists(Path.Combine(stagePath, "Cfs.App.exe")) && File.Exists(Path.Combine(stagePath, "Cfs.Broker.exe")) && File.Exists(Path.Combine(stagePath, "Cfs.CommandClient.exe")), "developer stage lacks App, Broker, or CommandClient");
        Assert(File.Exists(stagedTemplate) && CfsArchive.Validate(stagedTemplate).IsValid && CfsArchive.Load(stagedTemplate).ListEntries().Count == 0,
            "staged ShellNew template is not valid empty CFS1/v1");
        var stagedBytes = File.ReadAllBytes(stagedTemplate);
        Assert(stagedBytes.AsSpan(0, 4).SequenceEqual("CFS1"u8) && BitConverter.ToInt32(stagedBytes, 4) == 1, "staged ShellNew template header/version is wrong");
    }
    finally
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(basePath.Replace("\\Classes", ""), false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(allOwnedBase.Replace("\\Classes", ""), false); } catch { }
        if (Directory.Exists(stagePath)) Directory.Delete(stagePath, true);
    }
    return Task.CompletedTask;
}

static async Task RealProcessCreationWorkflow()
{
    if (!OperatingSystem.IsWindows()) return;
    using var workspace = new TestWorkspace(); var root = FindRepositoryRoot();
    var brokerExe = Path.Combine(root, "src", "Cfs.Broker", "bin", "Release", "net8.0-windows", "Cfs.Broker.exe");
    var project = File.ReadAllText(Path.Combine(root, "src", "Cfs.Broker", "Cfs.Broker.csproj"));
    Assert(project.Contains("<OutputType>WinExe</OutputType>"), "shell handler is console-capable");
    var source = MakeSource(workspace.Root, "Process Source ü");
    var empty = Path.Combine(workspace.Root, "Empty via broker.cfs"); var suffix = "Creation-" + Guid.NewGuid().ToString("N");
    var existingApps = Process.GetProcessesByName("Cfs.App").Select(p => p.Id).ToHashSet(); var spawned = new List<Process>();
    string? mountPath = null;
    try
    {
        var create = StartBroker(brokerExe, suffix, "create-empty", empty, Path.Combine(workspace.Root, "create.json")); spawned.Add(create);
        var created = await ReadResponse(Path.Combine(workspace.Root, "create.json"));
        Assert(created.Success && File.Exists(empty), "real broker did not create empty archive");
        var renamedEmpty = Path.Combine(workspace.Root, "Renamed Empty via broker.cfs");
        File.Move(empty, renamedEmpty); empty = renamedEmpty;
        var compress = StartBroker(brokerExe, suffix, "compress", source, Path.Combine(workspace.Root, "compress.json")); spawned.Add(compress);
        var compressed = await ReadResponse(Path.Combine(workspace.Root, "compress.json"));
        Assert(compressed.Success && CfsArchive.Validate(compressed.OutputPath!).IsValid, "real broker compression failed");
        Assert(compress.WaitForExit(15000) && compress.ExitCode == 0, "forwarded compression client remained resident");
        var hash = Hash(empty);
        var open = StartBroker(brokerExe, suffix, "open", empty, Path.Combine(workspace.Root, "open.json")); spawned.Add(open);
        var opened = await ReadResponse(Path.Combine(workspace.Root, "open.json")); mountPath = opened.MountPath;
        Assert(opened.Success && Directory.Exists(mountPath) && Hash(empty) == hash,
            $"broker open rewrote or failed to mount empty archive: code={opened.ErrorCode} message={opened.Message} mount={mountPath}");
        Assert(open.WaitForExit(15000) && open.ExitCode == 0, "forwarded renamed-archive open client remained resident");
        Assert(!Process.GetProcessesByName("Cfs.App").Any(p => !existingApps.Contains(p.Id)), "creation workflow launched Cfs.App");
        var shutdown = StartBroker(brokerExe, suffix, "shutdown", null, Path.Combine(workspace.Root, "shutdown.json")); spawned.Add(shutdown);
        Assert((await ReadResponse(Path.Combine(workspace.Root, "shutdown.json"))).Success, "controlled shutdown failed");
        Assert(create.WaitForExit(15000), "owner broker survived shutdown");
        Assert(!Directory.Exists(mountPath), "controlled shutdown left its test-owned mount");
        Console.WriteLine($"EVIDENCE brokerPid={created.BrokerProcessId} noCfsApp=True emptyValid=True compressValid=True mountClean=True");
    }
    finally
    {
        foreach (var process in spawned)
        {
            try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); } } catch { }
            process.Dispose();
        }
        if (mountPath is not null && Directory.Exists(mountPath) && File.Exists(Path.Combine(mountPath, ".cfs-mount-session"))) Directory.Delete(mountPath, true);
    }
}

static CfsCreationOperations NewOperations() => new(() => new RecordingProgress(), TimeSpan.FromSeconds(10));
static CfsCreationOperations CleanupFailingOperations() => new(() => new RecordingProgress(), TimeSpan.FromSeconds(10), (source, target, token) =>
{
    CfsArchive.CreateFromFolder(source, target, cancellationToken: token);
    File.WriteAllText(Path.Combine(Path.GetDirectoryName(target)!, ".cfs-work-owner"), "foreign");
});
static string MakeSource(string root, string name) { var source = Path.Combine(root, name); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "file.txt"), name); return source; }
static CfsBrokerDeadlinePolicy ShortPolicy(TimeSpan compression) => new(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2), compression, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(3), compression + TimeSpan.FromSeconds(1));
static CfsBrokerSessionRegistry FakeRegistry(string root) => new(root, (_, mount, _) => { Directory.CreateDirectory(mount); return Task.FromResult<ICfsBrokerSession>(new FakeSession(mount)); });
static IEnumerable<string> Snapshot(string folder) => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).OrderBy(x => Path.GetRelativePath(folder, x), StringComparer.OrdinalIgnoreCase).Select(x => Path.GetRelativePath(folder, x).Replace('\\', '/') + ":" + Hash(x)).ToArray();
static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
static void CreateJunction(string link, string target)
{
    var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in new[] { "/d", "/c", "mklink", "/J", link, target }) info.ArgumentList.Add(arg);
    using var process = Process.Start(info)!; var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException($"Could not create reparse fixture: {output} {error}");
}
static bool TryCreateFileSymlink(string link, string target)
{
    try { File.CreateSymbolicLink(link, target); return true; }
    catch (UnauthorizedAccessException) { return false; }
    catch (IOException ex) when (ex.HResult == unchecked((int)0x80070522)) { return false; }
}

static (int ExitCode, string Output, string Error) RunScript(string root, string broker, string template, string registryBase, bool dryRun, bool unregister)
{
    var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "tools", "Register-CfsFileAssociation.ps1"), "-BrokerPath", broker, "-EmptyTemplatePath", template, "-RegistryBasePath", registryBase }) info.ArgumentList.Add(arg);
    if (dryRun) info.ArgumentList.Add("-DryRun"); if (unregister) info.ArgumentList.Add("-Unregister");
    using var process = Process.Start(info)!; var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit(); return (process.ExitCode, output, error);
}
static (int ExitCode, string Output, string Error) RunDeveloperStage(string root, string stagePath)
{
    var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "tools", "Publish-CfsPrototype.ps1"), "-DeveloperStaging", "-OutputPath", stagePath }) info.ArgumentList.Add(arg);
    info.Environment["DOTNET_ROOT"] = @"C:\Program Files\dotnet";
    info.Environment["NUGET_PACKAGES"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    info.Environment["CFS_TEST_LOG_DIRECTORY"] = Path.Combine(root, "obj", "CfsCreationTestLogs");
    info.Environment.Remove("MSBuildSDKsPath");
    using var process = Process.Start(info)!; var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit(); return (process.ExitCode, output, error);
}

static Process StartBroker(string exe, string suffix, string command, string? input, string response)
{
    var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
    info.ArgumentList.Add(command); if (input is not null) info.ArgumentList.Add(input); info.ArgumentList.Add("--response-file"); info.ArgumentList.Add(response);
    info.Environment["CFS_BROKER_INSTANCE_SUFFIX"] = suffix; info.Environment["CFS_BROKER_ALLOW_SHUTDOWN"] = "1"; info.Environment["CFS_BROKER_DISABLE_EXPLORER"] = "1";
    info.Environment["CFS_BROKER_TEST_LOG_DIRECTORY"] = Path.Combine(Path.GetDirectoryName(response)!, "logs"); info.Environment["DOTNET_ROOT"] = @"C:\Program Files\dotnet"; info.Environment.Remove("MSBuildSDKsPath");
    return Process.Start(info)!;
}
static async Task<BrokerResponse> ReadResponse(string path)
{
    var deadline = DateTime.UtcNow.AddSeconds(15);
    while (DateTime.UtcNow < deadline) { try { if (File.Exists(path)) { var result = JsonSerializer.Deserialize<BrokerResponse>(await File.ReadAllTextAsync(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)); if (result is not null) return result; } } catch (IOException) { } catch (JsonException) { } await Task.Delay(50); }
    throw new TimeoutException("No broker response: " + path);
}
static string FindRepositoryRoot() { var current = new DirectoryInfo(AppContext.BaseDirectory); while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Build.props"))) current = current.Parent; return current?.FullName ?? throw new DirectoryNotFoundException(); }
static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static void AssertThrows<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
static async Task AssertThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

sealed class RecordingProgress : ICfsProgressSurface
{
    public int ShowCount; public int CloseCount; public DateTime ShownAt; public DateTime ClosedAt; public bool ThrowOnShow; public bool ThrowOnClose;
    public void Show(string message) { Interlocked.Increment(ref ShowCount); ShownAt = DateTime.UtcNow; if (ThrowOnShow) throw new InvalidOperationException("show"); }
    public void Close() { Interlocked.Increment(ref CloseCount); ClosedAt = DateTime.UtcNow; if (ThrowOnClose) throw new InvalidOperationException("close"); }
}
sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
sealed class NoOpExplorer : ICfsExplorerLauncher { public void OpenFolder(string folderPath) { } }
sealed class FakeSession(string mountPath) : ICfsBrokerSession { public string MountPath { get; } = mountPath; public ValueTask DisposeAsync() { if (Directory.Exists(MountPath)) Directory.Delete(MountPath, true); return ValueTask.CompletedTask; } }
sealed class TestWorkspace : IDisposable { public TestWorkspace() { Root = Path.Combine(Path.GetTempPath(), "cfs-creation-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); } public string Root { get; } public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); } }
