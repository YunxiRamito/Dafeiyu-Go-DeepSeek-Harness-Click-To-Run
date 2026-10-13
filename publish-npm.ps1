#Requires -Version 7.4
[CmdletBinding()]
param(
    [string]$ReceiptPath,
    [string]$WorkRoot,
    [string]$PackageName = '@yunxiramito/dsh-launcher',
    [string]$Proxy = 'http://127.0.0.1:7890',
    [string]$Otp,
    [switch]$NoPublish,
    [switch]$VerifyOnly,
    [ValidateRange(1, 60)][int]$MirrorTimeoutMinutes = 25,
    [switch]$FunctionsOnly
)

# Import definitions only, so the library's parameters cannot overwrite this command's switches.
$libraryTokens = $null; $libraryErrors = $null
$library = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'release.ps1'), [ref]$libraryTokens, [ref]$libraryErrors)
if ($libraryErrors.Count) { throw 'release.ps1 syntax errors.' }
foreach ($definition in $library.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.FunctionDefinitionAst] }) {
    . ([scriptblock]::Create($definition.Extent.Text))
}

function Test-ReleaseTarball {
    param([string]$Path, [string]$Version, [string]$PackageName, [string]$ExpectedSha256)
    Add-Type -AssemblyName System.Formats.Tar
    $file = [IO.File]::OpenRead($Path)
    $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionMode]::Decompress)
    $reader = [System.Formats.Tar.TarReader]::new($gzip)
    $zipCount = 0; $packageCount = 0
    try {
        while ($null -ne ($entry = $reader.GetNextEntry())) {
            if ($entry.Name -eq "package/DeepSeekHarness-$Version.zip") {
                if ($null -eq $entry.DataStream) { throw 'ZIP is not a regular tar entry.' }
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $actual = [Convert]::ToHexString($sha.ComputeHash($entry.DataStream)).ToLowerInvariant() }
                finally { $sha.Dispose() }
                if ($actual -cne $ExpectedSha256) { throw 'Embedded ZIP SHA256 mismatch.' }
                $zipCount++
            } elseif ($entry.Name -eq 'package/package.json') {
                if ($entry.Length -gt 1MB -or $null -eq $entry.DataStream) { throw 'Invalid package metadata.' }
                $textReader = [IO.StreamReader]::new($entry.DataStream, [Text.Encoding]::UTF8, $true, 4096, $true)
                try { $metadata = $textReader.ReadToEnd() | ConvertFrom-Json } finally { $textReader.Dispose() }
                if ($metadata.name -cne $PackageName -or $metadata.version -cne $Version) { throw 'Tarball package identity mismatch.' }
                $packageCount++
            }
        }
        if ($zipCount -ne 1 -or $packageCount -ne 1) { throw 'Tarball must contain exactly one expected ZIP and package.json.' }
        # Drain the gzip stream to validate the complete response, including its trailer.
        $gzip.CopyTo([IO.Stream]::Null)
    } finally { $reader.Dispose(); $gzip.Dispose(); $file.Dispose() }
}

function Wait-ReleaseMirror {
    param($Receipt, [string]$PackageName, [string]$Directory, [string]$Proxy, [int]$TimeoutMinutes)
    $bare = ($PackageName -split '/')[-1]
    $url = "https://registry.npmmirror.com/$PackageName/-/$bare-$($Receipt.version).tgz"
    $sync = "https://registry.npmmirror.com/-/package/$PackageName/syncs"
    $path = Join-Path $Directory 'mirror.tgz'
    $deadline = [DateTime]::UtcNow.AddMinutes($TimeoutMinutes)
    do {
        $verified = $false
        try {
            $response = Invoke-ReleaseHttp -Uri ($url + '?cacheBust=' + [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()) -OutFile $path -Proxy $Proxy
            if ($response.Status -eq 200) {
                Test-ReleaseTarball $path $Receipt.version $PackageName $Receipt.sha256
                $verified = $true
            }
        } catch { Write-Host 'Mirror download/content validation not ready; public manifests remain unchanged.' }
        if ($verified) {
            $proof = [ordered]@{ schema = 1; version = $Receipt.version; sha256 = $Receipt.sha256
                packageName = $PackageName; url = $url; path = $path
                tarSha256 = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
                verifiedAt = [DateTime]::UtcNow.ToString('o') }
            $proofPath = Join-Path $Directory 'mirror-proof.json'
            Write-ReleaseJson $proofPath $proof
            return $proofPath
        }
        try { $null = Invoke-ReleaseHttp -Uri $sync -Method PUT -Proxy $Proxy } catch { }
        if ([DateTime]::UtcNow -ge $deadline) { break }
        Write-Host 'Waiting 20 seconds for a complete verified npmmirror tarball...'
        Start-Sleep -Seconds 20
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Mirror did not deliver the accepted ZIP within the timeout. No manifest can be updated.'
}

function Get-ReleaseNpmVersion {
    param([string]$PackageName, [string]$Version, [string]$Proxy)
    # /<version> can retain a cached 404 while npm is processing a successful publish.
    $url = 'https://registry.npmjs.org/' + [uri]::EscapeDataString($PackageName) + '?cacheBust=' + [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $response = Invoke-ReleaseHttp -Uri $url -Headers @{ Accept = 'application/json' } -Proxy $Proxy
    if ($response.Status -eq 404) { return $null }
    if ($response.Status -ne 200) { throw "npm full metadata failed (HTTP $($response.Status))." }
    $metadata = $response.Body | ConvertFrom-Json -AsHashtable
    if ($metadata.versions -and $metadata.versions.ContainsKey($Version)) { return $metadata.versions[$Version] }
    return $null
}

function Wait-ReleaseNpmVersion {
    param($Receipt, [string]$PackageName, [string]$Directory, [string]$Proxy, [int]$TimeoutMinutes)
    $deadline = [DateTime]::UtcNow.AddMinutes($TimeoutMinutes)
    do {
        $metadata = Get-ReleaseNpmVersion $PackageName $Receipt.version $Proxy
        if ($null -ne $metadata) {
            $url = [uri]$metadata.dist.tarball
            if ($url.Scheme -ne 'https' -or $url.Host -ne 'registry.npmjs.org') { throw 'Unexpected npm tarball origin.' }
            $remote = Join-Path $Directory 'registry.tgz'
            $download = Invoke-ReleaseHttp -Uri $url.AbsoluteUri -OutFile $remote -Proxy $Proxy
            if ($download.Status -eq 200) {
                Test-ReleaseTarball $remote $Receipt.version $PackageName $Receipt.sha256
                return
            }
            if ($download.Status -ne 404) { throw "npm tarball download failed (HTTP $($download.Status))." }
        }
        if ([DateTime]::UtcNow -ge $deadline) { break }
        Write-Host 'npm processing: waiting 20 seconds; publication will not be repeated.'
        Start-Sleep -Seconds 20
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'npm processing timed out. Resume with the same acceptance receipt; never immediately publish again.'
}

function Invoke-LocalNpmPublish {
    param([string]$Root, [string]$ReceiptPath, [string]$WorkRoot, [string]$PackageName,
        [string]$Proxy, [string]$Otp, [bool]$NoPublish, [bool]$VerifyOnly, [int]$MirrorTimeoutMinutes)
    $receipt = Assert-LocalReleaseReceipt $Root $ReceiptPath
    if ($PackageName -cne '@yunxiramito/dsh-launcher') { throw 'Installer distribution requires @yunxiramito/dsh-launcher.' }
    $stage = Join-Path $WorkRoot ('npm-' + [guid]::NewGuid().ToString('N'))
    $proofDir = Join-Path (Split-Path $ReceiptPath -Parent) ('mirror-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    try {
        $zipName = "DeepSeekHarness-$($receipt.version).zip"
        Write-ReleaseJson (Join-Path $stage 'package.json') ([ordered]@{
            name = $PackageName; version = $receipt.version; description = 'Dafeiyu-Go launcher distribution'
            license = 'MIT'; files = @($zipName)
        })
        Copy-Item -LiteralPath $receipt.zipPath -Destination (Join-Path $stage $zipName)
        Push-Location $stage
        try {
            # Explicit registry, no lifecycle scripts, no user/global npmrc mutation.
            $output = & npm pack --json --ignore-scripts --registry=https://registry.npmjs.org 2>$null
            if ($LASTEXITCODE -ne 0) { throw 'npm pack failed.' }
            $packed = @($output -join "`n" | ConvertFrom-Json)
            if ($packed.Count -ne 1 -or (Split-Path $packed[0].filename -Leaf) -cne $packed[0].filename) { throw 'Unexpected npm pack output.' }
            $tgz = Join-Path $stage $packed[0].filename
            Test-ReleaseTarball $tgz $receipt.version $PackageName $receipt.sha256
            if ($NoPublish) { Write-Host 'Offline npm pack and embedded ZIP SHA256 passed.'; return }
            $existing = Get-ReleaseNpmVersion $PackageName $receipt.version $Proxy
            $journal = Join-Path (Split-Path $ReceiptPath -Parent) 'npm-publication.json'
            $attempted = Test-Path -LiteralPath $journal
            if ($attempted) {
                $attempt = Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json
                if ($attempt.version -cne $receipt.version -or $attempt.sha256 -cne $receipt.sha256) { throw 'npm publication journal conflicts with acceptance.' }
            }
            if ($null -eq $existing -and -not $VerifyOnly -and -not $attempted) {
                $null = Assert-LocalReleaseReceipt $Root $ReceiptPath
                # Persist BEFORE the network mutation: EOF/processing must not cause a second publish.
                Write-ReleaseJson $journal @{ version = $receipt.version; sha256 = $receipt.sha256; attemptedAt = [DateTime]::UtcNow.ToString('o') }
                $arguments = @('publish', $tgz, '--access', 'public', '--ignore-scripts', '--registry=https://registry.npmjs.org')
                if ($Otp) { $arguments += "--otp=$Otp" }
                # Capture output because npm errors may include authentication details.
                $null = & npm @arguments 2>&1
                if ($LASTEXITCODE -ne 0) { Write-Host 'npm did not confirm publication; checking registry before any further action.' }
            }
            Wait-ReleaseNpmVersion $receipt $PackageName $stage $Proxy $MirrorTimeoutMinutes
            Write-Host 'Immutable npm version downloaded; embedded ZIP SHA256 matches local acceptance.'
        } finally { Pop-Location }
        New-Item -ItemType Directory -Path $proofDir -Force | Out-Null
        Wait-ReleaseMirror $receipt $PackageName $proofDir $Proxy $MirrorTimeoutMinutes
    } finally { Remove-ReleaseScratch $stage $WorkRoot }
}

if ($FunctionsOnly) { return }
$ErrorActionPreference = 'Stop'
if (-not $ReceiptPath) { throw 'Use -ReceiptPath from release.ps1 local acceptance.' }
if ($NoPublish -and $VerifyOnly) { throw 'Choose -NoPublish (offline pack) or -VerifyOnly (online verification).' }
$WorkRoot = Get-LocalReleaseWorkRoot $PSScriptRoot $WorkRoot
Invoke-LocalReleaseEnvironment $WorkRoot $Proxy '' {
    Invoke-LocalNpmPublish $PSScriptRoot $ReceiptPath $WorkRoot $PackageName $Proxy $Otp $NoPublish.IsPresent $VerifyOnly.IsPresent $MirrorTimeoutMinutes
}
