# 贡献地图

先按功能定位代码，再提交范围小、验证清楚的 PR。完整流程见 [CONTRIBUTING.md](../CONTRIBUTING.md)。这张地图描述实际目录和现有耦合，不承诺每个模块已经是独立程序集。

| 功能 | 代码入口 | 边界与常用验证 |
|---|---|---|
| Aries 设备协议 | `source/Features/Connections/Aries/` | 签名、设备/组状态、消息、provider 协议和批量传输；`tests/ConnectionLifecycle.Regression/` |
| 本机 DSH 连接桥接 | `source/Features/Connections/Provider/` | 命名管道所有权、provider 生命周期、DSH 归档预加载与重试期限；连接生命周期回归及隔离宿主验证 |
| 配对请求 | `source/Features/Connections/Pairing/` | 队列、过期/世代校验、LauncherContext 通知与允许/拒绝；生命周期回归和原生提示窗验证 |
| 连接设置客户端 | `source/Features/Connections/Settings/` | 设置页 DTO、自动连接与刷新；页面仍在 `source/SettingsWindow.Connections.cs` / `source/SettingsWindow.xaml`，不在这里混入全局外观 |
| 通用提示窗 | `source/UI/Dialogs/PromptWindow.cs` | 标题、图标、内容/按钮容器、主题、材质和置顶；业务决定由调用者处理，运行中验证浅深/圆角/材质同步 |
| DSH 页面宿主 | `source/DshWindow.cs`、`DshWindowFileDrop.cs`、`DshWindowPolicy.cs` | WebView、菜单、F5、拖放与焦点；`tests/DshWindow.Compile/`、`tests/DshWindow.Regression/`、`tests/Window.Ui.Smoke.ps1` |
| 设置与外观 | `source/SettingsWindow.xaml`、`SettingsWindow.*.cs`、`LauncherAppearance.cs`、`CornerRadiusHelper.cs` | 保留 XAML 与生成代码相对布局；遵守 [UI 规范](UI-DESIGN-STANDARDS.md)，核实际运行时样式 |
| 备份与官方导入 | `source/Backup/`、`SettingsWindow.BackupImport.cs` | 官方格式、凭据、导入进度与恢复；`tests/DshDataImport.Regression/`、`tests/BackupImport.UiFixture/` |
| 插件安装与更新 | `source/ConnectionPluginInstallation.cs`、`ConnectionPluginOperation.mjs`、`ConnectionPluginUpdateService.cs`、`source/assets/` | 固定包完整性、官方 profile 写入和操作锁；`tests/PluginInstallRepair.Regression/`、`PluginOfficial.Regression/`；保留现有嵌入资源逻辑名 |
| 信息窗与通知 | `source/InfoHost/`、`InfoWindow.cs`、`InfoWindowClient.cs` | helper 进程/管道、公告和按钮回执；`tests/InfoWindowClient.Regression/`、`tests/InfoHost.NoticeDelivery.UiFixture/` |

## 目录与命名约定

- 连接功能的纯 C# 文件已迁入 `Features/Connections`。主项目使用 SDK 的递归 Compile，不需为每个移动文件加一条引用；linked-source 测试必须更新显式路径。
- 保持 `DeepSeekHarnessLauncher` 命名空间和 partial 类名，避免把目录整理变成 API 迁移。
- `SettingsWindow` 与 `LauncherContext` 的 partial 文件可按功能归档，但 XAML 生成入口、应用入口、打包资源和 InfoHost 项目边界保留原位。
- `source/Backup/`、`source/InfoHost/` 和已有独立仓库已经是边界；本轮不新建仓库、项目或大框架。

## 跨仓库协作

发布元数据：根目录 `manifest.json` 是客户端固定更新入口；版本清单归档在 `manifests/versions/`，说明见 [清单目录](../manifests/README.md)。README 只保留稳定的产品、使用与贡献说明；版本发布记录写入 CHANGELOG，开发进度与验收记录写入独立交接文档。

| 组件 | 对应仓库/目录 | 注意事项 |
|---|---|---|
| Windows 安装器 | `Dafeiyu-Go-DeepSeek-Harness-Setup` | 导入选择、安装向导、启动器获取与安装；与本仓库版本分别验收 |
| Android | `Dafeiyu-Go-DeepSeek-Harness-Mobile` | 手机连接、provider 能力与 APK 更新 |
| DSH 连接插件 | `Dafeiyu-GO-Connect-Plugin` | PC/Android 固定同一协议包，升级需核双方 pin 与哈希 |
| DeveloperCenter | `DeveloperCenter.Server` | 公告、通知、安装心跳与平台在线统计 |
| Aries | 现有 Aries 服务端仓库 | 设备配对、签名、消息/文件中继、组内 presence；不要复用 DeveloperCenter DTO 或认证 |

协议修改请在 PR 中列出兼容版本、缺失能力的降级方式、升级顺序和相关 PR。服务地址连通、配对成功、插件能力就绪、远端在线是不同状态，不能互相代替。
