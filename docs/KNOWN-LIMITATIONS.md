# CFS 0.3.1 Beta known limitations

- Experimental beta: not production-ready and not suitable as the only copy of any important file.
- Windows-only, with x64 as the supported beta target.
- Default Explorer mounting requires Windows 10 version 1809 or newer, or Windows 11, and enabled `Client-ProjFS`.
- Compatibility Mode is explicit full extraction, not on-demand mounting, and can require time and disk space comparable to the uncompressed archive.
- Compatibility with every Windows application is not guaranteed. Previews, antivirus, indexing, memory mapping, and application access patterns can trigger hydration.
- Broker commits are automatic after a quiet period; a failed commit retains recoverable session data and must be resolved before **Close CFS** can report success.
- Updates append changed data and manifests. Archives can grow because compaction is not implemented.
- Compression is independent per-file LZMA2, not solid compression. A requested file must be decompressed as its own block before ranges are served.
- No encryption, password protection, deduplication, version history, cloud synchronization, permissions preservation, or multi-user coordination.
- Directory deletion is limited to empty folders through supported workflows.
- A failed cleanup can leave a preserved temporary mount that the user must close and retry safely.
- The public GitHub issue tracker is the support channel; reports are public and must be scrubbed of private information.
- The beta installer is unsigned and Windows SmartScreen may warn before installation.

See [DATA-SAFETY.md](DATA-SAFETY.md) before testing and [on-demand-mount.md](on-demand-mount.md) for ProjFS details.
