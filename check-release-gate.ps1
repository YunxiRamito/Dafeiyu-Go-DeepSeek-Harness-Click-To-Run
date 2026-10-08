param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Root = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'
function ParseVersion([string]$Text) {
    if ($Text -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw "Invalid release version: $Text" }
    return [version]$Text
}
$target = ParseVersion $Version
$manifest = Get-Content -LiteralPath (Join-Path $Root 'manifest.json') -Raw -Encoding utf8 | ConvertFrom-Json
$current = ParseVersion ([string]$manifest.version)
if ([string]$manifest.sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Public manifest must retain a verified published SHA-256.' }
$major = $current.Major -ne $target.Major -or $current.Minor -ne $target.Minor
$pending = @()
foreach ($name in @('patches.json', 'patches-preview.json')) {
    $path = Join-Path $Root $name
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing patch feed: $name" }
    $feed = Get-Content -LiteralPath $path -Raw -Encoding utf8 | ConvertFrom-Json
    if ($feed.schemaVersion -ne 1 -or $null -eq $feed.patches) { throw "Invalid patch feed schema: $name" }
    foreach ($patch in @($feed.patches)) {
        if ([string]::IsNullOrWhiteSpace([string]$patch.id)) { throw "Patch ID missing: $name" }
        $merged = [string]$patch.mergedIn
        $mergedVersion = if ([string]::IsNullOrWhiteSpace($merged)) { $null } else { ParseVersion $merged }
        if ($major -and ($null -eq $mergedVersion -or $mergedVersion -gt $target)) {
            $pending += "$name / $($patch.id): $($patch.title)"
        }
    }
}
if ($pending.Count) { throw "Release blocked by unmerged patches:`n$($pending -join "`n")" }
Write-Host "PASS patch merge gate: $current -> $target"
