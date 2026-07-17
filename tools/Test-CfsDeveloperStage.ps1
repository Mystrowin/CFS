param([Parameter(Mandatory = $true)][string]$StagePath)

$ErrorActionPreference = 'Stop'
$stage = (Resolve-Path -LiteralPath $StagePath).Path
$required = @('Cfs.App.exe', 'Cfs.Broker.exe', 'Register-CfsFileAssociation.ps1', 'ShellNew\CFS-Empty.cfs')
$failures = [System.Collections.Generic.List[string]]::new()
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $stage $relative) -PathType Leaf)) { $failures.Add("missing:$relative") }
}

$template = Join-Path $stage 'ShellNew\CFS-Empty.cfs'
if (Test-Path -LiteralPath $template -PathType Leaf) {
    $bytes = [IO.File]::ReadAllBytes($template)
    if ($bytes.Length -lt 24 -or [Text.Encoding]::ASCII.GetString($bytes, 0, 4) -ne 'CFS1') { $failures.Add('template:magic') }
    elseif ([BitConverter]::ToInt32($bytes, 4) -ne 1) { $failures.Add('template:format-version') }
    else {
        $manifestOffset = [BitConverter]::ToInt64($bytes, 8)
        $manifestLength = [BitConverter]::ToInt64($bytes, 16)
        if ($manifestOffset -lt 24 -or $manifestLength -le 0 -or ($manifestOffset + $manifestLength) -gt $bytes.LongLength) { $failures.Add('template:manifest-bounds') }
        else {
            $json = [Text.Encoding]::UTF8.GetString($bytes, [int]$manifestOffset, [int]$manifestLength) | ConvertFrom-Json
            if ($json.Version -ne 1) { $failures.Add('template:manifest-version') }
            if (@($json.Entries).Count -ne 0) { $failures.Add('template:not-empty') }
        }
    }
}

if ($failures.Count -ne 0) {
    $failures | ForEach-Object { Write-Output "FAIL $_" }
    throw "Developer stage verification failed with $($failures.Count) error(s)."
}
Write-Output 'PASS app-executable'
Write-Output 'PASS broker-winexe'
Write-Output 'PASS portable-registration-script'
Write-Output 'PASS ShellNew-CFS1-v1-zero-entry-template'
Write-Output 'TOTAL 4 PASS 4 FAIL 0'
