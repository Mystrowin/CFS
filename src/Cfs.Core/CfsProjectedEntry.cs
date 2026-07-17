namespace Cfs.Core;

public sealed record CfsProjectedEntry(
    string Path,
    ArchiveEntryType Type,
    long Size,
    DateTimeOffset LastWriteTimeUtc,
    long Offset,
    long CompressedSize,
    string CompressionMethod,
    string Sha256);
