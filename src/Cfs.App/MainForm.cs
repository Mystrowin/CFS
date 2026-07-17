using Cfs.Core;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace Cfs.App;

public sealed class MainForm : Form
{
    private readonly Button _createButton = new() { Text = "Create from Folder" };
    private readonly Button _openButton = new() { Text = "Open .cfs" };
    private readonly Button _registerButton = new() { Text = "Register Broker for .cfs" };
    private readonly Button _betaInfoButton = new() { Text = "Beta Information" };
    private readonly Button _openLogsButton = new() { Text = "Open Logs Folder" };
    private readonly Button _reportBugButton = new() { Text = "Report Bug" };
    private readonly Button _checkUpdatesButton = new() { Text = "Check for Updates" };
    private readonly Button _closeButton = new() { Text = "Close" };
    private readonly Button _validateButton = new() { Text = "Validate" };
    private readonly Button _openExplorerButton = new() { Text = "Open in Explorer" };
    private readonly Button _compatibilityModeButton = new() { Text = "Compatibility Mode (Full Extraction)" };
    private readonly Button _saveMountedButton = new() { Text = "Save Mounted Changes" };
    private readonly Button _unmountButton = new() { Text = "Unmount" };
    private readonly Button _upButton = new() { Text = "Up" };
    private readonly Button _newFileButton = new() { Text = "New File" };
    private readonly Button _importButton = new() { Text = "Import File" };
    private readonly Button _overwriteButton = new() { Text = "Overwrite" };
    private readonly Button _newFolderButton = new() { Text = "New Folder" };
    private readonly Button _renameButton = new() { Text = "Rename" };
    private readonly Button _deleteButton = new() { Text = "Delete" };
    private readonly Button _extractButton = new() { Text = "Extract" };
    private readonly Label _archiveLabel = new() { AutoSize = true, Text = "No archive open" };
    private readonly Label _folderLabel = new() { AutoSize = true, Text = "Folder: /" };
    private readonly Label _mountLabel = new() { AutoSize = true, Text = "Mounted: no" };
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false };
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false };
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusLabel = new("Ready");
    private readonly Label _progressTitle = new() { AutoSize = true, Text = "" };
    private readonly Label _progressPhase = new() { AutoSize = true, Text = "" };
    private readonly Label _progressPath = new() { AutoSize = true, AutoEllipsis = true, MaximumSize = new Size(700, 0) };
    private readonly Label _progressCounters = new() { AutoSize = true, Text = "" };
    private readonly Label _progressPercent = new() { AutoSize = true, Text = "" };
    private readonly ProgressBar _progressBar = new() { Width = 420, Height = 18 };
    private readonly Button _cancelOperationButton = new() { Text = "Cancel", Visible = false, Enabled = false };
    private CancellationTokenSource? _operationCancellation;
    private bool _operationActive;

    private CfsArchive? _archive;
    private string _currentFolder = string.Empty;
    private CfsMountSession? _mountedSession;
    private bool _openInitialArchiveInExplorer;
    private readonly CfsUiStateModel _uiState = new();
    private readonly CfsUpdateClient _updateClient;

    public MainForm(string? initialArchive, bool openInitialArchiveInExplorer = false)
    {
        _updateClient = new CfsUpdateClient(this);
        Text = CfsProductInfo.WindowTitle;
        Width = 1100;
        Height = 700;
        MinimumSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        WireEvents();
        SetLifecycleState(CfsUiLifecycleState.Unmounted);
        SetArchiveControlsEnabled(false);

        if (!string.IsNullOrWhiteSpace(initialArchive) && File.Exists(initialArchive))
        {
            TryRun(() => OpenArchiveSynchronously(initialArchive), $"Opened {Path.GetFileName(initialArchive)}");
            if (openInitialArchiveInExplorer && _archive is not null)
            {
                AppLog.Write("Will open Explorer mount after form is shown for " + CfsDiagnosticLogger.DescribePath(initialArchive));
                _openInitialArchiveInExplorer = true;
            }
            else if (openInitialArchiveInExplorer)
            {
                AppLog.Write("Initial archive was not opened; skipping Explorer mount: " + CfsDiagnosticLogger.DescribePath(initialArchive));
            }
        }
        else if (!string.IsNullOrWhiteSpace(initialArchive))
        {
            AppLog.Write("Initial archive path does not exist: " + CfsDiagnosticLogger.DescribePath(initialArchive));
        }
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (_openInitialArchiveInExplorer && _archive is not null)
        {
            _openInitialArchiveInExplorer = false;
            await TryRunAsync(OpenMountedFolderInExplorerAsync, "Mounted folder opened in Explorer");
        }

        await _updateClient.CheckAsync(manual: false);
    }

    private void BuildLayout()
    {
        var topPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            Padding = new Padding(8)
        };

        var archiveButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        archiveButtons.Controls.AddRange([_createButton, _openButton, _registerButton, _betaInfoButton, _openLogsButton, _reportBugButton, _checkUpdatesButton, _closeButton, _validateButton, _archiveLabel]);

        var operationButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        operationButtons.Controls.AddRange([_openExplorerButton, _compatibilityModeButton, _saveMountedButton, _unmountButton, _upButton, _newFileButton, _importButton, _overwriteButton, _newFolderButton, _renameButton, _deleteButton, _extractButton, _folderLabel, _mountLabel]);

        topPanel.Controls.Add(archiveButtons);
        topPanel.Controls.Add(operationButtons);

        var progressPanel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Visible = false, Name = "progressPanel" };
        progressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        progressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        progressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        progressPanel.Controls.Add(_progressTitle, 0, 0);
        progressPanel.Controls.Add(_progressPhase, 1, 0);
        progressPanel.Controls.Add(_cancelOperationButton, 2, 0);
        progressPanel.Controls.Add(_progressBar, 0, 1);
        progressPanel.SetColumnSpan(_progressBar, 2);
        progressPanel.Controls.Add(_progressPercent, 2, 1);
        progressPanel.Controls.Add(_progressPath, 0, 2);
        progressPanel.SetColumnSpan(_progressPath, 3);
        progressPanel.Controls.Add(_progressCounters, 0, 3);
        progressPanel.SetColumnSpan(_progressCounters, 3);
        topPanel.Controls.Add(progressPanel);

        _list.Columns.Add("Name", 360);
        _list.Columns.Add("Type", 100);
        _list.Columns.Add("Size", 110);
        _list.Columns.Add("Modified", 180);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 300
        };
        split.Panel1.Controls.Add(_tree);
        split.Panel2.Controls.Add(_list);

        _status.Items.Add(_statusLabel);

        Controls.Add(split);
        Controls.Add(topPanel);
        Controls.Add(_status);
    }

    private void WireEvents()
    {
        _createButton.Click += async (_, _) => await TryRunAsync(CreateArchiveFromFolderAsync, "Archive created");
        _openButton.Click += async (_, _) => await TryRunAsync(OpenArchiveFromDialogAsync, "Archive opened");
        _registerButton.Click += (_, _) => TryRun(RegisterFileAssociation, ".cfs double-click registered");
        _betaInfoButton.Click += (_, _) => ShowBetaInformation();
        _openLogsButton.Click += (_, _) => TryRun(OpenLogsFolder, "Opened logs folder");
        _reportBugButton.Click += (_, _) => TryRun(ReportBug, "Bug report action opened");
        _checkUpdatesButton.Click += async (_, _) => await _updateClient.CheckAsync(manual: true);
        _closeButton.Click += (_, _) => TryRun(CloseArchiveFromButton, "Archive closed");
        _validateButton.Click += async (_, _) => await TryRunAsync(ValidateArchiveAsync, "Archive validated");
        _openExplorerButton.Click += async (_, _) => await TryRunAsync(OpenMountedFolderInExplorerAsync, "Mounted folder opened");
        _compatibilityModeButton.Click += async (_, _) => await TryRunAsync(OpenCompatibilityModeInExplorerAsync, "Compatibility Mode folder opened");
        _saveMountedButton.Click += async (_, _) => await TryRunAsync(SaveMountedChangesAsync, "Mounted changes saved");
        _unmountButton.Click += async (_, _) => await TryRunAsync(UnmountAsync, "Mounted folder closed");
        _upButton.Click += (_, _) => TryRun(NavigateUp, "Moved up");
        _newFileButton.Click += (_, _) => TryRun(CreateFile, "File created");
        _importButton.Click += (_, _) => TryRun(ImportFile, "File imported");
        _overwriteButton.Click += (_, _) => TryRun(OverwriteSelectedFile, "File overwritten");
        _newFolderButton.Click += (_, _) => TryRun(CreateFolder, "Folder created");
        _renameButton.Click += (_, _) => TryRun(RenameSelectedEntry, "Entry renamed");
        _deleteButton.Click += (_, _) => TryRun(DeleteSelectedEntry, "Entry deleted");
        _extractButton.Click += async (_, _) => await TryRunAsync(ExtractSelectedEntryAsync, "Extracted");
        _tree.AfterSelect += (_, _) => RefreshList();
        _list.DoubleClick += (_, _) => TryRun(OpenOrEnterSelectedEntry, "Opened");
        _cancelOperationButton.Click += (_, _) => _operationCancellation?.Cancel();
    }

    private void ShowBetaInformation()
    {
        MessageBox.Show(
            this,
            CfsProductInfo.BetaInformation,
            CfsProductInfo.DisplayName + " — Beta Information",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private void OpenLogsFolder()
    {
        Directory.CreateDirectory(AppLog.LogDirectory);
        AppLog.Write("Opening logs folder");
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppLog.LogDirectory}\"") { UseShellExecute = true });
    }

    private void ReportBug()
    {
        var action = CfsSupportActions.ResolveBugReportDestination(CfsProductInfo.BugReportDestination);
        AppLog.Write("Report Bug selected configured=" + action.IsConfigured);
        if (!action.IsConfigured)
        {
            MessageBox.Show(this, action.Message, CfsProductInfo.DisplayName + " — Report Bug", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Process.Start(new ProcessStartInfo(action.Destination!.AbsoluteUri) { UseShellExecute = true });
    }

    private async Task CreateArchiveFromFolderAsync()
    {
        using var folderDialog = new FolderBrowserDialog
        {
            Description = "Select the folder to convert into a .cfs archive",
            UseDescriptionForTitle = true
        };
        if (folderDialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        using var saveDialog = new SaveFileDialog
        {
            Filter = "CFS archive (*.cfs)|*.cfs",
            FileName = Path.GetFileName(folderDialog.SelectedPath.TrimEnd(Path.DirectorySeparatorChar)) + ".cfs",
            Title = "Create CFS Archive"
        };
        if (saveDialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        await RunOperationAsync("Creating archive", (progress, token) => CfsArchive.CreateFromFolder(folderDialog.SelectedPath, saveDialog.FileName, progress, token));
        await OpenArchiveAsync(saveDialog.FileName);
    }

    private async Task OpenArchiveFromDialogAsync()
    {
        using var openDialog = new OpenFileDialog
        {
            Filter = "CFS archive (*.cfs)|*.cfs|All files (*.*)|*.*",
            Title = "Open CFS Archive"
        };
        if (openDialog.ShowDialog(this) == DialogResult.OK)
        {
            await OpenArchiveAsync(openDialog.FileName);
        }
    }

    private void OpenArchiveSynchronously(string archivePath)
    {
        if (!CloseMountedSession(promptToSave: true))
        {
            return;
        }

        _archive = CfsArchive.Load(archivePath);
        CfsDiagnostics.Logger.WritePathEvent("archive.open", archivePath, "success");
        _archiveLabel.Text = archivePath;
        _currentFolder = string.Empty;
        SetArchiveControlsEnabled(true);
        RefreshTree();
        RefreshList();
    }

    private async Task OpenArchiveAsync(string archivePath)
    {
        if (!CloseMountedSession(promptToSave: true))
        {
            return;
        }

        _archive = await RunOperationAsync("Opening archive", (progress, token) => CfsArchive.Load(archivePath, progress, token));
        CfsDiagnostics.Logger.WritePathEvent("archive.open", archivePath, "success");
        _archiveLabel.Text = archivePath;
        _currentFolder = string.Empty;
        SetArchiveControlsEnabled(true);
        RefreshTree();
        RefreshList();
    }

    private void CloseArchiveFromButton()
    {
        _ = CloseArchive();
    }

    private bool CloseArchive()
    {
        if (!CloseMountedSession(promptToSave: true))
        {
            SetStatus("Close cancelled");
            return false;
        }

        _archive = null;
        _currentFolder = string.Empty;
        _archiveLabel.Text = "No archive open";
        _tree.Nodes.Clear();
        _list.Items.Clear();
        SetArchiveControlsEnabled(false);
        UpdateMountControls();
        SetStatus("Archive closed");
        return true;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try
        {
            if (CloseMountedSession(promptToSave: true))
            {
                base.OnFormClosing(e);
                return;
            }

            e.Cancel = true;
            SetStatus("Close cancelled");
        }
        catch (Exception ex)
        {
            e.Cancel = true;
            SetStatus("Error: " + ex.Message);
            MessageBox.Show(this, ex.Message, "CFS Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ImportFile()
    {
        var archive = RequireArchive();
        using var openDialog = new OpenFileDialog
        {
            Title = "Import File into CFS"
        };
        if (openDialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var targetName = Prompt("File name in archive", Path.GetFileName(openDialog.FileName));
        if (targetName is null)
        {
            return;
        }

        archive.WriteFile(CombineArchivePath(_currentFolder, targetName), File.ReadAllBytes(openDialog.FileName));
        ReloadArchive();
    }

    private void CreateFile()
    {
        var archive = RequireArchive();
        var fileName = Prompt("New file name", "New File.txt");
        if (fileName is null)
        {
            return;
        }

        var contents = PromptMultiline("File contents", string.Empty);
        if (contents is null)
        {
            return;
        }

        archive.WriteFile(CombineArchivePath(_currentFolder, fileName), System.Text.Encoding.UTF8.GetBytes(contents));
        ReloadArchive();
    }

    private void OverwriteSelectedFile()
    {
        var archive = RequireArchive();
        var entry = GetSelectedEntry();
        if (entry is null || entry.Type != ArchiveEntryType.File)
        {
            throw new CfsArchiveException("Select a file to overwrite.");
        }

        using var openDialog = new OpenFileDialog
        {
            Title = "Choose Replacement File"
        };
        if (openDialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        archive.WriteFile(entry.Path, File.ReadAllBytes(openDialog.FileName));
        ReloadArchive();
    }

    private void CreateFolder()
    {
        var archive = RequireArchive();
        var folderName = Prompt("New folder name", "New Folder");
        if (folderName is null)
        {
            return;
        }

        archive.CreateDirectory(CombineArchivePath(_currentFolder, folderName));
        ReloadArchive();
    }

    private void RenameSelectedEntry()
    {
        var archive = RequireArchive();
        var entry = GetSelectedEntry();
        if (entry is null)
        {
            throw new CfsArchiveException("Select a file or folder to rename.");
        }

        var currentName = entry.Path.Split('/').Last();
        var newName = Prompt("New name", currentName);
        if (newName is null || newName == currentName)
        {
            return;
        }

        archive.Rename(entry.Path, CombineArchivePath(ParentPath(entry.Path), newName));
        ReloadArchive();
    }

    private void DeleteSelectedEntry()
    {
        var archive = RequireArchive();
        var entry = GetSelectedEntry();
        if (entry is null)
        {
            throw new CfsArchiveException("Select a file or empty folder to delete.");
        }

        if (MessageBox.Show(this, $"Delete '{entry.Path}'?", "Confirm delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        if (entry.Type == ArchiveEntryType.File)
        {
            archive.DeleteFile(entry.Path);
        }
        else
        {
            archive.DeleteEmptyDirectory(entry.Path);
        }

        ReloadArchive();
    }

    private async Task ExtractSelectedEntryAsync()
    {
        var archive = RequireArchive();
        var entry = GetSelectedEntry();
        if (entry is null)
        {
            using var folderDialog = new FolderBrowserDialog { Description = "Extract all files to folder" };
            if (folderDialog.ShowDialog(this) == DialogResult.OK)
            {
                await RunOperationAsync("Extracting archive", (progress, token) => { archive.ExtractAll(folderDialog.SelectedPath, progress, token); return true; });
            }

            return;
        }

        if (entry.Type == ArchiveEntryType.Directory)
        {
            using var folderDialog = new FolderBrowserDialog { Description = "Extract archive to folder" };
            if (folderDialog.ShowDialog(this) == DialogResult.OK)
            {
                await RunOperationAsync("Extracting archive", (progress, token) => { archive.ExtractAll(folderDialog.SelectedPath, progress, token); return true; });
            }

            return;
        }

        using var saveDialog = new SaveFileDialog
        {
            FileName = entry.Path.Split('/').Last(),
            Title = "Extract File"
        };
        if (saveDialog.ShowDialog(this) == DialogResult.OK)
        {
            await RunOperationAsync("Extracting file", (progress, token) => { token.ThrowIfCancellationRequested(); archive.ExtractFile(entry.Path, saveDialog.FileName); return true; });
        }
    }

    private void OpenOrEnterSelectedEntry()
    {
        var archive = RequireArchive();
        var entry = GetSelectedEntry();
        if (entry is null)
        {
            return;
        }

        if (entry.Type == ArchiveEntryType.Directory)
        {
            SelectFolder(entry.Path);
            return;
        }

        var tempFolder = Path.Combine(Path.GetTempPath(), "CFS", Guid.NewGuid().ToString("N"));
        var tempFile = Path.Combine(tempFolder, entry.Path.Split('/').Last());
        archive.ExtractFile(entry.Path, tempFile);
        Process.Start(new ProcessStartInfo(tempFile) { UseShellExecute = true });
    }

    private void NavigateUp()
    {
        if (string.IsNullOrWhiteSpace(_currentFolder))
        {
            return;
        }

        SelectFolder(ParentPath(_currentFolder));
    }

    private void RegisterFileAssociation()
    {
        var command = BuildOpenCommand();
        var brokerPath = Path.Combine(AppContext.BaseDirectory, CfsShellRegistration.BrokerExecutableName);
        var templatePath = Path.Combine(AppContext.BaseDirectory, "ShellNew", "CFS-Empty.cfs");
        if (!File.Exists(templatePath)) throw new FileNotFoundException("The packaged CFS ShellNew template is missing. Repair the installation.", templatePath);
        using (var extensionKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.cfs"))
        {
            extensionKey.SetValue(null, "CFS.Archive");
        }

        using (var typeKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CFS.Archive"))
        {
            typeKey.SetValue(null, "CFS Compressed Folder");
        }

        using (var commandKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CFS.Archive\shell\open\command"))
        {
            commandKey.SetValue(null, command);
        }
        using (var closeVerb = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CFS.Archive\shell\CFS.Close"))
        {
            closeVerb.SetValue(null, "Close CFS");
            closeVerb.SetValue("Icon", brokerPath + ",0");
        }
        using (var closeCommand = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CFS.Archive\shell\CFS.Close\command"))
        {
            closeCommand.SetValue(null, CfsShellRegistration.BuildCloseCommand(brokerPath));
        }
        using (var shellNewKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.cfs\ShellNew"))
        {
            shellNewKey.SetValue("FileName", templatePath);
        }
        using (var verbKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\CFS.Compress"))
        {
            verbKey.SetValue(null, "Compress to CFS");
            verbKey.SetValue("Icon", brokerPath + ",0");
        }
        using (var verbCommand = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\CFS.Compress\command"))
        {
            verbCommand.SetValue(null, CfsShellRegistration.BuildCompressCommand(brokerPath));
        }
    }

    private async Task ValidateArchiveAsync()
    {
        var archive = RequireArchive();
        SetLifecycleState(CfsUiLifecycleState.Validating);
        try
        {
            var result = await RunOperationAsync("Validating archive", (progress, token) => CfsArchive.Validate(archive.ArchivePath, progress, token));
            CfsDiagnostics.Logger.WritePathEvent("archive.validation", archive.ArchivePath, result.IsValid ? "success" : "failed");
            if (!result.IsValid) throw new CfsArchiveException("Validation failed. Keep the archive and diagnostic log unchanged, and do not rely on this archive until the error is resolved. " + result.Message);

            MessageBox.Show(this, $"{result.Message}\n\nFiles: {result.FileCount}\nFolders: {result.DirectoryCount}", "CFS Validation", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            SetLifecycleState(_mountedSession is null ? CfsUiLifecycleState.Unmounted : CfsUiLifecycleState.Mounted, _mountedSession?.FolderPath);
        }
    }

    private async Task OpenCompatibilityModeInExplorerAsync()
    {
        if (MessageBox.Show(this,
                "Compatibility Mode fully extracts every archive file to a temporary folder. It is not an on-demand ProjFS mount and may use substantially more time and disk space. Continue explicitly in Compatibility Mode?",
                "Compatibility Mode (Full Extraction)", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        await OpenMountedFolderInExplorerAsync(compatibilityModeExplicitlySelected: true);
    }

    private async Task OpenMountedFolderInExplorerAsync() => await OpenMountedFolderInExplorerAsync(false);

    private async Task OpenMountedFolderInExplorerAsync(bool compatibilityModeExplicitlySelected)
    {
        var archive = RequireArchive();

        if (_mountedSession is null)
        {
            var availability = CfsProjFsPrerequisite.Check();
            var decision = CfsMountPolicy.Decide(availability, compatibilityModeExplicitlySelected);
            if (!decision.CanMount)
                throw new CfsArchiveException("CFS could not start the default ProjFS mount. " + decision.Message);

            var archiveName = Path.GetFileNameWithoutExtension(archive.ArchivePath);
            var mountFolder = Path.Combine(Path.GetTempPath(), "CFS", "mounts", $"{archiveName}-{Guid.NewGuid():N}");
            SetLifecycleState(CfsUiLifecycleState.Mounting);
            try
            {
                _mountedSession = await RunOperationAsync(
                    decision.Mode == CfsMountMode.ProjFs ? "Mounting through ProjFS" : "Compatibility Mode: fully extracting archive",
                    (progress, token) => decision.Mode == CfsMountMode.ProjFs
                        ? CfsMountSession.Create(archive, mountFolder, progress, token)
                        : CfsMountSession.CreateCompatibility(archive, mountFolder, progress, token));
                SetLifecycleState(CfsUiLifecycleState.Mounted, _mountedSession.FolderPath);
            }
            catch (Exception ex)
            {
                SetLifecycleState(CfsUiLifecycleState.Unmounted);
                CfsDiagnostics.Logger.WriteException("mount.ui", ex);
                var action = compatibilityModeExplicitlySelected ? "Compatibility Mode extraction failed. The partial temporary folder was removed; review the log and try again." : "ProjFS mounting failed and CFS did not fall back to extraction. Verify Client-ProjFS is enabled, then retry, or choose Compatibility Mode explicitly.";
                throw new CfsArchiveException(action, ex);
            }
        }

        AppLog.Write("Opening Explorer for mount " + CfsDiagnosticLogger.DescribePath(_mountedSession.FolderPath));
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_mountedSession.FolderPath}\"") { UseShellExecute = true });
        UpdateMountControls();
    }

    private async Task SaveMountedChangesAsync()
    {
        var archive = RequireArchive();
        if (_mountedSession is null)
        {
            throw new CfsArchiveException("No mounted folder is open.");
        }

        var mountPath = _mountedSession.FolderPath;
        SetLifecycleState(CfsUiLifecycleState.Saving);
        try
        {
            await RunOperationAsync("Saving mounted changes", (progress, token) => { _mountedSession.Save(archive, progress, token); return true; });
            ReloadArchive();
            SetLifecycleState(CfsUiLifecycleState.Mounted, mountPath);
        }
        catch (Exception ex)
        {
            SetLifecycleState(CfsUiLifecycleState.Mounted, mountPath);
            throw new CfsArchiveException($"Saving failed. The previous archive remains available and the mounted folder is preserved at '{mountPath}'. Close applications using its files, review the log, and retry Save Mounted Changes.", ex);
        }
    }

    private async Task UnmountAsync()
    {
        if (_mountedSession is null)
        {
            return;
        }

        if (MessageBox.Show(this, "Save mounted folder changes back to the .cfs before permanently unmounting? Select No to keep the folder mounted.", "Unmount CFS", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        var session = _mountedSession;
        var archive = RequireArchive();
        SetLifecycleState(CfsUiLifecycleState.Saving);
        try
        {
            await RunOperationAsync("Saving and unmounting", (progress, token) => { session.Save(archive, progress, token); session.PermanentlyDelete(progress, token); return true; });
            _mountedSession = null;
            ReloadArchive();
            SetLifecycleState(CfsUiLifecycleState.Unmounted);
            UpdateMountControls();
        }
        catch (Exception ex)
        {
            SetLifecycleState(CfsUiLifecycleState.CleanupFailed, session.FolderPath);
            CfsDiagnostics.Logger.WriteException("mount.cleanup.ui", ex);
            throw new CfsArchiveException($"Saving or cleanup failed. The mounted folder was preserved at '{session.FolderPath}'. Close open files or Explorer windows, then retry Unmount. The exact path is also recorded in the log.", ex);
        }
    }

    private bool CloseMountedSession(bool promptToSave)
    {
        if (_mountedSession is null)
        {
            return true;
        }

        if (promptToSave)
        {
            var result = MessageBox.Show(
                this,
                "Save mounted folder changes back to the .cfs before permanently unmounting? Select No to keep the folder mounted.",
                "Unmount CFS",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);

            if (result != DialogResult.Yes)
            {
                return false;
            }
        }

        var session = _mountedSession;
        SetLifecycleState(CfsUiLifecycleState.Saving);
        try
        {
            session.Save(RequireArchive());
            ReloadArchive();
            session.PermanentlyDelete();
            _mountedSession = null;
            SetLifecycleState(CfsUiLifecycleState.Unmounted);
            UpdateMountControls();
            return true;
        }
        catch (Exception ex)
        {
            SetLifecycleState(CfsUiLifecycleState.CleanupFailed, session.FolderPath);
            CfsDiagnostics.Logger.WriteException("mount.cleanup.ui", ex);
            throw new CfsArchiveException($"Saving or cleanup failed. The mounted folder was preserved at '{session.FolderPath}'. Close open files or Explorer windows and retry.", ex);
        }
    }

    private void RefreshTree()
    {
        _tree.Nodes.Clear();
        var root = _tree.Nodes.Add(string.Empty, Path.GetFileNameWithoutExtension(_archive?.ArchivePath) ?? "CFS");
        root.Tag = string.Empty;

        foreach (var directory in RequireArchive().ListEntries().Where(e => e.Type == ArchiveEntryType.Directory).Select(e => e.Path))
        {
            var parent = root;
            var current = string.Empty;
            foreach (var part in directory.Split('/'))
            {
                current = CombineArchivePath(current, part);
                var next = parent.Nodes.Cast<TreeNode>().FirstOrDefault(node => string.Equals((string?)node.Tag, current, StringComparison.OrdinalIgnoreCase));
                if (next is null)
                {
                    next = parent.Nodes.Add(current, part);
                    next.Tag = current;
                }

                parent = next;
            }
        }

        root.Expand();
        _tree.SelectedNode = root;
        _folderLabel.Text = "Folder: /";
        UpdateMountControls();
    }

    private void RefreshList()
    {
        _currentFolder = (_tree.SelectedNode?.Tag as string) ?? string.Empty;
        _folderLabel.Text = "Folder: /" + _currentFolder;
        _upButton.Enabled = _archive is not null && !string.IsNullOrWhiteSpace(_currentFolder);
        _list.Items.Clear();

        if (_archive is null)
        {
            return;
        }

        var entries = _archive.ListEntries()
            .Where(IsImmediateChild)
            .OrderBy(entry => entry.Type)
            .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var name = entry.Path.Split('/').Last();
            var item = new ListViewItem(name)
            {
                Tag = entry
            };
            item.SubItems.Add(entry.Type == ArchiveEntryType.Directory ? "Folder" : "File");
            item.SubItems.Add(entry.Type == ArchiveEntryType.File ? entry.OriginalSize.ToString("N0") : string.Empty);
            item.SubItems.Add(entry.Type == ArchiveEntryType.File ? entry.LastWriteTimeUtc.LocalDateTime.ToString("g") : string.Empty);
            _list.Items.Add(item);
        }
    }

    private bool IsImmediateChild(CfsEntry entry)
    {
        var parent = ParentPath(entry.Path);
        return string.Equals(parent, _currentFolder, StringComparison.OrdinalIgnoreCase);
    }

    private CfsEntry? GetSelectedEntry()
    {
        return _list.SelectedItems.Count == 0 ? null : _list.SelectedItems[0].Tag as CfsEntry;
    }

    private void ReloadArchive()
    {
        if (_archive is null)
        {
            return;
        }

        var archivePath = _archive.ArchivePath;
        var selectedFolder = _currentFolder;
        _archive = CfsArchive.Load(archivePath);
        RefreshTree();
        SelectFolderIfExists(selectedFolder);
        RefreshList();
    }

    private void SelectFolderIfExists(string folder)
    {
        _ = SelectFolderCore(folder);
    }

    private void SelectFolder(string folder)
    {
        if (SelectFolderCore(folder))
        {
            return;
        }

        throw new DirectoryNotFoundException(folder.Length == 0 ? "/" : folder);
    }

    private bool SelectFolderCore(string folder)
    {
        foreach (TreeNode node in FlattenNodes(_tree.Nodes))
        {
            if (string.Equals(node.Tag as string, folder, StringComparison.OrdinalIgnoreCase))
            {
                _tree.SelectedNode = node;
                node.EnsureVisible();
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<TreeNode> FlattenNodes(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            yield return node;
            foreach (var child in FlattenNodes(node.Nodes))
            {
                yield return child;
            }
        }
    }

    private CfsArchive RequireArchive()
    {
        return _archive ?? throw new CfsArchiveException("Open a CFS archive first.");
    }

    private async Task<T> RunOperationAsync<T>(string title, Func<IProgress<CfsProgress>, CancellationToken, T> operation)
    {
        if (_operationActive)
        {
            throw new CfsArchiveException("Another archive operation is already running.");
        }

        _operationActive = true;
        _operationCancellation = new CancellationTokenSource();
        var progressPanel = Controls.Find("progressPanel", true).OfType<Control>().First();
        progressPanel.Visible = true;
        _progressTitle.Text = title;
        _progressPhase.Text = "Starting";
        _progressPath.Text = string.Empty;
        _progressCounters.Text = string.Empty;
        _progressPercent.Text = string.Empty;
        _progressBar.Style = ProgressBarStyle.Marquee;
        _cancelOperationButton.Visible = true;
        _cancelOperationButton.Enabled = true;
        SetOperationControlsEnabled(false);

        var progress = new Progress<CfsProgress>(UpdateProgress);
        try
        {
            return await Task.Run(() => operation(progress, _operationCancellation.Token));
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            _operationActive = false;
            _cancelOperationButton.Visible = false;
            progressPanel.Visible = false;
            SetOperationControlsEnabled(true);
        }
    }

    private void UpdateProgress(CfsProgress progress)
    {
        _progressTitle.Text = progress.Operation;
        _progressPhase.Text = progress.Phase;
        _progressPath.Text = progress.CurrentPath ?? string.Empty;
        var items = progress.TotalItems is null ? $"{progress.CompletedItems:N0} items" : $"{progress.CompletedItems:N0} of {progress.TotalItems:N0} items";
        var bytes = progress.TotalBytes is null ? string.Empty : $" | {FormatBytes(progress.CompletedBytes)} of {FormatBytes(progress.TotalBytes.Value)}";
        _progressCounters.Text = items + bytes;
        if (progress.TotalItems is > 0 || progress.TotalBytes is > 0)
        {
            var itemFraction = progress.TotalItems is > 0 ? (double)progress.CompletedItems / progress.TotalItems.Value : 0;
            var byteFraction = progress.TotalBytes is > 0 ? (double)progress.CompletedBytes / progress.TotalBytes.Value : itemFraction;
            var fraction = Math.Clamp(Math.Max(itemFraction, byteFraction), 0, 1);
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Value = (int)Math.Round(fraction * 100);
            _progressPercent.Text = $"{_progressBar.Value}%";
        }
        else
        {
            _progressBar.Style = ProgressBarStyle.Marquee;
            _progressPercent.Text = string.Empty;
        }
    }

    private void SetOperationControlsEnabled(bool enabled)
    {
        foreach (var button in Controls.OfType<Control>().SelectMany(AllControls).OfType<Button>().Where(button => button != _cancelOperationButton))
        {
            button.Enabled = enabled;
        }

        if (enabled)
        {
            SetArchiveControlsEnabled(_archive is not null);
        }
    }

    private static IEnumerable<Control> AllControls(Control control)
    {
        yield return control;
        foreach (Control child in control.Controls)
        {
            foreach (var descendant in AllControls(child)) yield return descendant;
        }
    }

    private static string FormatBytes(long value) => value switch
    {
        >= 1024L * 1024 * 1024 => $"{value / 1024d / 1024 / 1024:F2} GB",
        >= 1024L * 1024 => $"{value / 1024d / 1024:F2} MB",
        >= 1024 => $"{value / 1024d:F1} KB",
        _ => $"{value:N0} B"
    };

    private void TryRun(Action action, string successMessage)
    {
        try
        {
            action();
            SetStatus(successMessage);
        }
        catch (Exception ex)
        {
            AppLog.WriteException("ui.operation", ex);
            SetStatus("Error: " + ex.Message);
            MessageBox.Show(this, ex.Message, "CFS Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task TryRunAsync(Func<Task> action, string successMessage)
    {
        try
        {
            await action();
            SetStatus(successMessage);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Operation cancelled");
        }
        catch (Exception ex)
        {
            AppLog.WriteException("ui.operation", ex);
            SetStatus("Error: " + ex.Message);
            MessageBox.Show(this, ex.Message, "CFS Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetStatus(string message)
    {
        _statusLabel.Text = message;
    }

    private void SetArchiveControlsEnabled(bool enabled)
    {
        foreach (var button in new[] { _closeButton, _validateButton, _openExplorerButton, _compatibilityModeButton, _upButton, _newFileButton, _importButton, _overwriteButton, _newFolderButton, _renameButton, _deleteButton, _extractButton })
        {
            button.Enabled = enabled;
        }

        _upButton.Enabled = enabled && !string.IsNullOrWhiteSpace(_currentFolder);
        UpdateMountControls();
    }

    private void UpdateMountControls()
    {
        var mounted = _mountedSession is not null;
        _saveMountedButton.Enabled = mounted;
        _unmountButton.Enabled = mounted;
        _mountLabel.Text = _uiState.DisplayText;
    }

    private void SetLifecycleState(CfsUiLifecycleState state, string? mountPath = null)
    {
        _uiState.Set(state, mountPath);
        _mountLabel.Text = _uiState.DisplayText;
        _statusLabel.Text = _uiState.DisplayText;
    }

    private static string? Prompt(string title, string defaultValue)
    {
        using var form = new Form
        {
            Text = title,
            Width = 420,
            Height = 140,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false
        };
        var input = new TextBox { Left = 12, Top = 12, Width = 380, Text = defaultValue };
        var ok = new Button { Text = "OK", Left = 236, Top = 48, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 317, Top = 48, Width = 75, DialogResult = DialogResult.Cancel };
        form.Controls.AddRange([input, ok, cancel]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(input.Text)
            ? input.Text.Trim()
            : null;
    }

    private static string? PromptMultiline(string title, string defaultValue)
    {
        using var form = new Form
        {
            Text = title,
            Width = 560,
            Height = 360,
            FormBorderStyle = FormBorderStyle.Sizable,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = true
        };
        var input = new TextBox
        {
            Left = 12,
            Top = 12,
            Width = 520,
            Height = 260,
            Text = defaultValue,
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            AcceptsReturn = true,
            AcceptsTab = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };
        var ok = new Button { Text = "OK", Left = 376, Top = 282, Width = 75, DialogResult = DialogResult.OK, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        var cancel = new Button { Text = "Cancel", Left = 457, Top = 282, Width = 75, DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        form.Controls.AddRange([input, ok, cancel]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog() == DialogResult.OK ? input.Text : null;
    }

    private static string BuildOpenCommand()
    {
        var brokerPath = Path.Combine(AppContext.BaseDirectory, CfsShellRegistration.BrokerExecutableName);
        if (!File.Exists(brokerPath))
            throw new FileNotFoundException("Cfs.Broker.exe is not installed beside Cfs.App. Repair the CFS installation before registering .cfs files.", brokerPath);
        return CfsShellRegistration.BuildOpenCommand(brokerPath);
    }

    private static string CombineArchivePath(string folder, string name)
    {
        return string.IsNullOrWhiteSpace(folder) ? CfsArchive.NormalizeEntryPath(name) : CfsArchive.NormalizeEntryPath(folder + "/" + name);
    }

    private static string ParentPath(string path)
    {
        var normalized = CfsArchive.NormalizeEntryPath(path);
        var index = normalized.LastIndexOf('/');
        return index < 0 ? string.Empty : normalized[..index];
    }
}
