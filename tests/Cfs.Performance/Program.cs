using Cfs.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

const int smallFileCount = 1000;
const int largeFileSize = 8 * 1024 * 1024;
var outputPath = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("CFS-0.1.0-Beta-performance.json");
var workspace = Path.Combine(Path.GetTempPath(), "cfs-performance", Guid.NewGuid().ToString("N"));
var source = Path.Combine(workspace, "source");
var archivePath = Path.Combine(workspace, "performance.cfs");
var readMountPath = Path.Combine(workspace, "read-mount");
var editMountPath = Path.Combine(workspace, "edit-mount");
var timings = new Dictionary<string, double>(StringComparer.Ordinal);
var notes = new List<string>();
CfsProjFsMount? readMount = null;
CfsMountSession? editSession = null;
var cleanupSucceeded = false;

try
{
    Directory.CreateDirectory(Path.Combine(source, "small"));
    Directory.CreateDirectory(Path.Combine(source, "large"));
    for (var index = 0; index < smallFileCount; index++)
        File.WriteAllBytes(Path.Combine(source, "small", $"f{index:D5}.bin"), Payload(256, index + 1));
    var largePayloads = Enumerable.Range(0, 3).Select(index => Payload(largeFileSize, 10_000 + index)).ToArray();
    for (var index = 0; index < largePayloads.Length; index++)
        File.WriteAllBytes(Path.Combine(source, "large", $"large-{index}.bin"), largePayloads[index]);

    Measure("create_1000_files_seconds", 180, () => CfsArchive.CreateFromFolder(source, archivePath));

    IReadOnlyList<CfsEntry> manifest = [];
    Measure("metadata_manifest_load_seconds", 10, () => manifest = CfsArchive.LoadManifestEntries(archivePath));
    Require(manifest.Count(entry => entry.Type == ArchiveEntryType.File) == smallFileCount + 3, "manifest file count was incorrect");
    Require(manifest.Count(entry => entry.Type == ArchiveEntryType.Directory) == 2, "manifest directory count was incorrect");

    Measure("projfs_mount_readiness_seconds", 30, () =>
    {
        readMount = CfsProjFsMount.Create(archivePath, readMountPath);
        var projectedCount = Directory.EnumerateFileSystemEntries(readMountPath, "*", SearchOption.AllDirectories).Count();
        Require(projectedCount >= smallFileCount + 5, $"projected namespace contained only {projectedCount} entries");
        Require(readMount.HydratedFileCount == 0, "metadata enumeration hydrated payloads");
    });

    var expectedSmall = File.ReadAllBytes(Path.Combine(source, "small", "f00500.bin"));
    Measure("single_file_hydration_seconds", 30, () =>
    {
        var actual = File.ReadAllBytes(Path.Combine(readMountPath, "small", "f00500.bin"));
        Require(actual.SequenceEqual(expectedSmall), "single requested-file bytes were incorrect");
        Require(readMount!.HydratedPaths.SequenceEqual(new[] { "small/f00500.bin" }, StringComparer.OrdinalIgnoreCase), "single hydration touched unrelated entries");
    });

    const int firstOffset = 2 * 1024 * 1024;
    const int secondOffset = 5 * 1024 * 1024;
    const int rangeLength = 64 * 1024;
    Measure("nonzero_concurrent_large_reads_seconds", 60, () =>
    {
        using var start = new ManualResetEventSlim(false);
        var firstRead = Task.Run(() => { start.Wait(); return ReadRange(Path.Combine(readMountPath, "large", "large-0.bin"), firstOffset, rangeLength); });
        var secondRead = Task.Run(() => { start.Wait(); return ReadRange(Path.Combine(readMountPath, "large", "large-1.bin"), secondOffset, rangeLength); });
        start.Set();
        Task.WaitAll(firstRead, secondRead);
        Require(firstRead.Result.SequenceEqual(largePayloads[0].AsSpan(firstOffset, rangeLength).ToArray()), "first nonzero large-file range was incorrect");
        Require(secondRead.Result.SequenceEqual(largePayloads[1].AsSpan(secondOffset, rangeLength).ToArray()), "second concurrent large-file range was incorrect");
        Require(!readMount!.HydratedPaths.Contains("large/large-2.bin", StringComparer.OrdinalIgnoreCase), "unrequested large file was hydrated");
    });

    readMount!.Dispose();
    readMount = null;
    Directory.Delete(readMountPath, recursive: true);

    var unchangedBefore = manifest.Single(entry => entry.Path == "small/f00010.bin");
    Measure("edit_mount_readiness_seconds", 30, () => editSession = CfsMountSession.Create(CfsArchive.Load(archivePath), editMountPath));
    var archive = CfsArchive.Load(archivePath);
    File.WriteAllBytes(Path.Combine(editMountPath, "small", "f00001.bin"), Payload(512, 20_001));
    File.WriteAllBytes(Path.Combine(editMountPath, "small", "created.bin"), Payload(384, 20_002));
    File.Move(Path.Combine(editMountPath, "small", "f00003.bin"), Path.Combine(editMountPath, "small", "renamed.bin"));
    File.Delete(Path.Combine(editMountPath, "small", "f00004.bin"));
    Measure("save_overwrite_create_rename_delete_seconds", 240, () => editSession!.Save(archive));

    var afterSaveManifest = CfsArchive.LoadManifestEntries(archivePath);
    var unchangedAfter = afterSaveManifest.Single(entry => entry.Path == "small/f00010.bin");
    Require(unchangedAfter.Offset == unchangedBefore.Offset && unchangedAfter.CompressedSize == unchangedBefore.CompressedSize, "one-change save rewrote an unchanged compressed block");
    notes.Add($"unchanged_block_reused_offset={unchangedAfter.Offset}");

    Measure("unmount_cleanup_seconds", 30, () => editSession!.PermanentlyDelete());
    editSession = null;
    Require(!Directory.Exists(editMountPath), "edit mount survived unmount");

    CfsValidationResult validation = new();
    Measure("validation_seconds", 240, () => validation = CfsArchive.Validate(archivePath));
    Require(validation.IsValid, "post-edit archive validation failed: " + validation.Message);

    CfsArchive? reopened = null;
    Measure("reopen_persistence_seconds", 240, () => reopened = CfsArchive.Load(archivePath));
    var reopenedEntries = reopened!.ListEntries();
    Require(reopenedEntries.Any(entry => entry.Path == "small/created.bin"), "created file missing after reopen");
    Require(reopenedEntries.Any(entry => entry.Path == "small/renamed.bin") && !reopenedEntries.Any(entry => entry.Path == "small/f00003.bin"), "rename did not persist after reopen");
    Require(!reopenedEntries.Any(entry => entry.Path == "small/f00004.bin"), "delete did not persist after reopen");
    Require(reopened.ReadFile("small/f00001.bin").SequenceEqual(Payload(512, 20_001)), "overwrite did not persist after reopen");

    var peakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64;
    Require(peakWorkingSet < 2L * 1024 * 1024 * 1024, $"peak working set {peakWorkingSet} exceeded 2 GiB gross-regression ceiling");
    notes.Add("Thresholds are intentionally broad release gates for gross regressions on shared beta hardware, not product performance guarantees.");

    Directory.Delete(workspace, recursive: true);
    cleanupSucceeded = !Directory.Exists(workspace);
    Require(cleanupSucceeded, "performance workspace survived cleanup");

    var result = new
    {
        Product = CfsProductInfo.DisplayName,
        CfsProductInfo.BuildIdentifier,
        TimestampUtc = DateTimeOffset.UtcNow,
        Hardware = new { ProcessorCount = Environment.ProcessorCount, OS = Environment.OSVersion.VersionString },
        Workload = new { SmallFiles = smallFileCount, SmallFileBytes = 256, LargeFiles = 3, LargeFileBytes = largeFileSize },
        TimingsSeconds = timings,
        PeakWorkingSetBytes = peakWorkingSet,
        UnchangedBlockReused = true,
        SingleHydrationIsolated = true,
        CleanupSucceeded = cleanupSucceeded,
        Notes = notes
    };
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    File.WriteAllText(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("PERFORMANCE_PASS");
    return 0;
}
finally
{
    readMount?.Dispose();
    if (editSession is not null && Directory.Exists(editSession.FolderPath))
    {
        try { editSession.PermanentlyDelete(); } catch { }
    }
    if (Directory.Exists(workspace))
    {
        try { Directory.Delete(workspace, recursive: true); } catch { }
    }
}

void Measure(string name, double maximumSeconds, Action action)
{
    var stopwatch = Stopwatch.StartNew();
    action();
    stopwatch.Stop();
    timings[name] = Math.Round(stopwatch.Elapsed.TotalSeconds, 3);
    Console.WriteLine($"MEASURE {name}={timings[name]:F3}s limit={maximumSeconds:F0}s");
    Require(stopwatch.Elapsed.TotalSeconds <= maximumSeconds, $"{name} exceeded {maximumSeconds:F0}s gross-regression ceiling");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static byte[] Payload(int length, int seed)
{
    var bytes = new byte[length];
    new Random(seed).NextBytes(bytes);
    return bytes;
}

static byte[] ReadRange(string path, long offset, int length)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.RandomAccess);
    var bytes = new byte[length];
    var completed = 0;
    while (completed < length)
    {
        var read = RandomAccess.Read(stream.SafeFileHandle, bytes.AsSpan(completed), offset + completed);
        if (read == 0) throw new EndOfStreamException();
        completed += read;
    }
    return bytes;
}
