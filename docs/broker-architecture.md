# CFS 0.2.0 Beta broker architecture

## Lifetime and ownership

`Cfs.Broker.exe` is a `WinExe` with no form and no console. The first invocation for a Windows user acquires the user's versioned global mutex and becomes the long-lived broker. Later invocations contact that broker through the user's versioned named pipe, forward one request, receive a bounded response, and exit. Normal `.cfs` shell open uses `Cfs.Broker.exe open "%1"`; `Cfs.App.exe` remains the explicit management/settings application.

The broker owns the existing `CfsMountSession` and `CfsProjFsMount`. Controlled test shutdown stops the provider and deletes only mount folders whose existing CFS-owned marker passes the established safety check. Production session-close and persistence policy are deliberately deferred.

## Names, protocol, and startup races

The mutex is `Global\CFS.Broker.v1.<user-hash>` and the pipe is `CFS.Broker.v1.<user-hash>`. The hash is derived from the Windows user SID; test-only instance suffixes isolate integration runs. Pipe access is restricted to the current user. Protocol version 1 uses a four-byte little-endian length followed by JSON, with a 64 KiB maximum. Requests support `open`, `status`/`query`, and controlled test-only `shutdown`. Reads, handling, and response writes have timeouts, so a stalled authenticated client cannot monopolize the only server instance.

The mutex winner starts the pipe listener before handling its initial request. A racing client retries pipe connection for up to ten seconds. Failure returns a stable error code and an actionable message; it does not start another provider or silently extract the archive.

## Canonical archive identity and mount root

Identity is centralized as: trim surrounding/trailing separators, make absolute relative to the caller's working directory, call `Path.GetFullPath`, normalize to Windows separators, require an existing `.cfs` file, and use an `OrdinalIgnoreCase` registry key. The deterministic mount path is `%LOCALAPPDATA%\CFS\Sessions\<first-32-hex-of-SHA256(uppercase-canonical-path)>`. A pre-existing mount target is rejected by the existing session safety rules; arbitrary existing folders are never reused.

This milestone intentionally does not resolve NTFS hard links, file IDs, junction aliases, or other cases where different canonical path strings identify the same underlying file.

## Explorer creation commands

Milestone 2 adds bounded `create-empty` and `compress` IPC commands without launching the management app. Explorer `ShellNew` copies the reproducibly generated `%ProgramFiles%\CFS\ShellNew\CFS-Empty.cfs` template and displays the ProgID label **CFS Compressed Folder**. The folder verb runs the exact command `"Cfs.Broker.exe" compress "%1"`.

Compression validates and iteratively enumerates the real source tree without following reparse points. It writes in a marked, hidden, not-content-indexed same-volume CFS work folder, atomically moves the finished archive beside the source, and chooses `Folder.cfs`, `Folder (2).cfs`, `Folder (3).cfs`, and so on without overwriting. Progress is a small best-effort surface shown only after 750 ms; progress failures never change a successful archive result.

## Metadata-only open

The broker calls the path-based `CfsMountSession.Create` overload. That overload reaches `CfsProjFsMount.Create`, which reads `CfsArchive.LoadManifestEntries`; it does not call `CfsArchive.Load` and does not hydrate payloads during normal open. File payloads remain on-demand through the established ProjFS callback. The archive remains CFS1 format version 1 and is not rewritten by broker open.
