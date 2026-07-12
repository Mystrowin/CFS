namespace Cfs.Core;

public sealed record CfsProgress(
    string Operation,
    string Phase,
    string? CurrentPath,
    long CompletedItems,
    long? TotalItems,
    long CompletedBytes,
    long? TotalBytes);

internal static class CfsProgressReporter
{
    public static void Report(IProgress<CfsProgress>? progress, string operation, string phase, string? path, long items, long? totalItems, long bytes, long? totalBytes)
        => progress?.Report(new CfsProgress(operation, phase, path, items, totalItems, bytes, totalBytes));
}
