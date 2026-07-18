[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..'),
    [string]$ArtifactDirectory = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
[xml]$props = Get-Content -Raw -LiteralPath (Join-Path $root 'Directory.Build.props')
$version = [string]$props.Project.PropertyGroup.CfsVersion
if ([string]::IsNullOrWhiteSpace($version)) { throw 'CfsVersion is required for the SBOM.' }

function Hash-FileOrEmpty([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$components = @(
    [ordered]@{ type='application'; name='CFS'; version=$version; licenses=@('LicenseRef-CFS-Proprietary'); sourceHash=(Hash-FileOrEmpty (Join-Path $root 'LICENSE.txt')); modified=$true },
    [ordered]@{ type='library'; name='7-Zip LZMA SDK'; version='26.02'; licenses=@('LicenseRef-Public-Domain'); sourceHash=(Hash-FileOrEmpty (Join-Path $root 'third_party\lzma-sdk\lzma2602.7z')); upstream='https://www.7-zip.org/sdk.html'; modified=$true },
    [ordered]@{ type='framework'; name='Microsoft .NET Runtime'; version='self-contained'; licenses=@('LicenseRef-Microsoft-NET'); sourceHash=$null; upstream='https://dotnet.microsoft.com/'; modified=$false }
)

$artifacts = @()
if (-not [string]::IsNullOrWhiteSpace($ArtifactDirectory) -and (Test-Path -LiteralPath $ArtifactDirectory -PathType Container)) {
    $base = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
    $artifacts = Get-ChildItem -LiteralPath $base -Recurse -File | ForEach-Object {
        [ordered]@{ path=$_.FullName.Substring($base.Length).TrimStart('\').Replace('\','/'); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); size=$_.Length }
    } | Sort-Object path
}

$serialBytes = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("CFS|$version|CycloneDX-1.5"))[0..15]
$serial = [guid]::new([byte[]]$serialBytes)
$sbom = [ordered]@{
    bomFormat = 'CycloneDX'; specVersion = '1.5'; serialNumber = "urn:uuid:$serial"; version = 1
    metadata = [ordered]@{ component = [ordered]@{ type='application'; name='CFS'; version=$version }; tools=@(@{ vendor='CFS'; name='New-CfsSbom.ps1'; version='1' }) }
    components = $components; properties = @(@{ name='cfs:artifact-hashes'; value=($artifacts | ConvertTo-Json -Compress -Depth 4) })
}

$destination = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
[IO.File]::WriteAllText($destination, ($sbom | ConvertTo-Json -Depth 8) + "`n", [Text.UTF8Encoding]::new($false))
