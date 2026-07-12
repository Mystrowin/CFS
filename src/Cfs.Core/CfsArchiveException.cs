namespace Cfs.Core;

public sealed class CfsArchiveException : Exception
{
    public CfsArchiveException(string message) : base(message)
    {
    }

    public CfsArchiveException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
