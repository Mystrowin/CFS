# CFS 0.3.1 Beta on-demand ProjFS mount

CFS cannot safely hydrate ordinary files in a normal temporary directory on first
open. Windows may hand an empty placeholder to an application before CFS can run.

CFS uses Windows Projected File System (ProjFS). It publishes the CFS manifest
as projected directory entries without reading file payloads. When Windows reads
a projected file, the provider reads, decompresses, and checksum-validates that
one CFS entry before returning the requested range. Repeated reads of the same
file share the in-mount hydrated cache.

Prerequisites:

- Windows 10 version 1809 or newer, or Windows 11.
- The `Client-ProjFS` Windows optional feature enabled. Enabling it requires an
  elevated administrator session and can require a restart.
- CFS will target x64 Windows first. External applications see normal paths when
  ProjFS is enabled; previews, thumbnailing, antivirus, and memory mapping can
  trigger hydration and are treated as normal read requests.

The verified development host has `Client-ProjFS` enabled and loads
`C:\\Windows\\System32\\ProjectedFSLib.dll`. The provider uses the official
Windows SDK `projectedfslib.h` ABI through managed P/Invoke; it does not create
empty placeholder files or silently extract the archive first.

Compatibility Mode is not selected automatically. **Compatibility Mode (Full
Extraction)** is a separate user action with an explicit warning and confirmation.
It creates a fully extracted temporary working folder, is not on-demand ProjFS,
and uses the same explicit save/unmount workflow. Normal Explorer mounting fails
clearly instead of silently falling back.
