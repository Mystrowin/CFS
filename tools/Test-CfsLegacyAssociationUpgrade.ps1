[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SetupPath,
    [string]$Workspace
)

# This test intentionally exercises the real merged HKCR view. It snapshots and
# restores the two affected HKCU subtrees byte-for-byte and installs CFS into an
# isolated Temp directory. No FileExts/UserChoice state is modified.
$ErrorActionPreference = 'Stop'
$SetupPath = (Resolve-Path -LiteralPath $SetupPath).Path
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$legacyRoot = Join-Path $repoRoot 'dist\CFS-0.2.0-Beta-win-x64'
$legacyApp = Join-Path $legacyRoot 'Cfs.App.exe'
$legacyCore = Join-Path $legacyRoot 'Cfs.Core.dll'
$legacyBroker = Join-Path $legacyRoot 'Cfs.Broker.exe'
$legacyTemplate = Join-Path $legacyRoot 'ShellNew\CFS-Empty.cfs'
foreach ($path in @($legacyApp, $legacyCore, $legacyBroker, $legacyTemplate)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Legacy CFS proof input missing: $path" }
}

if ([string]::IsNullOrWhiteSpace($Workspace)) {
    $Workspace = Join-Path $env:TEMP ('cfs-legacy-upgrade-' + [guid]::NewGuid().ToString('N'))
}
$Workspace = [IO.Path]::GetFullPath($Workspace)
$installPath = Join-Path $Workspace 'Installed CFS 0.2'
$foreignInstallPath = Join-Path $Workspace 'Installed CFS 0.2 Foreign Case'
$extensionReg = Join-Path $Workspace 'pre-extension.reg'
$progIdReg = Join-Path $Workspace 'pre-progid.reg'
$compressReg = Join-Path $Workspace 'pre-compress.reg'
$installLog = Join-Path $Workspace 'install.log'
$extensionPath = 'Software\Classes\.cfs'
$progIdPath = 'Software\Classes\CFS.Archive'
$openPath = $progIdPath + '\shell\open\command'
$foreignSiblingPath = $progIdPath + '\shell\open\command\ForeignSibling'
$closePath = $progIdPath + '\shell\CFS.Close'
$closeCommandPath = $closePath + '\command'
$shellNewPath = $extensionPath + '\ShellNew'
$compressPath = 'Software\Classes\Directory\shell\CFS.Compress'
$compressCommandPath = $compressPath + '\command'
$extensionExisted = $false
$progIdExisted = $false
$compressExisted = $false
$uninstaller = $null
$foreignUninstaller = $null

function Test-UserKey([string]$Path) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($Path)
    if ($null -eq $key) { return $false }
    $key.Dispose()
    return $true
}

function Get-UserValue([string]$Path, [string]$Name = '') {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($Path)
    if ($null -eq $key) { return $null }
    try { return $key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
    finally { $key.Dispose() }
}

function Set-UserValue([string]$Path, [string]$Name, [string]$Value) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($Path)
    try { $key.SetValue($Name, $Value, [Microsoft.Win32.RegistryValueKind]::String) }
    finally { $key.Dispose() }
}

function Remove-UserTree([string]$Path) {
    try { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($Path, $false) } catch { }
}

function Assert-Equal($Actual, $Expected, [string]$Name) {
    if ([string]$Actual -cne [string]$Expected) {
        throw "$Name mismatch. Expected '$Expected'; actual '$Actual'."
    }
}

function Start-And-Wait([string]$FilePath, [string[]]$Arguments, [int]$TimeoutSeconds = 120) {
    $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -PassThru
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        throw "Timed out waiting for PID $($process.Id): $FilePath"
    }
    if ($process.ExitCode -ne 0) { throw "$FilePath exited with $($process.ExitCode)." }
    return $process.ExitCode
}

New-Item -ItemType Directory -Path $Workspace -Force | Out-Null
try {
    $extensionExisted = Test-UserKey $extensionPath
    $progIdExisted = Test-UserKey $progIdPath
    $compressExisted = Test-UserKey $compressPath
    if ($extensionExisted) { & reg.exe export 'HKCU\Software\Classes\.cfs' $extensionReg /y | Out-Null }
    if ($progIdExisted) { & reg.exe export 'HKCU\Software\Classes\CFS.Archive' $progIdReg /y | Out-Null }
    if ($compressExisted) { & reg.exe export 'HKCU\Software\Classes\Directory\shell\CFS.Compress' $compressReg /y | Out-Null }

    Remove-UserTree $extensionPath
    Remove-UserTree $progIdPath
    Remove-UserTree $compressPath
    $legacyCommand = '"' + $legacyApp + '" "%1"'
    $legacyCloseCommand = '"' + $legacyBroker + '" close "%1"'
    $legacyCompressCommand = '"' + $legacyBroker + '" compress "%1"'
    Set-UserValue $extensionPath '' 'CFS.Archive'
    Set-UserValue $progIdPath '' 'CFS Archive'
    Set-UserValue $openPath '' $legacyCommand
    Set-UserValue $openPath 'ForeignNamed' 'preserve-named-value'
    Set-UserValue $foreignSiblingPath '' 'preserve-foreign-subkey'
    Set-UserValue $closePath '' 'Close CFS'
    Set-UserValue $closePath 'Icon' ($legacyBroker + ',0')
    Set-UserValue $closeCommandPath '' $legacyCloseCommand
    Set-UserValue $closeCommandPath 'ForeignNamed' 'preserve-close-value'
    Set-UserValue $shellNewPath 'FileName' $legacyTemplate
    Set-UserValue $shellNewPath 'ForeignNamed' 'preserve-shellnew-value'
    Set-UserValue $compressPath '' 'Compress to CFS'
    Set-UserValue $compressPath 'Icon' ($legacyBroker + ',0')
    Set-UserValue $compressCommandPath '' $legacyCompressCommand
    Set-UserValue $compressCommandPath 'ForeignNamed' 'preserve-compress-value'
    Write-Output "PRE_HKCU_EXTENSION=$(Get-UserValue $extensionPath)"
    Write-Output "PRE_HKCU_OPEN=$(Get-UserValue $openPath)"

    $installArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TASKS=""', ('/DIR="' + $installPath + '"'), ('/LOG="' + $installLog + '"'))
    $installExit = Start-And-Wait $SetupPath $installArgs
    $uninstaller = Join-Path $installPath 'unins000.exe'
    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) { throw 'Isolated installer did not produce unins000.exe.' }

    $expectedBrokerCommand = '"' + (Join-Path $installPath 'Cfs.Broker.exe') + '" open "%1"'
    $expectedCloseCommand = '"' + (Join-Path $installPath 'Cfs.Broker.exe') + '" close "%1"'
    $expectedCompressCommand = '"' + (Join-Path $installPath 'Cfs.Broker.exe') + '" compress "%1"'
    $expectedTemplate = Join-Path $installPath 'ShellNew\CFS-Empty.cfs'
    $effectiveCommand = [string](Get-ItemPropertyValue -LiteralPath 'Registry::HKEY_CLASSES_ROOT\CFS.Archive\shell\open\command' -Name '(default)')
    $effectiveClose = [string](Get-ItemPropertyValue -LiteralPath 'Registry::HKEY_CLASSES_ROOT\CFS.Archive\shell\CFS.Close\command' -Name '(default)')
    $effectiveCompress = [string](Get-ItemPropertyValue -LiteralPath 'Registry::HKEY_CLASSES_ROOT\Directory\shell\CFS.Compress\command' -Name '(default)')
    $effectiveTemplate = [string](Get-ItemPropertyValue -LiteralPath 'Registry::HKEY_CLASSES_ROOT\.cfs\ShellNew' -Name 'FileName')
    Assert-Equal $effectiveCommand $expectedBrokerCommand 'effective HKCR open command after upgrade'
    Assert-Equal $effectiveClose $expectedCloseCommand 'effective HKCR Close CFS command after upgrade'
    Assert-Equal $effectiveCompress $expectedCompressCommand 'effective HKCR Compress command after upgrade'
    Assert-Equal $effectiveTemplate $expectedTemplate 'effective HKCR ShellNew template after upgrade'
    Assert-Equal (Get-UserValue $extensionPath) $null 'migrated HKCU extension default'
    Assert-Equal (Get-UserValue $progIdPath) $null 'migrated HKCU ProgID description'
    Assert-Equal (Get-UserValue $openPath) $null 'migrated HKCU legacy open command'
    Assert-Equal (Get-UserValue $openPath 'ForeignNamed') 'preserve-named-value' 'foreign named command value'
    Assert-Equal (Get-UserValue $foreignSiblingPath) 'preserve-foreign-subkey' 'foreign command subkey'
    Assert-Equal (Get-UserValue $closeCommandPath) $null 'migrated HKCU legacy Close command'
    Assert-Equal (Get-UserValue $closeCommandPath 'ForeignNamed') 'preserve-close-value' 'foreign Close command value'
    Assert-Equal (Get-UserValue $shellNewPath 'FileName') $null 'migrated HKCU legacy ShellNew template'
    Assert-Equal (Get-UserValue $shellNewPath 'ForeignNamed') 'preserve-shellnew-value' 'foreign ShellNew value'
    Assert-Equal (Get-UserValue $compressCommandPath) $null 'migrated HKCU legacy Compress command'
    Assert-Equal (Get-UserValue $compressCommandPath 'ForeignNamed') 'preserve-compress-value' 'foreign Compress command value'
    Write-Output "INSTALL_EXIT=$installExit"
    Write-Output "POST_INSTALL_EFFECTIVE_OPEN=$effectiveCommand"
    Write-Output "POST_INSTALL_EFFECTIVE_CLOSE=$effectiveClose"
    Write-Output "POST_INSTALL_EFFECTIVE_SHELLNEW=$effectiveTemplate"
    Write-Output "POST_INSTALL_EFFECTIVE_COMPRESS=$effectiveCompress"
    Write-Output 'POST_INSTALL_LEGACY_DEFAULTS_REMOVED=True'
    Write-Output 'POST_INSTALL_FOREIGN_SIBLINGS_PRESERVED=True'

    $foreignExtension = 'Foreign.Archive'
    $foreignDescription = 'Foreign replacement preserved by uninstall'
    $foreignCommand = '"C:\Foreign Handler\Foreign.exe" "%1"'
    Set-UserValue $extensionPath '' $foreignExtension
    Set-UserValue $progIdPath '' $foreignDescription
    Set-UserValue $openPath '' $foreignCommand
    Set-UserValue $closeCommandPath '' $foreignCommand
    Set-UserValue $shellNewPath 'FileName' 'C:\Foreign Template\Foreign.cfs'
    Set-UserValue $compressCommandPath '' $foreignCommand
    $uninstallExit = Start-And-Wait $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    Assert-Equal (Get-UserValue $extensionPath) $foreignExtension 'foreign extension after uninstall'
    Assert-Equal (Get-UserValue $progIdPath) $foreignDescription 'foreign ProgID after uninstall'
    Assert-Equal (Get-UserValue $openPath) $foreignCommand 'foreign command after uninstall'
    Assert-Equal (Get-UserValue $openPath 'ForeignNamed') 'preserve-named-value' 'foreign named value after uninstall'
    Assert-Equal (Get-UserValue $foreignSiblingPath) 'preserve-foreign-subkey' 'foreign subkey after uninstall'
    Assert-Equal (Get-UserValue $closeCommandPath) $foreignCommand 'foreign Close command after uninstall'
    Assert-Equal (Get-UserValue $closeCommandPath 'ForeignNamed') 'preserve-close-value' 'foreign Close value after uninstall'
    Assert-Equal (Get-UserValue $shellNewPath 'FileName') 'C:\Foreign Template\Foreign.cfs' 'foreign ShellNew after uninstall'
    Assert-Equal (Get-UserValue $shellNewPath 'ForeignNamed') 'preserve-shellnew-value' 'foreign ShellNew value after uninstall'
    Assert-Equal (Get-UserValue $compressCommandPath) $foreignCommand 'foreign Compress command after uninstall'
    Assert-Equal (Get-UserValue $compressCommandPath 'ForeignNamed') 'preserve-compress-value' 'foreign Compress value after uninstall'
    Write-Output "UNINSTALL_EXIT=$uninstallExit"
    Write-Output "POST_UNINSTALL_HKCU_OPEN=$(Get-UserValue $openPath)"
    Write-Output 'POST_UNINSTALL_FOREIGN_VALUES_PRESERVED=True'

    $foreignInstallArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TASKS=""', ('/DIR="' + $foreignInstallPath + '"'), ('/LOG="' + (Join-Path $Workspace 'foreign-install.log') + '"'))
    $foreignInstallExit = Start-And-Wait $SetupPath $foreignInstallArgs
    $foreignUninstaller = Join-Path $foreignInstallPath 'unins000.exe'
    if (-not (Test-Path -LiteralPath $foreignUninstaller -PathType Leaf)) { throw 'Foreign-case installer did not produce unins000.exe.' }
    Assert-Equal (Get-UserValue $extensionPath) $foreignExtension 'foreign extension after negative-case install'
    Assert-Equal (Get-UserValue $progIdPath) $foreignDescription 'foreign ProgID after negative-case install'
    Assert-Equal (Get-UserValue $openPath) $foreignCommand 'foreign open command after negative-case install'
    Assert-Equal (Get-UserValue $closeCommandPath) $foreignCommand 'foreign Close command after negative-case install'
    Assert-Equal (Get-UserValue $shellNewPath 'FileName') 'C:\Foreign Template\Foreign.cfs' 'foreign ShellNew after negative-case install'
    Assert-Equal (Get-UserValue $compressCommandPath) $foreignCommand 'foreign Compress command after negative-case install'
    $foreignUninstallExit = Start-And-Wait $foreignUninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    Assert-Equal (Get-UserValue $extensionPath) $foreignExtension 'foreign extension after negative-case uninstall'
    Assert-Equal (Get-UserValue $progIdPath) $foreignDescription 'foreign ProgID after negative-case uninstall'
    Assert-Equal (Get-UserValue $openPath) $foreignCommand 'foreign open command after negative-case uninstall'
    Assert-Equal (Get-UserValue $closeCommandPath) $foreignCommand 'foreign Close command after negative-case uninstall'
    Assert-Equal (Get-UserValue $shellNewPath 'FileName') 'C:\Foreign Template\Foreign.cfs' 'foreign ShellNew after negative-case uninstall'
    Assert-Equal (Get-UserValue $compressCommandPath) $foreignCommand 'foreign Compress command after negative-case uninstall'
    Write-Output "FOREIGN_CASE_INSTALL_EXIT=$foreignInstallExit"
    Write-Output "FOREIGN_CASE_UNINSTALL_EXIT=$foreignUninstallExit"
    Write-Output 'FOREIGN_ASSOCIATION_UNCHANGED=True'
    Write-Output 'LEGACY_ASSOCIATION_UPGRADE_PASS=True'
}
finally {
    if ($uninstaller -and (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
        try { Start-And-Wait $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') | Out-Null } catch { }
    }
    if ($foreignUninstaller -and (Test-Path -LiteralPath $foreignUninstaller -PathType Leaf)) {
        try { Start-And-Wait $foreignUninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') | Out-Null } catch { }
    }
    Remove-UserTree $extensionPath
    Remove-UserTree $progIdPath
    Remove-UserTree $compressPath
    if ($extensionExisted -and (Test-Path -LiteralPath $extensionReg)) { & reg.exe import $extensionReg | Out-Null }
    if ($progIdExisted -and (Test-Path -LiteralPath $progIdReg)) { & reg.exe import $progIdReg | Out-Null }
    if ($compressExisted -and (Test-Path -LiteralPath $compressReg)) { & reg.exe import $compressReg | Out-Null }
    Write-Output "RESTORE_EXTENSION_EXISTS=$(Test-UserKey $extensionPath)"
    Write-Output "RESTORE_PROGID_EXISTS=$(Test-UserKey $progIdPath)"
    Write-Output "RESTORE_COMPRESS_EXISTS=$(Test-UserKey $compressPath)"
    Write-Output "RESTORE_EFFECTIVE_OPEN=$([string](Get-ItemPropertyValue -LiteralPath 'Registry::HKEY_CLASSES_ROOT\CFS.Archive\shell\open\command' -Name '(default)' -ErrorAction SilentlyContinue))"
}
