param(
    [Alias('AppPath', 'BrokerPath')]
    [string]$CommandClientPath,
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

if ([string]::IsNullOrWhiteSpace($CommandClientPath)) {
    $packaged = Join-Path $PSScriptRoot 'Cfs.CommandClient.exe'
    $developer = Join-Path $repoRoot 'src\Cfs.CommandClient\bin\Debug\net8.0-windows\Cfs.CommandClient.exe'
    if ($Unregister) { $CommandClientPath = $packaged }
    elseif (Test-Path -LiteralPath $packaged) { $CommandClientPath = $packaged }
    elseif (Test-Path -LiteralPath $developer) { $CommandClientPath = $developer }
    else { throw 'Pass -CommandClientPath or build Cfs.CommandClient first.' }
}

$resolvedCommandClientPath = if ($Unregister) { [IO.Path]::GetFullPath($CommandClientPath) } else { (Resolve-Path -LiteralPath $CommandClientPath).Path }
if (-not [System.IO.Path]::GetFileName($resolvedCommandClientPath).Equals('Cfs.CommandClient.exe', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Expected Cfs.CommandClient.exe. Cfs.Broker, Cfs.App, and Cfs.Cli are not shell handlers.'
}

if ([string]::IsNullOrWhiteSpace($EmptyTemplatePath)) {
    $EmptyTemplatePath = Join-Path $PSScriptRoot 'ShellNew\CFS-Empty.cfs'
}
$resolvedTemplatePath = [IO.Path]::GetFullPath($EmptyTemplatePath)
if (-not $Unregister -and -not (Test-Path -LiteralPath $resolvedTemplatePath)) {
    throw "The generated empty CFS template was not found at $resolvedTemplatePath."
}

$openCommand = '"' + $resolvedCommandClientPath + '" open "%1"'
$compressCommand = '"' + $resolvedCommandClientPath + '" compress "%1"'
$createHereCommand = '"' + $resolvedCommandClientPath + '" create-here "%V"'
$createInFolderCommand = '"' + $resolvedCommandClientPath + '" create-here "%1"'
$closeCommand = '"' + $resolvedCommandClientPath + '" close "%1"'
$extractCommand = '"' + $resolvedCommandClientPath + '" extract "%1"'
$commitCommand = '"' + $resolvedCommandClientPath + '" commit "%1"'
$discardCommand = '"' + $resolvedCommandClientPath + '" discard "%1"'
$statusCommand = '"' + $resolvedCommandClientPath + '" status "%1"'
$recoverCommand = '"' + $resolvedCommandClientPath + '" recover "%1"'
$recoveryStatusCommand = '"' + $resolvedCommandClientPath + '" recovery-status "%1"'
$discardRecoveryCommand = '"' + $resolvedCommandClientPath + '" discard-recovery "%1"'
$openReadOnlyCommand = '"' + $resolvedCommandClientPath + '" open-readonly "%1"'

if ($DryRun) {
    Write-Output "OPEN_COMMAND=$openCommand"
    Write-Output "SHELLNEW_FILENAME=$resolvedTemplatePath"
    Write-Output 'FOLDER_VERB_LABEL=Compress to CFS'
    Write-Output "FOLDER_VERB_COMMAND=$compressCommand"
    Write-Output 'CREATE_HERE_VERB_LABEL=Create empty CFS archive here'
    Write-Output "CREATE_HERE_VERB_COMMAND=$createHereCommand"
    Write-Output 'CREATE_IN_FOLDER_VERB_LABEL=Create empty CFS archive inside'
    Write-Output "CREATE_IN_FOLDER_VERB_COMMAND=$createInFolderCommand"
    Write-Output 'CLOSE_VERB_LABEL=Close CFS'
    Write-Output "CLOSE_VERB_COMMAND=$closeCommand"
    Write-Output 'EXTRACT_VERB_LABEL=Extract entire CFS archive'
    Write-Output "EXTRACT_VERB_COMMAND=$extractCommand"
    Write-Output 'COMMIT_VERB_LABEL=Commit pending changes'
    Write-Output "COMMIT_VERB_COMMAND=$commitCommand"
    Write-Output 'DISCARD_VERB_LABEL=Discard pending changes'
    Write-Output "DISCARD_VERB_COMMAND=$discardCommand"
    Write-Output 'STATUS_VERB_LABEL=Show CFS status'
    Write-Output "STATUS_VERB_COMMAND=$statusCommand"
    Write-Output 'RECOVER_VERB_LABEL=Open recovery workspace'
    Write-Output "RECOVER_VERB_COMMAND=$recoverCommand"
    Write-Output 'RECOVERY_STATUS_VERB_LABEL=Show recovery status'
    Write-Output "RECOVERY_STATUS_VERB_COMMAND=$recoveryStatusCommand"
    Write-Output 'DISCARD_RECOVERY_VERB_LABEL=Discard recovery data'
    Write-Output "DISCARD_RECOVERY_VERB_COMMAND=$discardRecoveryCommand"
    Write-Output 'OPEN_READONLY_VERB_LABEL=Open read-only (full extraction)'
    Write-Output "OPEN_READONLY_VERB_COMMAND=$openReadOnlyCommand"
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
        if ([string]$closeVerbKey.GetValue('Icon') -eq ($resolvedCommandClientPath + ',0')) { $closeVerbKey.DeleteValue('Icon', $false) }
        $closeVerbKey.Dispose()
    }

    $extractVerbPath = ClassKey 'CFS.Archive\shell\CFS.Extract'
    $extractCommandPath = $extractVerbPath + '\command'
    $extractCommandKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($extractCommandPath)
    $registeredExtract = if ($null -ne $extractCommandKey) { [string]$extractCommandKey.GetValue($null) } else { '' }
    if ($null -ne $extractCommandKey) { $extractCommandKey.Dispose() }
    if ($registeredExtract.Equals($extractCommand, [StringComparison]::OrdinalIgnoreCase)) {
        $ownedKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($extractCommandPath, $true)
        $ownedKey.DeleteValue('', $false); $ownedKey.Dispose()
    }
    $extractVerbKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($extractVerbPath, $true)
    if ($null -ne $extractVerbKey) {
        if ([string]$extractVerbKey.GetValue($null) -eq 'Extract entire CFS archive') { $extractVerbKey.DeleteValue('', $false) }
        if ([string]$extractVerbKey.GetValue('Icon') -eq ($resolvedCommandClientPath + ',0')) { $extractVerbKey.DeleteValue('Icon', $false) }
        $extractVerbKey.Dispose()
    }
    foreach ($ownedVerb in @(
        @{ Name = 'CFS.Commit'; Command = $commitCommand; Label = 'Commit pending changes' },
        @{ Name = 'CFS.Discard'; Command = $discardCommand; Label = 'Discard pending changes' },
        @{ Name = 'CFS.Status'; Command = $statusCommand; Label = 'Show CFS status' },
        @{ Name = 'CFS.Recover'; Command = $recoverCommand; Label = 'Open recovery workspace' },
        @{ Name = 'CFS.RecoveryStatus'; Command = $recoveryStatusCommand; Label = 'Show recovery status' },
        @{ Name = 'CFS.DiscardRecovery'; Command = $discardRecoveryCommand; Label = 'Discard recovery data' },
        @{ Name = 'CFS.OpenReadOnly'; Command = $openReadOnlyCommand; Label = 'Open read-only (full extraction)' })) {
        $ownedVerbPath = ClassKey ('CFS.Archive\shell\' + $ownedVerb.Name)
        $ownedCommandPath = $ownedVerbPath + '\command'
        $ownedCommandKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($ownedCommandPath)
        $registeredCommand = if ($null -ne $ownedCommandKey) { [string]$ownedCommandKey.GetValue($null) } else { '' }
        if ($null -ne $ownedCommandKey) { $ownedCommandKey.Dispose() }
        if ($registeredCommand.Equals($ownedVerb.Command, [StringComparison]::OrdinalIgnoreCase)) {
            $writeCommandKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($ownedCommandPath, $true)
            $writeCommandKey.DeleteValue('', $false); $writeCommandKey.Dispose()
        }
        $ownedVerbKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($ownedVerbPath, $true)
        if ($null -ne $ownedVerbKey) {
            if ([string]$ownedVerbKey.GetValue($null) -eq $ownedVerb.Label) { $ownedVerbKey.DeleteValue('', $false) }
            if ([string]$ownedVerbKey.GetValue('Icon') -eq ($resolvedCommandClientPath + ',0')) { $ownedVerbKey.DeleteValue('Icon', $false) }
            $ownedVerbKey.Dispose()
        }
        Remove-EmptyKey $ownedCommandPath
        Remove-EmptyKey $ownedVerbPath
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
        if ([string]$verbKey.GetValue('Icon') -eq ($resolvedCommandClientPath + ',0')) { $verbKey.DeleteValue('Icon', $false) }
        $verbKey.Dispose()
    }

    $createVerbPaths = @(
        @{ Path = ClassKey 'Directory\Background\shell\CFS.Create'; Command = $createHereCommand; Label = 'Create empty CFS archive here' },
        @{ Path = ClassKey 'Directory\shell\CFS.Create'; Command = $createInFolderCommand; Label = 'Create empty CFS archive inside' })
    foreach ($createVerb in $createVerbPaths) {
        $createCommandPath = $createVerb.Path + '\command'
        $createCommandKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($createCommandPath)
        $registeredCreate = if ($null -ne $createCommandKey) { [string]$createCommandKey.GetValue($null) } else { '' }
        if ($null -ne $createCommandKey) { $createCommandKey.Dispose() }
        if ($registeredCreate.Equals($createVerb.Command, [StringComparison]::OrdinalIgnoreCase)) {
            $ownedKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($createCommandPath, $true)
            $ownedKey.DeleteValue('', $false); $ownedKey.Dispose()
        }
        $createVerbKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($createVerb.Path, $true)
        if ($null -ne $createVerbKey) {
            if ([string]$createVerbKey.GetValue($null) -eq $createVerb.Label) { $createVerbKey.DeleteValue('', $false) }
            if ([string]$createVerbKey.GetValue('Icon') -eq ($resolvedCommandClientPath + ',0')) { $createVerbKey.DeleteValue('Icon', $false) }
            $createVerbKey.Dispose()
        }
        Remove-EmptyKey $createCommandPath
        Remove-EmptyKey $createVerb.Path
    }

    $extensionPath = ClassKey '.cfs'
    $extension = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($extensionPath, $true)
    if ($null -ne $extension) {
        if ([string]$extension.GetValue($null) -eq 'CFS.Archive') { $extension.DeleteValue('', $false) }
        $extension.Dispose()
    }
    foreach ($key in @(
        $openKeyPath, (ClassKey 'CFS.Archive\shell\open'), $closeCommandPath, $closeVerbPath, $extractCommandPath, $extractVerbPath, (ClassKey 'CFS.Archive\shell'), (ClassKey 'CFS.Archive\DefaultIcon'),
        $typePath, ($verbPath + '\command'), $verbPath,
        (ClassKey 'Directory\Background\shell\CFS.Create\command'), (ClassKey 'Directory\Background\shell\CFS.Create'),
        (ClassKey 'Directory\Background\shell'), (ClassKey 'Directory\Background'),
        (ClassKey 'Directory\shell\CFS.Create\command'), (ClassKey 'Directory\shell\CFS.Create'),
        (ClassKey 'Directory\shell'), (ClassKey 'Directory'),
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
$createHereVerbKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey((ClassKey 'Directory\Background\shell\CFS.Create'))
$createHereVerbKey.SetValue($null, 'Create empty CFS archive here'); $createHereVerbKey.SetValue('Icon', $resolvedCommandClientPath + ',0'); $createHereVerbKey.Dispose()
$createHereCommandKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey((ClassKey 'Directory\Background\shell\CFS.Create\command'))
$createHereCommandKey.SetValue($null, $createHereCommand); $createHereCommandKey.Dispose()
$createInFolderVerbKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey((ClassKey 'Directory\shell\CFS.Create'))
$createInFolderVerbKey.SetValue($null, 'Create empty CFS archive inside'); $createInFolderVerbKey.SetValue('Icon', $resolvedCommandClientPath + ',0'); $createInFolderVerbKey.Dispose()
$createInFolderCommandKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey((ClassKey 'Directory\shell\CFS.Create\command'))
$createInFolderCommandKey.SetValue($null, $createInFolderCommand); $createInFolderCommandKey.Dispose()
$verbKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey((ClassKey 'Directory\shell\CFS.Compress'))
$verbKey.SetValue($null, 'Compress to CFS'); $verbKey.SetValue('Icon', $resolvedCommandClientPath + ',0'); $verbKey.Dispose()
$verbCommandKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey((ClassKey 'Directory\shell\CFS.Compress\command'))
$verbCommandKey.SetValue($null, $compressCommand); $verbCommandKey.Dispose()
$closeVerbKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey((ClassKey 'CFS.Archive\shell\CFS.Close'))
$closeVerbKey.SetValue($null, 'Close CFS'); $closeVerbKey.SetValue('Icon', $resolvedCommandClientPath + ',0'); $closeVerbKey.Dispose()
$closeCommandKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey((ClassKey 'CFS.Archive\shell\CFS.Close\command'))
$closeCommandKey.SetValue($null, $closeCommand); $closeCommandKey.Dispose()
$extractVerbKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey((ClassKey 'CFS.Archive\shell\CFS.Extract'))
$extractVerbKey.SetValue($null, 'Extract entire CFS archive'); $extractVerbKey.SetValue('Icon', $resolvedCommandClientPath + ',0'); $extractVerbKey.Dispose()
$extractCommandKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey((ClassKey 'CFS.Archive\shell\CFS.Extract\command'))
$extractCommandKey.SetValue($null, $extractCommand); $extractCommandKey.Dispose()
foreach ($verb in @(
    @{ Name = 'CFS.Commit'; Command = $commitCommand; Label = 'Commit pending changes' },
    @{ Name = 'CFS.Discard'; Command = $discardCommand; Label = 'Discard pending changes' },
    @{ Name = 'CFS.Status'; Command = $statusCommand; Label = 'Show CFS status' },
    @{ Name = 'CFS.Recover'; Command = $recoverCommand; Label = 'Open recovery workspace' },
    @{ Name = 'CFS.RecoveryStatus'; Command = $recoveryStatusCommand; Label = 'Show recovery status' },
    @{ Name = 'CFS.DiscardRecovery'; Command = $discardRecoveryCommand; Label = 'Discard recovery data' },
    @{ Name = 'CFS.OpenReadOnly'; Command = $openReadOnlyCommand; Label = 'Open read-only (full extraction)' })) {
    $newVerbKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey((ClassKey ('CFS.Archive\shell\' + $verb.Name)))
    $newVerbKey.SetValue($null, $verb.Label); $newVerbKey.SetValue('Icon', $resolvedCommandClientPath + ',0'); $newVerbKey.Dispose()
    $newCommandKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey((ClassKey ('CFS.Archive\shell\' + $verb.Name + '\command')))
    $newCommandKey.SetValue($null, $verb.Command); $newCommandKey.Dispose()
}
Write-Host 'CFS shell open, create, ShellNew, Compress to CFS, and Close CFS entries were registered for the current user.'
Write-Host $openCommand
Write-Host $compressCommand
Write-Host $closeCommand
