$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sdk = Join-Path $root 'third_party\lzma-sdk\source\C'
$out = Join-Path $root 'third_party\lzma-sdk\bin\x64\cfs-lzma.dll'
$sources = @('Alloc.c','CpuArch.c','LzFind.c','LzFindOpt.c','LzmaEnc.c','Lzma2Enc.c','LzmaDec.c','Lzma2Dec.c') | ForEach-Object { Join-Path $sdk $_ }
& gcc -shared -O2 -s -DZ7_ST -I $sdk -o $out (Join-Path $root 'third_party\lzma-sdk\cfs-lzma\cfs_lzma.c') $sources
if ($LASTEXITCODE -ne 0) { throw 'Failed to build cfs-lzma.dll.' }
Write-Host "Built $out"
