$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$packageDocuments = @(
    'README.md',
    'docs/BETA-QUICK-START.md',
    'docs/INSTALL-UNINSTALL.md',
    'docs/DATA-SAFETY.md',
    'docs/KNOWN-LIMITATIONS.md',
    'docs/BUG-REPORT-TEMPLATE.md',
    'docs/CONTRIBUTOR-ACCESS-POLICY.md',
    'docs/RELEASE-NOTES-0.1.0-BETA.md',
    'docs/on-demand-mount.md',
    'docs/compression.md',
    'docs/PERFORMANCE-BASELINE.md'
)

$failures = [System.Collections.Generic.List[string]]::new()
$passes = 0

function Test-Requirement {
    param([string]$Name, [bool]$Condition)
    if ($Condition) {
        $script:passes++
        Write-Host "PASS $Name"
    } else {
        $script:failures.Add($Name)
        Write-Host "FAIL $Name"
    }
}

function Read-RepoFile([string]$RelativePath) {
    $path = Join-Path $root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    return Get-Content -Raw -LiteralPath $path
}

$props = Read-RepoFile 'Directory.Build.props'
$versionMatch = [regex]::Match($props, '<CfsVersion>([^<]+)</CfsVersion>')
$labelMatch = [regex]::Match($props, '<CfsReleaseLabel>([^<]+)</CfsReleaseLabel>')
$centralIdentity = if ($versionMatch.Success -and $labelMatch.Success) { "CFS $($versionMatch.Groups[1].Value) $($labelMatch.Groups[1].Value)" } else { '' }
Test-Requirement 'central identity is CFS 0.1.0 Beta' ($centralIdentity -eq 'CFS 0.1.0 Beta')

$contents = @{}
foreach ($document in $packageDocuments) {
    $contents[$document] = Read-RepoFile $document
    Test-Requirement "package document exists: $document" ($null -ne $contents[$document])
    if ($null -ne $contents[$document]) {
        Test-Requirement "version identity is present: $document" ($contents[$document].Contains($centralIdentity, [StringComparison]::Ordinal))
        $otherRelease = [regex]::Matches($contents[$document], 'CFS\s+\d+\.\d+\.\d+\s+(?:Alpha|Beta|RC)', 'IgnoreCase') |
            Where-Object { $_.Value -ne $centralIdentity }
        Test-Requirement "no conflicting release identity: $document" ($otherRelease.Count -eq 0)
    }
}

$coverage = @{
    'quick start supported Windows and ProjFS' = @('docs/BETA-QUICK-START.md', 'Supported Windows requirements', 'Client-ProjFS')
    'quick start first launch and acknowledgement' = @('docs/BETA-QUICK-START.md', 'First launch', 'acknowledgement')
    'quick start create open mount edit save unmount validate' = @('docs/BETA-QUICK-START.md', 'Create or open', 'Mount and browse', 'Edit and save', 'Unmount and reopen', 'Validate')
    'quick start logs reports uninstall' = @('docs/BETA-QUICK-START.md', 'Open Logs Folder', 'Report Bug', 'Uninstall')
    'installation extraction and paths with spaces' = @('docs/INSTALL-UNINSTALL.md', 'Install by extraction', 'Paths containing spaces')
    'association is reversible' = @('docs/INSTALL-UNINSTALL.md', 'Reverse the file association', 'reg.exe delete')
    'data safety backups and non-critical files' = @('docs/DATA-SAFETY.md', 'Backups and non-critical files', 'independent backup')
    'data safety validation and failed save' = @('docs/DATA-SAFETY.md', 'Validate an archive', 'After a failed save')
    'data safety preserved mount and private logs' = @('docs/DATA-SAFETY.md', 'Preserved mount folders', 'Privacy-safe log sharing')
    'known limitations are honest' = @('docs/KNOWN-LIMITATIONS.md', 'not production-ready', 'not guaranteed', 'explicit full extraction')
    'release notes features ProjFS LZMA2 edits progress warning limitations reporting' = @('docs/RELEASE-NOTES-0.1.0-BETA.md', 'Beta status and warning', 'Major features', 'ProjFS behavior', 'per-file LZMA2', 'Supported edit operations', 'Progress and cancellation', 'Known limitations', 'Reporting')
    'compatibility mode is current and explicit' = @('docs/on-demand-mount.md', 'Compatibility Mode (Full', 'not selected automatically', 'not on-demand ProjFS')
    'performance baseline records workload measurements thresholds and limitations' = @('docs/PERFORMANCE-BASELINE.md', '1,000', 'three independent 8 MiB', 'Peak process working set', 'Regression ceilings', 'Limitations', 'not a performance guarantee')
}

foreach ($item in $coverage.GetEnumerator()) {
    $document = $item.Value[0]
    $text = $contents[$document]
    $required = @($item.Value | Select-Object -Skip 1)
    Test-Requirement $item.Key ($null -ne $text -and @($required | Where-Object { -not $text.Contains($_, [StringComparison]::OrdinalIgnoreCase) }).Count -eq 0)
}

$bug = $contents['docs/BUG-REPORT-TEMPLATE.md']
foreach ($field in @('CFS version and build identifier', 'Windows version and edition', 'What were you doing', 'Expected behavior', 'Actual behavior', 'Exact reproduction steps', 'Were files lost or corrupted', 'Archive size and approximate file count', 'Application used to edit or read the file', 'Does the problem repeat', 'Diagnostic logs', 'Screenshots when useful')) {
    Test-Requirement "bug template field: $field" ($bug.Contains($field, [StringComparison]::OrdinalIgnoreCase))
}

$contributor = $contents['docs/CONTRIBUTOR-ACCESS-POLICY.md']
foreach ($clause in @(
    'Beta users may receive permanent access to the future paid version when they make a meaningful contribution to CFS.',
    'reporting a previously unknown and reproducible bug;',
    'providing clear reproduction steps and useful logs;',
    'testing and confirming fixes;',
    'documenting application compatibility;',
    'helping other beta users resolve verified issues;',
    'improving documentation, tests, translations, accessibility, or accepted code.',
    'Duplicate, false, abusive, automated, or low-effort reports do not qualify.',
    'Permanent access is not automatic for every report. It is awarded based on the usefulness and impact of the contribution and must be confirmed in writing by the project owner.',
    'The owner maintains the award record manually for this beta.',
    'does not include a licensing backend'
)) {
    Test-Requirement "contributor policy clause: $clause" ($contributor.Contains($clause, [StringComparison]::Ordinal))
}

$allDocumentation = ($contents.Values -join "`n")
Test-Requirement 'GitHub issue destination is documented' ($allDocumentation.Contains('https://github.com/Mystrowin/CFS/issues', [StringComparison]::Ordinal))
Test-Requirement 'no invented email address' (-not [regex]::IsMatch($allDocumentation, '[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}', 'IgnoreCase'))
$urls = [regex]::Matches($allDocumentation, 'https?://[^\s)]+') | ForEach-Object { $_.Value.TrimEnd('.', ',', '`') }
$unexpectedUrls = @($urls | Where-Object { $_ -notmatch '^https://www\.7-zip\.org/' -and $_ -notmatch '^https://github\.com/Mystrowin/CFS/' })
Test-Requirement 'only approved external domains are documented' ($unexpectedUrls.Count -eq 0)
Test-Requirement 'no positive production-ready claim' (-not [regex]::IsMatch($allDocumentation, '(?<!not\s)production[- ]ready', 'IgnoreCase'))
Test-Requirement 'no universal compatibility claim' (-not [regex]::IsMatch($allDocumentation, '(?:supports|guarantees|provides)\s+(?:universal|all-application)\s+compatibility', 'IgnoreCase'))

foreach ($document in $packageDocuments) {
    $text = $contents[$document]
    if ($null -eq $text) { continue }
    $documentDirectory = Split-Path -Parent (Join-Path $root $document)
    foreach ($match in [regex]::Matches($text, '\[[^\]]+\]\(([^)]+\.md)\)')) {
        $target = $match.Groups[1].Value
        if ($target -match '^[a-z]+://') { continue }
        $resolvedTarget = [IO.Path]::GetFullPath((Join-Path $documentDirectory $target))
        Test-Requirement "internal link exists: $document -> $target" (Test-Path -LiteralPath $resolvedTarget -PathType Leaf)
    }
}

Write-Host "PACKAGE_DOCUMENT_COUNT=$($packageDocuments.Count)"
$packageDocuments | ForEach-Object { Write-Host "PACKAGE_DOCUMENT=$_" }
Write-Host "TOTAL_CHECKS=$($passes + $failures.Count) PASS=$passes FAIL=$($failures.Count)"
if ($failures.Count -gt 0) { exit 1 }
