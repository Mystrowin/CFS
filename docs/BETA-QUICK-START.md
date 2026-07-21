# CFS 0.3.1 Beta quick start

## Safety warning

CFS 0.3.1 is the current stable beta. Keep an independent backup of important files while evaluating archive workflows.

## Supported Windows requirements

- Windows 11 x64.
- Windows Projected File System (`Client-ProjFS`) enabled for the default on-demand workflow.
- Permission to run the extracted application and create temporary folders under the current user's temporary directory.

If ProjFS is unavailable, CFS explains how to enable **Windows Projected File System** in Windows Features. It never silently falls back. **Compatibility Mode (Full Extraction)** is a separate, explicitly selected action.

## Install

1. Download `CFS-0.3.1-Beta-Setup.exe` from the public GitHub release.
2. Run setup and approve the Windows administrator prompt.
3. Leave **Windows Projected File System** selected when setup offers to enable it.

See [Installation and uninstall](INSTALL-UNINSTALL.md) for file association and removal.

## Create or open an archive

- Right-click empty space in an Explorer folder, choose **Show more options → Create empty CFS archive here**, name the archive, and CFS opens it in Explorer.
- Right-click a folder and choose **Create empty CFS archive inside** to create an empty archive in that folder.
- Explorer **New → CFS Compressed Folder** remains available for the normal Windows New-file workflow.
- Right-click a populated folder and choose **Compress to CFS** to turn that folder into an archive.
- Double-clicking an existing `.cfs` invokes the silent per-user broker and opens/reuses its Explorer folder.

None of these workflows launches or requires `Cfs.App`.

## Mount and browse

1. Double-click an archive or use its registered open command.
2. Browse the broker-provided Explorer folder. Directory metadata is projected without extracting every payload; reading a file hydrates its archive entry.

If you explicitly select **Compatibility Mode (Full Extraction)** and confirm its warning, CFS extracts every file to a temporary folder. This mode is not on-demand ProjFS.

## Edit and save

The beta supports creating, overwriting, renaming, moving, and deleting files; creating folders; and deleting empty folders. Application compatibility is not universal, so test the editor you intend to use.

The broker commits normal Explorer changes after a bounded quiet period. Right-click the archive and choose **Close CFS** to commit or discard pending changes and safely unmount it.

## Unmount and reopen

Use **Close CFS**, choose whether to commit or discard pending changes, and wait for cleanup. Reopen the same `.cfs` and verify the intended changes. If cleanup fails, CFS preserves and reports the exact mount path; follow [Data-safety guidance](DATA-SAFETY.md).

## Logs and bug reports

- Logs are under `%LOCALAPPDATA%\CFS\Logs`.
- Report bugs through the public CFS issue tracker. Remove private paths and personal information from logs before posting them.
- `Cfs.App` remains optional for diagnostics and settings; it is not part of normal archive creation or Explorer use.
- Use the packaged [bug-report template](BUG-REPORT-TEMPLATE.md).

## Uninstall

Use Windows **Installed apps** to uninstall a setup-based installation. The uninstaller preserves `.cfs` archives, logs, and acknowledgement data. Portable users should follow [Installation and uninstall](INSTALL-UNINSTALL.md).
