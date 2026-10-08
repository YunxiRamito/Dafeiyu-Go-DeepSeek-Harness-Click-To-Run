# 用 GitHub API 建 Release 并上传资产
#   .\publish-release.ps1                # 用 csproj 里的版本号
#   .\publish-release.ps1 -Version 1.3.17
#   .\publish-release.ps1 -Token 'ghp_...'   # 不给就读 .fix-lasso-state\github-token.txt
param(
    [string]$Version,
    [string]$Token,
    [string]$Repository = 'YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Click-To-Run',
    [switch]$SkipAsset,
    [switch]$Commit   # 顺手把代码/manifest 提交并推 tag(推荐带上)
)

$ErrorActionPreference = 'Stop'
throw 'This legacy publisher is disabled: it overwrites release assets and tags. Publish new tags through CI; never upload local verification ZIPs.'
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8

$Root = $PSScriptRoot
$TokenFile = 'G:\DeepSeek DSH\DSH Works\.fix-lasso-state\github-token.txt'

if (-not $Version) {
    $projectText = Get-Content (Join-Path $Root 'source\DeepSeekHarness.csproj') -Raw -Encoding UTF8
    $Version = ([regex]::Match($projectText, '<Version>([^<]+)</Version>')).Groups[1].Value.Trim()
}

if (-not $Token) {
    if (Test-Path $TokenFile) {
        $Token = (Get-Content $TokenFile -Raw).Trim()
    } else {
        throw "没有 token。给 -Token,或把它写进 $TokenFile"
    }
}

$ZipPath = Join-Path $Root "DeepSeekHarness-$Version.zip"
$Tag = "v$Version"
$Headers = @{
    Authorization          = "Bearer $Token"
    Accept                 = 'application/vnd.github+json'
    'User-Agent'           = 'Dafeiyu-Go-Release'
    'X-GitHub-Api-Version' = '2022-11-28'
}

function Say  ([string]$m) { Write-Host $m }
function Ok   ([string]$m) { Write-Host "  [OK]  $m" -ForegroundColor Green }
function Warn ([string]$m) { Write-Host "  [!!]  $m" -ForegroundColor Yellow }
function Step ([string]$m) { Write-Host "  [..]  $m" -ForegroundColor Cyan }

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
    $path = Join-Path $Root 'manifest.json'
    if (-not (Test-Path $path)) { return '' }
    try {
        $m = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
        return [string]$m.version
    } catch {
        Warn "manifest.json 解析失败,跳过补丁合入检查: $($_.Exception.Message)"
        return ''
    }
}

# 读补丁清单的 patches 数组;没有 patches.json 就返回空数组。
function Get-PatchFeedEntries([string]$path) {
    if (-not (Test-Path $path)) { return @() }
    try {
        $feed = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
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
        $path = Join-Path $Root $file
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

Say ''
Say '========================================'
Say "  发布启动器 $Tag 到 $Repository"
Say '========================================'

# 发版门禁:大版本变动前必须把未合入补丁处理掉(SPEC 4)
& (Join-Path $PSScriptRoot 'check-release-gate.ps1') -Version $Version

# 0) token 自检
Step '校验 token'
try {
    $me = Invoke-RestMethod -Uri 'https://api.github.com/user' -Headers $Headers -TimeoutSec 20
    Ok ("身份: " + $me.login)
} catch {
    throw "token 校验失败: $($_.Exception.Message)"
}

# 1) 资产在不在
if (-not $SkipAsset) {
    if (-not (Test-Path $ZipPath)) { throw "找不到资产: $ZipPath(先跑 release.ps1)" }
    $zipItem = Get-Item $ZipPath
    Ok ("资产: {0}  {1:N1} MB" -f $zipItem.Name, ($zipItem.Length / 1MB))
}

# 2) 从 manifest.json 取更新日志当 Release 正文
$ManifestPath = Join-Path $Root 'manifest.json'
$releaseBody = $null
if (Test-Path $ManifestPath) {
    try {
        $manifest = Get-Content $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($manifest.version -eq $Version -and $manifest.notes) {
            $releaseBody = [string]$manifest.notes
        }
    } catch { }
}
if ($releaseBody) {
    Ok "更新日志: 从 manifest.json 取到 $($releaseBody.Length) 字符"
} else {
    Warn 'manifest.json 里没有对应版本的更新日志,Release 正文只能放最简说明'
    $releaseBody = "大肥鱼Go启动器 v$Version"
}

# 3) release 在不在,不在就建
Say ''
Step "处理 Release $Tag"
$release = $null
try {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/releases/tags/$Tag" -Headers $Headers -TimeoutSec 20
    Ok ("已存在: " + $release.html_url)

    # 已存在也把正文刷新成最新日志,免得改过日志还要手工编辑
    if ($release.body -ne $releaseBody) {
        $patch = @{ body = $releaseBody } | ConvertTo-Json
        $release = Invoke-RestMethod -Method Patch `
            -Uri "https://api.github.com/repos/$Repository/releases/$($release.id)" `
            -Headers $Headers -Body $patch -ContentType 'application/json' -TimeoutSec 30
        Ok '正文已刷新为最新更新日志'
    }
} catch {
    $body = @{
        tag_name               = $Tag
        name                   = $Tag
        generate_release_notes = $false
        body                   = $releaseBody
    } | ConvertTo-Json
    $release = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$Repository/releases" `
        -Headers $Headers -Body $body -ContentType 'application/json' -TimeoutSec 30
    Ok ("已创建: " + $release.html_url)
}

# 3) 传资产
if (-not $SkipAsset) {
    Say ''
    Step '上传资产'
    $existing = $release.assets | Where-Object { $_.name -eq (Split-Path $ZipPath -Leaf) }
    if ($existing) {
        Warn "同名资产已存在,先删掉旧的"
        Invoke-RestMethod -Method Delete -Uri "https://api.github.com/repos/$Repository/releases/assets/$($existing.id)" -Headers $Headers -TimeoutSec 30 | Out-Null
    }

    $uploadUrl = "https://uploads.github.com/repos/$Repository/releases/$($release.id)/assets?name=$([uri]::EscapeDataString((Split-Path $ZipPath -Leaf)))"
    $uploadHeaders = $Headers.Clone()
    $uploadHeaders['Content-Type'] = 'application/zip'

    # 用 -InFile 流式上传：-Body [byte[]] 在部分 PowerShell 版本上会少传末尾字节，
    # 线上资产就和 manifest 的 sha256 对不上、客户端校验失败（v1.6.0 首次上传踩过）。
    $localSha = (Get-FileHash $ZipPath -Algorithm SHA256).Hash.ToLower()
    $asset = Invoke-RestMethod -Method Post -Uri $uploadUrl -Headers $uploadHeaders -InFile $ZipPath -TimeoutSec 900
    Ok ("资产已上传: " + $asset.browser_download_url)
    Ok ("大小: {0:N1} MB  状态: {1}" -f ($asset.size / 1MB), $asset.state)
    if ($asset.digest -and $asset.digest -ne "sha256:$localSha") {
        throw "线上资产摘要与本地不一致（线上 $($asset.digest) / 本地 sha256:$localSha），自更新会校验失败，请重传"
    }
    Ok ("摘要校验: sha256:$localSha")
}

# 4) 验证下载地址真的能下
Say ''
Step '验证下载地址'
$expected = "https://github.com/$Repository/releases/download/$Tag/DeepSeekHarness-$Version.zip"
try {
    $head = Invoke-WebRequest -Uri $expected -Method Head -MaximumRedirection 5 -TimeoutSec 30 -UseBasicParsing
    Ok "可访问: HTTP $($head.StatusCode)"
} catch {
    Warn "还访问不到(刚上传可能要等几秒): $($_.Exception.Message)"
}

# 5) 提交并推 tag(这一步以前漏了,导致仓库里的 manifest.json 落后于实际发布版本,
#    客户端因此查不到新版 —— 自更新直接失效)
if ($Commit) {
    Say ''
    Step '提交并推送'
    Push-Location $Root
    try {
        git fetch origin --prune
        if ($LASTEXITCODE -ne 0) { throw 'git fetch 失败' }

        git add -A
        if ($LASTEXITCODE -ne 0) { throw 'git add 失败' }

        git diff --cached --quiet
        $hasStagedChanges = $LASTEXITCODE -eq 1
        if ($LASTEXITCODE -gt 1) { throw '检查暂存区失败' }

        if ($hasStagedChanges) {
            $msg = "release: v$Version"
            git -c user.name="YunxiRamito" -c user.email="killmasterags@gmail.com" commit -m $msg
            if ($LASTEXITCODE -ne 0) { throw 'git commit 失败' }
        } else {
            Say '  没有新的文件改动，复用当前提交'
        }

        git rebase origin/main
        if ($LASTEXITCODE -ne 0) { throw '远端 main 与本地冲突，已停止发布；请解决冲突后重试' }

        git push origin main
        if ($LASTEXITCODE -ne 0) { throw 'git push main 失败' }

        git tag -d $Tag 2>&1 | Out-Null
        git tag $Tag
        if ($LASTEXITCODE -ne 0) { throw '创建 tag 失败' }

        git push --force origin $Tag
        if ($LASTEXITCODE -ne 0) { throw 'git push tag 失败' }
        Ok "已推送 main 与 $Tag"
    } catch {
        throw "git 操作失败: $($_.Exception.Message)"
    } finally {
        Pop-Location
    }
} else {
    Say ''
    Warn '提醒: 没有加 -Commit,代码与 manifest.json 还没提交。'
    Warn '      不提交的话客户端读到的清单是旧的,自更新会失效。'
}

Say ''
Say '完成。安装器的 LauncherFeed 和启动器的自更新都会读到这一版。'
Say ''
