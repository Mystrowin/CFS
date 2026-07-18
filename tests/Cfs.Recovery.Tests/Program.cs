using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cfs.Broker;
using Cfs.Core;

var logRoot = Path.Combine(Path.GetTempPath(), "cfs-recovery-tests", "logs-" + Environment.ProcessId); Directory.CreateDirectory(logRoot);
CfsDiagnostics.Logger = new CfsDiagnosticLogger(logRoot);
var tests = new (string Name, Action Body)[]
{
    ("marker is atomic bounded hidden and privacy safe", MarkerIsAtomicAndPrivate),
    ("checksum-invalid primary falls back to previous valid metadata", ChecksumInvalidPrimaryUsesPrevious),
    ("stale committed mount is ownership checked and recovered", StaleCommittedMountRecovers),
    ("committed metadata precedes owned backup cleanup", CommittedMetadataPrecedesBackupCleanup),
    ("malformed marker is preserved with recovery-needed", MalformedMarkerIsSafe),
    ("interrupted append preserves valid prior archive and mount", InterruptedAppendIsSafe),
    ("invalid candidate never replaces valid prior archive", InvalidCandidateNeverReplaces),
    ("corrupt archive is never replaced even by valid candidate", CorruptArchiveIsPreserved),
    ("legacy CFS1 version 1 remains byte-identical", LegacyArchiveIsCompatible)
    ,("pending recovery is inspectable and explicitly discardable", PendingRecoveryCanBeInspectedAndDiscarded)
    ,("unverified recovery cannot be discarded", UnverifiedRecoveryCannotBeDiscarded)
};
if (args.Length > 0)
{
    tests = tests.Where(test => test.Name.Contains(args[0], StringComparison.OrdinalIgnoreCase)).ToArray();
    if (tests.Length == 0) throw new ArgumentException($"No recovery test matched '{args[0]}'.");
}
var failed = 0;
foreach (var test in tests) { try { test.Body(); Console.WriteLine("PASS " + test.Name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + ex); } }
Console.WriteLine($"TOTAL {tests.Length} PASS {tests.Length - failed} FAIL {failed}");
try { if (Directory.Exists(logRoot)) Directory.Delete(logRoot, true); } catch { }
return failed == 0 ? 0 : 1;

static void MarkerIsAtomicAndPrivate()
{
    using var w = new Workspace(); var archive = ValidArchive(w.Root, "private"); var identity = CfsArchiveIdentity.Create(archive); var mount = OwnedMount(w.Root, identity);
    var transaction = CfsSessionTransaction.Create(identity, mount); transaction.MarkCommitPending(1); transaction.MarkCommitted(1);
    var bytes = File.ReadAllBytes(transaction.SidecarPath); var text = Encoding.UTF8.GetString(bytes);
    Assert(bytes.Length <= CfsSessionTransaction.MaximumMarkerBytes && !text.Contains(archive, StringComparison.OrdinalIgnoreCase)
        && transaction.Record.RecordChecksum.Length == 64 && text.Contains("recordChecksum", StringComparison.Ordinal)
        && transaction.Record.State == CfsSessionTransactionState.Committed && transaction.Record.LastCommittedGeneration == 1
        && transaction.Record.MutationSequence == 1,
        "marker is unbounded, leaks a raw path, lacks its checksum, or lost atomic state");
    Assert((File.GetAttributes(transaction.SidecarPath) & FileAttributes.Hidden) != 0
        && !Directory.EnumerateFiles(Path.GetDirectoryName(transaction.SidecarPath)!, "*.tmp").Any(), "marker is visible or left an atomic-write temp");
    transaction.Delete(); Assert(!File.Exists(transaction.SidecarPath), "normal close marker cleanup failed");
}

static void ChecksumInvalidPrimaryUsesPrevious()
{
    using var w = new Workspace();
    var archive = ValidArchive(w.Root, "checksum");
    var identity = CfsArchiveIdentity.Create(archive);
    var mount = OwnedMount(w.Root, identity);
    var transaction = CfsSessionTransaction.Create(identity, mount);
    var previous = CfsSessionTransaction.PreviousSidecarFor(transaction.SidecarPath);
    File.Copy(transaction.SidecarPath, previous);
    File.SetAttributes(transaction.SidecarPath, FileAttributes.Normal);
    var record = transaction.Record with { DirtyGeneration = transaction.Record.DirtyGeneration + 99 };
    File.WriteAllText(transaction.SidecarPath, JsonSerializer.Serialize(record, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    var result = CfsSessionTransaction.RecoverBeforeOpen(identity, mount);
    Assert(result.Recovered && !result.RecoveryNeeded
        && !Directory.Exists(mount) && !File.Exists(transaction.SidecarPath) && !File.Exists(previous),
        "checksum-invalid primary was trusted or the previous valid record was not used for safe stale cleanup");
}

static void StaleCommittedMountRecovers()
{
    using var w = new Workspace(); var archive = ValidArchive(w.Root, "stale"); var identity = CfsArchiveIdentity.Create(archive); var mount = OwnedMount(w.Root, identity);
    _ = CfsSessionTransaction.Create(identity, mount); var unrelated = Path.Combine(w.Root, "unrelated.txt"); File.WriteAllText(unrelated, "keep");
    var result = CfsSessionTransaction.RecoverBeforeOpen(identity, mount);
    Assert(result.Recovered && !result.RecoveryNeeded && !Directory.Exists(mount) && File.ReadAllText(unrelated) == "keep", "safe stale recovery failed or deleted unrelated data");
}

static void CommittedMetadataPrecedesBackupCleanup()
{
    using var w = new Workspace();
    var archive = ValidArchive(w.Root, "finalization");
    var identity = CfsArchiveIdentity.Create(archive);
    var mount = OwnedMount(w.Root, identity);
    var transaction = CfsSessionTransaction.Create(identity, mount);
    var token = Guid.NewGuid().ToString("N");
    var candidate = Path.Combine(w.Root, $".{Path.GetFileName(archive)}.{token}.cfs-candidate");
    var backup = Path.Combine(w.Root, $".{Path.GetFileName(archive)}.{token}.cfs-backup");
    File.Copy(archive, candidate);
    File.Copy(archive, backup);
    transaction.MarkCommitPending(3);
    transaction.MarkCommitPhase(CfsCommitPhase.Committed, candidate, backup);
    transaction.MarkCommitted(3);

    Assert(File.Exists(backup) && transaction.Record.State == CfsSessionTransactionState.Committed
        && transaction.Record.ArchiveSha256 == Hash(archive),
        "replacement backup was removed before the committed archive fingerprint became durable");
    Assert(transaction.FinalizeCommittedArtifacts()
        && !File.Exists(candidate) && !File.Exists(backup)
        && transaction.Record.CandidatePath is null && transaction.Record.BackupPath is null,
        "owned commit artifacts were not cleaned after durable committed metadata");
    transaction.Delete();
}

static void MalformedMarkerIsSafe()
{
    using var w = new Workspace(); var archive = ValidArchive(w.Root, "malformed"); var before = Hash(archive); var identity = CfsArchiveIdentity.Create(archive); var mount = OwnedMount(w.Root, identity);
    File.WriteAllText(CfsSessionTransaction.SidecarFor(mount), "{ definitely not valid json");
    var result = CfsSessionTransaction.RecoverBeforeOpen(identity, mount);
    Assert(result.RecoveryNeeded && !result.Recovered && Directory.Exists(mount) && Hash(archive) == before, "malformed metadata was accepted or changed user data");

    var hashArchive = ValidArchive(w.Root, "malformed-hash");
    var hashIdentity = CfsArchiveIdentity.Create(hashArchive);
    var hashMount = OwnedMount(w.Root, hashIdentity);
    var transaction = CfsSessionTransaction.Create(hashIdentity, hashMount);
    var badHex = transaction.Record with { OwnerMarkerHash = "not-hex" };
    File.SetAttributes(transaction.SidecarPath, FileAttributes.Normal);
    File.WriteAllText(transaction.SidecarPath, JsonSerializer.Serialize(badHex, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    result = CfsSessionTransaction.RecoverBeforeOpen(hashIdentity, hashMount);
    Assert(result.RecoveryNeeded && Directory.Exists(hashMount) && CfsArchive.Validate(hashArchive).IsValid,
        "malformed hash or checksum escaped or changed recovery state");
}

static void InterruptedAppendIsSafe()
{
    using var w = new Workspace(); var archive = ValidArchive(w.Root, "append"); var before = File.ReadAllBytes(archive); var identity = CfsArchiveIdentity.Create(archive); var mount = OwnedMount(w.Root, identity);
    var transaction = CfsSessionTransaction.Create(identity, mount); transaction.MarkCommitPending(1);
    using (var stream = new FileStream(archive, FileMode.Append, FileAccess.Write, FileShare.None)) { stream.Write("interrupted-manifest"u8); stream.Flush(true); }
    var result = CfsSessionTransaction.RecoverBeforeOpen(identity, mount);
    Assert(result.RecoveryNeeded && CfsArchive.Validate(archive).IsValid && File.ReadAllBytes(archive).AsSpan(0, before.Length).SequenceEqual(before)
        && Directory.Exists(mount), "interrupted append destroyed the valid prior archive or recoverable mount");
}

static void InvalidCandidateNeverReplaces()
{
    using var w = new Workspace(); var archive = ValidArchive(w.Root, "candidate"); var before = Hash(archive); var identity = CfsArchiveIdentity.Create(archive); var mount = OwnedMount(w.Root, identity);
    var transaction = CfsSessionTransaction.Create(identity, mount); File.WriteAllText(transaction.CandidatePath, "corrupt candidate");
    var result = CfsSessionTransaction.RecoverBeforeOpen(identity, mount);
    Assert(result.Recovered && Hash(archive) == before && CfsArchive.Validate(archive).IsValid && !File.Exists(transaction.CandidatePath), "invalid candidate replaced the prior archive or survived owned cleanup");
}

static void CorruptArchiveIsPreserved()
{
    using var w = new Workspace(); var archive = ValidArchive(w.Root, "corrupt"); var identity = CfsArchiveIdentity.Create(archive); var mount = OwnedMount(w.Root, identity); var transaction = CfsSessionTransaction.Create(identity, mount);
    File.Copy(archive, transaction.CandidatePath); File.WriteAllText(archive, "BROKEN"); var corrupt = File.ReadAllBytes(archive);
    var result = CfsSessionTransaction.RecoverBeforeOpen(identity, mount);
    Assert(result.RecoveryNeeded && File.ReadAllBytes(archive).SequenceEqual(corrupt) && File.Exists(transaction.CandidatePath) && Directory.Exists(mount), "unsafe recovery overwrote a corrupt archive or discarded evidence");
}

static void LegacyArchiveIsCompatible()
{
    using var w = new Workspace(); var archive = Path.Combine(w.Root, "legacy-0.1.cfs"); WriteLegacyEmpty(archive); var before = Hash(archive); var identity = CfsArchiveIdentity.Create(archive); var mount = OwnedMount(w.Root, identity);
    _ = CfsSessionTransaction.Create(identity, mount); var result = CfsSessionTransaction.RecoverBeforeOpen(identity, mount);
    Assert(result.Recovered && CfsArchive.Validate(archive).IsValid && Hash(archive) == before, "recovery rewrote or rejected a legacy CFS1/v1 archive");
}

static void PendingRecoveryCanBeInspectedAndDiscarded()
{
    using var w = new Workspace();
    var archive = ValidArchive(w.Root, "pending");
    var before = Hash(archive);
    var identity = CfsArchiveIdentity.Create(archive);
    var mount = OwnedMount(w.Root, identity);
    var transaction = CfsSessionTransaction.Create(identity, mount);
    File.WriteAllText(Path.Combine(mount, "recoverable.txt"), "pending-data");
    transaction.MarkCommitPending(7);

    var inspected = CfsSessionTransaction.InspectPendingRecovery(identity, mount);
    Assert(inspected.Found && inspected.OwnershipVerified && inspected.OriginalArchiveValid
        && inspected.MountPath == Path.GetFullPath(mount)
        && inspected.State == CfsSessionTransactionState.CommitPending
        && inspected.DirtyGeneration == 7 && inspected.CommittedGeneration == 0
        && inspected.MutationSequence == 7,
        "pending recovery inspection omitted ownership, state, or generations");

    var discarded = CfsSessionTransaction.DiscardPendingRecovery(identity, mount);
    Assert(!discarded.Found && !Directory.Exists(mount) && !File.Exists(transaction.SidecarPath)
        && Hash(archive) == before && CfsArchive.Validate(archive).IsValid,
        "explicit recovery discard changed the original archive or retained owned recovery data");
}

static void UnverifiedRecoveryCannotBeDiscarded()
{
    using var w = new Workspace();
    var archive = ValidArchive(w.Root, "unverified");
    var before = Hash(archive);
    var identity = CfsArchiveIdentity.Create(archive);
    var mount = OwnedMount(w.Root, identity);
    File.WriteAllText(CfsSessionTransaction.SidecarFor(mount), "{broken");
    try
    {
        _ = CfsSessionTransaction.DiscardPendingRecovery(identity, mount);
        throw new InvalidOperationException("unverified recovery was discarded");
    }
    catch (CfsArchiveException) { }
    Assert(Directory.Exists(mount) && Hash(archive) == before,
        "unverified recovery discard changed the archive or removed the workspace");
}

static string OwnedMount(string root, CfsArchiveIdentity identity) { var mount = Path.Combine(root, identity.MountKey); Directory.CreateDirectory(mount); File.WriteAllText(Path.Combine(mount, ".cfs-mount-session"), Guid.NewGuid().ToString("N")); return mount; }
static string ValidArchive(string root, string name) { var path = Path.Combine(root, name + ".cfs"); CfsArchive.CreateEmpty(path); return path; }
static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
static void WriteLegacyEmpty(string path) { var manifest = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Entries = Array.Empty<object>() }); using var stream = File.Create(path); stream.Write("CFS1"u8); stream.Write(BitConverter.GetBytes(1)); stream.Write(BitConverter.GetBytes(24L)); stream.Write(BitConverter.GetBytes((long)manifest.Length)); stream.Write(manifest); stream.Flush(true); }
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
sealed class Workspace : IDisposable { public Workspace() { Root = Path.Combine(Path.GetTempPath(), "cfs-recovery-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); } public string Root { get; } public void Dispose() { try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { } } }
