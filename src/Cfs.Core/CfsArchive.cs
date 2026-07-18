using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cfs.Core;

public sealed class CfsArchive
{
    public const int FormatVersion = 1;
    public const string CompressionLzma2 = "lzma2-7zip-sdk-26.02";
    public const string CompressionLzma2RawV2 = "lzma2-raw-v2";
    public const string CompressionNone = "none";
    public const int MaximumManifestBytes = 64 * 1024 * 1024;
    public const int MaximumEntryCount = 1_000_000;
    public const long MaximumEntryUncompressedBytes = 2L * 1024 * 1024 * 1024 - 1;
    public const long MaximumTotalUncompressedBytes = 16L * 1024 * 1024 * 1024 * 1024;

    private const int HeaderLength = 24;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CFS1");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly Dictionary<string, FileRecord> _files;
    private readonly HashSet<string> _directories;

    private CfsArchive(string archivePath, Dictionary<string, FileRecord> files, HashSet<string> directories)
    {
        ArchivePath = archivePath;
        _files = files;
        _directories = directories;
    }

    public string ArchivePath { get; }

    public static CfsArchive CreateEmpty(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var fullPath = Path.GetFullPath(archivePath);
        if (!string.Equals(Path.GetExtension(fullPath), ".cfs", StringComparison.OrdinalIgnoreCase))
            throw new CfsArchiveException("An empty CFS archive target must use the .cfs extension.");
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            throw new CfsArchiveException($"Refusing to overwrite existing target '{fullPath}'.");

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory ?? Environment.CurrentDirectory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            WriteArchive(tempPath, new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            _ = Load(tempPath);
            File.Move(tempPath, fullPath, overwrite: false);
            return Load(fullPath);
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }

    public static CfsArchive CreateFromFolder(string sourceFolder, string archivePath, IProgress<CfsProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var files = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var safeTree = CfsSourcePathSafety.EnumerateFolderTree(sourceFolder);
        var root = safeTree.RootPath;

        var sourceFiles = safeTree.Files.ToList();
        long totalBytes = 0;
        foreach (var file in sourceFiles)
        {
            CfsSourcePathSafety.RevalidateFileForRead(root, file);
            totalBytes = checked(totalBytes + new FileInfo(file).Length);
        }
        CfsProgressReporter.Report(progress, "Creating archive", "Scanning source folder", null, 0, sourceFiles.Count, 0, totalBytes);
        foreach (var directory in safeTree.Directories.Where(path => !string.Equals(path, root, StringComparison.OrdinalIgnoreCase)))
        {
            var relativePath = Path.GetRelativePath(root, directory);
            directories.Add(NormalizeEntryPath(relativePath));
        }

        long completedItems = 0, completedBytes = 0;
        foreach (var file in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CfsSourcePathSafety.RevalidateFileForRead(root, file);
            var relativePath = Path.GetRelativePath(root, file);
            var normalized = NormalizeEntryPath(relativePath);
            files[normalized] = FileRecord.New(File.ReadAllBytes(file), File.GetLastWriteTimeUtc(file));
            AddParentDirectories(normalized, directories);
            completedItems++;
            completedBytes += files[normalized].OriginalSize;
            CfsProgressReporter.Report(progress, "Creating archive", "Compressing files", normalized, completedItems, sourceFiles.Count, completedBytes, totalBytes);
        }

        var archive = new CfsArchive(archivePath, files, directories);
        CfsProgressReporter.Report(progress, "Creating archive", "Writing archive", null, completedItems, sourceFiles.Count, completedBytes, totalBytes);
        archive.ReplaceArchive(cancellationToken);
        return Load(archivePath, progress, cancellationToken);
    }

    public static CfsArchive Load(string archivePath, IProgress<CfsProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("CFS archive was not found.", archivePath);
        }

        using var stream = File.OpenRead(archivePath);
        var manifest = ReadManifest(stream);
        var files = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var orderedEntries = manifest.Entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
        long totalBytes = 0;
        foreach (var entry in orderedEntries.Where(e => e.Type == ArchiveEntryType.File))
        {
            if (entry.OriginalSize < 0 || entry.OriginalSize > MaximumEntryUncompressedBytes)
                throw new CfsArchiveException($"Archive entry '{entry.Path}' exceeds the {MaximumEntryUncompressedBytes} byte safety limit.");
            try { totalBytes = checked(totalBytes + entry.OriginalSize); }
            catch (OverflowException) { throw new CfsArchiveException("Archive total uncompressed size overflowed its safety limit."); }
            if (totalBytes > MaximumTotalUncompressedBytes)
                throw new CfsArchiveException($"Archive total uncompressed size exceeds the {MaximumTotalUncompressedBytes} byte safety limit.");
        }
        long completedItems = 0, completedBytes = 0;
        CfsProgressReporter.Report(progress, "Opening archive", "Reading archive metadata", null, 0, orderedEntries.Count, 0, totalBytes);
        foreach (var entry in orderedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = NormalizeEntryPath(entry.Path);
            if (entry.Type == ArchiveEntryType.Directory)
            {
                if (files.ContainsKey(normalized) || !directories.Add(normalized))
                    throw new CfsArchiveException($"Archive manifest contains a duplicate or conflicting path '{normalized}'.");
                completedItems++;
                CfsProgressReporter.Report(progress, "Opening archive", "Validating manifest", normalized, completedItems, orderedEntries.Count, completedBytes, totalBytes);
                continue;
            }

            if (entry.Type != ArchiveEntryType.File)
            {
                throw new CfsArchiveException($"Unsupported entry type for {entry.Path}.");
            }

            if (directories.Contains(normalized) || files.ContainsKey(normalized))
                throw new CfsArchiveException($"Archive manifest contains a duplicate or conflicting path '{normalized}'.");
            files.Add(normalized, FileRecord.FromEntry(entry));
            AddParentDirectories(normalized, directories);
            completedItems++;
            completedBytes += entry.OriginalSize;
            CfsProgressReporter.Report(progress, "Opening archive", "Reading archive metadata", normalized, completedItems, orderedEntries.Count, completedBytes, totalBytes);
        }

        return new CfsArchive(archivePath, files, directories);
    }

    public static IReadOnlyList<CfsEntry> LoadManifestEntries(string archivePath)
    {
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("CFS archive was not found.", archivePath);
        }

        using var stream = File.OpenRead(archivePath);
        return ReadManifest(stream).Entries
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new CfsEntry
            {
                Path = entry.Path,
                Type = entry.Type,
                OriginalSize = entry.OriginalSize,
                CompressedSize = entry.CompressedSize,
                Offset = entry.Offset,
                CompressionMethod = entry.CompressionMethod,
                Sha256 = entry.Sha256,
                LastWriteTimeUtc = entry.LastWriteTimeUtc
            })
            .ToList();
    }

    public static byte[] ReadManifestEntry(string archivePath, CfsEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Type != ArchiveEntryType.File)
        {
            throw new CfsArchiveException($"'{entry.Path}' is not a file entry.");
        }

        using var stream = File.OpenRead(archivePath);
        return ReadFileBytes(stream, entry);
    }

    public static IReadOnlyList<CfsProjectedEntry> LoadProjectedEntries(string archivePath) =>
        LoadManifestEntries(archivePath).Select(entry => new CfsProjectedEntry(
            entry.Path, entry.Type, entry.OriginalSize, entry.LastWriteTimeUtc,
            entry.Offset, entry.CompressedSize, entry.CompressionMethod, entry.Sha256)).ToList();

    public static CfsValidationResult Validate(string archivePath, IProgress<CfsProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        CfsDiagnostics.Logger.WritePathEvent("archive.validation", archivePath, "starting");
        try
        {
            var archive = Load(archivePath, progress, cancellationToken);
            var entries = archive.ListEntries();
            foreach (var entry in entries.Where(entry => entry.Type == ArchiveEntryType.File))
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = archive.ReadFile(entry.Path);
            }
            CfsDiagnostics.Logger.WritePathEvent("archive.validation", archivePath, "success");
            var warnings = entries
                .Where(entry => entry.Type == ArchiveEntryType.File && entry.OriginalSize > 0 && entry.CompressedSize > 0
                    && entry.OriginalSize / (double)entry.CompressedSize >= 1000d)
                .Select(entry => $"Entry '{entry.Path}' has a compression ratio of at least 1,000:1.")
                .ToArray();
            return new CfsValidationResult
            {
                IsValid = true,
                FileCount = entries.Count(entry => entry.Type == ArchiveEntryType.File),
                DirectoryCount = entries.Count(entry => entry.Type == ArchiveEntryType.Directory),
                Message = warnings.Length == 0 ? "Archive is valid." : "Archive is valid with compression-ratio warnings.",
                Warnings = warnings
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CfsArchiveException or InvalidDataException)
        {
            CfsDiagnostics.Logger.WriteException("archive.validation", ex);
            return new CfsValidationResult
            {
                IsValid = false,
                Message = ex.Message
            };
        }
    }

    public IReadOnlyList<CfsEntry> ListEntries()
    {
        var entries = new List<CfsEntry>();

        entries.AddRange(_directories
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new CfsEntry
            {
                Path = path,
                Type = ArchiveEntryType.Directory,
                LastWriteTimeUtc = DateTimeOffset.UnixEpoch,
                CompressionMethod = CompressionNone
            }));

        entries.AddRange(_files
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => ToEntry(pair.Key, pair.Value)));

        return entries;
    }

    public byte[] ReadFile(string entryPath)
    {
        var normalized = NormalizeEntryPath(entryPath);
        if (!_files.TryGetValue(normalized, out var record))
        {
            throw new FileNotFoundException("File was not found in the CFS archive.", normalized);
        }

        return ReadRecordBytes(entryPath, record).ToArray();
    }

    public void WriteFile(string entryPath, byte[] bytes)
    {
        var normalized = NormalizeEntryPath(entryPath);
        EnsureNoDirectoryAtPath(normalized);
        AddParentDirectories(normalized, _directories);
        _files[normalized] = FileRecord.New(bytes.ToArray(), DateTime.UtcNow);
        Save();
    }

    public void CreateDirectory(string entryPath)
    {
        var normalized = NormalizeEntryPath(entryPath);
        if (_files.ContainsKey(normalized))
        {
            throw new CfsArchiveException($"A file already exists at '{normalized}'.");
        }

        _directories.Add(normalized);
        AddParentDirectories(normalized, _directories);
        Save();
    }

    public void DeleteFile(string entryPath)
    {
        var normalized = NormalizeEntryPath(entryPath);
        if (!_files.Remove(normalized))
        {
            throw new FileNotFoundException("File was not found in the CFS archive.", normalized);
        }

        Save();
    }

    public void DeleteEmptyDirectory(string entryPath)
    {
        var normalized = NormalizeEntryPath(entryPath);
        var prefix = normalized + "/";
        if (_files.Keys.Any(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
            _directories.Any(path => !path.Equals(normalized, StringComparison.OrdinalIgnoreCase) &&
                                     path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new CfsArchiveException($"Directory '{normalized}' is not empty.");
        }

        if (!_directories.Remove(normalized))
        {
            throw new DirectoryNotFoundException(normalized);
        }

        Save();
    }

    public void Rename(string oldPath, string newPath)
    {
        var oldNormalized = NormalizeEntryPath(oldPath);
        var newNormalized = NormalizeEntryPath(newPath);

        if (_files.TryGetValue(oldNormalized, out var file))
        {
            EnsureDestinationAvailable(newNormalized);
            AddParentDirectories(newNormalized, _directories);
            _files.Remove(oldNormalized);
            _files[newNormalized] = file with { LastWriteTimeUtc = DateTime.UtcNow };
            Save();
            return;
        }

        if (_directories.Contains(oldNormalized))
        {
            EnsureDestinationAvailable(newNormalized);
            var oldPrefix = oldNormalized + "/";
            var newPrefix = newNormalized + "/";

            var renamedDirectories = _directories
                .Where(path => path.Equals(oldNormalized, StringComparison.OrdinalIgnoreCase) ||
                               path.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var renamedFiles = _files
                .Where(pair => pair.Key.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var directory in renamedDirectories)
            {
                _directories.Remove(directory);
            }

            foreach (var pair in renamedFiles)
            {
                _files.Remove(pair.Key);
            }

            foreach (var directory in renamedDirectories)
            {
                var suffix = directory.Equals(oldNormalized, StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : directory[oldPrefix.Length..];
                _directories.Add(newNormalized + (suffix.Length == 0 ? string.Empty : "/" + suffix));
            }

            foreach (var pair in renamedFiles)
            {
                var suffix = pair.Key[oldPrefix.Length..];
                _files[newPrefix + suffix] = pair.Value with { LastWriteTimeUtc = DateTime.UtcNow };
            }

            AddParentDirectories(newNormalized, _directories);
            Save();
            return;
        }

        throw new FileNotFoundException("Entry was not found in the CFS archive.", oldNormalized);
    }

    public void ExtractFile(string entryPath, string outputFile)
    {
        var bytes = ReadFile(entryPath);
        var directory = Path.GetDirectoryName(outputFile);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(outputFile, bytes);
    }

    public void ExtractAll(string outputFolder, IProgress<CfsProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputFolder);

        var totalBytes = _files.Values.Sum(file => file.OriginalSize);
        long completedItems = 0, completedBytes = 0;
        foreach (var directory in _directories)
        {
            Directory.CreateDirectory(GetSafeOutputPath(outputFolder, directory));
        }

        foreach (var pair in _files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outputPath = GetSafeOutputPath(outputFolder, pair.Key);
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var bytes = ReadRecordBytes(pair.Key, pair.Value);
            File.WriteAllBytes(outputPath, bytes);
            File.SetLastWriteTimeUtc(outputPath, pair.Value.LastWriteTimeUtc);
            completedItems++;
            completedBytes += bytes.Length;
            CfsProgressReporter.Report(progress, "Extracting archive", "Extracting files", pair.Key, completedItems, _files.Count, completedBytes, totalBytes);
        }
    }

    public bool ReplaceWithFolderSnapshot(string sourceFolder, string? excludedRootFileName = null, IProgress<CfsProgress>? progress = null, CancellationToken cancellationToken = default, IReadOnlySet<string>? excludedEntryPaths = null, bool persist = true)
    {
        if (!Directory.Exists(sourceFolder))
        {
            throw new DirectoryNotFoundException(sourceFolder);
        }

        var root = Path.GetFullPath(sourceFolder);
        var nextDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nextFiles = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            var normalized = NormalizeEntryPath(Path.GetRelativePath(root, directory));
            if (excludedEntryPaths?.Contains(normalized) == true) continue;
            EnsureMountedEntryIsSupported(new DirectoryInfo(directory), normalized, isDirectory: true);
            nextDirectories.Add(normalized);
            AddParentDirectories(normalized, nextDirectories);
        }

        var sourceFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
        long scannedItems = 0, scannedBytes = 0;
        var scanTotalBytes = sourceFiles.Sum(file => new FileInfo(file).Length);
        CfsProgressReporter.Report(progress, "Saving changes", "Scanning mounted folder", null, 0, sourceFiles.Count, 0, scanTotalBytes);
        foreach (var file in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = NormalizeEntryPath(Path.GetRelativePath(root, file));
            if (string.Equals(normalized, excludedRootFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (excludedEntryPaths?.Contains(normalized) == true) continue;
            EnsureMountedEntryIsSupported(new FileInfo(file), normalized, isDirectory: false);

            var snapshot = ReadConsistentSnapshot(file, normalized, cancellationToken);
            var bytes = snapshot.Bytes;
            var hash = ComputeSha256(bytes);

            if (_files.TryGetValue(normalized, out var existing) &&
                string.Equals(existing.Sha256, hash, StringComparison.OrdinalIgnoreCase))
            {
                nextFiles[normalized] = existing;
            }
            else
            {
                nextFiles[normalized] = FileRecord.New(bytes, snapshot.LastWriteTimeUtc);
            }

            AddParentDirectories(normalized, nextDirectories);
            scannedItems++;
            scannedBytes += bytes.Length;
            CfsProgressReporter.Report(progress, "Saving changes", "Comparing changes", normalized, scannedItems, sourceFiles.Count, scannedBytes, scanTotalBytes);
        }

        var previousFiles = new Dictionary<string, FileRecord>(_files, StringComparer.OrdinalIgnoreCase);
        var previousDirectories = new HashSet<string>(_directories, StringComparer.OrdinalIgnoreCase);
        var namespaceChanged = !previousDirectories.SetEquals(nextDirectories)
            || previousFiles.Count != nextFiles.Count
            || previousFiles.Any(pair => !nextFiles.TryGetValue(pair.Key, out var current) || current != pair.Value);
        if (!namespaceChanged) return false;

        try
        {
            _files.Clear();
            foreach (var pair in nextFiles)
            {
                _files[pair.Key] = pair.Value;
            }

            _directories.Clear();
            foreach (var directory in nextDirectories)
            {
                _directories.Add(directory);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (persist) Save(progress, cancellationToken);
            return true;
        }
        catch
        {
            _files.Clear();
            foreach (var pair in previousFiles)
            {
                _files[pair.Key] = pair.Value;
            }

            _directories.Clear();
            foreach (var directory in previousDirectories)
            {
                _directories.Add(directory);
            }

            throw;
        }
    }

    private static (byte[] Bytes, DateTime LastWriteTimeUtc) ReadConsistentSnapshot(
        string filePath,
        string entryPath,
        CancellationToken cancellationToken)
    {
        try
        {
            // FileShare.Read permits concurrent readers but rejects any existing or new
            // writer for the entire snapshot read. CFS therefore never commits a buffer
            // that can change underneath it and does not claim filesystem snapshot
            // semantics that it does not implement.
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                options: FileOptions.SequentialScan);
            if (stream.Length < 0 || stream.Length > MaximumEntryUncompressedBytes)
                throw new CfsArchiveException($"Archive entry '{entryPath}' exceeds the {MaximumEntryUncompressedBytes} byte safety limit.");

            var bytes = new byte[checked((int)stream.Length)];
            var offset = 0;
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0) throw new EndOfStreamException($"Mounted archive entry '{entryPath}' changed length while being read.");
                offset += read;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return (bytes, File.GetLastWriteTimeUtc(filePath));
        }
        catch (IOException ex) when (CfsFileInUseException.IsSharingOrLockViolation(ex))
        {
            throw new CfsFileInUseException(entryPath, ex);
        }
    }

    private void EnsureMountedEntryIsSupported(FileSystemInfo entry, string normalizedPath, bool isDirectory)
    {
        var attributes = entry.Attributes;
        if ((attributes & FileAttributes.SparseFile) != 0)
            throw new CfsArchiveException($"CFS cannot commit sparse {DescribeEntry(isDirectory)} '{normalizedPath}'. Sparse-file semantics are not preserved.");
        if ((attributes & FileAttributes.Device) != 0)
            throw new CfsArchiveException($"CFS cannot commit device {DescribeEntry(isDirectory)} '{normalizedPath}'.");
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            // ProjFS itself represents known archive entries with reparse attributes.
            // A newly introduced reparse point is an unsupported user object and must
            // never be followed or silently represented in the archive.
            var knownProjectedEntry = isDirectory ? _directories.Contains(normalizedPath) : _files.ContainsKey(normalizedPath);
            if (!knownProjectedEntry)
                throw new CfsArchiveException($"CFS cannot commit reparse-point {DescribeEntry(isDirectory)} '{normalizedPath}'. Links and junctions are not supported in CFS archives.");
        }
    }

    private static string DescribeEntry(bool isDirectory) => isDirectory ? "directory" : "file";

    /// <summary>
    /// Writes this in-memory archive state to a new same-volume candidate and proves that
    /// candidate can be reopened. This method deliberately never replaces <see cref="ArchivePath"/>.
    /// The broker owns the later backup/replacement transaction.
    /// </summary>
    public void WriteValidatedCandidate(string candidatePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        var fullCandidate = Path.GetFullPath(candidatePath);
        if (string.Equals(fullCandidate, Path.GetFullPath(ArchivePath), StringComparison.OrdinalIgnoreCase))
            throw new CfsArchiveException("A CFS commit candidate must not be the authoritative archive.");
        if (File.Exists(fullCandidate))
            throw new CfsArchiveException("Refusing to overwrite an existing CFS commit candidate.");

        cancellationToken.ThrowIfCancellationRequested();
        // Start the candidate as a byte-for-byte copy of the authoritative archive,
        // then append only dirty blocks and a new manifest. This keeps unchanged entry
        // offsets stable while preserving the broker's separate atomic replacement step.
        if (File.Exists(ArchivePath))
        {
            File.Copy(ArchivePath, fullCandidate, overwrite: false);
            AppendCurrentManifest(progress: null, cancellationToken: cancellationToken, outputPath: fullCandidate);
        }
        else
        {
            WriteArchive(fullCandidate, _files, _directories);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!Validate(fullCandidate, cancellationToken: cancellationToken).IsValid)
            throw new CfsArchiveException("The CFS commit candidate failed validation.");
    }

    public void Save(IProgress<CfsProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (File.Exists(ArchivePath))
        {
            AppendCurrentManifest(progress, cancellationToken);
        }
        else
        {
            ReplaceArchive();
        }
    }

    private void ReplaceArchive(CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(ArchivePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = Path.Combine(directory ?? Environment.CurrentDirectory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteArchive(tempPath, _files, _directories);
            if (!Validate(tempPath, cancellationToken: cancellationToken).IsValid)
                throw new CfsArchiveException("The newly written CFS archive failed validation.");

            if (File.Exists(fullPath))
            {
                File.Replace(tempPath, fullPath, null);
            }
            else
            {
                File.Move(tempPath, fullPath);
            }
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
    }

    public static string NormalizeEntryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path cannot be empty.", nameof(path));
        }

        if (path.Length > 32_767)
            throw new CfsArchiveException("Archive paths cannot exceed 32,767 UTF-16 code units.");
        var replaced = path.Replace('\\', '/');
        if (Path.IsPathRooted(replaced) || replaced.StartsWith('/'))
        {
            throw new CfsArchiveException("Archive paths must be relative.");
        }

        // Do not trim or remove empty components: aliases such as "a//b", leading
        // separators, and trailing separators must be rejected rather than silently
        // normalized into a different projected path.
        var parts = replaced.Split('/');
        if (parts.Length == 0 || parts.Length > 256
            || parts.Any(part => part.Length == 0 || part == "." || part == ".."))
        {
            throw new CfsArchiveException("Archive paths cannot contain empty, '.' or '..' components or exceed 256 levels.");
        }

        foreach (var part in parts)
        {
            if (part.Length > 255) throw new CfsArchiveException("Archive path segments cannot exceed 255 UTF-16 code units.");
            if (part.Any(ch => char.IsControl(ch) || ch is '<' or '>' or '"' or '|' or '?' or '*'))
                throw new CfsArchiveException("Archive paths contain characters that Windows cannot represent safely.");
            for (var index = 0; index < part.Length; index++)
            {
                if (char.IsHighSurrogate(part[index]))
                {
                    if (index + 1 >= part.Length || !char.IsLowSurrogate(part[index + 1]))
                        throw new CfsArchiveException("Archive paths cannot contain invalid Unicode.");
                    index++;
                }
                else if (char.IsLowSurrogate(part[index]))
                    throw new CfsArchiveException("Archive paths cannot contain invalid Unicode.");
            }
            if (part.IndexOf(':') >= 0) throw new CfsArchiveException("Archive paths cannot contain NTFS alternate data stream syntax.");
            if (part.EndsWith(' ') || part.EndsWith('.')) throw new CfsArchiveException("Archive path segments cannot end with a space or period.");
            var stem = Path.GetFileNameWithoutExtension(part);
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9'))
                throw new CfsArchiveException("Archive paths cannot use Windows reserved device names.");
        }

        return string.Join('/', parts);
    }

    private void AppendCurrentManifest(IProgress<CfsProgress>? progress, CancellationToken cancellationToken, string? outputPath = null)
    {
        var fullPath = Path.GetFullPath(outputPath ?? ArchivePath);
        var oldHeader = new byte[HeaderLength];

        using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.ReadExactly(oldHeader);
            stream.Position = stream.Length;

            var dirtyFiles = _files
                         .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                         .Where(item => item.Value.Dirty)
                         .ToList();
            var totalDirtyBytes = dirtyFiles.Sum(item => item.Value.OriginalSize);
            long completedItems = 0, completedBytes = 0;
            foreach (var pair in dirtyFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = pair.Value;
                var bytes = record.Bytes ?? throw new CfsArchiveException($"Dirty archive entry '{pair.Key}' has no staged content.");
                var method = bytes.Length == 0 ? CompressionNone : CompressionLzma2RawV2;
                var offset = stream.Position;
                var compressedBytes = method == CompressionNone ? [] : Compress(bytes);
                stream.Write(compressedBytes);

                _files[pair.Key] = record with
                {
                    Offset = offset,
                    CompressedSize = compressedBytes.Length,
                    CompressionMethod = method,
                    Sha256 = ComputeSha256(bytes),
                    Dirty = false
                };
                completedItems++;
                completedBytes += bytes.Length;
                CfsProgressReporter.Report(progress, "Saving changes", "Compressing changed files", pair.Key, completedItems, dirtyFiles.Count, completedBytes, totalDirtyBytes);
            }

            CfsProgressReporter.Report(progress, "Saving changes", "Writing manifest", null, completedItems, dirtyFiles.Count, completedBytes, totalDirtyBytes);
            var manifestOffset = stream.Position;
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(BuildManifest(), JsonOptions);
            stream.Write(manifestBytes);
            var newHeader = BuildHeader(manifestOffset, manifestBytes.Length);

            stream.Position = 0;
            stream.Write(newHeader);
            stream.Flush(flushToDisk: true);
        }

        try
        {
            if (!Validate(fullPath, cancellationToken: cancellationToken).IsValid)
                throw new CfsArchiveException("The updated CFS archive failed validation.");
        }
        catch
        {
            using var restore = new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            restore.Position = 0;
            restore.Write(oldHeader);
            restore.Flush(flushToDisk: true);
            throw;
        }
    }

    private static void WriteArchive(string path, Dictionary<string, FileRecord> files, HashSet<string> directories)
    {
        using var stream = File.Create(path);
        stream.Write(new byte[HeaderLength]);

        foreach (var pair in files.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase).ToList())
        {
            var record = pair.Value;
            var bytes = record.Bytes ?? throw new CfsArchiveException($"New archive entry '{pair.Key}' has no staged content.");
            var method = bytes.Length == 0 ? CompressionNone : CompressionLzma2RawV2;
            var offset = stream.Position;
            var compressedBytes = method == CompressionNone ? [] : Compress(bytes);
            stream.Write(compressedBytes);
            files[pair.Key] = record with
            {
                Offset = offset,
                CompressedSize = compressedBytes.Length,
                CompressionMethod = method,
                Sha256 = ComputeSha256(bytes),
                Dirty = false
            };
        }

        var manifestOffset = stream.Position;
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(BuildManifest(files, directories), JsonOptions);
        stream.Write(manifestBytes);

        stream.Position = 0;
        stream.Write(BuildHeader(manifestOffset, manifestBytes.Length));
        stream.Flush(flushToDisk: true);
    }

    private CfsManifest BuildManifest()
    {
        return BuildManifest(_files, _directories);
    }

    private static CfsManifest BuildManifest(Dictionary<string, FileRecord> files, HashSet<string> directories)
    {
        var entries = new List<CfsEntry>();

        foreach (var directory in directories.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            entries.Add(new CfsEntry
            {
                Path = directory,
                Type = ArchiveEntryType.Directory,
                LastWriteTimeUtc = DateTimeOffset.UnixEpoch,
                CompressionMethod = CompressionNone
            });
        }

        entries.AddRange(files
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => ToEntry(pair.Key, pair.Value)));

        return new CfsManifest { Entries = entries };
    }

    private static CfsEntry ToEntry(string path, FileRecord record)
    {
        return new CfsEntry
        {
            Path = path,
            Type = ArchiveEntryType.File,
            OriginalSize = record.OriginalSize,
            CompressedSize = record.CompressedSize,
            Offset = record.Offset,
            CompressionMethod = record.CompressionMethod,
            Sha256 = record.Sha256,
            LastWriteTimeUtc = record.LastWriteTimeUtc
        };
    }

    private static byte[] BuildHeader(long manifestOffset, long manifestLength)
    {
        var header = new byte[HeaderLength];
        using var stream = new MemoryStream(header);
        stream.Write(Magic);
        stream.Write(BitConverter.GetBytes(FormatVersion));
        stream.Write(BitConverter.GetBytes(manifestOffset));
        stream.Write(BitConverter.GetBytes(manifestLength));
        return header;
    }

    private static CfsManifest ReadManifest(Stream stream)
    {
        if (stream.Length < HeaderLength)
        {
            throw new CfsArchiveException("Archive is too small to be a CFS file.");
        }

        Span<byte> magic = stackalloc byte[4];
        stream.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic))
        {
            throw new CfsArchiveException("File is not a CFS archive.");
        }

        Span<byte> intBuffer = stackalloc byte[4];
        stream.ReadExactly(intBuffer);
        var version = BitConverter.ToInt32(intBuffer);
        if (version != FormatVersion)
        {
            throw new CfsArchiveException($"Unsupported CFS format version {version}.");
        }

        Span<byte> longBuffer = stackalloc byte[8];
        stream.ReadExactly(longBuffer);
        var manifestOffset = BitConverter.ToInt64(longBuffer);
        stream.ReadExactly(longBuffer);
        var manifestLength = BitConverter.ToInt64(longBuffer);

        if (manifestOffset < HeaderLength || manifestLength <= 0 || manifestLength > MaximumManifestBytes
            || manifestOffset > stream.Length - manifestLength)
        {
            throw new CfsArchiveException("Archive manifest location is invalid.");
        }

        stream.Position = manifestOffset;
        var manifestBytes = new byte[checked((int)manifestLength)];
        stream.ReadExactly(manifestBytes);
        var manifest = JsonSerializer.Deserialize<CfsManifest>(manifestBytes, JsonOptions)
            ?? throw new CfsArchiveException("Archive manifest could not be read.");
        if (manifest.Entries is null || manifest.Entries.Count > MaximumEntryCount)
            throw new CfsArchiveException($"Archive manifest exceeds the {MaximumEntryCount} entry safety limit.");
        ValidateManifestStructure(manifest, manifestOffset);
        return manifest;
    }

    private static void ValidateManifestStructure(CfsManifest manifest, long manifestOffset)
    {
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dataRanges = new List<(long Start, long End, string Path)>();
        long totalProjectedBytes = 0;

        foreach (var entry in manifest.Entries)
        {
            if (entry is null) throw new CfsArchiveException("Archive manifest contains an empty entry.");
            var normalized = NormalizeEntryPath(entry.Path);
            if (!string.Equals(normalized, entry.Path, StringComparison.Ordinal))
                throw new CfsArchiveException($"Archive manifest path '{normalized}' is not canonical.");
            if (!seenPaths.Add(normalized))
                throw new CfsArchiveException($"Archive manifest contains a duplicate or conflicting path '{normalized}'.");

            if (entry.Type == ArchiveEntryType.Directory)
            {
                if (entry.OriginalSize != 0 || entry.CompressedSize != 0 || entry.Offset != 0
                    || !string.Equals(entry.CompressionMethod, CompressionNone, StringComparison.Ordinal))
                    throw new CfsArchiveException($"Directory entry '{normalized}' contains file data.");
                continue;
            }
            if (entry.Type != ArchiveEntryType.File)
                throw new CfsArchiveException($"Unsupported entry type for '{normalized}'.");

            filePaths.Add(normalized);
            if (entry.OriginalSize < 0 || entry.OriginalSize > MaximumEntryUncompressedBytes
                || entry.CompressedSize < 0 || entry.CompressedSize > MaximumEntryUncompressedBytes
                || entry.Offset < HeaderLength
                || entry.CompressedSize > manifestOffset
                || entry.Offset > manifestOffset - entry.CompressedSize)
                throw new CfsArchiveException($"Invalid data block for '{normalized}'.");
            try { totalProjectedBytes = checked(totalProjectedBytes + entry.OriginalSize); }
            catch (OverflowException) { throw new CfsArchiveException("Archive total projected size overflowed its safety limit."); }
            if (totalProjectedBytes > MaximumTotalUncompressedBytes)
                throw new CfsArchiveException($"Archive total projected size exceeds the {MaximumTotalUncompressedBytes} byte safety limit.");

            if (entry.OriginalSize == 0)
            {
                if (entry.CompressedSize != 0 || !string.Equals(entry.CompressionMethod, CompressionNone, StringComparison.Ordinal))
                    throw new CfsArchiveException($"Empty entry '{normalized}' has inconsistent compression metadata.");
            }
            else if (entry.CompressedSize <= 0
                || entry.CompressionMethod is not (CompressionLzma2 or CompressionLzma2RawV2))
                throw new CfsArchiveException($"Unsupported or inconsistent compression method for '{normalized}'.");

            if (!IsSha256(entry.Sha256))
                throw new CfsArchiveException($"Archive entry '{normalized}' has an invalid SHA-256 value.");
            if (entry.CompressedSize > 0)
                dataRanges.Add((entry.Offset, checked(entry.Offset + entry.CompressedSize), normalized));
        }

        foreach (var filePath in filePaths)
        {
            var separator = filePath.IndexOf('/');
            while (separator >= 0)
            {
                if (filePaths.Contains(filePath[..separator]))
                    throw new CfsArchiveException($"Archive manifest contains a file/directory ancestor conflict at '{filePath}'.");
                separator = filePath.IndexOf('/', separator + 1);
            }
        }

        long previousEnd = HeaderLength;
        string? previousPath = null;
        foreach (var range in dataRanges.OrderBy(range => range.Start).ThenBy(range => range.End))
        {
            if (range.Start < previousEnd)
                throw new CfsArchiveException($"Archive data blocks for '{previousPath}' and '{range.Path}' overlap.");
            previousEnd = range.End;
            previousPath = range.Path;
        }
    }

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64) return false;
        try { return Convert.FromHexString(value).Length == 32; }
        catch (FormatException) { return false; }
    }

    private static byte[] ReadFileBytes(Stream stream, CfsEntry entry)
    {
        if (entry.OriginalSize < 0 || entry.OriginalSize > MaximumEntryUncompressedBytes
            || entry.CompressedSize < 0 || entry.CompressedSize > MaximumEntryUncompressedBytes
            || entry.Offset < HeaderLength || entry.CompressedSize > stream.Length
            || entry.Offset > stream.Length - entry.CompressedSize)
        {
            throw new CfsArchiveException($"Invalid data block for '{entry.Path}'.");
        }

        stream.Position = entry.Offset;
        var compressed = new byte[checked((int)entry.CompressedSize)];
        stream.ReadExactly(compressed);

        var bytes = entry.CompressionMethod switch
        {
            CompressionNone => [],
            CompressionLzma2 => Lzma2Compressor.Decompress(compressed, entry.OriginalSize),
            CompressionLzma2RawV2 => Lzma2Compressor.DecompressRaw(compressed, entry.OriginalSize),
            _ => throw new CfsArchiveException($"Unsupported compression method '{entry.CompressionMethod}'.")
        };

        if (bytes.LongLength != entry.OriginalSize)
        {
            throw new CfsArchiveException($"Size check failed for '{entry.Path}'.");
        }

        var actualHash = ComputeSha256(bytes);
        if (!string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CfsArchiveException($"Checksum failed for '{entry.Path}'.");
        }

        return bytes;
    }

    private static byte[] Compress(byte[] bytes)
    {
        return Lzma2Compressor.Compress(bytes);
    }

    private static string ComputeSha256(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private byte[] ReadRecordBytes(string entryPath, FileRecord record)
    {
        if (record.Bytes is not null) return record.Bytes;
        return ReadManifestEntry(ArchivePath, new CfsEntry
        {
            Path = entryPath,
            Type = ArchiveEntryType.File,
            OriginalSize = record.OriginalSize,
            CompressedSize = record.CompressedSize,
            Offset = record.Offset,
            CompressionMethod = record.CompressionMethod,
            Sha256 = record.Sha256,
            LastWriteTimeUtc = record.LastWriteTimeUtc
        });
    }

    private static void AddParentDirectories(string path, HashSet<string> directories)
    {
        var parts = NormalizeEntryPath(path).Split('/');
        for (var i = 1; i < parts.Length; i++)
        {
            directories.Add(string.Join('/', parts.Take(i)));
        }
    }

    private void EnsureNoDirectoryAtPath(string path)
    {
        if (_directories.Contains(path))
        {
            throw new CfsArchiveException($"A directory already exists at '{path}'.");
        }
    }

    private void EnsureDestinationAvailable(string path)
    {
        if (_files.ContainsKey(path) || _directories.Contains(path))
        {
            throw new CfsArchiveException($"An entry already exists at '{path}'.");
        }
    }

    private static string GetSafeOutputPath(string outputFolder, string entryPath)
    {
        var normalized = NormalizeEntryPath(entryPath);
        var root = Path.GetFullPath(outputFolder);
        var target = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));

        if (!target.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new CfsArchiveException($"Archive path escapes output folder: {entryPath}");
        }

        return target;
    }

    private sealed record FileRecord(
        byte[]? Bytes,
        long OriginalSize,
        DateTime LastWriteTimeUtc,
        long Offset,
        long CompressedSize,
        string CompressionMethod,
        string Sha256,
        bool Dirty)
    {
        public static FileRecord New(byte[] bytes, DateTime lastWriteTimeUtc)
        {
            return new FileRecord(
                bytes,
                bytes.LongLength,
                lastWriteTimeUtc,
                Offset: 0,
                CompressedSize: 0,
                CompressionMethod: bytes.Length == 0 ? CompressionNone : CompressionLzma2,
                Sha256: ComputeSha256(bytes),
                Dirty: true);
        }

        public static FileRecord FromEntry(CfsEntry entry)
        {
            return new FileRecord(
                null,
                entry.OriginalSize,
                entry.LastWriteTimeUtc.UtcDateTime,
                entry.Offset,
                entry.CompressedSize,
                entry.CompressionMethod,
                entry.Sha256,
                Dirty: false);
        }
    }
}
