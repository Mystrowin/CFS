# CFS 0.3.1 Beta installation and uninstall

## Install

1. Download `CFS-0.3.1-Beta-Setup.exe` from the [official release](https://github.com/Mystrowin/CFS/releases/tag/v0.3.1-beta).
2. Compare its SHA-256 value with the checksum published on the release page and website.
3. Run setup, approve the Windows administrator prompt, and follow the wizard.
4. Allow setup to enable **Windows Projected File System** when required. Restart Windows if requested.

Setup installs CFS under `Program Files\CFS`, registers the Explorer workflow, creates uninstall information, and installs a validated template for **New → CFS Compressed Folder**.

The beta installer is unsigned, so Microsoft SmartScreen may display a warning. A valid checksum proves that the file matches the published asset; it does not provide Authenticode publisher verification.

## ProjFS prerequisite

CFS Explorer mounting requires Windows Projected File System. If it is unavailable, open **Turn Windows features on or off**, enable **Windows Projected File System**, and restart if Windows requests it.

## Uninstall

1. Close editors and Explorer windows using a mounted CFS workspace.
2. Use **Close CFS** and resolve any pending commit or recovery state.
3. Open Windows **Installed apps**, select CFS, and choose **Uninstall**.

The uninstaller removes CFS-owned integration. It does not delete `.cfs` archives or recovery data under `%LOCALAPPDATA%\CFS`.

If another application previously handled `.cfs` files, select that application again through Windows default-app settings. CFS does not bypass Windows user-choice protections.
