#Requires -Version 7.4
[CmdletBinding()]
param(
    [string]$ReceiptPath,
    [string]$Repository = 'YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Click-To-Run',
    [string]$WorkRoot,
    [string]$Proxy = 'http://127.0.0.1:7890',
    [string]$Otp,
    [switch]$PushCommit,
    [switch]$FunctionsOnly
)

foreach ($file in @('release.ps1', 'publish-npm.ps1')) {
    $libraryTokens = $null; $libraryErrors = $null
    $library = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $file), [ref]$libraryTokens, [ref]$libraryErrors)
    if ($libraryErrors.Count) { throw "$file syntax errors." }
    foreach ($definition in $library.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.FunctionDefinitionAst] }) {
        . ([scriptblock]::Create($definition.Extent.Text))
    }
}

function Get-LocalGitHubToken {
    param([string]$Root)
    foreach ($name in @('GH_TOKEN', 'GITHUB_TOKEN')) {
        $value = [Environment]::GetEnvironmentVariable($name, 'Process')
        if ($value) { return $value }
    }
    if (Get-Command gh -ErrorAction SilentlyContinue) {
        $value = & gh auth token --hostname github.com 2>$null
        if ($LASTEXITCODE -eq 0 -and $value) { return ($value -join '').Trim() }
    }
    $credential = "protocol=https`nhost=github.com`n`n" | & git -C $Root credential fill 2>$null
    if ($LASTEXITCODE -eq 0) {
        foreach ($line in $credential) {
            if ($line.StartsWith('password=')) { return $line.Substring(9) }
        }
    }
    throw 'GitHub authentication unavailable. Use existing gh auth, git credential manager or GH_TOKEN. Tokens are never written to disk.'
}

function Invoke-LocalGitHubApi {
    param([string]$Repository, [string]$Path, [hashtable]$Headers, [string]$Proxy,
        [string]$Method = 'GET', $Value)
    $arguments = @{ Uri = "https://api.github.com/repos/$Repository/$Path"; Headers = $Headers; Proxy = $Proxy; Method = $Method }
    if ($null -ne $Value) { $arguments.Body = $Value | ConvertTo-Json -Depth 10 -Compress }
    $response = Invoke-ReleaseHttp @arguments
    if ($response.Status -eq 404 -and $Method -eq 'GET') { return $null }
    if ($response.Status -lt 200 -or $response.Status -ge 300) {
        throw "GitHub $Method $Path failed (HTTP $($response.Status)). No overwrite or force retry is permitted."
    }
    if ($response.Body) { $response.Body | ConvertFrom-Json }
}

function Assert-LocalReleaseTag {
    param([string]$Repository, [string]$Tag, [string]$Commit, [hashtable]$Headers, [string]$Proxy)
    $reference = Invoke-LocalGitHubApi $Repository "git/ref/tags/$Tag" $Headers $Proxy
    if ($null -eq $reference) { return $false }
    $object = $reference.object
    for ($i = 0; $i -lt 8 -and $object.type -eq 'tag'; $i++) {
        $annotated = Invoke-LocalGitHubApi $Repository "git/tags/$($object.sha)" $Headers $Proxy
        $object = $annotated.object
    }
    if ($object.type -ne 'commit' -or $object.sha -cne $Commit) {
        throw "Existing tag $Tag does not identify accepted commit $Commit. Tags are immutable."
    }
    return $true
}

function Get-LocalGitHubRelease {
    param([string]$Repository, [string]$Tag, [hashtable]$Headers, [string]$Proxy)
    $release = Invoke-LocalGitHubApi $Repository "releases/tags/$Tag" $Headers $Proxy
    if ($release) { return $release }
    # Some API/auth combinations omit drafts from the tag endpoint.
    for ($page = 1; $page -le 20; $page++) {
        $releases = @(Invoke-LocalGitHubApi $Repository "releases?per_page=100&page=$page" $Headers $Proxy)
        $matches = @($releases | Where-Object tag_name -CEQ $Tag)
        if ($matches.Count -gt 1) { throw 'Multiple releases identify the same tag.' }
        if ($matches.Count -eq 1) { return $matches[0] }
        if ($releases.Count -lt 100) { return $null }
    }
    throw 'Release listing exceeded 2000 entries; refusing to create a possible duplicate draft.'
}

function Test-GitHubReleaseAsset {
    param([string]$Repository, $Release, $Receipt, [hashtable]$Headers, [string]$Proxy, [string]$Directory)
    $name = "Dafeiyu-GO $($Receipt.version).zip"
    $assets = @($Release.assets | Where-Object { $_.name -ceq $name -or $_.name -ceq $name.Replace(' ','.') })
    if ($assets.Count -gt 1) { throw 'Duplicate release assets are forbidden.' }
    if (-not $assets.Count) { return $false }
    $path = Join-Path $Directory ('github-' + [guid]::NewGuid().ToString('N') + '.zip')
    try {
        $downloadHeaders = $Headers.Clone()
        $downloadHeaders.Accept = 'application/octet-stream'
        $response = Invoke-ReleaseHttp -Uri "https://api.github.com/repos/$Repository/releases/assets/$($assets[0].id)" -Headers $downloadHeaders -OutFile $path -Proxy $Proxy
        if ($response.Status -ne 200 -or (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Receipt.sha256) {
            throw 'Existing GitHub asset differs from accepted ZIP; it will not be overwritten.'
        }
        return $true
    } finally { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force } }
}

function Get-LocalReleaseNotes {
    param([string]$Root, [string]$Version)
    $lines = Get-Content -LiteralPath (Join-Path $Root 'CHANGELOG.md') -Encoding utf8
    $collect = $false; $notes = @()
    foreach ($line in $lines) {
        if ($line -match '^##\s+(.+?)\s*$') {
            if ($collect) { break }
            $collect = $Matches[1] -in @($Version, "v$Version")
            continue
        }
        if ($collect) { $notes += $line }
    }
    if (-not $notes.Count) { throw "Missing CHANGELOG section for $Version. Commit release notes explicitly." }
    ($notes -join "`n").Trim()
}

function Write-LocalReleaseManifests {
    param([string]$Root, $Receipt, [string]$AssetUrl, [string]$Notes)
    if ($Receipt.version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?$') { throw 'Invalid manifest version.' }
    $versioned = "manifests/versions/manifest-$($Receipt.version).json"
    $paths = @((Join-Path $Root $versioned), (Join-Path $Root 'manifest.json'))
    New-Item -ItemType Directory -Path (Split-Path $paths[0] -Parent) -Force | Out-Null
    $manifest = [ordered]@{ version = $Receipt.version; sha256 = $Receipt.sha256; subDirectory = ''
        assets = [ordered]@{ github = $AssetUrl; mirrors = @() }; notes = $Notes; releasedAt = [DateTime]::UtcNow.ToString('o') }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json -Depth 10))
    # A versioned manifest is immutable too. The main feed is always the last write.
    if (Test-Path -LiteralPath $paths[0]) {
        $old = Get-Content -LiteralPath $paths[0] -Raw | ConvertFrom-Json
        if ($old.version -cne $Receipt.version -or $old.sha256 -cne $Receipt.sha256 -or $old.assets.github -cne $AssetUrl) {
            throw 'Existing versioned manifest conflicts with accepted release.'
        }
        $bytes = [IO.File]::ReadAllBytes($paths[0])
    }
    $saved = @{}
    foreach ($path in $paths) { $saved[$path] = $(if (Test-Path -LiteralPath $path) { [IO.File]::ReadAllBytes($path) } else { $null }) }
    try {
        foreach ($path in $paths) { [IO.File]::WriteAllBytes($path, $bytes) }
    } catch {
        foreach ($path in $paths) {
            if ($null -eq $saved[$path]) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force } }
            else { [IO.File]::WriteAllBytes($path, $saved[$path]) }
        }
        throw
    }
    Write-Host 'Both local manifests now describe fully downloaded and verified distribution bytes.'
    Write-Host 'Review, stage ONLY the two manifests, then explicitly commit and push them:'
    Write-Host "git add -- manifest.json $versioned"
    Write-Host "git diff --cached -- manifest.json $versioned"
    Write-Host "git commit --only -m 'release: manifests v$($Receipt.version)' -- manifest.json $versioned"
    Write-Host 'git push origin HEAD'
}

function Invoke-LocalGitHubPublish {
    param([string]$Root, [string]$ReceiptPath, [string]$Repository, [string]$WorkRoot, [string]$Proxy, [string]$Otp, [bool]$PushCommit)
    if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid GitHub repository.' }
    $receipt = Assert-LocalReleaseReceipt $Root $ReceiptPath
    $notes = Get-LocalReleaseNotes $Root $receipt.version
    & (Join-Path $Root 'check-release-gate.ps1') -Version $receipt.version -Root $Root | Out-Host
    $directory = Join-Path $WorkRoot ('github-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $headers = @{}
    try {
        $headers = @{ Authorization = 'Bearer ' + (Get-LocalGitHubToken $Root)
            Accept = 'application/vnd.github+json'; 'User-Agent' = 'Dafeiyu-Go-LocalRelease'; 'X-GitHub-Api-Version' = '2022-11-28' }
        $tag = "v$($receipt.version)"
        $exists = Assert-LocalReleaseTag $Repository $tag $receipt.commit $headers $Proxy
        $release = Get-LocalGitHubRelease $Repository $tag $headers $Proxy
        if ($release -and -not $exists) { throw 'Release exists without the expected immutable tag.' }
        $hasAsset = $false
        if ($release) { $hasAsset = Test-GitHubReleaseAsset $Repository $release $receipt $headers $Proxy $directory }
        if ($PushCommit) {
            $origin = Get-ReleaseGit $Root @('remote', 'get-url', 'origin')
            if ($origin -notmatch ('^(https://github\.com/|git@github\.com:)' + [regex]::Escape($Repository) + '(\.git)?/?$')) {
                throw 'origin must match -Repository before pushing the existing commit.'
            }
            $branch = Get-ReleaseGit $Root @('symbolic-ref', '--quiet', '--short', 'HEAD')
            if (-not $branch) { throw '-PushCommit requires an attached branch.' }
            $null = Assert-LocalReleaseReceipt $Root $ReceiptPath
            Invoke-ReleaseNative git @('-C', $Root, 'push', 'origin', "$($receipt.commit):refs/heads/$branch")
        }
        $remoteCommit = Invoke-LocalGitHubApi $Repository "commits/$($receipt.commit)" $headers $Proxy
        if (-not $remoteCommit -or $remoteCommit.sha -cne $receipt.commit) {
            throw 'Accepted source commit is not on GitHub. Push it explicitly or use -PushCommit.'
        }
        # No GitHub mutation until npm and the complete mirrored ZIP have passed.
        $proofPath = Invoke-LocalNpmPublish $Root $ReceiptPath $WorkRoot '@yunxiramito/dsh-launcher' $Proxy $Otp $false $false 25
        $proof = Get-Content -LiteralPath $proofPath -Raw | ConvertFrom-Json
        if ($proof.sha256 -cne $receipt.sha256 -or $proof.version -cne $receipt.version -or
            (Get-FileHash $proof.path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $proof.tarSha256) { throw 'Mirror proof changed.' }
        Test-ReleaseTarball $proof.path $receipt.version '@yunxiramito/dsh-launcher' $receipt.sha256
        $null = Assert-LocalReleaseReceipt $Root $ReceiptPath
        $exists = Assert-LocalReleaseTag $Repository $tag $receipt.commit $headers $Proxy
        if (-not $exists) {
            $null = Invoke-LocalGitHubApi $Repository 'git/refs' $headers $Proxy 'POST' @{ ref = "refs/tags/$tag"; sha = $receipt.commit }
        }
        $null = Assert-LocalReleaseTag $Repository $tag $receipt.commit $headers $Proxy
        $release = Get-LocalGitHubRelease $Repository $tag $headers $Proxy
        if (-not $release) {
            $release = Invoke-LocalGitHubApi $Repository 'releases' $headers $Proxy 'POST' @{
                tag_name = $tag; target_commitish = $receipt.commit; name = $tag; draft = $true; prerelease = $false
                body = $notes
            }
        }
        $hasAsset = Test-GitHubReleaseAsset $Repository $release $receipt $headers $Proxy $directory
        $zipName = "Dafeiyu-GO $($receipt.version).zip"
        if (-not $hasAsset) {
            $upload = "https://uploads.github.com/repos/$Repository/releases/$($release.id)/assets?name=$([uri]::EscapeDataString($zipName))&label=$([uri]::EscapeDataString($zipName))"
            $response = Invoke-ReleaseHttp -Uri $upload -Method POST -Headers $headers -Proxy $Proxy -InFile $receipt.zipPath -ContentType 'application/zip'
            if ($response.Status -ne 201) { throw "Asset upload failed (HTTP $($response.Status)); existing assets are never deleted." }
        }
        $release = Invoke-LocalGitHubApi $Repository "releases/$($release.id)" $headers $Proxy
        if (-not (Test-GitHubReleaseAsset $Repository $release $receipt $headers $Proxy $directory)) { throw 'Uploaded asset missing.' }
        if ($release.draft) {
            $release = Invoke-LocalGitHubApi $Repository "releases/$($release.id)" $headers $Proxy 'PATCH' @{ draft = $false }
        }
        if ($release.prerelease) { throw 'Stable manifests cannot point to a prerelease.' }
        $publicAsset = @($release.assets | Where-Object { $_.name -ceq $zipName -or $_.name -ceq $zipName.Replace(' ','.') })
        if ($publicAsset.Count -ne 1) { throw 'Expected one accepted public launcher asset.' }
        $assetUrl = "https://github.com/$Repository/releases/download/$tag/$([uri]::EscapeDataString($publicAsset[0].name))"
        $publicZip = Join-Path $directory 'public.zip'
        $response = Invoke-ReleaseHttp -Uri $assetUrl -OutFile $publicZip -Proxy $Proxy
        if ($response.Status -ne 200 -or (Get-FileHash $publicZip -Algorithm SHA256).Hash.ToLowerInvariant() -cne $receipt.sha256) {
            throw 'Public GitHub download does not match acceptance. Public manifests remain unchanged.'
        }
        $null = Assert-LocalReleaseTag $Repository $tag $receipt.commit $headers $Proxy
        $null = Assert-LocalReleaseReceipt $Root $ReceiptPath
        Write-LocalReleaseManifests $Root $receipt $assetUrl $notes
    } finally {
        $headers.Clear()
        Remove-ReleaseScratch $directory $WorkRoot
    }
}

if ($FunctionsOnly) { return }
$ErrorActionPreference = 'Stop'
if (-not $ReceiptPath) { throw 'Use -ReceiptPath from release.ps1 local acceptance; raw ZIP publication is not supported.' }
$WorkRoot = Get-LocalReleaseWorkRoot $PSScriptRoot $WorkRoot
Invoke-LocalReleaseEnvironment $WorkRoot $Proxy '' {
    Invoke-LocalGitHubPublish $PSScriptRoot $ReceiptPath $Repository $WorkRoot $Proxy $Otp $PushCommit.IsPresent
}
