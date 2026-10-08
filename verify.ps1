<#
  verify.ps1 —— 启动器产物自检

  回答一个问题:把这个 dist 目录丢到一台陌生电脑上,能不能跑起来?
  它不启动 GUI,只静态检查该有的东西在不在、有没有写死开发机路径。

  用法:
    .\verify.ps1                                   # 检查 source\dist-<版本>
    .\verify.ps1 -Dist 'E:\somewhere\DeepSeek Harness'
    .\verify.ps1 -DshRoot 'D:\DSH'                 # 顺便验证目标机的 DSH 目录结构
#>
[CmdletBinding()]
param(
    [string]$Dist,
    [string]$DshRoot,
    [string]$Version
)

$ErrorActionPreference = 'Continue'
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8

$Root = $PSScriptRoot
if (-not $Version) {
    $projectText = Get-Content (Join-Path $Root 'source\DeepSeekHarness.csproj') -Raw -Encoding UTF8
    $Version = ([regex]::Match($projectText, '<Version>([^<]+)</Version>')).Groups[1].Value.Trim()
}
if (-not $Dist) { $Dist = Join-Path $Root "source\dist-$Version" }

$script:Failures = 0
$script:Warnings = 0

function Pass ([string]$m) { Write-Host "  [OK]   $m" -ForegroundColor Green }
function Fail ([string]$m) { Write-Host "  [FAIL] $m" -ForegroundColor Red; $script:Failures++ }
function Warn ([string]$m) { Write-Host "  [WARN] $m" -ForegroundColor Yellow; $script:Warnings++ }
function Head ([string]$m) { Write-Host ''; Write-Host $m -ForegroundColor Cyan }

Write-Host ''
Write-Host '============================================'
Write-Host "  大肥鱼Go启动器 产物自检   v$Version"
Write-Host "  目录: $Dist"
Write-Host '============================================'

Head '[1] 关键文件'
foreach ($name in 'DeepSeek Harness.exe', 'DeepSeek Harness.Core.exe', 'DeepSeek Harness.Core.dll', 'DeepSeek Harness.Core.pri', 'DeepSeek Harness.Core.runtimeconfig.json', 'info-host\DafeiyuGo.Info.exe', 'info-host\DafeiyuGo.Info.dll', 'info-host\DafeiyuGo.Info.deps.json', 'info-host\DafeiyuGo.Info.runtimeconfig.json', 'info-host\DafeiyuGo.Info.pri', 'info-host\App.xbf', 'info-host\sounds\notify_F4_F5_v2.mp3') {
    $path = Join-Path $Dist $name
    if (Test-Path $path) {
        $item = Get-Item $path
        Pass ("{0}  {1}  {2:N0} KB" -f $name, $item.VersionInfo.FileVersion, ($item.Length / 1KB))
        if ($item.VersionInfo.FileVersion -and $item.VersionInfo.FileVersion -notlike "$Version*") {
            Fail ("版本号对不上: 文件是 $($item.VersionInfo.FileVersion),期望 $Version")
        }
    } else {
        Fail "缺少 $name"
    }
}

Head '[2] 运行库依赖(目标机必须已装)'
$winAppRuntime = $false
try {
    $key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications'
    if (Test-Path $key) {
        $winAppRuntime = [bool]((Get-Item $key).GetSubKeyNames() | Where-Object { $_ -like 'MicrosoftCorporationII.WinAppRuntime.Main.1.8_*' })
    }
} catch { }
if ($winAppRuntime) { Pass 'Windows App Runtime 1.8(本机已装,目标机需要)' } else { Warn '本机没装 Windows App Runtime 1.8 —— 目标机必须有,否则启动器起不来' }

$dotnet8 = $false
$sharedDir = Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.WindowsDesktop.App'
if (Test-Path $sharedDir) {
    $dotnet8 = [bool](Get-ChildItem $sharedDir -Directory | Where-Object { $_.Name -like '8.*' })
}
if ($dotnet8) { Pass '.NET 8 桌面运行时(本机已装,目标机需要)' } else { Warn '本机没装 .NET 8 桌面运行时 —— 目标机必须有' }

$bootstrapDll = Join-Path $Dist 'Microsoft.WindowsAppRuntime.Bootstrap.dll'
if (Test-Path $bootstrapDll) { Pass 'Bootstrap.dll 在(负责拉起 Windows App Runtime)' } else { Fail '缺少 Microsoft.WindowsAppRuntime.Bootstrap.dll' }

Head '[3] 可移植性:源码里不该再写死开发机路径'
$sources = Get-ChildItem (Join-Path $Root 'source') -Filter '*.cs' -File | Where-Object { $_.Name -ne 'Program.cs' }
$hardcoded = @()
foreach ($file in $sources) {
    $hits = Select-String -Path $file.FullName -Pattern 'G:\\DeepSeek|c:\\users\\|C:\\Users\\|E:\\Nodejs' -ErrorAction SilentlyContinue
    foreach ($hit in $hits) {
        # 注释里提到历史路径不算问题
        if ($hit.Line -match '历史上|曾经|旧路径') { continue }
        # Program.cs 是旧版 WinForms 尸体,不参与编译;Constants 里的默认值允许留作兜底但要确认有替代路径
        if ($file.Name -eq 'WinUIProgram.cs' -and $hit.Line -match 'DefaultRoot|DefaultNode') {
            $hardcoded += ("{0}:{1}  {2}" -f $file.Name, $hit.LineNumber, $hit.Line.Trim())
        } elseif ($hit.Line -notmatch '只当|兜底|fallback') {
            $hardcoded += ("{0}:{1}  {2}" -f $file.Name, $hit.LineNumber, $hit.Line.Trim())
        }
    }
}
if ($hardcoded.Count -eq 0) {
    Pass 'core 源码里没有写死的机器路径'
} else {
    Warn ("还有 $($hardcoded.Count) 处写死路径(确认只作兜底):")
    $hardcoded | ForEach-Object { Write-Host "         $_" }
}

$locatorPath = Join-Path $Root 'source\LauncherLocator.cs'
if (Test-Path $locatorPath) { Pass 'LauncherLocator.cs 在(负责动态找 DSH 和 node)' } else { Fail '缺少 LauncherLocator.cs' }

$logPathOk = Select-String -Path (Join-Path $Root 'source\WinUIProgram.cs') -Pattern 'LocalApplicationData' -ErrorAction SilentlyContinue
if ($logPathOk) { Pass '日志路径走 %LOCALAPPDATA%,没写死' } else { Warn '日志路径可能还是写死的' }

Head '[4] 打包内容'
$stale = Get-ChildItem $Dist -Force | Where-Object { $_.Name -match '\.old' }
if ($stale) {
    Fail ("目录里有 $($stale.Count) 个 .old 残留,打包前必须清掉")
    $stale | ForEach-Object { Write-Host "         $($_.Name)" }
} else {
    Pass '没有 .old 残留'
}

$dllCount = (Get-ChildItem $Dist -Filter '*.dll' | Measure-Object).Count
$sizeMb = [math]::Round(((Get-ChildItem $Dist -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
Pass "dll $dllCount 个,总大小 $sizeMb MB"

Head '[5] DSH 目标目录'
$targetRoot = $DshRoot
if (-not $targetRoot) {
    $marker = Join-Path (Split-Path $Dist -Parent) 'node_modules\@deepseek-ai\dsh\lib\bin.js'
    if (Test-Path $marker) { $targetRoot = Split-Path $Dist -Parent }
}
if ($targetRoot) {
    $marker = Join-Path $targetRoot 'node_modules\@deepseek-ai\dsh\lib\bin.js'
    if (Test-Path $marker) { Pass "DSH 本体在: $marker" } else { Fail "这个目录里没有 DSH 本体: $targetRoot" }

    $nodeBundled = Join-Path $targetRoot 'node_modules\node\bin\node.exe'
    if (Test-Path $nodeBundled) { Pass "DSH 自带 node 在: $nodeBundled" } else { Warn 'DSH 目录里没有 node_modules\node,启动器会去 PATH 里找 node' }

    $lastUrl = Join-Path $targetRoot 'logs\last-url.txt'
    if (Test-Path $lastUrl) { Pass 'logs\last-url.txt 在(启动器会读它恢复上次地址)' } else { Warn 'logs\last-url.txt 不在(第一次启动会正常生成)' }
} else {
    Warn '没指定 -DshRoot,也没能从 dist 的上级目录推断出 DSH 目录,跳过'
}

Head '结论'
if ($script:Failures -eq 0) {
    Write-Host "  通过($script:Warnings 条提醒)" -ForegroundColor Green
} else {
    Write-Host "  失败 $script:Failures 项,提醒 $script:Warnings 条" -ForegroundColor Red
}
Write-Host ''
Write-Host '提醒:静态检查过不代表真能跑。目标机上还要实测:'
Write-Host '  1) 双击 DeepSeek Harness.exe → 弹一次 UAC'
Write-Host '  2) 托盘出现鲸鱼图标,右键菜单能开'
Write-Host '  3) 菜单点"打开页面" → 浏览器打开 127.0.0.1:8787'
Write-Host '  4) 菜单点"开机自启动" → 看打过勾'
Write-Host '  5) 菜单点"退出" → 进程干净退出,不留 node 孤儿'
Write-Host ''
exit ($script:Failures -gt 0 ? 1 : 0)
