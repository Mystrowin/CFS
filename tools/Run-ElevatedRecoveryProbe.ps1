[CmdletBinding()]
param(
    [string]$Workspace = (Split-Path -Parent $PSScriptRoot),
    [string]$ResultPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'elevated-recovery-result.json')
)

$ErrorActionPreference = 'Stop'

# Pin the x64 .NET host for the net8.0-windows broker when this is run from
# Windows PowerShell 5.1, whose inherited environment may resolve another host.
$dotNetRoot = 'C:\Program Files\dotnet'
$dotNetExe = Join-Path $dotNetRoot 'dotnet.exe'
if (-not (Test-Path -LiteralPath $dotNetExe)) { throw "Required .NET host not found: $dotNetExe" }
$env:DOTNET_ROOT = $dotNetRoot
$env:DOTNET_ROLL_FORWARD = 'Major'
if (-not (($env:PATH -split ';') -contains $dotNetRoot)) { $env:PATH = "$dotNetRoot;$env:PATH" }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell window.'
}

$broker = Join-Path $Workspace 'src\Cfs.Broker\bin\Release\net8.0-windows\Cfs.Broker.exe'
if (-not (Test-Path -LiteralPath $broker)) { throw "Broker binary not found: $broker" }

$runRoot = Join-Path $env:TEMP ('cfs-elevated-recovery-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$archive = Join-Path $runRoot 'RecoveryProbe.cfs'
$suffix = 'elevated-' + ([guid]::NewGuid().ToString('N'))
$oldEnv = @{}
foreach ($name in 'CFS_BROKER_INSTANCE_SUFFIX','CFS_BROKER_ALLOW_SHUTDOWN','CFS_BROKER_DISABLE_EXPLORER','CFS_BROKER_TEST_QUIET_PERIOD_MS') {
    $oldEnv[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$env:CFS_BROKER_INSTANCE_SUFFIX = $suffix
$env:CFS_BROKER_ALLOW_SHUTDOWN = '1'
$env:CFS_BROKER_DISABLE_EXPLORER = '1'
$env:CFS_BROKER_TEST_QUIET_PERIOD_MS = '250'

function Invoke-Broker([string[]]$Arguments, [string]$Name) {
    $responsePath = Join-Path $runRoot ($Name + '.json')
    $argumentsToStart = @($Arguments + @('--response-file', $responsePath))
    # Windows PowerShell 5.1 flattens ArgumentList arrays using legacy parsing.
    # Quote every token explicitly so paths and option values arrive unchanged.
    $argumentString = (($argumentsToStart | ForEach-Object {
        '"' + ([string]$_).Replace('"', '\"') + '"'
    }) -join ' ')
    $isResidentOwnerCommand = $Arguments.Count -gt 0 -and $Arguments[0] -in @('create-empty','open')
    if ($isResidentOwnerCommand) {
        $p = Start-Process -FilePath $broker -ArgumentList $argumentString -PassThru
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while (-not (Test-Path -LiteralPath $responsePath)) {
            if ($p.HasExited) { throw "$Name broker exited with code $($p.ExitCode) before writing a response." }
            if ([DateTime]::UtcNow -gt $deadline) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; throw "$Name produced no response within 15 seconds." }
            Start-Sleep -Milliseconds 100
        }
    }
    else {
        $p = Start-Process -FilePath $broker -ArgumentList $argumentString -Wait -PassThru
    }
    if (-not (Test-Path -LiteralPath $responsePath)) { throw "$Name produced no response (exit $($p.ExitCode))." }
    $response = Get-Content -Raw -LiteralPath $responsePath | ConvertFrom-Json
    if (-not $response.Success) { throw "$Name failed: $($response.ErrorCode) $($response.Message)" }
    if ($Arguments[0] -eq 'create-empty') {
        $null = Invoke-Broker @('shutdown') ($Name + '-shutdown')
    }
    return $response
}

function Get-Sha256([string]$Path) { return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash }

$result = [ordered]@{
    schema = 1
    startedUtc = [DateTime]::UtcNow.ToString('o')
    elevated = $true
    projfs = $null
    archive = $archive
    brokerPath = $broker
    create = $null
    firstOpen = $null
    killedBrokerPid = $null
    reopen = $null
    archiveHashBeforeKill = $null
    archiveHashAfterReopen = $null
    recoveredFile = $false
    cleanup = $false
    error = $null
}

try {
    $feature = Get-WindowsOptionalFeature -Online -FeatureName Client-ProjFS
    $result.projfs = $feature.State
    if ($feature.State -ne 'Enabled') { throw "Client-ProjFS is not enabled: $($feature.State)" }

    $result.create = Invoke-Broker @('create-empty', $archive) 'create'
    $result.firstOpen = Invoke-Broker @('open', $archive) 'open'
    $mount = [string]$result.firstOpen.MountPath
    if (-not (Test-Path -LiteralPath $mount)) { throw "Mount path was not created: $mount" }
    $probeFile = Join-Path $mount 'elevated-recovery.txt'
    [IO.File]::WriteAllText($probeFile, 'elevated recovery probe')
    Start-Sleep -Milliseconds 1200
    $result.archiveHashBeforeKill = Get-Sha256 $archive

    $brokerPid = [int]$result.firstOpen.BrokerProcessId
    $proc = Get-CimInstance Win32_Process -Filter "ProcessId=$brokerPid"
    if (-not $proc -or [IO.Path]::GetFullPath($proc.ExecutablePath) -ne [IO.Path]::GetFullPath($broker)) {
        throw "Refusing to kill PID $brokerPid because it is not the expected CFS broker."
    }
    $result.killedBrokerPid = $brokerPid
    Stop-Process -Id $brokerPid -Force
    Start-Sleep -Milliseconds 500

    $result.reopen = Invoke-Broker @('open', $archive) 'reopen'
    $reopenMount = [string]$result.reopen.MountPath
    $result.recoveredFile = (Test-Path -LiteralPath (Join-Path $reopenMount 'elevated-recovery.txt')) -and
        ((Get-Content -Raw -LiteralPath (Join-Path $reopenMount 'elevated-recovery.txt')) -eq 'elevated recovery probe')
    $result.archiveHashAfterReopen = Get-Sha256 $archive
    if (-not $result.recoveredFile) { throw 'Recovered file was not present after broker kill and reopen.' }
    if ($result.archiveHashBeforeKill -ne $result.archiveHashAfterReopen) { throw 'Archive hash changed during reopen unexpectedly.' }

    $null = Invoke-Broker @('close', $archive) 'close'
    $null = Invoke-Broker @('shutdown') 'shutdown'
    $result.cleanup = $true
}
catch {
    $result.error = $_.Exception.Message
    throw
}
finally {
    $result.finishedUtc = [DateTime]::UtcNow.ToString('o')
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ResultPath -Encoding UTF8
    foreach ($name in $oldEnv.Keys) { [Environment]::SetEnvironmentVariable($name, $oldEnv[$name], 'Process') }
    Remove-Item -LiteralPath $runRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output (Get-Content -Raw -LiteralPath $ResultPath)

