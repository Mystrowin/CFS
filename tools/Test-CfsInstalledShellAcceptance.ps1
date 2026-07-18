[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SetupPath,
    [Parameter(Mandatory = $true)]
    [string]$Workspace,
    [Parameter(Mandatory = $true)]
    [switch]$AcceptMachineAssociationChanges
)

# Opt-in real-shell acceptance. This script must run elevated because the
# production installer writes HKLM. It never writes FileExts/UserChoice and
# refuses to run over a foreign association, active CFS session, or installed
# machine-wide CFS copy.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$result = [ordered]@{
    schema = 1
    status = 'FAIL'
    setupSha256 = $null
    effectiveOpen = $null
    effectiveClose = $null
    effectiveShellNew = $null
    effectiveCompress = $null
    shellNewValid = $false
    renamedArchive = $false
    firstBrokerPid = $null
    secondBrokerPid = $null
    mountPath = $null
    sessionReused = $false
    explorerObserved = $false
    noCfsApp = $false
    normalProcessWrite = $false
    atomicReplacement = $false
    atomicMoveOverwrite = $false
    ordinaryWriter = [ordered]@{
        host = $null
        exitCode = $null
        stdout = $null
        stderr = $null
        replace = $null
        moveOverwrite = $null
    }
    firstCloseRemovedMount = $false
    reopenedPersistedContent = $false
    finalCloseRemovedMount = $false
    registryRestored = $false
    userChoiceUnchanged = $false
    installRemoved = $false
    processesClean = $false
    sessionsClean = $false
    workspaceClean = $false
    error = $null
}

if (-not $AcceptMachineAssociationChanges) { throw 'Pass -AcceptMachineAssociationChanges to opt in.' }
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this acceptance harness from an elevated PowerShell. The production installer requires HKLM access.'
}

$SetupPath = (Resolve-Path -LiteralPath $SetupPath).Path
$Workspace = [IO.Path]::GetFullPath($Workspace).TrimEnd('\')
$tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')
if (-not $Workspace.StartsWith($tempRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Workspace must be a unique child of TEMP: $tempRoot"
}
if (Test-Path -LiteralPath $Workspace) { throw "Workspace already exists: $Workspace" }
if ([IO.Path]::GetFileName($Workspace) -notmatch '^cfs-installed-shell-[0-9a-f]{32}$') {
    throw 'Workspace leaf must be cfs-installed-shell- followed by a 32-character GUID.'
}

$installPath = Join-Path $Workspace 'Installed CFS 0.2'
$installLog = Join-Path $Workspace 'install.log'
$sessionsRoot = Join-Path $env:LOCALAPPDATA 'CFS\Sessions'
$archive = Join-Path $Workspace 'Renamed Shell Archive.cfs'
$shellNewCopy = Join-Path $Workspace 'New CFS Compressed Folder.cfs'
$mountPath = $null
$installed = $false
$initialCfsAppIds = @(Get-Process -Name Cfs.App -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
$snapshotRoot = Join-Path $Workspace 'registry-snapshots'
$snapshots = [ordered]@{}
$cleanupErrors = [System.Collections.Generic.List[string]]::new()
$userChoicePath = 'Registry::HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.cfs\UserChoice'
$userChoiceSnapshot = $null
$userChoiceExisted = Test-Path -LiteralPath $userChoicePath

function ConvertTo-WindowsArgument([string]$Value) {
    if ($Value.Length -eq 0) { return '""' }
    if ($Value -notmatch '[\s"]') { return $Value }
    return '"' + $Value.Replace('\', '\').Replace('"', '\"') + '"'
}

function Start-BoundedProcess([string]$FilePath, [string[]]$Arguments, [int]$TimeoutSeconds = 120) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FilePath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.Arguments = (($Arguments | ForEach-Object { ConvertTo-WindowsArgument ([string]$_) }) -join ' ')
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw "Could not start $FilePath" }
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $process.Kill() } catch { }
        throw "Timed out waiting for PID $($process.Id): $FilePath"
    }
    if ($process.ExitCode -ne 0) { throw "$FilePath exited with $($process.ExitCode)." }
    return $process.ExitCode
}

function Start-CapturedProcess([string]$FilePath, [string[]]$Arguments, [int]$TimeoutSeconds = 120) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FilePath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Arguments = (($Arguments | ForEach-Object { ConvertTo-WindowsArgument ([string]$_) }) -join ' ')
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw "Could not start $FilePath" }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $process.Kill() } catch { }
        throw "Timed out waiting for PID $($process.Id): $FilePath"
    }
    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        Stdout = $stdout.GetAwaiter().GetResult()
        Stderr = $stderr.GetAwaiter().GetResult()
    }
}

function Wait-Until([scriptblock]$Condition, [string]$Failure, [int]$TimeoutSeconds = 30) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 200
    }
    throw $Failure
}

function Get-DefaultValue([string]$RegistryPath) {
    try { return [string](Get-ItemPropertyValue -LiteralPath $RegistryPath -Name '(default)' -ErrorAction Stop) }
    catch { return $null }
}

function Test-CfsHandlerCommand([string]$Command) {
    if ([string]::IsNullOrWhiteSpace($Command)) { return $true }
    return $Command -match '^"[^\"]*Cfs\.(App|Broker|CommandClient)\.exe"\s+(?:(?:open|close|compress)\s+)?"%1"$'
}

function Assert-PreexistingAssociationIsSafe {
    $extension = Get-DefaultValue 'Registry::HKEY_CURRENT_USER\Software\Classes\.cfs'
    if ($extension -and $extension -cne 'CFS.Archive') { throw "Refusing foreign HKCU .cfs association: $extension" }
    $description = Get-DefaultValue 'Registry::HKEY_CURRENT_USER\Software\Classes\CFS.Archive'
    $open = Get-DefaultValue 'Registry::HKEY_CURRENT_USER\Software\Classes\CFS.Archive\shell\open\command'
    if ($open) {
        if ($description -notin @('CFS Archive', 'CFS Compressed Folder') -or
            $open -notmatch '^"(?<app>[^"]*\\Cfs\.App\.exe)"\s+"%1"$' -or
            -not (Test-Path -LiteralPath $Matches.app -PathType Leaf) -or
            -not (Test-Path -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($Matches.app)) 'Cfs.Core.dll') -PathType Leaf)) {
            throw 'Refusing an HKCU CFS.Archive overlay that the installer cannot prove is a legacy CFS Cfs.App association.'
        }
    }
    $close = Get-DefaultValue 'Registry::HKEY_CURRENT_USER\Software\Classes\CFS.Archive\shell\CFS.Close\command'
    if ($close -and $close -notmatch '^"[^\"]*Cfs\.(Broker|CommandClient)\.exe"\s+close\s+"%1"$') { throw 'Refusing a foreign HKCU Close CFS command.' }
    $compress = Get-DefaultValue 'Registry::HKEY_CURRENT_USER\Software\Classes\Directory\shell\CFS.Compress\command'
    if ($compress -and $compress -notmatch '^"[^\"]*Cfs\.(Broker|CommandClient)\.exe"\s+compress\s+"%1"$') { throw 'Refusing a foreign HKCU Compress command.' }
}

function Export-Key([string]$HivePath, [string]$Name) {
    $providerPath = 'Registry::' + $HivePath.Replace('HKCU', 'HKEY_CURRENT_USER').Replace('HKLM', 'HKEY_LOCAL_MACHINE')
    $exists = Test-Path -LiteralPath $providerPath
    $file = Join-Path $snapshotRoot ($Name + '.reg')
    if ($exists) {
        & reg.exe export $HivePath $file /y | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not snapshot $HivePath" }
    }
    $snapshots[$HivePath] = [pscustomobject]@{ Exists = $exists; File = $file; ProviderPath = $providerPath }
}

function Restore-Snapshots {
    $failures = [System.Collections.Generic.List[string]]::new()
    foreach ($item in $snapshots.GetEnumerator()) {
        $snapshot = $item.Value
        try {
            if (Test-Path -LiteralPath $snapshot.ProviderPath) { Remove-Item -LiteralPath $snapshot.ProviderPath -Recurse -Force }
            if ($snapshot.Exists) {
                & reg.exe import $snapshot.File | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "reg.exe import exited with $LASTEXITCODE" }
            }
        } catch { $failures.Add("$($item.Key): $($_.Exception.Message)") }
    }
    if ($failures.Count -gt 0) { throw 'Registry snapshot restoration failed: ' + ($failures -join ' | ') }
}

function Get-UserChoiceFingerprint {
    if (-not (Test-Path -LiteralPath $userChoicePath)) { return $null }
    $key = Get-Item -LiteralPath $userChoicePath
    $properties = Get-ItemProperty -LiteralPath $userChoicePath
    $pairs = foreach ($name in $key.GetValueNames() | Sort-Object) { "$name=$($properties.$name)" }
    return ($pairs -join "`n")
}

function Invoke-ShellOpen([string]$Path) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Path
    $start.UseShellExecute = $true
    $start.Verb = 'open'
    [void][Diagnostics.Process]::Start($start)
}

function Invoke-ShellClose([string]$Path) {
    $shell = New-Object -ComObject Shell.Application
    $shell.ShellExecute($Path, '', '', 'CFS.Close', 0)
}

function Get-InstalledBrokers {
    $expected = [IO.Path]::GetFullPath((Join-Path $installPath 'Cfs.Broker.exe'))
    return @(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -eq 'Cfs.Broker.exe' -and $_.ExecutablePath -and
        [IO.Path]::GetFullPath($_.ExecutablePath).Equals($expected, [StringComparison]::OrdinalIgnoreCase)
    })
}

function Close-TestExplorerWindows([string]$Mount) {
    if ([string]::IsNullOrWhiteSpace($Mount)) { return }
    $uri = ([Uri]$Mount).AbsoluteUri.TrimEnd('/')
    $shell = New-Object -ComObject Shell.Application
    foreach ($window in @($shell.Windows())) {
        try { if ([string]$window.LocationURL.TrimEnd('/') -eq $uri) { $window.Quit() } } catch { }
    }
}

function Test-EmptyTemplate([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 24 -or [Text.Encoding]::ASCII.GetString($bytes, 0, 4) -ne 'CFS1') { return $false }
    if ([BitConverter]::ToInt32($bytes, 4) -ne 1) { return $false }
    $offset = [BitConverter]::ToInt64($bytes, 8)
    $length = [BitConverter]::ToInt64($bytes, 16)
    if ($offset -lt 24 -or $length -le 0 -or ($offset + $length) -gt $bytes.LongLength) { return $false }
    $json = [Text.Encoding]::UTF8.GetString($bytes, [int]$offset, [int]$length) | ConvertFrom-Json
    return $json.Version -eq 1 -and @($json.Entries).Count -eq 0
}

try {
    foreach ($name in @('CFS_BROKER_INSTANCE_SUFFIX', 'CFS_BROKER_ALLOW_SHUTDOWN', 'CFS_BROKER_DISABLE_EXPLORER')) {
        if ([Environment]::GetEnvironmentVariable($name, 'Process')) { throw "Refusing inherited broker test variable: $name" }
    }
    if (@(Get-ChildItem Env: | Where-Object { $_.Name -like 'CFS_BROKER_TEST_*' }).Count -ne 0) { throw 'Refusing inherited CFS_BROKER_TEST_* variables.' }
    if (@(Get-ChildItem -LiteralPath $sessionsRoot -Force -ErrorAction SilentlyContinue).Count -ne 0) { throw 'Refusing to run while CFS session entries exist.' }
    if (Test-Path -LiteralPath 'Registry::HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Uninstall\{8A9237D3-6476-4F69-AE72-58221802FA45}_is1') {
        throw 'Refusing to replace an existing machine-wide CFS installation.'
    }
    foreach ($path in @('Registry::HKEY_LOCAL_MACHINE\Software\Classes\.cfs', 'Registry::HKEY_LOCAL_MACHINE\Software\Classes\CFS.Archive', 'Registry::HKEY_LOCAL_MACHINE\Software\Classes\Directory\shell\CFS.Compress')) {
        if (Test-Path -LiteralPath $path) { throw "Refusing pre-existing machine CFS association: $path" }
    }
    Assert-PreexistingAssociationIsSafe

    New-Item -ItemType Directory -Path $snapshotRoot -Force | Out-Null
    $userChoiceSnapshot = Get-UserChoiceFingerprint
    Export-Key 'HKCU\Software\Classes\.cfs' 'hkcu-extension'
    Export-Key 'HKCU\Software\Classes\CFS.Archive' 'hkcu-progid'
    Export-Key 'HKCU\Software\Classes\Directory\shell\CFS.Compress' 'hkcu-compress'

    $result.setupSha256 = (Get-FileHash -LiteralPath $SetupPath -Algorithm SHA256).Hash
    Start-BoundedProcess $SetupPath @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TASKS=', ('/DIR=' + $installPath), ('/LOG=' + $installLog)) | Out-Null
    $installed = $true

    $brokerPath = Join-Path $installPath 'Cfs.Broker.exe'
    $commandClientPath = Join-Path $installPath 'Cfs.CommandClient.exe'
    $expectedOpen = '"' + $commandClientPath + '" open "%1"'
    $expectedClose = '"' + $commandClientPath + '" close "%1"'
    $expectedCompress = '"' + $commandClientPath + '" compress "%1"'
    $expectedTemplate = Join-Path $installPath 'ShellNew\CFS-Empty.cfs'
    $result.effectiveOpen = Get-DefaultValue 'Registry::HKEY_CLASSES_ROOT\CFS.Archive\shell\open\command'
    $result.effectiveClose = Get-DefaultValue 'Registry::HKEY_CLASSES_ROOT\CFS.Archive\shell\CFS.Close\command'
    $result.effectiveShellNew = [string](Get-ItemPropertyValue -LiteralPath 'Registry::HKEY_CLASSES_ROOT\.cfs\ShellNew' -Name 'FileName')
    $result.effectiveCompress = Get-DefaultValue 'Registry::HKEY_CLASSES_ROOT\Directory\shell\CFS.Compress\command'
    if ($result.effectiveOpen -cne $expectedOpen -or $result.effectiveClose -cne $expectedClose -or
        $result.effectiveShellNew -cne $expectedTemplate -or $result.effectiveCompress -cne $expectedCompress) {
        throw 'Merged HKCR does not resolve every CFS shell surface to the isolated installed payload.'
    }

    Copy-Item -LiteralPath $result.effectiveShellNew -Destination $shellNewCopy
    $result.shellNewValid = Test-EmptyTemplate $shellNewCopy
    if (-not $result.shellNewValid) { throw 'Registered ShellNew template is not an empty CFS1/v1 archive.' }
    Move-Item -LiteralPath $shellNewCopy -Destination $archive
    $result.renamedArchive = Test-Path -LiteralPath $archive -PathType Leaf

    Invoke-ShellOpen $archive
    Wait-Until { @(Get-InstalledBrokers).Count -eq 1 } 'The installed broker owner did not start.'
    Wait-Until { @(Get-ChildItem -LiteralPath $sessionsRoot -Directory -Force -ErrorAction SilentlyContinue).Count -eq 1 } 'Exactly one CFS mount did not appear.'
    $mountPath = @(Get-ChildItem -LiteralPath $sessionsRoot -Directory -Force)[0].FullName
    $result.mountPath = $mountPath
    $firstBroker = @(Get-InstalledBrokers)[0]
    $result.firstBrokerPid = [int]$firstBroker.ProcessId
    $mountUri = ([Uri]$mountPath).AbsoluteUri.TrimEnd('/')
    Wait-Until {
        $shell = New-Object -ComObject Shell.Application
        @($shell.Windows() | Where-Object { try { ([string]$_.LocationURL).TrimEnd('/') -eq $mountUri } catch { $false } }).Count -ge 1
    } 'Explorer did not navigate to the projected mount.'
    $result.explorerObserved = $true

    Invoke-ShellOpen $archive
    Start-Sleep -Seconds 2
    $brokers = @(Get-InstalledBrokers)
    $mounts = @(Get-ChildItem -LiteralPath $sessionsRoot -Directory -Force -ErrorAction SilentlyContinue)
    $result.secondBrokerPid = if ($brokers.Count -eq 1) { [int]$brokers[0].ProcessId } else { $null }
    $result.sessionReused = $brokers.Count -eq 1 -and $mounts.Count -eq 1 -and
        $result.firstBrokerPid -eq $result.secondBrokerPid -and $mounts[0].FullName -eq $mountPath
    if (-not $result.sessionReused) { throw 'Repeated ShellExecute did not reuse one broker session and mount.' }

    $writer = Join-Path $Workspace 'ordinary-writer.ps1'
    @'
param([string]$Mount)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @"
using System.IO;
public static class CfsAcceptanceAtomicFile
{
    public static void ReplaceWithoutBackup(string source, string destination)
    {
        File.Replace(source, destination, null);
    }
}
"@
$evidence = [ordered]@{
    replace = [ordered]@{ callSucceeded = $false; error = $null; targetExists = $false; targetContent = $null; tempExists = $false; tempContent = $null }
    moveOverwrite = [ordered]@{ callSucceeded = $false; error = $null; targetExists = $false; targetContent = $null; tempExists = $false; tempContent = $null }
}
function Read-IfPresent([string]$Path) {
    if ([IO.File]::Exists($Path)) { return [IO.File]::ReadAllText($Path) }
    return $null
}
$replaceTarget = Join-Path $Mount 'acceptance.txt'
$replaceTemp = Join-Path $Mount 'acceptance.tmp'
try {
    [IO.File]::WriteAllText($replaceTarget, 'replace initial text')
    [IO.File]::WriteAllText($replaceTemp, 'File.Replace persisted')
    [CfsAcceptanceAtomicFile]::ReplaceWithoutBackup($replaceTemp, $replaceTarget)
    $evidence.replace.callSucceeded = $true
} catch {
    $evidence.replace.error = $_.Exception.ToString()
}
$evidence.replace.targetExists = [IO.File]::Exists($replaceTarget)
$evidence.replace.targetContent = Read-IfPresent $replaceTarget
$evidence.replace.tempExists = [IO.File]::Exists($replaceTemp)
$evidence.replace.tempContent = Read-IfPresent $replaceTemp

$moveTarget = Join-Path $Mount 'acceptance-move.txt'
$moveTemp = Join-Path $Mount 'acceptance-move.tmp'
try {
    [IO.File]::WriteAllText($moveTarget, 'move initial text')
    [IO.File]::WriteAllText($moveTemp, 'File.Move overwrite persisted')
    [IO.File]::Move($moveTemp, $moveTarget, $true)
    $evidence.moveOverwrite.callSucceeded = $true
} catch {
    $evidence.moveOverwrite.error = $_.Exception.ToString()
}
$evidence.moveOverwrite.targetExists = [IO.File]::Exists($moveTarget)
$evidence.moveOverwrite.targetContent = Read-IfPresent $moveTarget
$evidence.moveOverwrite.tempExists = [IO.File]::Exists($moveTemp)
$evidence.moveOverwrite.tempContent = Read-IfPresent $moveTemp

$evidence | ConvertTo-Json -Depth 5 -Compress | Write-Output
if (-not ($evidence.replace.callSucceeded -and $evidence.replace.targetContent -ceq 'File.Replace persisted' -and -not $evidence.replace.tempExists -and
    $evidence.moveOverwrite.callSucceeded -and $evidence.moveOverwrite.targetContent -ceq 'File.Move overwrite persisted' -and -not $evidence.moveOverwrite.tempExists)) { exit 1 }
'@ | Set-Content -LiteralPath $writer -Encoding UTF8
    $writerHost = (Get-Command pwsh.exe -ErrorAction Stop).Source
    $capturedWriter = Start-CapturedProcess $writerHost @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $writer, $mountPath) 30
    $result.ordinaryWriter.host = $writerHost
    $result.ordinaryWriter.exitCode = $capturedWriter.ExitCode
    $result.ordinaryWriter.stdout = $capturedWriter.Stdout
    $result.ordinaryWriter.stderr = $capturedWriter.Stderr
    $writerJson = @($capturedWriter.Stdout -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })[-1]
    $writerEvidence = $writerJson | ConvertFrom-Json
    $result.ordinaryWriter.replace = $writerEvidence.replace
    $result.ordinaryWriter.moveOverwrite = $writerEvidence.moveOverwrite
    $targetFile = Join-Path $mountPath 'acceptance.txt'
    $result.normalProcessWrite = Test-Path -LiteralPath $targetFile -PathType Leaf
    $result.atomicReplacement = $writerEvidence.replace.callSucceeded -and $writerEvidence.replace.targetContent -ceq 'File.Replace persisted' -and -not $writerEvidence.replace.tempExists
    $result.atomicMoveOverwrite = $writerEvidence.moveOverwrite.callSucceeded -and $writerEvidence.moveOverwrite.targetContent -ceq 'File.Move overwrite persisted' -and -not $writerEvidence.moveOverwrite.tempExists
    if ($capturedWriter.ExitCode -ne 0 -or -not $result.normalProcessWrite -or -not $result.atomicReplacement -or -not $result.atomicMoveOverwrite) {
        throw "Ordinary writer failed (exit=$($capturedWriter.ExitCode)); replace=$($result.atomicReplacement); moveOverwrite=$($result.atomicMoveOverwrite); stderr=$($capturedWriter.Stderr)"
    }

    Start-BoundedProcess $commandClientPath @('commit', $archive) | Out-Null
    Invoke-ShellClose $archive
    Wait-Until { -not (Test-Path -LiteralPath $mountPath) } 'Registered Close CFS did not remove the first mount.' 45
    $result.firstCloseRemovedMount = $true
    Close-TestExplorerWindows $mountPath

    Invoke-ShellOpen $archive
    Wait-Until { @(Get-ChildItem -LiteralPath $sessionsRoot -Directory -Force -ErrorAction SilentlyContinue).Count -eq 1 } 'Reopen did not create a projected mount.'
    $mountPath = @(Get-ChildItem -LiteralPath $sessionsRoot -Directory -Force)[0].FullName
    Wait-Until { Test-Path -LiteralPath (Join-Path $mountPath 'acceptance.txt') -PathType Leaf } 'Persisted file did not appear after reopen.'
    $result.reopenedPersistedContent = [IO.File]::ReadAllText((Join-Path $mountPath 'acceptance.txt')) -eq 'File.Replace persisted' -and
        [IO.File]::ReadAllText((Join-Path $mountPath 'acceptance-move.txt')) -eq 'File.Move overwrite persisted'
    if (-not $result.reopenedPersistedContent) { throw 'Atomic replacement content did not persist across close/reopen.' }
    Invoke-ShellClose $archive
    Wait-Until { -not (Test-Path -LiteralPath $mountPath) } 'Registered Close CFS did not remove the reopened mount.' 45
    $result.finalCloseRemovedMount = $true
    Close-TestExplorerWindows $mountPath

    $newApps = @(Get-Process -Name Cfs.App -ErrorAction SilentlyContinue | Where-Object { $initialCfsAppIds -notcontains $_.Id })
    $result.noCfsApp = $newApps.Count -eq 0
    if (-not $result.noCfsApp) { throw 'The installed shell workflow launched Cfs.App.' }
    $result.status = 'PASS'
}
catch {
    $result.error = $_.Exception.ToString()
}
finally {
    try { Close-TestExplorerWindows $mountPath } catch { $cleanupErrors.Add($_.Exception.Message) }
    try {
        foreach ($broker in @(Get-InstalledBrokers)) {
            Stop-Process -Id $broker.ProcessId -Force -ErrorAction Stop
            Wait-Process -Id $broker.ProcessId -Timeout 10 -ErrorAction SilentlyContinue
        }
    } catch { $cleanupErrors.Add($_.Exception.Message) }
    try {
        if ($mountPath -and (Test-Path -LiteralPath $mountPath)) {
            $fullMount = [IO.Path]::GetFullPath($mountPath)
            $marker = Join-Path $fullMount '.cfs-mount-session'
            if ([IO.Path]::GetDirectoryName($fullMount) -eq [IO.Path]::GetFullPath($sessionsRoot) -and
                (Test-Path -LiteralPath $marker) -and (Get-Content -Raw -LiteralPath $marker) -match '^[0-9a-fA-F]{32}$') {
                [IO.Directory]::Delete($fullMount, $true)
                foreach ($sidecar in @("$fullMount.cfs-session.json", "$fullMount.cfs-candidate")) {
                    if ([IO.File]::Exists($sidecar)) { [IO.File]::Delete($sidecar) }
                }
            }
        }
    } catch { $cleanupErrors.Add($_.Exception.Message) }
    try {
        $uninstaller = Join-Path $installPath 'unins000.exe'
        if ($installed -and (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
            Start-BoundedProcess $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') | Out-Null
        }
    } catch { $cleanupErrors.Add($_.Exception.Message) }
    try { Restore-Snapshots; $result.registryRestored = $true } catch { $cleanupErrors.Add($_.Exception.Message) }
    try { $result.userChoiceUnchanged = (Get-UserChoiceFingerprint) -ceq $userChoiceSnapshot } catch { $cleanupErrors.Add($_.Exception.Message) }
    $result.installRemoved = -not (Test-Path -LiteralPath $installPath)
    $result.processesClean = @(Get-InstalledBrokers).Count -eq 0
    $result.sessionsClean = @(Get-ChildItem -LiteralPath $sessionsRoot -Force -ErrorAction SilentlyContinue).Count -eq 0
    try {
        if ($result.registryRestored -and $result.userChoiceUnchanged -and [IO.Directory]::Exists($Workspace)) { [IO.Directory]::Delete($Workspace, $true) }
        $result.workspaceClean = -not [IO.Directory]::Exists($Workspace)
    } catch { $cleanupErrors.Add($_.Exception.Message) }
    if ($cleanupErrors.Count -gt 0) {
        if (-not $result.error) { $result.error = 'Cleanup failed: ' + ($cleanupErrors -join ' | ') }
        $result.status = 'FAIL'
    }
    if (-not ($result.registryRestored -and $result.userChoiceUnchanged -and $result.installRemoved -and
        $result.processesClean -and $result.sessionsClean -and $result.workspaceClean)) {
        if (-not $result.error) { $result.error = 'One or more cleanup/restoration assertions failed.' }
        $result.status = 'FAIL'
    }
}

$json = $result | ConvertTo-Json -Depth 5
Write-Output $json
if ($result.status -ne 'PASS') { exit 1 }
