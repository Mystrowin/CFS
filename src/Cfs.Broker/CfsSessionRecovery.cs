using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cfs.Core;

namespace Cfs.Broker;

public enum CfsSessionTransactionState { Active, CommitPending, Committed }
public enum CfsCommitPhase { Idle, Preparing, WritingCandidate, FlushingCandidate, ValidatingCandidate, ReadyToReplace, Replacing, VerifyingReplacement, RestoringBackup, Committed, RecoveryRequired }

public sealed record CfsSessionTransactionRecord(
    int Version,
    string ArchiveKey,
    string OwnerMarkerHash,
    CfsSessionTransactionState State,
    ulong LastCommittedGeneration,
    long ArchiveLength,
    string ArchiveSha256,
    CfsCommitPhase CommitPhase = CfsCommitPhase.Idle,
    string? CandidatePath = null,
    string? BackupPath = null,
    ulong DirtyGeneration = 0,
    string RecordChecksum = "",
    ulong MutationSequence = 0);

public sealed record CfsSessionRecoveryResult(bool Recovered, bool RecoveryNeeded, string Message);
public sealed record CfsPendingRecoveryInfo(
    bool Found,
    bool OwnershipVerified,
    bool OriginalArchiveValid,
    string Message,
    string? MountPath = null,
    CfsSessionTransactionState? State = null,
    CfsCommitPhase? CommitPhase = null,
    ulong DirtyGeneration = 0,
    ulong CommittedGeneration = 0,
    ulong MutationSequence = 0);

/// <summary>Privacy-safe, atomic sidecar for one CFS-owned deterministic mount.</summary>
public sealed class CfsSessionTransaction
{
    public const int CurrentVersion = 2;
    public const long MaximumMarkerBytes = 1024 * 1024;
    public const string SidecarSuffix = ".cfs-session.json";
    public const string CandidateSuffix = ".cfs-candidate";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _sync = new();
    private readonly string _archivePath;
    private CfsSessionTransactionRecord _record;

    private CfsSessionTransaction(string archivePath, string sidecarPath, string candidatePath, CfsSessionTransactionRecord record)
    { _archivePath = Path.GetFullPath(archivePath); SidecarPath = sidecarPath; CandidatePath = candidatePath; _record = record; }

    public string SidecarPath { get; }
    public string CandidatePath { get; }
    public CfsSessionTransactionRecord Record { get { lock (_sync) return _record; } }

    public static CfsSessionTransaction Create(CfsArchiveIdentity identity, string mountPath)
    {
        var owner = ReadOwnerMarker(mountPath);
        var fingerprint = Fingerprint(identity.FullPath);
        var transaction = new CfsSessionTransaction(identity.FullPath, SidecarFor(mountPath), CandidateFor(mountPath),
            new(CurrentVersion, identity.MountKey, HashText(owner), CfsSessionTransactionState.Active, 0, fingerprint.Length, fingerprint.Hash));
        transaction.Write();
        transaction.MarkCommitted(0);
        return transaction;
    }

    public void MarkCommitPending(ulong generation) => Update(record => record with
    {
        State = CfsSessionTransactionState.CommitPending,
        DirtyGeneration = Math.Max(record.DirtyGeneration, generation),
        MutationSequence = Math.Max(record.MutationSequence, generation)
    });

    public void MarkCommitPhase(CfsCommitPhase phase, string? candidatePath = null, string? backupPath = null)
        => Update(record => record with { CommitPhase = phase, CandidatePath = candidatePath ?? record.CandidatePath, BackupPath = backupPath ?? record.BackupPath });

    public void MarkCommitted(ulong generation)
    {
        var fingerprint = Fingerprint(_archivePath);
        Update(record => record with
        {
            State = CfsSessionTransactionState.Committed, CommitPhase = CfsCommitPhase.Committed,
            LastCommittedGeneration = Math.Max(record.LastCommittedGeneration, generation),
            DirtyGeneration = Math.Max(record.DirtyGeneration, generation),
            MutationSequence = Math.Max(record.MutationSequence, generation),
            ArchiveLength = fingerprint.Length,
            ArchiveSha256 = fingerprint.Hash
        });
    }

    public bool FinalizeCommittedArtifacts()
    {
        lock (_sync)
        {
            if (_record.State != CfsSessionTransactionState.Committed
                || _record.CommitPhase != CfsCommitPhase.Committed)
                throw new CfsArchiveException("CFS recovery artifacts cannot be finalized before the committed record is durable.");
            try
            {
                DeleteOwnedArtifact(_record.CandidatePath, _archivePath, ".cfs-candidate");
                DeleteOwnedArtifact(_record.BackupPath, _archivePath, ".cfs-backup");
                _record = _record with { CandidatePath = null, BackupPath = null };
                Write();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CfsArchiveException)
            {
                // The authoritative archive is already validated and the committed
                // record is durable. Retaining a referenced backup is safer than
                // turning cleanup failure into an ambiguous failed commit.
                CfsDiagnostics.Logger.WriteException("recovery.artifact.cleanup", ex);
                return false;
            }
        }
    }

    public void Delete()
    {
        lock (_sync)
        {
            DeleteOwnedArtifact(_record.CandidatePath, _archivePath, ".cfs-candidate");
            DeleteOwnedArtifact(_record.BackupPath, _archivePath, ".cfs-backup");
            if (File.Exists(CandidatePath)) File.Delete(CandidatePath);
            if (File.Exists(SidecarPath)) File.Delete(SidecarPath);
            var previous = PreviousSidecarFor(SidecarPath);
            if (File.Exists(previous)) File.Delete(previous);
        }
    }

    private void Update(Func<CfsSessionTransactionRecord, CfsSessionTransactionRecord> update)
    { lock (_sync) { _record = update(_record); Write(); } }

    private void Write()
    {
        _record = _record with { RecordChecksum = ComputeRecordChecksum(_record) };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(_record, JsonOptions);
        if (bytes.Length > MaximumMarkerBytes) throw new CfsArchiveException("CFS session recovery metadata exceeded its safety bound.");
        Directory.CreateDirectory(Path.GetDirectoryName(SidecarPath)!);
        var temp = SidecarPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var previous = PreviousSidecarFor(SidecarPath);
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            if (!TryReadRecordFile(temp, out _))
                throw new CfsArchiveException("CFS refused to install recovery metadata whose checksum could not be verified.");
            if (File.Exists(SidecarPath))
            {
                if (!TryReadRecordFile(SidecarPath, out _))
                    throw new CfsArchiveException("The previous CFS recovery record is invalid and was preserved without replacement.");
                if (File.Exists(previous)) File.Delete(previous);
                File.Replace(temp, SidecarPath, previous, ignoreMetadataErrors: true);
            }
            else File.Move(temp, SidecarPath, false);
            if (!TryReadRecordFile(SidecarPath, out _))
                throw new CfsArchiveException("The newly installed CFS recovery record failed its durable checksum verification.");
            File.SetAttributes(SidecarPath, FileAttributes.Hidden | FileAttributes.NotContentIndexed);
            if (File.Exists(previous)) File.Delete(previous);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static CfsSessionRecoveryResult RecoverBeforeOpen(CfsArchiveIdentity identity, string mountPath)
    {
        var sidecar = SidecarFor(mountPath); var candidate = CandidateFor(mountPath);
        var previous = PreviousSidecarFor(sidecar);
        if (!Directory.Exists(mountPath) && !File.Exists(sidecar) && !File.Exists(previous) && !File.Exists(candidate)) return new(false, false, "No stale session state exists.");
        try
        {
            if (!Directory.Exists(mountPath) || !TryReadRecord(sidecar, out var record))
                return Needed("CFS recovery metadata is malformed, checksum-invalid, or incomplete and was preserved for inspection.");
            if (record is null || record.Version != CurrentVersion || !Enum.IsDefined(record.State)
                || !Enum.IsDefined(record.CommitPhase)
                || !string.Equals(record.ArchiveKey, identity.MountKey, StringComparison.Ordinal)
                || !HashMatches(record.OwnerMarkerHash, HashText(ReadOwnerMarker(mountPath))))
                return Needed("CFS recovery ownership or archive identity could not be verified; no files were changed.");

            var validation = CfsArchive.Validate(identity.FullPath);
            if (!validation.IsValid) return Needed("The last archive is not valid. CFS preserved it and the recovery data without replacement.");
            var fingerprint = Fingerprint(identity.FullPath);
            if (record.State != CfsSessionTransactionState.Committed
                || record.ArchiveLength != fingerprint.Length
                || !string.Equals(record.ArchiveSha256, fingerprint.Hash, StringComparison.Ordinal))
                return Needed("An interrupted CFS commit may contain recoverable mounted edits. The valid prior archive and marked session were preserved.");
            if (!MountMatchesArchive(identity.FullPath, mountPath))
                return Needed("The stale marked mount differs from the last committed archive. Recoverable files were preserved for manual recovery.");

            // A candidate is never promoted over an already-valid committed archive. It is
            // CFS-owned by its exact deterministic name and may be removed after validation.
            if (File.Exists(candidate)) _ = CfsArchive.Validate(candidate);
            DeleteOwnedArtifact(record.CandidatePath, identity.FullPath, ".cfs-candidate");
            DeleteOwnedArtifact(record.BackupPath, identity.FullPath, ".cfs-backup");
            DeleteOwnedStaleState(mountPath, sidecar, candidate, record.OwnerMarkerHash);
            return new(true, false, "A stale committed CFS session was ownership-checked and cleaned before opening.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CfsArchiveException or FormatException or ArgumentException)
        { return Needed("CFS could not safely resolve stale recovery state. It was preserved; inspect the diagnostic log before retrying."); }
    }

    public static CfsPendingRecoveryInfo InspectPendingRecovery(CfsArchiveIdentity identity, string mountPath)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var fullMount = Path.GetFullPath(mountPath);
        var sidecar = SidecarFor(fullMount);
        if (!Directory.Exists(fullMount) && !File.Exists(sidecar) && !File.Exists(PreviousSidecarFor(sidecar)))
            return new(false, false, CfsArchive.Validate(identity.FullPath).IsValid, "No preserved CFS recovery state exists.");
        if (!TryReadOwnedRecord(identity, fullMount, out var record, out var message))
            return new(true, false, CfsArchive.Validate(identity.FullPath).IsValid, message, fullMount);
        return new(true, true, CfsArchive.Validate(identity.FullPath).IsValid,
            "CFS verified the preserved recovery workspace. The original archive has not been replaced.",
            fullMount, record!.State, record.CommitPhase, record.DirtyGeneration, record.LastCommittedGeneration, record.MutationSequence);
    }

    public static CfsPendingRecoveryInfo DiscardPendingRecovery(CfsArchiveIdentity identity, string mountPath)
    {
        var info = InspectPendingRecovery(identity, mountPath);
        if (!info.Found) return info;
        if (!info.OwnershipVerified)
            throw new CfsArchiveException("CFS refused to discard recovery data whose ownership could not be verified.");
        if (!info.OriginalArchiveValid)
            throw new CfsArchiveException("CFS refused to discard the recovery workspace because the original archive is not valid.");

        var fullMount = Path.GetFullPath(mountPath);
        if (!TryReadOwnedRecord(identity, fullMount, out var record, out _))
            throw new CfsArchiveException("CFS recovery ownership changed before discard.");
        DeleteOwnedArtifact(record!.CandidatePath, identity.FullPath, ".cfs-candidate");
        DeleteOwnedArtifact(record.BackupPath, identity.FullPath, ".cfs-backup");
        DeleteOwnedStaleState(fullMount, SidecarFor(fullMount), CandidateFor(fullMount), record.OwnerMarkerHash);
        return info with { Found = false, MountPath = null, Message = "Verified CFS recovery data was discarded. The valid original archive was not changed." };
    }

    private static void DeleteOwnedStaleState(string mountPath, string sidecar, string candidate, string ownerHash)
    {
        var owner = ReadOwnerMarker(mountPath);
        if (!string.Equals(HashText(owner), ownerHash, StringComparison.Ordinal)) throw new CfsArchiveException("Stale mount ownership changed during recovery.");
        if (File.Exists(candidate)) File.Delete(candidate);
        Directory.Delete(mountPath, true);
        if (File.Exists(sidecar)) File.Delete(sidecar);
        var previous = PreviousSidecarFor(sidecar);
        if (File.Exists(previous)) File.Delete(previous);
    }

    private static bool TryReadOwnedRecord(CfsArchiveIdentity identity, string mountPath, out CfsSessionTransactionRecord? record, out string message)
    {
        record = null;
        message = "CFS recovery metadata is incomplete or malformed and was preserved.";
        if (!Directory.Exists(mountPath)) return false;
        var sidecar = SidecarFor(mountPath);
        if (!TryReadRecord(sidecar, out record)) return false;
        if (record is null || record.Version != CurrentVersion || !Enum.IsDefined(record.State)
            || !Enum.IsDefined(record.CommitPhase)
            || !string.Equals(record.ArchiveKey, identity.MountKey, StringComparison.Ordinal)
            || !HashMatches(record.OwnerMarkerHash, HashText(ReadOwnerMarker(mountPath))))
        {
            record = null;
            message = "CFS recovery ownership or archive identity could not be verified; no files were changed.";
            return false;
        }
        message = "CFS verified the preserved recovery workspace.";
        return true;
    }

    private static bool TryReadRecord(string sidecarPath, out CfsSessionTransactionRecord? record)
    {
        if (TryReadRecordFile(sidecarPath, out record)) return true;
        return TryReadRecordFile(PreviousSidecarFor(sidecarPath), out record);
    }

    private static bool TryReadRecordFile(string path, out CfsSessionTransactionRecord? record)
    {
        record = null;
        if (!File.Exists(path)) return false;
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > MaximumMarkerBytes) return false;
        try { record = JsonSerializer.Deserialize<CfsSessionTransactionRecord>(File.ReadAllBytes(path), JsonOptions); }
        catch (JsonException) { return false; }
        if (record is null || record.Version != CurrentVersion || !HashMatches(record.RecordChecksum, ComputeRecordChecksum(record)))
        {
            record = null;
            return false;
        }
        return true;
    }

    private static string ComputeRecordChecksum(CfsSessionTransactionRecord record)
    {
        var unsigned = record with { RecordChecksum = "" };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(unsigned, JsonOptions)));
    }

    private static void DeleteOwnedArtifact(string? path, string archivePath, string requiredSuffix)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var full = Path.GetFullPath(path);
        var archiveDirectory = Path.GetDirectoryName(Path.GetFullPath(archivePath))!;
        var fileName = Path.GetFileName(full);
        var requiredPrefix = "." + Path.GetFileName(archivePath) + ".";
        if (!string.Equals(Path.GetDirectoryName(full), archiveDirectory, StringComparison.OrdinalIgnoreCase)
            || !fileName.StartsWith(requiredPrefix, StringComparison.Ordinal)
            || !fileName.EndsWith(requiredSuffix, StringComparison.Ordinal))
            throw new CfsArchiveException("CFS refused a recovery artifact path outside its owned archive directory.");
        if (File.Exists(full)) File.Delete(full);
    }

    private static bool MountMatchesArchive(string archivePath, string mountPath)
    {
        // Full comparison is intentionally limited to exceptional stale-session recovery;
        // normal opens remain manifest-only and on-demand. It prevents a watcher/crash race
        // from deleting an edit that arrived just before the old broker terminated.
        var archive = CfsArchive.Load(archivePath);
        var expectedFiles = archive.ListEntries().Where(entry => entry.Type == ArchiveEntryType.File)
            .ToDictionary(entry => entry.Path.Replace('/', Path.DirectorySeparatorChar), entry => entry.Path, StringComparer.OrdinalIgnoreCase);
        var actualFiles = Directory.EnumerateFiles(mountPath, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFileName(path), CfsFolderSync.MountMarkerFileName, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => Path.GetRelativePath(mountPath, path), path => path, StringComparer.OrdinalIgnoreCase);
        if (!expectedFiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(actualFiles.Keys)) return false;
        foreach (var pair in expectedFiles)
            if (!File.ReadAllBytes(actualFiles[pair.Key]).SequenceEqual(archive.ReadFile(pair.Value))) return false;
        return true;
    }

    private static string ReadOwnerMarker(string mountPath)
    {
        var marker = Path.Combine(mountPath, CfsFolderSync.MountMarkerFileName);
        if (!File.Exists(marker)) throw new CfsArchiveException("The CFS-owned mount marker is missing.");
        var value = File.ReadAllText(marker); if (string.IsNullOrWhiteSpace(value) || value.Length > 256) throw new CfsArchiveException("The CFS-owned mount marker is invalid.");
        return value;
    }
    private static (long Length, string Hash) Fingerprint(string path) => (new FileInfo(path).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool HashMatches(string left, string right)
    {
        if (left.Length != 64 || right.Length != 64) return false;
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right)); }
        catch (FormatException) { return false; }
    }
    public static string SidecarFor(string mountPath) => Path.GetFullPath(mountPath) + SidecarSuffix;
    public static string PreviousSidecarFor(string sidecarPath) => Path.GetFullPath(sidecarPath) + ".previous";
    public static string CandidateFor(string mountPath) => Path.GetFullPath(mountPath) + CandidateSuffix;
    private static CfsSessionRecoveryResult Needed(string message) => new(false, true, message);
}
