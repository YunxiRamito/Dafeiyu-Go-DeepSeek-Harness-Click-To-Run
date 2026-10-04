# 启动器发布与接入指南

> 这份文档讲两件事:**这个仓库怎么发版**,以及 **DSH Installer 怎么拿到它**。
> 最后更新:2026-10-04

## 当前发布规则与必做清单

本节优先于下方历史命令示例。两个仓库同版本发布，正式出包走各自 tag CI；父级 `release-all.ps1` 只用 `-BuildOnly` 做本地验证，不用旧本地直发流程。当前签名策略见两仓库 `SIGNING.md`；未配置策略时官方包未签名，配置后不得绕过验证。

**每次发布必须更新 GitHub 更新日志，不能只上传包。** CI 自动生成的提交列表、包名和哈希不等于产品更新日志。

1. 推送 tag 前更新启动器 `CHANGELOG.md` 和安装器 `docs/CHANGELOG.md`，同时描述两边改动、已知限制与验证范围，提交 README 和版本信息。
2. 先发布安装器 tag 并确认 CI 成功，再发布启动器同版本 tag。只推本次新 tag，不强推历史 tag。
3. 确认 npm 与 npmmirror 实际可下载，解出包内 ZIP 并核对 SHA-256 与 GitHub Release 一致，然后更新 `manifest.json`；不得填本地验证包的哈希。
4. **分别编辑两个 GitHub Release 的标题和正文**：写入同一批产品更新说明，保留各自下载文件名、实际 SHA-256、签名状态及已验证的镜像状态。npm 失败时不能宣称镜像已同步。
5. **通过 GitHub 页面或 API 读回两个 Release**，确认版本、标题、更新日志、文件名、哈希、限制说明均正确。只改仓库 CHANGELOG 不算完成 GitHub 更新日志。
6. 将发布结果、CI 链接、镜像核验和未测事项回填两边 HANDOVER / STATUS，并提交推送文档。全部完成后才能宣布发布完成。

发布验收必须同时满足：两个 Release 更新日志已核验、资产可下载、镜像包一致、公开更新清单正确。日志缺失时继续补齐，不把“稍后更新”留给下次。


---

## 零、仓库现状

| 项 | 值 |
|----|-----|
| 启动器仓库 | `https://github.com/YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Click-To-Run` |
| 默认分支 | `main` |
| 本地已初始化 git,remote `origin` 已配好 | `G:\DeepSeek DSH\DSH Works\Project\Dafeiyu-Go\Dafeiyu-Go-DeepSeek-Harness-Click-To-Run` |
| 当前产物 | `DeepSeekHarness-1.4.9.zip`（约 11 MB） |
| 安装器读取的清单 | 仓库根目录 `manifest.json` |

**当前版本构建：**

```powershell
cd 'G:\DeepSeek DSH\DSH Works\Project\Dafeiyu-Go\Dafeiyu-Go-DeepSeek-Harness-Click-To-Run'
git add -A
git commit -m "release: 大肥鱼Go启动器 1.4.9"
git tag v1.4.9
git push origin main --tags
# 然后把 DeepSeekHarness-1.4.9.zip 传到 v1.4.9 的 Release 资产里
```

正常发布统一使用父级 `release-all.ps1`，它会按正确顺序处理两个仓库。

---

## 一、仓库分工

```
deepseek-harness-launcher          本仓库 = DeepSeek Starter(启动器)
  ├─ source\                        WinUI3 源码
  ├─ manifest.json                  ← 安装器读的发布清单(必须放仓库根目录)
  ├─ release.ps1                    一键发版脚本
  ├─ .github\workflows\release.yml  打 tag 自动出包
  └─ Releases                       DeepSeekHarness-<版本>.zip

dsh-installer                      另一个仓库 = 安装程序
  └─ 装着的时候:读 launcher 仓库的 manifest.json → 下 zip → 解开
```

**为什么要分开**

启动器一两周就可能改一次(托盘菜单、图标、动画),安装器可能几个月才动一次。
拆开之后:启动器发新版,**安装器一个字都不用重发**,装机的人照样拿到最新启动器。

---

## 二、安装器怎么取包

```
安装器启动
  └─ LauncherFeed.Fetch("owner/deepseek-harness-launcher", 源偏好)
       ├─ 国内源优先:https://cdn.jsdelivr.net/gh/<repo>@main/manifest.json
       └─ 官方兜底:  https://raw.githubusercontent.com/<repo>/main/manifest.json
            ↓ 拿到清单
       ├─ version  → 跟本机已装的比,一样就跳过
       ├─ assets.github → 官方 zip 地址
       │    └─ 国内源时自动加前缀:https://ghproxy.net/<原地址>
       ├─ assets.mirrors[] → 额外的备用地址(自建镜像、网盘直链都行)
       └─ sha256 → 下完校验,对不上就重下
```

拉不到清单怎么办:**回落到安装器自带的 `payload\launcher.zip`**(离线兜底)。
所以安装器构建时仍然建议打一份 payload,但那份可以是很久以前的版本。

---

## 三、发版流程

### 省事版:一条命令

```powershell
cd 'G:\DeepSeek DSH\DSH Works\Project\Dafeiyu-Go\Dafeiyu-Go-DeepSeek-Harness-Click-To-Run'
.\release.ps1
```

它会:读版本号 → 编译主程序 + 引导程序 → 检查产物 → 清 `.old` 残留 →
打包 zip → 算 SHA256 → 写好 `manifest.json` → 打印后面的 git 命令。

然后:

```powershell
git add manifest.json
git commit -m "release: v1.3.9"
git tag v1.3.9
git push origin main --tags
```

最后把 `DeepSeekHarness-1.3.9.zip` 传到 `v1.3.9` Release 的资产里(网页点一下,
或 `gh release upload v1.3.9 .\DeepSeekHarness-1.3.9.zip`)。

### CI 版:打 tag 自动出包

`.github\workflows\release.yml` 会在推 tag 时自动编译 + 打包 + 建 Release,
并且**校验 tag 和 csproj 版本一致**,不一致直接失败,防止发错版本。

CI 出包后,`manifest.json` 里的 sha256 仍需本地跑一次 `release.ps1` 更新再提交
(runner 上的哈希没法自动推回来)。

### 手动版:一步步来

#### 1. 改版本号(四个地方必须一起改)

| 文件 | 字段 |
|------|------|
| `source\DeepSeekHarness.csproj` | `<Version>` `<AssemblyVersion>` `<FileVersion>` |
| `source\RuntimeBootstrap.cs` | `AssemblyVersion` `AssemblyFileVersion` `AssemblyInformationalVersion` |
| `source\WinUIProgram.cs` | `Constants.Version` |
| `source\app.manifest` / `source\RuntimeBootstrap.manifest` | `assemblyIdentity version` |

### 2. 编译

```powershell
cd 'G:\DeepSeek DSH\DSH Works\Project\Dafeiyu-Go\Dafeiyu-Go-DeepSeek-Harness-Click-To-Run\source'
$env:NUGET_PACKAGES = 'G:\DeepSeek DSH\.nuget-packages'
.\build-winui.ps1 -OutputDirectory (Join-Path $PWD 'dist-1.3.9')

# 外层引导程序没有进 build-winui.ps1 的流程,单独编一次
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:winexe /platform:x64 /optimize+ `
  "/win32icon:$PWD\DeepSeekHarness.ico" "/win32manifest:$PWD\RuntimeBootstrap.manifest" `
  "/out:$PWD\dist-1.3.9\DeepSeek Harness.exe" "$PWD\RuntimeBootstrap.cs"
```

产物里必须包含:

```
DeepSeek Harness.exe        ← 外层引导,清单声明 requireAdministrator
DeepSeek Harness.Core.exe   ← WinUI3 主程序
*.dll / *.pri / *.json      ← 依赖
```

### 3. 打包 zip

zip **根目录直接就是文件**,不要多套一层文件夹:

```
DeepSeekHarness-1.3.9.zip
├─ DeepSeek Harness.exe
├─ DeepSeek Harness.Core.exe
└─ ...
```

```powershell
Compress-Archive -Path '.\dist-1.3.9\*' -DestinationPath '.\DeepSeekHarness-1.3.9.zip'
```

### 4. 算 SHA256

```powershell
(Get-FileHash '.\DeepSeekHarness-1.3.9.zip' -Algorithm SHA256).Hash.ToLower()
```

### 5. 发 Release

- tag:`v1.3.9`
- 资产:`DeepSeekHarness-1.3.9.zip`
- 说明:写这一版改了什么

### 6. 更新仓库根目录的 `manifest.json`

```json
{
  "version": "1.3.9",
  "sha256": "把上一步的哈希粘这里",
  "subDirectory": "",
  "assets": {
    "github": "https://github.com/<owner>/deepseek-harness-launcher/releases/download/v1.3.9/DeepSeekHarness-1.3.9.zip",
    "mirrors": []
  },
  "notes": "开机自启动菜单项 + 浅色模式高亮修复"
}
```

提交到 `main` 分支后,**所有新装用户立刻拿到这一版**。

> jsDelivr 有缓存(约 12 小时)。想立刻刷新,用
> `https://cdn.jsdelivr.net/gh/<owner>/<repo>@<commit-sha>/manifest.json`
> 或者临时只走 raw.githubusercontent。安装器两边都会试,不会卡死。

---

## 四、manifest.json 字段说明

| 字段 | 必需 | 说明 |
|------|------|------|
| `version` | 是 | 语义化版本,安装器用它跟本机比对 |
| `assets.github` | 是* | Release 资产地址。安装器会自动配镜像前缀 |
| `assets.mirrors` | 否 | 额外备用地址,顺序越前越优先(国内源模式下) |
| `sha256` | 推荐 | zip 校验和;留空则跳过校验 |
| `subDirectory` | 否 | zip 里启动器在子目录时填,正常留空 |
| `notes` | 否 | 一句话更新说明,显示在安装器的版本行上 |

\* `assets.github` 和 `assets.mirrors` 至少要有一个。也兼容最简格式:`{ "version": "...", "url": "..." }`。

---

## 五、国内下载加速

安装器会自己处理,不用你额外做什么:

| 场景 | 用的地址 |
|------|----------|
| 拉 manifest(国内源) | `https://cdn.jsdelivr.net/gh/<repo>@main/manifest.json` |
| 拉 manifest(官方源) | `https://raw.githubusercontent.com/<repo>/main/manifest.json` |
| 下 zip(国内源) | `https://ghproxy.net/<github原始地址>` → `https://ghfast.top/<原始地址>` → 原始地址 |
| 下 zip(官方源) | 原始地址 → ghproxy 兜底 |

想加自建镜像,写进 `assets.mirrors` 就行,安装器会挨个试。

---

## 六、常见问题

**Q:装完发现启动器版本不对?**
看安装日志 `%LOCALAPPDATA%\DeepSeekHarness\installer.log`,里面记了实际用的清单地址和下载 URL。

**Q:manifest 更新了但用户还拿到旧版?**
jsDelivr 缓存。等 12 小时,或者让安装器走官方源,或者用 commit sha 固定路径。

**Q:zip 里的目录多了一层,启动器起不来?**
`Compress-Archive -Path '.\dist-1.3.9\*'` 的星号别丢。多套一层就把 `subDirectory` 填上。

**Q:为什么 zip 里没有 `.old` 那种残留文件?**
本机换文件时留下的,发布前清一下 `dist-*` 目录再打包。
