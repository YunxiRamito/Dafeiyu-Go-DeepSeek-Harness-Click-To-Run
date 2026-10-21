#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WorkRoot,
    [Parameter(Mandatory)][string]$DotnetPath,
    [Parameter(Mandatory)][string]$CorePath,
    [string]$NodePath = 'G:/DeepSeek DSH/node_modules/node/bin/node.exe',
    [string]$NuGetCache
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'release.ps1'),[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw 'release.ps1 contains syntax errors.' }
foreach ($definition in $ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.FunctionDefinitionAst] }) {
    . ([scriptblock]::Create($definition.Extent.Text))
}
$WorkRoot=Get-LocalReleaseWorkRoot $root $WorkRoot
if (Test-Path -LiteralPath $WorkRoot) { throw 'Use a fresh owned DshInstall release regression directory.' }
$fixture=Join-Path $WorkRoot 'source'
$dist=Join-Path $WorkRoot 'dist'
New-Item -ItemType Directory -Path $fixture,$dist -Force | Out-Null
$files=@('release.ps1','verify.ps1','source/build-winui.ps1','source/DeepSeekHarness.csproj','source/WinUIProgram.cs','source/NuGet.config')
foreach ($suite in @('DshBundle','DshNpmBundle')) {
    $directory=Join-Path $root "tests/$suite.Regression"
    $files += Get-ChildItem -LiteralPath $directory -File | Where-Object Extension -In @('.cs','.csproj') | ForEach-Object { "tests/$suite.Regression/"+$_.Name }
    [xml]$project=Get-Content -LiteralPath (Join-Path $directory "$suite.Regression.csproj") -Raw
    foreach ($compile in $project.SelectNodes('/Project/ItemGroup/Compile[@Include]')) {
        $files += [IO.Path]::GetRelativePath($root,[IO.Path]::GetFullPath((Join-Path $directory $compile.Include)))
    }
}
foreach ($file in $files | Sort-Object -Unique) {
    $target=Join-Path $fixture $file
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root $file) -Destination $target
}
Copy-Item -LiteralPath $CorePath -Destination (Join-Path $dist 'DeepSeek Harness.Core.dll')
Invoke-ReleaseNative git @('-C',$fixture,'init','--quiet')
Invoke-ReleaseNative git @('-C',$fixture,'add','--all')
Invoke-ReleaseNative git @('-C',$fixture,'-c','user.name=DshInstall Test','-c','user.email=dsh-install-test@localhost','commit','--quiet','-m','DSH installation release fixture')
function Assert-DshRelease([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-DshReleaseThrows([scriptblock]$Action,[string]$Pattern) {
    try { & $Action | Out-Null } catch { if ($_.Exception.Message -match $Pattern) { return }; throw }
    throw "Expected DshInstall rejection: $Pattern"
}
Invoke-LocalReleaseEnvironment $WorkRoot 'http://127.0.0.1:7890' $NuGetCache {
    $proof=Invoke-LocalDshInstallChecks $fixture $fixture $WorkRoot $dist $DotnetPath $NodePath
    [xml]$project=Get-Content -LiteralPath (Join-Path $fixture 'source/DeepSeekHarness.csproj') -Raw
    $version=$project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
    $zip=Join-Path $WorkRoot "Dafeiyu-GO $version.zip"
    Compress-Archive -LiteralPath (Join-Path $dist 'DeepSeek Harness.Core.dll') -DestinationPath $zip
    $receipt=[ordered]@{schema=1;version=$version;commit=(Get-ReleaseGit $fixture @('rev-parse','HEAD'));
        checks=@('DshBundle.LocalOnly','DshNpmBundle.Offline90','verify');testMode='DshInstall';dshInstall=$proof;
        zipPath=$zip;sha256=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()}
    $receiptPath=Join-Path $WorkRoot 'profile-regression-receipt.json'
    Write-ReleaseJson $receiptPath $receipt
    $accepted=Assert-LocalReleaseReceipt $fixture $receiptPath
    Assert-DshRelease ($accepted.testMode -ceq 'DshInstall' -and $accepted.dshInstall.report.sha256) 'Real DshInstall proof was not accepted.'
    $report=Get-Content -LiteralPath $proof.report.path -Raw | ConvertFrom-Json
    Assert-DshRelease ($report.suites.Count -eq 2 -and $report.suites[1].checks -eq 90) 'Both actual installation suites must finish.'
    Assert-DshRelease (-not (Test-Path -LiteralPath (Join-Path $WorkRoot 'focused-fixture'))) 'Unrelated connection tests must not run.'
    $log=$proof.artifacts | Where-Object { $_.path -like '*DshNpmBundle*run.log' } | Select-Object -First 1
    $original=[IO.File]::ReadAllBytes($log.path)
    try {
        [IO.File]::AppendAllText($log.path,"`nchanged result")
        Assert-DshReleaseThrows { Assert-LocalReleaseReceipt $fixture $receiptPath } 'input or result changed'
    } finally { [IO.File]::WriteAllBytes($log.path,$original) }
    $savedChecks=$receipt.checks
    try {
        $receipt.checks=@('ManagedConnectionPlugins.Compiled12','verify')
        Write-ReleaseJson $receiptPath $receipt
        Assert-DshReleaseThrows { Assert-LocalReleaseReceipt $fixture $receiptPath } 'checks differ'
    } finally { $receipt.checks=$savedChecks; Write-ReleaseJson $receiptPath $receipt }
    $core=Join-Path $dist 'DeepSeek Harness.Core.dll'
    $original=[IO.File]::ReadAllBytes($core)
    try {
        [IO.File]::AppendAllText($core,'changed core')
        Assert-DshReleaseThrows { Assert-LocalReleaseReceipt $fixture $receiptPath } 'input or result changed'
    } finally { [IO.File]::WriteAllBytes($core,$original) }
    $null=Assert-LocalReleaseReceipt $fixture $receiptPath
    $publisher=Join-Path $WorkRoot 'publisher-clone'
    Invoke-ReleaseNative git @('clone','--quiet','--no-hardlinks',$fixture,$publisher)
    $null=Assert-LocalReleaseReceipt $publisher $receiptPath
    Write-Host 'PASS DshInstall release profile: both real suites, source/runtime/result/Core/ZIP binding, altered result/Core rejection, and incompatible check rejection.'
    Write-Host 'PASS DshInstall receipt validates against a separate publication clone with canonical Git source hashes.'
    Write-Host "Profile regression evidence: $receiptPath"
}
