param([string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$fixture = Join-Path $Root ('.update-integrity-tests\gate-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $fixture | Out-Null
$gate = Join-Path $Root 'check-release-gate.ps1'
function Save([string]$Name, [object]$Value) { $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $fixture $Name) -Encoding utf8 }
function Expect([string]$Version, [bool]$Fail) {
 $failed = $false
 try { & $gate -Version $Version -Root $fixture } catch { $failed = $true }
 if ($failed -ne $Fail) { throw "Unexpected gate result: $Version, expected fail=$Fail" }
}
Save 'manifest.json' @{version='1.6.0';sha256=('a'*64)}
Save 'patches.json' @{schemaVersion=1;patches=@()}
Save 'patches-preview.json' @{schemaVersion=1;patches=@()}
Expect '1.6.1' $false
Save 'patches.json' @{schemaVersion=1;patches=@(@{id='p';title='test';mergedIn=''})}
Expect '1.7.0' $true
Save 'patches.json' @{schemaVersion=1;patches=@(@{id='p';title='test';mergedIn='banana'})}
Expect '1.7.0' $true
Save 'patches.json' @{schemaVersion=1;patches=@(@{id='p';title='test';mergedIn='1.7.0'})}
Expect '1.7.0' $false
'bad-json' | Set-Content -LiteralPath (Join-Path $fixture 'patches.json') -Encoding utf8
Expect '1.7.0' $true
Save 'patches.json' @{schemaVersion=1;patches=@()}
Save 'manifest.json' @{version='1.6.0';sha256=''}
Expect '1.6.1' $true
Write-Host 'PASS six strict release-gate fixture checks'
