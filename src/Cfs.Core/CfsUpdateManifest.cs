using System.Security.Cryptography;
using System.Text.Json;

namespace Cfs.Core;

public sealed record CfsUpdateManifest(
    int SchemaVersion,
    string Version,
    string Channel,
    DateTimeOffset PublishedUtc,
    string Architecture,
    int MinimumWindowsBuild,
    string SetupUrl,
    string Sha256,
    string ReleaseNotesUrl,
    bool Mandatory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static CfsUpdateManifest Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var manifest = JsonSerializer.Deserialize<CfsUpdateManifest>(json, JsonOptions)
            ?? throw new InvalidDataException("The update manifest was empty.");
        manifest.Validate();
        return manifest;
    }

    public bool IsNewerThan(string installedVersion)
    {
        if (!System.Version.TryParse(Version, out var available) || !System.Version.TryParse(installedVersion, out var installed))
            throw new InvalidDataException("The update manifest contains an invalid version.");
        return available > installed;
    }

    public bool VerifyFile(string path)
    {
        using var stream = File.OpenRead(path);
        return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException("Unsupported update manifest schema.");
        if (!System.Version.TryParse(Version, out _)) throw new InvalidDataException("Invalid update version.");
        if (!string.Equals(Architecture, "x64", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("This update is not for x64 Windows.");
        if (MinimumWindowsBuild < 17763) throw new InvalidDataException("Invalid minimum Windows build.");
        if (Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid update checksum.");
        ValidateHttpsUrl(SetupUrl, "setup");
        ValidateHttpsUrl(ReleaseNotesUrl, "release notes");
    }

    private static void ValidateHttpsUrl(string value, string label)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException($"The {label} URL must use HTTPS.");
    }
}
