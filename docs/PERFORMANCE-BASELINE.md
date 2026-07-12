# CFS 0.1.0 Beta performance baseline

## Purpose

This baseline detects gross regressions in the limited beta workflow. It is not a performance guarantee for other computers, archives, storage devices, antivirus configurations, or Windows applications.

## Measured environment

- Timestamp: 2026-07-12 UTC
- Windows: Microsoft Windows NT 10.0.26200.0
- Logical processor count exposed to the harness: 4
- Build: CFS 0.1.0 Beta (`0.1.0-Beta-2b9a4eddb7694b0c9bce65c1464f9131`)
- Workload: 1,000 independent 256-byte files plus three independent 8 MiB binary files

## Results

| Stage | Measured time |
| --- | ---: |
| Create archive containing 1,000 small files and three large files | 5.049 s |
| Load manifest metadata only | 0.005 s |
| Project namespace and reach ProjFS mount readiness | 0.186 s |
| Hydrate one requested file with unrelated entries untouched | 0.012 s |
| Concurrent nonzero-offset reads from two large files | 0.093 s |
| Create edit-session ProjFS mount | 0.179 s |
| Save overwrite, create, rename, and delete workload | 3.652 s |
| Unmount and remove temporary mount | 0.348 s |
| Validate edited archive | 0.108 s |
| Reopen and verify persisted edits | 0.113 s |

Peak process working set was 385,101,824 bytes (approximately 367 MiB). The save retained the unchanged test file's compressed block at offset `25170030`. The harness confirmed isolated single-file hydration, exact ranged/concurrent reads, validation, reopen persistence, and workspace cleanup.

The machine-readable result is generated as `dist/CFS-0.1.0-Beta-performance.json` and is release evidence, not a required runtime dependency.

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
& "$env:USERPROFILE\scoop\apps\dotnet-sdk\current\dotnet.exe" build .\tests\Cfs.Performance\Cfs.Performance.csproj -c Release
& "C:\Program Files\dotnet\dotnet.exe" .\tests\Cfs.Performance\bin\Release\net8.0\Cfs.Performance.dll .\dist\CFS-0.1.0-Beta-performance.json
```
