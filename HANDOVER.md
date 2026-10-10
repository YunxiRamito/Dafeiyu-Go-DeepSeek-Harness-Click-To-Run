# 交接：大肥鱼Go / Dafeiyu-Go Launcher

## 2026-10-10 16:50 v1.7.4 已发布（本节优先）

- Release：`https://github.com/YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Click-To-Run/releases/tag/v1.7.4`（id `408817960`，tag v1.7.4 → `caf7070`）。
- 资产：`DeepSeekHarness-1.7.4.zip`，23,972,592 字节，SHA-256 `7C30E42A28B6E12FE7B43037837813516C92E52D7227226C3E460AB2ACD2643A`（本机 `release.ps1 -NoManifest` 从当前源码构建，`verify.ps1` 通过，121 个产物文件、版本均 `1.7.4.0`）。
- `manifest.json` / `manifest-1.7.4.json` 已指向该哈希（提交 `95346af`）；npm `@yunxiramito/dsh-launcher@1.7.4` 已发布。
- 更新日志已替换为用户当轮给的短文案并标「已发布」（原来那份详细草稿作废）。
- 本轮只发启动器：安装器 Release 与网页预览都没动。
- tag CI run `38039125249` 已取消，避免覆盖 Release 资产。

## 2026-10-10 本地修复版 1.7.4

用户要求本次 DWM 修复版改为 1.7.4，已同步程序显示版本、核心/引导/信息窗口版本及三份清单，重编译和 verify 通过。新包 `../Temp_SetupandLauncher/IntegrationAcceptance-20261010/Dafeiyu-Go-Launcher-1.7.4.zip`，23,972,337 字节，SHA256 `8934FEC20E083C0FF46D151A520000B023E6B25D8E6E0FC5E0C7C540115AECCD`；引导与核心 FileVersion 1.7.4.0，198 输入/121 ZIP 文件校验一致。与已验收 DWM 快照仅七个版本信息文件不同，四轮测量沿用下方记录，未再次测量。安装器独立版本不变，未发布、未推送、未更新线上清单。

## 2026-10-10 DWM 内存修复续做（未发布）

最终主源码构建及本地 ZIP 已完成：`../Temp_SetupandLauncher/IntegrationAcceptance-20261010/Dafeiyu-Go-Launcher-1.7.3-dwm-fix-20261010.zip`，23,972,346 字节，SHA256 `C10D09730CE04C6E751C5505B68FD31333D086271AE4AE11A81470340180D2CE`，版本仍 1.7.3.0；198 输入/121 ZIP 文件哈希一致。最终正式 host 四轮同 HWND 7799388、150%最大化、动画开启：关窗 +30 秒 DWM 提交内存 527.89/514.45/514.60/514.37 MiB，私有工作集 262.02/248.45/248.78/248.68；启动就绪 499.27/226.21，退出 +15 秒 500.00/194.84。没有逐轮增加；仍有一次性原生窗口缓存。12 项 UI 生命周期冒烟、托盘3次开关、外观18项回归、build/verify/diffcheck通过。模型 RPC 编辑/UAC/Explorer壳图标本次未验收；隔离假服务接管和最后一轮服务停止的限制已完整记录在 MEASUREMENT-REPORT.md。未正式发布。

- 用户实测正式 1.7.3 在 150% 缩放、云母 Alt、设置最大化时 DWM 内存增长，关设置不完全下降，退出主进程才释放。按用户要求以普通启动、真实 LauncherContext/SettingsWindowHost 验收，不以设置预览作修复证明。
- 旧版正式路径每次关设置后约增加 55 MiB DWM 提交内存；空 WinUI 窗口及 Solid/默认标题栏也复现约 40 MiB。禁切页动画、标题栏 Reset、计时器解绑没有消除增长。关闭前缩小只降到约 13 MiB/轮，未作为完整修复搬入生产。
- 实施窗口复用：新增 source/SettingsWindow.Lifetime.cs，原生 Closing 与代码 Close 都隐藏并卸载内容；重开同一个 HWND、恢复主题/材质/轮询并重置公告通知自动获取标记。预览模式仍按原有销毁行为处理。动态页 generation 与余额/更新日志会话 generation 阻止迟到结果跨重开更新。
- 临时正式 host 候选四轮同 HWND、144 DPI、2582×1390、每轮 30 次导航：关窗 +30 秒 DWM 提交内存 492.02/492.09/492.02/472.95 MiB（就绪 477.80，第四轮桌面基线下降）；私有工作集 204.56/204.57/189.81/171.20 MiB。显示稳定平台，没有原先每轮约 55 MiB 增长。该结果为候选，最终主源码构建还在单独冒烟验收。
- 构建 Launcher-DwmReuseFinal-20261010 已通过 build 和 verify；所有临时源码、日志、截图与成品在 ../Temp_SetupandLauncher。详细有效/无效对照、Shell 图标注册/UAC限制和隔离网络说明：../Temp_SetupandLauncher/DwmAudit-20261010/MEASUREMENT-REPORT.md。
- 未提交、未推送、未修改线上 release/manifest。保留所有已经发布的 1.7.3 记录。

## 2026-10-10 12:40 v1.7.3 已发布（本节优先）

- Release：`https://github.com/YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Click-To-Run/releases/tag/v1.7.3`（id `408645592`，tag v1.7.3 → `b150b95`）。
- 资产：`DeepSeekHarness-1.7.3.zip`（用户提供的 `Dafeiyu-Go-Launcher-1.7.3.zip`，按仓库既有 `DeepSeekHarness-<版本>.zip` 约定改名上传），23,969,701 字节，SHA-256 `BA9440FE10EE480C7D1861D35F2E8195ABB6CA2617D53D68F98A1D8814E7ED1C`。
- `manifest.json` / `manifest-1.7.3.json` 已指向该哈希（提交 `a9ccc62`）；npm `@yunxiramito/dsh-launcher@1.7.3` 已发布。
- 更新日志 1.7.3 已替换为用户给的合并文案并标「已发布」。
- `.gitignore` 补了 `.build-temp/`：里面 `VBCSCompiler` 的超长路径会让 `git add -A` 直接以 128 失败。
- tag CI run `38024487712` 已取消，避免 CI 自建 ZIP 覆盖 Release 资产、与 manifest 哈希不一致。

## 2026-10-10 主页通知按钮补修（优先于下方）

上一轮将主页通知按钮禁用为预览，用户要求实际可用，已启用并接 `SettingsWindowHost.ExecuteNotificationAction`，真实主进程共用右下角通知的链接/设置/PowerShell 动作。主页仅浏览不确认，动作成功后记录 click/read，PowerShell 拒绝或失败不确认。真实 UIA 四类按钮派发 10 项通过，测试仅拦截执行边界。补修包 `../Temp_SetupandLauncher/IntegrationAcceptance-20261010/Dafeiyu-Go-Launcher-1.7.3-home-buttons-20261010.zip`；入口 `../Temp_SetupandLauncher/BootAnimation-20261010/Launcher-HomeButtons-20261010/DeepSeek Harness.exe`；23,969,701 字节，SHA-256 `BA9440FE10EE480C7D1861D35F2E8195ABB6CA2617D53D68F98A1D8814E7ED1C`。构建/verify/197 输入/121 ZIP 文件校验通过。此包替代上一轮 accepted 包。

## 2026-10-10 下载、通知、模型修复已验收并构建（本节优先）

- 最新本地测试入口：`../Temp_SetupandLauncher/BootAnimation-20261010/Launcher-1.7.3-Accepted-20261010/DeepSeek Harness.exe`；ZIP：`../Temp_SetupandLauncher/IntegrationAcceptance-20261010/Dafeiyu-Go-Launcher-1.7.3-accepted-20261010.zip`，23,969,409 字节，SHA-256 `757CC11936D0F3BB6219E1C5FCEAED22FD6C1A9FB57CB5B3A5594817EA1CFE9F`。未正式发布。
- 已完成 CDN/后端线路隔离及引擎立即生效、过期原生 PID 锁恢复、官方插件网络失败自动归档安装；归档构建沿用精确版本授权，取消贯穿依赖构建/注册，授权重试保留原提交及路径。官方两次自动重试耗尽会返回，避免无限等手动操作使兜底失效。
- 通知编辑使用原 ID，已展示/已读/点击用户不重送；等待队列更新内容并保留延后状态；主页 Markdown 公告/当前通知按钮预览无回执；管理页每窗口各自动获取一次。取消编辑保持原内容，仅修改正文保持原到期时间/按钮/发布时间。
- 模型刷新后恢复行按钮，保持 DSH 原顺序与用户排序；真实编辑留空 API Key 不清除原凭据。主页版本及服务状态已移到 Token 用量下方，快捷入口删除。
- 冒烟后最终 Release 构建与 `verify.ps1` 通过；197 个源码输入匹配，121 个 ZIP 文件逐项哈希匹配，无 PDB/日志/私密设置。仍有既有过时网络 API 等编译警告。
- 定向回归：通知客户端 183、通知管理 86、服务端真实 HTTP 239、Git 预算 59、官方插件 103、修复 63、插件更新 71、归档 24、原生锁互操作 66、模型 RPC 40；真实 WinUI 模型 41、通知编辑/主页 18；下载任务、引擎切换、拓展页签、最终包主页及下载任务 GUI 通过。证据见 `../Temp_SetupandLauncher/IntegrationAcceptance-20261010/REPORT.md`。
- 用户本轮明确授权通知服务端 SSH 自动更新，已于 2026-10-10 05:27:58 +08 上线，包含 Git 预算修复及 Deferred CHECK 兼容迁移；API/数据库 healthy，HTTPS 与已有生产回执只读校验通过，未修改真实通知。部署报告与回滚见 `../Temp_SetupandLauncher/NoticeEdit-20261010/PRODUCTION-DEPLOYMENT.md`。
- 未改写真实 DSH profile、替换已安装启动器或发布 GitHub/npm。完整真实插件网络安装及 UAC/自更新流程仍未对用户环境执行；不能把隔离测试描述为真实用户插件已安装。

## 2026-10-09 23:45 v1.7.3 本地手测包已构建（未发布，本节优先）

- 手测入口：`G:\DeepSeek DSH\DSH Works\Project\Dafeiyu-Go\Temp_SetupandLauncher\Dist\Dafeiyu-Go-Launcher-1.7.3-local-test.zip`，23,828,206 字节，SHA-256 `6d78fd216b13f09bbfbacf5970b2f522d16c7006658290abd49542b76e0740a5`。这是本地测试包，不是正式 Release。
- Release/x64 构建 0 错误、42 条警告；启动器、引导程序、信息窗均报告 1.7.3.0。自检及 ZIP 解压后的 `verify.ps1` 通过（2 条环境提醒：未提供 DSH 目标目录，源码命中仅在注释中的本机路径示例）。包内 120 项，没有 PDB、日志或私密配置。
- 当前回归复跑通过：通知客户端 166、下载任务、插件安装修复 60、设置搜索 214、更新元数据 57、启动更新门控 14；安装器下载修复 53、安装失败收尾 25。日志位于父目录 `Temp_SetupandLauncher\Build\Regression-*-rerun.log`。
- 待做：由用户手测包并完成人工 GUI 截图验收；模型/供应商/鸣谢真实写入、信息窗、更新源切换、完整安装/卸载/UAC/取消路径仍未做真实环境验收。跨卷回归因 Windows 错误 5 无法创建盘符别名，不能算通过。
- 安装器项目版本仍为 1.7.2.0，本轮没有生成或声称生成安装器 1.7.3 包。工作区保留未提交改动；未提交、推送、打 tag 或发布。临时产物继续只写入 `Temp_SetupandLauncher`。

## 2026-10-09 14:45 v1.7.2 已发布（本节优先）

- Release：`https://github.com/YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Click-To-Run/releases/tag/v1.7.2`（id `407608502`，tag v1.7.2 → `649f685`）。
- 资产：`DeepSeekHarness-1.7.2.zip`，23,571,328 字节，SHA-256 `821B1218CBB625186F512D6AA182C1BE92D596308F5C8E838DF281EFBF94709A`（用户提供的本地构建）。
- `manifest.json` / `manifest-1.7.2.json` 已更新为该哈希并推送（`d0d0394`）；npm `@yunxiramito/dsh-launcher@1.7.2` 已发布。
- 本版修复：.dym 与原版 DSH 备份的导入导出、技能页大加载。

## 2026-10-09 00:10 v1.7.1 已发布（本节优先）

- Release：`https://github.com/YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Click-To-Run/releases/tag/v1.7.1`（id `407026093`，tag v1.7.1 → `5305347`）。
- 资产：`DeepSeekHarness-1.7.1.zip`，23,560,998 字节，SHA-256 `BBEFB7C1D3C1C8AA5A087C68A6DC889CE4796F22B914D432C08F0448BA82FE2E`（用户提供的本地构建；压缩包比 1.7.0 小很多，因清掉重复运行库）。
- `manifest.json` / `manifest-1.7.1.json` 已更新为该哈希并推送（`9c7884b`）；npm `@yunxiramito/dsh-launcher@1.7.1` 已发布。
- tag CI run `37806490535` 已取消，避免 CI 产物覆盖 Release 资产。

## 2026-10-08 19:40 v1.7.0 已发布（本节优先）

- 正式 Release：`https://github.com/YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Click-To-Run/releases/tag/v1.7.0`（id `406746499`，tag v1.7.0 → `842b128`）。
- 资产：`DeepSeekHarness-1.7.0.zip`，66,200,443 字节，SHA-256 `736A28A9479E698EA4772857B4DF88C5D79EBC4A87464A255DA630975C3D8D54`（本地发布流程构建；tag CI 因公共 runner 卡在回归步被取消）。
- `manifest.json` / `manifest-1.7.0.json` 已登记上述哈希并推送（1d29326）；npm `@yunxiramito/dsh-launcher@1.7.0` 已用 `publish-npm.ps1` 发布。
- 本地验收：`release.ps1 -NoManifest` 成功；`verify.ps1 -Dist .\source\dist-1.7.0 -DshRoot 'G:\DeepSeek DSH' -Version 1.7.0` 通过（1 条提醒）；CI 的 7 套回归本地全绿（PatchSystem 改成离线 stub 后 127 项 5.6 秒）。
- ⚠️ 重跑 tag CI 会用 CI 构建的 ZIP 覆盖同一 Release 资产，重跑后必须用 CI 资产哈希重新更新 manifest。

## 2026-10-08 17:30 最新测试包与收尾（本节优先）

- 当前 ZIP 为 `DeepSeekHarness-1.7.0-feedback-log-startup-review.zip`，66,200,155 字节，SHA-256 `7D9F90AE5C93340EF1BD03BFE752C2B5CE6E01F72674596C1465411FFAC9A3B8`。入口 `source/dist-1.7.0-feedback-log-startup-review/DeepSeek Harness.exe`，旧测试目录不含本轮全部修改。
- 新建反馈/补充可选上传启动器日志，默认不勾选；公共卡右下角仅便签图标，开发者分页查看与原生另存为下载。固定四日志、每条最近 128 KiB、JSON 最多 1 MiB，双端脱敏和严格服务端验证，封禁阻止上传，删除反馈级联清理。
- 封禁列表与处理反馈展开失效修复父级鼠标事件抢走 ToggleButton 焦点，ButtonBase 豁免及异步布局定位已验证；24 项真实物理按住/松开检查通过。
- 启动信息窗短标题“检查插件”“检查补丁”，描述“正在读取插件版本。”“正在读取补丁版本。”；最终“一句一行”显示无更新/更新数量/失败数量。公告/通知后台检查只记日志，原 UI 自排队等待 8 秒卡顿已修复。真实点击公告已读和通知按钮后，第二进程同配置不再重复显示。
- GitHub/内置后端 HTTPS 只读请求有限直连回退，写请求/取消不重放，Token 仅 api.github.com；实际推荐文件读取通过。统一日志记录脱敏、时间/时区、会话/PID、调用方法与完整异常链；主 WriteLog 保留委托兼容，源码行指向包装层并另记 caller 方法。
- 最终快照 `build-input-final-r2` 与 117 个现有输入 hash 一致；416 ZIP 文件逐个校验。八组关键回归、反馈 GUI 14 项、最终 InfoHost 13 状态检查通过；服务端反馈日志 API 已部署并完成只读验收。
- 完整证据、服务器镜像/备份和安装器状态见 [本轮验收报告](../preview-artifacts/1.7.0-feedback-log-startup-20261008/validation.md) 与 [总交接](../HANDOVER.md)。用户删除 Backup 文件保持删除；真实安装/卸载/UAC/Explorer 重启尚待实机。此为本地测试包，未修改公开 manifest、推送或正式发版。


## 2026-10-08 14:19 图片、反馈封禁与本轮修复交接（优先于下方历史章节）

启动器 1.7.0 新本地检查包已构建；服务端已按本会话用户批准更新现有服务器。启动器与安装器仍未正式发布。安装器当前源码/测试包为 1.7.0.0，公开安装器历史基线 1.6.0 不代表当前源码。

- 新 [启动器检查 ZIP](<G:/DeepSeek DSH/DSH Works/Project/Dafeiyu-Go/Dafeiyu-Go-DeepSeek-Harness-Click-To-Run/DeepSeekHarness-1.7.0-download-feedback-review.zip>)：66,187,056 字节，SHA-256 `AF6C6C3D853ED58B31DA43F01D4CD9951F2860DF9C54F8037A91AB3CD2ECA256`。目录 `source/dist-1.7.0-download-feedback-review`，入口 `DeepSeek Harness.exe`。构建快照 `build-input-feedback-images-bans-r3`，115 个输入 hash 一致；416 个包内文件逐项校验通过。旧 review ZIP 不包含这轮最新改动。
- [安装器测试 EXE](<G:/DeepSeek DSH/DSH Works/Project/Dafeiyu-Go/Dafeiyu-Go-DeepSeek-Harness-Setup/dist/test-1.7.0-download-fixes-20261008/DSH-Installer-Setup.exe>)已供用户测试，版本 1.7.0.0，11,432,005 字节，SHA-256 `1E50347934A332906E38A011EEF16277E0D262B8A0C7880F9BC1214FA6B91DC3`；本轮反馈功能需使用新启动器检查包。安装器沿用用户本轮确认的启动检测运行库、缺少时先显示普通下载/安装窗口的现有流程，未改为捆绑运行库。
- 更新元数据超时回退、官方插件有限自动重试/实际缓存字节进度、图片国内 CDN 默认、用户提供启动文案与实际换行已应用并纳入启动器构建。用户删除的三个 Backup 文件只在快照补齐，未恢复工作区。
- 反馈新回复为蓝色感叹号，音效重新从头播放，helper 显示 5 秒后关闭；反馈紧凑排版、公共/本人/开发者筛选后每页 20 条、总数/翻页、确认删除及空末页回退已完成。
- 新建反馈与补充每次最多 5 张、每张最多 5 MiB 的 JPG/PNG/WebP 静态图片；96px 正方形裁剪横排/换行，点击展开完整图片，再点击缩回。选图改用支持管理员进程的 WinAppSDK 1.8 picker，正式管理员权限不变。服务端完整解码后重新绘制为 JPEG，剥离元数据/附加内容，输出长边最多 2048px；像素、字节、频率、并发均设上限。SkiaSharp 4.153.1 的依赖审计未报告已知漏洞。
- 管理员可按匿名机器码限时/永久封禁、删除并封禁、从封禁列表分页解封；服务端对 JSON/multipart 的新建和补充全部校验封禁；客户端底部显示期限和原因并禁用发送，允许继续查看反馈。
- 回归：客户端反馈 134、信息窗 IPC 70、图片来源 80、更新元数据 14、官方插件 81、设置搜索 193、下载任务全部通过；服务端真实 HTTP/SQLite 106、既有离线 59、Linux/独立 PostgreSQL 74 通过。主/helper Release 构建 0 错误，服务端 0 警告/错误；最终实际反馈页与13状态信息窗 GUI 通过。图片/封禁 fixture GUI 26 项通过，包含真实多选、取消和移除预览；仅在独立副本执行，不混入正式包。GUI 自动化为普通权限，实际 UAC/elevated 流程待实机测试。
- 生产镜像 `sha256:65b54b237b80ae16ef4dd173757ab74d9b33eb951af5c461d0ad3457971689e4`，备份 `/home/dafeiyu/developer-center-backups/feedback-20261008t055314z`，含数据库、源码、TLS配置及旧镜像/回滚脚本。FeedbackImages/FeedbackBans 迁移存在，API/db healthy、TLS running；生产验收只读取和检查匿名401，未上传fixture、未执行真实删除/封禁。
- 完整证据与边界见 [本轮验收报告](<G:/DeepSeek DSH/DSH Works/Project/Dafeiyu-Go/preview-artifacts/1.7.0-download-fixes-20261008/final-validation.md>)。真实安装/卸载、自更新替换、官方DSH完整安装及音效听感仍需实机测试。


## 2026-10-08 用户截图修复补充（未发布）

- 更新元数据原先选择 backend 后仅请求 `/api/fetch`，其超时错误把完整地址和长异常展示到界面。`UpdateMetadataReader`、`UpdateSupport`、`DshUpdateService`、`InstallerUpdateService` 现在保留 backend 为第一候选，约 500ms 后并行独立元数据来源，失败显示短提示，完整诊断记入设置目录 `launcher.log`；更新包下载仍保留已有来源与校验策略。
- 官方插件安装加入 `PluginOperationSupport` 的 profile 级互斥租约，锁位于临时目录 `Dafeiyu-Go/plugin-operation-locks`，不接管或删除官方 `package.json.lock`。官方 CLI 使用 pnpm NDJSON reporter，显示实际获取字节/缓存写入量与依赖数量；取消会终止子进程及 profile 排队等待。
- `PluginStoreService` 将官方插件安装接入 `DownloadTaskCenter`，超时或暂时网络故障自动重试 2 次，仍失败可在下载任务中重试或取消；官方过程不显示不可实现的暂停按钮。400/401/403/404、构建授权等永久失败不自动重试。详细官方命令输出保留在日志。
- 本次定向验证：UpdateMetadata 14；DownloadTasks 全部通过；PluginOfficial 81（新增并发排队/取消、重入租约、临时失败分类、NDJSON 字节累加断言）；PluginInstallRepair 55；PluginInstallationState 19；PluginUpdateCompare 62。离线回归未运行真实 node/pnpm，也未修改真实 profile。插件回归子进程的 `TEMP/TMP` 必须指向工作区隔离目录，本机默认 Temp 在当前执行环境中拒绝创建 fixture。
- 当前改动未提交、未构建新的最终检查包、未发布；上述回归不能替代真实网络超时重试、官方管理器字节进度和并发安装的 GUI 验收。启动流程原文案留待用户修改，本修复不替用户改写。

## 2026-10-08 09:23 最终检查包交接（本节优先）

启动器 1.7.0 已完成本轮开发收尾、反馈客户端/服务端及示例网页同步，并生成本地检查包；**尚未正式发布 1.7.0**。安装器仍独立为 1.6.0。用户单独授权发布的空 patches.json 已完成，提交 `7093b2bc1b3cf2ddbfafe0ba558d1fa75d8204df`；这不表示整版发布。

- 当前 [dist](<G:/DeepSeek DSH/DSH Works/Project/Dafeiyu-Go/Dafeiyu-Go-DeepSeek-Harness-Click-To-Run/source/dist-1.7.0-review>)、[启动入口](<G:/DeepSeek DSH/DSH Works/Project/Dafeiyu-Go/Dafeiyu-Go-DeepSeek-Harness-Click-To-Run/source/dist-1.7.0-review/DeepSeek Harness.exe>)及[ZIP 检查包](<G:/DeepSeek DSH/DSH Works/Project/Dafeiyu-Go/Dafeiyu-Go-DeepSeek-Harness-Click-To-Run/DeepSeekHarness-1.7.0-review.zip>)。ZIP 为 66,158,906 字节，SHA-256：`4556784DE17A61460CBCF79CDEBFE02101B47F2349EBE79CFB00BF0BFD36E001`。
- 最终构建快照 `build-input-feedback-complete-r4`，110 个主程序/helper 输入与当前源码哈希一致。包内 416 个文件逐项大小及解压流 SHA-256 验证通过；dist 无运行日志、fixture、用户设置、临时文件、PDB、Token 或非空凭据配置。
- [最终验收报告](<G:/DeepSeek DSH/DSH Works/Project/Dafeiyu-Go/preview-artifacts/1.7.0-closeout-20261008/final-validation-feedback.md>)记录全部产物、日志、截图、部署、边界和复现脚本；旧 dist/ZIP 与此前哈希仅作历史记录。
- 原生“关于—反馈与建议”默认公开；我的提交按匿名稳定机器指纹；四种状态、补充、开发者处理及后台回复信息窗已接入。终态补充客户端/服务端均拒绝。列表与补充独立分页、按需加载，正文及历史不截断；回复按反馈 ID + 正文摘要持久化去重。
- 真实窗口修正并复测“我的提交”/wdtj 搜索范围、“反馈处理”/fkcl 解锁后的实际模块导航，以及状态筛选标题裁切。反馈回归 55、设置搜索 193、通知客户端 121、信息窗 IPC 62、插件安装状态 19、更新元数据 14、补丁 127、下载任务通过；InfoHost 12 状态和公告/通知/扩展真实 GUI 通过。
- 服务端隔离 PostgreSQL/API 合约 40 项、既有离线回归 59 项通过；生产 API/DB healthy、TLS running。公开及本人反馈 GET 200，无 Token 管理 401，生产验收只读。备份 `/home/dafeiyu/developer-center-backups/feedback-20261008t010758z`，镜像 `sha256:e17ad448c55592e5cbf2d1902c58b7832798e03a302f0c764cafba9026a22f22`。详见[服务端反馈报告](<G:/DeepSeek DSH/DSH Works/Project/Dafeiyu-Go/preview-artifacts/1.7.0-feedback-server/feedback-api-validation.md>)。
- 现有 `dafeiyu`/start、reload/restart、stop、status、Token 天数创建（0 永久）/list/remove、Bash Tab 补全已部署；stop 保留数据库卷。
- 网页目标是用户指定的 [interactive-preview](<G:/DeepSeek DSH/DSH Works/Project/Dafeiyu-Go/Dafeiyu-Go-DeepSeek-Harness-HTML/interactive-preview>)，保留 [4173 HTTP 预览](http://127.0.0.1:4173/?launcher=1)。最终反馈专项 47 项、通用交互 126 项，浏览器异常和意外外网请求为 0；测试 profile/temp 在 G 盘并已清理。网页反馈及管理员操作保持本地模拟，不调用生产反馈 API。
- 更新检查已细分启动器/安装器/DSH/插件/补丁/公告/通知，raw 优先、限时并发与取消接线完成；信息窗缓存优先非系统盘并正常关闭清理。此前补丁未发布造成 69.159 秒多源等待的日志已归档；本轮未重新测量完整真实 DSH 启动耗时。
- 主工作区仍保留用户删除的三个 Backup 文件；仅在构建快照补齐既有项目引用。复现使用 [Build-Review.ps1](<G:/DeepSeek DSH/DSH Works/Project/Dafeiyu-Go/preview-artifacts/1.7.0-closeout-20261008/Build-Review.ps1>) 和新快照名。
- GUI 采用隔离设置的只读预览；完整自更新、新机安装、真实 DSH 全流程耗时、音效听感与原生回复提醒的人工联动仍待实机检查。没有创建正式 tag/Release、npm 发布或更新公开 manifest。

下方旧章节保留历史背景，旧版本状态、待办和产物路径不能替代本节及最终报告。



> `1.4.9` 是品牌过渡版。可见品牌改为“大肥鱼Go / Dafeiyu-Go”，内部可执行文件名、
> 数据目录、注册表键、计划任务、快捷方式和 npm 包名保持不变。迁移边界与发布顺序
> 以 [`TRANSITION.md`](TRANSITION.md) 为准。

> 当前源码目标版本为 `1.7.0`（收尾中，**未发布 / 未推包**）；公开清单为已发布 `1.6.0`。
> 以下保留各轮历史交接，旧章节的版本、待办与验证结果不代表本轮状态。

最后更新：2026-10-08

## 2026-10-08 交接补充：1.7.0 未发布工作区

- 安装器选择现在作为新建启动器设置的默认值继承。安装成功后实际 `LauncherRoot/installer-defaults.json` 保存来源和绝对 DSH 路径，首次创建时优先读取；本机路径匹配的 `installer-state.json` 是回退。`china` 对应 `Accelerated + Auto`、`backend` 对应 `Accelerated + backend`、`official` 对应 `Official + Auto`，已存在的用户设置不覆盖。机器范围与自定义组件目录使用安装目录默认，不依赖安装器账号。修复恢复来源，显式静默参数优先；新增 InstallerSourceDefaults 35 项、ProxyScope 32 项通过，未写真实配置。完整产品构建仍待主任务，未发布。

- 下载后端接线正在收尾：设置的 `MirrorSource=backend` 且 `UpdateSource` 不是 `Official` 时，`dsh plugin`、pnpm/npm fallback、DSH 更新、技能与推荐插件安装都对单次子进程注入 `https://202.189.21.218:8787/api/npm/`；GitHub 依赖用同次进程的 CA pin、`url.*.insteadOf` 指向 `/api/git/`。不写用户全局配置、不关闭 strict TLS、不传 GitHub/NPM token。旧 `dist-1.7.0-test-20261008-final` ZIP 不是接线后的最终产物，必须完成后端部署、隔离 npm/Git 验证和重新构建再更新摘要。

- 当前版本目标为 `1.7.0`，源码变更未提交、未推送、未正式发布。管理员 token 不得随启动器发布。
- 管理员 token 的桌面端存储通过 `LauncherSettingsStore.SetAdminToken` 调用 DPAPI `CurrentUser` 加密，写入 `%LOCALAPPDATA%\\DeepSeekHarness\\LauncherSettings.json` 的 `adminTokenProtected`；发布包不包含该值。
- `WinUIProgram.UpdateItemClick` 现在通过 `_manualUpdateWindowRequested` 包围手动检查更新，`ShowUpdateWindow` 在服务提示占用时允许手动更新接管窗口。
- `ServerMetricsClient` 对服务器监控专用 `HttpClient` 强制 HTTPS，默认使用 `NoticeTransport`；仅限定部署 IP/端口使用既有 SPKI pin，其它地址必须通过系统证书校验，不接受任意自签或过期证书。服务器页性能每 2 秒刷新，在线历史每 5 秒刷新，离页/关窗取消在途请求。
- Footer 布局：展开时在线人数/服务器监控内容左对齐，收缩时圆点与数字居中，收缩 padding 和宽度缩小以适应窄导航栏。
- 最终测试副本为 `source/dist-1.7.0-test-20261008-final`，构建与 verify 0 失败 / 2 提醒。实际 GUI 通过范围：Home 搜 `fwqjk` 跳服务器并读取 metrics/history、PrintWindow 截图；Home 搜 `gly` 跳 API 并显示 AdminTokenBox；InfoHost 12 状态和 InfoWindowClient 62 项通过。当前进程无管理员 token，仅留下只读预览供用户查看，不是正常服务启动；听音未测。最终报告见父目录 [final-validation.md](../preview-artifacts/1.7.0-final/final-validation.md)，桌面维护者 token 文件已确认存在，未读取内容。
- 服务器页离开、补丁页面覆盖或设置窗口关闭时停止计时器并取消在途请求。公网历史 404 已部署修复，后台采样持续部署：readiness/metrics/history 200、匿名管理接口 401、pin 读回通过；最终备份 `/home/dafeiyu/developer-center-backups/metrics-20261007T204933Z`，镜像 `sha256:edd1714e53dbe546cf19e3b041d4be7ddeb4fe342a3accd3da88b1d7a227734b`。网络卡片显示上传 Mbps、30 Mbps 分母百分比/进度与动态峰值纵轴的 `ServerUploadChart`。证据见 [后台采样摘要](../preview-artifacts/1.7.0-final/background-metrics-summary.md)。
- 在线人数订阅默认关闭，设置窗口打开后立即 GET `/api/presence` 并每 5 秒刷新，关闭不再 GET；公告与隐私心跳保持原频率并独立运行。服务器页才 GET `/api/presence/history` 压缩完整响应，PostgreSQL 每分钟持久化真实计数，24 小时/1441 点，不补造过去，缺口不连线，初次历史可能不足 24 小时。预览只读请求不登记在线，真实返回 0 就显示 0。
- 网络占用百分比/进度条始终以 30 Mbps 为分母，上传趋势纵轴按近 5 分钟真实峰值加 15% 余量，取 1/2/5/10 整洁刻度，空/零最小 0.1 Mbps；不能把动态图轴描述成固定 30 Mbps。
- 用户新增的后端离线/恢复提示已实现。同段离线只提醒一次，确认恢复后再弹恢复；服务、更新或 Notice 正占用信息窗时排队。离线标题“大肥鱼后端服务器离线”，正文“您可能无法及时收到公告与通知，不影响依靠后端的基础功能，以及DeepSeek Harness的使用。”；恢复标题“你的大肥鱼又上线了”，正文“后端服务器已恢复在线状态，所有功能均可正常使用。”。最终副本实际显示与播放待本地验收。
- 音效使用 `PlayChime` 区分来源：公告/通知、后端离线/恢复与更新成功都有提醒音，只有普通服务启动/重启成功不播放音效。InfoWindowClient 62 项验证实际 IPC 序列化协议，未启动 helper 或播放音频，不能代替最终 GUI/听音确认。
- 设置搜索补齐 1.7.0 非标准布局新增项：服务器监控/硬件、在线人数、管理员 Token、公告 API 配置与轮询、在线统计/回执和开发者公告/通知/信息窗预览。拼音字表补丁、传、磁、频、硬、遥；开发者管理项当前窗口解锁后重建索引，点击搜索结果会切到正确模块与公告/通知页签。SettingsSearch 133 项通过，包含 24 小时在线趋势与人数图表查询，定位锚点 `ServerPresenceChart`；网络上传带宽/速率/百分比覆盖中文与首字母，最终 GUI 定位结果待报告。
- 后续新增设置功能必须同时同步搜索索引、中文别名、拼音首字母字表和定位落点；新增页面或非标准行布局必须显式登记，不允许仅加界面而漏掉搜索。测试需覆盖中文、首字母、结果页面与锚点，真实窗口点击定位也要验证；不要按缩进猜测搬移 XAML。

## 公告通知与实时预览（2026-10-08）

本节为最新状态，后文 HTTP、会话 Token、纯文本 Markdown 和 helper 平铺目录等记录均为历史状态。

- 公告管理与通知管理分开，使用“编辑正文”按钮打开共用 Markdown 弹窗编辑器；保存应用、取消保留原稿。字号、粗体、斜体、下划线、删除线等工具已接入原生渲染。
- 公告支持获取 GitHub 当前文件及 SHA、新增、编辑、删除、上下排序、标题/正文/标签/日期/置顶、发布。整表写入使用编辑时 SHA，409 冲突要求重新获取，避免覆盖并发修改。
- 服务端事务保证只有一条当前通知；重新推送用新 ID 重新计数，旧指标保留。支持独立显示日期、最多两个 HTTPS/设置/PowerShell 按钮；PowerShell 仅点击并明确确认后执行。
- GitHub Token 使用通用的 API 与翻译设置；管理员 Token 同页提供显示、保存、清空并以 DPAPI 加密持久化。未填写时开发者页显示红色提示。已验证密文、重读和清空。
- 正式开发者工具保留信息窗口预览。预览与真实操作各用独立会话，不执行动作或上报回执；设置预览立即并每 5 秒只读真实在线人数，关闭停止请求，不发心跳。在线组件位于 FooterMenuItems 最后一项，下载任务与开发者位于其上，保持正常主题颜色。
- 信息 helper 独立打包到 `info-host/`，必须包含 exe/dll/pri/App.xbf；不能混用主程序 PRI。运行副本位于设置目录 `info-host-cache/<guid>`，解决 Temp 目录权限和资源加载崩溃。
- 通知尺寸测量已区分 XAML DIP 与窗口物理像素。公告/通知宽 480、最大高 480，正文滚动，图标对齐左上标题，自定义按钮单行省略并显示全文提示；其他状态宽 380，图标居中，隐藏进度条与页脚无空行间距。结果切换恢复 XAML 图标透明度，避免合成动画无法显示。
- 公网 API 为 `https://202.189.21.218:8787`；保留既有有效证书，客户端仅该 IP/端口固定 SPKI `2c72728755267c607124b2ac86e431743c34af5c6069aa22bbcc401e0f626f0b`。202610070003 日期迁移已部署，真实 PostgreSQL 单例/重推/计数/日期验证通过；测试通知已撤回。
- 当前回归：PatchSystem 127、ClientNotice 117、DeveloperNotice 58、GitHubAnnouncement 21、InfoWindowClient 62、InfoHost 12 状态、服务端 59、SettingsSearch 133、HTML 124 + 162。Helper 状态覆盖普通、下载、短通知、长通知、结果与固定右下锚点；仍有历史 WebRequest 弃用警告。实际 GUI 通过范围和截图见最终验收报告，听音/正常管理员服务启动/自更新/新机安装未测。
- 网页实时预览 `http://127.0.0.1:4173/?launcher=1`，启动命令 `node dev-server.mjs 4173`（HTML 的 interactive-preview 目录）。网页使用本地模拟数据，Token 只保存在内存；不向真实服务发送管理操作。
- 未执行 GitHub 公告正式发布、提交、推送、tag/release、npm 或公开 manifest 更新。全部改动完成后重新构建并提供本地测试入口，等待用户明确反馈本地测试无问题后推进正式发布。补丁 page/resource 已接实际资源消费者与事务恢复；binary/script 安装仍拒绝，参见 PATCHES.md。

## 1.6.0 正式发布 + XAML 事故复盘（2026-10-07 02:25）

**发布（已上线，2026-10-07 03:03）**：`.\release.ps1` 与 `.\publish-release.ps1` 均已执行。
`DeepSeekHarness-1.6.0.zip` sha256 `f6a9488c3a47ca1c8f373f669c15dc126a966837063c0454cf52a938632ea8b4`（12,020,168 字节 / 90 文件）；
GitHub Release `v1.6.0` 已创建（非草稿、非预发布），资产已上传且**线上摘要与本地、与 `manifest.json` 的 `sha256` 三方一致**；
`main` 已推送（`6b8b2da`），标签 `v1.6.0` 已推送。
Release：https://github.com/YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Click-To-Run/releases/tag/v1.6.0
（推送时旧标签 `v1.3.20` 因远端已存在被拒，与本次发布无关。）
> ⚠️ 修复前那一版 zip (`92d732cf…`) 是用丢绑定的源码打的，**已作废，别外发**；修复后中间版 `7dd71ab2…` 未发布。
> ⚠️ 发布脚本坑（已修）：`publish-release.ps1` 原先用 `-Body [byte[]]` 上传，实测**少传末尾 420 字节**，
> 线上资产与 manifest 哈希对不上（客户端自更新会校验失败）。已改为 `-InFile` 流式上传，
> 并在上传后自校验 `asset.digest`，不一致直接抛错——这个检查以后能拦住同类问题。

**绑定缺失（比事件更深的一层，修复前的"内容不见了"就是它）**：
编译器的 obj 中间产物同样会**吃掉 `{x:Bind}`**（70 处）与 `x:DataType`（5 处）——
只按连接映射补事件，UI 会因为**所有数据绑定为空**而只剩空壳卡片（名称/简介/作者/star 全不见）。
真正无损的修法是**整块替换 5 个资源模板**（`PluginCardTemplate` / `SkillCardTemplate` /
`DeveloperEntryTemplate` / `HomeAnnouncementTemplate` / `SettingsSearchSuggestionTemplate`），
源码取自现场备份 `preview-artifacts\xaml-recovery\SettingsWindow.xaml.broken-*`；
另按备份补齐 `DefaultPortRadio` 的 `Checked="PortModeRadio_Checked"`。

**事故**：用脚本按"缩进猜块边界"搬 `SettingsWindow.xaml` 的自检横排时切错了边界，
把插件页 TabView 一段搬走/搬丢，中途又用编译器留在 `source\obj` 里的副本"恢复"，
结果丢了 **62/64 个 `Click=` 事件**（构建仍 0 错误，属于静默损坏）。

**恢复**（有效且可复现）：
1. `source\obj\Release\net8.0-windows10.0.19041.0\win-x64\SettingsWindow.xaml` = 结构完整的中间产物，
   **带 `x:ConnectionId`、没有事件属性**；
2. `source\obj\Release\net8.0-windows10.0.19041.0\win-x64\SettingsWindow.g.cs`（事故前那次构建）
   里有 `case N: // SettingsWindow.xaml line L` + `((Btn)el).Click += this.Xxx_Click;` 的**连接映射**；
3. 按 `x:ConnectionId='N'` 把 94 个事件里 93 个注回元素 → 再清掉注入属性 → `Click=` 回到 64 个；
4. 导入区（PluginLinkBox 等）从现场备份 `preview-artifacts\xaml-recovery\SettingsWindow.xaml.broken-*` 取回。
- 验证（最终）：`source\SettingsWindow.xaml` 与现场备份**逐项一致** —— `x:Name` 374/374、`{x:Bind}` 70/70、
  事件绑定 70/70，`Compare-Object` 差异为空；构建 0 错误；
  打包后成品实测：插件页 `插件/技能` 页签、导入框、`官方推荐/在线插件/本地插件`、卡片 `dsh-meme` 与作者/star 文本全部真实渲染；
  回归 10/11 套通过（唯一失败见下条）。
- 唯一失败 `LauncherSafety.Regression`：`linked target unchanged`，**旧夹具缺陷非产品问题**（第 206 行详述：
  前面的合法 PAX 解压会先删掉 sentinel，本机允许建符号链接才走这条断言；产品侧 `RejectReparseAncestors` 行为正确）。
- **教训**：① 不要用脚本按缩进猜 XAML 块边界搬块，要用唯一文本锚点 + `edit`；
  ② `source\obj` 里的 `.xaml` 是编译器处理过的中间产物，**事件和 `{x:Bind}` 都会被吃掉**，不能当源码恢复；
  ③ 恢复 XAML 后必须同时核对三类：`x:Name`、事件属性、`{x:Bind}`，只看"能编译"会把静默损坏放行。

**本轮同时修掉**：插件页顶部改为「仓库导入」区（粘贴 GitHub/owner/repo/tarball/本地路径 + 安装插件 + 热加载提示），
`插件自检 / 一键修复 / PluginHealthText` 挪到插件页**底部**（页签之后，x:Name 与 Click 不变）；
组内页排版统一：`PivotHeaderItem` 字号 28（与独立页标题同号）、`AddGroupTab` 里 `PivotItem.Margin = (0,20,0,0)`
（与 v1.5.4 旧版写法一致，之前被代码里显式的 `new Thickness(0)` 盖掉过）。

## 进度小窗动画（2026-10-07 02:00）

- `UpdateProgressWindow` 现在有 Win11 通知那种出入场动画：**先把窗口渲染出来**（窗口先摆到
  `DisplayArea.OuterBounds` 右边的屏幕外、内容 `Opacity=0`），等 `CompositionTarget.Rendering`
  给到第一帧之后再开始滑动；入场从屏幕外滑到右下角、同时淡入，退场滑回屏幕外、同时淡出，
  曲线是 `cubic-bezier(0.16, 1, 0.3, 1)`（自己实现的二分求 t + 三次贝塞尔）。
- 用**帧数**推进而不是墙钟：启动阶段 UI 线程被插件/市场加载占着时，墙钟算法会直接"到点瞬移"，
  帧推进保证动画永远是从头播一遍（代价是忙的时候整体变慢，可以接受）。
- 关闭走 `CloseAnimated()`：滑出屏幕外之后才真正 `Close()`；`Close()` 被调用时会先停掉入场计时器。
- 实测：起点 x=2576（屏幕宽 2560，完全在屏幕外）→ 2437 → 2278 → 目标 2164；
  窗口坐标采样有多个中间位置，确认不是瞬移。小窗**不可拖动**（已去掉 `SetTitleBar`）。

## 验收轮补丁：主页顺序 / Y 轴 / 进度小窗 / 插件调研（2026-10-07 01:50）

- **主页卡片顺序**：用量与余额置顶 → 公告 → 版本+状态 → 快捷入口。
  做法：把 `HomePage` 里那两块 `Border` 在 XAML 里整段搬位置（脚本搬，逐行核对），没有改字段与事件。
- **Y 轴不再用科学计数法**：`TokenUsageService.FormatAxisValue` 之前对 `scaled>=1000 || scaled<1`
  会打印 `"0E+0"`，于是出现「8E+3万」。改成按量级给整数 / 一位 / 两位小数，极小值退到四位小数、
  再小就输出 `<0.0001`；单位链补到 兆(1e12)/京(1e16)/垓(1e20)。回归 `BalanceUsage` 从 32 项加到 34 项全绿。
- **右下角更新进度小窗的描边（两层）**：
  1. 外层 2px 是 **DWM 边框** → `DWMWA_BORDER_COLOR` 染成卡片底色（`StyleWindowFrame`），
     主题/材质变化时跟着换（挂在 `LauncherAppearance` 的回调里）。
  2. 内层 1px 白线来自 **`WS_OVERLAPPEDWINDOW` 框架样式**：非客户区 3px，DWM 只涂外面 2px，
     最里面 1px 露出窗口白底。WinUI 的 `SetBorderAndTitleBar(false,false)` 并不会去掉它。
     解法：`MakeBorderlessPopup()` —— 把窗口样式换成 `WS_POPUP` 再 `SetWindowPos(SWP_FRAMECHANGED)`，
     非客户区整块消失（实测客户区偏移 3px → 0px，白线像素变成底色）。
  3. 小窗**不允许拖动**：去掉 `SetTitleBar(root)`（保留 `ExtendsContentIntoTitleBar`）。
     真鼠标拖拽测试：拖前后窗口坐标一致。
- 预览模式仍是单实例锁；`--settings-preview=UpdateWindow` 会同时拉起设置窗口（落在 Updates 页）。

### 插件安装：官方实现调研结论（子代理只读调研，未改代码）

来源：桌面版 `G:\DSH_Official`（DSH 0.2.0-rc.2）+ `G:\DeepSeek DSH\node_modules\@deepseek-ai\*`（0.2.1-alpha.1）。

- **清单**是 profile 的 `package.json`：`dependencies` + `dsh.profile.bundles[]`；装完由官方
  plugin-manager 自动把带 `dsh.bundle` 的新依赖追加进 bundles（`dsh-plugin-manager/lib/types/operations.js:44-72`）。
- **官方入口** `dsh plugin --profile <名> <pnpm 参数…>`：直接转发 pnpm，支持
  `github:owner/repo#rev`、tarball、绝对路径、`link:`/`file:`。子目录仓库没有 git 语义，仍需自研下载 + `link:`。
- **不需要重启**：base bundle 默认启用 `@deepseek-ai/dsh-hmr`（chokidar 盯 profile/package.json、
  profile/cordis.patch.yml、`$DSH_HOME/cordis.patch.yml`），bundles 一变就热重算并挂载新插件。
  **只有覆盖已加载的同名包才必须重启**（`replacing "X" requires a process restart`）。
- **没有** reload 命令 / HTTP 重载接口；桌面 IPC 只有 shutdown/quit-inspection/update-tasks；`/api` 需要
  启动随机 token 换的 cookie，外部进程调不通。
- **桌面 profile 有保护**：直接 `node <dshRoot>\node_modules\@deepseek-ai\dsh\lib\bin.js plugin --profile desktop`
  会被 `rejectElectronProfile` 拒；必须走桌面 shim `G:\DSH_Official\resources\runtime\cli\bin\dsh.cmd`
  （`ELECTRON_RUN_AS_NODE=1` + 桌面自带 pnpm 11.7.0）。
- 现有 `DshPluginCliService.cs` 有两处坑：用的是被拒的 bin.js 路径；`DSH_HOME` 被写死成 `<dshRoot>\.dsh`，
  而桌面 app 真实 home 是 `C:\Users\...\.dsh`。
- **改造计划（待做）**：插件页加「粘贴 GitHub 链接」入口 → 翻成 `github:owner/repo#rev` → 调桌面 shim 的
  `plugin add`；本地目录走绝对路径；装卸完不再要求重启（提示"已热加载"），只有同名覆盖才提示重启；
  子目录仓库保留自研下载 + `link:`。风险：两把锁（CLI 的 package.json 锁 vs Electron 的 `<profile>\lock`）、
  pnpm 11 的 `allowBuilds`、Windows 绝对路径。

## 验收轮：搜索重构 + 分组改名 + 关于页/下载入口/更新小窗（2026-10-07 01:40，优先于下方历史交接）

用户一口气提了 8 条，全部实现并用真实窗口验证过。**未提交、未推送、未发布、未改 manifest。**

### 1) 搜索（借鉴 deskbox 的样子）

- **位置与形状**：搜索框从导航栏搬到**标题栏正中**，椭圆（Border 圆角 17 + 透明底的 AutoSuggestBox）。
  代码：`SettingsWindow.xaml` 的 `AppTitleBar`（三列：品牌 / 搜索 / 让开系统按钮的 140px 占位）。
- **必须让非客户区里的输入框收得到鼠标**：`UpdateTitleBarSearchRegion()` 用
  `InputNonClientPointerSource.GetForWindowId(...).SetRegionRects(NonClientRegionKind.Passthrough, ...)`
  把搜索框矩形登记成通透区（物理像素，`SettingsRoot.SizeChanged` 时重算）。没有这一步，点搜索框会被当成拖标题栏。
- **聚焦不再有系统方框**：`UseSystemFocusVisuals="False"` + 覆盖
  `TextControlBorderThemeThicknessFocused` / `TextControlBorderBrushFocused` /
  `TextControl{Background,BorderBrush}PointerOver`；聚焦提示改成胶囊描边加粗一档 + 主题色。
  主题色走 `ResolveAccentBrush()`：先看 `SettingsRoot.Resources["AccentFillColorDefaultBrush"]`
  （只有「自定义强调色」时 ApplyAccent 才会写这个键），没有再问 `Application.Current.Resources`，最后兜底颜色选择器。
  **坑**：AutoSuggestBox 会把焦点转给模板里的 TextBox，普通 `GotFocus +=` 收不到，
  所以 `HookSearchFocus()` 用 `FindDescendant<TextBox>` 直接把事件挂在真正吃焦点的控件上。
- **下拉面板**：不再用 AutoSuggestBox 自带候选（`ItemsSource` 不再设），改成标题栏下面垂一个
  `SettingsSearchPanel`（Grid.Row=1，居中，520 宽，`SolidBackgroundFillColorSecondaryBrush` 不透明底——
  用卡片色刷会半透明、透出后面的内容）。列表是 `SettingsSearchResults`（复用 `SettingsSearchSuggestionTemplate`）。
  交互：输入实时刷新（180ms 防抖）、Up/Down 选、Enter 打开、Esc 收起、点空白处收起、点候选跳页并收起。
- **命中数在面板底部**：`SettingsSearchHint` 文案是「已找到 n 个结果 / 没有匹配项」；
  **有多少条显示多少条**（`Search(..., limit: 0)`，不再截断 8 条）；
  没有结果时列表和分隔线一起收掉，只留一句「没有匹配项」。
- **拼音首字母搜索**：新增 `source/SettingsSearchInitials.cs`（832 个设置界面里出现过的汉字，
  按 GB2312 码位离线推出来的声母，5KB 静态表）。`SettingsSearchEntry.Initials` 存
  `标题 + 页名 + 别名` 的首字母串，`SettingsSearchMatcher.Score` 里纯英文字母的词会命中 `Initials`（权重 2）。
  例：`cg` → 常规页、`jkdz` → 接口地址、`xz` → 下载任务、`fymx` → 翻译模型。
  **坑**：`title` 是 TextBlock，一开始写成 `title + pageTitle` 拼进去的是控件对象（编译不报错、匹配全废），
  必须用 `title.Text`。

### 2) 分组与页面标题

- 一级入口改名：**主页 / 通用 / 拓展 / 核心 / 更新 / 关于**（原 基础设置/功能管理/系统管理）。
- 通用组内顺序：**常规 / 提醒 / 外观 / API 与翻译**（提醒提到外观前面）。
- **更新单拎成一级入口**（`UpdatesNavItem`，Tag=Updates，SVG 用 `updates.svg`），不再挂在系统管理里。
- **有子页签的页面不再重复写自己的大标题**：删掉 General/Theme/Api/Alerts/Plugins/Skills/Service/Components
  八个页面里的 `SettingsPageTitleTextStyle` 那块。主页、更新、关于、下载、开发者是独立页，保留标题。

### 3) 关于页

- 删掉「环境健康检查」卡片（连同 `_healthReport`、`HealthCheck_Click`、`HealthExport_Click`；
  `LauncherHealthReport` 类保留，回归测试还在用）。「日志与诊断」卡片保留。
- **更新日志挪到版本卡上面**。
- **启动器版本 + 安装器版本合并成一张卡**：右边两行右对齐（启动器 / 安装器 + 版本值），
  不再单开一栏；`RefreshInstallerVersion()` 同时写 Updates 页那句说明和
  `AboutInstallerVersionText`（只放版本号，读不到就是「未读到」），About 页也会调它。

### 4) 下载入口默认隐藏

- XAML 里 `DownloadsNavItem` 默认 `Visibility="Collapsed"`。
- `RefreshDownloadCenterCore()`：只有在**有活动下载**（`active > 0`，即下载中/暂停/待重试），
  或者人已经在下载页时才显示；纯历史记录不再让入口常驻。
- `SelectPage("Downloads")` 里先把它设成可见再选中（否则选一个折叠项选不上）。
- **下载页本身要能搜到**：它不是标准行布局、索引采不到条目，所以 `BuildSettingsSearchIndex()`
  里补了一条页面级条目（Title=下载任务，PageTitle=下载，Anchor=DownloadsPage）——
  这就是「搜 xz / 下载 → 点进去 → 入口出现」这条链路。

### 5) 启动时右下角进度小窗的描边

- `UpdateProgressWindow`：无边框窗口默认还会被 DWM 套一圈方角细描边。新增 `StyleWindowFrame()`，
  在 `Show()` 里 `DwmSetWindowAttribute(DWMWA_BORDER_COLOR = COLOR_NONE)` +
  `DWMWA_WINDOW_CORNER_PREFERENCE = DWMWA_ROUND`；内容再套一层同色 Grid 底，避免圆角处露白。

### 6) 更新时的重复弹窗

- 删掉 `InstallUpdate()` 开头那条「正在更新到 vX…」托盘气泡（右下角已经有进度小窗）。
- 更新完成的通知本来就有：重启后的实例读 `--updated=` 推 `_pendingUpdateNotification`，保持不动。

### 验证与交付

- Release/x64 构建 0 错误、40 条既有 SYSLIB0014 警告；`SettingsSearch.Regression` 从 36 项加到 **43 项**全绿。
- 真实窗口验证（仓库外脚本 `../verify-search-click.ps1` 真鼠标点击 + 真键盘/ UIA 输入）：
  搜索框可点可输入；api / jkdz / xz 出对应候选；面板在搜索框下方；底部命中数与候选数一致；
  Esc 收起；点候选跳页并收起。`../verify-focus-ring.ps1` 截图对比：聚焦后胶囊四周 3000+ 主题色像素、
  内部没有整行横线（方框/下划线已消）。默认导航 6 项（下载任务隐藏）、搜 xz 才出现。
- 验收包：`preview-artifacts/Dafeiyu-Go-1.6.0-settings-UI-review-r3.zip`（12.2 MB / 100 项），
  含 `screenshots/`（General/About/Plugins/Skills/Updates/Api + 搜索面板.png + 搜索框聚焦.png）、
  `1-预览设置界面.cmd`、`README.txt`。**包内二进制最后一次刷新时间见文件时间戳。**
- 预览模式是**单实例**（互斥锁 `Local\DeepSeekHarness.Launcher.Preview`），所以
  `--settings-preview=UpdateWindow` 现在会**同时**拉起设置窗口（落在 Updates 页），
  一次就能看全设置 + 右下角进度小窗。
- **没验的**：深色/窄窗/高 DPI 人工观感、真跑一次自更新看小窗与通知、开发者解锁手势。
- 辅助脚本都在仓库外：`../capture-ui.ps1`、`../capture-package.ps1`、`../verify-search-click.ps1`、
  `../verify-focus-ring.ps1`。

## 本轮完成：回退二级菜单 + 用户后续两条修正（2026-10-07 00:20，优先于下方历史交接）

> 用户在本轮后来又提了两条，都已实现并用真实窗口验证：
> 1. **开发者必须留在左下角隐藏入口**，不能写进常规设置页 → 它不再是功能管理里的页签，回到 Footer 独立页面（可见性由 SelectPage 单独控制，搜索分组也不再挂「功能管理」）。
> 2. **更新要单拎出来** → 从系统管理移出，成为一级入口，页面单独显示、不参与任何组内页签。
>
> 现在的一级入口是 6 个：主页 / 基础设置（常规·外观·API 与翻译·提醒）/ 功能管理（插件·技能）/ 系统管理（服务·组件）/ 更新 / 关于；底部 下载任务、开发者（隐藏）。
> 如果用户还想继续调整分类，改动点只有 `GroupOf()`、`BuildSettingsGroups()` 的 AddGroupTab 列表、`SelectPage()` 的 standalone 分支和 XAML 里的 NavigationViewItem。

- 已执行下节要求：恢复「一级入口 + 组内页内 Pivot」，不再动态添加左侧子项、不再展开分类。保留导航宽度 224、去除分类重复大标题、原页面实例与搜索索引、主页默认行为及其他 1.6.0 未提交改动。
- `source/SettingsWindow.xaml`：BasicTabs / FeaturesTabs / SystemTabs 恢复为 Pivot，绑定 SettingsSection_SelectionChanged；未恢复 TabView 外观或额外页面上边距。新增一级入口 `UpdatesNavItem`（Tag=Updates，字形 E895，SVG 走 `updates.svg`）。
- `source/SettingsWindow.xaml.cs`：恢复 `_groupTabs`、AddGroupTab、SelectGroupTab(Pivot) 与 `_suppressSectionSelection`；页内点击仍走 SelectPage 执行原有加载/刷新，分类恢复上次子页，左侧选中一级入口。`GroupOf()` 不再把 Developer/Updates 归到任何组；SelectPage 把 Developer、Updates 当独立页面（只切 Visibility），DeveloperNavItem 只在 Footer，五连点解锁照旧。
- Release/x64/win-x64 构建通过：0 错误、40 条既有 SYSLIB0014 弃用警告；SettingsSearch.Regression 离线 36 项通过（索引分组改为不再把开发者/更新挂到分类下，跑过确认没破）。
- 真实窗口 UI Automation（构建产物 5 页 + 验收包 6 页各跑一遍）：一级入口正好 7 项且顺序为 主页/基础设置/功能管理/系统管理/更新/关于/下载任务；基础设置组内页签 常规/外观/API 与翻译/提醒、系统管理 服务/组件、功能管理 插件/技能；更新页不再出现组内页签；普通预览无「开发者」，解锁/Developer 预览里 Footer 有「开发者」且它不是任何组内页签。页面自己的内层页签（如技能页的「本地技能」、开发者页的「插件推荐」）不参与判断。切页（API→常规、插件→技能、更新→服务）与搜索「监听」→ 常规页「启动端口」通过。未实测五连点解锁、深色/窄窗/高 DPI 完整视觉验收及所有功能交互。
- 仓库外 `../capture-ui.ps1` 的 Select 改为 Pivot TabItem，新增 SubmitSearch 与 -Exe；证据在 `../preview-artifacts/rescue-160/`。截图未做视觉验收，不代表外观全部通过。
- 本轮只修改两份设置窗口源码与本交接，保留既有其他文件改动；辅助预览脚本在仓库外。`git diff --check` 发现原有技能集代码一处尾随空格（SettingsWindow.xaml.cs 的 failures.Add 行），未做无关清理。
- 未提交、未推送、未发布。旧 `preview-artifacts/Dafeiyu-Go-1.6.0-settings-UI-review.zip` 仍包含已撤销的二级菜单，不能当作本轮结果。

### 本轮收尾：重新打包与全量回归（2026-10-07 00:00 前后）

- 用当前源码重新走了一遍正式打包流程：`.\release.ps1 -NoManifest`（publish + csc 引导 + Compress-Archive），产物 `source/dist-1.6.0`（90 文件）与仓库根 `DeepSeekHarness-1.6.0.zip`；两个 exe 的 FileVersion 均为 1.6.0.0。**没有动 manifest.json、没有打 tag、没有发 npm。**
- 最终验收包（末尾那次打包，已含上面两条修正）：`preview-artifacts/Dafeiyu-Go-1.6.0-settings-UI-review-r2.zip`（12.3 MB / 97 项），
  SHA-256 `d3e84ad9cc5b75b196a6b6b6e57ccac21ea15b4e5501e8ae88395e36c9f53488`。
  内容 = dist-1.6.0 + `screenshots/`（General/Plugins/Skills/Updates/Api，均为本包实际运行截图） + `1-预览设置界面.cmd` + `README.txt`，同名解包目录也在 `preview-artifacts/` 下。（更早那版 hash `2f33de06…` 已被此次覆盖，作废。）
- **验收包本体验证**：从包内引导 exe 启动（新增仓库外脚本 `../capture-package.ps1`，只跟踪自己拉起的 bootstrap 及其子进程），UIA 读到一级入口 7 项、顺序正确，普通预览看不到「开发者」，Developer 预览里能看到，更新页无组内页签。截图未人工目检。
- **全量离线回归 10 套**：BalanceUsage 32、ProxyScope 32、DownloadTasks 34、UpdateIntegrity 49、PluginOfficial 29、SkillSets 57、TranslationCore 42、SettingsSearch 36、TranslationStore 63 全绿；
  **LauncherSafety 未过**：在最后一条 `linked target unchanged` 断言处抛异常。原因已定位为**测试夹具陈旧、不是产品缺陷**：前面的 PAX 合法解压（Program.cs 第 98/111 行）会整体替换目标目录，`sentinel` 那时就已被删除；该断言只在“成功创建符号链接”的分支里执行，本机现在允许创建符号链接（以前因权限不足走 SKIP），于是暴露出来。产品侧行为正确：`SafeArchiveExtractor` 在解压前 `RejectReparseAncestors` 直接拒绝链接目标，返回错误且不动原目录（第 118 行断言通过）。修法是测试里在第 113 行的链接探针前重建 sentinel；本轮按“只做导航改动”的范围约束**没有改测试**，留给用户/下一轮决定。
- 辅助脚本改动都在仓库外：`../capture-ui.ps1`（Select 改 TabItem、新增 SubmitSearch 与 -Exe）、新增 `../capture-package.ps1`。本轮只改了两份设置窗口源码、HANDOVER，以及仓库 `preview-artifacts/`（已 gitignore）里的验收包；`git diff --check` 之前发现的那处既有尾随空格未动。

## 最新用户验收反馈与下一轮任务（2026-10-06 23:36，已由上节执行完成）

用户验收设置界面 ZIP 后明确要求：**「菜单二级界面这个改动回退吧，其他没什么大问题，二级菜单怎么改都很怪」**。随后要求仅把任务写入交接文档，让下一轮执行。本轮因此没有执行源码回退，也没有重新构建或打包；只更新本节。

### 下一轮应做

- 回退本轮新增的**左侧可展开二级菜单**，恢复实施该改动之前的导航。不要重新设计二级菜单，不要继续尝试另一种层级导航。
- 根据本轮明确的回退承诺，优先恢复「左侧五个一级入口 + 组内页内 Pivot 导航」的上一版。用户的原话也可能表示希望取消全部二级界面；如果下一轮发现此含义影响实施范围，应先确认，不要擅自恢复原作者所有 TabView 视觉问题。
- 保留其他已获认可的布局优化：导航栏宽度 224、删除基础设置/功能管理/系统管理内容区重复大标题，以及原有页面、搜索、功能逻辑。默认页面行为不要变。
- 项目有大量原有 1.6.0 未提交改动，**禁止用 git reset/checkout 整文件来回退**。仅针对本轮层级导航修改做精确还原；不提交、不推送、不发包，除非用户另行要求。

### 当前源码状态与修改定位

仓库：`G:\DeepSeek DSH\DSH Works\Project\Dafeiyu-Go\Dafeiyu-Go-DeepSeek-Harness-Click-To-Run`。

- `source/SettingsWindow.xaml`：`BasicTabs`、`FeaturesTabs`、`SystemTabs` 当前是 `ContentControl`；此前为 `Pivot`，绑定 `SelectionChanged="SettingsSection_SelectionChanged"`。这三个内容宿主位于 BasicPage/FeaturesPage/SystemPage 中，重复分类标题已移除。
- `source/SettingsWindow.xaml.cs`：`BuildSettingsGroups()` 当前调用 `AddSettingsSection()`，动态把子项加入 BasicNavItem/FeaturesNavItem/SystemNavItem.MenuItems。`DeveloperNavItem` 从 Footer 搬到 FeaturesNavItem.MenuItems。
- 当前 `_sectionPages` 保存页面实例，`_sectionNavItems` 保存二级导航项；页面从 SettingsContentHost 摘下后，由 `SelectGroupTab(ContentControl, tag)` 设置 Content 显示。
- 当前 `SelectPage()` 将分类入口解析为 `_lastGroupTab`，选中具体子项并执行 `parent.IsExpanded = true`。回退时恢复选中一级分类，并由组内 Pivot 选择子页。
- 上一版 Pivot 实现使用 `_groupTabs: Dictionary<string, PivotItem>`，AddGroupTab(Pivot, tag, header, page) 摘下页面后放入 PivotItem.Content；PivotItem.Margin 为 0。开发者子页默认隐藏，保留原有解锁规则。
- 上一版有 `SettingsSection_SelectionChanged` 调用 SelectPage(tag)，以及 `_suppressSectionSelection` 防重入；程序调用 SelectGroupTab 时暂时抑制该事件，避免重复加载。不要遗漏子页点击时的加载/刷新逻辑。
- 上一版最终已移除额外的 `page.Margin = new Thickness(0, 20, 0, 0)`，无需恢复这层多余间距。
- 搜索索引直接从各原页面实例采集，未挂在可视树中的页面仍须可搜索；搜索结果调用 RevealSearchTarget → SelectPage。

### 已做验证与交付（仅代表当前二级菜单版本）

- 最后一次 Release/x64 构建：0 错误、40 条弃用警告；使用仓库 SDK：
  `& 'G:\DeepSeek DSH\.tools\dotnet\dotnet.exe' build source\DeepSeekHarness.csproj -c Release -p:Platform=x64 -r win-x64 --no-restore -m:1 -nodeReuse:false`
- 已通过真实窗口 UI Automation 检查：API → 常规、插件 → 技能、更新 → 服务；搜索「监听」命中「启动端口」。搜索结果提交跳转、所有功能页完整交互、深色模式和窄窗外观尚未全面验收，不能声称全部通过。
- 用户验收包：`preview-artifacts/Dafeiyu-Go-1.6.0-settings-UI-review.zip`，该包包含**被用户要求回退的二级菜单**，不能作为回退后的最终包。
- 辅助脚本位于仓库外的 `../capture-ui.ps1`，支持 Page/Select/Search 参数；Select 当前按 NavigationView 的 ListItem 查找，恢复 Pivot 后需要调整为 TabItem 才能测试页内选项。
- 截图与 UIA 输出在仓库外 `../preview-artifacts/rescue-160/`；预览配置通过 `DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY` 指向独立目录。
- 下一轮完成精确回退后，重新构建、验证页面切换/搜索定位/开发者显隐，再按用户需要重新打包。不要把当前 ZIP 当成回退结果交付。


## 1.6.0 本轮交接（2026-10-06，未推包）

按 `DS41-实施任务.md` 完成的一轮改造。**未提交、未推送、未打标签、未发 npm**；公开
`manifest.json` 仍是 1.5.4，没有动。

### 改了什么

- **设置重新分组**：一级入口 13 → 5（主页 / 基础设置 / 功能管理 / 系统管理 / 关于），
  下载任务与开发者留 Footer。页面在 XAML 里原地不动，`BuildSettingsGroups()` 在构造期
  把它们搬进组内 TabView；`SelectPage` 用 `GroupOf()` 做旧标签 → 新入口映射，
  托盘 / 主页快捷入口 / `ShowSettings("Api")` 等调用点一个都没改。默认主页行为未动。
- **设置搜索**：导航顶部 `AutoSuggestBox`；索引在构造期**一次性**沿 XAML 对象树采集
  （走对象树而非视觉树，否则未选中的 Tab 采不到），标题/描述直接取真实控件文案。
  匹配是纯函数（`source/SettingsSearch.cs`）：大小写不敏感、全角空格当分隔、多词 AND、
  标题(3)>别名(2)>描述(1)。点击结果会切页 + 展开 Expander + 滚动 + 高亮 + 交焦点。
  不联网、不调模型、不写设置。
- **技能集**：`source/SkillSets.cs` 负责来源标识（类型+规范化仓库+分支）、成员标识
  （来源标识+仓库内路径）、归组、`MarkInstalled`（认不出的记录原样返回）、
  `SkillSetDiff`、卸载路径安全校验。市场列表改成一仓库一卡（Tag1 = `已安装 / 总数`，
  有任何成员装过按钮变「修改」），点进去是多选对话框，反选卸载列名字二次确认。
  `SkillInstallService.InstallManyFromRepository` 同仓库只下一次归档。
- **官方插件入口**：`source/DshPluginCliService.cs` 调
  `node <dshRoot>\node_modules\@deepseek-ai\dsh\lib\bin.js plugin --profile <名> <pnpm 参数>`
  （设 `DSH_HOME`），识别旧版 DSH 与 `restart-required`。`PluginStoreService` 装/卸
  优先走它，装完核对 profile 依赖与 bundles，表达不了的来源（monorepo 子目录）回退旧流程。
- **PAX 解压**：`SafeArchiveExtractor` 按类型跳过 `GlobalExtendedAttributes`。
  实测 .NET 8 的 `TarReader` 会把 `pax_global_header`（typeflag `g`）返回给调用方，
  而 `x`/`L`/`K` 它自己吞掉；旧代码只放行目录/普通文件，于是合法 PAX 归档被拒。
- **中文翻译**：`source/TranslationCore.cs`（缓存键 / Markdown 分块 / 结构校验）、
  `source/TranslationStore.cs`（缓存落盘，写 `.tmp` 再原子替换，只有校验通过才保存）、
  `source/TranslationService.cs`（提示词、分块并发 2、按序重组、重试、返回解析）。
  缓存在 `%LOCALAPPDATA%\DeepSeekHarness\translations`。设置页「基础设置 · API」
  新增模型/接口地址；技能与插件详情页新增翻译按钮与原文/中文切换。
- **安装器独立更新**：SemVer 比较抽到 `source/ProductVersion.cs`；新增
  `source/InstallerVersionPolicy.cs`（UpToDate/Update/Repair/UnknownLocal）与
  `source/InstallerVersionReader.cs`（状态记录 → 安装清单 → 注册表 → 文件版本）。
  `InstallerUpdateService.FetchLatestPackage(requiredVersion)` →
  `FetchLatestRelease(settings, out)`，缓存不再按启动器版本过滤。
  `PrepareInstallerCompanion` 改为**返回警告而不阻断**启动器更新；设置页「更新」新增
  「修复安装器」。
- **更新按钮修复**：卡片属性改了通知（`PluginCards.cs` / `SkillsModels.cs`），XAML 绑定补
  `Mode=OneWay`；单卡检查改 `forceRefresh: true`；失败恢复按钮 + 中文原因 + 「重试」；
  无来源记录的手动项明确提示不可自动更新。

### 验证

- 离线回归 **10 套 446 项**全绿：BalanceUsage 32 / ProxyScope 32 / DownloadTasks 34 /
  LauncherSafety 72 / UpdateIntegrity 49 / PluginOfficial 29 / SkillSets 57 /
  TranslationCore 42 / SettingsSearch 36 / TranslationStore 63。
  新增测试工程：`PluginOfficial.Regression`、`SkillSets.Regression`、
  `TranslationCore.Regression`、`TranslationStore.Regression`、`SettingsSearch.Regression`。
- `Release/x64` 构建 0 错误、40 条既有弃用警告（`SYSLIB0014`）。
- 本机 16 GB 内存且用户开着 Minecraft 时，WinUI XAML 编译器会 OOM；
  构建需 `-m:1 -nodeReuse:false`。
- **未测**：真实模型翻译的联网调用（需要 Key 与网络，不拿用户账户做测试）、
  设置窗口/对话框真机点击、深浅色与高 DPI 视觉核对、UAC、完整安装卸载 e2e。

### 未做 / 已知限制

- 翻译只支持已安装到本地的技能与插件（市场里没装的条目没有本地正文）。
- 技能集只做了多选安装/卸载与详情成员列表，没有「按技能集批量更新」的独立按钮
  （单项更新与整体更新仍走原有入口）。
- 安装器仓库本轮只做了独立更新解耦所需的改动（删除误导性的硬编码 `LauncherVersion`），
  没有新增安装/卸载流程功能。

## 1.5.4 已发布交接（2026-10-06）

- 本轮产品内容为余额 / Token 图表、下载中心、启动器与 DSH 服务独立代理生效开关；安装器同步版本，不新增安装 / 卸载流程功能。
- 独立审查已修复：余额跨账户/币种误记与日界线不一致、DSH SOCKS 静默直连与 home .env 恢复代理、更新共享暂存互删、同版本 DSH 下载覆盖、分片初始化异常留下孤儿下载线程。Y 轴改为短单位刻度，完整数值保留 tooltip。
- 最终集成回归：BalanceUsage 32、ProxyScope 32、DownloadTasks 34、LauncherSafety 61、UpdateIntegrity 11 项全部通过；真实 DSH 代理层 3 个本地请求通过。Release/x64 构建通过，0 错误、40 条既有弃用警告。安装器计划回归 30 项与本地打包通过。
- 隔离 WinUI 预览真实点击两个代理范围开关，确认文件保存与下次启动重载独立状态；下载页面入口及空任务页 UIA 可见。图表短刻度可由 UIA 读取。当前模型无法查看截图，未完成图表视觉/DPI 全面验收、真实 HTTP 下载按钮 GUI 操作、UAC 或完整安装卸载 e2e。符号链接用例因权限跳过。测试不触真实账户/用户配置，不重启用户 DSH。
- 旧余额账本保留但不认领给新账户；实际消费是观测差额，估算花费按本机价格表计算。当前进程活跃下载可重试，重启后历史中断记录不可重放安装；HTTP 下载任务不覆盖所有 pnpm 联网阶段。正式发布与镜像已核验，见下方证据。
- 本地打包使用 `release.ps1 -NoManifest`。正式发布按 `RELEASE.md` 走两仓新 tag CI，先安装器后启动器；仅在 CI 资产及 npm / npmmirror 包核验完成后更新 manifest，并使用实际正式资产 SHA-256。
- 以下 1.5.3 及更早内容为历史记录。

- 正式发布：安装器 CI `37423542360`（commit `88c7e8f`）成功，SHA-256 `f5baaca934311a13056ff62dd3c7356ff686687b5eb18654ed7e1adefac7625f`；启动器 CI `37423773769`（commit `dd5c448`）成功，SHA-256 `2a0e93e8c078c6f648582b29048e2a07745a4a8038cbe825b39f9c8d68843fb8`。两包未签名，正式 ZIP 中引导与 Core 的 FileVersion 均为 1.5.4.0，签名状态 NotSigned。
- npm 与 npmmirror 实际 tgz 完全一致，SHA-256 `5b9fc39432d9efd3bd662da2b91350a71ee22968666aa75d76b8ec232913aa92`；包内 ZIP 与正式启动器资产一致。两个 GitHub Release 标题与产品更新正文已 PATCH 并 GET 读回核验。
- 清单已提交 `d8b6bd6` 并推送 main；官方 raw main、jsDelivr main（purge 后）与固定 d8b6bd6 清单均读回 1.5.4，SHA-256 与正式启动器 ZIP 一致。

## v1.5.3 发布交接（2026-10-04）

- 启动器 CI `37179772669`（commit `950d354`）成功，已下载核验 SHA256 `56a21d1057a392a4b5b24c3f2d74aa34fc98d529242dc02a188a5332045c66ec`；安装器 CI `37179657388`（commit `abf3a0a`）成功，产物 SHA256 `69e770e068c997fec62956ce362250cba0cfd12d64491f96b03c1f943e7f9b88`。
- npm 1.5.3 与 npmmirror 均已就绪（HTTP 200）；下载 tgz 完全一致，SHA256 `1d0af60ab658f15ecc216b02cfc7408be30e5d6747b478fdb1d57d87f8d4a9f5`，内含 ZIP 哈希与官方一致，manifest 已更新并核验 raw/CDN 清单。
- 版本内容：只读健康预览与白名单 JSON、安装计划、tar.gz 安全解包、保留数据保护、修复范围/自定义目录、Node 目录边界与缺失/无效 SHA256 停止更新；README 历史移出首页、使用真实宣传图、移除受版本控制的过时 1.3.21 归档。
- 回归 61 + 11 + 30 项及 Release 构建通过；GUI、UAC、完整安装/卸载 e2e 未测；符号链接用例因 Windows 权限不足跳过。不保证所有安全问题均已解决或完整事务回滚。
- 本轮仅收尾发布文档，未提交或推送；仓库提交由父级流程负责。


- 中文首页改为产品标题、直接下载入口、四组功能、三步开始、运行要求与故障处理；CLI、构建与英文快览折叠，避免中英全文重复。
- 首屏插图使用 `docs/images/readme-overview.svg`，明确标为「功能示意图（非界面截图）」。本轮发现 `source/ui-review/*.png` 与 `preview-artifacts/*.png`，但模型不支持图片查看，无法确认是否包含密钥、token、账户或本机信息，因此未使用这些截图。
- 依据 `manifest.json`、`DeepSeekHarness.csproj`、`LauncherLocator.cs`、`WinUIProgram.cs`、引导清单与余额存储代码核对功能；修正旧 README 的 Run 键自启说法为最高权限计划任务，补充 x64、管理员继承、估算花费与 DPAPI 边界。
- 文档规则：信息前置、一处说一次；首屏服务于下载与开始使用；开发细节收进折叠或专门文档。截图必须真实且先脱敏，示意图不能称为界面截图；版本、平台、权限、隐私和测试结论须有源码、清单或实际验证支持。
- 验证范围：文档链接目标与差异检查；未运行应用、编译、GUI、虚拟机或历史交接中记录的测试。本轮不发布、不提交、不推送；保留已有 `source/InstallerUpdateService.cs` 改动，不纳入文档工作。

## 代码审查与文案清理（2026-10-04）

- 已确认高风险：`source/SkillInstallService.cs:303-331` 与 `source/PluginStoreService.cs:969-1009` 解压 tar.gz 时直接把归档路径拼进目标目录，未做规范化后的目录边界校验；恶意 `../` 条目可写到技能/插件目录外。应在解压前拒绝越界、绝对路径和链接类条目，并为两条链补回归测试。
- 另见父级审查：`InstallerUpdateService.cs` 的无哈希兜底会允许缓存包跳过完整性校验；不要把该用户改动与本轮文案改动混在一起。卸载/保留数据路径另需核对是否存在“保留”选项仍删除用户数据的情形。
- 本轮仅精简 UI 文案，保留原有 key、占位符和行为：开机自启、静默启动、加速源提示、备份导出/导入、自动更新周期、Python 提示。未修改 `InstallerUpdateService.cs`。
- 验证：使用仓库工具目录的 .NET SDK 8.0.425 执行 Release/x64/win-x64 `--no-restore` 构建通过（0 错误、50 条 WebRequest/WebClient 弃用警告）；`git diff --check` 通过。未启动应用、安装器或执行破坏性测试。建议下一轮先做 tar traversal、卸载保留数据和更新包 hash 的离线测试，再跑 GUI 与本地化扫描。

## 第一批安全与诊断交接（2026-10-04）

- `source/SafeArchiveExtractor.cs` 统一 tar.gz 落地：先在唯一 GUID 暂存目录完整校验，再目录交换并失败恢复旧目录；拒绝绝对路径、`..`、Windows 保留名/非法字符、ADS、链接/特殊条目、reparse 目标与目录前缀逃逸。保留 GitHub 顶层目录剥离。
- `SkillInstallService` 和 `PluginStoreService` 都改走共享实现；zip 入口未改。`tests/LauncherSafety.Regression` 生成隔离 tar fixtures，验证合法 root stripping、路径变体、链接类型、目标保留、allowlist 诊断 JSON，共 61 项通过；Windows 无创建符号链接权限时明确跳过该一项。
- `source/LauncherHealthReport.cs` + 关于页「日志与诊断」区域的刷新/预览/SavePicker 导出：仅版本、架构、DSH/Node 可用性、被选端口监听状态与行动建议；不读出配置、路径、token、备份或原始日志。版本文本只输出数字版本前缀，不能把包元数据当作安全脱敏。检查只读，不启动、安装、杀进程或自动修复。
- 使用 `G:\DeepSeek DSH\.tools\dotnet\dotnet.exe` 执行 Release/x64/win-x64 `--no-restore` 构建通过，0 错误、50 条既有 WebRequest/WebClient 弃用警告；未启动应用。父级另有 InstallerUpdateService SHA256 fail-closed 与回归测试，本轮未触碰该文件。

---

## 1.5.2 已发布：Token 用量与余额重做（2026-09-29）

### 做了什么

| 项 | 说明 |
|----|------|
| 主页「Token 用量与余额」重做 | 柱状图 = 每天 token 用量，折线 = 当天花费，**共用一条 x 轴**；带真正的坐标系（左轴 tokens、右轴 ¥、底部日期、网格线）、近 7/15/30 天切换、悬停高亮 + 竖/横参考线 + 信息牌 |
| 花费估算 | 照官方口径算：高峰 = 工作日 9:00–12:00、14:00–18:00，其余（含周末与法定节假日全天）按空闲价（半价）；内置 2026 年节假日；价格表在 `%LOCALAPPDATA%\DeepSeekHarness\model-prices.json`，调价改文件即可 |
| 余额实扣对账 | 新增 `BalanceLedger`：启动器每次查余额记一笔日账本（跌 = 消费、涨 = 充值），卡片上跟估算曲线对照 |
| 自带的用量统计插件 | 新增 `source/assets/dsh-token-stats`（零依赖、不声明 peerDependencies）+ `TokenStatsPlugin`：一键安装 = 复制到 `<dshRoot>\plugins\` 并写 profile，顺手摘掉不兼容的社区插件条目 |
| 卡片瘦身 | 只留「余额 + 今日」两行，其余进悬停提示 |

### 发布状态

- 启动器 `v1.5.2`（commit `796e9e6`，清单提交 `7b1144e`）：Release 资产 `DeepSeekHarness-1.5.2.zip`（sha256 `d48956c5…`）、npm 与 npmmirror 均已同步、`manifest.json` 已更新。
- 安装器仍是 `1.5.1`（这一版只动启动器，两仓版本号故意不同步）。

### 本轮踩的坑（都写进代码注释了）

- **"没有动画"其实是两条路都断了**：先试 Composition 的 `SpringVector3NaturalMotionAnimation` + `Offset`（Offset 会被布局在下次 Arrange 写回，动画被拍平）；再试 `Translation`（得先 `ElementCompositionPreview.SetIsTranslationEnabled`，而且同样怕布局打断 —— 表现是"柱子错位、鼠标一悬停就正常"）。最后换成 **Storyboard + BackEase**：RenderTransform 归 XAML 管，谁也抢不走。
- **图表里两套 x 坐标必然对不上**：柱子按等分格排、折线按首尾均分，悬停参考线就落在两根柱子中间。统一到"柱心"才对。
- **分层别靠色相**：柱子（主题色半透明面积）+ 折线（同色实线）比"一金一蓝"更像一套界面；数值也别在图上再标一遍 —— 信息牌里给就够（贴左轴压刻度、贴图内压柱子）。
- **坐标轴刻度会撞**：最底下的左轴 `0` 与第一个日期同一行；右轴最低刻度与最后一个日期同一行。左轴不标 0、右轴只标顶/中。
- **强推 tag 会让 CI 跑第二遍**：npm 报 `E409 Cannot publish over previously staged version`，job 变红、**Release 步骤被 skip**（这次幸好第一遍已经建好）。已给 npm 步骤加 `continue-on-error` —— 启动器本体在 GitHub 上，Release 出不来才是事故。
- **临时文件别进仓库**：删大段代码前留的 `.bak2` 被 `git add -A` 一起提交了，只能再补一个提交 + 强推 tag。

## 1.5.1 本轮交接：插件装不上会说人话 + 通知不再被静默（2026-09-29）

### 交接状态（下一轮从这里接着走）

到本轮为止**已经做完并且验过**的（都在 `main` 上，两仓工作区干净）：

| 项 | 状态 |
|----|------|
| 插件安装失败回滚 + pnpm 原话 + 装完自检 | 完成，反射探针验过 |
| npm 插件依赖值不再写成裸包名（`profile` 被写坏的根源） | 完成，假目录端到端验过（写出 `^1.1.1`，再跑 `pnpm install` 退出码 0） |
| 插件自检 / 一键修复按钮 | 完成，真实 profile 跑过（7 个插件 0 问题） |
| Token 用量与余额卡片 | 完成，解析器用 4 行假数据验过（今日 9150 等数值全对） |
| 更新周期「关闭」 + 通知门控修正 | 完成，三条通道的 Off 分支逐个看过 |
| 安装清单 + 完整性校验 | 完成，损坏/过期/痕迹三种情况验过 |
| 安装器「版本」页（DSH 可选，启动器禁用） | 完成，编译过；界面没点过 |
| `.dym` 内核（真 7z，两仓各一份） | 完成，两仓都端到端验过（魔数 `37 7A BC AF 27 1C`、导出→导入内容全对） |
| 安装器：装完后的导入页 | 完成，编译过；界面没点过 |
| 安装器：卸载前的导出页 | 完成，编译过；界面没点过 |
| 「修复安装」挪到「开始安装」左边 + 无痕迹禁用 | 完成，判定函数五种情况验过 |

**启动器侧的「数据备份」入口（这一轮加上）**：

| 项 | 状态 |
|----|------|
| 常规页「数据备份」卡片：导出 / 导入两个面板、进度、结果 | 完成，服务层 36 项离线端到端验过（导出 → 列包 → 覆盖/跳过/两个都留/取消，含"同一分钟连点两次不互盖"） |
| 主页 Token 卡片分清「插件没装」和「装了还没记录」 | 完成，16 项离线验过（用户报的"老是说未安装"就是这个） |
| 1.5.1 在虚拟机里起得来、界面渲染正常 | 完成，截图见 `.fix-lasso-state\vm-shots-151\`（`v2.png` = 常规页「数据备份」卡片 + 自动展开的导出面板 + 「配置与插件清单」已勾选） |
| 「数据备份」卡片上的按钮在 VM 里点一遍 | **没做到** —— VM 里 UIA 抓不到 WinUI 内容树，只截到图；点按钮留给人手 |

**还差什么**：发版收尾 —— tag 推上去等 CI 出包 / 发 npm，等 npmmirror 同步好，再拿 CI 那份 zip 的哈希更新 `manifest.json`。

### 这一轮（启动器入口）踩到并记下的坑

- **`usage.jsonl` 是"用了模型才生成"的文件**：卡片原来只按"文件在不在"判断，于是插件明明装了、只要还没聊过天，就一直劝你去装（用户看到的"老是说未安装"）。
  判断要拆成两步：插件在不在（`node_modules` 里在、或 profile 的 `package.json` 里记着）+ 有没有记录；
  装了但没记录时要把记录文件路径摆出来，而不是再给一个安装按钮。
- **VM 里 UIA 抓不到 WinUI 内容树**：计划任务（`/it /rl highest`）拉起的预览窗口，`FindAll(Descendants)` 只返回窗口外框那几个元素，
  页面控件一个都看不到 —— 同一时刻截图里界面却是好的。所以"看"界面靠截图，"点"界面得换低层输入或人工。
- **`--settings-preview=General:Backup` 是好的，但第一次截图偏早**：第一枪那张（24 秒后截的）停在主页，第二枪（同一个参数）就正常落在常规页并展开了导出面板 —— 等页面切完再截图。
  另外 VM 分辨率 1705×800，窗口最大宽度 1200 逻辑像素被 DPI 放大后右侧按钮会被裁掉一半，截图只能看到卡片左半部分。
- **本机（agent 的进程里）跑 `source\dist-*` 里的预览会以 `0x80070005` 退出**：部署目录里那份
  （`G:\DeepSeek DSH\DeepSeek Harness`，用户正在跑的那份）却能正常开预览窗口；同一份源码、同一个 exe 名，差别没查明。
  本机的界面自检目前只能借虚拟机做。
- **7z 打包进度是真进度**：`7zr` 的 `-bsp1 -bb1` 会逐文件报 `Compressing xxx`，`StreamReader` 把单独的 `\r` 也当行尾，
  所以进度条会一格一格走，界面还能把当前文件名显示出来（`BackupFlow.DescribeProgressLine`）。

### 本轮踩到并修掉的坑（值得记住）

- **`Process.Start(UseShellExecute=true)` 打开网址在有些机器上不报错也不开窗**（https 关联指向商店应用那条时），
  而且当时的代码把所有异常都吞了 —— 现象就是「点了没反应」，日志里什么都没有。
  现在统一走 `UrlLauncher`（直接调 → 退给 explorer.exe → 还是失败就把网址摆给用户 + 写日志）。
  测试机那台更极端：只注册了 IEXPLORE，等于没有能用的浏览器。
- **`CommandLine` 里的版本号要筛字符**：版本页填的值会拼进 `npm install`，所以只放行字母数字和 `. - _ ^ ~ > < = * +`。
- **`StringBuilder` 在 stdout/stderr 双线程回调里必须加锁**（`DymArchive` 里踩过，`ProcessRunner` 本来是锁的）。
- **7z 的 `l -slt` 输出里,分隔线之前是包自己的表头**（含一行 `Path = 备份包`），不跳过就会把包自己列进内容清单。
- **PowerShell 探针调 .NET 回调的坑**：`Action` 若是 PS 脚本块，被后台线程调用会抛
  「此线程中没有可用于运行脚本的运行空间」；要么传 `$null`，要么用 `Add-Type` 编出来的真委托。


代码已完成、本地构建通过（启动器与安装器都 0 错），**包已打但没发版** ——
等用户确认是直接走 CI 发版，还是先推虚拟机点一遍。

### 起因（一个真实的坑）

用户报「插件装不上」。日志里只有一句 `pnpm install 退出码 = 1`，pnpm 自己的报错被吞了。
真正的根子不在插件：profile 的 `package.json` 里有一条坏依赖
`"minecraft-dev": "minecraft-dev"`（版本号位置写成了包名），pnpm 整树解析必失败 ——
于是从 9/28 起**四个插件连着装不上**，每个都表现为「文件解压了、卡片显示已安装、
重启 DSH 报无法解析 bundle」。

复盘出的三条规矩（都已落进代码）：
1. 包管理器失败时**必须把它的原话带出来**，只记退出码等于没记；
2. 失败**必须回滚**刚写进 profile 的条目，否则坏条目会让后面每一个插件都装不上；
3. 「说装完了」之前**必须验一遍**（node_modules 里真的有这个包、link 指向的目录真的在）。

### 改了什么

| 位置 | 改动 |
|------|------|
| `source/PluginStoreService.cs` | pnpm 失败改为回滚 profile 条目（依赖 + bundles）并 `Ok=false`；新增 `DescribePnpmFailure` / `PnpmOutputTail`（pnpm 原话）、`VerifyProfileLink`（装完自检）、`ProfileCheck` + `SelfCheckInstalled`、`RepairProfile`、`InstallNpmPackage` |
| `source/SettingsWindow.xaml(.cs)` | 插件页加「插件自检」「一键修复」；主页公告下加「Token 用量与余额」卡片（没数据时给一键安装按钮） |
| `source/TokenUsageService.cs` | 新文件：读 `.dsh\storages\token-stats\usage.jsonl`（社区插件 `@zerro223/dsh-token-usage` 写的，一行一条），聚合今日 / 近 7 天 / 近 30 天、输入输出、缓存命中率、最常用模型 |
| `source/WinUIProgram.cs` | 更新失败、自动更新完成的通知不再被「更新提醒」开关静默；`UpdateInterval` 支持 `Off` |
| `source/LauncherSettingsStore.cs` | `UpdateInterval` 允许 `Off`（周期可以整个关掉） |

通知那条补充说明：自动安装模式下**检测阶段本来就不弹**（启动器和 DSH 都是这个结构），
这次补的是「装完必弹、失败必弹」。

### 验证到了哪一步

- 编译：两仓 0 错（48 条既有警告不变）。
- profile 修复：`pnpm install` 退出码 0，7 个包全挂上软链；`dsh plugin --profile web list` 已经认得 `dsh-our-free-model`。
- **没验的**：界面全是点不了的（本机没有能点的测试环境）；Token 卡片本机没数据
  （这台没装那个统计插件），只验证到编译与数据源格式。
- 虚拟机：**没开机**（ping 不通、VMware 进程没跑），包已备好但没推上去。

### 发版前要做的

1. 先确认发版口径（直接打 tag 走 CI，还是先推虚拟机点一遍）。
2. 打 tag 之前**别**让 `release.ps1` 更新 `manifest.json` —— 本次打包用的是 `-NoManifest`。
   提前写 1.5.1 会把用户引到一个还不存在的下载地址。
3. 父级 `release-all.ps1` 要求两仓版本相等，现在都是 1.5.1。

---

## 1.4.10 本轮交接：技能中心 + 开发者中心 + 主页公告（2026-09-27）

代码已完成、本地构建通过；逻辑测试 107 项离线全绿（联网那轮 66 项另算），界面自检见下面「本轮验证」。
**版本号还没升，CHANGELOG 里这一节标的是「未发布」**。发版前先确认版本号，再走父级 `set-version.ps1` + `release-all.ps1`。

### 新增文件

| 文件 | 作用 |
|------|------|
| `source/SkillsModels.cs` | `SkillEntry` / `SkillRootInfo` / `SkillInstallRecord` / `SkillCardItem`（技能卡片数据形状） |
| `source/SkillStore.cs` | 技能根解析、扫本地技能、frontmatter 解析、启停（`SKILL.md` ⇄ `SKILL.md.disabled`）、删除进回收站、`SkillInstalls.json` |
| `source/SkillMarketService.cs` | 在线目录：GitHub topic 搜索 → 递归 tree 找 `SKILL.md` → raw 拉 frontmatter；4 小时磁盘缓存；单仓库读取 |
| `source/SkillInstallService.cs` | 整仓库 tar.gz 下载 → 解压 → 只复制目标技能目录 → 写安装记录；覆盖安装带备份回滚 |
| `source/SkillUpdateService.cs` | 用 `SkillInstalls.json` 的 `pushedAt` 对比在线目录，产出可更新项 |
| `source/FeaturedSkillService.cs` | 官方推荐：远端 `featured-skills.json` → 本地缓存 → 内置兜底 |
| `featured-skills.json` | 仓库根目录，推荐页的内容源 |
| `source/FeaturedAdminService.cs` | 开发者页的仓库读写：Contents API 读文件 / 带 sha 提交 / 从仓库导入插件与技能 / 把接口错误翻成人话 |
| `source/DeveloperCenterModels.cs` | `DeveloperEntryCard`（卡片：`Kind` + 序号 + 标题 + 副标题 + 原索引） |
| `source/GitHubAccelerator.cs` | 加速前缀池的唯一出处：按「在线源」档位把 GitHub 地址展开成候选（raw / 资产 / 归档 / 检索），API 恒直连 |
| `source/AnnouncementService.cs` | 主页公告：远端 `announcements.json` → 本地缓存 → 内置兜底；解析 / 序列化 / 排序 / 已读判断 |
| `source/ChangelogService.cs` | 关于页更新日志：GitHub Release → `CHANGELOG.md` 同名小节 → 本地缓存；Markdown 小节抽取 |
| `source/AcceleratorLatency.cs` | 加速源延迟测速与落盘（`AcceleratorProbe.json`），给「自动」排序和「高级设置」面板用 |
| `announcements.json` | 仓库根目录，主页公告栏的内容源（schemaVersion 1） |

**删掉的文件**：`source/DeveloperCenterServer.cs`（1300 行的本地 `HttpListener` 后台：`127.0.0.1:8788`、10 条路由、内嵌 HTML 页面、设备码登录、角色判定）——整条链路换成设置窗口里的开发者页 + GitHub Contents API。

### 改动过的文件

| 文件 | 改动 |
|------|------|
| `source/LauncherSettings.cs` | 新增 `SkillRoots`、`LauncherChannel`、`DshChannel`、`LastSeenAnnouncementId` |
| `source/LauncherSettingsStore.cs` | `Normalize` 接受新字段的取值（Stable/Preview；Auto/latest/next/alpha） |
| `source/DshUpdateService.cs` | 新增 `DshUpdatePackage.Channel` 与 `SelectNewestVersion`；不再只读 `dist-tags.latest` |
| `source/UpdateSupport.cs` | 清单地址按通道解析：预览版优先 `manifest-preview.json`，缺失时退回 `manifest.json`；资产镜像改走 `GitHubAccelerator` |
| `source/FeaturedSkillService.cs`、`FeaturedPluginService.cs`、`SkillMarketService.cs`、`PluginStoreService.cs`、`SkillInstallService.cs` | raw / 归档候选统一交给 `GitHubAccelerator`；顺手补上插件推荐读取时漏传的代理设置 |
| `source/SettingsWindow.xaml` | 技能页（三页签 + 详情弹窗）；**更新页整体重排**；**开发者页**（模块列表 + 推荐管理 + 公告模块 + 下载源/权限预留）；**主页**（欢迎卡 + 三格状态 + 公告栏 + 快捷入口）；**关于页更新日志卡**；`assets/SettingsNavIcons*/skills.svg`、`home.svg` |
| `source/SettingsWindow.xaml.cs` | 技能页全部交互；更新页通道与摘要；代理就绪闸门；开发者页交互（含公告增删改与发布）；主页状态与公告；关于页更新日志；默认落地页改为「主页」 |
| `source/WinUIProgram.cs` | DSH 检查结果带上命中的发布通道；去掉 `DeveloperCenterServer` 的启动与停止 |
| `CHANGELOG.md` | 新增 1.4.10 小节（技能中心 / 开发者中心 / 主页与公告 / 加速源） |

### 关键设计约定（改这块前先读）

1. **技能根**：`<dshRoot>\.dsh\skills`（写入首选）、`%USERPROFILE%\.agents\skills`、`LauncherSettings.SkillRoots` 里用户自己加的目录。DSH 只认根下一层，嵌套 `**/SKILL.md` 不扫，我们也不扫。
2. **停用不是删除**：`SKILL.md` 改名成 `SKILL.md.disabled`，DSH 的 watcher 下一步就把它从目录里摘掉，资源一个不动。
3. **删除是挪走**：整目录或单个 md 挪到 `%LocalAppData%\DeepSeekHarness\skills-trash\<时间戳>-<名字>`，永远可以捞回来。
4. **在线目录靠 tree 不靠整包**：一次 `/git/trees/<branch>?recursive=1` 列出全部 `SKILL.md`，简介再由 `raw.githubusercontent.com`（不占 API 配额）并发补齐。
5. **GitHub API 恒走 `api.github.com`**：第三方加速域名对搜索/tree 不稳定；线路差异只体现在 raw 与 tarball 的候选地址上。所有请求都接 `ProxySupport.Apply(request, settings)`。
6. **只有启动器装的技能能检查更新**：靠 `SkillInstalls.json` 里的 `pushedAt` 对比，手工放进技能根的技能没有来源，只会提示「不是启动器装的」。
7. **推荐页内容源是仓库根 `featured-skills.json`**：`schemaVersion` 必须为 1，字段 `owner/repository/path/name/description/category/note/order/defaultBranch`。远端拿不到时不写缓存，继续回退内置列表——这样下次启动还会重试远端。
8. **压缩包来源走 `LocalArchive`**：`SkillMarketItem.LocalArchive` 非空时 `Install` 直接转 `InstallFromArchive`，不碰 GitHub；解压按文件头 `1F 8B` 嗅探 tar.gz、否则当 zip，后缀写错也认。落地逻辑统一在 `PlaceSkill`，GitHub 与压缩包两条链共用。
9. **更新通道是两个开关，不是一个**：启动器用 `LauncherChannel`（Stable→`manifest.json`，Preview→`manifest-preview.json`，缺失自动退回），DSH 用 `DshChannel`（Auto 取所有 npm tag 里版本最高的，也可钉住 `latest`/`next`/`alpha`）。**DSH 的 npm `latest` 不等于最新版**，只读它就会漏掉挂在 `next` 上的版本。
10. **RadioButtons 不要写死 `SelectedIndex`**：控件首次布局会按内部索引重排选中项，触发一次 `SelectionChanged`。代理设置曾经因此被覆盖成「不使用代理」——`SelectRadioByTag` 现在同时设置 `SelectedIndex` 和 `SelectedItem`，`SaveProxySettings` 也要等 `_proxyUiReady` 才允许落盘。
11. **软件内产品名一律写 `Dafeiyu-Go`**（英文 + 空格 + 用途，例如「Dafeiyu-Go 设置」）。中文名只留在仓库名、发布说明和文档里；改文案时不要写回中文名。
12. **技能卡片只放条目自身的信息**：分类可以留；Stars、编程语言、「套装 N 个技能」都是仓库级数据，同一个仓库几十条技能会每张卡重复一遍，已经移除，改放详情弹窗。图标用 GitHub 头像（本地技能读自己目录的 icon / logo）。
13. **「优先显示中文内容」的判定不能看分类**：分类是启动器自己写的中文标签，算进去会让全体条目都命中，排序等于没生效。只看名字、简介和仓库路径。
14. **DSH 更新进度的分母是「净增」，不是「总大小」**：升级是替换不是从零装。必须先在开始时量一份 `node_modules` 基线，进度只由 delta 驱动；`NpmInstallProgress.ExpectedGrowthMb = 300`（实测 223MB → 504MB，净增约 281MB）。拿总大小当分母就会出现「一开始 223/230」和「后面 503.8/230」两个假读数。
15. **npm install 必须带 registry**：`ResolveInstallRegistry` 在大陆 CDN 线路返回 npmmirror，官方线路返回 null（不套第三方镜像）。不带的话国内是直连 registry.npmjs.org 装 500MB 依赖——虚拟机上实测 8 分钟没结束，看起来就是卡死。
16. **技能目录是两段式并发**：先并发拉各仓库 git tree（8 线程，一仓库一请求），再统一并发补 raw frontmatter（16 线程）。**别在 tree 线程内部各自再开 raw 线程**，那是 8×8=64 个瞬时请求，会被 GitHub 限速反而更慢。匿名 API 额度只有 60 次/小时，一次全量目录要约 45 次请求，所以 4 小时磁盘缓存和「写入 Token」提示都不是可选项。
17. **下载进度不能依赖总长度**：镜像/加速源常用 chunked，`TotalBytesToReceive` 会是 -1。任何 `DownloadProgressChanged` 的处理都要在 total 未知时改用「已下载量」的单调曲线（插件 2%→70%、技能 8%→82% 各自的分段），否则进度条会卡死在起始值而文案照常跳。
18. **文案标准（本仓库沿用）**：砍半再砍半、信息前置、用户视角、按钮用动词。具体到落地：描述不超过约 22 字；按钮禁止「确定/是/好的」（用「关闭/重启 DSH/稍后」）；空列表写成「还没有 X，去 Y 装一个」；错误文案是「发生了什么 + 怎么办」，不要重复标题里已有的「XX 失败」，技术细节进日志。
19. **开发者页是模块拼装**：左边 `DeveloperModuleList` 的列表项 `Tag` 对应右边一个面板，代码里 `DeveloperPanels()` 登记 `(Tag / 标题 / 面板)`。加板块 = XAML 加一条列表项 + 一个 `x:Name="Developer<X>Panel"` 面板 + 代码模块表加一项，`SelectPage("Developer")` 会自动显示并加载。**面板必须是 `SettingsContentHost` 的直接子级（20 格缩进）**：塞进别的页面里会被那个页面的 `Collapsed` 连带压成 0×0，页面看起来就是一片空白（这个坑踩过一次，诊断日志里 `W=0 H=0`）。
20. **开发者页写仓库靠 Token，不靠登录**：没有本地服务、没有网页、没有设备码。`FeaturedAdminService.Publish` 每次发布前重新 GET 一次 sha 再 PUT，避免两边同时改文件撞 409；读的时候如果 Token 被 GitHub 拒（401 Bad credentials），自动匿名重读一次并在 InfoBar 提示换 Token——公开仓库的读取不该被一个过期 Token 卡死。
21. **加速看档位，不看地域**：`GitHubAccelerator.IsEnabled` 只认设置里的「在线源」——加速源就把 GitHub 地址展开成「镜像在前、官方垫底」的候选，官方源就只给官方地址。以前按 `RegionInfo.IsChinaMainland` 判断，海外的用户选了加速源也吃不到加速，国内用户想走官方源也甩不掉镜像。**唯一例外是 `api.github.com` / `uploads.github.com`**：第三方域名对 API 不稳定，带 Token 的请求也不该交给第三方。前缀池只有 `GitHubAccelerator.Prefixes` 一份，别再各自维护数组。

    **候选顺序是实测出来的，别凭感觉换**（2026-09-27 虚拟机 `192.168.188.130` 实测，只要响应头）：

    | 地址 | 结果 |
    |------|------|
    | `codeload.github.com/.../tar.gz/...` | ✅ 0.65~0.84s（不跳转，最稳） |
    | `ghproxy.net/<github 归档或 raw>` | ✅ 归档 2.7s / raw 0.95s |
    | `gh-proxy.com/<...>` | ✅ 归档 2.8s / raw 1.6s |
    | `ghfast.top/<...>` | ❌ 每次都是超时 |
    | `github.com/.../archive/...`（直连） | ❌ 超时 |
    | `cdn.jsdelivr.net/gh/...`（raw） | ❌ 超时（但别的线路它更快，所以留在第二位） |
    | `raw.githubusercontent.com`（直连） | ❌ 超时 |

    所以**归档候选 = `codeload → 镜像 → 直连`**，**raw 候选 = 镜像（按优先级）→ 直连**。踩过的坑：把 `codeload` 排到直连归档后面，装技能包要先等两条死路走完，看起来就是「下载不动」。

22. **加速源可以自选，默认自动**：`LauncherSettings.MirrorSource`（`Auto` / `ghproxy` / `gh-proxy` / `ghfast` / `jsdelivr`）对应 `GitHubAccelerator.Sources` 里那四个源。规则就两条：
    - **自动**：启动时测一轮（`AcceleratorLatencyWatcher.Start` + `MeasureInBackground`），**网络环境一变再测一轮**（`NetworkChange` 的网络可用性/地址变化，以及用户在设置里换代理时手工叫一次 `NetworkChanged`，10 秒防抖），`OrderedSourceIds` 按**实测延迟升序**排候选；归档的第一位固定是 `codeload`（它不是镜像、实测最快，不参与源排序）。
    - **自选**：选中的源排第一，其余保持默认顺序兜底，**不再按延迟做任何自动重排** —— 用户选了就听用户的，哪怕它比别人慢。
    界面在常规页「在线引擎」那一行左边（`高级设置` 按钮 → `AcceleratorAdvancedPanel`），单选框文案由 `RebuildAcceleratorSourceOptions` 填成「名字 · 0.9 秒 / 超时 / 还没测」。**「大陆 CDN 加速」和「自动」是同一个档位**，`SyncAcceleratorControls` 负责两边对上：自动 → 下拉是「大陆 CDN 加速」；钉了具体源 → 下拉就地变「自定义」（`ComboBoxItem.Tag="Custom"`，读出来还要折算回 `UpdateSource="Accelerated"`，别把 Custom 写进设置）；选「官方源」→ `高级设置` 按钮整个藏起来。**`ComboBox` / `RadioButtons` 的程序化选中都会触发 `SelectionChanged`**，重建时要先关掉 `_acceleratorUiReady` 再选、选完再打开 —— 跟代理那套 `_proxyUiReady` 是同一个坑。
23. **下载前必须探活**：`TimeoutWebClient` 里 `request.Timeout` 只管握手、`ReadWriteTimeout` 管下载中，所以一个「连上了但不吐数据」的镜像会把整条下载吊到下载超时为止，进度条一动不动。`GitHubAccelerator.Probe(url, settings, 15000)` 只等响应头、不读正文，不通过就换下一个候选；插件和技能安装的候选循环都要先过这一步。
24. **主页公告和更新日志都是「远端 → 缓存 → 兜底」三级**：`announcements.json` 与 `Changelog.json` 各自缓存在 `%LocalAppData%\DeepSeekHarness`。主页先同步读缓存立刻出内容（`forceRefresh=false` 只读缓存，**不要在这条路上发网络请求**，否则会卡 UI 线程），再后台 `forceRefresh=true` 刷远端；更新日志的缓存带版本号，版本对不上就丢掉重取，免得显示上一个版本的说明。公告正文里的 `order` 就是文件数组顺序，开发者页「上移 / 下移」改的也是它，两个页面看到的顺序才会一致；置顶永远压在非置顶上面。
25. **开发者页三个列表共用一套卡片模板与按钮**：按钮只能靠 `DeveloperEntryCard.Kind`（plugin / skill / announce）判断该动哪份数据。**不要按「当前选中哪个页签」分派**——公告模块下页签状态还是「技能推荐」，会误改技能列表（这个 bug 真出现过）。
26. **下载一律走 `DownloadSupport.Download`**（插件包、技能包）：4 线程分片（服务端不支持 `Range` 就用探测结果退回单线程）、每个分片单独落 `.partN`、失败带 `Range` 续传（每片 2 次）、下完拼成一个文件再删临时文件。进度是真进度：
    - **不要再用同步 `WebClient.DownloadFile`** —— 它根本不触发 `DownloadProgressChanged`，界面只能等下载结束才从 0 跳到 100（用户报的「有进度但不显示、直接跳完了」就是这个）。现在自己读流、每 80KB 报一次。
    - 界面文案：按钮只放动词（`ProgressActionLabel` 取第一个空格前的词），速度和体积进 InfoBar（`1.2 MB/s · 8.4 MB / 24.0 MB`；总长未知时 `1.2 MB/s · 已收 8.4 MB`）。
    - **停滞看门狗**：20 秒没有新字节就 Abort 当前候选。`request.Timeout` 只管握手、`ReadWriteTimeout` 管单次读；把 3 分钟的下载预算塞进 `ReadWriteTimeout` 会出现「连得上、响应头秒回、正文不吐数据」的镜像吊满 3 分钟（虚拟机日志实测一条候选卡了 6 分 18 秒）。
27. **技能卡片的三个坑**：星星的可见性看的是 `Stars`（文案），`StarsCount` 只是数据 —— 只塞 `StarsCount` 就永远不显示；仓库头像走加速档位（`SkillAvatar`），`github.com/<owner>.png` 国内直连经常超时；「只看中文内容」必须是**真过滤**（`IsLocalLanguageCard`），只把中文顶到前面的话用户翻两页就以为没生效。
28. **插件市场的「已验证」必然命中全部**：市场 API 自己写着 `stats.verified == fetched`（2026-09-27 实测 2500/2500，每条 `validation.overall == "verified"`），也就是市场只出版验证过的插件。所以筛选必须给解释文案 + 一个「未验证」选项，并提示「想看未验证的把插件来源切成 GitHub」—— 不然会被当成筛选坏了。

### 本轮验证

- `build-winui.ps1` 等价命令发布通过（注意：本机 XAML 编译器需要 cmd.exe 管道，受限沙箱会报 `Win32Exception (5) 拒绝访问`，用完整权限跑）。
- 临时控制台工程复用生产源码，绕过 WinUI 跑真实逻辑：
  - 扫描/解析/启停/删除/安装记录：通过（含 `>` 与 `|` 块标量、嵌套目录不扫、停用往返）。
  - 在线目录：160 条技能 / 40 个仓库，缓存命中一致。
  - 安装：子目录安装、references 一起复制、覆盖安装、无 `SKILL.md` 报错，全部通过。
  - 更新：判定有新版本 → 覆盖安装 → `pushedAt` 刷新 → 再查无更新 → 手放技能不参与，全部通过。
  - 推荐：远端 404 时回退内置 7 条，且能直接安装，全部通过。
  - 压缩包：输入识别、多技能 zip 列表 + 按子目录安装 + 只复制目标目录、单技能自动定位、tar.gz 安装、空包报错，全部通过。
  - DSH 通道：打真 registry，Auto→`0.1.7-rc.1`(next)、latest→`0.1.5-rc.3`、next→`0.1.7-rc.1`、alpha→`0.1.7-alpha.2`、不存在的 tag 自动退回 Auto，全部通过。
- 用 `--settings-preview=<页>` 起真窗口做了界面自检（只按自己启动的 PID 取窗口，避免误伤用户自己的启动器）：
  - 技能页三个分页、160 条在线目录、卡片与分页控件渲染正常。
  - 代理设置：把配置改成 `Custom` 起窗，修前 1 秒后会被写成 `None`，修后保持不变；再用 UI Automation 真点「系统代理 / 自定义代理 / 不使用代理」，三次都正确落盘。
  - 更新页：控件清单确认新的四张卡、两个通道下拉、三行「当前版本 · 通道」摘要都在；窗口标题与页眉显示为 `Dafeiyu-Go 设置`；两个通道默认都是「正式版」，启动器通道置灰。
  - 进度条相位：`--settings-preview=Updates:Installing` 下按钮显示「更新中」、进度文本为「下载中 12.4 MB / 230 MB · 请不要关闭软件」。
  - 技能卡片：UI Automation 清单确认星星、语言标签、「套装 N 个技能」都已消失。
- **没验到的**：技能卡片的头像图标只做了代码层设置，因为截图时用户的其它窗口一直占着前台，没能目视确认；卡片图标走的是插件页同一套模板，插件页的头像是正常显示的。
- 开发者页（`--settings-preview=Developer`，UI Automation 逐个选中模块）：模块清单 4 项都在，四个面板各自渲染出自己的内容；推荐管理读到远端 `featured-plugins.json` 的 4 条并渲染成「序号 + 仓库 + 分类 + 备注 + 编辑/移除」；公告模块在远端还没有 `announcements.json` 时显示「还没有公告。点「新增」写一条。」；点「新增」能弹出公告编辑弹窗（标题 / 正文 / 标签 / 日期 / 置顶 / 详情链接 / 保存 / 取消，日期默认今天）。修前这一整页空白，原因是 `DeveloperPage` 被嵌在「关于」页里面。
- 主页与关于页（`--settings-preview=Home`）：主页渲染出欢迎卡（`启动器 1.4.9.1 · DSH v0.1.5-rc.3`）、三格状态（DSH 服务 / 启动器 / DSH 本体）、公告卡与四个快捷入口；切到「关于」后更新日志卡显示 `v1.4.9.1 · 来自 GitHub Release` 并渲染出发布说明正文。
- 逻辑自测（`G:\DeepSeek DSH\DSH Works\.ui-check\full-test`，直接编译生产源码 + 最小 shim，`dotnet run -- --online`）：**离线 62 项、联网 66 项全绿**。覆盖加速器候选展开与档位开关、公告解析/容错/序列化往返/排序/缓存、更新日志小节抽取与版本失配丢弃、候选去重；联网那轮实测 GitHub Release 命中 `v1.4.9.1`（606 字发布说明），远端还没有 `announcements.json` 时正确回落到内置三条。
- 自测里发现的两件事：`ghfast.top` 连续超时（已挪到池子最后）；`ghproxy.net` / `gh-proxy.com` / `jsDelivr` 都能正常拿到本仓库的 404/内容，说明路由是通的。
- **加速候选顺序回归（虚拟机实测定位）**：第一版加速改写把归档的 `codeload` 排到了直连 `github.com/.../archive` 后面。虚拟机日志里旧行为是 `ghproxy 失败 → ghfast 超时 → codeload 成功`，改完还要多走 `gh-proxy` 和直连归档两条死路。在虚拟机上逐条实测响应头：`codeload` 0.65s ✅、`ghproxy` 归档 2.7s / raw 0.95s ✅、`gh-proxy` 归档 2.8s / raw 1.6s ✅、`ghfast` 超时 ❌、直连归档超时 ❌、`jsDelivr`(raw) 超时 ❌、raw 直连超时 ❌。按实测重排后虚拟机复测**第一发 codeload 0.65 秒**命中。同一次日志还确认插件安装走 `ghproxy.net` 第一发就成功（13:26），所以坏的只是归档那条链的顺序。
- **加速源自选 + 测速的界面自检**（`--settings-preview=General`）：默认收起，点「高级设置」展开；展开时四个源先显示「还没测」，后台测完自动变成实测值——本机实测 `ghproxy.net 0.9 秒 / gh-proxy.com 1.3 秒 / ghfast.top 超时 / jsDelivr 超时`，摘要「测于 13:34 · 最快 ghproxy.net 0.9 秒」；选中的项写进 `LauncherSettings.json` 的 `MirrorSource`。UI Automation 里这个按钮只暴露成 `[Text] 高级设置`，脚本要往上找一层父按钮才点得到。
- 逻辑测试扩到 **93 项离线全绿**，新增源选择与排序 26 项：自动按延迟升序、自选不做自动重排（哪怕别人更快）、jsDelivr 不参与归档、官方档位下自选不生效、延迟文案（0.9 秒 / 超时 / 还没测）、`AcceleratorProbe.json` 落盘。
- **下拉与高级设置的联动自检**（脚本 `.ui-check\check-accelerator-sync.ps1`，改前先把设置还原成默认）：① 默认「大陆 CDN 加速」+ 自动；② 在高级设置里钉 ghproxy.net → 下拉就地变「自定义」，`MirrorSource=ghproxy`；③ 下拉里选回「大陆 CDN 加速」→ 高级设置的单选自动跳回「自动」，`MirrorSource=Auto`，且「自定义」这一项又从列表里消失；④ 钉 gh-proxy 后选「官方源」→ **「高级设置」按钮整个不可见**，`UpdateSource=Official`；⑤ 收尾恢复默认。
- 自检脚本踩的坑：页面上不止一个 `ComboBox`（「静默启动」那个也是），按控件树往上找会抓错邻居，最后改成**按纵向位置**匹配「在线引擎」标签同一行的下拉框；弹层里的 `ListItem` 挂在窗口子树里、要展开后才可见。
- **下载链路自测**：`DownloadSupport.FormatSize/FormatSpeed` 的文案（512.0 MB / 1.5 MB/s / 未知长度 → ?）、百分比按字节算、总长未知时百分比返回 -1 且文案只说已收多少；**真下了一个文件**（本仓库 CHANGELOG.md，10459 字节）验证分片拼回的内容一致、且不留下 `.part` 临时文件。
- **插件市场「已验证」的实测结论**：拉实时 `api.dshmk.com`（9.6 MB，带 `Accept-Encoding: gzip`）统计：`"validation"` 出现 2500 次、`"overall":"verified"` 2500 次、`stats.verified = 2500 = fetched`，`verificationUrl` 非空的只有 1 条。结论是**市场只出版验证过的插件**，筛选命中全部不是 bug。
- **踩坑记录**：预览模式（`--settings-preview`）用独立互斥体 `MutexName + ".Preview"`，**上一个预览进程没退干净时，后面起的预览会静默退出且退出码是 0**（看起来就像「窗口打不开」）。自检脚本要么显式 kill 自己起的进程，要么在开头清理 `dist*` 下的残留进程——但**绝不能碰用户自己那个启动器**。
- 虚拟机（`DESKTOP-G4QS2K7`，Win10，`192.168.188.130`，SSH 脚本见 `.fix-lasso-state\vm-ssh.ps1`）本轮实测记录，三个问题的根因都在日志里：
  - 技能目录：`09:36:41 搜索到 100 个仓库` → `09:38:12 160 条`，**冷启动 91 秒**；`awesome-*` 那类超大仓库单次能吃掉一分钟。
  - DSH 更新：`09:38:07` 开始，`09:38:09` 起 npm 进程一直跑，**09:46 仍未结束**（8 分钟+），进度条停在 `503.8 / 230`；此时 `package.json` 其实早已是 0.1.7-rc.2、`node_modules` 504MB——是「慢 + 假进度」，不是卡死。日志里还夹着几十条重复的「本地插件列表」，就是进度每跳一次重扫一次。
  - 修后本地实测：技能目录冷启动 **20.9 秒**（tree 8 线程）、缓存命中 **120 毫秒**；raw 并发提到 16 后没能复测（本机匿名额度被测试打爆，core 限流 60/小时）。
- **未做**：真机 Windows 10 上的图标与布局复核。

### 发版前还欠

1. 确认版本号并跑 `set-version.ps1`。
2. 把 `featured-skills.json` 推到 `main`，否则推荐页一直走内置兜底。
3. 想要「启动器预览版通道」真正生效，需要在仓库里发布 `manifest-preview.json`；没有这个文件时预览通道会静默退回正式清单。
4. Windows 10 主题下的导航图标目视复核（本次只按 Fluent regular 对齐了写法，没法在本机看到 Win10 渲染）。
5. 开发者页发布推荐或公告前必须换一个新 Token：本机存的旧 Token 已被 GitHub 判定失效（`GET /user` 返回 401），读取会自动匿名兜底，但写仓库会直接失败。
6. 把 `announcements.json` 推到 `main`（或在开发者页「公告」里点一次「发布到仓库」），否则主页公告栏一直是内置的三条兜底。

---

## 1.4.9.1 本轮交接（2026-09-23）

- 主题页新增“窗口风格”，可选 Windows 10 与 Windows 11。
- Windows 10 使用直角外框和描边导航图标；Windows 11 使用圆角 Fluent 样式。
- DWM 外框已支持运行时切换，不需要重启启动器。
- 插件安装使用按钮下方 2px 进度条，下载阶段和安装阶段共用一条进度。
- 按钮文案只显示“下载中”“安装中”“已安装”，不显示百分比。
- 启动器自更新顺序改为：先更新安装器和卸载器，再更新启动器，然后重启并清理旧副本。
- 推荐插件安装改用和启动器一致的 tarball 下载流程，不再依赖 Git SSH。
- 插件下载使用 4 线程；其他大文件使用 8 线程。
- 大陆 CDN 与官方源严格分流，共用统一的 GitHub 镜像池。
- 取消信号会传递给 pnpm/git 子进程。

发布状态（2026-09-27）：`v1.5.0` 已发布 —— GitHub Release、npm（`@yunxiramito/dsh-launcher@1.5.0`）、
`manifest.json` 都更新到 1.5.0。**没有代码签名**：SignPath Foundation 的免费申请未通过，
两个仓库都走未签名路径（见 SIGNING.md）。

---

## 零、1.4.9 当前状态（接手先读这一节）

> `1.4.9` 是品牌过渡版：界面与仓库先切到“大肥鱼Go / Dafeiyu-Go”，内部兼容标识不变。
> 代码、构建、tag、npm 和 GitHub Release 均已完成。后续启动器与安装器按同一版本同步发布。

### 已经做完并验证过的

| 模块 | 状态 |
|------|------|
| 代理设置 | 完成。`ProxySupport.cs`，直连/系统/自定义(http·https·socks5)，回环强制绕过；已接到更新清单、更新包下载、DSH npm 元数据与 tgz、余额查询、B 站头像；子进程(pnpm/git)走环境变量；DSH 本体进程不接管 |
| 在线引擎两级来源 | 完成。常规页「在线引擎」(大陆 CDN 加速/官方源) + 「插件来源」(DSH 插件市场（优先）/ GitHub 大陆节点或 GitHub 官方)，落盘字段 `updateSource` / `pluginSource` |
| 插件目录 | 完成。主源 `api.dshmk.com`（**必须带 `Accept-Encoding: gzip`，否则 6.7MB 裸传必然超时**），校验 `schemaVersion == 1`、只收 `projectType == plugin`、已验证判定用 `validation.overall == "verified"`；GitHub 搜索接口作兜底。实测 2500 条，磁盘缓存 1.3MB |
| 本地插件页 | 完成。扫 profile 依赖，版本/说明读插件自己的 package.json，图标只认插件目录里的 icon/logo |
| 安装链路 | 完成。支持市场 `install.candidates`、固定 SHA 的 GitHub 压缩包和 `npm:` 来源；GitHub 来源仍按源码落到 `<dshRoot>\plugins\<名字>` 并写 `link:` |
| 插件更新 | 完成。对比 `PluginInstalls.json` 与在线目录的 `pushedAt`，支持单个检查、单个更新、全部更新和更新策略 |
| 更新通知 | 完成。更新后发 AppNotification，按钮为“好的 / 稍后再说”；“好的”重启 DSH |
| 官方推荐 | 完成。读仓库根目录 `featured-plugins.json`，支持远端、磁盘缓存、内置兜底、搜索和分页 |
| 开发者管理中心 | 完成。设置窗口内的「开发者」页（连点五次版本号解锁），模块化布局：推荐管理 + 预留下载源/公告/权限。直接用 GitHub Contents API 读写 `featured-plugins.json` / `featured-skills.json`，支持仓库地址一键导入、排序、编辑、移除、发布；不再起本地服务、不再开网页、没有设备码登录 |
| 组件页 | 完成检测+安装六项：.NET 8 桌面运行时、Windows App Runtime 1.8、Node 22 LTS、MinGit、pnpm、Python；源跟随在线引擎 |
| 组件 PATH | 完成。便携 Node、Git、pnpm、Python 目录写入当前用户 PATH，同时保留启动器绝对路径兜底 |
| 插件页 UI | 三处分页（官方推荐/在线插件/本地插件）、每页 9/18/36/54、搜索、分类(含"已验证")、排序、本语言优先、图标三级回退(API 图片→仓库 icon→GitHub 标记)、卡片内嵌进度按钮、详情卡片、卸载确认 |
| 版本和日志 | `1.4.9` 已同步到 csproj、manifest、设置页和 `Constants.Version`；`CHANGELOG.md` 已写 1.4.9 |

### 当前状态与剩余收尾

1. 尚未在**真实用户 profile** 上执行安装、更新和卸载；目前使用临时 DSH 根目录验证，避免污染环境。
2. 开发者页写仓库需要一个能写该仓库的 GitHub Token。本机存的旧 Token 已失效（GitHub 回 401 Bad credentials），读取会自动匿名兜底并提示，但**发布前必须先去「API」页换一个新的**。
3. `featured-skills.json` 目前只在本地仓库里还没提交，开发者页第一次读会显示 0 条——在推荐管理里导入条目后点「发布到仓库」，它才会出现在远端。
4. 在线插件卡片底部按钮已改为两列等宽，`查看详情` 内容恢复居中；还需要在目标机器上做最后一轮视觉确认。
5. Windows 10 真机仍需复核应用内视觉、完整更新链和退出行为。

`1.4.9` 已发布到 GitHub、npm 和 npmmirror；后续升级统一使用父级 `release-all.ps1`。

### 调试手法（这次用得很多，接着用）

- `source\build-winui.ps1 -OutputDirectory <目录>` 编译；别忘它会把输出目录整个清空。
- 设置窗可以单独起：`DeepSeek Harness.Core.exe --settings-preview=Plugins:Online`
  （分页用 `Plugins:Local` / `Plugins:Featured`，其它页传 `General`/`Api`/`Alerts`/`Updates`/`Components`）。
- 预览模式的日志写在 `%LOCALAPPDATA%\DeepSeekHarness\settings-preview.log`，
  主进程日志仍是 `launcher.log`。排查数据问题就看它（例如"插件市场：解析出 2500 条"）。
- 界面控件有没有真的渲染出来、按钮点不点得动，用 PowerShell 5.1 + UIAutomation 验：
  `%TEMP%\dsh-plugin-selftest\click-test.ps1`（点"查看详情"并报数量/截图）、
  `measure.ps1`（量按钮坐标宽度）。中文控件名要用字符码构造，PS5.1 读不了无 BOM 的
  UTF-8 脚本里的中文。

---

## 一、当前结论

### 发布状态

| 项目 | 仓库 | 基线 | 最新 tag | 状态 |
|------|------|------|----------|------|
| Dafeiyu-Go Launcher | `https://github.com/YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Click-To-Run` | `v1.4.9` | `v1.4.9` | 已发布 |
| Dafeiyu-Go Setup | `https://github.com/YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Setup` | `v1.4.9` | `v1.4.9` | 已发布 |

启动器 1.4.2：

- npm：`@yunxiramito/dsh-launcher@1.4.2`
- npmmirror：
  `https://registry.npmmirror.com/@yunxiramito/dsh-launcher/-/dsh-launcher-1.4.2.tgz`
- GitHub Release：
  `https://github.com/YunxiRamito/DSH-Launcher/releases/tag/v1.4.2`
- 资产：
  `https://github.com/YunxiRamito/DSH-Launcher/releases/download/v1.4.2/DeepSeekHarness-1.4.2.zip`
- SHA256：
  `4edb0f52b1a2b1c295a6edd54664a7cf98269a28815760a272bacaa28a03ed04`

### 两个项目的关系

- 安装器负责：检测环境、补运行库、安装 DSH 本体、下载部署启动器、快捷方式、卸载。
- 启动器负责：托盘常驻、启动和管理 DSH、余额、设置、更新、提醒和系统通知。
- 安装器不内置启动器，安装时读取启动器仓库根目录的 `manifest.json`。
- 启动器发版无需重发安装器；npm 与 GitHub Release 就绪后，新装用户会自动拿到新版。

---

## 二、代码地图

仓库根目录：

`G:\DeepSeek DSH\DSH Works\Project\Dafeiyu-Go\Dafeiyu-Go-DeepSeek-Harness-Click-To-Run`

### 核心代码

| 文件 | 作用 |
|------|------|
| `source/WinUIProgram.cs` | 主入口、托盘、服务生命周期、更新调度、提醒、退出 |
| `source/SettingsWindow.xaml` | 设置窗口布局 |
| `source/SettingsWindow.xaml.cs` | 设置窗交互、即时保存、服务状态、更新状态 |
| `source/SettingsWindowHost.cs` | 设置窗与主进程的回调接口 |
| `source/LauncherSettings.cs` | `LauncherSettings.json` 数据模型 |
| `source/LauncherSettingsStore.cs` | 配置读写、旧配置迁移、DPAPI API Key |
| `source/LauncherAppearance.cs` | 设置窗、托盘、进度窗、通知窗共享主题材质 |
| `source/CornerRadiusHelper.cs` | Windows 10/11 圆角策略 |
| `source/UpdateSupport.cs` | 启动器清单、SemVer 比较、下载、npm tgz 解包、替换脚本 |
| `source/DshUpdateService.cs` | DSH npm 元数据、发布时间判断、tgz 下载、npm 安装与回滚 |
| `source/UpdateUiSnapshot.cs` | 设置页更新状态模型 |
| `source/BalanceSupport.cs` | API Key、余额查询 |
| `source/BalanceAlerts.cs` | 余额、消费和充值提醒 |
| `source/InstallerRegistration.cs` | 同步安装器状态文件与卸载注册表 |
| `source/BilibiliProfileService.cs` | 作者头像 API 与进程缓存 |

### 图标资源

| 路径 | 用途 |
|------|------|
| `source/assets/SettingsNavIcons/*.svg` | Windows 11 彩色 Fluent 图标 |
| `source/assets/SettingsNavIconsWin10/*.svg` | Windows 10 浅色主题图标 |
| `source/assets/SettingsNavIconsWin10Dark/*.svg` | Windows 10 深色主题图标 |

Win10 图标来自 Microsoft Fluent System Icons，`THIRD-PARTY-NOTICES.md` 已记录 MIT 来源。

---

## 三、当前功能

### 设置窗口

- 默认 `1200x720`，最小 `800x560`，可缩放、可最大化。
- 48 像素自绘标题栏，左侧 NavigationView，内容最大宽度 760。
- 页面：常规、主题、API、提醒、服务、更新、关于。
- 关闭设置窗后销毁，再次打开重建。

### 服务生命周期

- 启动时先判断更新是否到期：
  - 仅检查更新：不影响正常启动。
  - 自动下载并安装：先检查/安装更新，再启动 DSH。
- 启动器自更新时直接交棒给替换脚本，不启动 DSH。
- DSH 更新时先停止服务，安装成功后重启并等待就绪。
- 后台每 1.5 秒探测服务状态，连续两次确认后同步：
  - DSH 停止：托盘显示“启动 DSH 服务”，设置页显示“启动服务”。
  - DSH 恢复：托盘显示“重启 DSH 服务”，设置页显示“重启服务”。
- 外部停止或重新启动 DSH 后，状态会自动同步，无需重启启动器。

### 启动器更新

- 来源：`manifest.json` 和 GitHub Release。
- 支持加速源和官方源。
- 更新包支持 zip、npm tgz。
- 下载后校验 SHA256，再解压和替换。
- 进度窗显示：
  - `正在更新启动器到 vX`
  - `准备下载…`
  - `下载中`
  - `安装中 / 请不要关闭计算机`

### DSH 更新

- 读取 npm 完整元数据：
  - `dist-tags.latest`
  - 最新版 tarball
  - 每个版本的发布时间
- 更新判断优先比较发布时间，缺少时间时回退 SemVer。
- 正确识别预发布版本：
  - `0.1.5-rc.2 > 0.1.5-rc.1`
  - `0.1.5 > 0.1.5-rc.2`
- 下载阶段显示真实 `已下载/总大小`。
- 下载完成后执行本地部署，再重启 DSH。
- npm 定位顺序：
  - 内置 Node 下常见 npm-cli 布局
  - 独立 Node 相邻的 `npm.cmd`
  - DSH `node_modules/.bin/npm.cmd`
  - 系统 PATH
  - 最后直接交给 `cmd.exe` 按 PATH 解析 `npm.cmd`

### 更新完成通知

- 启动器重启并确认服务就绪后，只发一条更新完成通知。
- DSH 更新并重启成功后，只发一条更新完成通知。
- 以上通知覆盖本次普通的“服务启动成功/重启成功”通知。
- `更新提醒` 关闭时不发送更新完成通知。

### 退出

- 退出前停止定时器、注销托盘、关闭通知、停止 DSH。
- 不再调用 WinUI `Application.Exit()`。
- 清理完成后直接 `Environment.Exit(0)`，避开部分机器上的原生 `0xc0000005`。

---

## 四、配置与数据位置

### 启动器配置

`%LOCALAPPDATA%\DeepSeekHarness\LauncherSettings.json`

关键字段：

- `portMode` / `fixedPort`
- `startWithWindows` / `silentStart`
- `theme` / `accentSource` / `accentColor` / `material`
- `updateSource`
- `launcherUpdateMode` / `dshUpdateMode` / `updateInterval`
- `lastUpdateCheckUtc`
- `lastNotifiedLauncherVersion` / `lastNotifiedDshVersion`
- `apiKeyProtected`
- 提醒开关和阈值
- `dshRoot` / `nodePath`

### DSH 数据

DSH 启动时设置：

```text
DSH_HOME=<dshRoot>\.dsh
```

常见 profile：

```text
<dshRoot>\.dsh\profiles\web\package.json
<dshRoot>\.dsh\profiles\web\node_modules
```

### 日志

- 启动器：`%LOCALAPPDATA%\DeepSeekHarness\launcher.log`
- 早期启动：`%LOCALAPPDATA%\DeepSeekHarness\launcher-boot.log`
- DSH：`<dshRoot>\logs`
- 安装器：`%LOCALAPPDATA%\DeepSeekHarness\installer.log`

### 更新临时目录

- `%TEMP%\DeepSeekHarnessUpdate`
- `%TEMP%\DeepSeekHarnessBackup-*`

---

## 五、构建与发布

### 本地构建

```powershell
cd 'G:\DeepSeek DSH\DSH Works\Project\Dafeiyu-Go\Dafeiyu-Go-DeepSeek-Harness-Click-To-Run'
.\release.ps1
```

产物：

- `source\dist-1.4.2`
- `DeepSeekHarness-1.4.2.zip`
- `manifest.json`
- `manifest-1.4.2.json`

自检：

```powershell
.\verify.ps1 -Dist '.\source\dist-1.4.2' -DshRoot 'G:\DeepSeek DSH'
```

### 发布顺序

必须按顺序执行：

1. 构建并更新 `manifest.json`。
2. 发 npm。
3. 等 npmmirror 返回 200。
4. 提交 main、推 tag、创建 GitHub Release。
5. 验证 raw manifest、GitHub 资产和 npmmirror tarball。

### 版本号位置

- `source/DeepSeekHarness.csproj`
- `source/app.manifest`
- `source/RuntimeBootstrap.cs`
- `source/RuntimeBootstrap.manifest`
- `source/WinUIProgram.cs`
- `source/SettingsWindow.xaml`
- `CHANGELOG.md`

### CI

- GitHub Actions：`.github/workflows/release.yml`
- tag 触发自动构建、发布和 Release。
- 若 npm 上已存在同名版本，CI 会跳过 npm 发布，不再报重复发布失败。
- 真正的新版本首次发布仍要求仓库 secret `NPM_TOKEN` 有效。

---

## 六、已验证状态

- 1.4.2 本机构建通过。
- `verify.ps1` 通过。
- DSH npm 发布时间判断已用真实 registry 验证。
- 加速源和官方源均能识别 `0.1.5-rc.2 > 0.1.5-rc.1`。
- DSH 主包 tgz 下载和字节进度回调已验证。
- npm 定位已验证到 `E:\Nodejs\npm.cmd`。
- 编译后的 `RunNpmInstall` 已向临时目录安装 `is-number@7.0.0`，返回成功。
- Windows 10 VM 需要复核：
  - 三套导航图标实际显示
  - 自动更新顺序
  - 服务停止/恢复状态同步
  - 退出无 `0xc0000005`
  - DSH 从 `rc.1` 更新到 `rc.2`

---

## 七、已知风险

1. Windows 10 应用内视觉与完整更新链尚未在当前交接窗口完成真机回归。
2. DSH npm 主包 tgz 很小，依赖主要在 npm 部署阶段下载；主包进度不等于完整依赖下载量。
3. `pnpm`、`npm` 等工具在不同安装环境中的目录布局不同，必须保留多候选和 PATH 兜底。
4. 更新第三方插件或依赖会执行第三方代码，UI 必须明确提示风险。
5. GitHub Search API 未认证时限流明显，插件目录需要磁盘缓存和可选 Token。

---

## 八、插件商店方案 B

参考项目：

`https://github.com/0xKcyzz/dsh-plugin-store`

### 原实现结构

它是 DSH 双面插件：

- Host：`src/index.ts` 注册 `/plugin-store/*` HTTP 路由。
- Client：`src/client/StoreTab.tsx` 注入“设置 → 插件”页面。
- 目录：GitHub Search API 搜索 `topic:dsh-plugin`，按 stars 分片、合并和缓存。
- 安装：在 Web profile 执行 `pnpm add`，再把声明 `dsh.bundle.patch` 的依赖写入 bundles。
- 卸载：修改 profile `package.json`，删除依赖/bundle，并删除 node_modules 目录。
- 更新：比较安装时记录的 `pushed_at` 与目录中的最新 `pushed_at`。
- 所有操作重启 DSH 后生效。

### 启动器原生实现目标

在设置窗新增“插件”页面，原生完成浏览、搜索、安装、更新和卸载，不要求 DSH 正在运行。

### 建议模块

| 模块 | 职责 |
|------|------|
| `PluginCatalogService` | GitHub Search、分片、归一化、分类、缓存 |
| `PluginCatalogModels` | 目录项、安装状态、更新状态 |
| `DshProfileService` | 定位 profile、读写 package.json、维护 bundles |
| `PackageManagerRunner` | 定位并执行 pnpm，处理输出、超时和错误 |
| `PluginStoreService` | 安装、更新、卸载、批量更新 |
| `PluginStorePage` | WinUI 搜索、筛选、排序、卡片和分页 |

### profile 与工具位置

默认 profile：

```text
<dshRoot>\.dsh\profiles\web
```

pnpm 定位应参考启动器 npm 定位策略：

- Node 相邻目录
- DSH 的 `.bin`
- `%APPDATA%\npm`
- 系统 PATH
- 最后交给 `cmd.exe` 解析 `pnpm.cmd`

### 安装流程

1. 用户确认安装。
2. GitHub 根 `package.json` 预检 `dsh.bundle.patch`。
3. 在 profile 目录执行 `pnpm add <spec>`。
4. 读取依赖和 package.json。
5. 只追加真实 bundle，保留 DSH 安装自带 bundle。
6. 记录仓库 `pushed_at` 和安装时间。
7. 提示“重启 DSH 后生效”。

### 卸载流程

1. 从 profile dependencies 找到依赖键。
2. 删除依赖和 bundle 项。
3. 删除 profile `node_modules/<dep>`。
4. 清理安装时间记录。
5. 提示重启。

### 更新流程

1. 读取安装元数据。
2. 比较最新 `pushed_at`。
3. 对单个插件执行安装流程覆盖更新。
4. 支持“全部更新”。

### 必须保持的规则

- 必须使用 pnpm，因为 profile 可能包含 `link:` 依赖。
- 不得删除这些安装自带 bundle：
  - `@deepseek-ai/dsh-base`
  - `@deepseek-ai/dsh-web-app`
  - headless profile 下还有 `@deepseek-ai/dsh-headless`
- 卸载不能依赖 `pnpm remove`，避免重新联网解析 git 依赖。
- 安装 URL、npm 包名、`github:owner/repo#subdir` 都要支持。

### 推荐落地顺序

1. 先实现 profile 读取、pnpm 定位和安装/卸载服务。
2. 再实现 GitHub 目录和磁盘缓存。
3. 最后做 WinUI 页面、筛选和批量更新。

---

## 九、接手第一步

1. 读父级 `Dafeiyu-Go\HANDOVER.md`、本文件、`TRANSITION.md` 和 `CHANGELOG.md` 的 1.4.9。
2. 修改设置 UI 前读 `SettingsWindow.xaml`、`SettingsWindow.xaml.cs`、`SettingsWindowHost.cs`。
3. 修改更新前读 `UpdateSupport.cs`、`DshUpdateService.cs`、`UpdateUiSnapshot.cs`。
4. 修改主题、圆角和图标前读 `LauncherAppearance.cs`、`CornerRadiusHelper.cs`。
5. 修改路径和卸载前读 `InstallerRegistration.cs` 和安装器仓库的 `ConfigStore.cs`。
6. 后续版本必须通过父级 `set-version.ps1` 和 `release-all.ps1` 同步启动器与安装器。
## 本轮公告通知交接（2026-10-07 晚）

### 已完成

- 独立 DeveloperCenter.Server 已部署，临时公网地址为 `http://202.189.21.218:8787`。
- 根路径、`/health/live`、`/health/ready`、`/api/messages` 已验证 HTTP 200。
- `/api/admin/messages` 返回 403；数据库无公网端口，SSH 映射未改；TLS 配置和证书目录保留。
- 客户端已接入公告/通知模型、60 秒轮询、匿名安装 ID、心跳、回执模型、公告已读状态和队列逻辑。
- 信息小窗增加 Notice 模式：标题、正文、时间、蓝色圆圈叹号、最多两个按钮、确认已读按钮、长正文滚动和受限 actions 命名管道。
- Markdown 当前为安全纯文本转换，不执行 HTML、脚本、图片、链接目标或代码块，不是完整富文本渲染。
- 设置窗口常规页增加 BaseUrl、60~86400 秒轮询、在线统计、交互回执、允许 HTTP 开关和明文风险提示。
- 客户端回归 38 项通过；主启动器构建 0 错误；helper 构建 0 错误；信息窗基础烟测通过。

### 本轮用户反馈与暂缓项

1. 设置窗口左下角没有看到在线人数，presence 当前只写日志，未接到可见绿色圆点和 `N 人在线` UI。
2. 信息小窗真实公告内容下仍会出现上下留白不对等；尚未完成 Notice 真实内容截图和像素测量。
3. 最终启动器预览曾被旧安装实例的单实例机制接管，从最终预览目录启动的进程随后退出，主启动器真实预览原因待查。
4. 尚未用服务端发布测试公告完成真实轮询、弹窗、确认已读、按钮点击和指标核验。
5. HTTP 是临时明文方案，禁止客户端传管理员 Bearer Token；备案后恢复 HTTPS、关闭 Allow HTTP。

### 后续建议

1. 暂不继续改布局；先发布测试公告并测量 Notice 客户区四边距。
2. 把 presence 接到设置窗口左下角在线状态。
3. 修复主启动器预览退出原因。
4. 完成真实端到端指标验收。

## 服务端与信息小窗接口

### 服务端 HTTP 接口

临时公网基址：`http://202.189.21.218:8787`。

```text
公网 :8787 -> Nginx HTTP -> API 127.0.0.1:8788 -> PostgreSQL Docker 内网
```

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/` | 服务状态 JSON |
| GET | `/health/live` | 进程存活 |
| GET | `/health/ready` | API 与数据库就绪 |
| GET | `/api/messages` | 拉取已发布且未过期消息 |
| GET | `/api/presence` | 最近 300 秒在线安装数 |
| POST | `/api/installations/heartbeat` | 匿名 installation UUID 心跳 |
| POST | `/api/messages/{id}/metrics` | Delivered/Displayed/Read/Click 幂等回执 |

管理接口：

```text
GET  /api/admin/messages
POST /api/admin/messages
POST /api/admin/messages/{id}/publish
POST /api/admin/messages/{id}/withdraw
GET  /api/admin/messages/{id}/metrics
GET  /api/admin/presence
```

管理 API 要求 Bearer Token。Token 只能保存在可信管理端，不能嵌入启动器客户端。当前公网 HTTP 为明文临时方案，禁止发送任何管理凭据；备案后恢复 HTTPS。

消息字段：`kind`、`title`、`markdown`、`expiresAt`、`buttons`。按钮最多 2 个，动作只允许 `Url`、`Settings`、`Dismiss`。Url 仅 HTTPS 无凭据地址；Settings 只能跳转白名单设置页；客户端必须再次校验。

```json
{"installationId":"匿名UUID"}
```

```json
{"installationId":"匿名UUID","kind":"Displayed","buttonPosition":null}
```

指标类型为 `Delivered`、`Displayed`、`Read`、`Click`。Click 的 `buttonPosition` 只能为 0 或 1，其余指标必须为空；按消息、安装 ID、指标类型和按钮位置去重。

完整服务端契约见 [`DeveloperCenter.Server/docs/API.md`](../DeveloperCenter.Server/docs/API.md)。

### 信息小窗 IPC 接口

主进程使用 `InfoWindowClient`，helper 为 `DafeiyuGo.Info.exe`。主命名管道：

```text
DafeiyuGo.Info.<session>
主启动器 -> helper
CurrentUserOnly，每行一个 JSON
```

启动参数：

```text
--theme=<System|Light|Dark>
--material=<Mica|Acrylic|...>
--window-style=<System|Windows10|Windows11>
```

命令：

| Command | 作用 |
|---|---|
| `Appearance` | 应用主题、材质、窗口风格 |
| `Update` | 更新进度，含 Percent |
| `Working` | 工作中状态 |
| `Complete` | 成功/失败/警告结果 |
| `Close` | 关闭 |
| `Notice` | 公告/通知专用窗口 |

Notice 字段包括 `NoticeId`、`Title`、安全纯文本 `Detail`、`PublishedAt` 和最多两个 `Buttons`。窗口显示正文滚动区、发布时间、按钮和确认已读按钮。

动作回传使用独立管道：

```text
DafeiyuGo.Info.<session>.actions
helper -> 主启动器
CurrentUserOnly，每行一个 JSON
```

```json
{"NoticeId":"消息UUID","ButtonIndex":null}
```

`ButtonIndex:null` 表示确认已读；`0` 或 `1` 表示点击按钮。主启动器必须校验当前消息和按钮定义，再执行已读、URL、设置页或关闭动作，并发送 Read/Click 指标。helper 只回传索引，不执行 URL 或远程命令。

发布/预览目录必须包含 `DafeiyuGo.Info.exe`、`DafeiyuGo.Info.dll`、运行时依赖和 `DafeiyuGo.Info.pri`。

## 2026-10-10 1.7.3 续做补充（追加）

详细变更、内存采样、回归结果和未完成事项见父目录 `HANDOVER-1.7.3-2026-10-09.md` 的“2026-10-10 续做记录”。模型页只管理启动器启动的 DSH 提供商/模型目录，不含默认模型或网页会话同步。全屏通知延后指标迁移 `202610100001_DeferredNotificationMetric` 尚未部署。新的本地测试 ZIP 为 `Temp_SetupandLauncher\Dist\Dafeiyu-Go-Launcher-1.7.3-local-test-20261010.zip`，不代表正式发布。

外网代理：`http://127.0.0.1:7890`。后端 SSH：`ssh -p 23129 -i "$env:USERPROFILE\.ssh\dafeiyu_ed25519" dafeiyu@202.189.21.218`。用户要求后续有可并行工作时尽可能使用多个子 Agent 分工。交互网页/预览留到功能完成后的发版阶段再处理。

2026-10-10 暂停点与最新问题状态已追加到父目录 `HANDOVER-1.7.3-2026-10-09.md` 的“2026-10-10 最新问题记录（暂停点）”。本轮仅文档更新后暂停；文中明确区分已改但未完成启动器构建/烟测的代码、已通过的隔离回归，以及仍需处理的模型页、切页内存、导出与导入等事项。

## 2026-10-10 下载源隔离与即时切换验收

本轮恢复工作并使用三个子 Agent 分工，当前最新本地构建为父目录 `Temp_SetupandLauncher/FinalAcceptance-20261010/Launcher-SourceIsolation-Final`，启动入口 `DeepSeek Harness.exe`。构建与 `verify.ps1` 均通过，未发布或替换真实安装。具体修复和验证见父目录交接文档末尾的同名章节；旧暂停状态保留供历史对照。

## 2026-10-10 端口误判问题

Steam 运行时占用 127.0.0.1:8080 曾被启动器误认成 DSH。当前未编译的本地修复、回归 DLL 被安全软件隔离的详情及验证边界，见父目录 `HANDOVER-1.7.3-2026-10-09.md` 的“Steam 8080 端口误判修复（未编译）”。

## 2026-10-10 反馈对话与支持按钮最终补充

前述“未编译”是历史状态。当前正式源码已完整编译并生成本地 1.7.4.0 测试包，未发布启动器。最终入口 `../Temp_SetupandLauncher/FeedbackVoteDelivery-20261010/Launcher-PersonPlus-Accent/DeepSeek Harness.exe`，源输入/ZIP 校验及完整功能记录见该目录上级 `REPORT.md`。支持按钮采用正文右下角 16px 人形加号：未选中描边与界面文字同色，选中实心且图标/人数跟随主题强调色，可取消；暗色标签恢复偏灰圆角矩形。反馈默认服务器每页 10 条，支持人数优先，同票按创建时间新到旧；通知扫描独立使用 updated 排序。253 项客户端回归及最终浅/深色各 20 项 UI 检查通过，端口独立 DLL 仍因之前隔离未运行。服务端已按用户本轮授权更新，细节见服务器交接记录。

## 2026-10-10 1.7.5 启动失败修复与统一图标

- 日志定位到启动失败根因：DSH 实际已输出 `http://127.0.0.1:8787/?token=...` 并在监听，但启动器的严格进程身份检查没有识别 Windows 命令行中被转义成双反斜杠的 DSH 路径，于是 60 秒后误报未就绪、停止 DSH 并退出。现已规范化重复路径分隔符。
- 真实 Node/Windows 监听夹具 34 项通过：双反斜杠路径、普通路径、第三方 Node HTTP 服务、重复就绪检查、停止后状态均覆盖；普通 HTTP 服务仍不会被认成 DSH。
- 启动器版本统一为 `1.7.5.0`：核心、引导、信息窗、清单和启动日志版本同步。
- 新图标已覆盖启动器 EXE、核心进程、托盘、设置页和信息窗进程；用户指定的白色留白完整保留，不裁切、不透明化。
- 最终本地包：`Temp_SetupandLauncher/StartupFix-1.7.5-AllIcons-20261010/Dafeiyu-Go-Launcher-1.7.5-all-process-icons-test-20261010.zip`，34,014,500 字节，SHA-256 `EEE58663D0ED196E53DB239C0152C5B8AF68E8A1B70309F49528EA431883CD38`；`verify.ps1` 通过，121 个 ZIP 文件校验通过。
- 旧启动器进程和 Windows 图标缓存可能让第一次观察仍显示旧图标；测试前请退出旧启动器并从新包目录启动。真实 UAC、真实用户 DSH 启动和安装/自更新仍需手测。
