param(
    [string]$AppPath,
    [switch]$Unregister,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if ($Unregister) {
    if ($DryRun) {
        Write-Output 'Would remove HKCU\Software\Classes\.cfs'
        Write-Output 'Would remove HKCU\Software\Classes\CFS.Archive'
        return
    }

    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree('Software\Classes\.cfs', $false)
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree('Software\Classes\CFS.Archive', $false)
    Write-Host '.cfs current-user association for CFS was removed.'
    return
}

if ([string]::IsNullOrWhiteSpace($AppPath)) {
    $repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
    $defaultDll = Join-Path $repoRoot 'src\Cfs.App\bin\Debug\net8.0-windows\Cfs.App.dll'
    $defaultExe = Join-Path $repoRoot 'src\Cfs.App\bin\Debug\net8.0-windows\Cfs.App.exe'

    if (Test-Path -LiteralPath $defaultExe) {
        $AppPath = $defaultExe
    }
    elseif (Test-Path -LiteralPath $defaultDll) {
        $AppPath = $defaultDll
    }
    else {
        throw "Pass -AppPath or build Cfs.App first."
    }
}

$resolvedAppPath = (Resolve-Path -LiteralPath $AppPath).Path
$extension = [System.IO.Path]::GetExtension($resolvedAppPath)

if ($extension.Equals('.exe', [System.StringComparison]::OrdinalIgnoreCase)) {
    $command = '"' + $resolvedAppPath + '" "%1"'
} elseif ($extension.Equals('.dll', [System.StringComparison]::OrdinalIgnoreCase)) {
    $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet)) {
        throw "dotnet.exe was not found at $dotnet. Pass a self-contained Cfs.App.exe as AppPath, or install the .NET Desktop Runtime."
    }

    $command = '"' + $dotnet + '" "' + $resolvedAppPath + '" "%1"'
} else {
    throw 'Expected a Cfs.App.exe or Cfs.App.dll.'
}

if ($DryRun) {
    Write-Output $command
    return
}

$extensionKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Classes\.cfs')
$extensionKey.SetValue($null, 'CFS.Archive')
$extensionKey.Dispose()

$typeKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Classes\CFS.Archive')
$typeKey.SetValue($null, 'CFS Archive')
$typeKey.Dispose()

$commandKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Classes\CFS.Archive\shell\open\command')
$commandKey.SetValue($null, $command)
$commandKey.Dispose()

Write-Host ".cfs files are now associated with CFS for the current user."
Write-Host $command
