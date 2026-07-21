# CFS 0.3.1 Beta release notes

## Explorer-first archive creation

CFS archives can now be created without opening or depending on `Cfs.App`.

- Right-click empty space in a folder and choose **Show more options → Create empty CFS archive here**.
- Right-click a folder and choose **Create empty CFS archive inside**.
- Choose the archive name in the native Windows save dialog; CFS creates a structurally valid archive and opens it in Explorer.
- **New → CFS Compressed Folder** remains available through the validated ShellNew template.
- **Compress to CFS** remains available for creating an archive from an existing folder.

The Explorer commands launch the small command client, which sends a versioned
request to the broker. Archive code does not load into `explorer.exe`, and the
management application remains optional for diagnostics and settings.

## Safety and compatibility

- CFS refuses to replace an existing file or folder during empty-archive creation.
- The archive is produced through the broker's production `create-empty` path and
  is opened through the normal broker/ProjFS workflow.
- Installer and portable registration use the same exact quoted commands.
- Uninstall removes the new Explorer verbs only while they still match this CFS
  installation.

CFS 0.3.1 Beta remains experimental Windows software. Keep an independently
accessible backup of important files and use the beta with non-critical data.
