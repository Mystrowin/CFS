# CFS 0.2.0 Beta release notes

## Beta status and safety

CFS 0.2.0 Beta remains experimental Windows software. Keep an independently
accessible backup of every important file and use `.cfs` archives only with
non-critical test data. This beta does not claim universal Windows-application
compatibility or production readiness.

## Silent broker and Explorer workflow

- `.cfs` file opens are handled by the windowless per-user `Cfs.Broker.exe`,
  not by the visible CFS management application or a console handler.
- A broker canonicalizes archive paths and reuses one live ProjFS session and
  mount folder for concurrent opens of the same archive.
- Explorer integration provides **New → CFS Compressed Folder**, **Compress to
  CFS** for folders, and **Close CFS** for an active archive. The ShellNew
  template is a generated, validated empty CFS1 version-1 archive.

## Persistence and recovery

- Broker-owned sessions detect normal Explorer file changes and commit them
  after a bounded quiet period. Commits validate the archive and reuse
  unchanged compressed blocks when possible.
- **Close CFS** flushes pending changes, validates the archive, stops the
  target provider, and removes only a CFS-owned mount. A failed flush or mount
  cleanup leaves recoverable data in place instead of reporting a false clean
  close.
- Hidden CFS-owned recovery metadata records interrupted sessions. On the next
  open, CFS validates the last archive before cleanup or recovery and reports
  an actionable recovery-needed result when state is ambiguous.

## Compatibility

CFS 0.2.0 Beta continues to open CFS1 format-version-1 archives, including
archives produced by the 0.1 beta. It does not rewrite an archive merely to
open it through the broker.

## Known limitations

- Windows x64 with Client-ProjFS is required for the on-demand Explorer path.
- The broker canonicalizes by normalized path, not file ID; hard-link identity
  is not resolved in this beta.
- There is no encryption, password support, deduplication, compaction, cloud
  synchronization, version history, or multi-user coordination.
- Recovery is intentionally conservative: an ambiguous or invalid candidate
  is preserved for inspection rather than overwriting the last valid archive.

## Reporting

Use the packaged registration script and diagnostics described in the project
documentation. Before sharing logs or screenshots, remove private paths and
archive contents. The bug-report destination remains the configured project
issue tracker; no production-safety claim is implied.
