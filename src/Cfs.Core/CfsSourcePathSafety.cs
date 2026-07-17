namespace Cfs.Core;

public static class CfsSourcePathSafety
{
    public static string ValidateFolderTree(string sourceFolder)
    {
        return EnumerateFolderTree(sourceFolder).RootPath;
    }

    public static string ValidateFolderRoot(string sourceFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFolder);
        var fullPath = Path.GetFullPath(Path.TrimEndingDirectorySeparator(sourceFolder));
        var rootPath = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(rootPath) && string.Equals(Path.TrimEndingDirectorySeparator(fullPath), Path.TrimEndingDirectorySeparator(rootPath), StringComparison.OrdinalIgnoreCase))
            throw new CfsArchiveException("Compress to CFS does not accept a drive or volume root. Select a folder beneath the root.");
        if (File.Exists(fullPath)) throw new CfsArchiveException("Compress to CFS requires a folder, not a file.");
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException($"Source folder was not found: '{fullPath}'.");
        RejectReparsePoint(new DirectoryInfo(fullPath));
        return fullPath;
    }

    public static CfsSafeSourceTree EnumerateFolderTree(string sourceFolder)
    {
        var fullPath = ValidateFolderRoot(sourceFolder);

        var pending = new Stack<DirectoryInfo>();
        var directories = new List<string>();
        var files = new List<string>();
        pending.Push(new DirectoryInfo(fullPath));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            RejectReparsePoint(directory);
            directories.Add(directory.FullName);
            foreach (var entry in directory.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
            {
                RejectReparsePoint(entry);
                if (entry is DirectoryInfo child) pending.Push(child);
                else if (entry is FileInfo file) files.Add(file.FullName);
            }
        }
        return new CfsSafeSourceTree(fullPath, directories, files);
    }

    public static void RevalidateFileForRead(string rootPath, string filePath)
    {
        var root = Path.GetFullPath(rootPath);
        var file = Path.GetFullPath(filePath);
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new CfsArchiveException("Source enumeration escaped the selected folder.");
        RejectReparsePoint(new FileInfo(file));
        var parent = Directory.GetParent(file);
        while (parent is not null && parent.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            RejectReparsePoint(parent);
            if (string.Equals(parent.FullName, root, StringComparison.OrdinalIgnoreCase)) break;
            parent = parent.Parent;
        }
    }

    private static void RejectReparsePoint(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new CfsArchiveException("Compress to CFS does not follow symbolic links, junctions, or other reparse points. Remove the reparse entry and try again.");
    }
}

public sealed record CfsSafeSourceTree(string RootPath, IReadOnlyList<string> Directories, IReadOnlyList<string> Files);
