using Cfs.Core;

namespace Cfs.App;

internal static class AppLog
{
    public static string LogPath => CfsDiagnostics.Logger.LogPath;
    public static string LogDirectory => CfsDiagnostics.Logger.LogDirectory;

    public static void Write(string message)
    {
        try
        {
            CfsDiagnostics.Logger.Write("application", message);
        }
        catch
        {
        }
    }

    public static void WriteException(string eventName, Exception exception)
    {
        try { CfsDiagnostics.Logger.WriteException(eventName, exception); }
        catch { }
    }
}
