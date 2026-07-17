[CmdletBinding()]
param(
    [string]$Workspace,
    [string]$PackageFolder = ''
)

# This is a production-command smoke test. It deliberately uses an isolated
# HKCU registry subtree, so it verifies every Explorer-facing command without
# replacing the user's real .cfs association or opening an Explorer window.
$ErrorActionPreference = 'Stop'
$dotNetRoot = 'C:\Program Files\dotnet'
if (-not (Test-Path -LiteralPath (Join-Path $dotNetRoot 'dotnet.exe'))) { throw "Required .NET host not found: $dotNetRoot" }
$msbuildSdks = Join-Path $dotNetRoot 'sdk\8.0.423\Sdks'
if (-not (Test-Path -LiteralPath $msbuildSdks)) { throw "Required MSBuild SDK path not found: $msbuildSdks" }
$env:DOTNET_ROOT = $dotNetRoot
$env:MSBuildSDKsPath = $msbuildSdks
if (-not (($env:PATH -split ';') -contains $dotNetRoot)) { $env:PATH = "$dotNetRoot;$env:PATH" }
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$packageTemplate = $null
if ([string]::IsNullOrWhiteSpace($PackageFolder)) {
    $broker = Join-Path $repoRoot 'src\Cfs.Broker\bin\Release\net8.0-windows\Cfs.Broker.exe'
    $registrationScript = Join-Path $repoRoot 'tools\Register-CfsFileAssociation.ps1'
}
else {
    $PackageFolder = (Resolve-Path -LiteralPath $PackageFolder).Path
    $broker = Join-Path $PackageFolder 'Cfs.Broker.exe'
    $registrationScript = Join-Path $PackageFolder 'Register-CfsFileAssociation.ps1'
    $packageTemplate = Join-Path $PackageFolder 'ShellNew\CFS-Empty.cfs'
}
$cli = Join-Path $repoRoot 'src\Cfs.Cli\bin\Release\net8.0\Cfs.Cli.exe'
foreach ($path in @($broker, $cli, $registrationScript)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required workflow input is missing: $path" }
}

if ([string]::IsNullOrWhiteSpace($Workspace)) {
    $Workspace = Join-Path $env:TEMP ('cfs-shell-workflow-' + [guid]::NewGuid().ToString('N'))
}
$Workspace = [IO.Path]::GetFullPath($Workspace)
New-Item -ItemType Directory -Path $Workspace -Force | Out-Null
$registryBase = 'Software\CFS.ShellWorkflow.' + [guid]::NewGuid().ToString('N')
$runRoot = Join-Path $Workspace 'run'
$archive = Join-Path $Workspace 'Silent Open.cfs'
$template = Join-Path $Workspace 'CFS-Empty.cfs'
$suffix = 'shell-workflow-' + [guid]::NewGuid().ToString('N')
$sessionsRoot = Join-Path $env:LOCALAPPDATA 'CFS\Sessions'
$env:CFS_BROKER_INSTANCE_SUFFIX = $suffix
$env:CFS_BROKER_ALLOW_SHUTDOWN = '1'
$env:CFS_BROKER_DISABLE_EXPLORER = '1'
$env:CFS_BROKER_TEST_LOG_DIRECTORY = Join-Path $Workspace 'logs'
$ownedPids = [System.Collections.Generic.List[int]]::new()
$mount = $null

function Wait-ForFile([string]$Path, [int]$TimeoutSeconds = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not (Test-Path -LiteralPath $Path)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw "Timed out waiting for $Path" }
        Start-Sleep -Milliseconds 100
    }
}

function ConvertTo-WindowsCommandLineArgument([string]$Argument) {
    if ($Argument.Length -eq 0) { return '""' }
    if ($Argument -notmatch '[\s"]') { return $Argument }
    $builder = [Text.StringBuilder]::new('"')
    $slashes = 0
    foreach ($character in $Argument.ToCharArray()) {
        if ($character -eq '\\') { $slashes++; continue }
        if ($character -eq '"') {
            [void]$builder.Append('\\' * ($slashes * 2 + 1))
            [void]$builder.Append('"')
            $slashes = 0
            continue
        }
        if ($slashes -gt 0) { [void]$builder.Append('\\' * $slashes); $slashes = 0 }
        [void]$builder.Append($character)
    }
    if ($slashes -gt 0) { [void]$builder.Append('\\' * ($slashes * 2)) }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Invoke-Broker([string[]]$CommandArguments, [string]$Name, [switch]$KeepResident) {
    $responsePath = Join-Path $runRoot ($Name + '.json')
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $broker
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WorkingDirectory = $runRoot
    # Windows PowerShell 5.1 does not expose ProcessStartInfo.ArgumentList.
    # Build the same argv with the documented Windows escaping rules instead.
    $arguments = @($CommandArguments) + @('--response-file', $responsePath)
    $startInfo.Arguments = (($arguments | ForEach-Object { ConvertTo-WindowsCommandLineArgument ([string]$_) }) -join ' ')
    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) { throw "Could not start broker for $Name" }
    $ownedPids.Add($process.Id)
    Wait-ForFile $responsePath
    $response = Get-Content -Raw -LiteralPath $responsePath | ConvertFrom-Json
    if (-not $KeepResident) {
        if (-not $process.WaitForExit(15000)) { throw "$Name broker client did not exit." }
    }
    return [pscustomobject]@{ Response = $response; Process = $process }
}

function Read-RegistryValue([string]$Path, [string]$Name = '') {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($Path)
    if ($null -eq $key) { return $null }
    try { return [string]$key.GetValue($Name) } finally { $key.Dispose() }
}

function Assert-Equal([string]$Actual, [string]$Expected, [string]$Name) {
    if ($Actual -cne $Expected) { throw "$Name mismatch. Expected '$Expected'; actual '$Actual'." }
}

try {
    New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
    if ($packageTemplate) { Copy-Item -LiteralPath $packageTemplate -Destination $template }
    else {
        & $cli create-empty $template | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the ShellNew template fixture.' }
    }
    & $cli validate $template | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'ShellNew template fixture is not a valid CFS1 archive.' }

    $expectedOpen = '"' + [IO.Path]::GetFullPath($broker) + '" open "%1"'
    $expectedCompress = '"' + [IO.Path]::GetFullPath($broker) + '" compress "%1"'
    $expectedClose = '"' + [IO.Path]::GetFullPath($broker) + '" close "%1"'
    $dryRun = & $registrationScript -BrokerPath $broker -EmptyTemplatePath $template -RegistryBasePath $registryBase -DryRun
    if ($LASTEXITCODE -ne 0) { throw 'Association dry-run failed.' }
    foreach ($expectedLine in @("OPEN_COMMAND=$expectedOpen", "SHELLNEW_FILENAME=$([IO.Path]::GetFullPath($template))", "FOLDER_VERB_COMMAND=$expectedCompress", "CLOSE_VERB_COMMAND=$expectedClose")) {
        if ($dryRun -notcontains $expectedLine) { throw "Association dry-run omitted: $expectedLine" }
    }

    & $registrationScript -BrokerPath $broker -EmptyTemplatePath $template -RegistryBasePath $registryBase
    if ($LASTEXITCODE -ne 0) { throw 'Isolated association registration failed.' }
    Assert-Equal (Read-RegistryValue "$registryBase\.cfs") 'CFS.Archive' '.cfs ProgID'
    Assert-Equal (Read-RegistryValue "$registryBase\CFS.Archive") 'CFS Compressed Folder' 'CFS display label'
    Assert-Equal (Read-RegistryValue "$registryBase\CFS.Archive\shell\open\command") $expectedOpen 'open command'
    Assert-Equal (Read-RegistryValue "$registryBase\.cfs\ShellNew" 'FileName') ([IO.Path]::GetFullPath($template)) 'ShellNew FileName'
    Assert-Equal (Read-RegistryValue "$registryBase\Directory\shell\CFS.Compress\command") $expectedCompress 'folder Compress to CFS command'
    Assert-Equal (Read-RegistryValue "$registryBase\CFS.Archive\shell\CFS.Close\command") $expectedClose 'Close CFS command'
    if ($expectedOpen -match 'Cfs\.(App|Cli)') { throw 'The shell open command incorrectly targets App or CLI.' }

    $create = Invoke-Broker @('create-empty', $archive) 'create' -KeepResident
    if (-not $create.Response.Success) { throw "Broker create-empty failed: $($create.Response.ErrorCode) $($create.Response.Message)" }
    $appBefore = @(Get-Process -Name Cfs.App -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
    $open = Invoke-Broker @('open', $archive) 'open'
    if (-not $open.Response.Success) { throw "Silent broker open failed: $($open.Response.ErrorCode) $($open.Response.Message)" }
    $mount = [string]$open.Response.MountPath
    if (-not (Test-Path -LiteralPath $mount)) { throw "Broker returned a missing mount: $mount" }
    $reuse = Invoke-Broker @('open', $archive) 'open-reuse'
    if (-not $reuse.Response.Success -or
        [string]$reuse.Response.MountPath -cne $mount -or
        [int]$reuse.Response.BrokerProcessId -ne [int]$open.Response.BrokerProcessId) {
        throw 'Repeated broker open did not reuse the exact broker process and mount.'
    }
    $status = Invoke-Broker @('status', $archive) 'status'
    if (-not $status.Response.Success -or [int]$status.Response.SessionCount -ne 1 -or [int]$status.Response.CreatedSessionCount -ne 1) { throw 'Broker status did not report exactly one created silent session.' }
    $appAfter = @(Get-Process -Name Cfs.App -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
    if (@($appAfter | Where-Object { $appBefore -notcontains $_ }).Count -ne 0) { throw 'Silent shell handler launched Cfs.App.' }

    $close = Invoke-Broker @('close', $archive) 'close'
    if (-not $close.Response.Success -or (Test-Path -LiteralPath $mount)) { throw 'Close CFS did not remove the silent broker mount.' }

    $sourceFolder = Join-Path $Workspace 'Folder to Compress'
    New-Item -ItemType Directory -Path (Join-Path $sourceFolder 'nested') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $sourceFolder 'nested\payload.txt'), 'packaged broker compression payload')
    $compressed = Invoke-Broker @('compress', $sourceFolder) 'compress'
    if (-not $compressed.Response.Success -or -not (Test-Path -LiteralPath ([string]$compressed.Response.OutputPath))) {
        throw "Packaged broker compression failed: $($compressed.Response.ErrorCode) $($compressed.Response.Message)"
    }
    & $cli validate ([string]$compressed.Response.OutputPath) | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Packaged broker compression output did not validate.' }
    $shutdown = Invoke-Broker @('shutdown') 'shutdown'
    if (-not $shutdown.Response.Success) { throw 'Controlled broker shutdown failed.' }
    Write-Host "SHELL_WORKFLOW brokerPid=$($open.Response.BrokerProcessId) sessionCount=$($status.Response.SessionCount) createdSessionCount=$($status.Response.CreatedSessionCount) identicalMount=True noCfsApp=True compressValid=True mountClean=True"
    Write-Host 'Shell workflow passed: broker-only open/reuse, ShellNew, Compress to CFS, and Close CFS commands are exact.'
}
finally {
    foreach ($processId in $ownedPids) {
        $candidate = Get-CimInstance Win32_Process -Filter "ProcessId=$processId" -ErrorAction SilentlyContinue
        if ($candidate -and $candidate.ExecutablePath -and [IO.Path]::GetFullPath($candidate.ExecutablePath) -eq [IO.Path]::GetFullPath($broker) -and $candidate.CommandLine -like "*$suffix*") {
            Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
        }
    }
    if ($mount -and (Test-Path -LiteralPath $mount)) {
        try {
            $ownerMarker = Join-Path $mount '.cfs-mount-session'
            $marker = if (Test-Path -LiteralPath $ownerMarker) { Get-Content -Raw -LiteralPath $ownerMarker } else { '' }
            if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($mount)) -eq [IO.Path]::GetFullPath($sessionsRoot) -and $marker -match '^[0-9a-fA-F]{32}$') {
                Remove-Item -LiteralPath $mount -Recurse -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath "$mount.cfs-session.json" -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath "$mount.cfs-candidate" -Force -ErrorAction SilentlyContinue
            }
        } catch { }
    }
    & $registrationScript -BrokerPath $broker -EmptyTemplatePath $template -RegistryBasePath $registryBase -Unregister 2>$null | Out-Null
    Remove-Item -LiteralPath "Registry::HKEY_CURRENT_USER\$registryBase" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $Workspace -Recurse -Force -ErrorAction SilentlyContinue
}
