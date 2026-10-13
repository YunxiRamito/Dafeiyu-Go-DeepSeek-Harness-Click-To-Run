#Requires -Version 7.4
# Offline boundary tests. HTTP, git and publication are mocked; no application regressions run.
[CmdletBinding()]
param([string]$WorkRoot)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
foreach ($name in @('release.ps1', 'publish-npm.ps1', 'publish-release.ps1')) {
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repoRoot $name), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw "$name parse errors: $errors" }
    foreach ($definition in $ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.FunctionDefinitionAst] }) {
        . ([scriptblock]::Create($definition.Extent.Text))
    }
    Write-Host "PASS parse $name"
}

function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Throws([scriptblock]$Action, [string]$Pattern) {
    try { & $Action } catch {
        if ($_.Exception.Message -match $Pattern) { return }
        throw "Unexpected error: $($_.Exception.Message); expected $Pattern"
    }
    throw "Expected error: $Pattern"
}
$WorkRoot = Get-LocalReleaseWorkRoot $repoRoot $WorkRoot
# Any accidental real external call fails immediately.
function git { throw 'Unmocked git invocation' }
function npm { throw 'Unmocked npm invocation' }
function gh { throw 'Unmocked gh invocation' }
function Invoke-WebRequest { throw 'Unmocked HTTP invocation' }

$fixtureRoot = Join-Path $WorkRoot ('smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
$fakeRoot = Join-Path $fixtureRoot 'repo'
New-Item -ItemType Directory -Path (Join-Path $fakeRoot 'source') -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $fakeRoot 'source/DeepSeekHarness.csproj'), '<Project><PropertyGroup><Version>9.8.7</Version></PropertyGroup></Project>')
[IO.File]::WriteAllText((Join-Path $fakeRoot 'CHANGELOG.md'), "## 9.8.7`nOffline fixture")
[IO.File]::WriteAllText((Join-Path $fakeRoot 'check-release-gate.ps1'), 'param($Version, $Root)')
$fixtureZip = Join-Path $fixtureRoot 'DeepSeekHarness-9.8.7.zip'
[IO.File]::WriteAllBytes($fixtureZip, [byte[]](1, 2, 3, 4, 5))
$fixtureHash = (Get-FileHash $fixtureZip -Algorithm SHA256).Hash.ToLowerInvariant()
$testReceipt = Join-Path $fixtureRoot 'acceptance.json'
$fixtureReceipt = [pscustomobject]@{ schema = 1; version = '9.8.7'; commit = ('a' * 40); zipPath = $fixtureZip
    sha256 = $fixtureHash; checks = @((Get-LocalReleaseSuites) + @('StartupUpdateOwnership', 'RuntimeBootstrap.Compiled', 'verify')) }
Write-ReleaseJson $testReceipt $fixtureReceipt

function New-FixtureTar([string]$Path, [byte[]]$ZipBytes) {
    Add-Type -AssemblyName System.Formats.Tar
    $file = [IO.File]::Create($Path)
    $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionLevel]::Optimal)
    $writer = [System.Formats.Tar.TarWriter]::new($gzip, $true)
    try {
        foreach ($item in @(@('package/package.json', [Text.Encoding]::UTF8.GetBytes('{"name":"@yunxiramito/dsh-launcher","version":"9.8.7"}')),
            @('package/DeepSeekHarness-9.8.7.zip', $ZipBytes))) {
            $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, $item[0])
            $entry.DataStream = [IO.MemoryStream]::new([byte[]]$item[1])
            try { $writer.WriteEntry($entry) } finally { $entry.DataStream.Dispose() }
        }
    } finally { $writer.Dispose(); $gzip.Dispose(); $file.Dispose() }
}
$fixtureTar = Join-Path $fixtureRoot 'good.tgz'
$badTar = Join-Path $fixtureRoot 'wrong-zip.tgz'
New-FixtureTar $fixtureTar ([IO.File]::ReadAllBytes($fixtureZip))
New-FixtureTar $badTar ([byte[]](9, 9, 9))

try {
    Assert-Throws { Get-LocalReleaseWorkRoot $fakeRoot 'C:/Temp_SetupandLauncher/release' } 'non-C'
    Assert-Throws { Get-LocalReleaseWorkRoot $fakeRoot 'G:/other-release' } 'Temp_SetupandLauncher'
    Assert-Throws { Remove-ReleaseScratch $fakeRoot (Join-Path $fixtureRoot 'other') } 'outside scratch'
    Write-Host 'PASS path and deletion boundaries'

    $environmentBefore = @{}
    foreach ($name in @('TEMP', 'TMP', 'TMPDIR', 'DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'npm_config_cache', 'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY', 'GODEBUG', 'GIT_TERMINAL_PROMPT')) {
        $environmentBefore[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
    Assert-Throws {
        Invoke-LocalReleaseEnvironment (Join-Path $fixtureRoot 'env') '' '' {
            Assert ($env:TEMP.StartsWith($fixtureRoot)) 'TEMP escaped project scratch'
            Assert ($env:npm_config_cache.StartsWith($fixtureRoot)) 'npm cache escaped project scratch'
            Assert ($env:GODEBUG -match 'http2client=0') 'gh HTTP2 mitigation missing'
            throw 'Injected environment failure'
        }
    } 'Injected environment failure'
    foreach ($name in $environmentBefore.Keys) {
        Assert ([Environment]::GetEnvironmentVariable($name, 'Process') -ceq $environmentBefore[$name]) "Environment not restored: $name"
    }
    Write-Host 'PASS environment finally restoration after failure'

    & {
        function Get-ReleaseGit { param($Root, $Arguments)
            if ($Arguments[0] -eq 'status') { return '' }
            'a' * 40
        }
        $null = Assert-LocalReleaseReceipt $fakeRoot $testReceipt
        $invalid = Join-Path $fixtureRoot 'invalid.json'
        $copy = $fixtureReceipt | ConvertTo-Json | ConvertFrom-Json
        $copy.checks = @('verify'); Write-ReleaseJson $invalid $copy
        Assert-Throws { Assert-LocalReleaseReceipt $fakeRoot $invalid } 'all 14'
        $copy.checks = $fixtureReceipt.checks; $copy.sha256 = 'b' * 64; Write-ReleaseJson $invalid $copy
        Assert-Throws { Assert-LocalReleaseReceipt $fakeRoot $invalid } 'ZIP changed'
        function Get-ReleaseGit { param($Root, $Arguments) ' M source/changed.cs' }
        Assert-Throws { Assert-LocalReleaseReceipt $fakeRoot $testReceipt } 'Commit tracked changes'
    }
    Test-ReleaseTarball $fixtureTar '9.8.7' '@yunxiramito/dsh-launcher' $fixtureHash
    Assert-Throws { Test-ReleaseTarball $badTar '9.8.7' '@yunxiramito/dsh-launcher' $fixtureHash } 'SHA256 mismatch'
    Write-Host 'PASS acceptance gates and actual tgz embedded ZIP hashing'

    & {
        $script:nativeCalls = [Collections.Generic.List[object]]::new()
        function Get-ReleaseGit { param($Root, $Arguments)
            if ($Arguments[0] -eq 'status') { return '' }
            'a' * 40
        }
        function Invoke-ReleaseNative { param($File, $Arguments)
            $script:nativeCalls.Add(@{ File = $File; Arguments = $Arguments })
            if ($script:failNative -and ($Arguments -join ' ') -match 'ComponentRuntime.Regression') { throw 'Injected regression failure' }
            if ($File -eq 'tar') {
                $snapshot = $Arguments[3]
                Copy-Item -LiteralPath (Join-Path $fakeRoot 'source') -Destination $snapshot -Recurse
                Copy-Item -LiteralPath (Join-Path $repoRoot 'source/build-winui.ps1') -Destination (Join-Path $snapshot 'source/build-winui.ps1')
            }
            if ($Arguments -contains '-OutputDirectory') {
                $dist = $Arguments[-1]
                New-Item -ItemType Directory -Path $dist -Force | Out-Null
                [IO.File]::WriteAllText((Join-Path $dist 'DeepSeek Harness.exe'), 'simulated build')
            }
        }
        Invoke-LocalReleaseEnvironment (Join-Path $fixtureRoot 'build') '' '' {
            $builtReceipt = Invoke-LocalReleaseBuild $fakeRoot (Join-Path $fixtureRoot 'build') 'fixture-dotnet.exe'
            $accepted = Get-Content $builtReceipt -Raw | ConvertFrom-Json
            Assert ($accepted.checks.Count -eq 17) 'A regression or verifier was skipped'
            Assert (-not (Test-Path (Join-Path (Split-Path $builtReceipt -Parent) 'snapshot'))) 'Snapshot not cleaned'
            $suiteCalls = @($script:nativeCalls | Where-Object { $_.File -eq 'fixture-dotnet.exe' })
            Assert ($suiteCalls.Count -eq 15) 'Must run 14 regression suites plus ownership'
            Assert (@($script:nativeCalls | Where-Object { $_.Arguments -contains '-BootstrapPath' }).Count -eq 1) 'Compiled bootstrap gate missing'
            Assert (@($script:nativeCalls | Where-Object { $_.Arguments -contains '-OutputDirectory' }).Count -eq 1) 'Official build entry missing'
            $script:failNative = $true
            Assert-Throws { Invoke-LocalReleaseBuild $fakeRoot (Join-Path $fixtureRoot 'build-fail') 'fixture-dotnet.exe' } 'Injected regression failure'
            Assert (@(Get-ChildItem (Join-Path $fixtureRoot 'build-fail') -Filter acceptance.json -Recurse).Count -eq 0) 'Failed regression produced acceptance'
            Assert (@(Get-ChildItem (Join-Path $fixtureRoot 'build-fail') -Directory -Recurse | Where-Object Name -EQ 'snapshot').Count -eq 0) 'Failed regression leaked snapshot'
        }
    }
    Write-Host 'PASS mocked full local pipeline invokes all mandatory checks and cleans snapshot'

    & {
        $script:readCount = 0; $script:waitCount = 0
        function Start-Sleep { param($Seconds) $script:waitCount++ }
        function Invoke-WebRequest {
            $script:readCount++
            if ($script:readCount -lt 3) { throw 'EOF signedURL=secret-JWT' }
            @{ StatusCode = 200; Content = '{}'; Headers = @{} }
        }
        $null = Invoke-ReleaseHttp -Uri 'https://api.github.com/test'
        Assert ($script:readCount -eq 3 -and $script:waitCount -eq 2) 'GET retry failed'
        $script:readCount = 0
        Assert-Throws { Invoke-ReleaseHttp -Uri 'https://api.github.com/test' -Method POST -Body '{}' } '^Release HTTP transport failed for api.github.com. Credentials are not logged.$'
        Assert ($script:readCount -eq 1) 'Mutation was retried'
    }
    Write-Host 'PASS EOF read retries, no mutation retries, secret-free errors'

    & {
        $script:metadataReads = 0; $script:syncCalls = 0; $script:mirrorReads = 0
        function Start-Sleep { param($Seconds) }
        function Invoke-ReleaseHttp { param($Uri, $Method = 'GET', $OutFile, $Proxy, $Headers)
            if ($Uri -match 'registry.npmjs.org/.+cacheBust=') {
                $script:metadataReads++
                $body = if ($script:metadataReads -lt 3) { '{"versions":{}}' } else {
                    '{"versions":{"9.8.7":{"dist":{"tarball":"https://registry.npmjs.org/example.tgz"}}}}'
                }
                return @{ Status = 200; Body = $body }
            }
            if ($Uri -eq 'https://registry.npmjs.org/example.tgz') { Copy-Item $fixtureTar $OutFile; return @{ Status = 200 } }
            if ($Method -eq 'PUT') { $script:syncCalls++; return @{ Status = 200; Body = 'success' } }
            Assert ($Uri -match 'registry.npmmirror.com/.+\.tgz\?cacheBust=') 'Mirror must download actual cache-busted tarball'
            $script:mirrorReads++
            Copy-Item $(if ($script:mirrorReads -eq 1) { $badTar } else { $fixtureTar }) $OutFile -Force
            @{ Status = 200 }
        }
        Wait-ReleaseNpmVersion $fixtureReceipt '@yunxiramito/dsh-launcher' $fixtureRoot '' 1
        Assert ($script:metadataReads -eq 3) 'npm processing was not polled using fullmetadata'
        $null = Wait-ReleaseMirror $fixtureReceipt '@yunxiramito/dsh-launcher' $fixtureRoot '' 1
        Assert ($script:mirrorReads -eq 2 -and $script:syncCalls -eq 1) 'Mirror sync success/stale tarball bypassed hashing'
    }
    Write-Host 'PASS npm processing and stale mirror success require actual verified tarballs'

    foreach ($npmScenario in @('first', 'resume', 'existing', 'wrong-existing', 'pack-only')) {
        & {
            $journal = Join-Path $fixtureRoot 'npm-publication.json'
            if (Test-Path $journal) { Remove-Item -LiteralPath $journal }
            if ($npmScenario -eq 'resume') { Write-ReleaseJson $journal @{ version = '9.8.7'; sha256 = $fixtureHash } }
            $script:publishCalls = 0; $script:npmReads = 0
            function Assert-LocalReleaseReceipt { param($Root, $ReceiptPath) $fixtureReceipt }
            function Start-Sleep { param($Seconds) }
            function npm {
                $global:LASTEXITCODE = 0
                if ($args[0] -eq 'pack') {
                    Copy-Item $fixtureTar (Join-Path $PWD 'fixture.tgz')
                    return '[{"filename":"fixture.tgz"}]'
                }
                Assert ($args[0] -eq 'publish') 'Unexpected npm command'
                $script:publishCalls++
            }
            function Get-ReleaseNpmVersion {
                $script:npmReads++
                if ($npmScenario -notin @('existing', 'wrong-existing') -and $script:npmReads -lt 4) { return $null }
                @{ dist = @{ tarball = 'https://registry.npmjs.org/fixture.tgz' } }
            }
            function Invoke-ReleaseHttp { param($Uri, $OutFile, $Proxy)
                Assert ($Uri -eq 'https://registry.npmjs.org/fixture.tgz') 'Unexpected npm tarball URL'
                Copy-Item $(if ($npmScenario -eq 'wrong-existing') { $badTar } else { $fixtureTar }) $OutFile -Force
                @{ Status = 200 }
            }
            function Wait-ReleaseMirror { 'fixture-proof.json' }
            if ($npmScenario -eq 'wrong-existing') {
                Assert-Throws { Invoke-LocalNpmPublish $fakeRoot $testReceipt $fixtureRoot '@yunxiramito/dsh-launcher' '' '' $false $false 1 } 'SHA256 mismatch'
            } else {
                $null = Invoke-LocalNpmPublish $fakeRoot $testReceipt $fixtureRoot '@yunxiramito/dsh-launcher' '' '' ($npmScenario -eq 'pack-only') $false 1
            }
            Assert ($script:publishCalls -eq $(if ($npmScenario -eq 'first') { 1 } else { 0 })) 'npm was republished during processing/resume'
            if ($npmScenario -eq 'pack-only') { Assert ($script:npmReads -eq 0) 'Offline pack accessed registry' }
            Assert (@(Get-ChildItem $fixtureRoot -Directory | Where-Object Name -LIKE 'npm-*').Count -eq 0) 'npm staging leaked'
        }
        Write-Host "PASS mocked npm scenario: $npmScenario"
    }

    foreach ($scenario in @('new', 'asset', 'draft', 'conflict', 'tag-conflict', 'public-corrupt')) {
        & {
            $script:mutations = [Collections.Generic.List[string]]::new()
            $script:events = [Collections.Generic.List[string]]::new()
            $script:tagExists = $scenario -ne 'new'
            $script:currentRelease = if ($scenario -eq 'new') { $null } else {
                [pscustomobject]@{ id = 42; tag_name = 'v9.8.7'; draft = ($scenario -eq 'draft'); prerelease = $false
                    assets = @([pscustomobject]@{ id = 43; name = 'DeepSeekHarness-9.8.7.zip' }) }
            }
            function Assert-LocalReleaseReceipt { param($Root, $ReceiptPath) $fixtureReceipt }
            function Get-LocalGitHubToken { param($Root) 'test-secret' }
            function Invoke-LocalNpmPublish {
                $script:events.Add('npm-mirror-verified')
                $path = Join-Path $fixtureRoot 'proof.json'
                Write-ReleaseJson $path @{ version = '9.8.7'; sha256 = $fixtureHash; path = $fixtureTar
                    tarSha256 = (Get-FileHash $fixtureTar -Algorithm SHA256).Hash.ToLowerInvariant() }
                $path
            }
            function Write-LocalReleaseManifests { $script:events.Add('manifests') }
            function Invoke-ReleaseHttp { param($Uri, $Method = 'GET', $Headers, $OutFile, $InFile, $ContentType, $Proxy, $Body)
                if ($Method -ne 'GET') { $script:mutations.Add("$Method $Uri") }
                if ($Uri -match '/git/ref/tags/') {
                    if (-not $script:tagExists) { return @{ Status = 404 } }
                    return @{ Status = 200; Body = (@{ object = @{ type = 'commit'; sha = $(if ($scenario -eq 'tag-conflict') { 'b' * 40 } else { 'a' * 40 }) } } | ConvertTo-Json -Depth 5) }
                }
                if ($Uri -match '/commits/') { return @{ Status = 200; Body = (@{ sha = 'a' * 40 } | ConvertTo-Json) } }
                if ($Uri -match '/git/refs$') { $script:tagExists = $true; return @{ Status = 201; Body = '{}' } }
                if ($Uri -match '/releases\?') { return @{ Status = 200; Body = $(if ($scenario -eq 'draft') { ConvertTo-Json -InputObject @($script:currentRelease) -Depth 5 } else { '[]' }) } }
                if ($Uri -match '/releases/assets/' -or $Uri -match '/releases/download/') {
                    $script:events.Add($(if ($Uri -match '/download/') { 'public-download' } else { 'asset-download' }))
                    if ($scenario -eq 'conflict' -or ($scenario -eq 'public-corrupt' -and $Uri -match '/download/')) { [IO.File]::WriteAllText($OutFile, 'corrupt') }
                    else { Copy-Item $fixtureZip $OutFile -Force }
                    return @{ Status = 200 }
                }
                if ($Uri -match 'uploads.github.com') {
                    $script:currentRelease.assets = @([pscustomobject]@{ id = 43; name = 'DeepSeekHarness-9.8.7.zip' })
                    return @{ Status = 201; Body = '{}' }
                }
                if ($Uri -match '/releases$' -and $Method -eq 'POST') {
                    $payload = $Body | ConvertFrom-Json
                    Assert ($payload.body -ceq 'Offline fixture') 'Release body must be the original CHANGELOG body without a version heading or appended metadata'
                    $script:currentRelease = [pscustomobject]@{ id = 42; tag_name = 'v9.8.7'; draft = $true; prerelease = $false; assets = @() }
                }
                if ($Method -eq 'PATCH') { $script:currentRelease.draft = $false }
                if ($scenario -eq 'draft' -and $Uri -match '/releases/tags/') { return @{ Status = 404 } }
                if (-not $script:currentRelease) { return @{ Status = 404 } }
                @{ Status = 200; Body = ($script:currentRelease | ConvertTo-Json -Depth 5) }
            }
            if ($scenario -in @('conflict', 'tag-conflict', 'public-corrupt')) {
                Assert-Throws { Invoke-LocalGitHubPublish $fakeRoot $testReceipt 'owner/repo' $fixtureRoot '' '' $false } 'differs|immutable|does not match'
                Assert (-not $script:events.Contains('manifests')) 'Failure updated manifests'
                if ($scenario -ne 'public-corrupt') { Assert ($script:mutations.Count -eq 0) 'Conflict mutated GitHub'; Assert (-not $script:events.Contains('npm-mirror-verified')) 'Conflict published npm' }
            } else {
                Invoke-LocalGitHubPublish $fakeRoot $testReceipt 'owner/repo' $fixtureRoot '' '' $false
                Assert ($script:events[-1] -eq 'manifests') 'Manifests must be last'
                Assert ($script:events.Contains('public-download')) 'Public GitHub bytes not downloaded'
                if ($scenario -eq 'asset') { Assert ($script:mutations.Count -eq 0) 'Existing identical asset was mutated' }
                if ($scenario -eq 'draft') { Assert ($script:mutations.Count -eq 1 -and $script:mutations[0] -match '^PATCH ') 'Draft was recreated/reuploaded' }
                if ($scenario -eq 'new') { Assert ($script:mutations.Count -eq 4) 'New release must create tag, draft, asset, then publish draft' }
            }
            Assert (@($script:mutations | Where-Object { $_ -match '^(DELETE|PUT) ' }).Count -eq 0) 'Destructive GitHub request'
        }
        Write-Host "PASS mocked GitHub scenario: $scenario"
    }
    Write-Host 'PASS all local release boundary tests (offline, no application regression rerun)'
} finally { Remove-ReleaseScratch $fixtureRoot $WorkRoot }
