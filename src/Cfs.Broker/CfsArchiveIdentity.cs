using System.Security.Cryptography;
using System.Text;

namespace Cfs.Broker;

public sealed record CfsArchiveIdentity(string FullPath, string Key, string MountKey)
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

        // The comparer is OrdinalIgnoreCase; upper-casing only supplies stable hash input.
        var key = fullPath;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToUpperInvariant()))).ToLowerInvariant();
        return new CfsArchiveIdentity(fullPath, key, hash[..32]);
    }
}

public sealed class BrokerRequestException : Exception
{
    public BrokerRequestException(string errorCode, string message, Exception? innerException = null)
        : base(message, innerException) => ErrorCode = errorCode;

    public string ErrorCode { get; }
}
