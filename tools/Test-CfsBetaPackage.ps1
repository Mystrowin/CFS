param(
    [string]$PackageFolder = '',
    [string]$ZipPath = ''
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$packageName = 'CFS-0.1.0-Beta-win-x64'
if ([string]::IsNullOrWhiteSpace($PackageFolder)) { $PackageFolder = Join-Path $root "dist\$packageName" }
if ([string]::IsNullOrWhiteSpace($ZipPath)) { $ZipPath = Join-Path $root "dist\$packageName.zip" }
$folder = [IO.Path]::GetFullPath($PackageFolder)
$zipFile = [IO.Path]::GetFullPath($ZipPath)
$failures = [Collections.Generic.List[string]]::new(); $passes = 0
function Check([string]$name, [bool]$condition) { if ($condition) { $script:passes++; Write-Host "PASS $name" } else { $script:failures.Add($name); Write-Host "FAIL $name" } }

Check 'versioned package folder exists' ((Test-Path -LiteralPath $folder -PathType Container) -and (Split-Path -Leaf $folder) -eq $packageName)
Check 'versioned ZIP exists' (Test-Path -LiteralPath $zipFile -PathType Leaf)
$required = @(
    'Cfs.App.exe','Cfs.App.dll','Cfs.App.deps.json','Cfs.App.runtimeconfig.json','Cfs.Core.dll','cfs-lzma.dll','tools/7zr.exe',
    'hostfxr.dll','hostpolicy.dll','coreclr.dll','clrjit.dll','Register-CfsFileAssociation.ps1','BETA-NOTICE.txt','THIRD-PARTY-NOTICES.txt','README.md',
    'licenses/DOTNET-LICENSE.txt','licenses/DOTNET-THIRD-PARTY-NOTICES.txt','licenses/LZMA-SDK-NOTICE.txt',
    'docs/BETA-QUICK-START.md','docs/INSTALL-UNINSTALL.md','docs/DATA-SAFETY.md','docs/KNOWN-LIMITATIONS.md','docs/BUG-REPORT-TEMPLATE.md',
    'docs/CONTRIBUTOR-ACCESS-POLICY.md','docs/RELEASE-NOTES-0.1.0-BETA.md','docs/on-demand-mount.md','docs/compression.md','docs/PERFORMANCE-BASELINE.md','docs/CFS-0.1.0-Beta-performance.json'
)
foreach ($relative in $required) { Check "required content: $relative" (Test-Path -LiteralPath (Join-Path $folder $relative) -PathType Leaf) }

$files = @(Get-ChildItem -Recurse -File -LiteralPath $folder)
$forbidden = @($files | Where-Object { $_.Extension -in @('.pdb','.cs','.obj','.cfs','.log','.zip') -or $_.Name -match 'pasted-text|goal-objective|benchmark-workspace|\.codex' })
Check 'no forbidden source temporary log archive or AI artifacts' ($forbidden.Count -eq 0)
Check 'no CLI included in GUI runtime package' (-not (Test-Path -LiteralPath (Join-Path $folder 'Cfs.Cli.dll')))

$exeVersion = (Get-Item -LiteralPath (Join-Path $folder 'Cfs.App.exe')).VersionInfo.ProductVersion
Check 'packaged executable identity is CFS 0.1.0 Beta' ($exeVersion -eq '0.1.0 Beta')
foreach ($relative in @('README.md','BETA-NOTICE.txt','docs/BETA-QUICK-START.md','docs/RELEASE-NOTES-0.1.0-BETA.md','docs/PERFORMANCE-BASELINE.md')) {
    Check "document identity: $relative" ((Get-Content -Raw -LiteralPath (Join-Path $folder $relative)).Contains('CFS 0.1.0 Beta',[StringComparison]::Ordinal))
}
Check 'performance report identity' ((Get-Content -Raw -LiteralPath (Join-Path $folder 'docs\CFS-0.1.0-Beta-performance.json')).Contains('"Product": "CFS 0.1.0 Beta"',[StringComparison]::Ordinal))

$associationScript = Join-Path $folder 'Register-CfsFileAssociation.ps1'
$spaceRoot = Join-Path $env:TEMP ("CFS package path with spaces\" + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $spaceRoot -Force | Out-Null
    $spaceExe = Join-Path $spaceRoot 'Cfs.App.exe'; Copy-Item -LiteralPath (Join-Path $folder 'Cfs.App.exe') -Destination $spaceExe
    $dryRun = (& $associationScript -AppPath $spaceExe -DryRun) -join "`n"
    Check 'association command quotes packaged executable path with spaces' ($dryRun -eq ('"' + $spaceExe + '" "%1"'))
    $unregisterDryRun = (& $associationScript -Unregister -DryRun) -join "`n"
    Check 'association removal is available and scoped to current-user keys' ($unregisterDryRun.Contains('HKCU\Software\Classes\.cfs') -and $unregisterDryRun.Contains('HKCU\Software\Classes\CFS.Archive'))
}
finally { if (Test-Path -LiteralPath (Split-Path -Parent $spaceRoot)) { Remove-Item -LiteralPath (Split-Path -Parent $spaceRoot) -Recurse -Force } }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$folderHashes = @{}
foreach ($file in $files) { $relative = $file.FullName.Substring($folder.Length).TrimStart('\').Replace('\','/'); $folderHashes[$relative] = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash }
$zipHashes = @{}
$archive = [IO.Compression.ZipFile]::OpenRead($zipFile)
try {
    foreach ($entry in $archive.Entries | Where-Object { -not $_.FullName.EndsWith('/') }) {
        $name = $entry.FullName.Replace('\','/'); $prefix = $packageName + '/'
        if (-not $name.StartsWith($prefix,[StringComparison]::Ordinal)) { $zipHashes["<bad-root>/$name"] = ''; continue }
        $stream = $entry.Open(); try { $hash = [Security.Cryptography.SHA256]::Create(); try { $zipHashes[$name.Substring($prefix.Length)] = [Convert]::ToHexString($hash.ComputeHash($stream)) } finally { $hash.Dispose() } } finally { $stream.Dispose() }
    }
}
finally { $archive.Dispose() }
Check 'ZIP and folder file counts match' ($zipHashes.Count -eq $folderHashes.Count)
$mismatches = @($folderHashes.Keys | Where-Object { -not $zipHashes.ContainsKey($_) -or $zipHashes[$_] -ne $folderHashes[$_] })
Check 'ZIP and folder contents match by SHA-256' ($mismatches.Count -eq 0 -and -not $zipHashes.ContainsKey('<bad-root>'))

$totalBytes = ($files | Measure-Object Length -Sum).Sum
Write-Host "PACKAGE_FILE_COUNT=$($files.Count)"
Write-Host "PACKAGE_FOLDER_BYTES=$totalBytes"
Write-Host "PACKAGE_ZIP_BYTES=$((Get-Item -LiteralPath $zipFile).Length)"
Write-Host "TOTAL_CHECKS=$($passes + $failures.Count) PASS=$passes FAIL=$($failures.Count)"
if ($failures.Count -gt 0) { exit 1 }
