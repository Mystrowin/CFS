namespace Cfs.Core;

public sealed class CfsFileInUseException : IOException
{
    public CfsFileInUseException(string entryPath, Exception innerException)
        : base($"The mounted archive entry '{entryPath}' is open for writing. Close the file and retry the commit.", innerException)
    {
        EntryPath = entryPath;
    }

    public string EntryPath { get; }

    internal static bool IsSharingOrLockViolation(IOException exception)
    {
        var nativeCode = exception.HResult & 0xFFFF;
        return nativeCode is 32 or 33;
    }
}
