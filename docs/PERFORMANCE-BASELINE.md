# CFS 0.2.0 Beta performance baseline

## Purpose

This baseline detects gross regressions in the limited beta workflow. It is not a performance guarantee for other computers, archives, storage devices, antivirus configurations, or Windows applications.

## Measured environment

- Timestamp: 2026-07-16 UTC
- Windows: Microsoft Windows NT 10.0.26200.0
- Logical processor count exposed to the harness: 4
- Build: CFS 0.2.0 Beta (`0.2.0-Beta-cb57dca0be944fb2a8c6a95b5bd38188`)
- Workload: 1,000 independent 256-byte files plus three independent 8 MiB binary files

## Results

| Stage | Measured time |
| --- | ---: |
| Create archive containing 1,000 small files and three large files | 10.944 s |
| Load manifest metadata only | 0.004 s |
| Project namespace and reach ProjFS mount readiness | 0.123 s |
| Hydrate one requested file with unrelated entries untouched | 0.029 s |
| Concurrent nonzero-offset reads from two large files | 0.285 s |
| Create edit-session ProjFS mount | 0.308 s |
| Save overwrite, create, rename, and delete workload | 10.453 s |
| Unmount and remove temporary mount | 0.994 s |
| Validate edited archive | 0.195 s |
| Reopen and verify persisted edits | 0.203 s |

Peak process working set was 311,005,184 bytes (approximately 297 MiB). The save retained the unchanged test file's compressed block at offset `25170030`. The harness confirmed isolated single-file hydration, exact ranged/concurrent reads, validation, reopen persistence, and workspace cleanup.

The machine-readable result is generated as `dist/CFS-0.2.0-Beta-performance.json` and is release evidence, not a required runtime dependency.

## Regression ceilings

The harness uses intentionally broad ceilings: 180 seconds for creation, 10 seconds for metadata load, 30 seconds for mount stages and single hydration, 60 seconds for concurrent ranged reads, 240 seconds for save/validation/reopen stages, 30 seconds for cleanup, and 2 GiB peak working set. These thresholds catch gross regressions without treating normal shared-machine variation as a release failure.

## Limitations

- Results describe one x64 Windows beta host and one deterministic data distribution.
- Small-file contents and large binaries are synthetic and may compress differently from user data.
- Windows caching, storage speed, security software, previews, and indexing can materially change timings and hydration patterns.
- ProjFS chooses callback recall ranges; an application subrange read may cause Windows to request a broader hydration range for that file.
- The workload proves unrelated archive entries remain untouched during isolated hydration, but save materialization can intentionally read projected entries to build a stable snapshot.
- No universal application-performance or compatibility claim is made.

Run the Release harness from the repository root:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build .\tests\Cfs.Performance\Cfs.Performance.csproj -c Release
& "C:\Program Files\dotnet\dotnet.exe" .\tests\Cfs.Performance\bin\Release\net8.0\Cfs.Performance.dll .\dist\CFS-0.2.0-Beta-performance.json
```
