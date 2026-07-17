param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputPath = '',
    [switch]$DeveloperStaging
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
[xml]$props = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'Directory.Build.props')
$version = $props.Project.PropertyGroup.CfsVersion
$label = $props.Project.PropertyGroup.CfsReleaseLabel
if ([string]::IsNullOrWhiteSpace($version) -or $version -notmatch '^\d+\.\d+\.\d+$' -or
    [string]::IsNullOrWhiteSpace($label)) {
    throw 'Directory.Build.props must define a semantic CfsVersion and a non-empty CfsReleaseLabel.'
}
$packageName = "CFS-$version-$label-win-x64"
if ($DeveloperStaging) {
    if ($version -ne '0.2.0' -or $label -ne 'Beta' -or $Runtime -ne 'win-x64') { throw 'Developer staging currently supports only CFS 0.2.0 Beta for win-x64.' }
    if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $repoRoot 'obj\Cfs-0.2.0-Beta-developer-stage' }
    $resolvedStage = [IO.Path]::GetFullPath($OutputPath)
    $resolvedRepo = [IO.Path]::GetFullPath($repoRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not $resolvedStage.StartsWith($resolvedRepo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Developer staging must stay inside the repository.' }
    if (Test-Path -LiteralPath $resolvedStage) {
        Get-ChildItem -LiteralPath $resolvedStage -Recurse -Force -ErrorAction SilentlyContinue |
            ForEach-Object { $_.Attributes = [IO.FileAttributes]::Normal }
        (Get-Item -LiteralPath $resolvedStage -Force).Attributes = [IO.FileAttributes]::Normal
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resolvedStage -Force | Out-Null
    $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    & $dotnet publish (Join-Path $repoRoot 'src\Cfs.App\Cfs.App.csproj') -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=false --ignore-failed-sources -o $resolvedStage
    if ($LASTEXITCODE -ne 0) { throw 'Cfs.App developer staging failed.' }
    & $dotnet publish (Join-Path $repoRoot 'src\Cfs.Broker\Cfs.Broker.csproj') -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=false --ignore-failed-sources -o $resolvedStage
    if ($LASTEXITCODE -ne 0) { throw 'Cfs.Broker developer staging failed.' }
    foreach ($required in @('Cfs.App.exe','Cfs.Broker.exe')) { if (-not (Test-Path -LiteralPath (Join-Path $resolvedStage $required))) { throw "Developer staging is missing $required." } }
    $shellNewDirectory = Join-Path $resolvedStage 'ShellNew'
    New-Item -ItemType Directory -Path $shellNewDirectory -Force | Out-Null
    $emptyTemplate = Join-Path $shellNewDirectory 'CFS-Empty.cfs'
    & $dotnet run --project (Join-Path $repoRoot 'src\Cfs.Cli\Cfs.Cli.csproj') -c $Configuration --no-restore -- create-empty $emptyTemplate
    if ($LASTEXITCODE -ne 0) { throw 'Empty ShellNew template generation failed.' }
    & $dotnet run --project (Join-Path $repoRoot 'src\Cfs.Cli\Cfs.Cli.csproj') -c $Configuration --no-restore -- validate $emptyTemplate
    if ($LASTEXITCODE -ne 0) { throw 'Generated ShellNew template validation failed.' }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'tools\Register-CfsFileAssociation.ps1') -Destination (Join-Path $resolvedStage 'Register-CfsFileAssociation.ps1') -Force
    Write-Host "DEVELOPER_STAGE=$resolvedStage"
    Write-Host 'DEVELOPER_STAGE_APP=True'
    Write-Host 'DEVELOPER_STAGE_BROKER=True'
    Write-Host 'DEVELOPER_STAGE_SHELLNEW_VALID=True'
    return
}
if ($Runtime -ne 'win-x64') { throw 'Release packaging currently supports only win-x64.' }

$distRoot = Join-Path $repoRoot 'dist'
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $distRoot $packageName }
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$resolvedDist = [IO.Path]::GetFullPath($distRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
if (-not $resolvedOutput.StartsWith($resolvedDist + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "OutputPath must stay inside $resolvedDist." }
if ((Split-Path -Leaf $resolvedOutput) -ne $packageName) { throw "Output folder must be named $packageName." }
$zipPath = Join-Path $distRoot ($packageName + '.zip')
$releaseIdentity = "CFS $version $label"
$releaseNotes = "RELEASE-NOTES-$version-$($label.ToUpperInvariant()).md"
$performanceReportName = "CFS-$version-$label-performance.json"
$packageDocs = @('BETA-QUICK-START.md','INSTALL-UNINSTALL.md','DATA-SAFETY.md','KNOWN-LIMITATIONS.md','BUG-REPORT-TEMPLATE.md','CONTRIBUTOR-ACCESS-POLICY.md',$releaseNotes,'on-demand-mount.md','compression.md','PERFORMANCE-BASELINE.md')
$versionedInputs = @('README.md','packaging\BETA-NOTICE.txt') + ($packageDocs | ForEach-Object { "docs\$_" }) + @("dist\$performanceReportName")
foreach ($relative in $versionedInputs) {
    $inputPath = Join-Path $repoRoot $relative
    if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) {
        throw "Current release input is missing: $relative. Refusing to build a package labelled $releaseIdentity."
    }
    if ((Get-Content -Raw -LiteralPath $inputPath).IndexOf($releaseIdentity, [StringComparison]::Ordinal) -lt 0) {
        throw "Current release input is not marked '$releaseIdentity': $relative. Refusing to package stale release material."
    }
}

$dotnet = if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' } else { Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = Join-Path $env:USERPROFILE 'scoop\apps\dotnet-sdk\current\dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnet)) { throw 'dotnet.exe was not found.' }

if (Test-Path -LiteralPath $resolvedOutput) { Remove-Item -LiteralPath $resolvedOutput -Recurse -Force }
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null

& $dotnet publish (Join-Path $repoRoot 'src\Cfs.App\Cfs.App.csproj') -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=false -o $resolvedOutput
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
& $dotnet publish (Join-Path $repoRoot 'src\Cfs.Broker\Cfs.Broker.csproj') -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=false -o $resolvedOutput
if ($LASTEXITCODE -ne 0) { throw 'Cfs.Broker publish failed.' }
if (-not (Test-Path -LiteralPath (Join-Path $resolvedOutput 'Cfs.Broker.exe'))) { throw 'Published staging is missing Cfs.Broker.exe.' }
Get-ChildItem -LiteralPath $resolvedOutput -Filter '*.pdb' -File | Remove-Item -Force

$shellNewDirectory = Join-Path $resolvedOutput 'ShellNew'
New-Item -ItemType Directory -Path $shellNewDirectory -Force | Out-Null
$emptyTemplate = Join-Path $shellNewDirectory 'CFS-Empty.cfs'
& $dotnet run --project (Join-Path $repoRoot 'src\Cfs.Cli\Cfs.Cli.csproj') -c $Configuration --no-restore -- create-empty $emptyTemplate
if ($LASTEXITCODE -ne 0) { throw 'Empty ShellNew template generation failed.' }
& $dotnet run --project (Join-Path $repoRoot 'src\Cfs.Cli\Cfs.Cli.csproj') -c $Configuration --no-restore -- validate $emptyTemplate
if ($LASTEXITCODE -ne 0) { throw 'Generated ShellNew template validation failed.' }

Copy-Item -LiteralPath (Join-Path $repoRoot 'tools\Register-CfsFileAssociation.ps1') -Destination (Join-Path $resolvedOutput 'Register-CfsFileAssociation.ps1') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $resolvedOutput 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\BETA-NOTICE.txt') -Destination (Join-Path $resolvedOutput 'BETA-NOTICE.txt') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $resolvedOutput 'THIRD-PARTY-NOTICES.txt') -Force

$docsOutput = Join-Path $resolvedOutput 'docs'
New-Item -ItemType Directory -Path $docsOutput -Force | Out-Null
foreach ($document in $packageDocs) { Copy-Item -LiteralPath (Join-Path $repoRoot "docs\$document") -Destination (Join-Path $docsOutput $document) -Force }

$performanceReport = Join-Path $repoRoot "dist\$performanceReportName"
Copy-Item -LiteralPath $performanceReport -Destination (Join-Path $docsOutput $performanceReportName) -Force

$licensesOutput = Join-Path $resolvedOutput 'licenses'
New-Item -ItemType Directory -Path $licensesOutput -Force | Out-Null
$dotnetRoot = Split-Path -Parent $dotnet
Copy-Item -LiteralPath (Join-Path $dotnetRoot 'LICENSE.txt') -Destination (Join-Path $licensesOutput 'DOTNET-LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') -Destination (Join-Path $licensesOutput 'DOTNET-THIRD-PARTY-NOTICES.txt') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'third_party\lzma-sdk\DOC\lzma-sdk.txt') -Destination (Join-Path $licensesOutput 'LZMA-SDK-NOTICE.txt') -Force

Compress-Archive -LiteralPath $resolvedOutput -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "PUBLISHED_FOLDER=$resolvedOutput"
Write-Host "PUBLISHED_ZIP=$zipPath"
