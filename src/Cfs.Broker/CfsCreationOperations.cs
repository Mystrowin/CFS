using Cfs.Core;

namespace Cfs.Broker;

public interface ICfsProgressSurface
{
    void Show(string message);
    void Close();
}

public sealed class CfsDelayedProgressScope : IAsyncDisposable
{
    private readonly ICfsProgressSurface _surface;
    private readonly CancellationTokenSource _delayCancellation = new();
    private readonly Task _showTask;
    private int _shown;
    private int _showAttempted;

    public CfsDelayedProgressScope(ICfsProgressSurface surface, string message, TimeSpan delay)
    {
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay));
        _showTask = ShowAfterDelayAsync(message, delay);
    }

    public bool WasShown => Volatile.Read(ref _shown) != 0;

    private async Task ShowAfterDelayAsync(string message, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _delayCancellation.Token).ConfigureAwait(false);
            try
            {
                Interlocked.Exchange(ref _showAttempted, 1);
                _surface.Show(message);
                Interlocked.Exchange(ref _shown, 1);
            }
            catch (Exception ex) { TryLogProgressFailure(ex); }
        }
        catch (OperationCanceledException) when (_delayCancellation.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        _delayCancellation.Cancel();
        try { await _showTask.ConfigureAwait(false); }
        catch (Exception ex) { TryLogProgressFailure(ex); }
        if (Volatile.Read(ref _showAttempted) != 0)
        {
            try { _surface.Close(); }
            catch (Exception ex) { TryLogProgressFailure(ex); }
        }
        _delayCancellation.Dispose();
    }

    private static void TryLogProgressFailure(Exception exception)
    {
        try { CfsDiagnostics.Logger.WriteException("broker.progress", exception); }
        catch { }
    }
}

public sealed class CfsNativeProgressSurface : ICfsProgressSurface
{
    private readonly object _sync = new();
    private Thread? _thread;
    private Form? _form;
    private ManualResetEventSlim? _ready;
    private Exception? _threadFailure;

    public void Show(string message)
    {
        lock (_sync)
        {
            if (_thread is not null) return;
            _ready = new ManualResetEventSlim(false);
            _thread = new Thread(() => RunWindow(message)) { IsBackground = true, Name = "CFS compression progress" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
        if (!_ready!.Wait(TimeSpan.FromSeconds(5)))
        {
            Close();
            throw new TimeoutException("The CFS progress surface did not start within five seconds.");
        }
        if (_threadFailure is not null) throw new InvalidOperationException("The CFS progress surface could not start.", _threadFailure);
    }

    private void RunWindow(string message)
    {
        try
        {
            Application.EnableVisualStyles();
            var form = new Form
            {
                Text = "CFS — Compressing folder", Width = 420, Height = 125,
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                ControlBox = false, StartPosition = FormStartPosition.CenterScreen, ShowInTaskbar = true
            };
            form.Controls.Add(new Label { Text = message, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter });
            lock (_sync) _form = form;
            form.Shown += (_, _) => _ready?.Set();
            Application.Run(form);
        }
        catch (Exception ex)
        {
            lock (_sync) _threadFailure = ex;
        }
        finally { _ready?.Set(); }
    }

    public void Close()
    {
        Form? form; Thread? thread;
        lock (_sync) { form = _form; thread = _thread; }
        if (form is not null && !form.IsDisposed)
        {
            try { form.BeginInvoke(new Action(form.Close)); }
            catch (InvalidOperationException) { }
        }
        if (thread is not null && !thread.Join(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("The CFS progress surface did not close within five seconds.");
        lock (_sync) { _form = null; _thread = null; _threadFailure = null; _ready?.Dispose(); _ready = null; }
    }
}

public sealed class CfsCreationOperations
{
    private readonly Func<ICfsProgressSurface> _progressFactory;
    private readonly TimeSpan _progressDelay;
    private readonly Action<string, string, CancellationToken>? _archiveCreator;

    public CfsCreationOperations(Func<ICfsProgressSurface> progressFactory, TimeSpan? progressDelay = null,
        Action<string, string, CancellationToken>? archiveCreator = null)
    {
        _progressFactory = progressFactory ?? throw new ArgumentNullException(nameof(progressFactory));
        _progressDelay = progressDelay ?? TimeSpan.FromMilliseconds(750);
        if (_progressDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(progressDelay));
        _archiveCreator = archiveCreator;
    }

    public string CreateEmpty(string targetPath)
    {
        CfsWritableStoragePolicy.EnsureSupported(targetPath);
        var archive = CfsArchive.CreateEmpty(targetPath);
        CfsDiagnostics.Logger.WritePathEvent("broker.create-empty", archive.ArchivePath, "success");
        return archive.ArchivePath;
    }

    public Task<CfsCreationResult> CompressFolderAsync(string sourceFolder, CancellationToken cancellationToken) =>
        CompressFolderAsync(sourceFolder, progress: null, cancellationToken);

    public async Task<CfsCreationResult> CompressFolderAsync(string sourceFolder, IProgress<CfsProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        // Root validation is intentionally fast. The Core creator owns the one
        // authoritative, non-following traversal after the progress timer starts.
        var source = CfsSourcePathSafety.ValidateFolderRoot(sourceFolder);
        CfsWritableStoragePolicy.EnsureSupported(source);
        var parent = Directory.GetParent(source)?.FullName
            ?? throw new CfsArchiveException("A drive root cannot be compressed beside itself.");
        var baseName = Path.GetFileName(source);
        var workspace = CfsCompressionWorkspace.Create(parent);
        var temporaryArchive = workspace.ArchivePath;
        ICfsProgressSurface surface;
        if (progress is not null)
        {
            // The Explorer command client owns the structured progress window.
            // Do not display a second broker-owned wait dialog.
            surface = NoOpProgressSurface.Instance;
        }
        else try { surface = _progressFactory() ?? throw new InvalidOperationException("The progress factory returned no surface."); }
        catch (Exception progressFailure)
        {
            TryLogProgressFactoryFailure(progressFailure);
            surface = NoOpProgressSurface.Instance;
        }
        await using var delayedSurface = new CfsDelayedProgressScope(surface, $"Compressing {baseName} to CFS…", _progressDelay);
        try
        {
            await Task.Run(() =>
            {
                if (_archiveCreator is not null)
                    _archiveCreator(source, temporaryArchive, cancellationToken);
                else
                    CfsArchive.CreateFromFolder(source, temporaryArchive, progress, cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            string? committedArchive = null;
            for (var suffix = 1; suffix < int.MaxValue; suffix++)
            {
                var candidateName = suffix == 1 ? $"{baseName}.cfs" : $"{baseName} ({suffix}).cfs";
                var candidate = Path.Combine(parent, candidateName);
                try
                {
                    File.Move(temporaryArchive, candidate, overwrite: false);
                    CfsDiagnostics.Logger.WritePathEvent("broker.compress", candidate, "success");
                    committedArchive = candidate;
                    break;
                }
                catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate)) { }
            }
            if (committedArchive is null)
                throw new CfsArchiveException("CFS could not allocate a collision-free archive name.");

            try
            {
                workspace.Cleanup();
                return new CfsCreationResult(committedArchive);
            }
            catch (Exception cleanupFailure)
            {
                TryLogCleanupFailure(cleanupFailure);
                return new CfsCreationResult(committedArchive,
                    "The archive was created successfully and is usable, but CFS could not remove its hidden temporary workspace. See the diagnostic log.");
            }
        }
        catch
        {
            try { workspace.Cleanup(); }
            catch (Exception cleanupFailure) { TryLogCleanupFailure(cleanupFailure); }
            throw;
        }
    }

    /// <summary>Extracts a validated archive to a newly created sibling folder without overwriting existing user files.</summary>
    public async Task<CfsExtractionResult> ExtractArchiveAsync(string archivePath, IProgress<CfsProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var identity = CfsArchiveIdentity.Create(archivePath);
        CfsWritableStoragePolicy.EnsureSupported(identity.FullPath);
        var validation = CfsArchive.Validate(identity.FullPath, progress: null, cancellationToken: cancellationToken);
        if (!validation.IsValid) throw new CfsArchiveException("CFS refused to extract an invalid archive: " + validation.Message);
        var archive = CfsArchive.Load(identity.FullPath, cancellationToken: cancellationToken);
        var parent = Path.GetDirectoryName(identity.FullPath) ?? throw new CfsArchiveException("The archive has no parent directory.");
        var baseName = Path.GetFileNameWithoutExtension(identity.FullPath);
        var workspace = Path.Combine(parent, ".cfs-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        string MoveToNewOutputFolder()
        {
            for (var suffix = 1; suffix < int.MaxValue; suffix++)
            {
                var name = suffix == 1 ? baseName + " extracted" : $"{baseName} extracted ({suffix})";
                var candidate = Path.Combine(parent, name);
                try
                {
                    Directory.Move(workspace, candidate);
                    return candidate;
                }
                catch (IOException) when (Directory.Exists(candidate) || File.Exists(candidate)) { }
            }
            throw new CfsArchiveException("CFS could not allocate a collision-free extraction folder.");
        }

        try
        {
            await Task.Run(() => archive.ExtractAll(workspace, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
            var output = MoveToNewOutputFolder();
            CfsDiagnostics.Logger.WritePathEvent("broker.extract", identity.FullPath, "success");
            return new CfsExtractionResult(output);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            string partialOutput;
            try { partialOutput = MoveToNewOutputFolder(); }
            catch (Exception ex)
            {
                TryLogCleanupFailure(ex);
                partialOutput = workspace;
            }
            throw new CfsPartialExtractionException(partialOutput);
        }
        catch
        {
            try { if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true); }
            catch (Exception ex) { TryLogCleanupFailure(ex); }
            throw;
        }
    }

    private static void TryLogCleanupFailure(Exception cleanupFailure)
    {
        try { CfsDiagnostics.Logger.WriteException("broker.compress.cleanup", cleanupFailure); }
        catch { }
    }

    private static void TryLogProgressFactoryFailure(Exception progressFailure)
    {
        try { CfsDiagnostics.Logger.WriteException("broker.progress.factory", progressFailure); }
        catch { }
    }

    private sealed class NoOpProgressSurface : ICfsProgressSurface
    {
        public static NoOpProgressSurface Instance { get; } = new();
        public void Show(string message) { }
        public void Close() { }
    }
}

public sealed record CfsCreationResult(string OutputPath, string? Warning = null);
public sealed record CfsExtractionResult(string OutputPath);

public sealed class CfsPartialExtractionException(string outputPath) : OperationCanceledException("CFS extraction was cancelled; already extracted files were preserved."),
    IHasCfsOutputPath
{
    public string OutputPath { get; } = outputPath;
}

public interface IHasCfsOutputPath
{
    string OutputPath { get; }
}

public sealed class CfsCompressionWorkspace : IDisposable
{
    private const string MarkerName = ".cfs-work-owner";
    private readonly string _markerValue;
    private int _cleanupAttempted;

    private CfsCompressionWorkspace(string folderPath, string markerValue)
    {
        FolderPath = folderPath;
        _markerValue = markerValue;
        ArchivePath = Path.Combine(folderPath, "archive.cfs.tmp");
    }

    public string FolderPath { get; }
    public string ArchivePath { get; }

    public static CfsCompressionWorkspace Create(string targetParent)
    {
        var parent = Path.GetFullPath(targetParent);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
        var folder = Path.Combine(parent, $".cfs-work-{Guid.NewGuid():N}");
        var marker = Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, MarkerName), marker);
            if (OperatingSystem.IsWindows())
                File.SetAttributes(folder, File.GetAttributes(folder) | FileAttributes.Hidden | FileAttributes.NotContentIndexed);
            return new CfsCompressionWorkspace(folder, marker);
        }
        catch
        {
            try
            {
                var markerPath = Path.Combine(folder, MarkerName);
                if (Directory.Exists(folder) && ((!Directory.EnumerateFileSystemEntries(folder).Any()) || (File.Exists(markerPath) && File.ReadAllText(markerPath) == marker)))
                    Directory.Delete(folder, recursive: true);
            }
            catch { }
            throw;
        }
    }

    public void Cleanup()
    {
        if (Interlocked.Exchange(ref _cleanupAttempted, 1) != 0) return;
        var markerPath = Path.Combine(FolderPath, MarkerName);
        if (!Directory.Exists(FolderPath)) return;
        if (!File.Exists(markerPath) || File.ReadAllText(markerPath) != _markerValue)
            throw new CfsArchiveException("Refusing to clean an unverified CFS compression workspace.");
        Directory.Delete(FolderPath, recursive: true);
    }

    public void Dispose() => Cleanup();
}
