# CFS 0.2 Recovery Verification Blocker

Status: **Partially resolved — the supplied fix package documents a successful clean-session probe, but live verification of this checkout is still incomplete.**

## Observed failure

The real recovery probe reached broker startup but failed before a ProjFS mount was created:

```text
System.UnauthorizedAccessException: Access to the path
'C:\Users\Krishna\AppData\Local\CFS\Sessions\9ef265547812b1a1eac51b7454d6b993' is denied
at CfsMountSession.Create (line 52)
```

Because the session could not be created, the probe produced no valid reopen result and could not compare archive hashes after a broker kill.

## Environment evidence

- The user confirmed `Client-ProjFS` is enabled.
- A separately launched elevated PowerShell reported `Client-ProjFS: Enabled`.
- The Codex process executing repository commands repeatedly reported:

  ```text
  IsElevated=False
  ```

- The same Codex process received `The requested operation requires elevation` when querying the Windows optional feature.
- A scoped elevated write probe from a normal PowerShell succeeded against `%LOCALAPPDATA%\CFS\Sessions`, so the denial is specific to the Codex execution environment rather than evidence that the product path is unwritable for an elevated user.

## Verification that did pass

The deterministic production/recovery suites passed:

| Suite | Result |
| --- | --- |
| Broker | 11/11 |
| Creation | 10/10 |
| Persistence | 8/8 |
| Close | 8/8 |
| Recovery | 7/7 |

The session-root residue audit after cleanup reported:

```text
SESSION_ENTRIES=0
CFS_PROCESSES=0
```

## Elevation attempts

The Codex desktop app was closed and relaunched from an elevated PowerShell, but this task continued to report `IsElevated=False`, indicating that the task remained attached to an unelevated Codex process. An elevated PowerShell by itself does not elevate an already-running Codex task.

## Reproduction and workaround

The repository now contains [`tools/Run-ElevatedRecoveryProbe.ps1`](../tools/Run-ElevatedRecoveryProbe.ps1). It is designed to be run from an actually elevated PowerShell and writes machine-readable evidence to `elevated-recovery-result.json` in the repository root. It creates an isolated archive, opens it through the broker, writes a file, kills only the expected broker PID, reopens the archive, checks recovery and SHA-256 stability, then closes and cleans up.

Run from an elevated PowerShell in the repository root:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\tools\Run-ElevatedRecoveryProbe.ps1
```

Do not mark CFS 0.2 complete until that probe returns success and its result file confirms `recoveredFile: true`, matching archive hashes, and `cleanup: true`.

## Supplied fix package (2026-07-16)

`CFS-Recovery-Probe-Fix.zip` reports two Windows PowerShell 5.1 harness fixes:

- pin `DOTNET_ROOT`/`PATH` to `C:\Program Files\dotnet` for the .NET 8 broker;
- rename the probe’s `$pid` variable to `$brokerPid` because PowerShell variable names are case-insensitive and `$PID` is read-only.

The package includes a redacted result with matching archive hashes, `recoveredFile: true`, and `cleanup: true`. Its recorded session has `isDirty=false`, `dirtyGeneration=0`, and `committedGeneration=0`; therefore it demonstrates clean-session broker restart and archive integrity, but not recovery from a crash during an uncommitted write. The redacted artifact is supporting information, not a substitute for a live result from this checkout.

## Focused dirty-write probe evidence (2026-07-16)

The live elevated run reached the required dirty state and force-killed the active broker:

```text
elevated = true
projfs = 2
isDirty = true
dirtyGeneration = 2
committedGeneration = 0
transactionState = 1
killedBrokerPid = 10948
recoveryErrorCode = recovery-needed (legacy pre-0.3 code; current protocol returns CFS_E_RECOVERY_REQUIRED)
baselineArchiveSha256 = B32A0303E95A2966370CD46559F54A2C61AD4E7BFC19698CDC26AC3C2D867E76
archiveSha256AfterReopen = B32A0303E95A2966370CD46559F54A2C61AD4E7BFC19698CDC26AC3C2D867E76
lastValidArchivePreserved = true
```

The recovery response explicitly reported that the valid prior archive and marked session were preserved. The initial run left that marked mount/sidecar by design; those verified probe artifacts were subsequently removed and the session-root audit returned `SESSION_ENTRIES=0 CFS_PROCESSES=0`. The focused probe now records `recoveryDataPreservedBeforeCleanup` and performs exact probe-owned cleanup before writing its final result.

## Scope and interpretation

This report preserves the historical 0.2 verification record. The elevated dirty broker-kill/reopen workflow subsequently passed, and the current 0.3 automated recovery suite passes **9/9**, including ownership-verified inspection and explicit discard. The remaining release blockers are the clean Windows VM acceptance matrix and production Authenticode signing, not local ProjFS recovery detection.
