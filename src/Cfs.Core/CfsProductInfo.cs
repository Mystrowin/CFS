using System.Reflection;

namespace Cfs.Core;

public static class CfsProductInfo
{
    public const string BetaSafetyWarning = """
        CFS is experimental beta software.

        Do not use CFS as the only copy of important or irreplaceable files. Keep a separate backup of every file stored in a .cfs archive.

        This beta may contain bugs involving application compatibility, mounting, saving, performance, or data integrity. Use it only for testing and non-critical files.
        """;

    public const string BugReportDestination = "https://github.com/Mystrowin/CFS/issues";
    public const string UpdateManifestDestination = "https://raw.githubusercontent.com/Mystrowin/CFS/main/update.json";
    public const string VersionNumber = "0.3.0";

    private static readonly Assembly ProductAssembly = typeof(CfsProductInfo).Assembly;

    public static string ReleaseIdentity { get; } =
        ProductAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? throw new InvalidOperationException("CFS informational version metadata is missing.");

    public static string DisplayName => "CFS " + ReleaseIdentity;
    public static string WindowTitle => DisplayName;
    public static string AcknowledgementKey => ReleaseIdentity;
    public static string BuildIdentifier { get; } =
        $"{ReleaseIdentity.Replace(' ', '-')}-{ProductAssembly.ManifestModule.ModuleVersionId:N}";

    public static string BetaInformation => $"""
        {DisplayName}
        Build: {BuildIdentifier}

        Experimental beta software — not production-ready.

        {BetaSafetyWarning}

        Support and bug reports: {BugReportDestination}
        Include this version and build identifier with every report.
        """;
}

public sealed class CfsBetaAcknowledgement
{
    public CfsBetaAcknowledgement(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CFS", "beta-warning-acknowledgement.txt");

    public bool ShouldShow(string acknowledgementKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(acknowledgementKey);
        try
        {
            return !File.Exists(Path)
                || !string.Equals(File.ReadAllText(Path).Trim(), acknowledgementKey, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public void Acknowledge(string acknowledgementKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(acknowledgementKey);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, acknowledgementKey);
    }
}
