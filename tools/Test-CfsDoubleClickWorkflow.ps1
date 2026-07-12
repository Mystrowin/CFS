$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win32 {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hWnd, EnumWindowsProc cb, IntPtr lp);
    [DllImport("user32.dll")]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    public static string GetText(IntPtr hWnd) {
        var text = new System.Text.StringBuilder(512);
        GetWindowText(hWnd, text, text.Capacity);
        return text.ToString();
    }
    public static IntPtr FindWindow(uint pid, string title) {
        IntPtr result = IntPtr.Zero;
        EnumWindows((h,l) => {
            uint windowPid;
            GetWindowThreadProcessId(h, out windowPid);
            if (windowPid == pid && IsWindowVisible(h) && GetText(h) == title) {
                result = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }
    public static IntPtr FindChildByText(IntPtr parent, string contains) {
        IntPtr result = IntPtr.Zero;
        EnumChildWindows(parent, (h,l) => {
            if (GetText(h).Contains(contains)) {
                result = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
'@

function Get-CfsAppProcesses {
    $direct = Get-Process Cfs.App -ErrorAction SilentlyContinue
    $dotnet = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like '*Cfs.App.dll*' } |
        ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
    @($direct) + @($dotnet) | Where-Object { $_ }
}

function Get-CfsAppProcessIds {
    Get-CfsAppProcesses | Select-Object -ExpandProperty Id
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$cli = Join-Path $repoRoot 'src\Cfs.Cli\bin\Debug\net8.0\Cfs.Cli.dll'
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
$workspace = Join-Path $env:TEMP ('cfs-gui-smoke-' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $workspace 'source'
$archive = Join-Path $workspace 'Smoke.cfs'
$mountRoot = Join-Path $env:TEMP 'CFS\mounts'

Get-ChildItem -LiteralPath $mountRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like 'Smoke-*' } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Path $source | Out-Null
New-Item -ItemType Directory -Path (Join-Path $source 'folder') | Out-Null
[System.IO.File]::WriteAllText((Join-Path $source 'overwrite.txt'), 'old')
[System.IO.File]::WriteAllText((Join-Path $source 'delete.txt'), 'delete')
[System.IO.File]::WriteAllText((Join-Path $source 'folder\rename-me.txt'), 'rename content')

& $dotnet $cli create $source $archive | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Creating smoke archive failed.' }

$before = Get-CfsAppProcessIds
$launchTime = Get-Date
Start-Process -FilePath $archive

$app = $null
for ($i = 0; $i -lt 80; $i++) {
    Start-Sleep -Milliseconds 250
    $app = Get-CfsAppProcesses |
        Where-Object { $before -notcontains $_.Id } |
        Select-Object -First 1
    if ($app) { break }
}
if (-not $app) { throw 'Cfs.App did not start from .cfs association.' }

$mount = $null
for ($i = 0; $i -lt 80; $i++) {
    Start-Sleep -Milliseconds 250
    $mount = Get-ChildItem -LiteralPath $mountRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'Smoke-*' -and $_.LastWriteTime -ge $launchTime } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($mount -and (Test-Path -LiteralPath (Join-Path $mount.FullName 'overwrite.txt'))) { break }
}
if (-not $mount) { throw 'Mounted folder did not appear.' }

[System.IO.File]::WriteAllText((Join-Path $mount.FullName 'overwrite.txt'), 'new')
Remove-Item -LiteralPath (Join-Path $mount.FullName 'delete.txt') -Force
Move-Item -LiteralPath (Join-Path $mount.FullName 'folder\rename-me.txt') -Destination (Join-Path $mount.FullName 'folder\renamed.txt')
New-Item -ItemType Directory -Path (Join-Path $mount.FullName 'created folder') | Out-Null
[System.IO.File]::WriteAllText((Join-Path $mount.FullName 'created folder\new.txt'), 'created')

$app.Refresh()
$handle = $app.MainWindowHandle
if ($handle -eq 0) { throw 'Cfs.App main window handle was not available.' }

[Win32]::PostMessage($handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
$clicked = $false
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 250
    $prompt = [Win32]::FindWindow([uint32]$app.Id, 'Unmount CFS')
    if ($prompt -ne [IntPtr]::Zero) {
        $yes = [Win32]::FindChildByText($prompt, 'Yes')
        if ($yes -ne [IntPtr]::Zero) {
            [Win32]::SendMessage($yes, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
            $clicked = $true
        }
        break
    }
}
if (-not $clicked) { throw 'Could not click Yes on the unmount save prompt.' }

if (-not $app.WaitForExit(15000)) {
    throw 'Cfs.App did not exit after accepting save prompt.'
}

if (Test-Path -LiteralPath $mount.FullName) {
    throw "CFS mount folder was not permanently removed: $($mount.FullName)"
}

$validateOutput = & $dotnet $cli validate $archive
if ($LASTEXITCODE -ne 0) {
    $validateOutput | Out-Host
    throw 'Smoke archive validation failed.'
}

$extract = Join-Path $workspace 'extracted'
& $dotnet $cli extract $archive $extract | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Smoke archive extraction failed.' }

$overwrite = Get-Content -LiteralPath (Join-Path $extract 'overwrite.txt') -Raw
$renamed = Get-Content -LiteralPath (Join-Path $extract 'folder\renamed.txt') -Raw
$created = Get-Content -LiteralPath (Join-Path $extract 'created folder\new.txt') -Raw

if ($overwrite -ne 'new') { throw 'Overwrite did not persist through double-click workflow.' }
if ($renamed -ne 'rename content') { throw 'Rename did not persist through double-click workflow.' }
if ($created -ne 'created') { throw 'Create did not persist through double-click workflow.' }
if (Test-Path -LiteralPath (Join-Path $extract 'delete.txt')) { throw 'Delete did not persist through double-click workflow.' }

Write-Host 'Double-click workflow smoke test passed.'
Write-Host "Archive: $archive"
Write-Host "Validation: $validateOutput"
