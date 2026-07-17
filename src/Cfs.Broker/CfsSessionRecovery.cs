using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cfs.Core;

namespace Cfs.Broker;

public enum CfsSessionTransactionState { Active, CommitPending, Committed }

public sealed record CfsSessionTransactionRecord(
    int Version,
    string ArchiveKey,
    string OwnerMarkerHash,
    CfsSessionTransactionState State,
    long LastCommittedGeneration,
    long ArchiveLength,
    string ArchiveSha256);

public sealed record CfsSessionRecoveryResult(bool Recovered, bool RecoveryNeeded, string Message);

/// <summary>Privacy-safe, atomic sidecar for one CFS-owned deterministic mount.</summary>
public sealed class CfsSessionTransaction
{
    public const int CurrentVersion = 1;
    public const long MaximumMarkerBytes = 16 * 1024;
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

    public void MarkCommitPending(long generation) => Update(record => record with { State = CfsSessionTransactionState.CommitPending });

    public void MarkCommitted(long generation)
    {
        var fingerprint = Fingerprint(_archivePath);
        Update(record => record with
        {
            State = CfsSessionTransactionState.Committed,
            LastCommittedGeneration = Math.Max(record.LastCommittedGeneration, generation),
            ArchiveLength = fingerprint.Length,
            ArchiveSha256 = fingerprint.Hash
        });
    }

    public void Delete()
    {
        lock (_sync)
        {
            if (File.Exists(CandidatePath)) File.Delete(CandidatePath);
            if (File.Exists(SidecarPath)) File.Delete(SidecarPath);
        }
    }

    private void Update(Func<CfsSessionTransactionRecord, CfsSessionTransactionRecord> update)
    { lock (_sync) { _record = update(_record); Write(); } }

    private void Write()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(_record, JsonOptions);
        if (bytes.Length > MaximumMarkerBytes) throw new CfsArchiveException("CFS session recovery metadata exceeded its safety bound.");
        Directory.CreateDirectory(Path.GetDirectoryName(SidecarPath)!);
        var temp = SidecarPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, SidecarPath, true);
            File.SetAttributes(SidecarPath, FileAttributes.Hidden | FileAttributes.NotContentIndexed);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static CfsSessionRecoveryResult RecoverBeforeOpen(CfsArchiveIdentity identity, string mountPath)
    {
        var sidecar = SidecarFor(mountPath); var candidate = CandidateFor(mountPath);
        if (!Directory.Exists(mountPath) && !File.Exists(sidecar) && !File.Exists(candidate)) return new(false, false, "No stale session state exists.");
        try
        {
            if (!Directory.Exists(mountPath) || !File.Exists(sidecar)) return Needed("Incomplete CFS-owned recovery metadata was preserved for inspection.");
            var info = new FileInfo(sidecar); if (info.Length <= 0 || info.Length > MaximumMarkerBytes) return Needed("CFS recovery metadata is malformed or exceeds its safety bound.");
            CfsSessionTransactionRecord? record;
            try { record = JsonSerializer.Deserialize<CfsSessionTransactionRecord>(File.ReadAllBytes(sidecar), JsonOptions); }
            catch (JsonException) { return Needed("CFS recovery metadata is malformed and was preserved for inspection."); }
            if (record is null || record.Version != CurrentVersion || !Enum.IsDefined(record.State)
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
            DeleteOwnedStaleState(mountPath, sidecar, candidate, record.OwnerMarkerHash);
            return new(true, false, "A stale committed CFS session was ownership-checked and cleaned before opening.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CfsArchiveException or FormatException or ArgumentException)
        { return Needed("CFS could not safely resolve stale recovery state. It was preserved; inspect the diagnostic log before retrying."); }
    }

    private static void DeleteOwnedStaleState(string mountPath, string sidecar, string candidate, string ownerHash)
    {
        var owner = ReadOwnerMarker(mountPath);
        if (!string.Equals(HashText(owner), ownerHash, StringComparison.Ordinal)) throw new CfsArchiveException("Stale mount ownership changed during recovery.");
        if (File.Exists(candidate)) File.Delete(candidate);
        Directory.Delete(mountPath, true);
        if (File.Exists(sidecar)) File.Delete(sidecar);
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
    public static string CandidateFor(string mountPath) => Path.GetFullPath(mountPath) + CandidateSuffix;
    private static CfsSessionRecoveryResult Needed(string message) => new(false, true, message);
}
