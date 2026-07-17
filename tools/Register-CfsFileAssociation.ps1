param(
    [Alias('AppPath')]
    [string]$BrokerPath,
    [string]$EmptyTemplatePath,
    [string]$RegistryBasePath = 'Software\Classes',
    [switch]$Unregister,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$classesBase = $RegistryBasePath.TrimEnd('\')
function ClassKey([string]$suffix) { return $classesBase + '\' + $suffix.TrimStart('\') }
function Remove-EmptyKey([string]$keyPath) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($keyPath)
    if ($null -eq $key) { return }
    $empty = ($key.GetValueNames().Count -eq 0 -and $key.GetSubKeyNames().Count -eq 0)
    $key.Dispose()
    if ($empty) { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey($keyPath, $false) }
}

if ([string]::IsNullOrWhiteSpace($BrokerPath)) {
    $packaged = Join-Path $PSScriptRoot 'Cfs.Broker.exe'
    $developer = Join-Path $repoRoot 'src\Cfs.Broker\bin\Debug\net8.0-windows\Cfs.Broker.exe'
    if ($Unregister) { $BrokerPath = $packaged }
    elseif (Test-Path -LiteralPath $packaged) { $BrokerPath = $packaged }
    elseif (Test-Path -LiteralPath $developer) { $BrokerPath = $developer }
    else { throw 'Pass -BrokerPath or build Cfs.Broker first.' }
}

$resolvedBrokerPath = if ($Unregister) { [IO.Path]::GetFullPath($BrokerPath) } else { (Resolve-Path -LiteralPath $BrokerPath).Path }
if (-not [System.IO.Path]::GetFileName($resolvedBrokerPath).Equals('Cfs.Broker.exe', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Expected Cfs.Broker.exe. Cfs.App and Cfs.Cli are not shell handlers.'
}

if ([string]::IsNullOrWhiteSpace($EmptyTemplatePath)) {
    $EmptyTemplatePath = Join-Path $PSScriptRoot 'ShellNew\CFS-Empty.cfs'
}
$resolvedTemplatePath = [IO.Path]::GetFullPath($EmptyTemplatePath)
if (-not $Unregister -and -not (Test-Path -LiteralPath $resolvedTemplatePath)) {
    throw "The generated empty CFS template was not found at $resolvedTemplatePath."
}

$openCommand = '"' + $resolvedBrokerPath + '" open "%1"'
$compressCommand = '"' + $resolvedBrokerPath + '" compress "%1"'
$closeCommand = '"' + $resolvedBrokerPath + '" close "%1"'

if ($DryRun) {
    Write-Output "OPEN_COMMAND=$openCommand"
    Write-Output "SHELLNEW_FILENAME=$resolvedTemplatePath"
    Write-Output 'FOLDER_VERB_LABEL=Compress to CFS'
    Write-Output "FOLDER_VERB_COMMAND=$compressCommand"
    Write-Output 'CLOSE_VERB_LABEL=Close CFS'
    Write-Output "CLOSE_VERB_COMMAND=$closeCommand"
    if ($Unregister) { Write-Output 'UNREGISTER=ownership-checked' }
    return
}

if ($Unregister) {
    $openKeyPath = ClassKey 'CFS.Archive\shell\open\command'
    $openKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($openKeyPath)
    $registeredOpen = if ($null -ne $openKey) { [string]$openKey.GetValue($null) } else { '' }
    if ($null -ne $openKey) { $openKey.Dispose() }
    if ($registeredOpen.Equals($openCommand, [StringComparison]::OrdinalIgnoreCase)) {
        $ownedKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($openKeyPath, $true)
        $ownedKey.DeleteValue('', $false); $ownedKey.Dispose()
    }

    $closeVerbPath = ClassKey 'CFS.Archive\shell\CFS.Close'
    $closeCommandPath = $closeVerbPath + '\command'
    $closeCommandKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($closeCommandPath)
    $registeredClose = if ($null -ne $closeCommandKey) { [string]$closeCommandKey.GetValue($null) } else { '' }
    if ($null -ne $closeCommandKey) { $closeCommandKey.Dispose() }
    if ($registeredClose.Equals($closeCommand, [StringComparison]::OrdinalIgnoreCase)) {
        $ownedKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($closeCommandPath, $true)
        $ownedKey.DeleteValue('', $false); $ownedKey.Dispose()
    }
    $closeVerbKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($closeVerbPath, $true)
    if ($null -ne $closeVerbKey) {
        if ([string]$closeVerbKey.GetValue($null) -eq 'Close CFS') { $closeVerbKey.DeleteValue('', $false) }
        if ([string]$closeVerbKey.GetValue('Icon') -eq ($resolvedBrokerPath + ',0')) { $closeVerbKey.DeleteValue('Icon', $false) }
        $closeVerbKey.Dispose()
    }

    $typePath = ClassKey 'CFS.Archive'
    $typeKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($typePath, $true)
    if ($null -ne $typeKey) {
        if ([string]$typeKey.GetValue($null) -eq 'CFS Compressed Folder') { $typeKey.DeleteValue('', $false) }
        $typeKey.Dispose()
    }

    $shellNewPath = ClassKey '.cfs\ShellNew'
    $shellNew = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($shellNewPath)
    $registeredTemplate = if ($null -ne $shellNew) { [string]$shellNew.GetValue('FileName') } else { '' }
    if ($null -ne $shellNew) { $shellNew.Dispose() }
    if ($registeredTemplate.Equals($resolvedTemplatePath, [StringComparison]::OrdinalIgnoreCase)) {
        $ownedKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($shellNewPath, $true)
        $ownedKey.DeleteValue('FileName', $false); $ownedKey.Dispose()
    }

    $verbPath = ClassKey 'Directory\shell\CFS.Compress'
    $verbCommandKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($verbPath + '\command')
    $registeredCompress = if ($null -ne $verbCommandKey) { [string]$verbCommandKey.GetValue($null) } else { '' }
    if ($null -ne $verbCommandKey) { $verbCommandKey.Dispose() }
    if ($registeredCompress.Equals($compressCommand, [StringComparison]::OrdinalIgnoreCase)) {
        $ownedKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($verbPath + '\command', $true)
        $ownedKey.DeleteValue('', $false); $ownedKey.Dispose()
    }
    $verbKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($verbPath, $true)
    if ($null -ne $verbKey) {
        if ([string]$verbKey.GetValue($null) -eq 'Compress to CFS') { $verbKey.DeleteValue('', $false) }
        if ([string]$verbKey.GetValue('Icon') -eq ($resolvedBrokerPath + ',0')) { $verbKey.DeleteValue('Icon', $false) }
        $verbKey.Dispose()
    }

    $extensionPath = ClassKey '.cfs'
    $extension = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($extensionPath, $true)
    if ($null -ne $extension) {
        if ([string]$extension.GetValue($null) -eq 'CFS.Archive') { $extension.DeleteValue('', $false) }
        $extension.Dispose()
    }
    foreach ($key in @(
        $openKeyPath, (ClassKey 'CFS.Archive\shell\open'), $closeCommandPath, $closeVerbPath, (ClassKey 'CFS.Archive\shell'), (ClassKey 'CFS.Archive\DefaultIcon'),
        $typePath, ($verbPath + '\command'), $verbPath, (ClassKey 'Directory\shell'), (ClassKey 'Directory'),
        $shellNewPath, $extensionPath)) { Remove-EmptyKey $key }
    Write-Host 'CFS-owned current-user shell entries were removed when their exact values matched.'
    return
}

$extensionKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey((ClassKey '.cfs'))
$extensionKey.SetValue($null, 'CFS.Archive'); $extensionKey.Dispose()
$typeKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey((ClassKey 'CFS.Archive'))
$typeKey.SetValue($null, 'CFS Compressed Folder'); $typeKey.Dispose()
$commandKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey((ClassKey 'CFS.Archive\shell\open\command'))
$commandKey.SetValue($null, $openCommand); $commandKey.Dispose()
$shellNewKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey((ClassKey '.cfs\ShellNew'))
$shellNewKey.SetValue('FileName', $resolvedTemplatePath); $shellNewKey.Dispose()
$verbKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey((ClassKey 'Directory\shell\CFS.Compress'))
$verbKey.SetValue($null, 'Compress to CFS'); $verbKey.SetValue('Icon', $resolvedBrokerPath + ',0'); $verbKey.Dispose()
$verbCommandKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey((ClassKey 'Directory\shell\CFS.Compress\command'))
$verbCommandKey.SetValue($null, $compressCommand); $verbCommandKey.Dispose()
$closeVerbKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey((ClassKey 'CFS.Archive\shell\CFS.Close'))
$closeVerbKey.SetValue($null, 'Close CFS'); $closeVerbKey.SetValue('Icon', $resolvedBrokerPath + ',0'); $closeVerbKey.Dispose()
$closeCommandKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey((ClassKey 'CFS.Archive\shell\CFS.Close\command'))
$closeCommandKey.SetValue($null, $closeCommand); $closeCommandKey.Dispose()
Write-Host 'CFS shell open, ShellNew, Compress to CFS, and Close CFS entries were registered for the current user.'
Write-Host $openCommand
Write-Host $compressCommand
Write-Host $closeCommand
