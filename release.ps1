#Requires -Version 7.4
[CmdletBinding()]
param(
    [switch]$Publish,
    [switch]$PushCommit,
    [string]$Repository = 'YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Click-To-Run',
    [string]$WorkRoot,
    [string]$DotnetPath,
    [string]$NuGetCache,
    [string]$Proxy = 'http://127.0.0.1:7890',
    [string]$Otp,
    [switch]$NoManifest,
    [switch]$FunctionsOnly
)

function Get-LocalReleaseSuites {
    @('PluginUpdateCompare', 'PluginInstallRepair', 'PatchSystem', 'SettingsSearch',
      'ClientNotice', 'DeveloperNotice', 'InfoWindowClient', 'DshDataImport',
      'DshDataExport', 'DshDataDiscovery', 'StartupBackgroundUpdates',
      'UpdateMetadata', 'UpdateIntegrity', 'ComponentRuntime')
}

function Invoke-ReleaseNative {
    param([string]$File, [string[]]$Arguments)
    & $File @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $File" }
}

function Get-ReleaseGit {
    param([string]$Root, [string[]]$Arguments)
    $value = & git -C $Root @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) { throw "Git failed: $($Arguments[0])" }
    ($value -join "`n").Trim()
}

function Assert-ReleaseClean {
    param([string]$Root, [string]$Commit)
    # Never stage, stash or repair a developer's checkout. Untracked files are not archived.
    if (Get-ReleaseGit $Root @('status', '--porcelain', '--untracked-files=no')) {
        throw 'Commit tracked changes explicitly before releasing. No automatic git add/commit is performed.'
    }
    if ($Commit -and (Get-ReleaseGit $Root @('rev-parse', 'HEAD')) -cne $Commit) {
        throw 'HEAD changed after local acceptance; run release.ps1 again.'
    }
}

function Get-LocalReleaseWorkRoot {
    param([string]$Root, [string]$Requested)
    if (-not $Requested) {
        $common = Get-ReleaseGit $Root @('rev-parse', '--path-format=absolute', '--git-common-dir')
        $project = Split-Path (Split-Path $common -Parent) -Parent
        $Requested = Join-Path $project 'Temp_SetupandLauncher/LocalRelease'
    }
    $path = [IO.Path]::GetFullPath($Requested)
    if ($path -match '^[cC]:' -or $path -notmatch '[/\\]Temp_SetupandLauncher[/\\]') {
        throw 'WorkRoot must be on a non-C drive beneath a project Temp_SetupandLauncher directory.'
    }
    $path
}

function Remove-ReleaseScratch {
    param([string]$Path, [string]$Parent)
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($Parent).TrimEnd('/','\') + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove outside scratch root: $full"
    }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}

function Invoke-LocalReleaseEnvironment {
    param([string]$WorkRoot, [string]$Proxy, [string]$NuGetCache, [scriptblock]$Action)
    $values = @{
        TEMP = (Join-Path $WorkRoot 'temp'); TMP = (Join-Path $WorkRoot 'temp')
        TMPDIR = (Join-Path $WorkRoot 'temp')
        DOTNET_CLI_HOME = (Join-Path $WorkRoot 'dotnet-home')
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'; DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        DOTNET_NOLOGO = '1'; DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
        NUGET_PACKAGES = $(if ($NuGetCache) { [IO.Path]::GetFullPath($NuGetCache) } else { Join-Path $WorkRoot 'nuget/packages' })
        NUGET_HTTP_CACHE_PATH = (Join-Path $WorkRoot 'nuget/http')
        NUGET_PLUGINS_CACHE_PATH = (Join-Path $WorkRoot 'nuget/plugins')
        npm_config_cache = (Join-Path $WorkRoot 'npm-cache')
        npm_config_update_notifier = 'false'
        HTTP_PROXY = $Proxy; HTTPS_PROXY = $Proxy; ALL_PROXY = $Proxy
        NO_PROXY = $(if ($Proxy) { 'localhost,127.0.0.1' } else { '*' })
        GIT_TERMINAL_PROMPT = '0'; GCM_INTERACTIVE = 'Never'
        GODEBUG = $(if ($env:GODEBUG) { $env:GODEBUG + ',http2client=0' } else { 'http2client=0' })
        MSBUILDDISABLENODEREUSE = '1'
    }
    if ($values.NUGET_PACKAGES -match '^[cC]:') { throw 'NuGetCache cannot be on C:.' }
    $saved = @{}
    foreach ($name in $values.Keys) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
    try {
        foreach ($name in $values.Keys) { [Environment]::SetEnvironmentVariable($name, $values[$name], 'Process') }
        foreach ($name in @('TEMP', 'DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'NUGET_PLUGINS_CACHE_PATH', 'npm_config_cache')) {
            New-Item -ItemType Directory -Path $values[$name] -Force | Out-Null
        }
        & $Action
    } finally {
        foreach ($name in $saved.Keys) {
            if ($null -eq $saved[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
            else { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
        }
    }
}

function Write-ReleaseJson {
    param([string]$Path, $Value)
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
}

function Invoke-ReleaseHttp {
    param([string]$Uri, [string]$Method = 'GET', [hashtable]$Headers = @{},
        [string]$OutFile, [string]$InFile, [string]$Body, [string]$ContentType = 'application/json', [string]$Proxy)
    if ([uri]$Uri -and ([uri]$Uri).Scheme -ne 'https') { throw 'Release requests require HTTPS.' }
    $arguments = @{ Uri = $Uri; Method = $Method; Headers = $Headers; TimeoutSec = 180
        SkipHttpErrorCheck = $true; MaximumRedirection = 5; ErrorAction = 'Stop' }
    if ($Proxy) { $arguments.Proxy = $Proxy }
    if ($OutFile) { $arguments.OutFile = $OutFile; $arguments.PassThru = $true }
    if ($InFile) { $arguments.InFile = $InFile; $arguments.ContentType = $ContentType }
    elseif ($Body) { $arguments.Body = [Text.Encoding]::UTF8.GetBytes($Body); $arguments.ContentType = $ContentType }
    # Retry only idempotent reads. Never repeat npm publication or GitHub mutations.
    $attempts = if ($Method -in @('GET', 'HEAD')) { 3 } else { 1 }
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        try {
            $response = Invoke-WebRequest @arguments
            if ($Method -in @('GET', 'HEAD') -and ($response.StatusCode -eq 429 -or $response.StatusCode -ge 500) -and $attempt -lt $attempts) {
                Start-Sleep -Seconds ([Math]::Min(20, 2 * $attempt))
                continue
            }
            return @{ Status = [int]$response.StatusCode; Body = $response.Content; Headers = $response.Headers }
        } catch {
            if ($OutFile -and (Test-Path -LiteralPath $OutFile)) { Remove-Item -LiteralPath $OutFile -Force }
            if ($attempt -eq $attempts) { throw "Release HTTP transport failed for $(([uri]$Uri).Host). Credentials are not logged." }
            Start-Sleep -Seconds ([Math]::Min(20, 2 * $attempt))
        }
    }
}

function Assert-LocalReleaseReceipt {
    param([string]$Root, [string]$ReceiptPath)
    $receipt = Get-Content -LiteralPath $ReceiptPath -Raw | ConvertFrom-Json
    if ($receipt.schema -ne 1 -or $receipt.version -notmatch '^\d+\.\d+\.\d+$' -or
        $receipt.commit -notmatch '^[a-f0-9]{40}$' -or $receipt.sha256 -notmatch '^[a-f0-9]{64}$') {
        throw 'Invalid local acceptance receipt.'
    }
    $expected = @((Get-LocalReleaseSuites) + @('StartupUpdateOwnership', 'RuntimeBootstrap.Compiled', 'verify'))
    if (@(Compare-Object $expected @($receipt.checks)).Count -or $receipt.checks.Count -ne $expected.Count) {
        throw 'Receipt must include all 14 suites, StartupUpdateOwnership, compiled RuntimeBootstrap and verify.'
    }
    Assert-ReleaseClean $Root $receipt.commit
    [xml]$project = Get-Content -LiteralPath (Join-Path $Root 'source/DeepSeekHarness.csproj') -Raw
    if ($project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText -cne $receipt.version) { throw 'Receipt/source version mismatch.' }
    if ((Split-Path $receipt.zipPath -Leaf) -cne "Dafeiyu-GO $($receipt.version).zip" -or
        (Get-FileHash -LiteralPath $receipt.zipPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $receipt.sha256) {
        throw 'ZIP changed after acceptance.'
    }
    $receipt
}

function Set-SnapshotBuildSettings {
    param([string]$BuildScript, [string]$DotnetPath, [string]$NuGetCache)
    # The committed build entry has legacy machine defaults. Adapt ONLY its two
    # defaults in the disposable snapshot; keep all publish/InfoHost/detector logic.
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($BuildScript, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw 'Build script has syntax errors.' }
    $text = [IO.File]::ReadAllText($BuildScript)
    $edits = @()
    foreach ($setting in @(@('dotnet', $DotnetPath), @('localNuGet', $NuGetCache))) {
        $name = $setting[0]
        $nodes = @($ast.FindAll({ param($n)
            $n -is [Management.Automation.Language.AssignmentStatementAst] -and
            $n.Left -is [Management.Automation.Language.VariableExpressionAst] -and
            $n.Left.VariablePath.UserPath -eq $name -and
            $n.Right.Extent.Text.StartsWith("'")
        }, $true))
        if ($nodes.Count -ne 1) { throw "Build default changed ($name); review compatibility adapter." }
        $edits += @{ Start = $nodes[0].Right.Extent.StartOffset; End = $nodes[0].Right.Extent.EndOffset
            Value = "'" + $setting[1].Replace("'", "''") + "'" }
    }
    foreach ($edit in ($edits | Sort-Object Start -Descending)) {
        $text = $text.Substring(0, $edit.Start) + $edit.Value + $text.Substring($edit.End)
    }
    [IO.File]::WriteAllText($BuildScript, $text, [Text.UTF8Encoding]::new($false))
}

function Invoke-LocalReleaseBuild {
    param([string]$Root, [string]$WorkRoot, [string]$DotnetPath)
    Assert-ReleaseClean $Root
    $commit = Get-ReleaseGit $Root @('rev-parse', 'HEAD')
    [xml]$project = Get-Content -LiteralPath (Join-Path $Root 'source/DeepSeekHarness.csproj') -Raw
    $version = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Local release requires a three-part npm-compatible version.' }
    $run = Join-Path $WorkRoot ("$version-" + [guid]::NewGuid().ToString('N'))
    $snapshot = Join-Path $run 'snapshot'
    $archive = Join-Path $run 'source.tar'
    $dist = Join-Path $run 'dist'
    $pwsh = (Get-Process -Id $PID).Path
    New-Item -ItemType Directory -Path $snapshot -Force | Out-Null
    try {
        Invoke-ReleaseNative git @('-C', $Root, 'archive', '--format=tar', "--output=$archive", $commit)
        Invoke-ReleaseNative tar @('-xf', $archive, '-C', $snapshot)
        Push-Location $snapshot
        try {
            Invoke-ReleaseNative $pwsh @('-NoProfile', '-File', (Join-Path $snapshot 'check-release-gate.ps1'), '-Version', $version)
            $checks = @()
            foreach ($suite in (Get-LocalReleaseSuites)) {
                Write-Host "Regression: $suite"
                Invoke-ReleaseNative $DotnetPath @('run', '--project', "tests/$suite.Regression/$suite.Regression.csproj", '-c', 'Release', '-p:UseSharedCompilation=false')
                $checks += $suite
            }
            $build = Join-Path $snapshot 'source/build-winui.ps1'
            Set-SnapshotBuildSettings $build $DotnetPath $env:NUGET_PACKAGES
            Invoke-ReleaseNative $pwsh @('-NoProfile', '-File', $build, '-OutputDirectory', $dist)
            Invoke-ReleaseNative $DotnetPath @('run', '--project', 'tests/StartupUpdateOwnership.Regression/StartupUpdateOwnership.Regression.csproj', '-c', 'Release', '-p:UseSharedCompilation=false', '--', $dist)
            $checks += 'StartupUpdateOwnership'
            Invoke-ReleaseNative $pwsh @('-NoProfile', '-File', 'tests/RuntimeBootstrap.Regression/Run.ps1', '-BootstrapPath', (Join-Path $dist 'DeepSeek Harness.exe'))
            $checks += 'RuntimeBootstrap.Compiled'
            Invoke-ReleaseNative $pwsh @('-NoProfile', '-File', 'verify.ps1', '-Dist', $dist, '-Version', $version)
            $checks += 'verify'
            Assert-ReleaseClean $Root $commit
            $zip = Join-Path $run "Dafeiyu-GO $version.zip"
            Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $zip -CompressionLevel Optimal
            $receipt = [ordered]@{ schema = 1; version = $version; commit = $commit; checks = $checks
                zipPath = $zip; sha256 = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
                acceptedAt = [DateTime]::UtcNow.ToString('o') }
            $receiptPath = Join-Path $run 'acceptance.json'
            Write-ReleaseJson $receiptPath $receipt
            Write-Host "Local acceptance complete: $receiptPath"
            return $receiptPath
        } finally { Pop-Location }
    } finally {
        Remove-ReleaseScratch $snapshot $run
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
    }
}

if ($FunctionsOnly) { return }
$ErrorActionPreference = 'Stop'
if ($Publish -and $NoManifest) { throw '-NoManifest cannot be combined with -Publish.' }
if ($PushCommit -and -not $Publish) { throw '-PushCommit requires -Publish.' }
$WorkRoot = Get-LocalReleaseWorkRoot $PSScriptRoot $WorkRoot
if (-not $DotnetPath) {
    $DotnetPath = 'G:/DeepSeek DSH/.tools/dotnet/dotnet.exe'
    if (-not (Test-Path -LiteralPath $DotnetPath)) { $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
}
$DotnetPath = (Resolve-Path -LiteralPath $DotnetPath).Path
if (-not $NuGetCache) {
    foreach ($candidate in @($env:NUGET_PACKAGES, 'G:/DeepSeek DSH/.nuget-packages')) {
        if ($candidate -and $candidate -notmatch '^[cC]:' -and (Test-Path -LiteralPath $candidate -PathType Container)) {
            $NuGetCache = $candidate
            break
        }
    }
}
Invoke-LocalReleaseEnvironment $WorkRoot $Proxy $NuGetCache {
    $receiptPath = Invoke-LocalReleaseBuild $PSScriptRoot $WorkRoot $DotnetPath
    if ($Publish) {
        & (Join-Path $PSScriptRoot 'publish-release.ps1') -ReceiptPath $receiptPath -Repository $Repository -WorkRoot $WorkRoot -Proxy $Proxy -Otp $Otp -PushCommit:$PushCommit
    } else {
        Write-Host "Verified ZIP and acceptance receipt retained. Publish explicitly with:"
        Write-Host ".\publish-release.ps1 -ReceiptPath '$receiptPath' -WorkRoot '$WorkRoot'"
    }
}
