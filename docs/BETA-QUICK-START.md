# CFS 0.2.0 Beta quick start

## Safety warning

CFS is experimental beta software. Never use a `.cfs` archive as the only copy of important or irreplaceable files. Keep a separate backup of every stored file and use only non-critical test data.

## Supported Windows requirements

- x64 Windows 10 version 1809 or newer, or Windows 11.
- Windows Projected File System (`Client-ProjFS`) enabled for the default on-demand workflow.
- Permission to run the extracted application and create temporary folders under the current user's temporary directory.

If ProjFS is unavailable, CFS explains how to enable **Windows Projected File System** in Windows Features. It never silently falls back. **Compatibility Mode (Full Extraction)** is a separate, explicitly selected action.

## Install or extract

1. Obtain the versioned `CFS-0.2.0-Beta-win-x64` ZIP from the project owner.
2. Extract the entire ZIP to a normal folder; paths containing spaces are supported.
3. Keep all packaged files together, including `Cfs.Broker.exe` and `ShellNew\CFS-Empty.cfs`.

See [Installation and uninstall](INSTALL-UNINSTALL.md) for file association and removal.

## First launch

Read the beta safety warning before using the management application. The acknowledgement applies to CFS 0.2.0 Beta and is requested again after a beta-version change.

## Create or open an archive

- Use Explorer **New → CFS Compressed Folder** for an empty archive, or **Compress to CFS** on a folder.
- Double-clicking an existing `.cfs` invokes the silent per-user broker and opens/reuses its Explorer folder.

## Mount and browse

1. Double-click an archive or use its registered open command.
2. Browse the broker-provided Explorer folder. Directory metadata is projected without extracting every payload; reading a file hydrates its archive entry.

If you explicitly select **Compatibility Mode (Full Extraction)** and confirm its warning, CFS extracts every file to a temporary folder. This mode is not on-demand ProjFS.

## Edit and save

The beta supports creating, overwriting, renaming, moving, and deleting files; creating folders; and deleting empty folders. Application compatibility is not universal, so test the editor you intend to use.

The broker commits normal Explorer changes after a bounded quiet period. Use **Close CFS** to force a flush, validate, and safely unmount the one selected archive.

## Unmount and reopen

Select **Unmount**, agree to save, and wait for cleanup. Reopen the same `.cfs` and verify the intended changes. If cleanup fails, CFS preserves and reports the exact mount path; follow [Data-safety guidance](DATA-SAFETY.md).

## Validate

Select **Validate** after important test operations. A successful validation checks archive structure, decompression, sizes, and hashes, but does not replace an independent backup.

## Logs and bug reports

- Select **Open Logs Folder**. Logs are under `%LOCALAPPDATA%\CFS\Logs`.
- Select **Report Bug** to open the public CFS issue tracker. Remove private paths and personal information from logs before posting them.
- Select **Check for Updates** to check immediately. CFS also checks no more than once every 24 hours at startup, verifies the setup SHA-256, and requires confirmation before install.
- Use the packaged [bug-report template](BUG-REPORT-TEMPLATE.md).

## Uninstall

Use Windows **Installed apps** to uninstall a setup-based installation. The uninstaller preserves `.cfs` archives, logs, and acknowledgement data. Portable users should follow [Installation and uninstall](INSTALL-UNINSTALL.md).
