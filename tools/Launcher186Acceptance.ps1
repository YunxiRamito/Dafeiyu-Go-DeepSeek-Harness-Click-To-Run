# Dot-sourced by release.ps1; this file has no import-time side effects.
function Get-Launcher186Suites {
    @(
        @{ name='DshStartup'; assembly='DshStartup.Regression'; minimum=31; pattern='(?m)^PASS (\d+) startup diagnostic and real Cordis compatibility checks; owned fixtures only\.' },
        @{ name='NativeDshProcessQuery'; assembly='NativeDshWmiQuery'; minimum=74; pattern='(?m)^PASS (\d+) checks; WMI records require matching native identity' },
        @{ name='DshPortResolver'; assembly='DshPortResolver.Regression'; minimum=67; pattern='(?m)^DshPortResolver regression checks passed: (\d+)' },
        @{ name='ServiceStartup'; assembly='ServiceStartup.Regression'; minimum=293; pattern='(?m)^PASS (\d+) real Node/Windows listener checks; isolated fixtures only\.' },
        @{ name='DsHarnessRemote'; assembly='DsHarnessRemote.Regression'; minimum=33; pattern='(?m)^PASS: (\d+) Remote status, profile, official command and preservation checks\.' },
        @{ name='DsHarnessRemote.Runtime'; assembly='DsHarnessRemote.Runtime.Regression'; minimum=10; pattern='(?m)^PASS (\d+) actual compiled Remote/official SDK checks; owned profile only\.' }
    )
}

function Invoke-Launcher186Checks {
    param([string]$Root,[string]$Snapshot,[string]$Run,[string]$Dist,[string]$DotnetPath,[string]$NodePath,[string]$CordisEntry)
    if (-not $NodePath) { $NodePath='G:/DeepSeek DSH/node_modules/node/bin/node.exe' }
    if (-not $CordisEntry) { $CordisEntry='G:/DeepSeek DSH/node_modules/@deepseek-ai/cordis/lib/index.js' }
    $NodePath=(Resolve-Path -LiteralPath $NodePath -ErrorAction Stop).Path
    $CordisEntry=(Resolve-Path -LiteralPath $CordisEntry -ErrorAction Stop).Path
    $output=Join-Path $Run 'launcher186'
    if ($Run -match '^[cC]:' -or $NodePath -match '^[cC]:' -or $CordisEntry -match '^[cC]:' -or (Test-Path -LiteralPath $output)) {
        throw 'Launcher186 requires fresh owned project-drive output and a non-C runtime.'
    }
    New-Item -ItemType Directory -Path $output -ErrorAction Stop | Out-Null
    $suites=@(Get-Launcher186Suites)
    $paths=@('release.ps1','verify.ps1','tools/Launcher186Acceptance.ps1','source/build-winui.ps1')
    # The accepted commit supplies every product input, including the UI and
    # package resources. The small source-linked checks are described honestly.
    $gitPaths=@('ls-files','--','source') + @($suites | ForEach-Object { 'tests/'+$_.name+'.Regression' })
    $paths+=@((Get-ReleaseGit $Root $gitPaths) -split "`n" | Where-Object { $_ })
    $inputs=@($paths | Sort-Object -Unique | ForEach-Object {
        if ($_ -match '^\.\./|^[A-Za-z]:|^/|(^|/)\.\.(/|$)') { throw 'Launcher186 source escaped its repository.' }
        $input=New-LocalReleaseInput (Join-Path $Root $_)
        $input.relative=$_
        $input.gitBlob=Get-ReleaseGit $Root @('hash-object',('--path='+$_),'--',$input.path)
        if ($_ -ne 'source/build-winui.ps1') {
            $snapshotPath=Join-Path $Snapshot $_
            if ((Get-ReleaseGit $Root @('hash-object',('--path='+$_),'--',$snapshotPath)) -cne $input.gitBlob) {
                throw 'Launcher186 snapshot differs from its accepted source.'
            }
        }
        $input
    })
    $runtimePaths=@($DotnetPath,$NodePath,$CordisEntry,(Join-Path (Split-Path (Split-Path $CordisEntry -Parent) -Parent) 'package.json'))
    $remoteFixture=[Environment]::GetEnvironmentVariable('DSH_REMOTE_FIXTURE_PACKAGE','Process')
    if (-not $remoteFixture -or $remoteFixture -match '^[cC]:' -or -not (Test-Path -LiteralPath $remoteFixture -PathType Container)) {
        throw 'Launcher186 requires the integrity-verified official Remote package fixture in DSH_REMOTE_FIXTURE_PACKAGE.'
    }
    $remoteIdentity=Get-Content -LiteralPath (Join-Path $remoteFixture 'package.json') -Raw | ConvertFrom-Json
    if ($remoteIdentity.name -cne 'ds-harness-remote' -or $remoteIdentity.version -cne '0.6.4') {
        throw 'Launcher186 Remote package fixture identity differs.'
    }
    $runtimePaths+=@(Get-ChildItem -LiteralPath $remoteFixture -Recurse -File | ForEach-Object FullName)
    $sdk=[Environment]::GetEnvironmentVariable('DSH_REMOTE_SDK_NODE_MODULES','Process')
    $pnpm=[Environment]::GetEnvironmentVariable('DSH_REMOTE_PNPM_EXE','Process')
    if (-not $sdk -or -not $pnpm -or $sdk -match '^[cC]:' -or $pnpm -match '^[cC]:') {
        throw 'Launcher186 requires an actual non-C DSH SDK and verified pnpm executable for compiled Core acceptance.'
    }
    $sdk=(Resolve-Path -LiteralPath $sdk -ErrorAction Stop).Path
    $pnpm=(Resolve-Path -LiteralPath $pnpm -ErrorAction Stop).Path
    $runtimePaths+=$pnpm
    foreach ($package in @('@deepseek-ai/dsh','@deepseek-ai/dsh-plugin-manager')) {
        $runtimePaths+=Join-Path $sdk ($package+'/package.json')
        $runtimePaths+=@(Get-ChildItem -LiteralPath (Join-Path $sdk ($package+'/lib')) -Recurse -File | ForEach-Object FullName)
    }
    $cosmokit=Join-Path (Split-Path (Split-Path (Split-Path $CordisEntry -Parent) -Parent) -Parent) 'cosmokit'
    foreach ($relative in @('package.json','lib/index.js')) {
        $path=Join-Path $cosmokit $relative
        if (Test-Path -LiteralPath $path -PathType Leaf) { $runtimePaths+=$path }
    }
    $runtimeInputs=@($runtimePaths | Sort-Object -Unique | ForEach-Object { New-LocalReleaseInput $_ })
    $core=New-LocalReleaseInput (Join-Path $Dist 'DeepSeek Harness.Core.dll')
    $results=@(); $artifacts=@()
    Push-Location $output
    try {
        foreach ($suite in $suites) {
            $suiteOutput=Join-Path $output $suite.name
            $runnerOutput=Join-Path $suiteOutput 'runner'
            New-Item -ItemType Directory -Force -Path $suiteOutput | Out-Null
            $project=Join-Path $Snapshot ('tests/'+$suite.name+'.Regression/'+$suite.name+'.Regression.csproj')
            $buildLog=Join-Path $suiteOutput 'build.log'; $runLog=Join-Path $suiteOutput 'run.log'
            $intermediate=(Join-Path $suiteOutput 'obj')+[IO.Path]::DirectorySeparatorChar
            Invoke-DshInstallNative $DotnetPath @('build',$project,'-c','Release','--output',$runnerOutput,
                '--configfile',(Join-Path $Snapshot 'source/NuGet.config'),'-p:UseSharedCompilation=false',
                ('-p:BaseIntermediateOutputPath='+$intermediate),('-p:MSBuildProjectExtensionsPath='+$intermediate)) $buildLog
            $runner=Join-Path $runnerOutput ($suite.assembly+'.dll')
            $arguments=@($runner)
            if ($suite.name -eq 'DshStartup') { $arguments+=@((Join-Path $suiteOutput 'fixtures'),$NodePath,$CordisEntry) }
            elseif ($suite.name -eq 'ServiceStartup') { $arguments+=@((Join-Path $suiteOutput 'fixtures'),$NodePath) }
            elseif ($suite.name -eq 'DsHarnessRemote.Runtime') {
                $fixture=Join-Path $suiteOutput 'fixtures'
                New-Item -ItemType Directory -Path $fixture -ErrorAction Stop | Out-Null
                New-Item -ItemType Junction -Path (Join-Path $fixture 'node_modules') -Target $sdk -ErrorAction Stop | Out-Null
                $arguments+=@($Dist,$fixture,$sdk,$pnpm)
            }
            Invoke-DshInstallNative $DotnetPath $arguments $runLog
            $match=[regex]::Match([IO.File]::ReadAllText($runLog),$suite.pattern)
            if (-not $match.Success -or [int]$match.Groups[1].Value -lt $suite.minimum) { throw "Launcher186 suite did not prove all required checks: $($suite.name)" }
            $results+=[ordered]@{name=$suite.name;exitCode=0;passed=$true;checks=[int]$match.Groups[1].Value;minimumChecks=$suite.minimum}
            foreach ($path in @($buildLog,$runLog) + @(Get-ChildItem -LiteralPath $runnerOutput -File | ForEach-Object FullName)) {
                $artifacts+=New-LocalReleaseInput $path
            }
            if ($suite.name -eq 'DsHarnessRemote.Runtime') {
                $runtimeReport=Join-Path $fixture 'runtime-report.json'
                $actualCore=Get-Content -LiteralPath $runtimeReport -Raw | ConvertFrom-Json
                if ($actualCore.passed -lt 10 -or $actualCore.failed -ne 0 -or $actualCore.coreSha256.ToLowerInvariant() -cne $core.sha256) {
                    throw 'Remote runtime checks do not prove the accepted compiled Core.'
                }
                $artifacts+=New-LocalReleaseInput $runtimeReport
            }
        }
    } finally { Pop-Location }
    foreach ($input in @($inputs)+@($runtimeInputs)) {
        $current=New-LocalReleaseInput $input.path
        if ($current.sha256 -cne $input.sha256 -or $current.bytes -ne $input.bytes) { throw 'Launcher186 input changed during acceptance.' }
    }
    $report=Join-Path $output 'launcher186-report.json'
    Write-ReleaseJson $report ([ordered]@{schema=1;profile='Launcher186';executionMode='SourceLinkedAndCompiledCore';success=$true;
        sourceCommit=(Get-ReleaseGit $Root @('rev-parse','HEAD'));suites=$results;inputs=$inputs;runtimeInputs=$runtimeInputs;artifacts=$artifacts;core=$core})
    [ordered]@{schema=1;profile='Launcher186';report=(New-LocalReleaseInput $report);inputs=$inputs;runtimeInputs=$runtimeInputs;artifacts=$artifacts;core=$core}
}

function Assert-Launcher186Proof {
    param([string]$Root,$Receipt)
    $proof=$Receipt.launcher186
    if ($null -eq $proof -or $proof.schema -ne 1 -or $proof.profile -cne 'Launcher186' -or
        $proof.inputs.Count -lt 20 -or $proof.runtimeInputs.Count -lt 4 -or $proof.artifacts.Count -lt 15) {
        throw 'Launcher186 acceptance proof is incomplete.'
    }
    foreach ($input in @($proof.inputs)+@($proof.runtimeInputs)+@($proof.artifacts)+@($proof.report,$proof.core)) {
        $actual=New-LocalReleaseInput $input.path
        if ($actual.sha256 -cne $input.sha256 -or $actual.bytes -ne $input.bytes) { throw 'Launcher186 input or result changed.' }
    }
    foreach ($input in $proof.inputs) {
        if (-not $input.relative -or $input.relative -match '^\.\./|^[A-Za-z]:|^/|(^|/)\.\.(/|$)') { throw 'Invalid Launcher186 source input.' }
        if ($input.gitBlob -cnotmatch '^[a-f0-9]{40}$' -or
            (Get-ReleaseGit $Root @('hash-object',('--path='+$input.relative),'--',(Join-Path $Root $input.relative))) -cne $input.gitBlob) {
            throw 'Launcher186 proof does not match accepted source.'
        }
    }
    foreach ($relative in @('release.ps1','tools/Launcher186Acceptance.ps1','source/WinUIProgram.cs','source/DshStartupDiagnostics.cs',
        'source/DshStartupCompatibility.cs','source/DshPortResolver.cs','source/NativeDshProcessQuery.cs','source/DsHarnessRemoteService.cs',
        'source/DshPluginCliService.cs','source/SettingsWindow.Connections.cs','source/SettingsWindow.xaml')) {
        if ($relative -cnotin @($proof.inputs.relative)) { throw 'Launcher186 required source input is missing.' }
    }
    $report=Get-Content -LiteralPath $proof.report.path -Raw | ConvertFrom-Json
    $suites=@(Get-Launcher186Suites)
    if ($report.schema -ne 1 -or $report.profile -cne 'Launcher186' -or $report.executionMode -cne 'SourceLinkedAndCompiledCore' -or
        $report.success -ne $true -or $report.sourceCommit -cne $Receipt.commit -or $report.suites.Count -ne $suites.Count -or
        $report.core.sha256 -cne $proof.core.sha256) { throw 'Launcher186 report does not prove its successful suites.' }
    for ($index=0; $index -lt $suites.Count; $index++) {
        $actual=$report.suites[$index]; $expected=$suites[$index]
        if ($actual.name -cne $expected.name -or $actual.exitCode -ne 0 -or $actual.passed -ne $true -or
            $actual.minimumChecks -ne $expected.minimum -or $actual.checks -lt $expected.minimum) { throw 'Launcher186 suite proof differs.' }
    }
    foreach ($name in @('inputs','runtimeInputs','artifacts','core')) {
        if (($report.$name | ConvertTo-Json -Depth 20 -Compress) -cne ($proof.$name | ConvertTo-Json -Depth 20 -Compress)) {
            throw 'Launcher186 report/input binding differs.'
        }
    }
}
