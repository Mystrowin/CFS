param([int[]] $Counts = @(100, 250, 500, 1000))

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
$cli = Join-Path $root 'src\Cfs.Cli\bin\Debug\net8.0\Cfs.Cli.dll'
$workspace = Join-Path $env:TEMP ('cfs-small-files-benchmark-' + [guid]::NewGuid().ToString('N'))
$results = [System.Collections.Generic.List[object]]::new()
$resultPath = Join-Path $root 'dist\cfs-small-files-benchmark.csv'

function Stop-ProcessTree([int] $ProcessId) {
    Get-CimInstance Win32_Process -Filter "ParentProcessId = $ProcessId" | ForEach-Object { Stop-ProcessTree $_.ProcessId }
    Get-Process -Id $ProcessId -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

function Invoke-CliStage([string] $Name, [string[]] $Arguments) {
    $stdout = Join-Path $workspace ($Name + '.out.txt')
    $stderr = Join-Path $workspace ($Name + '.err.txt')
    $processArguments = (@($cli) + $Arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' '
    $process = Start-Process -FilePath $dotnet -ArgumentList $processArguments -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $peakMemory = 0L
    try {
        while (-not $process.HasExited) {
            $process.Refresh()
            $peakMemory = [Math]::Max($peakMemory, $process.WorkingSet64)
            Start-Sleep -Milliseconds 100
        }
        $process.WaitForExit()
        return [pscustomobject]@{ ProcessId=$process.Id; StartTime=$process.StartTime; ExitCode=$process.ExitCode; PeakMemory=$peakMemory; Output=(Get-Content $stdout -Raw); Error=(Get-Content $stderr -Raw) }
    }
    finally {
        if (-not $process.HasExited) { Stop-ProcessTree $process.Id }
    }
}

try {
    New-Item -ItemType Directory -Path $workspace | Out-Null
    foreach ($count in $Counts) {
        foreach ($mixed in @($false, $true)) {
            if ($mixed -and $count -ne 1000) { continue }
            $stage = if ($mixed) { "${count}-mixed" } else { "$count-small" }
            $source = Join-Path $workspace ($stage + '-source'); $archive = Join-Path $workspace ($stage + '.cfs'); $extract = Join-Path $workspace ($stage + '-extract')
            New-Item -ItemType Directory -Path (Join-Path $source 'nested\deep') -Force | Out-Null
            1..$count | ForEach-Object { [IO.File]::WriteAllBytes((Join-Path $source ("nested\f{0:D5}.bin" -f $_)), [byte[]](1..128)) }
            if ($mixed) { [IO.File]::WriteAllBytes((Join-Path $source 'nested\deep\large.bin'), [byte[]]::new(8MB)) }
            $stopwatch = [Diagnostics.Stopwatch]::StartNew(); $create = Invoke-CliStage ($stage + '-create') @('create',$source,$archive); $stopwatch.Stop()
            if ($create.ExitCode -ne 0) { throw "$stage create failed: $($create.Error)" }
            $validate = Invoke-CliStage ($stage + '-validate') @('validate',$archive); if ($validate.ExitCode -ne 0) { throw "$stage validate failed: $($validate.Error)" }
            $extractResult = Invoke-CliStage ($stage + '-extract') @('extract',$archive,$extract); if ($extractResult.ExitCode -ne 0) { throw "$stage extract failed: $($extractResult.Error)" }
            $result = [pscustomobject]@{ Stage=$stage; Files=$count + [int]$mixed; ElapsedSeconds=[Math]::Round($stopwatch.Elapsed.TotalSeconds,3); ArchiveBytes=(Get-Item $archive).Length; CreatePid=$create.ProcessId; CreateStart=$create.StartTime; CreateExit=$create.ExitCode; CreatePeakBytes=$create.PeakMemory; CleanupSucceeded=$true }
            $results.Add($result)
            $result | Format-List | Out-Host
            $results | Export-Csv $resultPath -NoTypeInformation
        }
    }
    $results | Format-Table -AutoSize | Out-Host
    $results | Export-Csv $resultPath -NoTypeInformation
}
finally {
    if (Test-Path $workspace) { Remove-Item $workspace -Recurse -Force -ErrorAction SilentlyContinue }
}
