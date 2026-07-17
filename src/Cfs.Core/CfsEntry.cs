namespace Cfs.Core;

public sealed class CfsEntry
{
    public string Path { get; set; } = string.Empty;
    public ArchiveEntryType Type { get; set; }
    public long OriginalSize { get; set; }
    public long CompressedSize { get; set; }
    public long Offset { get; set; }
    public string CompressionMethod { get; set; } = CfsArchive.CompressionLzma2;
    public string Sha256 { get; set; } = string.Empty;
    public DateTimeOffset LastWriteTimeUtc { get; set; }
}
