# CFS 0.1.0 Beta release notes

## Beta status and warning

This is a limited experimental beta, not a production release. Keep separate backups and use only non-critical test files. The application displays and version-keys acknowledgement of this warning.

## Major features

- Create and open single-file `.cfs` archives.
- Browse manifests and mounted folders.
- Read, create, overwrite, rename, move, and delete files.
- Create folders and delete empty folders.
- Save, unmount, reopen, and validate archives.
- Version/build diagnostics, lifecycle logging, Open Logs Folder, and Report Bug actions.

## ProjFS behavior

The default **Open in Explorer** workflow uses Windows Projected File System. It projects metadata without extracting every payload and hydrates the requested archive entry when Windows requests file data. ProjFS availability is checked first; CFS reports remediation and never silently falls back.

**Compatibility Mode (Full Extraction)** is a separate, explicit user choice and is clearly not on-demand mounting.

## Archive and compression behavior

Files use independent per-file LZMA2 blocks. Changed and new blocks plus a new manifest are appended during normal saves. Unchanged compressed blocks retain their offsets and are reused. Save failures are designed to leave the previous archive current.

## Supported edit operations

Supported beta edits include file creation, overwrite, rename, move, and delete; folder creation; and empty-folder deletion. Saving and reopening verifies persistence in the normal workflow.

## Progress and cancellation

Archive creation, opening, extraction, saving, validation, and cleanup report real progress where available. Long-running supported operations expose cancellation; cancellation and failed-save paths preserve existing archives.

## Known limitations

Windows x64 and ProjFS prerequisites apply. Universal application compatibility is not claimed. Archives can grow without compaction. The beta has no encryption, deduplication, history, cloud sync, permissions preservation, or multi-user support. See [KNOWN-LIMITATIONS.md](KNOWN-LIMITATIONS.md).

## Reporting

Use **Open Logs Folder**, **Report Bug**, and [BUG-REPORT-TEMPLATE.md](BUG-REPORT-TEMPLATE.md). Reports go to the public CFS issue tracker. Review logs and screenshots for private information before sharing.

This build adds a machine-wide Windows setup program and an opt-in update checker. Update downloads must use HTTPS, pass the release manifest's SHA-256 verification, and receive explicit approval before installation.

Contributor recognition follows [CONTRIBUTOR-ACCESS-POLICY.md](CONTRIBUTOR-ACCESS-POLICY.md) and requires manual written confirmation by the project owner.
