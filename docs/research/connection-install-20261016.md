# 连接插件安装接线（2026-10-16）

通过本机 `http://127.0.0.1:7890` 核查官方 DeepSeek Harness 实现，采用已存在的官方 profile writer。

- 上游：`deepseek-ai/deepseek-harness`，固定 `dsh-v0.2.1-alpha.1` / `5badb15009ae1756c3afe0ae0cef1faafc290ccc`，MIT。
- 已读取该 commit 的 `README.md`、`LICENSE`、`packages/boot/plugin-manager/src/operations.ts`，实际下载留在 `../Temp_SetupandLauncher/Feedback-20261016/plugin-install/upstream-*`。初次误用 src/types/operations.ts 返回 HTTP 404；GitHub tree API 返回 403，随后正确 raw 源码获取成功，不将失败写成没有现成实现。
- 复用官方 `runProfilePnpm`、`saveManifest`、`resolveProfileDir`、`resolveBundleDir` 与 `withFileLock`，调用本机固定 SDK。没有复制上游实现或执行仓库安装脚本。
- 连接包随 PC/Android 交付同一冻结 `dafeiyu-go-connect@0.3.1`，205295 bytes，SHA-256 `8793c4066a7df2e15911322090671e4f1f30bc02093fcda55d74a0a59d835d65`。安装仅添加本地归档，`--offline --ignore-scripts`，不会为安装连接功能请求后端清单或 CA。
- 安装前后检查完整文件摘要、未知同名来源、Node/SDK 实际解析、profile bundle、用户 patch 和其他字段。已验证的旧冻结 0.3.0 可升级；外来或被修改的同名包保留并报错。
- 用户定义连接默认包含 DSH 工作区/对话与网页，因此仅对已确认本应用来源的 Connect bundle 恢复启用，其他插件状态不变。实际远端能力由真实服务 readiness 广告，磁盘安装通过不等于公网互联已验收。

真实固定 SDK/pnpm 离线安装 7 项、实际生产 C# 安装入口回归、完整 WinUI build 与真实 DSH Loader/Registry/Session/Web 测试证据在本轮 Feedback QA 目录；最终结果以报告为准。
