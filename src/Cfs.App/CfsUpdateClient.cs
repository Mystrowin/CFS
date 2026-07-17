using Cfs.Core;
using System.ComponentModel;
using System.Diagnostics;

namespace Cfs.App;

internal sealed class CfsUpdateClient
{
    private static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(24);
    private readonly Form _owner;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly string _statePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CFS", "last-update-check.txt");

    public CfsUpdateClient(Form owner) => _owner = owner;

    public async Task CheckAsync(bool manual)
    {
        if (!manual && !IsAutomaticCheckDue()) return;

        try
        {
            AppLog.Write($"Checking for updates manual={manual}");
            using var response = await _http.GetAsync(CfsProductInfo.UpdateManifestDestination);
            response.EnsureSuccessStatusCode();
            var manifest = CfsUpdateManifest.Parse(await response.Content.ReadAsStringAsync());
            RecordCheck();

            if (!manifest.IsNewerThan(CfsProductInfo.VersionNumber))
            {
                if (manual) MessageBox.Show(_owner, "CFS is up to date.", "CFS — Check for Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (OperatingSystem.IsWindows() && Environment.OSVersion.Version.Build < manifest.MinimumWindowsBuild)
            {
                if (manual) MessageBox.Show(_owner, $"CFS {manifest.Version} requires Windows build {manifest.MinimumWindowsBuild} or newer.", "CFS — Update Not Compatible", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var choice = MessageBox.Show(
                _owner,
                $"CFS {manifest.Version} ({manifest.Channel}) is available.\n\nOpen the release notes and download the verified installer?",
                "CFS — Update Available",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (choice != DialogResult.Yes) return;

            Process.Start(new ProcessStartInfo(manifest.ReleaseNotesUrl) { UseShellExecute = true });
            await DownloadAndOfferInstallAsync(manifest);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            AppLog.WriteException("update-check", ex);
            if (manual)
                MessageBox.Show(_owner, "CFS could not check for updates. Your current installation is unchanged.\n\n" + ex.Message, "CFS — Update Check Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task DownloadAndOfferInstallAsync(CfsUpdateManifest manifest)
    {
        var downloadDirectory = Path.Combine(Path.GetTempPath(), "CFS", "updates", manifest.Version);
        Directory.CreateDirectory(downloadDirectory);
        var setupPath = Path.Combine(downloadDirectory, "CFS-" + manifest.Version + "-Setup.exe");
        using (var response = await _http.GetAsync(manifest.SetupUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var destination = new FileStream(setupPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(destination);
        }

        if (!manifest.VerifyFile(setupPath))
        {
            File.Delete(setupPath);
            throw new InvalidDataException("The downloaded installer failed SHA-256 verification and was deleted.");
        }

        if (MessageBox.Show(_owner, "The installer passed SHA-256 verification. Close CFS and start the update now?", "CFS — Install Update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        Process.Start(new ProcessStartInfo(setupPath) { UseShellExecute = true, Verb = "runas" });
        _owner.Close();
    }

    private bool IsAutomaticCheckDue()
    {
        try
        {
            return !File.Exists(_statePath) || DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(_statePath) >= AutomaticCheckInterval;
        }
        catch { return true; }
    }

    private void RecordCheck()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        File.WriteAllText(_statePath, DateTimeOffset.UtcNow.ToString("O"));
    }
}
