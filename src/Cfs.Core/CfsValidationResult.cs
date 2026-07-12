namespace Cfs.Core;

public sealed class CfsValidationResult
{
    public bool IsValid { get; init; }
    public int FileCount { get; init; }
    public int DirectoryCount { get; init; }
    public string Message { get; init; } = string.Empty;
}
