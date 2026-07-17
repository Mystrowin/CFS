# CFS 0.2.0 Beta

CFS is experimental Windows beta software for testing compressed `.cfs` archives through a ProjFS-backed Explorer workflow.

> Do not use CFS as the only copy of important or irreplaceable files. Keep separate backups. Use this beta only with non-critical test data.

CFS 0.2.0 Beta is not production-ready and does not claim compatibility with every Windows application.

## Start here

- [Beta quick start](docs/BETA-QUICK-START.md)
- [Installation and uninstall](docs/INSTALL-UNINSTALL.md)
- [Data-safety guidance](docs/DATA-SAFETY.md)
- [Known limitations](docs/KNOWN-LIMITATIONS.md)
- [Bug-report template](docs/BUG-REPORT-TEMPLATE.md)
- [Contributor access policy](docs/CONTRIBUTOR-ACCESS-POLICY.md)
- [Release notes](docs/RELEASE-NOTES-0.2.0-BETA.md)
- [ProjFS behavior](docs/on-demand-mount.md)
- [Compression and archive behavior](docs/compression.md)
- [Measured performance baseline](docs/PERFORMANCE-BASELINE.md)

Report reproducible bugs through the public CFS issue tracker at `https://github.com/Mystrowin/CFS/issues`. Installed copies check the repository's signed-by-checksum release manifest for updates and always require confirmation before downloading or installing anything.

## Developer verification

```powershell
& "$env:USERPROFILE\scoop\apps\dotnet-sdk\current\dotnet.exe" build .\tests\Cfs.Core.Tests\Cfs.Core.Tests.csproj
& "C:\Program Files\dotnet\dotnet.exe" .\tests\Cfs.Core.Tests\bin\Debug\net8.0\Cfs.Core.Tests.dll
```

The release workflow and package are produced by repository scripts under `tools`. End users should follow the packaged [quick start](docs/BETA-QUICK-START.md), not developer build instructions.
