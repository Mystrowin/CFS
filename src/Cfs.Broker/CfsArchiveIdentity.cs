using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Cfs.Broker;

public sealed record CfsArchiveIdentity(
    string FullPath,
    string Key,
    string MountKey,
    uint? VolumeSerialNumber,
    ulong? FileId,
    long FileSize,
    DateTimeOffset LastWriteTimeUtc,
    string HeaderFingerprint)
{
    public static CfsArchiveIdentity Create(string path, string? baseDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new BrokerRequestException("invalid-path", "An archive path is required.");

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0)
            throw new BrokerRequestException("invalid-path", "The archive path is empty after normalization.");

        string fullPath;
        try
        {
            fullPath = Path.IsPathFullyQualified(trimmed)
                ? Path.GetFullPath(trimmed)
                : Path.GetFullPath(trimmed, baseDirectory ?? Environment.CurrentDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new BrokerRequestException("invalid-path", "The archive path is not a valid Windows path.", ex);
        }

        fullPath = fullPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetExtension(fullPath), ".cfs", StringComparison.OrdinalIgnoreCase))
            throw new BrokerRequestException("invalid-extension", "Only .cfs archives can be opened by CFS Broker.");
        if (!File.Exists(fullPath))
            throw new BrokerRequestException("archive-not-found", "The requested .cfs archive does not exist or is not accessible.");

        var info = new FileInfo(fullPath);
        var headerFingerprint = ReadHeaderFingerprint(fullPath);
        var native = TryGetNativeIdentity(fullPath);
        // The broker session key is path-stable: CFS's own atomic replacement changes
        // the destination file ID, but must not strand the live session or make status
        // lookups report a new clean 0/0 session. File identity remains separately
        // recorded and is compared by RepresentsSameFile before a writable commit.
        var key = $"path:{fullPath.ToUpperInvariant()}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToUpperInvariant()))).ToLowerInvariant();
        return new CfsArchiveIdentity(fullPath, key, hash[..32], native?.VolumeSerialNumber, native?.FileId,
            info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), headerFingerprint);
    }

    public bool RepresentsSameFile(CfsArchiveIdentity other)
    {
        if (other is null || !StringComparer.OrdinalIgnoreCase.Equals(FullPath, other.FullPath)) return false;
        if (VolumeSerialNumber is { } volume && FileId is { } fileId
            && other.VolumeSerialNumber is { } otherVolume && other.FileId is { } otherFileId)
            return volume == otherVolume && fileId == otherFileId;
        return FileSize == other.FileSize
            && LastWriteTimeUtc == other.LastWriteTimeUtc
            && StringComparer.OrdinalIgnoreCase.Equals(HeaderFingerprint, other.HeaderFingerprint);
    }

    public bool MatchesAuthoritativeState(CfsArchiveIdentity other)
    {
        if (!RepresentsSameFile(other)) return false;

        // A stable Windows file ID proves that this is the same filesystem object,
        // but it does not prove that another process did not modify that object in
        // place. Size, timestamp, and the CFS header fingerprint are conflict
        // signals. CFS refreshes all of them only after its own replacement has
        // succeeded and the destination has been reopened and validated.
        return FileSize == other.FileSize
            && LastWriteTimeUtc == other.LastWriteTimeUtc
            && StringComparer.OrdinalIgnoreCase.Equals(HeaderFingerprint, other.HeaderFingerprint);
    }

    private static string ReadHeaderFingerprint(string path)
    {
        const int maximumHeaderBytes = 64 * 1024;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var count = checked((int)Math.Min(stream.Length, maximumHeaderBytes));
        var buffer = new byte[count];
        stream.ReadExactly(buffer);
        return Convert.ToHexString(SHA256.HashData(buffer));
    }

    private static NativeFileIdentity? TryGetNativeIdentity(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, options: FileOptions.None);
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out var information)) return null;
            return new NativeFileIdentity(information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { return null; }
    }

    private sealed record NativeFileIdentity(uint VolumeSerialNumber, ulong FileId);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out ByHandleFileInformation information);
}

public sealed class BrokerRequestException : Exception
{
    public BrokerRequestException(string errorCode, string message, Exception? innerException = null)
        : base(message, innerException) => ErrorCode = errorCode;

    public string ErrorCode { get; }
}
