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

    public static CfsArchive CreateFromFolder(string sourceFolder, string archivePath, IProgress<CfsProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(sourceFolder))
        {
            throw new DirectoryNotFoundException(sourceFolder);
        }

        var files = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(sourceFolder);

        var sourceFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
        var totalBytes = sourceFiles.Sum(file => new FileInfo(file).Length);
        CfsProgressReporter.Report(progress, "Creating archive", "Scanning source folder", null, 0, sourceFiles.Count, 0, totalBytes);
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(root, directory);
            directories.Add(NormalizeEntryPath(relativePath));
        }

        long completedItems = 0, completedBytes = 0;
        foreach (var file in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(root, file);
            var normalized = NormalizeEntryPath(relativePath);
            files[normalized] = FileRecord.New(File.ReadAllBytes(file), File.GetLastWriteTimeUtc(file));
            AddParentDirectories(normalized, directories);
            completedItems++;
            completedBytes += files[normalized].Bytes.Length;
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
        var totalBytes = orderedEntries.Where(e => e.Type == ArchiveEntryType.File).Sum(e => e.OriginalSize);
        long completedItems = 0, completedBytes = 0;
        CfsProgressReporter.Report(progress, "Opening archive", "Reading archive metadata", null, 0, orderedEntries.Count, 0, totalBytes);
        foreach (var entry in orderedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = NormalizeEntryPath(entry.Path);
            if (entry.Type == ArchiveEntryType.Directory)
            {
                directories.Add(normalized);
                completedItems++;
                CfsProgressReporter.Report(progress, "Opening archive", "Validating manifest", normalized, completedItems, orderedEntries.Count, completedBytes, totalBytes);
                continue;
            }

            if (entry.Type != ArchiveEntryType.File)
            {
                throw new CfsArchiveException($"Unsupported entry type for {entry.Path}.");
            }

            var bytes = ReadFileBytes(stream, entry);
            files[normalized] = FileRecord.FromEntry(bytes, entry);
            AddParentDirectories(normalized, directories);
            completedItems++;
            completedBytes += entry.OriginalSize;
            CfsProgressReporter.Report(progress, "Opening archive", "Reading archive entries", normalized, completedItems, orderedEntries.Count, completedBytes, totalBytes);
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
            CfsDiagnostics.Logger.WritePathEvent("archive.validation", archivePath, "success");
            return new CfsValidationResult
            {
                IsValid = true,
                FileCount = entries.Count(entry => entry.Type == ArchiveEntryType.File),
                DirectoryCount = entries.Count(entry => entry.Type == ArchiveEntryType.Directory),
                Message = "Archive is valid."
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

        return record.Bytes.ToArray();
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

        var totalBytes = _files.Values.Sum(file => (long)file.Bytes.Length);
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

            File.WriteAllBytes(outputPath, pair.Value.Bytes);
            File.SetLastWriteTimeUtc(outputPath, pair.Value.LastWriteTimeUtc);
            completedItems++;
            completedBytes += pair.Value.Bytes.Length;
            CfsProgressReporter.Report(progress, "Extracting archive", "Extracting files", pair.Key, completedItems, _files.Count, completedBytes, totalBytes);
        }
    }

    public void ReplaceWithFolderSnapshot(string sourceFolder, string? excludedRootFileName = null, IProgress<CfsProgress>? progress = null, CancellationToken cancellationToken = default, IReadOnlySet<string>? excludedEntryPaths = null)
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

            var bytes = File.ReadAllBytes(file);
            var hash = ComputeSha256(bytes);

            if (_files.TryGetValue(normalized, out var existing) &&
                string.Equals(existing.Sha256, hash, StringComparison.OrdinalIgnoreCase))
            {
                nextFiles[normalized] = existing;
            }
            else
            {
                nextFiles[normalized] = FileRecord.New(bytes, File.GetLastWriteTimeUtc(file));
            }

            AddParentDirectories(normalized, nextDirectories);
            scannedItems++;
            scannedBytes += bytes.Length;
            CfsProgressReporter.Report(progress, "Saving changes", "Comparing changes", normalized, scannedItems, sourceFiles.Count, scannedBytes, scanTotalBytes);
        }

        var previousFiles = new Dictionary<string, FileRecord>(_files, StringComparer.OrdinalIgnoreCase);
        var previousDirectories = new HashSet<string>(_directories, StringComparer.OrdinalIgnoreCase);

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
            Save(progress, cancellationToken);
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
            using (File.OpenRead(tempPath))
            {
                _ = Load(tempPath);
            }

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

        var replaced = path.Replace('\\', '/').Trim('/');
        if (Path.IsPathRooted(replaced))
        {
            throw new CfsArchiveException("Archive paths must be relative.");
        }

        var parts = replaced.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(part => part == "." || part == ".."))
        {
            throw new CfsArchiveException("Archive paths cannot contain '.' or '..'.");
        }

        return string.Join('/', parts);
    }

    private void AppendCurrentManifest(IProgress<CfsProgress>? progress, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(ArchivePath);
        var oldHeader = new byte[HeaderLength];

        using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.ReadExactly(oldHeader);
            stream.Position = stream.Length;

            var dirtyFiles = _files
                         .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                         .Where(item => item.Value.Dirty)
                         .ToList();
            var totalDirtyBytes = dirtyFiles.Sum(item => (long)item.Value.Bytes.Length);
            long completedItems = 0, completedBytes = 0;
            foreach (var pair in dirtyFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = pair.Value;
                var method = record.Bytes.Length == 0 ? CompressionNone : CompressionLzma2RawV2;
                var offset = stream.Position;
                var compressedBytes = method == CompressionNone ? [] : Compress(record.Bytes);
                stream.Write(compressedBytes);

                _files[pair.Key] = record with
                {
                    Offset = offset,
                    CompressedSize = compressedBytes.Length,
                    CompressionMethod = method,
                    Sha256 = ComputeSha256(record.Bytes),
                    Dirty = false
                };
                completedItems++;
                completedBytes += record.Bytes.Length;
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
            _ = Load(fullPath);
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
            var bytes = record.Bytes;
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
            OriginalSize = record.Bytes.Length,
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

        if (manifestOffset < HeaderLength || manifestLength <= 0 || manifestOffset + manifestLength > stream.Length)
        {
            throw new CfsArchiveException("Archive manifest location is invalid.");
        }

        stream.Position = manifestOffset;
        var manifestBytes = new byte[manifestLength];
        stream.ReadExactly(manifestBytes);
        return JsonSerializer.Deserialize<CfsManifest>(manifestBytes, JsonOptions)
               ?? throw new CfsArchiveException("Archive manifest could not be read.");
    }

    private static byte[] ReadFileBytes(Stream stream, CfsEntry entry)
    {
        if (entry.CompressedSize < 0 || entry.Offset < HeaderLength || entry.Offset + entry.CompressedSize > stream.Length)
        {
            throw new CfsArchiveException($"Invalid data block for '{entry.Path}'.");
        }

        stream.Position = entry.Offset;
        var compressed = new byte[entry.CompressedSize];
        stream.ReadExactly(compressed);

        var bytes = entry.CompressionMethod switch
        {
            CompressionNone => [],
            CompressionLzma2 => Decompress(compressed),
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

    private static byte[] Decompress(byte[] bytes)
    {
        return Lzma2Compressor.Decompress(bytes);
    }

    private static string ComputeSha256(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
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
        byte[] Bytes,
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
                lastWriteTimeUtc,
                Offset: 0,
                CompressedSize: 0,
                CompressionMethod: bytes.Length == 0 ? CompressionNone : CompressionLzma2,
                Sha256: ComputeSha256(bytes),
                Dirty: true);
        }

        public static FileRecord FromEntry(byte[] bytes, CfsEntry entry)
        {
            return new FileRecord(
                bytes,
                entry.LastWriteTimeUtc.UtcDateTime,
                entry.Offset,
                entry.CompressedSize,
                entry.CompressionMethod,
                entry.Sha256,
                Dirty: false);
        }
    }
}
