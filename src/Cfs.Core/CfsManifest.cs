namespace Cfs.Core;

public sealed class CfsManifest
{
    public int Version { get; set; } = CfsArchive.FormatVersion;
    public List<CfsEntry> Entries { get; set; } = [];
}
