using Cfs.Core;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var harnessLogRoot = Path.Combine(Path.GetTempPath(), "cfs-tests", "harness-" + Environment.ProcessId);
Directory.CreateDirectory(harnessLogRoot);
CfsDiagnostics.Logger = new CfsDiagnosticLogger(harnessLogRoot);

var tests = new (string Name, Action Body)[]
{
    ("create/list/extract roundtrip", CreateListExtractRoundtrip),
    ("manifest entry reader hydrates one file", ManifestEntryReaderHydratesOneFile),
    ("ProjFS mount enumerates manifest and hydrates requested CFS file", ProjFsMountHydratesRequestedFile),
    ("ProjFS nonzero-offset partial read returns exact bytes and isolates unrelated entries", ProjFsNonzeroOffsetPartialReadIsExactAndIsolated),
    ("ProjFS concurrent partial reads return exact bytes and isolate unrelated entries", ProjFsConcurrentPartialReadsAreExactAndIsolated),
    ("ProjFS hydration cache evicts old payloads within fixed bounds", ProjFsHydrationCacheIsBounded),
    ("beta identity is centralized and consistent", BetaIdentityIsCentralizedAndConsistent),
    ("update manifests validate versions URLs and checksums", UpdateManifestsValidateVersionsUrlsAndChecksums),
    ("beta warning acknowledgement is version keyed", BetaWarningAcknowledgementIsVersionKeyed),
    ("diagnostic logging records lifecycle errors and protects private paths", DiagnosticLoggingRecordsLifecycleErrorsAndProtectsPrivatePaths),
    ("diagnostic locations and bug report actions are deterministic", DiagnosticLocationsAndBugReportActionsAreDeterministic),
    ("ProjFS prerequisites and mount policy never silently fall back", ProjFsPrerequisitesAndMountPolicyNeverSilentlyFallBack),
    ("writable storage policy accepts only local NTFS and rejects cloud paths", WritableStoragePolicyIsStrict),
    ("explicit compatibility mode persists edits", ExplicitCompatibilityModePersistsEdits),
    ("UI lifecycle states include exact cleanup failure path", UiLifecycleStatesIncludeExactCleanupFailurePath),
    ("ProjFS mount session persists Explorer-style edits without rewriting unchanged blocks", ProjFsMountSessionPersistsExplorerStyleEdits),
    ("ProjFS manual save followed by unmount succeeds", ProjFsManualSaveThenUnmountSucceeds),
    ("supported edits persist after reopen", SupportedEditsPersistAfterReopen),
    ("overwrites append without rewriting unchanged file blocks", OverwritesAppendWithoutRewritingUnchangedFileBlocks),
    ("mounted folder sync persists supported Explorer-style edits", MountedFolderSyncPersistsSupportedExplorerStyleEdits),
    ("mounted folder sync failure preserves archive", MountedFolderSyncFailurePreservesArchive),
    ("successful unmount permanently removes the CFS mount folder without Recycle Bin", SuccessfulUnmountPermanentlyRemovesMountFolder),
    ("failed save preserves the mount folder", FailedSavePreservesMountFolder),
    ("failed cleanup reports and preserves the mount folder", FailedCleanupReportsRemainingPath),
    ("unrelated folders cannot become CFS mount cleanup targets", UnrelatedFoldersCannotBecomeCleanupTargets),
    ("progress reports real archive and cleanup work", ProgressReportsRealWork),
    ("cancelling archive creation preserves existing archive", CancellationPreservesExistingArchive),
    ("validation detects corrupted file block", ValidationDetectsCorruptedFileBlock),
    ("validation reports extreme compression ratios", ValidationReportsExtremeCompressionRatio),
    ("archive path validation rejects hostile Windows names", ArchivePathValidationRejectsHostileNames),
    ("archive parser rejects oversized manifest metadata before allocation", ArchiveParserRejectsOversizedManifestMetadata),
    ("archive parser rejects duplicate and conflicting manifest paths", ArchiveParserRejectsDuplicateManifestPaths),
    ("manifest parser rejects hostile paths ranges methods and versions before projection", ManifestParserRejectsHostileStructureBeforeProjection),
    ("manifest parser enforces 16 TiB projected limit without hydration", ManifestParserEnforcesProjectedLimitWithoutHydration),
    ("deleting non-empty folder fails safely", DeletingNonEmptyFolderFailsSafely)
};

if (args.Length > 0)
{
    tests = tests.Where(test => args.Any(filter => test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();
    if (tests.Length == 0)
    {
        Console.WriteLine("No tests matched the supplied filters.");
        return 2;
    }
}

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Body();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {test.Name}: {ex}");
    }
}

Console.WriteLine($"TOTAL {tests.Length} PASS {tests.Length - failed} FAIL {failed}");
if (Directory.Exists(harnessLogRoot)) Directory.Delete(harnessLogRoot, recursive: true);
return failed == 0 ? 0 : 1;

static void CreateListExtractRoundtrip()
{
    using var workspace = new TestWorkspace();
    var source = Path.Combine(workspace.Root, "source");
    Directory.CreateDirectory(source);
    Directory.CreateDirectory(Path.Combine(source, "nested folder"));
    File.WriteAllText(Path.Combine(source, "hello world.txt"), "hello CFS", Encoding.UTF8);
    File.WriteAllBytes(Path.Combine(source, "empty.bin"), []);
    File.WriteAllBytes(Path.Combine(source, "nested folder", "binary.bin"), Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());

    var archivePath = Path.Combine(workspace.Root, "sample.cfs");
    CfsArchive.CreateFromFolder(source, archivePath);

    var archive = CfsArchive.Load(archivePath);
    var entries = archive.ListEntries();
    Assert(entries.Any(entry => entry.Path == "hello world.txt" && entry.Type == ArchiveEntryType.File), "file with spaces missing");
    Assert(entries.Any(entry => entry.Path == "nested folder" && entry.Type == ArchiveEntryType.Directory), "nested folder missing");
    Assert(entries.Any(entry => entry.Path == "empty.bin" && entry.OriginalSize == 0), "empty file missing");
    Assert(entries.Where(entry => entry.Type == ArchiveEntryType.File && entry.OriginalSize > 0)
        .All(entry => entry.CompressionMethod == CfsArchive.CompressionLzma2RawV2), "new non-empty files must use raw LZMA2");

    var extracted = Path.Combine(workspace.Root, "extracted");
    archive.ExtractAll(extracted);
    Assert(File.ReadAllText(Path.Combine(extracted, "hello world.txt"), Encoding.UTF8) == "hello CFS", "text file mismatch");
    Assert(File.ReadAllBytes(Path.Combine(extracted, "empty.bin")).Length == 0, "empty file mismatch");
    Assert(Sha256(Path.Combine(source, "nested folder", "binary.bin")) == Sha256(Path.Combine(extracted, "nested folder", "binary.bin")), "binary file mismatch");
}

static void ArchivePathValidationRejectsHostileNames()
{
    foreach (var path in new[] { "../escape.txt", "safe:stream", "CON", "folder/AUX.txt", "trailing.", "trailing ", new string('a', 256) })
    {
        try { _ = CfsArchive.NormalizeEntryPath(path); }
        catch (CfsArchiveException) { continue; }
        throw new InvalidOperationException($"Hostile path was accepted: {path}");
    }
    Assert(CfsArchive.NormalizeEntryPath("folder/ordinary-file.txt") == "folder/ordinary-file.txt", "ordinary archive path was rejected");
}

static void ArchiveParserRejectsOversizedManifestMetadata()
{
    using var workspace = new TestWorkspace();
    var path = Path.Combine(workspace.Root, "oversized-manifest.cfs");
    var length = CfsArchive.MaximumManifestBytes + 1L;
    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    {
        stream.Write("CFS1"u8);
        stream.Write(BitConverter.GetBytes(CfsArchive.FormatVersion));
        stream.Write(BitConverter.GetBytes(24L));
        stream.Write(BitConverter.GetBytes(length));
        stream.SetLength(24L + length);
    }
    try { _ = CfsArchive.Load(path); }
    catch (CfsArchiveException ex)
    {
        Assert(ex.Message.Contains("manifest location", StringComparison.OrdinalIgnoreCase), "oversized manifest did not fail at the metadata boundary");
        return;
    }
    throw new InvalidOperationException("oversized manifest metadata was accepted");
}

static void ArchiveParserRejectsDuplicateManifestPaths()
{
    using var workspace = new TestWorkspace();
    var duplicate = Path.Combine(workspace.Root, "duplicate.cfs");
    WriteManifestOnlyArchive(duplicate, [
        new CfsEntry { Path = "same", Type = ArchiveEntryType.Directory, CompressionMethod = CfsArchive.CompressionNone },
        new CfsEntry { Path = "same", Type = ArchiveEntryType.Directory, CompressionMethod = CfsArchive.CompressionNone }
    ]);
    var conflict = Path.Combine(workspace.Root, "conflict.cfs");
    WriteManifestOnlyArchive(conflict, [
        new CfsEntry { Path = "same", Type = ArchiveEntryType.Directory, CompressionMethod = CfsArchive.CompressionNone },
        new CfsEntry { Path = "same", Type = ArchiveEntryType.File, OriginalSize = 0, CompressedSize = 0, Offset = 24, CompressionMethod = CfsArchive.CompressionNone, Sha256 = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant() }
    ]);
    foreach (var path in new[] { duplicate, conflict })
    {
        try { _ = CfsArchive.Load(path); }
        catch (CfsArchiveException ex) when (ex.Message.Contains("duplicate or conflicting", StringComparison.OrdinalIgnoreCase)) { continue; }
        throw new InvalidOperationException("duplicate or conflicting manifest path was accepted: " + path);
    }
}

static void ManifestParserRejectsHostileStructureBeforeProjection()
{
    using var workspace = new TestWorkspace();
    foreach (var hostilePath in new[] { "../escape", "/absolute", "a//b", "safe:stream", "trailing.", "bad\u0001name" })
    {
        var path = Path.Combine(workspace.Root, Guid.NewGuid().ToString("N") + ".cfs");
        WriteManifestOnlyArchive(path, [
            new CfsEntry { Path = hostilePath, Type = ArchiveEntryType.Directory, CompressionMethod = CfsArchive.CompressionNone }
        ]);
        AssertThrows<CfsArchiveException>(() => CfsArchive.LoadManifestEntries(path),
            $"hostile manifest path reached projected metadata: {hostilePath}");
    }
    AssertThrows<CfsArchiveException>(() => CfsArchive.NormalizeEntryPath("bad\uD800name"),
        "unpaired Unicode surrogate was accepted");

    var caseCollision = Path.Combine(workspace.Root, "case-collision.cfs");
    WriteManifestOnlyArchive(caseCollision, [
        new CfsEntry { Path = "Folder", Type = ArchiveEntryType.Directory, CompressionMethod = CfsArchive.CompressionNone },
        new CfsEntry { Path = "folder", Type = ArchiveEntryType.Directory, CompressionMethod = CfsArchive.CompressionNone }
    ]);
    AssertThrows<CfsArchiveException>(() => CfsArchive.LoadManifestEntries(caseCollision),
        "case-colliding manifest paths reached projected metadata");

    var emptyHash = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();
    var ancestorConflict = Path.Combine(workspace.Root, "ancestor-conflict.cfs");
    WriteManifestOnlyArchive(ancestorConflict, [
        new CfsEntry { Path = "node", Type = ArchiveEntryType.File, Offset = 24, CompressionMethod = CfsArchive.CompressionNone, Sha256 = emptyHash },
        new CfsEntry { Path = "node/child", Type = ArchiveEntryType.File, Offset = 24, CompressionMethod = CfsArchive.CompressionNone, Sha256 = emptyHash }
    ]);
    AssertThrows<CfsArchiveException>(() => CfsArchive.LoadManifestEntries(ancestorConflict),
        "file/directory ancestor conflict reached projected metadata");

    var fakeHash = new string('0', 64);
    var overlap = Path.Combine(workspace.Root, "overlap.cfs");
    WriteArchiveWithData(overlap, new byte[8], [
        new CfsEntry { Path = "one", Type = ArchiveEntryType.File, OriginalSize = 1, CompressedSize = 4, Offset = 24, CompressionMethod = CfsArchive.CompressionLzma2RawV2, Sha256 = fakeHash },
        new CfsEntry { Path = "two", Type = ArchiveEntryType.File, OriginalSize = 1, CompressedSize = 4, Offset = 26, CompressionMethod = CfsArchive.CompressionLzma2RawV2, Sha256 = fakeHash }
    ]);
    AssertThrows<CfsArchiveException>(() => CfsArchive.LoadManifestEntries(overlap),
        "overlapping archive blocks reached projected metadata");

    var outOfRange = Path.Combine(workspace.Root, "out-of-range.cfs");
    WriteArchiveWithData(outOfRange, new byte[4], [
        new CfsEntry { Path = "outside", Type = ArchiveEntryType.File, OriginalSize = 1, CompressedSize = 8, Offset = 24, CompressionMethod = CfsArchive.CompressionLzma2RawV2, Sha256 = fakeHash }
    ]);
    AssertThrows<CfsArchiveException>(() => CfsArchive.LoadManifestEntries(outOfRange),
        "out-of-range archive block reached projected metadata");

    var unsupported = Path.Combine(workspace.Root, "unsupported-method.cfs");
    WriteArchiveWithData(unsupported, new byte[4], [
        new CfsEntry { Path = "unsupported", Type = ArchiveEntryType.File, OriginalSize = 1, CompressedSize = 4, Offset = 24, CompressionMethod = "future-codec", Sha256 = fakeHash }
    ]);
    AssertThrows<CfsArchiveException>(() => CfsArchive.LoadManifestEntries(unsupported),
        "unsupported compression method reached projected metadata");

    var future = Path.Combine(workspace.Root, "future.cfs");
    using (var stream = File.Create(future))
    {
        stream.Write("CFS1"u8);
        stream.Write(BitConverter.GetBytes(CfsArchive.FormatVersion + 1));
        stream.Write(new byte[16]);
    }
    AssertThrows<CfsArchiveException>(() => CfsArchive.LoadManifestEntries(future),
        "unsupported future archive version reached projected metadata");
}

static void ManifestParserEnforcesProjectedLimitWithoutHydration()
{
    using var workspace = new TestWorkspace();
    const int acceptedCount = 8192;
    var fakeHash = new string('0', 64);
    var acceptedEntries = Enumerable.Range(0, acceptedCount)
        .Select(index => new CfsEntry
        {
            Path = $"entry-{index:D5}",
            Type = ArchiveEntryType.File,
            OriginalSize = CfsArchive.MaximumEntryUncompressedBytes,
            CompressedSize = 1,
            Offset = 24L + index,
            CompressionMethod = CfsArchive.CompressionLzma2RawV2,
            Sha256 = fakeHash
        }).ToList();
    var accepted = Path.Combine(workspace.Root, "projected-16tib.cfs");
    WriteArchiveWithData(accepted, new byte[acceptedCount], acceptedEntries);
    var metadata = CfsArchive.LoadManifestEntries(accepted);
    Assert(metadata.Count == acceptedCount
        && metadata.Sum(entry => (decimal)entry.OriginalSize) < CfsArchive.MaximumTotalUncompressedBytes,
        "metadata-only parser did not accept the within-limit projected archive");

    acceptedEntries.Add(new CfsEntry
    {
        Path = "entry-over-limit",
        Type = ArchiveEntryType.File,
        OriginalSize = CfsArchive.MaximumEntryUncompressedBytes,
        CompressedSize = 1,
        Offset = 24L + acceptedCount,
        CompressionMethod = CfsArchive.CompressionLzma2RawV2,
        Sha256 = fakeHash
    });
    var rejected = Path.Combine(workspace.Root, "projected-over-limit.cfs");
    WriteArchiveWithData(rejected, new byte[acceptedCount + 1], acceptedEntries);
    AssertThrows<CfsArchiveException>(() => CfsArchive.LoadManifestEntries(rejected),
        "projected archive above 16 TiB was accepted");
}

static void WriteManifestOnlyArchive(string path, IReadOnlyList<CfsEntry> entries)
{
    var manifest = JsonSerializer.SerializeToUtf8Bytes(new CfsManifest { Entries = entries.ToList() });
    using var stream = File.Create(path);
    stream.Write("CFS1"u8);
    stream.Write(BitConverter.GetBytes(CfsArchive.FormatVersion));
    stream.Write(BitConverter.GetBytes(24L));
    stream.Write(BitConverter.GetBytes((long)manifest.Length));
    stream.Write(manifest);
}

static void WriteArchiveWithData(string path, byte[] data, IReadOnlyList<CfsEntry> entries)
{
    var manifest = JsonSerializer.SerializeToUtf8Bytes(new CfsManifest { Entries = entries.ToList() });
    using var stream = File.Create(path);
    stream.Write("CFS1"u8);
    stream.Write(BitConverter.GetBytes(CfsArchive.FormatVersion));
    stream.Write(BitConverter.GetBytes(24L + data.Length));
    stream.Write(BitConverter.GetBytes((long)manifest.Length));
    stream.Write(data);
    stream.Write(manifest);
}

static void ManifestEntryReaderHydratesOneFile()
{
    using var workspace = new TestWorkspace();
    var source = Path.Combine(workspace.Root, "source");
    Directory.CreateDirectory(source);
    File.WriteAllBytes(Path.Combine(source, "one.bin"), [1, 2, 3]);
    File.WriteAllBytes(Path.Combine(source, "two.bin"), [4, 5, 6]);
    var archivePath = Path.Combine(workspace.Root, "manifest.cfs");
    CfsArchive.CreateFromFolder(source, archivePath);

    var entries = CfsArchive.LoadManifestEntries(archivePath);
    Assert(entries.Count == 2, "manifest entry count mismatch");
    Assert(CfsArchive.ReadManifestEntry(archivePath, entries.Single(entry => entry.Path == "two.bin")).SequenceEqual(new byte[] { 4, 5, 6 }), "single entry hydration mismatch");
}

static void ProjFsMountHydratesRequestedFile()
{
    if (!OperatingSystem.IsWindows()) return;

    using var workspace = new TestWorkspace();
    var source = Path.Combine(workspace.Root, "source");
    Directory.CreateDirectory(Path.Combine(source, "nested"));
    var expected = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
    File.WriteAllBytes(Path.Combine(source, "nested", "payload.bin"), expected);
    var archivePath = Path.Combine(workspace.Root, "projected.cfs");
    CfsArchive.CreateFromFolder(source, archivePath);
    var mountRoot = Path.Combine(workspace.Root, "mount");

    using var mount = CfsProjFsMount.Create(archivePath, mountRoot);
    Assert(Directory.EnumerateDirectories(mountRoot).Select(Path.GetFileName).Contains("nested"), "ProjFS did not enumerate the manifest directory");
    Assert(mount.HydratedFileCount == 0, "directory enumeration hydrated CFS file data");
    var actual = File.ReadAllBytes(Path.Combine(mountRoot, "nested", "payload.bin"));
    Assert(actual.SequenceEqual(expected), "ProjFS file-data callback returned incorrect CFS bytes");
    Assert(mount.HydratedFileCount == 1, "requested file was not recorded as hydrated");
}

static void ProjFsNonzeroOffsetPartialReadIsExactAndIsolated()
{
    if (!OperatingSystem.IsWindows()) return;

    using var workspace = new TestWorkspace();
    var source = Path.Combine(workspace.Root, "source");
    Directory.CreateDirectory(source);
    var expected = CreatePayload(6 * 1024 * 1024, 17);
    File.WriteAllBytes(Path.Combine(source, "large.bin"), expected);
    File.WriteAllBytes(Path.Combine(source, "unrelated.bin"), CreatePayload(2 * 1024 * 1024, 91));
    var archivePath = Path.Combine(workspace.Root, "partial.cfs");
    CfsArchive.CreateFromFolder(source, archivePath);
    var mountRoot = Path.Combine(workspace.Root, "mount");
    using var mount = CfsProjFsMount.Create(archivePath, mountRoot);

    const int offset = 3 * 1024 * 1024;
    const int length = 8192;
    var actual = ReadRange(Path.Combine(mountRoot, "large.bin"), offset, length);

    Assert(actual.SequenceEqual(expected.AsSpan(offset, length).ToArray()), "nonzero-offset projected read did not match the exact source slice");
    Assert(CfsProjFsMount.GetRequestedBytes(expected, offset, length).SequenceEqual(actual), "provider nonzero-offset range slicing did not match the mounted read");
    Assert(mount.ReadRequests.Any(request => request.Path == "large.bin" && Covers(request, offset, length)), $"ProjFS callback range did not cover the application read: {FormatRequests(mount.ReadRequests)}");
    Assert(mount.HydratedPaths.SequenceEqual(new[] { "large.bin" }, StringComparer.OrdinalIgnoreCase), "partial read hydrated an unrelated archive entry");
    Assert(!mount.ReadRequests.Any(request => request.Path == "unrelated.bin"), "partial read requested the unrelated archive payload");

    mount.Dispose();
    Directory.Delete(mountRoot, recursive: true);
    Assert(!Directory.Exists(mountRoot), "partial-read test left its ProjFS mount directory behind");
}

static void ProjFsConcurrentPartialReadsAreExactAndIsolated()
{
    if (!OperatingSystem.IsWindows()) return;

    using var workspace = new TestWorkspace();
    var source = Path.Combine(workspace.Root, "source");
    Directory.CreateDirectory(source);
    var first = CreatePayload(8 * 1024 * 1024, 33);
    var second = CreatePayload(8 * 1024 * 1024, 67);
    File.WriteAllBytes(Path.Combine(source, "first.bin"), first);
    File.WriteAllBytes(Path.Combine(source, "second.bin"), second);
    File.WriteAllBytes(Path.Combine(source, "untouched.bin"), CreatePayload(1024 * 1024, 121));
    var archivePath = Path.Combine(workspace.Root, "concurrent.cfs");
    CfsArchive.CreateFromFolder(source, archivePath);
    var mountRoot = Path.Combine(workspace.Root, "mount");
    using var mount = CfsProjFsMount.Create(archivePath, mountRoot);

    const int firstOffset = 2 * 1024 * 1024;
    const int secondOffset = 5 * 1024 * 1024;
    const int firstLength = 32 * 1024;
    const int secondLength = 48 * 1024;
    using var start = new ManualResetEventSlim(false);
    var firstRead = Task.Run(() => { start.Wait(); return ReadRange(Path.Combine(mountRoot, "first.bin"), firstOffset, firstLength); });
    var secondRead = Task.Run(() => { start.Wait(); return ReadRange(Path.Combine(mountRoot, "second.bin"), secondOffset, secondLength); });
    start.Set();
    Task.WaitAll(firstRead, secondRead);

    Assert(firstRead.Result.SequenceEqual(first.AsSpan(firstOffset, firstLength).ToArray()), "concurrent first-file range did not match the exact source slice");
    Assert(secondRead.Result.SequenceEqual(second.AsSpan(secondOffset, secondLength).ToArray()), "concurrent second-file range did not match the exact source slice");
    Assert(CfsProjFsMount.GetRequestedBytes(first, firstOffset, firstLength).SequenceEqual(firstRead.Result), "provider first-file nonzero range slicing was incorrect");
    Assert(CfsProjFsMount.GetRequestedBytes(second, secondOffset, secondLength).SequenceEqual(secondRead.Result), "provider second-file nonzero range slicing was incorrect");
    Assert(mount.ReadRequests.Any(request => request.Path == "first.bin" && Covers(request, firstOffset, firstLength)), $"concurrent first-file callback range did not cover the application read: {FormatRequests(mount.ReadRequests)}");
    Assert(mount.ReadRequests.Any(request => request.Path == "second.bin" && Covers(request, secondOffset, secondLength)), $"concurrent second-file callback range did not cover the application read: {FormatRequests(mount.ReadRequests)}");
    Assert(mount.HydratedPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).SequenceEqual(new[] { "first.bin", "second.bin" }, StringComparer.OrdinalIgnoreCase), "concurrent reads hydrated the wrong archive entries");
    Assert(!mount.ReadRequests.Any(request => request.Path == "untouched.bin"), "concurrent reads requested the untouched archive payload");
    Assert(mount.HydrationJobLimit == Math.Min(8, Math.Max(2, Environment.ProcessorCount))
        && mount.MaximumObservedConcurrentHydrations <= mount.HydrationJobLimit,
        "ProjFS hydration exceeded its configured CPU-bounded concurrency limit");

    mount.Dispose();
    Directory.Delete(mountRoot, recursive: true);
    Assert(!Directory.Exists(mountRoot), "concurrent-read test left its ProjFS mount directory behind");
}

static byte[] ReadRange(string path, long offset, int length)
{
    return UnbufferedFileReader.Read(path, offset, length);
}

static string FormatRequests(IEnumerable<CfsProjFsReadRequest> requests) =>
    string.Join(", ", requests.Select(request => $"{request.Path}@{request.ByteOffset}+{request.Length}"));

static bool Covers(CfsProjFsReadRequest request, long offset, int length) =>
    request.ByteOffset <= (ulong)offset && request.ByteOffset + request.Length >= (ulong)(offset + length);

static byte[] CreatePayload(int length, int seed)
{
    var bytes = new byte[length];
    new Random(seed).NextBytes(bytes);
    return bytes;
}

static void ProjFsMountSessionPersistsExplorerStyleEdits()
{
    if (!OperatingSystem.IsWindows()) return;

    using var workspace = new TestWorkspace();
    var source = Path.Combine(workspace.Root, "source");
    Directory.CreateDirectory(Path.Combine(source, "folder"));
    Directory.CreateDirectory(Path.Combine(source, "empty"));
    File.WriteAllBytes(Path.Combine(source, "unchanged.bin"), Enumerable.Range(0, 8192).Select(i => (byte)(i % 239)).ToArray());
    File.WriteAllText(Path.Combine(source, "overwrite.txt"), "old", Encoding.UTF8);
    File.WriteAllText(Path.Combine(source, "delete.txt"), "delete", Encoding.UTF8);
    File.WriteAllText(Path.Combine(source, "folder", "rename.txt"), "rename", Encoding.UTF8);
    var archivePath = Path.Combine(workspace.Root, "session.cfs");
    var archive = CfsArchive.CreateFromFolder(source, archivePath);
    var unchangedOffset = archive.ListEntries().Single(entry => entry.Path == "unchanged.bin").Offset;

    var session = CfsMountSession.Create(archive, Path.Combine(workspace.Root, "mount"));
    File.WriteAllText(Path.Combine(session.FolderPath, "overwrite.txt"), "new", Encoding.UTF8);
    File.Delete(Path.Combine(session.FolderPath, "delete.txt"));
    Assert(!File.Exists(Path.Combine(session.FolderPath, "delete.txt")), "ProjFS delete did not remove the projected path before save");
    File.Move(Path.Combine(session.FolderPath, "folder", "rename.txt"), Path.Combine(session.FolderPath, "folder", "renamed.txt"));
    Directory.CreateDirectory(Path.Combine(session.FolderPath, "created"));
    File.WriteAllText(Path.Combine(session.FolderPath, "created", "new.txt"), "created", Encoding.UTF8);
    Directory.Delete(Path.Combine(session.FolderPath, "empty"));
    session.SaveAndUnmount(archive);

    Assert(!Directory.Exists(session.FolderPath), "ProjFS mount root remained after save/unmount");
    var reopened = CfsArchive.Load(archivePath);
    var entries = reopened.ListEntries();
    Assert(entries.Single(entry => entry.Path == "unchanged.bin").Offset == unchangedOffset, "ProjFS save rewrote unchanged compressed block");
    Assert(Encoding.UTF8.GetString(reopened.ReadFile("overwrite.txt")).TrimStart('\uFEFF') == "new", "ProjFS overwrite did not persist");
    Assert(!entries.Any(entry => entry.Path == "delete.txt"), "ProjFS delete did not persist");
    Assert(!entries.Any(entry => entry.Path == "folder/rename.txt"), "ProjFS rename source remained");
    Assert(Encoding.UTF8.GetString(reopened.ReadFile("folder/renamed.txt")).TrimStart('\uFEFF') == "rename", "ProjFS rename target missing");
    Assert(Encoding.UTF8.GetString(reopened.ReadFile("created/new.txt")).TrimStart('\uFEFF') == "created", "ProjFS created file missing");
    Assert(!entries.Any(entry => entry.Path == "empty"), "ProjFS empty directory delete did not persist");
}

static void ProjFsManualSaveThenUnmountSucceeds()
{
    if (!OperatingSystem.IsWindows()) return;

    using var workspace = new TestWorkspace();
    var source = Path.Combine(workspace.Root, "source");
    Directory.CreateDirectory(source);
    File.WriteAllText(Path.Combine(source, "file.txt"), "before", Encoding.UTF8);
    var archivePath = Path.Combine(workspace.Root, "repeat-save.cfs");
    var archive = CfsArchive.CreateFromFolder(source, archivePath);
    var mountPath = Path.Combine(workspace.Root, "mount");
    var session = CfsMountSession.Create(archive, mountPath);

    File.WriteAllText(Path.Combine(mountPath, "file.txt"), "first save", Encoding.UTF8);
    session.Save(archive);
    File.WriteAllText(Path.Combine(mountPath, "file.txt"), "second save", Encoding.UTF8);
    session.SaveAndUnmount(archive);

    Assert(!Directory.Exists(mountPath), "manual save followed by unmount left the mount folder behind");
    Assert(Encoding.UTF8.GetString(CfsArchive.Load(archivePath).ReadFile("file.txt")).TrimStart('\uFEFF') == "second save", "second save did not persist after provider was already stopped");
}

static void BetaIdentityIsCentralizedAndConsistent()
{
    Assert(CfsProductInfo.ReleaseIdentity == "0.3.0 Beta", $"unexpected release identity: {CfsProductInfo.ReleaseIdentity}");
    Assert(CfsProductInfo.DisplayName == "CFS 0.3.0 Beta", $"unexpected display name: {CfsProductInfo.DisplayName}");
    Assert(CfsProductInfo.WindowTitle == CfsProductInfo.DisplayName, "window title diverged from central identity");
    Assert(CfsProductInfo.AcknowledgementKey == CfsProductInfo.ReleaseIdentity, "warning acknowledgement is not keyed to the release identity");
    Assert(CfsProductInfo.BuildIdentifier.StartsWith("0.3.0-Beta-", StringComparison.Ordinal), $"build identifier lacks beta identity: {CfsProductInfo.BuildIdentifier}");
    Assert(CfsProductInfo.BetaInformation.Contains(CfsProductInfo.BetaSafetyWarning, StringComparison.Ordinal), "beta information omitted the backup warning");
    Assert(CfsProductInfo.BetaInformation.Contains(CfsProductInfo.BugReportDestination, StringComparison.Ordinal), "beta information omitted support/reporting instructions");
}

static void BetaWarningAcknowledgementIsVersionKeyed()
{
    using var workspace = new TestWorkspace();
    var acknowledgement = new CfsBetaAcknowledgement(Path.Combine(workspace.Root, "settings", "beta-warning.txt"));

    Assert(acknowledgement.ShouldShow("0.1.0 Beta"), "first beta use did not require the warning");
    acknowledgement.Acknowledge("0.1.0 Beta");
    Assert(!acknowledgement.ShouldShow("0.1.0 Beta"), "acknowledgement did not suppress repeat warning for the same beta");
    Assert(acknowledgement.ShouldShow("0.1.1 Beta"), "a beta version change did not require the warning again");
}

static void UpdateManifestsValidateVersionsUrlsAndChecksums()
{
    using var workspace = new TestWorkspace();
    var setupPath = Path.Combine(workspace.Root, "setup.exe");
    File.WriteAllBytes(setupPath, [1, 2, 3, 4]);
    var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(setupPath)));
    var json = $$"""
        { "schemaVersion": 1, "version": "0.1.1", "channel": "Beta", "publishedUtc": "2026-07-12T00:00:00Z", "architecture": "x64", "minimumWindowsBuild": 17763, "setupUrl": "https://example.invalid/setup.exe", "sha256": "{{hash}}", "releaseNotesUrl": "https://example.invalid/notes", "mandatory": false }
        """;
    var manifest = CfsUpdateManifest.Parse(json);
    Assert(manifest.IsNewerThan("0.1.0"), "newer update was not detected");
    Assert(!manifest.IsNewerThan("0.1.1"), "same-version update was treated as newer");
    Assert(manifest.VerifyFile(setupPath), "valid update checksum was rejected");
    File.AppendAllText(setupPath, "tampered");
    Assert(!manifest.VerifyFile(setupPath), "tampered update passed checksum verification");
    AssertThrows<InvalidDataException>(() => CfsUpdateManifest.Parse(json.Replace("https://example.invalid/setup.exe", "http://example.invalid/setup.exe")), "non-HTTPS setup URL was accepted");
    AssertThrows<InvalidDataException>(() => CfsUpdateManifest.Parse(json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2")), "unsupported update schema was accepted");
}

static void DiagnosticLoggingRecordsLifecycleErrorsAndProtectsPrivatePaths()
{
    using var workspace = new TestWorkspace();
    var logger = new CfsDiagnosticLogger(Path.Combine(workspace.Root, "logs"));
    logger.WriteStartup();
    var privateArchive = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "private", "customer-secret.cfs");
    foreach (var eventName in new[] { "mount", "projfs.initialize", "projfs.hydration", "archive.save", "archive.validation", "mount.cleanup" })
        logger.WritePathEvent(eventName, privateArchive, "success");
    try { ThrowDiagnosticFailure(privateArchive); }
    catch (Exception ex) { logger.WriteException("test.failure", ex); }

    var log = File.ReadAllText(logger.LogPath);
    Assert(System.Text.RegularExpressions.Regex.IsMatch(log, @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}"), "log omitted ISO timestamp");
    Assert(log.Contains($"version={CfsProductInfo.ReleaseIdentity}", StringComparison.Ordinal), "startup log omitted version");
    Assert(log.Contains($"build={CfsProductInfo.BuildIdentifier}", StringComparison.Ordinal), "startup log omitted build identifier");
    Assert(log.Contains($"windows={Environment.OSVersion.VersionString}", StringComparison.Ordinal), "startup log omitted Windows version");
    foreach (var eventName in new[] { "mount", "projfs.initialize", "projfs.hydration", "archive.save", "archive.validation", "mount.cleanup" })
        Assert(log.Contains($"event={eventName}", StringComparison.Ordinal), $"log omitted {eventName} lifecycle entry");
    Assert(log.Contains("exceptionType=System.InvalidOperationException", StringComparison.Ordinal), "exception type missing");
    Assert(log.Contains("message=diagnostic failure", StringComparison.Ordinal), "exception message missing");
    Assert(log.Contains(nameof(ThrowDiagnosticFailure), StringComparison.Ordinal), "exception stack trace missing");
    Assert(!log.Contains(privateArchive, StringComparison.OrdinalIgnoreCase), "log exposed the private archive path");
    Assert(!log.Contains("customer-secret.cfs", StringComparison.OrdinalIgnoreCase), "log exposed the private archive filename");
    Assert(log.Contains("path-id:", StringComparison.Ordinal), "log omitted the privacy-safe path identifier");
}

static void ThrowDiagnosticFailure(string privateArchive) =>
    throw new InvalidOperationException("diagnostic failure at " + privateArchive);

static void DiagnosticLocationsAndBugReportActionsAreDeterministic()
{
    var expectedDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CFS", "Logs");
    Assert(CfsDiagnostics.DefaultLogDirectory == expectedDirectory, "default log directory is not deterministic");
    var logger = new CfsDiagnosticLogger(expectedDirectory);
    Assert(logger.LogPath == Path.Combine(expectedDirectory, "CFS.log"), "log filename is not predictable");

    var projectDestination = CfsSupportActions.ResolveBugReportDestination(CfsProductInfo.BugReportDestination);
    Assert(projectDestination.IsConfigured && projectDestination.Destination?.Scheme == Uri.UriSchemeHttps, "project bug-report URL was not configured safely");

    var configured = CfsSupportActions.ResolveBugReportDestination("https://example.invalid/cfs-bugs");
    Assert(configured.IsConfigured && configured.Destination?.AbsoluteUri == "https://example.invalid/cfs-bugs", "valid configured bug-report URL was not accepted");
    var invalid = CfsSupportActions.ResolveBugReportDestination("file:///private/report.txt");
    Assert(!invalid.IsConfigured && invalid.Destination is null, "unsafe bug-report destination scheme was accepted");
}

static void ProjFsPrerequisitesAndMountPolicyNeverSilentlyFallBack()
{
    var wrongPlatform = CfsProjFsPrerequisite.Evaluate(false, false);
    Assert(!wrongPlatform.IsAvailable && wrongPlatform.Message.Contains("Windows 10", StringComparison.Ordinal), "unsupported Windows message lacks requirements");
    var missingFeature = CfsProjFsPrerequisite.Evaluate(true, false);
    Assert(!missingFeature.IsAvailable, "missing ProjectedFSLib was reported available");
    Assert(missingFeature.Message.Contains("Client-ProjFS", StringComparison.Ordinal) && missingFeature.Message.Contains("Windows Features", StringComparison.Ordinal), "ProjFS remediation is not useful");
    Assert(missingFeature.Message.Contains("did not fall back", StringComparison.Ordinal), "unavailable message does not state no fallback");

    var defaultDecision = CfsMountPolicy.Decide(missingFeature, compatibilityModeExplicitlySelected: false);
    Assert(!defaultDecision.CanMount && defaultDecision.Mode is null, "unavailable ProjFS silently selected another mount mode");
    var explicitCompatibility = CfsMountPolicy.Decide(missingFeature, compatibilityModeExplicitlySelected: true);
    Assert(explicitCompatibility.CanMount && explicitCompatibility.Mode == CfsMountMode.CompatibilityFullExtraction, "explicit Compatibility Mode was not honored");
    Assert(explicitCompatibility.Message.Contains("fully extracted", StringComparison.Ordinal) && explicitCompatibility.Message.Contains("not an on-demand ProjFS mount", StringComparison.Ordinal), "Compatibility Mode is not explicitly described");
    var available = CfsMountPolicy.Decide(CfsProjFsPrerequisite.Evaluate(true, true), compatibilityModeExplicitlySelected: false);
    Assert(available.CanMount && available.Mode == CfsMountMode.ProjFs, "available ProjFS did not remain the default");
}

static void ExplicitCompatibilityModePersistsEdits()
{
    using var workspace = new TestWorkspace();
    var source = Path.Combine(workspace.Root, "source");
    Directory.CreateDirectory(source);
    File.WriteAllText(Path.Combine(source, "edit.txt"), "before", Encoding.UTF8);
    var archivePath = Path.Combine(workspace.Root, "compatibility.cfs");
    var archive = CfsArchive.CreateFromFolder(source, archivePath);
    var mountPath = Path.Combine(workspace.Root, "compatibility-mount");
    var progress = new CaptureProgress();
    var session = CfsMountSession.CreateCompatibility(archive, mountPath, progress);
    Assert(session.Mode == CfsMountMode.CompatibilityFullExtraction, "session did not retain explicit Compatibility Mode identity");
    Assert(File.Exists(Path.Combine(mountPath, "edit.txt")), "Compatibility Mode did not fully extract the archive");
    Assert(progress.Reports.Any(report => report.Operation == "Extracting archive"), "Compatibility Mode lost extraction progress reporting");
    File.WriteAllText(Path.Combine(mountPath, "edit.txt"), "after", Encoding.UTF8);
    session.SaveAndUnmount(archive);
    Assert(!Directory.Exists(mountPath), "Compatibility Mode unmount left its folder behind");
    Assert(Encoding.UTF8.GetString(CfsArchive.Load(archivePath).ReadFile("edit.txt")).TrimStart('\uFEFF') == "after", "Compatibility Mode edit did not persist after reopen");
}

static void UiLifecycleStatesIncludeExactCleanupFailurePath()
{
    using var workspace = new TestWorkspace();
    var mountPath = Path.Combine(workspace.Root, "preserved mount");
    var model = new CfsUiStateModel();
    Assert(model.State == CfsUiLifecycleState.Unmounted && model.DisplayText == "Unmounted", "initial Unmounted state is unclear");
    model.Set(CfsUiLifecycleState.Mounting);
    Assert(model.DisplayText == "Mounting…", "Mounting state is unclear");
    model.Set(CfsUiLifecycleState.Mounted, mountPath);
    Assert(model.DisplayText.Contains(Path.GetFullPath(mountPath), StringComparison.Ordinal), "Mounted state omitted exact path");
    model.Set(CfsUiLifecycleState.Saving);
    Assert(model.DisplayText.Contains("Saving", StringComparison.Ordinal), "Saving state is unclear");
    model.Set(CfsUiLifecycleState.Validating);
    Assert(model.DisplayText.Contains("Validating", StringComparison.Ordinal), "Validating state is unclear");
    model.Set(CfsUiLifecycleState.CleanupFailed, mountPath);
    Assert(model.DisplayText.Contains("Cleanup failed", StringComparison.Ordinal) && model.DisplayText.Contains(Path.GetFullPath(mountPath), StringComparison.Ordinal), "cleanup-failed state omitted exact preserved path");

    var logger = new CfsDiagnosticLogger(Path.Combine(workspace.Root, "logs"));
    logger.WriteMountCleanupFailure(mountPath, new IOException("simulated lock"));
    Assert(File.ReadAllText(logger.LogPath).Contains(Path.GetFullPath(mountPath), StringComparison.Ordinal), "cleanup failure log omitted exact preserved mount path");
}

static void SupportedEditsPersistAfterReopen()
{
    using var workspace = new TestWorkspace();
    var source = Path.Combine(workspace.Root, "source");
    Directory.CreateDirectory(source);
    File.WriteAllText(Path.Combine(source, "edit.txt"), "old", Encoding.UTF8);
    File.WriteAllText(Path.Combine(source, "delete.txt"), "delete me", Encoding.UTF8);

    var archivePath = Path.Combine(workspace.Root, "editable.cfs");
    var archive = CfsArchive.CreateFromFolder(source, archivePath);

    archive.WriteFile("edit.txt", Encoding.UTF8.GetBytes("new"));
    archive.WriteFile("created file.txt", Encoding.UTF8.GetBytes("created"));
    archive.CreateDirectory("new folder");
    archive.Rename("created file.txt", "new folder/renamed.txt");
    archive.DeleteFile("delete.txt");
    archive.CreateDirectory("empty folder");
    archive.DeleteEmptyDirectory("empty folder");

    var reopened = CfsArchive.Load(archivePath);
    Assert(Encoding.UTF8.GetString(reopened.ReadFile("edit.txt")) == "new", "overwrite did not persist");
    Assert(Encoding.UTF8.GetString(reopened.ReadFile("new folder/renamed.txt")) == "created", "rename/create did not persist");
    Assert(!reopened.ListEntries().Any(entry => entry.Path == "delete.txt"), "delete did not persist");
    Assert(!reopened.ListEntries().Any(entry => entry.Path == "empty folder"), "empty folder delete did not persist");

    reopened.WriteFile("second reopen.txt", Encoding.UTF8.GetBytes("still valid"));
    var afterSecondSave = CfsArchive.Load(archivePath);
    Assert(Encoding.UTF8.GetString(afterSecondSave.ReadFile("second reopen.txt")) == "still valid", "archive invalid after repeated edit");
}

static void DeletingNonEmptyFolderFailsSafely()
{
    using var workspace = new TestWorkspace();
    var source = Path.Combine(workspace.Root, "source");
    Directory.CreateDirectory(Path.Combine(source, "folder"));
    File.WriteAllBytes(Path.Combine(source, "folder", "file.txt"), Encoding.UTF8.GetBytes("data"));

    var archivePath = Path.Combine(workspace.Root, "safe.cfs");
    var archive = CfsArchive.CreateFromFolder(source, archivePath);

    var failed = false;
    try
    {
        archive.DeleteEmptyDirectory("folder");
    }
    catch (CfsArchiveException)
    {
        failed = true;
    }

    Assert(failed, "non-empty folder delete should fail");
    var reopened = CfsArchive.Load(archivePath);
    var reopenedText = Encoding.UTF8.GetString(reopened.ReadFile("folder/file.txt"));
    Assert(reopenedText == "data", $"archive changed after failed delete: '{reopenedText}'");
}

static void OverwritesAppendWithoutRewritingUnchangedFileBlocks()
{
    using var workspace = new TestWorkspace();
    var source = Path.Combine(workspace.Root, "source");
    Directory.CreateDirectory(source);
    File.WriteAllBytes(Path.Combine(source, "large.bin"), Enumerable.Range(0, 128 * 1024).Select(i => (byte)(i % 251)).ToArray());
    File.WriteAllText(Path.Combine(source, "change.txt"), "before", Encoding.UTF8);

    var archivePath = Path.Combine(workspace.Root, "append.cfs");
    var archive = CfsArchive.CreateFromFolder(source, archivePath);
    var beforeLarge = archive.ListEntries().Single(entry => entry.Path == "large.bin");
    var beforeLength = new FileInfo(archivePath).Length;

    archive.WriteFile("change.txt", Encoding.UTF8.GetBytes("after"));

    var reopened = CfsArchive.Load(archivePath);
    var afterLarge = reopened.ListEntries().Single(entry => entry.Path == "large.bin");
    var afterLength = new FileInfo(archivePath).Length;

    Assert(afterLarge.Offset == beforeLarge.Offset, "unchanged file offset moved");
    Assert(afterLarge.CompressedSize == beforeLarge.CompressedSize, "unchanged file block changed");
    Assert(afterLength > beforeLength, "archive did not append a new manifest/data region");
    Assert(Encoding.UTF8.GetString(reopened.ReadFile("change.txt")) == "after", "changed file did not persist");
}

static void MountedFolderSyncPersistsSupportedExplorerStyleEdits()
{
    using var workspace = new TestWorkspace();
    var source = Path.Combine(workspace.Root, "source");
    Directory.CreateDirectory(Path.Combine(source, "folder"));
    Directory.CreateDirectory(Path.Combine(source, "empty folder"));
    File.WriteAllBytes(Path.Combine(source, "unchanged.bin"), Enumerable.Range(0, 4096).Select(i => (byte)(i % 193)).ToArray());
    File.WriteAllBytes(Path.Combine(source, "overwrite.txt"), Encoding.UTF8.GetBytes("old"));
    File.WriteAllBytes(Path.Combine(source, "delete.txt"), Encoding.UTF8.GetBytes("delete"));
    File.WriteAllBytes(Path.Combine(source, "folder", "rename-me.txt"), Encoding.UTF8.GetBytes("rename content"));

    var archivePath = Path.Combine(workspace.Root, "mounted.cfs");
    var archive = CfsArchive.CreateFromFolder(source, archivePath);
    var unchangedBefore = archive.ListEntries().Single(entry => entry.Path == "unchanged.bin");

    var mount = Path.Combine(workspace.Root, "mount");
    CfsFolderSync.PrepareMountFolder(archive, mount);
    File.WriteAllBytes(Path.Combine(mount, "overwrite.txt"), Encoding.UTF8.GetBytes("new"));
    File.Delete(Path.Combine(mount, "delete.txt"));
    File.Move(Path.Combine(mount, "folder", "rename-me.txt"), Path.Combine(mount, "folder", "renamed.txt"));
    Directory.CreateDirectory(Path.Combine(mount, "created folder"));
    File.WriteAllBytes(Path.Combine(mount, "created folder", "new.txt"), Encoding.UTF8.GetBytes("created"));
    Directory.Delete(Path.Combine(mount, "empty folder"));

    CfsFolderSync.ApplyFolderChanges(archive, mount);

    var reopened = CfsArchive.Load(archivePath);
    var entries = reopened.ListEntries();
    var unchangedAfter = entries.Single(entry => entry.Path == "unchanged.bin");
    Assert(unchangedAfter.Offset == unchangedBefore.Offset, "mount sync rewrote unchanged block");
    Assert(Encoding.UTF8.GetString(reopened.ReadFile("overwrite.txt")) == "new", "mount overwrite did not persist");
    Assert(!entries.Any(entry => entry.Path == "delete.txt"), "mount delete did not persist");
    Assert(!entries.Any(entry => entry.Path == "folder/rename-me.txt"), "mount rename source still exists");
    Assert(Encoding.UTF8.GetString(reopened.ReadFile("folder/renamed.txt")) == "rename content", "mount rename target missing");
    Assert(Encoding.UTF8.GetString(reopened.ReadFile("created folder/new.txt")) == "created", "mount created file missing");
    Assert(!entries.Any(entry => entry.Path == "empty folder"), "mount empty folder delete did not persist");
}

static void MountedFolderSyncFailurePreservesArchive()
{
    using var workspace = new TestWorkspace();
    var source = Path.Combine(workspace.Root, "source");
    Directory.CreateDirectory(source);
    File.WriteAllBytes(Path.Combine(source, "keep.txt"), Encoding.UTF8.GetBytes("original"));
    File.WriteAllBytes(Path.Combine(source, "remove.txt"), Encoding.UTF8.GetBytes("remove"));

    var archivePath = Path.Combine(workspace.Root, "readonly.cfs");
    var archive = CfsArchive.CreateFromFolder(source, archivePath);
    var mount = Path.Combine(workspace.Root, "mount");
    CfsFolderSync.PrepareMountFolder(archive, mount);
    File.WriteAllBytes(Path.Combine(mount, "keep.txt"), Encoding.UTF8.GetBytes("changed"));
    File.Delete(Path.Combine(mount, "remove.txt"));
    File.WriteAllBytes(Path.Combine(mount, "created.txt"), Encoding.UTF8.GetBytes("created"));

    File.SetAttributes(archivePath, File.GetAttributes(archivePath) | FileAttributes.ReadOnly);

    var failed = false;
    try
    {
        CfsFolderSync.ApplyFolderChanges(archive, mount);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        failed = true;
    }
    finally
    {
        File.SetAttributes(archivePath, File.GetAttributes(archivePath) & ~FileAttributes.ReadOnly);
    }

    Assert(failed, "read-only archive sync should fail");
    var reopened = CfsArchive.Load(archivePath);
    var entries = reopened.ListEntries();
    Assert(Encoding.UTF8.GetString(reopened.ReadFile("keep.txt")) == "original", "failed sync changed existing file");
    Assert(entries.Any(entry => entry.Path == "remove.txt"), "failed sync deleted file");
    Assert(!entries.Any(entry => entry.Path == "created.txt"), "failed sync created file");
}

static void SuccessfulUnmountPermanentlyRemovesMountFolder()
{
    using var workspace = new TestWorkspace();
    var source = Path.Combine(workspace.Root, "source");
    Directory.CreateDirectory(source);
    File.WriteAllText(Path.Combine(source, "file.txt"), "original", Encoding.UTF8);

    var archivePath = Path.Combine(workspace.Root, "mounted.cfs");
    var archive = CfsArchive.CreateFromFolder(source, archivePath);
    var mountPath = Path.Combine(workspace.Root, "cfs-owned-mount");
    var session = CfsMountSession.Create(archive, mountPath);
    File.WriteAllBytes(Path.Combine(mountPath, "file.txt"), Encoding.UTF8.GetBytes("saved"));

    session.SaveAndUnmount(archive);

    Assert(!Directory.Exists(mountPath), "permanent unmount left the mount folder behind");
    var reopened = CfsArchive.Load(archivePath);
    Assert(Encoding.UTF8.GetString(reopened.ReadFile("file.txt")) == "saved", "unmount did not save changes first");
    Assert(!reopened.ListEntries().Any(entry => entry.Path == ".cfs-mount-session"), "mount marker was saved into the archive");
}

static void FailedSavePreservesMountFolder()
{
    using var workspace = new TestWorkspace();
    var source = Path.Combine(workspace.Root, "source");
    Directory.CreateDirectory(source);
    File.WriteAllText(Path.Combine(source, "file.txt"), "original", Encoding.UTF8);

    var archivePath = Path.Combine(workspace.Root, "readonly.cfs");
    var archive = CfsArchive.CreateFromFolder(source, archivePath);
    var mountPath = Path.Combine(workspace.Root, "cfs-owned-mount");
    var session = CfsMountSession.Create(archive, mountPath);
    File.WriteAllText(Path.Combine(mountPath, "file.txt"), "unsaved", Encoding.UTF8);
    File.SetAttributes(archivePath, File.GetAttributes(archivePath) | FileAttributes.ReadOnly);

    var failed = false;
    try
    {
        session.SaveAndUnmount(archive);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        failed = true;
    }
    finally
    {
        File.SetAttributes(archivePath, File.GetAttributes(archivePath) & ~FileAttributes.ReadOnly);
    }

    Assert(failed, "read-only archive save should fail");
    Assert(Directory.Exists(mountPath), "failed save removed the mount folder");
    Assert(File.ReadAllText(Path.Combine(mountPath, "file.txt"), Encoding.UTF8) == "unsaved", "failed save changed the mount contents");
}

static void FailedCleanupReportsRemainingPath()
{
    using var workspace = new TestWorkspace();
    var source = Path.Combine(workspace.Root, "source");
    Directory.CreateDirectory(source);
    File.WriteAllText(Path.Combine(source, "file.txt"), "content", Encoding.UTF8);

    var archive = CfsArchive.CreateFromFolder(source, Path.Combine(workspace.Root, "mounted.cfs"));
    var mountPath = Path.Combine(workspace.Root, "cfs-owned-mount");
    var session = CfsMountSession.Create(archive, mountPath);
    var originalDelete = CfsMountSession.DeleteDirectory;
    CfsMountSession.DeleteDirectory = _ => throw new IOException("simulated locked file");

    try
    {
        var error = string.Empty;
        try
        {
            session.PermanentlyDelete();
        }
        catch (CfsArchiveException ex)
        {
            error = ex.Message;
        }

        Assert(error.Contains(mountPath, StringComparison.Ordinal), "cleanup error did not report the exact remaining path");
        Assert(Directory.Exists(mountPath), "failed cleanup removed the mount folder");
    }
    finally
    {
        CfsMountSession.DeleteDirectory = originalDelete;
    }
}

static void UnrelatedFoldersCannotBecomeCleanupTargets()
{
    using var workspace = new TestWorkspace();
    var source = Path.Combine(workspace.Root, "source");
    Directory.CreateDirectory(source);
    File.WriteAllText(Path.Combine(source, "file.txt"), "content", Encoding.UTF8);
    var archive = CfsArchive.CreateFromFolder(source, Path.Combine(workspace.Root, "mounted.cfs"));

    var unrelatedFolder = Path.Combine(workspace.Root, "unrelated");
    Directory.CreateDirectory(unrelatedFolder);
    var unrelatedFile = Path.Combine(unrelatedFolder, "keep.txt");
    File.WriteAllText(unrelatedFile, "keep", Encoding.UTF8);

    var failed = false;
    try
    {
        _ = CfsMountSession.Create(archive, unrelatedFolder);
    }
    catch (CfsArchiveException)
    {
        failed = true;
    }

    Assert(failed, "existing unrelated folder was accepted as a CFS mount");
    Assert(File.Exists(unrelatedFile), "unrelated folder contents were removed");
}

static void ProgressReportsRealWork()
{
    using var workspace = new TestWorkspace();
    var source = Path.Combine(workspace.Root, "source");
    Directory.CreateDirectory(Path.Combine(source, "nested"));
    File.WriteAllBytes(Path.Combine(source, "one.bin"), Enumerable.Repeat((byte)1, 128).ToArray());
    File.WriteAllBytes(Path.Combine(source, "nested", "two.bin"), Enumerable.Repeat((byte)2, 256).ToArray());
    var progress = new CaptureProgress();
    var archive = CfsArchive.CreateFromFolder(source, Path.Combine(workspace.Root, "progress.cfs"), progress);
    var openProgress = new CaptureProgress();
    var reopened = CfsArchive.Load(archive.ArchivePath, openProgress);
    var extracted = Path.Combine(workspace.Root, "extracted");
    var extractProgress = new CaptureProgress();
    reopened.ExtractAll(extracted, extractProgress);
    var validateProgress = new CaptureProgress();
    _ = CfsArchive.Validate(archive.ArchivePath, validateProgress);
    var mount = CfsMountSession.Create(archive, Path.Combine(workspace.Root, "mount"), progress);
    File.WriteAllBytes(Path.Combine(mount.FolderPath, "one.bin"), Enumerable.Repeat((byte)3, 64).ToArray());
    mount.Save(archive, progress);
    mount.PermanentlyDelete(progress);

    Assert(progress.Reports.Any(report => report.Operation == "Creating archive" && report.Phase == "Compressing files"), "create did not report file progress");
    Assert(openProgress.Reports.Any(report => report.Operation == "Opening archive" && report.CurrentPath == "one.bin"), "open did not report processed entry");
    Assert(extractProgress.Reports.Any(report => report.Operation == "Extracting archive" && report.CurrentPath == "nested/two.bin"), "extract did not report processed entry");
    Assert(validateProgress.Reports.Any(report => report.Operation == "Opening archive"), "validation did not report archive work");
    Assert(progress.Reports.Any(report => report.Operation == "Saving changes" && report.Phase == "Compressing changed files"), "save did not report changed-file compression");
    Assert(progress.Reports.Count(report => report.Operation == "Saving changes" && report.Phase == "Compressing changed files") == 1, "unchanged files were counted as compression work");
    Assert(progress.Reports.Any(report => report.Operation == "Cleaning temporary mount" && report.Phase == "Deleting files"), "cleanup did not report deleted files");
    foreach (var report in progress.Reports.Where(report => report.TotalItems is not null || report.TotalBytes is not null))
    {
        Assert(report.TotalItems is null || report.CompletedItems <= report.TotalItems, "progress items exceeded total");
        Assert(report.TotalBytes is null || report.CompletedBytes <= report.TotalBytes, "progress bytes exceeded total");
    }
    foreach (var phase in progress.Reports.GroupBy(report => (report.Operation, report.Phase)))
    {
        var previousItems = 0L;
        var previousBytes = 0L;
        foreach (var report in phase)
        {
            Assert(report.CompletedItems >= previousItems, "progress items moved backward within a phase");
            Assert(report.CompletedBytes >= previousBytes, "progress bytes moved backward within a phase");
            previousItems = report.CompletedItems;
            previousBytes = report.CompletedBytes;
        }
    }
}

static void CancellationPreservesExistingArchive()
{
    using var workspace = new TestWorkspace();
    var existingSource = Path.Combine(workspace.Root, "existing");
    Directory.CreateDirectory(existingSource);
    File.WriteAllText(Path.Combine(existingSource, "keep.txt"), "keep", Encoding.UTF8);
    var archivePath = Path.Combine(workspace.Root, "existing.cfs");
    CfsArchive.CreateFromFolder(existingSource, archivePath);

    var replacement = Path.Combine(workspace.Root, "replacement");
    Directory.CreateDirectory(replacement);
    File.WriteAllBytes(Path.Combine(replacement, "large.bin"), new byte[1024 * 1024]);
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var cancelled = false;
    try { CfsArchive.CreateFromFolder(replacement, archivePath, cancellationToken: cancellation.Token); }
    catch (OperationCanceledException) { cancelled = true; }
    Assert(cancelled, "creation did not cancel");
    Assert(Encoding.UTF8.GetString(CfsArchive.Load(archivePath).ReadFile("keep.txt")).TrimStart('\uFEFF') == "keep", "cancellation changed existing archive");
}

static void ValidationDetectsCorruptedFileBlock()
{
    using var workspace = new TestWorkspace();
    var source = Path.Combine(workspace.Root, "source");
    Directory.CreateDirectory(source);
    File.WriteAllBytes(Path.Combine(source, "data.bin"), Enumerable.Range(0, 2048).Select(i => (byte)(i % 211)).ToArray());

    var archivePath = Path.Combine(workspace.Root, "corrupt.cfs");
    var archive = CfsArchive.CreateFromFolder(source, archivePath);
    var entry = archive.ListEntries().Single(item => item.Path == "data.bin");

    using (var stream = new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        stream.Position = entry.Offset + Math.Min(16, Math.Max(0, entry.CompressedSize - 1));
        var value = stream.ReadByte();
        stream.Position -= 1;
        stream.WriteByte((byte)(value ^ 0x5a));
    }

    var result = CfsArchive.Validate(archivePath);
    var metadataOnly = CfsArchive.Load(archivePath);
    Assert(metadataOnly.ListEntries().Count == 1,
        "metadata-only archive load hydrated or rejected the corrupted payload before access");
    AssertThrows<CfsArchiveException>(() => metadataOnly.ReadFile("data.bin"),
        "lazy payload access did not detect corrupted compressed data");
    Assert(!result.IsValid, "corrupted archive should fail validation");
}

static void WritableStoragePolicyIsStrict()
{
    var localNtfs = CfsWritableStoragePolicy.Evaluate(
        @"C:\Cfs\archive.cfs", true, _ => new CfsStorageDescriptor(DriveType.Fixed, "NTFS"));
    Assert(localNtfs.IsSupported, "local NTFS was unexpectedly rejected");
    var removable = CfsWritableStoragePolicy.Evaluate(
        @"E:\archive.cfs", true, _ => new CfsStorageDescriptor(DriveType.Removable, "NTFS"));
    Assert(!removable.IsSupported && removable.Message.Contains("local NTFS", StringComparison.OrdinalIgnoreCase),
        "removable NTFS was not rejected");
    var fat = CfsWritableStoragePolicy.Evaluate(
        @"E:\archive.cfs", true, _ => new CfsStorageDescriptor(DriveType.Fixed, "exFAT"));
    Assert(!fat.IsSupported, "exFAT was not rejected");
    var cloud = CfsWritableStoragePolicy.Evaluate(
        @"C:\Users\Example\OneDrive\archive.cfs", true, _ => new CfsStorageDescriptor(DriveType.Fixed, "NTFS"),
        [@"C:\Users\Example\OneDrive"]);
    Assert(!cloud.IsSupported && cloud.Message.Contains("cloud-synchronized", StringComparison.OrdinalIgnoreCase),
        "cloud-synchronized path was not rejected");
}

static void ProjFsHydrationCacheIsBounded()
{
    if (!OperatingSystem.IsWindows()) return;

    using var workspace = new TestWorkspace();
    var source = Path.Combine(workspace.Root, "source");
    Directory.CreateDirectory(source);
    const int fileCount = 72;
    for (var index = 0; index < fileCount; index++)
        File.WriteAllBytes(Path.Combine(source, $"entry-{index:D3}.bin"), CreatePayload(4096, index + 1));
    var archivePath = Path.Combine(workspace.Root, "bounded-cache.cfs");
    CfsArchive.CreateFromFolder(source, archivePath);
    var mountRoot = Path.Combine(workspace.Root, "mount");
    using var mount = CfsProjFsMount.Create(archivePath, mountRoot);

    for (var index = 0; index < fileCount; index++)
    {
        var bytes = File.ReadAllBytes(Path.Combine(mountRoot, $"entry-{index:D3}.bin"));
        Assert(bytes.SequenceEqual(CreatePayload(4096, index + 1)), $"bounded hydration returned incorrect entry {index}");
    }

    Assert(mount.HydratedFileCount <= mount.HydrationCacheEntryLimit
        && mount.HydrationCacheRetainedBytes <= mount.HydrationCacheLimitBytes,
        $"hydration cache exceeded its bounds: entries={mount.HydratedFileCount}/{mount.HydrationCacheEntryLimit} bytes={mount.HydrationCacheRetainedBytes}/{mount.HydrationCacheLimitBytes}");
    Assert(!mount.HydratedPaths.Contains("entry-000.bin", StringComparer.OrdinalIgnoreCase)
        && mount.HydratedPaths.Contains($"entry-{fileCount - 1:D3}.bin", StringComparer.OrdinalIgnoreCase),
        "hydration cache did not evict least-recently-used payloads");
}

static void ValidationReportsExtremeCompressionRatio()
{
    using var workspace = new TestWorkspace();
    var source = Path.Combine(workspace.Root, "ratio-source");
    Directory.CreateDirectory(source);
    File.WriteAllBytes(Path.Combine(source, "zeros.bin"), new byte[4 * 1024 * 1024]);
    var archivePath = Path.Combine(workspace.Root, "ratio.cfs");
    CfsArchive.CreateFromFolder(source, archivePath);
    var entry = CfsArchive.LoadManifestEntries(archivePath).Single(item => item.Type == ArchiveEntryType.File);
    Assert(entry.OriginalSize / (double)entry.CompressedSize >= 1000d,
        "compression-ratio fixture did not reach the warning threshold");
    var validation = CfsArchive.Validate(archivePath);
    Assert(validation.IsValid && validation.Warnings.Count == 1
        && validation.Message.Contains("warnings", StringComparison.OrdinalIgnoreCase),
        "valid extreme-ratio archive did not produce a non-fatal validation warning");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertThrows<TException>(Action action, string message) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new InvalidOperationException(message);
}

static string Sha256(string path)
{
    return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}

internal sealed class TestWorkspace : IDisposable
{
    public TestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "cfs-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

internal sealed class CaptureProgress : IProgress<CfsProgress>
{
    public List<CfsProgress> Reports { get; } = [];
    public void Report(CfsProgress value) => Reports.Add(value);
}

internal static class UnbufferedFileReader
{
    private const uint GenericRead = 0x80000000;
    private const uint ShareReadWriteDelete = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagNoBuffering = 0x20000000;
    private const uint FileFlagRandomAccess = 0x10000000;
    private const uint MemCommitReserve = 0x3000;
    private const uint PageReadWrite = 0x04;
    private static readonly IntPtr InvalidHandle = new(-1);

    public static byte[] Read(string path, long offset, int length)
    {
        if ((offset & 4095) != 0 || (length & 4095) != 0) throw new ArgumentException("Unbuffered test reads must be 4 KiB aligned.");
        var handle = CreateFile(path, GenericRead, ShareReadWriteDelete, IntPtr.Zero, OpenExisting, FileFlagNoBuffering | FileFlagRandomAccess, IntPtr.Zero);
        if (handle == InvalidHandle) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not open projected file '{path}' for unbuffered reading.");
        var buffer = IntPtr.Zero;
        try
        {
            if (!SetFilePointerEx(handle, offset, out _, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not seek projected file.");
            buffer = VirtualAlloc(IntPtr.Zero, (nuint)length, MemCommitReserve, PageReadWrite);
            if (buffer == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not allocate aligned read buffer.");
            if (!ReadFile(handle, buffer, (uint)length, out var read, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read projected file range.");
            if (read != length) throw new EndOfStreamException($"Projected file returned {read} of {length} requested bytes.");
            var bytes = new byte[length];
            Marshal.Copy(buffer, bytes, 0, length);
            return bytes;
        }
        finally
        {
            if (buffer != IntPtr.Zero) _ = VirtualFree(buffer, 0, 0x8000);
            _ = CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFilePointerEx(IntPtr file, long distance, out long newPointer, uint moveMethod);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr file, IntPtr buffer, uint bytesToRead, out uint bytesRead, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr address, nuint size, uint allocationType, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(IntPtr address, nuint size, uint freeType);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
