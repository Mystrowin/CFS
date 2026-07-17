using System.Security.Cryptography;
using System.Text;

namespace Cfs.Core;

public sealed class CfsDiagnosticLogger
{
    private readonly object _writeLock = new();

    public CfsDiagnosticLogger(string logDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        LogDirectory = Path.GetFullPath(logDirectory);
        LogPath = Path.Combine(LogDirectory, "CFS.log");
    }

    public string LogDirectory { get; }
    public string LogPath { get; }

    public void WriteStartup() => Write("startup",
        $"version={CfsProductInfo.ReleaseIdentity} build={CfsProductInfo.BuildIdentifier} windows={Environment.OSVersion.VersionString}");

    public void Write(string eventName, string? details = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        var line = $"{DateTimeOffset.UtcNow:O} event={Sanitize(eventName)}";
        if (!string.IsNullOrWhiteSpace(details)) line += " " + Sanitize(details);
        lock (_writeLock)
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
        }
    }

    public void WritePathEvent(string eventName, string path, string outcome) =>
        Write(eventName, $"target={DescribePath(path)} outcome={outcome}");

    public void WriteException(string eventName, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Write(eventName, $"result=error exceptionType={exception.GetType().FullName} message={exception.Message} stackTrace={exception.StackTrace ?? "<unavailable>"}");
    }

    public void WriteMountCleanupFailure(string mountPath, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mountPath);
        ArgumentNullException.ThrowIfNull(exception);
        var line = $"{DateTimeOffset.UtcNow:O} event=mount.cleanup result=error preservedMountPath={Path.GetFullPath(mountPath)} exceptionType={exception.GetType().FullName} message={Sanitize(exception.Message)} stackTrace={Sanitize(exception.StackTrace ?? "<unavailable>")}";
        lock (_writeLock)
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
        }
    }

    public static string DescribePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "<none>";
        var normalized = Path.GetFullPath(path).ToUpperInvariant();
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..12].ToLowerInvariant();
        var extension = Path.GetExtension(path);
        return string.IsNullOrWhiteSpace(extension) ? $"path-id:{id}" : $"path-id:{id};type:{extension.ToLowerInvariant()}";
    }

    private static string Sanitize(string value)
    {
        var result = value.Replace('\r', ' ').Replace('\n', ' ');
        foreach (var privateRoot in new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) }
                     .Where(root => !string.IsNullOrWhiteSpace(root)).Distinct(StringComparer.OrdinalIgnoreCase))
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                System.Text.RegularExpressions.Regex.Escape(privateRoot) + @"(?:[\\/][^\s]+)*",
                "<private-path>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return result;
    }
}

public static class CfsDiagnostics
{
    private static CfsDiagnosticLogger _logger = new(DefaultLogDirectory);
    public static string DefaultLogDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CFS", "Logs");
    public static CfsDiagnosticLogger Logger
    {
        get => Volatile.Read(ref _logger);
        set => Interlocked.Exchange(ref _logger, value ?? throw new ArgumentNullException(nameof(value)));
    }
}

public sealed record CfsBugReportAction(bool IsConfigured, Uri? Destination, string Message);

public static class CfsSupportActions
{
    public static CfsBugReportAction ResolveBugReportDestination(string? configuredDestination)
    {
        if (string.IsNullOrWhiteSpace(configuredDestination) || configuredDestination.Equals("BUG_REPORT_URL_NOT_CONFIGURED", StringComparison.Ordinal))
            return new(false, null, $"Bug reporting is not configured yet. Include {CfsProductInfo.DisplayName}, build {CfsProductInfo.BuildIdentifier}, Windows {Environment.OSVersion.VersionString}, and the diagnostic log when reporting through a project-owner-provided channel.");
        if (!Uri.TryCreate(configuredDestination, UriKind.Absolute, out var destination) || destination.Scheme != Uri.UriSchemeHttps)
            return new(false, null, "The configured bug-report destination is invalid. An absolute HTTPS URL is required.");
        return new(true, destination, $"Report for {CfsProductInfo.DisplayName}, build {CfsProductInfo.BuildIdentifier}, Windows {Environment.OSVersion.VersionString}.");
    }
}
