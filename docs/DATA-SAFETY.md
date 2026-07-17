# CFS 0.2.0 Beta data-safety guidance

## Backups and non-critical files

Keep a separate, independently accessible backup of every file placed in a `.cfs`. Use beta archives only for non-critical test files. CFS 0.2.0 Beta is not production-ready and must not be the sole storage location.

## Validate an archive

Use **Validate** after creating an archive, after significant edits, and before deleting any external test copy. Validation checks the current manifest and file payload integrity. A successful result does not prove that an editor saved the content you intended, so reopen and inspect important test files too.

## After a failed save

1. Do not delete or replace the `.cfs` or mounted folder.
2. Read the exact error and keep the application open when practical.
3. Close editors or Explorer windows that may hold files open.
4. Select **Open Logs Folder** and preserve the diagnostic log.
5. Retry the broker commit or use **Close CFS** once the likely cause is removed.
6. Validate and reopen the archive before trusting the retry.

CFS is designed to leave the previous readable archive current when saving fails, but beta users must still retain an independent backup.

## Preserved mount folders after cleanup failure

If unmount cleanup fails, the UI reports the exact preserved path, normally below `%TEMP%\CFS\mounts`. Do not delete an unrelated folder. Close applications using that exact path, retry **Unmount**, and retain the folder until saved changes have been verified in the reopened archive. Include the exact path in a private bug report when useful; remove personal path portions from public screenshots.

## Privacy-safe log sharing

Logs use identifiers for archive paths and do not intentionally record file contents, passwords, access tokens, or unrelated private information. Review every log before sharing it. Redact usernames, preserved mount paths, organization names, or other personal details that may appear in operating-system exception text. Never attach the archive itself unless you have reviewed its contents and the project owner explicitly requests it through a trusted channel.

Use [BUG-REPORT-TEMPLATE.md](BUG-REPORT-TEMPLATE.md) and note whether any file was lost or corrupted.
