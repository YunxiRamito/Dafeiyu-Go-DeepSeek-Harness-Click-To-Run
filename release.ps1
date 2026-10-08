<#
  release.ps1 —— 启动器一键发版

  干的事:
    1. 从源码里读出当前版本号
    2. 编译 WinUI3 主程序 + 外层引导
    3. 打包成 DeepSeekHarness-<版本>.zip
    4. 算 SHA256,生成/更新仓库根目录的 manifest.json
    5. 打印后续要执行的 git 命令

  用法:
    .\release.ps1                     # 构建 + 打包 + 生成 manifest
    .\release.ps1 -NoBuild            # 用现成的 dist-<版本> 重新打包
    .\release.ps1 -Push               # 顺手把 tag 推到远端的命令也打出来
#>
[CmdletBinding()]
param(
    [string]$Repository = 'YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Click-To-Run',
    [string]$OutputRoot,
    [switch]$NoBuild,
    [switch]$NoManifest,
    [switch]$KeepDist
)

$ErrorActionPreference = 'Stop'
if (-not $NoManifest) {
    throw 'Local builds cannot update the public manifest. Use -NoManifest; publish a new tag through CI.'
}
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8

$SourceRoot = Join-Path $PSScriptRoot 'source'
$ProjectFile = Join-Path $SourceRoot 'DeepSeekHarness.csproj'
$BootstrapSource = Join-Path $SourceRoot 'RuntimeBootstrap.cs'
$BootstrapManifest = Join-Path $SourceRoot 'RuntimeBootstrap.manifest'
$IconPath = Join-Path $SourceRoot 'DeepSeekHarness.ico'
$BuildScript = Join-Path $SourceRoot 'build-winui.ps1'
$ManifestPath = Join-Path $PSScriptRoot 'manifest.json'
# 优先用本机那套便携 SDK;没有就退回 PATH 里的 dotnet(CI 上就是这种)
$Dotnet = 'G:\DeepSeek DSH\.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $Dotnet)) { $Dotnet = 'dotnet' }

# 本机有离线 NuGet 缓存就用它;没有就不设(CI 上不存在这个目录)
$NuGetCache = 'G:\DeepSeek DSH\.nuget-packages'
$Csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

function Say  ([string]$m) { Write-Host $m }
function Ok   ([string]$m) { Write-Host "  [OK]  $m" -ForegroundColor Green }
function Warn ([string]$m) { Write-Host "  [!!]  $m" -ForegroundColor Yellow }
function Step ([string]$m) { Write-Host "  [..]  $m" -ForegroundColor Cyan }

# 从 CHANGELOG.md 里抠出这一版的说明,给 GitHub Release 当正文。
# 没写就打警告,并退回一句最简说明——不拦着发版,但会提醒你补。
function Get-ReleaseNotes([string]$targetVersion) {
    $changeLog = Join-Path $PSScriptRoot 'CHANGELOG.md'
    if (-not (Test-Path $changeLog)) {
        Warn "没有 CHANGELOG.md,这版将没有更新日志"
        return "大肥鱼Go启动器 v$targetVersion"
    }

    $lines = Get-Content $changeLog -Encoding UTF8
    $collecting = $false
    $buffer = New-Object System.Collections.ArrayList
    foreach ($line in $lines) {
        if ($line -match '^##\s+(.+?)\s*$') {
            if ($collecting) { break }
            $heading = $Matches[1].Trim()
            if ($heading -eq $targetVersion -or $heading -eq "v$targetVersion") {
                $collecting = $true
                [void]$buffer.Add("## $targetVersion")
                continue
            }
        }
        if ($collecting) { [void]$buffer.Add($line) }
    }

    $text = ($buffer -join "`n").Trim()
    if ([string]::IsNullOrWhiteSpace($text)) {
        Warn "CHANGELOG.md 里没有 $targetVersion 这一节,建议补上再发"
        return "大肥鱼Go启动器 v$targetVersion"
    }

    Ok "更新日志: 取到 $targetVersion 一节($($text.Length) 字符)"
    return $text
}

# 把 "1.6.0.0" / "1.6" 这类版本串统一成 [version]。
# 只取开头的数字主体,带前缀后缀的写法(1.6.0-beta)也能认。
# 解析不了就返回 $null,调用方按“无法比较”处理,不要在这里抛错。
function ConvertTo-GateVersion([string]$text) {
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    $head = ([regex]::Match($text.Trim(), '^\d+(\.\d+)*')).Value
    if (-not $head) { return $null }
    $parts = @($head.Split('.') | ForEach-Object { [int]$_ })
    while ($parts.Count -lt 4) { $parts += 0 }
    if ($parts.Count -gt 4) { $parts = $parts[0..3] }
    try { return [version]::new($parts[0], $parts[1], $parts[2], $parts[3]) } catch { return $null }
}

# 读 manifest.json 里当前已发布的版本号,读不到返回空串。
function Get-ManifestPublishedVersion {
    $path = Join-Path $PSScriptRoot 'manifest.json'
    if (-not (Test-Path -LiteralPath $path)) { return '' }
    try {
        $m = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
        return [string]$m.version
    } catch {
        Warn "manifest.json 解析失败,跳过补丁合入检查: $($_.Exception.Message)"
        return ''
    }
}

# 读补丁清单的 patches 数组;没有 patches.json 就返回空数组。
function Get-PatchFeedEntries([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    try {
        $feed = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    } catch {
        Warn "补丁清单解析失败,按无补丁处理: $path — $($_.Exception.Message)"
        return @()
    }
    if (-not $feed.patches) { return @() }
    return @($feed.patches)
}

# 发版门禁(SPEC 4):
#   X 或 Y 变动 = 大版本,patches.json / patches-preview.json 里只要还有 mergedIn 为空
#   或 mergedIn 晚于新版本的补丁,就 throw 中止;只变 Z 时仅提示一句,不阻断。
#   没有补丁清单、或清单里没有补丁时安全跳过。
function Assert-PatchMergeGate([string]$targetVersion) {
    $published = Get-ManifestPublishedVersion
    if (-not $published) {
        Warn "manifest.json 里没有当前发布版本,跳过补丁合入检查"
        return
    }

    $currentVersion = ConvertTo-GateVersion $published
    $newVersion = ConvertTo-GateVersion $targetVersion
    $isMajor = $false
    if ($currentVersion -and $newVersion) {
        $isMajor = ($currentVersion.Major -ne $newVersion.Major) -or ($currentVersion.Minor -ne $newVersion.Minor)
    } else {
        Warn "版本号无法解析(当前 $published → 新 $targetVersion),按大版本处理,继续检查补丁"
        $isMajor = $true
    }

    if (-not $isMajor) {
        Warn "当前发布 $published → 新版本 $targetVersion 只变修订号(X 与 Y 不变),不做补丁合入门禁"
        return
    }

    Step "大版本发版($published → $targetVersion):检查补丁合入状态"
    $pending = @()
    foreach ($file in @('patches.json', 'patches-preview.json')) {
        $path = Join-Path $PSScriptRoot $file
        foreach ($patch in (Get-PatchFeedEntries $path)) {
            $id = [string]$patch.id
            $mergedIn = [string]$patch.mergedIn
            $mergedVersion = ConvertTo-GateVersion $mergedIn
            $mergedText = '(空)'
            $isMergedEmpty = $true
            if ($mergedIn -and $mergedIn.Trim()) {
                $mergedText = $mergedIn
                $isMergedEmpty = $false
            }
            $unmerged = $isMergedEmpty
            if (-not $isMergedEmpty -and $mergedVersion -and $newVersion) {
                $unmerged = ($mergedVersion -gt $newVersion)
            }
            if ($unmerged) {
                $title = [string]$patch.title
                if (-not $id) { $id = '(无 id)' }
                if (-not $title) { $title = '(无标题)' }
                $pending += [pscustomobject]@{
                    Id       = $id
                    Title    = $title
                    MergedIn = $mergedText
                    File     = $file
                }
            }
        }
    }

    if ($pending.Count -gt 0) {
        Say ''
        Say '  未合入补丁:'
        foreach ($p in $pending) {
            Say ("    - {0}  {1}  (mergedIn = {2}, 来自 {3})" -f $p.Id, $p.Title, $p.MergedIn, $p.File)
        }
        Say ''
        throw ("大版本发版中止: $published → $targetVersion 属于 X 或 Y 变动,以下补丁还没合入新版本。" +
               "把它们的 mergedIn 写成 $targetVersion(或更早已实际合入的版本)、或从清单里移除,再发版。")
    }

    Ok "补丁合入检查通过:两个补丁清单里没有待合入的补丁"
}

# ---------------------------------------------------------------- 版本号
Say ''
Say '========================================'
Say '  大肥鱼Go启动器 发版'
Say '========================================'

Say ''
Say '[1/5] 读版本号'
$projectText = Get-Content -Path $ProjectFile -Raw -Encoding UTF8
$versionMatch = [regex]::Match($projectText, '<Version>([^<]+)</Version>')
if (-not $versionMatch.Success) { throw "从 csproj 里读不到 <Version>" }
$version = $versionMatch.Groups[1].Value.Trim()
Ok "版本: $version"

# 发版门禁:大版本变动前必须把未合入补丁处理掉(SPEC 4)
& (Join-Path $PSScriptRoot 'check-release-gate.ps1') -Version $version

$distDir = if ($OutputRoot) { $OutputRoot } else { Join-Path $SourceRoot ("dist-" + $version) }
$zipName = "DeepSeekHarness-$version.zip"
$zipPath = Join-Path $PSScriptRoot $zipName
Say "    输出目录: $distDir"

# ---------------------------------------------------------------- 编译
if (-not $NoBuild) {
    Say ''
    Say '[2/5] 编译'
    if (-not (Test-Path $Dotnet)) { throw "找不到 .NET SDK: $Dotnet" }

    # 只在缓存目录真的存在时才指过去(CI 上没有这个目录)
    if (Test-Path -LiteralPath $NuGetCache) { $env:NUGET_PACKAGES = $NuGetCache }

    Step 'dotnet publish (WinUI3 主程序)'
    & $Dotnet publish $ProjectFile `
        --configuration Release `
        --runtime win-x64 `
        --self-contained false `
        --output $distDir `
        -p:AssemblyName='DeepSeek Harness.Core' `
        -p:PublishSingleFile=false `
        -p:IncludeNativeLibrariesForSelfExtract=false `
        -p:WindowsAppSDKSelfContained=false `
        -p:DebugType=None -p:DebugSymbols=false -v minimal
    if ($LASTEXITCODE -ne 0) { throw "主程序编译失败: $LASTEXITCODE" }
    Ok '主程序完成'

    Step 'csc (外层引导程序)'
    if (-not (Test-Path $Csc)) { throw "找不到 csc.exe: $Csc" }
    & $Csc /nologo /target:winexe /platform:x64 /optimize+ /utf8output `
        "/win32icon:$IconPath" "/win32manifest:$BootstrapManifest" `
        "/out:$(Join-Path $distDir 'DeepSeek Harness.exe')" $BootstrapSource
    if ($LASTEXITCODE -ne 0) { throw "引导程序编译失败: $LASTEXITCODE" }
    Ok '引导程序完成'
} else {
    Say ''
    Say '[2/5] 跳过编译(用现成产物)'
}

# ---------------------------------------------------------------- 体检产物
Say ''
Say '[3/5] 检查产物'
$required = @('DeepSeek Harness.exe', 'DeepSeek Harness.Core.exe')
foreach ($name in $required) {
    $path = Join-Path $distDir $name
    if (-not (Test-Path $path)) { throw "缺少关键文件: $path" }
    $item = Get-Item $path
    Ok ("{0}  {1}  {2}" -f $name, $item.VersionInfo.FileVersion, [math]::Round($item.Length / 1KB, 0).ToString() + ' KB')
}

# 清掉本机换文件留下的 .old 残留,别打进包里
Get-ChildItem $distDir -Filter '*.old*' -ErrorAction SilentlyContinue | ForEach-Object {
    Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
    Warn ("清理残留: " + $_.Name)
}

$fileCount = (Get-ChildItem $distDir -Recurse -File | Measure-Object).Count
Ok "产物文件数: $fileCount"

# ---------------------------------------------------------------- 打包
Say ''
Say '[4/5] 打包'
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $distDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
$zipItem = Get-Item $zipPath
Ok ("{0}  {1:N1} MB" -f $zipName, ($zipItem.Length / 1MB))

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
Ok "SHA256: $hash"

# ---------------------------------------------------------------- manifest
if (-not $NoManifest) {
    Say ''
    Say '[5/5] 生成 manifest.json'
    $assetUrl = "https://github.com/$Repository/releases/download/v$version/$zipName"

    $manifest = [ordered]@{
        version      = $version
        sha256       = $hash
        subDirectory = ''
        assets       = [ordered]@{
            github  = $assetUrl
            mirrors = @()
        }
        notes        = Get-ReleaseNotes $version
        releasedAt   = (Get-Date).ToString('s')
    }

    $json = $manifest | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText($ManifestPath, $json, (New-Object System.Text.UTF8Encoding($false)))
    Ok "已写入: $ManifestPath"

    # 清单也在 jsDelivr 上给国内加速,而 jsDelivr 对固定路径有约 12 小时缓存。
    # 再写一份带版本号的路径,URL 里带上版本号就永远是新的,不受缓存影响。
    $versionedManifest = Join-Path $PSScriptRoot ("manifest-" + $version + ".json")
    [System.IO.File]::WriteAllText($versionedManifest, $json, (New-Object System.Text.UTF8Encoding($false)))
    Ok "带版本号副本: manifest-$version.json(给 jsDelivr 绕缓存用)"
} else {
    Say ''
    Say '[5/5] 跳过 manifest.json（构建验证模式）'
}

if (-not $NoManifest) {
    Say ''
    Say '----------------------------------------'
    Say '接下来手动执行(或者用 GitHub 网页发 release):'
    Say ''
    Say "  git add manifest.json"
    Say "  git commit -m `"release: v$version`""
    Say "  git tag v$version"
    Say "  git push origin main --tags"
    Say ''
    Say "  然后把 $zipName 作为资产传到 v$version 这个 release 下:"
    Say "  $assetUrl"
    Say '----------------------------------------'
    Say ''
    Say '提示:推完 release 之后,安装器会自动读到这一版,不用重发安装器。'
} else {
    Say ''
    Say "构建验证完成: $zipName"
    Say '未修改 manifest.json，也未生成发布链接。'
}
Say ''
