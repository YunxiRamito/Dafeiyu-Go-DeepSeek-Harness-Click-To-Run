param([Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop'
if ([IO.Path]::GetFullPath($OutputRoot) -match '^[cC]:' -or (Test-Path -LiteralPath $OutputRoot)) { throw 'Use a fresh owned G-drive output root.' }
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'release.ps1') -FunctionsOnly
$current=[IO.File]::ReadAllText((Join-Path $root 'source/build-winui.ps1'))
$candidate='$dotnetCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1'
$nuget="'G:\DeepSeek DSH\.nuget-packages'"
$dotnetPath="G:/fixture/SDK's/dotnet.exe"; $cachePath="G:/fixture/cache's"
$dotnetLiteral="'"+$dotnetPath.Replace("'","''")+"'"; $cacheLiteral="'"+$cachePath.Replace("'","''")+"'"
$legacy=@'
param([string]$OutputDirectory='keep')
$dotnet='legacy-dotnet'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}
$localNuGet='legacy-cache'
& $dotnet publish $project --configuration Release
& $dotnet publish $infoProject --configuration Release
& $frameworkCompiler /target:winexe $runtimeDetectorSource
'@
$results=[Collections.Generic.List[object]]::new()
function Check([string]$Name,[string]$InputText,[bool]$Reject,[string]$Expected='') {
 $path=Join-Path $OutputRoot ($Name+'.ps1'); [IO.File]::WriteAllText($path,$InputText,[Text.UTF8Encoding]::new($false)); $before=(Get-FileHash $path -Algorithm SHA256).Hash; $bad=$false; $reason=''
 try { Set-SnapshotBuildSettings $path $dotnetPath $cachePath } catch { $bad=$true; $reason=$_.Exception.Message }
 if($bad -ne $Reject){throw ('Unexpected result '+$Name+': '+$reason)}
 if($Reject -and (Get-FileHash $path -Algorithm SHA256).Hash -cne $before){throw ('Rejected script mutated: '+$Name)}
 if(!$Reject -and [IO.File]::ReadAllText($path) -cne $Expected){throw ('Changed beyond two default RHS: '+$Name)}
 $results.Add(@{name=$Name;passed=$true;rejected=$bad;beforeSha256=$before;afterSha256=(Get-FileHash $path -Algorithm SHA256).Hash})
}
Check 'legacy-literal' $legacy $false ($legacy.Replace("'legacy-dotnet'",$dotnetLiteral).Replace("'legacy-cache'",$cacheLiteral))
Check 'current-real-build' $current $false ($current.Replace($candidate,$dotnetLiteral).Replace($nuget,$cacheLiteral))
Check 'unknown-dotnet' ($current.Replace($candidate,'(Get-Command custom).Source')) $true
Check 'unknown-nuget' ($current.Replace($nuget,'(Get-Location).Path')) $true
Check 'near-miss-pipeline' ($current.Replace('Select-Object -First 1','Select-Object -First 2')) $true
Check 'duplicate-dotnet' ($current+[char]10+'$dotnet = ''extra''') $true
Check 'duplicate-nuget' ($current+[char]10+'$localNuGet = ''extra''') $true
Check 'nested-dotnet' ($current+[char]10+'if ($true) { $dotnet = ''extra'' }') $true
Check 'scoped-dotnet' ($current+[char]10+'$script:dotnet = ''extra''') $true
Check 'unknown-fallback' ($current.Replace('(Get-Command dotnet -ErrorAction Stop).Source','(Get-Command custom).Source')) $true
Check 'compound-assignment' ($legacy.Replace('$dotnet=','$dotnet+=')) $true
Check 'missing-nuget' ($legacy.Replace('$localNuGet=''legacy-cache''','# removed')) $true
Check 'syntax-error' ($current+[char]10+'if (') $true
@{status='passed';checks=$results.Count;results=@($results);releaseSha256=(Get-FileHash (Join-Path $root 'release.ps1') -Algorithm SHA256).Hash}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $OutputRoot 'report.json') -Encoding utf8
Write-Host ('PASS '+$results.Count+' snapshot adapter checks')