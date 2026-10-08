# 补丁系统（1.7.0，未发布）

启动器 1.7.0 的补丁机制目前支持数据资源和清单页面。脚本和二进制类型保留清单契约，补丁安装流程尚未实现可靠的执行、替换与恢复，因此即使确认也拒绝安装。
本文件写清单格式、风险等级、本地存放位置、应用 / 回滚时序，以及**发版时的合入规则**。
本文件记录当前操作契约；历史 `.launcher-161/SPEC.md` 不在当前工作区，不能作为已实现证明。行为以生产入口、回归覆盖和本文件明确列出的边界为准。

## 一、四种 kind 与风险

| kind | 载荷 | 风险 | 默认自动应用 |
| --- | --- | --- | --- |
| `resource` | 公告、推荐插件、推荐技能 JSON | low | 是 |
| `page` | 清单驱动的动态页面，可覆盖内置页、可禁用内置条目 | low | 是 |
| `script` | PowerShell 脚本（当前仅识别并拒绝安装） | high | 否，确认后仍拒绝 |
| `binary` | exe / dll（当前仅识别并拒绝安装） | high | 否，确认后仍拒绝 |

- `risk` 只有 `low` / `high` 两个值。
- `kind` 是 `script` / `binary`，或 `risk` 是 `high` 时，一律走用户确认；未确认时**不落盘、不做任何改动**。
- 当前实际可应用类型只有 `page` 与 `resource`；`script` / `binary` 即使确认也会明确拒绝，不下载、不执行、不替换程序文件。
- 资源消费者仅接受白名单 `announcements.json`、`featured-plugins.json`、`featured-skills.json`，校验体积、路径与重解析点，生产入口优先读取有效补丁资源。

## 二、官方清单 patches.json

放在仓库根，与 `manifest.json` 同级，按通道分两份：

| 文件 | 通道 |
| --- | --- |
| `patches.json` | Stable / Auto |
| `patches-preview.json` | Preview |

两份清单都走**同一套加速通道**获取（复用 `GitHubAccelerator` / `AnnouncementService` 那套「加速源 + 直连回退 + 本地缓存」），
不允许写死 `raw.githubusercontent.com` 直连。

```json
{
  "schemaVersion": 1,
  "updatedAt": "2026-10-07T12:00:00Z",
  "patches": [
    {
      "id": "2026-10-07-home-card-order",
      "title": "主页卡片顺序调整",
      "description": "把余额卡片移到用量图表上方，并补充说明文字。",
      "kind": "resource",
      "risk": "low",
      "version": "1.0.0",
      "minLauncherVersion": "1.6.0",
      "maxLauncherVersion": "1.6.x",
      "mergedIn": "",
      "publishedAt": "2026-10-07T12:00:00Z",
      "size": 20480,
      "sha256": "<zip 的 sha256，小写十六进制>",
      "url": "https://github.com/YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Click-To-Run/releases/download/patches/2026-10-07-home-card-order-1.0.0.zip",
      "overrides": ["page:home"],
      "disable": ["nav:service"]
    }
  ]
}
```

字段规则：

- `id`：稳定标识；同一补丁升级时 id 不变，`version` 递增。
- `minLauncherVersion` / `maxLauncherVersion`：适用启动器版本区间；`1.6.x` 这种通配只允许出现在最后一段。
- `mergedIn`：合入的启动器版本（如 `1.7.0`）；**空字符串表示还没合入**。
- `url` + `sha256` + `size`：下载和校验必需，缺任何一个该补丁视为无效并跳过（记日志）。
- `overrides` / `disable`：仅 `page` 类使用。

## 三、本地存放位置

根目录 `PatchPaths.Root` = `%LOCALAPPDATA%\DeepSeekHarness\patches\`

```
patches\
  feed-stable.json           Stable / Auto 清单缓存
  feed-preview.json          Preview 清单缓存，旧 feed.json 不作为回退来源
  installed.json             已安装补丁记录
  health.ok                  启动成功标记
  pending.json               正在应用、尚未确认启动成功的补丁 id 列表
  downloads\<id>-<version>.zip
  stage\<id>\                解包后的载荷
  backup\<id>\               应用前的被覆盖文件备份
  transactions\<id>\         写前事务日志及前一版本快照
  scripts\<id>\              历史契约预留，当前不落地 script 载荷
  patch-data\<id>\           resource 类落地目录（启动器优先读这里）
  pages\<id>\                page 类页面描述落地目录
```

`installed.json` 里每条记录带 `id / version / title / description / kind / risk / sha256 / appliedAtUtc / files / script / overrides / disable`。

## 四、应用与回滚时序

1. 看策略：`自动下载并安装`（Install）→ 检查并自动下载，low 风险直接应用，high 风险弹确认；
   `仅检查更新`（Check）→ 只检查、列出来，等用户点；`关闭`（Off）→ 完全不联网检查。
2. 应用前：把要写入 / 覆盖的每个已存在文件复制到 `backup\<id>\`，写 `pending.json` = 该 id，并删除 `health.ok`。
3. 写前保存事务与上一版本快照；resource 落地到 `patch-data\<id>\`，page 落地到 `pages\<id>\`。成功后标记 AwaitingHealth，安装失败立即恢复快照；恢复失败保留日志供重试。
4. 启动器成功启动约 10 秒后调用 `PatchStore.MarkHealthy()`，恢复逻辑仅确认 AwaitingHealth 事务，Installing 事务仍回滚。
5. 下次启动发现未确认事务会尝试恢复；失败保留 pending 与快照，不清理恢复线索。
6. 同 ID 升级保留原始基线，失败恢复上一可用版本；卸载回到原始基线。已安装记录带适用版本范围与 mergedIn，升级后不再启用不适用补丁。

## 五、覆盖与优先级

- `page` 类补丁的 `overrides: ["page:<内置页key>"]` 表示该补丁页替换内置页；渲染该 key 时优先用补丁页。
- `disable: ["nav:<内置条目key>"]` 表示隐藏内置导航条目。
- 补丁之间按 `publishedAt` 从旧到新应用，后者覆盖前者；同一 id 只保留最高 `version`。
- **补丁与内置逻辑冲突时补丁优先**：补丁页 / 补丁资源覆盖内置实现，内置逻辑不得反向覆盖补丁内容；
  内置页被覆盖时不报错，只在补丁页页脚显示来源说明。

## 六、发版合入规则（门禁）

当前 `release.ps1` 与 tag CI 共用 `check-release-gate.ps1`；`publish-release.ps1` 已停用，其历史 `Assert-PatchMergeGate` 不作为当前验收入口：

- 读 `manifest.json` 里当前已发布的版本，与本次新版本比较；
- **X 或 Y 变化（大版本）**：读 `patches.json` 与 `patches-preview.json`，
  只要还有补丁的 `mergedIn` 为空、或 `mergedIn` 解析出的版本晚于新版本，就 **throw 中止发版**，
  错误信息逐个列出补丁 id 与标题；
- **只变 Z**（如 1.6.0 → 1.6.1）：只 `Write-Host` 提示一句，不阻断；
- 缺少 `patches.json` / `patches-preview.json`、坏 JSON 或非法版本时停止；有效清单为空时通过。

一句话：**大版本（X.Y 变动）发版前必须把未合入补丁处理掉，否则发版中止。**

处理方式二选一：

1. 把该补丁的 `mergedIn` 写成这次要发的版本（或更早已实际合入的版本）——前提是它真的已经并进内置逻辑；
2. 从补丁清单里移除该条目——适用于该补丁作废、或改由内置实现的情况。

只改 Z 的补丁节奏是「照常发，不受门禁影响」：`mergedIn` 为空的补丁在 1.6.x 里继续通过清单更新。

## 七、清单维护注意事项

- 每次改动清单都要更新 `updatedAt`，格式 `yyyy-MM-ddTHH:mm:ssZ`。
- `schemaVersion` 当前是 `1`；两份清单保持一致。
- 上架前核对 `url` 可下载、`sha256` 与 zip 一致、`size` 对得上；三者缺一客户端会跳过这条补丁。
- 通道区分：Stable 用户只看 `patches.json`，Preview 用户看 `patches-preview.json`。不确定的补丁先放 preview。
