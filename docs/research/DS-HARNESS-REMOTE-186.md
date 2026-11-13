# Launcher 1.8.6：DS Harness Remote 连接适配

本轮连接页改用 https://github.com/liguobao/ds-harness-remote 。旧 Aerial / Aries 的配对码、连接服务器、设备遥测、文件缓存与后台自动配对接线不再是新连接页的运行流程；旧身份文件和已安装的旧插件不会被静默删除。

## 已核对的上游

- 2026-11-13 通过本机 7890 代理核对 GitHub HEAD：`25a81da11b667be8f61a8d3f676615d7ff38fd18`。其 README 写 0.6.5，但 npm 的该版本端点返回 404，不能按未发布文案安装。
- npm 正式 `latest` 为 **0.6.4**，发布 gitHead 为 `d873acbdd6ad9f8b1215d6f4ef5be6a427d20d44`，本轮固定适配该可安装版本。
- 正式 npm tarball 已下载并核对 registry 的 SHA-512 integrity：`sha512-5su+nEttUldvhIoPrWrJbH3x5/D56BRsV3TPHbrjIQ4FP703ltCXqamQuVloFFQwzKP4LcNDzXMIbmjijKDcvg==`。
- 上游主入口 `dist/index.js`，bundle `cordis.patch.yml`，MIT 许可。没有将旧 Connect 的 `lib/index.js` 规则套到 Remote。
- 文档链接固定在已发布 gitHead 的 `README.zh.md`；Remote Web 为 https://dsh.r2049.cn/app 。

## 运行方式与边界

连接页提供安装 / 修复适配版本、刷新本机插件状态、显式卸载、打开本机 DSH、打开 Remote Web、打开使用文档。

登录账号、Host 在线状态、主机与工作区选择、传输协商都由上游 Remote 承担。安装完成不等于远端在线，启动器不伪造设备遥测或把服务器可达显示成 Host 在线。其他设备登录同一账号，在 Remote 中选择在线 Host；本机 DSH 需要保持运行。

安装/修复调用现有 `DshPluginCliService.Run`，使用 DSH 官方 `plugin --profile web/desktop add -w ds-harness-remote@0.6.4 --ignore-scripts`；桌面版本走现有官方 shim。普通启动实际是 `web`，不会因 `web` 尚不存在而选中无关 profile。参数保留其他插件依赖，临时操作目录在 DSH 安装根的 `.launcher-remote/operations`。不静默授权可选原生依赖构建脚本，不添加版本兼容豁免；实际可用传输由 Remote 协商。

更高版本、预发布版和无法判断的版本不自动降级；后续更新可在 DSH 自带插件管理中处理。安装与卸载都有界面确认，命令支持取消与超时；退出码为 0 后还会读取依赖、bundle 与真实包文件验证结果。缺失、损坏、过大清单和错误包名不显示成安装成功。账号凭据与工作区不清除。

普通启动不再创建 `ConnectionProviderHost`、注入 `DAFEIYU_CONNECTION_PIPE`、检查旧 Connect 或保存旧连接偏好。独立移动端 UI 准备逻辑保留。`SettingsWindowHost` 不再因构造设置窗口而初始化 Aries singleton。旧兼容类仍保留源码；本地归档删除 admission 继续保留，仅防止既有归档删除意图被跳过，不创建 Aries/Provider，不安装旧 Connect。恢复专用 factory 保留原冻结捕获契约，未扩大备份恢复范围。

## 生产改动闭包

- `source/DsHarnessRemoteService.cs`（新增）
- `source/SettingsWindow.Connections.cs`（新连接页逻辑替换旧配对逻辑）
- `source/SettingsWindow.xaml`（仅 ConnectionsPage 区块）
- `source/SettingsWindowHost.cs`（移除 Aries 的惰性属性初始化副作用）
- `source/WinUIProgram.cs`（仅旧连接启动接线片段）
- `tests/DsHarnessRemote.Regression/*`（新增专项）

共享工作区原本已有大量其他修改。发布必须只合入上述闭包中属于本轮的改动；不能直接以整仓 diff 作为本轮发布差异。备份恢复用 `PcOfficialActualDshLaunchFactory` 的旧连接捕获参数由主 Agent 单独评估，不在本子任务内擅改。

## 验证结果

- `DsHarnessRemote.Regression`：**33 项通过**，包含真实完整性验证 npm 包读取、缺失 / 损坏 / 错包 / 缺入口 / 缺 bundle、profile 精确匹配、版本不降级、官方命令、成功后条件、取消、超时、旧凭据与其他插件保留。CLI 调用边界使用受控替身，不会运行真实安装。
- WinUI **Release x64 编译通过，0 错误**（现有警告保留）。这验证了连接页 XAML、事件处理和真实官方 CLI 方法签名。
- 独立设置 / 假 DSH profile 的页面 UI 专项 **9 项通过**：安装版本、页面与按钮可访问、刷新、旧 Aries 控件移除、安装和卸载确认、取消后保持安装。浅色页面截图已检查。
- UI 专项通过 MSBuild `ApplicationManifest` 参数生成仅测试用 `asInvoker` 宿主，生产 `app.manifest` 没有改动。所有测试数据在 G 盘；未操作真实插件 / 凭据 / DSH 进程。
- 未验证真实账号登录、多设备 P2P / Relay / TURN 联通、真实用户机器安装或杀毒软件是否放行；不得写为这些端到端已经通过。
- 只运行本轮专项和必要编译，未跑全量回归。

### 实际官方 CLI 补充验收

2026-11-13 使用实际编译 Launcher 程序集、官方 DSH `0.2.2-rc.2`、官方完整性校验的便携 pnpm `10.15.1`，通过 `DsHarnessRemoteService.Execute` 在独立 G 盘 profile 执行真实安装与卸载，**10 项通过**。检查真实依赖与 bundle 启用/移除、其他依赖保留、虚拟 Remote 凭据与旧资料逐字节保留。没有使用真实账号或启动 DSH 服务。

初次实测发现系统 PATH 的 pnpm `12.4.1` 在导入包后发生原生栈溢出；服务正确报告失败。普通官方 CLI 子进程现按 `PackageManagerRunner.LocatePnpm` 的既定组件优先级前置包管理器目录，仅修改子进程环境；桌面 carrier 保持其自带实现。复测时 runner 没有修改自身 PATH，只将已校验 pnpm 放入独立 `components/pnpm`，实际安装/卸载均通过。

新增 `tests/DsHarnessRemote.Runtime.Regression/` 可针对最终接受的 Core 重跑此检查。证据在 `../Temp_SetupandLauncher/Feedback5-186-20261113/remote-runtime/run-04.log` 和 `fixture-04/runtime-report.json`。

发行负载不再包含旧 `assets/dafeiyu-go-connect` 包和专用 `ConnectionPluginOperation.mjs`。共享 `ManagedConnectionPluginOperation.mjs` 仍供独立手机网页 UI 检查使用，因此保留；它不恢复旧 Aries 自动启动。

证据目录：`Temp_SetupandLauncher/Feedback5-186-20261113/remote`（相对项目根）。关键文件：`upstream-provenance.json`、`npm-metadata.json`、`npm-0.6.4.json`、`regression.log`、`build-final.log`、`ui-asInvoker-check.log`、`remote-ui-light.png`。

建议本轮更新日志附加：**连接页适配 DS Harness Remote**。1.8.6 的发布由主 Agent 统一执行，本子任务未发布。
