# 启动器发布与接入指南

> 这份文档讲三件事:**这个仓库怎么发版**、**补丁怎么发**、**DSH Installer 怎么拿到它**。
> 最后更新:2026-10-08（1.7.0 收尾中，未发布）

## 当前发布规则与必做清单

**2026-10-08 收尾说明（优先于历史示例）**：正式清单保持已发布 1.6.0，启动器 1.7.0 未发布，安装器仍独立维护在 1.6.0。补丁事务、通道缓存、版本范围与资源消费者已修复并回归，binary/script 仍明确拒绝；插件事务与真实自更新替换等未重测边界继续按未证实处理。公网监控、后台两秒采样与 PostgreSQL 在线历史已部署，readiness/metrics/history 200、匿名管理 401 与 pin 读回通过；服务端 59 项、ClientNotice 117、DeveloperNotice 58、InfoWindowClient 62、InfoHost 12 状态、SettingsSearch 133、HTML 124 + 162 项通过。限定范围 GUI 通过项见 `final-validation.md`，实际听音、正常管理员服务启动、真实自更新和新机安装未测。

本地只允许 `release.ps1 -NoManifest`，不允许把本地产物哈希写到正式清单。最终目录 `source/dist-1.7.0-test-20261008-final` 已构建，verify 0 失败 / 2 提醒；本地 ZIP 66,116,192 字节，SHA-256 `BFCB020F6A7C7C5151C98DD274865F6C5904B1350EFB58D072118C4712CE9C45`，仅供用户本地测试，不写公开 manifest。限定 GUI：`fwqjk` 从 Home 跳服务器并读取 metrics/history，`gly` 跳 API 并显示 AdminTokenBox，InfoHost 12 状态通过；当前无管理员 token只保留只读预览，听音未测。证据见 `preview-artifacts/1.7.0-final/final-validation.md`。

本轮未提交、未推送、未创建 tag/release、未发 npm、未更新公开 manifest。先完成最终重新构建并提供本地测试地址，等待用户明确反馈本地测试无问题后，再执行下方正式发布清单。在线人数必须仅设置窗口打开时立即并每 5 秒 GET 小响应，关闭不 GET；服务器页性能 2 秒、在线历史 5 秒，离页/关窗取消请求。24 小时历史按真实分钟点保存、最多 1441 点，缺口不连线、不补造过去，初次历史不满 24 小时。部署证据见 [在线历史摘要](../preview-artifacts/1.7.0-final/presence-history-summary.md)。

网络上传百分比/进度固定以 30 Mbps 为分母；趋势图纵轴按近 5 分钟真实峰值加 15% 余量后取 1/2/5/10 整洁刻度，空/零最小 0.1 Mbps。最终图表验收必须区分这两个口径，避免把动态图轴上限当作带宽容量。

发布包必须在独立 `info-host/` 目录包含 `DafeiyuGo.Info.exe`、对应 DLL / deps / runtimeconfig、`DafeiyuGo.Info.pri`、`App.xbf` 及 `sounds/notify_F4_F5_v2.mp3`；它由主项目 Publish 目标统一构建，本地构建脚本执行资源门禁。主程序以当前用户受限随机命名管道发送显示状态，不由窗口进程执行更新或服务控制；自更新通过随机会话标识交接窗口。新增可执行文件必须纳入签名与验收，签名配置未覆盖它时不能宣称全包签名成功。

本节优先于下方历史命令示例。新增后端离线/恢复提示需验证一次离线段只提醒一次、恢复才提示、忙时排队与窗口关闭清理；修改完成后重新构建验收。

**发版方式**：启动器与安装器**各自独立发版**（1.6.0 起两边各自读自己的版本，不再要求同版本 tag）。正式产物由各自 tag CI 构建，不手动上传本地验证 ZIP；父级 `release-all.ps1` 只用 `-BuildOnly` 做本地验证。签名策略见 `SIGNING.md`；未配置策略时官方包未签名，配置后不得绕过验证。

**每次发布必须更新 GitHub 更新日志，不能只上传包。** CI 自动生成的提交列表、包名和哈希不等于产品更新日志。

1. 更新本仓库 `CHANGELOG.md` 的对应版本小节（写给用户看：不写类名、字段、测试套件名、提交号），提交版本信息与文档。
2. 本地验证：`.\release.ps1 -NoManifest` + `.\verify.ps1 -Dist .\source\dist-<版本> -Version <版本>`。
3. **同步 HTML 交互预览**（下一节，硬要求）：任何一次更新或补丁发版都必须做，做完要重新生成单文件并跑 `verify.mjs` 全绿。
4. 推本次新 tag（不强推历史 tag），确认 CI 成功。
5. 确认 npm / npmmirror 实际可下载，解出包内 ZIP 并核对 SHA-256 与 GitHub Release 一致，再更新 `manifest.json` 与 `manifest-<版本>.json`；不得填本地验证包的哈希。
6. **编辑 GitHub Release 的标题和正文**：写入产品更新说明，保留下载文件名、实际 SHA-256、签名状态及已验证的镜像状态。npm 失败时不能宣称镜像已同步。
7. **通过 GitHub 页面或 API 读回 Release**，确认版本、标题、更新日志、文件名、哈希、限制说明均正确。只改仓库 CHANGELOG 不算完成 GitHub 更新日志。
8. 将发布结果、CI 链接、镜像核验和未测事项回填 `HANDOVER.md`，提交推送文档。全部完成后才能宣布发布完成。

发布验收必须同时满足：Release 更新日志已核验、资产可下载、镜像包一致、公开更新清单正确、**HTML 预览已同步且验证通过**。日志缺失时继续补齐，不把“稍后更新”留给下次。

## HTML 预览同步（每次更新与补丁发版都必须做）

`..\Dafeiyu-Go-DeepSeek-Harness-HTML\interactive-preview` 是这套产品的浏览器交互预览。**从 1.7.0 起，任何一次更新或补丁发版，都要把对应变化同步进预览**，否则这次发版算没做完：

1. 启动器 / 安装器界面或流程有变化 → 在预览里实现同一套交互（导航、子项、弹窗、状态与文案一致）。
2. 发了补丁 → 在预览「更新 → 补丁」子项里加上对应的模拟补丁（标题、描述、类型、风险、体积）；高风险补丁需要确认，binary/script 即使确认仍拒绝，预览不能模拟成安装成功。
3. 改了更新 / 服务流程 → 预览右下角信息窗口（转圈 → 绿勾 / 红叉 / 黄色感叹号）要与真机一致，含「先检查更新、再启动服务」的顺序。
4. 改完重新生成单文件并跑验证：

   ```powershell
   cd ..\Dafeiyu-Go-DeepSeek-Harness-HTML\interactive-preview
   node build.mjs      # 生成 Dafeiyu-Go-Interactive-Preview.html（单文件：内嵌样式、脚本、图标、壁纸）
   node verify.mjs     # 全量断言，必须全绿
   ```

5. 预览是**纯前端模拟**：不安装、不联网、不改系统；只允许「立即下载」这类显式外链主动跳转。

## 补丁发版流程（1.7.0 起）

补丁用来处理「改动不大、不值得为它发一个大版本」的东西。清单识别资源 / 页面 / 脚本 / 程序文件四种类型，当前只有 page/resource 可应用，binary/script 拒绝安装。格式、风险等级、本地目录布局与恢复时序见 `PATCHES.md`；这里只讲发版动作。

1. 打补丁包：按 `PATCHES.md` 的目录约定把载荷打成 zip。
2. 算哈希与体积：`Get-FileHash <zip> -Algorithm SHA256`，并记录字节数（清单里的 `size` 是字节）。
3. 上传 zip：放本仓库 Release 资产（推荐）或其它稳定直链，记为 `url`。
4. 往 `patches.json`（正式）或 `patches-preview.json`（内测）追加条目，字段一个都不能少：
   `id`（稳定标识；同一补丁升级时 id 不变、`version` 递增）、`title`、`description`（写给用户看）、`kind`、`risk`、`version`、
   `minLauncherVersion`、`maxLauncherVersion`、**`mergedIn` 先留空**、`publishedAt`、`size`、`sha256`、`url`；`page` 类还要写 `overrides` / `disable`。
5. 同步 HTML 预览（见上一节第 2 条），让这个补丁在预览里看得见。
6. 提交推送。客户端按用户选的补丁策略拉取：`自动下载并安装` 会直接装低风险 page/resource 补丁；高风险需要确认，binary/script 即使确认也拒绝，不下载、不执行、不替换文件；`仅检查更新` 只提示，`关闭` 完全不联网检查。

**大版本合入（X 或 Y 变动时）**：补丁不能永远挂着。准备发 `1.7.0` 这类大版本前，先把线上仍在生效的补丁合进主线代码，然后：

- 已合入的补丁，在清单里把 `mergedIn` 写成**真正合入它的版本**（例如 `1.7.0`）；客户端在本机版本 ≥ `mergedIn` 时不再下发该补丁。
- 当前 `release.ps1` 与 CI 共用 `check-release-gate.ps1` 门禁：**大版本发版时只要还有 `mergedIn` 为空、或 `mergedIn` 晚于新版本的补丁，直接中止发版**，并逐个列出补丁 id、标题与来源文件。只变 Z（如 1.7.0 → 1.7.1）只提示，不阻断；旧 `publish-release.ps1` 已停用。
- 冲突优先级：补丁与内置逻辑打架时**以补丁为准**——补丁声明的页面覆盖内置页，也可以禁用内置导航条目。

---

## 零、仓库现状

| 项 | 值 |
|----|-----|
| 启动器仓库 | `https://github.com/YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Click-To-Run` |
| 默认分支 | `main` |
| 本地仓库 | `G:\DeepSeek DSH\DSH Works\Project\Dafeiyu-Go\Dafeiyu-Go-DeepSeek-Harness-Click-To-Run` |
| 当前版本 | `1.7.0`（收尾中，未发布） |
| 安装器读取的清单 | 仓库根目录 `manifest.json`（公开版本仍为 1.6.0；旧 `manifest-1.6.1.json` 是草稿，不得作为正式入口） |
| 补丁清单 | `patches.json`（正式）/ `patches-preview.json`（内测） |
| 更新日志 | `CHANGELOG.md`（发版脚本会把对应小节写进 `manifest.json` 的 `notes`） |

**当前版本本地验证：**

```powershell
.\release.ps1 -NoManifest
.\verify.ps1 -Dist .\source\dist-1.7.0 -Version 1.7.0
```

正式产物由 tag CI 构建，不手动上传本地验证 ZIP。启动器与安装器各自独立发版；只推本次新 tag，不强推或批量推历史 tag。父级 `release-all.ps1` 仅使用 `-BuildOnly`。

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
拆开之后两边独立维护、各自读自己的版本。本轮启动器 1.7.0 不要求安装器同步抬版本或重发 1.6.0。

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

当前安装器没有 `payload\launcher.zip` 离线回退。清单 / API 和可用下载源均失败时停止并提示错误；不能宣称离线安装可用。

---

## 三、发版流程

### 本地构建验证

```powershell
.\release.ps1 -NoManifest
.\verify.ps1
```

本地脚本编译、检查和打包，供验收使用。禁止用本地验证包哈希更新正式清单，也不手动把该包当作正式 CI 资产上传。

### 正式发布

使用顶部当前发布清单：本仓提交并推送 `main` → 本次启动器新 tag CI 成功 → 核验 Release 与 npm / npmmirror 实际包 → 更新 manifest 并读回 → 更新本次 Release 产品正文并读回。安装器有独立改动时按它自己的版本和发布指南执行。

### CI 版:打 tag 自动出包

`.github\workflows\release.yml` 会在推 tag 时自动编译 + 打包 + 建 Release,
并且**校验 tag 和 csproj 版本一致**,不一致直接失败,防止发错版本。

CI 出包后下载实际 Release ZIP 计算 SHA-256，核验 npm / npmmirror 内含 ZIP 后再更新 `manifest.json`。不要重跑本地构建替代 CI 哈希；本地和 CI ZIP 即使来自相同源码也不保证字节一致。

### 历史手动流程（不用于当前正式发布）

以下 1.3.9 命令仅保留历史构建格式；父级 `set-version.ps1` 仍会同步两仓，不能用于本轮启动器独立抬版本。本地验证加 `-NoManifest`，正式发布仅走顶部 tag CI。

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
| `sha256` | 正式发布必需 | 实际 CI Release ZIP 的 SHA-256；不得留空或填写本地验证包哈希 |
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
