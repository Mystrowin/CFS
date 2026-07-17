$root = Split-Path -Parent $PSScriptRoot
$vsDevCmd = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat'
$exe = Join-Path $env:TEMP 'cfs-projfs-minimal.exe'
$mount = Join-Path $env:TEMP ('cfs-projfs-minimal-' + [guid]::NewGuid().ToString('N'))
& cmd.exe /d /c ('call "' + $vsDevCmd + '" -arch=x64 -host_arch=x64 && cl.exe /nologo /O2 /DUNICODE /D_UNICODE "' + (Join-Path $root 'tools\ProjFsMinimalProvider.c') + '" /link ProjectedFSLib.lib Ole32.lib User32.lib /OUT:"' + $exe + '"')
if ($LASTEXITCODE) { throw 'ProjFS provider build failed.' }
try { & $exe $mount; if ($LASTEXITCODE) { throw "ProjFS provider test failed: $LASTEXITCODE" } } finally { if (Test-Path $mount) { Remove-Item $mount -Recurse -Force } }
