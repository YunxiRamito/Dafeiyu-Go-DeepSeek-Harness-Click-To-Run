# 参与贡献

欢迎从一个可复现的问题、一个清楚的功能边界开始提交 PR。目录入口见 [贡献地图](docs/CONTRIBUTION-MAP.md)，界面修改先读 [UI 规范](docs/UI-DESIGN-STANDARDS.md) 和 [AGENTS.md](AGENTS.md)。

## 找到正确仓库与模块

本仓库负责 Windows 启动器、设置界面、DSH WebView 宿主与桌面侧集成。安装器、Android、连接插件、DeveloperCenter 和 Aries 已有各自独立仓库；这里的目录拆分不新增仓库，也不把不同服务协议混为一个模块。跨端协议 PR 应在描述里列出受影响仓库、兼容顺序与对应 PR。

- 设备连接、签名、配对与 DSH 工作区/对话互联：`source/Features/Connections/`。
- 可复用提示窗外壳：`source/UI/Dialogs/`。窗口只负责呈现，允许/拒绝等业务决定保留在调用模块。
- 导入与备份：`source/Backup/`；设置界面仍以 `SettingsWindow.xaml` 和相应 partial 文件为入口。
- 独立信息窗：`source/InfoHost/` 与 `source/InfoWindow.cs`。

现阶段使用同一 `DeepSeekHarnessLauncher` 命名空间与主项目，功能目录不是新的程序集边界。移动纯 C# 文件时保持内容和命名空间，更新 linked-source 测试和源码探针；不要为了目录整齐批量移动 XAML、资源、嵌入脚本或运行时路径。

## 本地构建与验证

需要 Windows x64、.NET 8 SDK 与项目声明的 WinUI/Windows App SDK 依赖。在本仓库根目录运行，先把 `QA_ROOT` 指向项目旁的隔离目录；不要使用真实用户设置、配对身份或正在运行的 DSH 服务。

```powershell
$env:QA_ROOT = [IO.Path]::GetFullPath('../Temp_SetupandLauncher/Contributor-QA')
New-Item -ItemType Directory -Force $env:QA_ROOT | Out-Null
$env:TEMP = Join-Path $env:QA_ROOT 'tmp'
$env:TMP = $env:TEMP
$env:DOTNET_CLI_HOME = Join-Path $env:QA_ROOT 'dotnet-home'
New-Item -ItemType Directory -Force $env:TEMP, $env:DOTNET_CLI_HOME | Out-Null
dotnet build source/DeepSeekHarness.csproj -c Release -r win-x64 --self-contained false --artifacts-path "$env:QA_ROOT/build" --output "$env:QA_ROOT/launcher"
dotnet build source/InfoHost/Dafeiyu-Go.Info.csproj -c Release -r win-x64 --self-contained false --artifacts-path "$env:QA_ROOT/build" --output "$env:QA_ROOT/info-host"
```

构建缓存与输出也应放在隔离目录；本仓库的回归入口支持指定 `Output` 或标准 `--artifacts-path`，选择与改动有关的套件即可。连接生命周期：

```powershell
./tests/ConnectionLifecycle.Regression/Run.ps1 -Dotnet (Get-Command dotnet).Source -Output "$env:QA_ROOT/connection"
```

该历史脚本目前要求 `Output` 在 G 盘；其他盘符的贡献者可直接 `dotnet build tests/ConnectionLifecycle.Regression/ConnectionLifecycle.Regression.csproj --artifacts-path "$env:QA_ROOT/lifecycle"`，并在运行生成的 DLL 前把 `CONNECTION_REPO` 指向仓库根目录、`CONNECTION_QA` 指向隔离状态目录。不要把个人磁盘路径写进生产代码。更多模块测试见贡献地图。

## 提交 PR

1. 一次解决一个问题；目录搬迁、行为修改和协议升级尽量分开，让审阅者能看清变化。
2. 写明复现方式、修改后的行为、受影响模块和测试结果；未执行的真实设备或操作系统测试明确标注。
3. 界面 PR 附浅色/深色与 Windows10/Windows11 风格截图，检查运行中切换、进入/退出动画、原生关闭与键盘操作。图标使用已有或微软官方资源。
4. 连接修改不能绕过配对确认、签名、世代/过期检查；连接表示提供 DSH 工作区与对话访问，不表示拷贝工作区内全部文件。DeveloperCenter 公告/心跳协议与 Aries 设备互联协议保持分离。
5. 不提交用户配置、令牌、身份密钥、聊天记录、备份包、构建输出或临时文件。发布、部署和签名不属于普通修复 PR 的默认范围。

新增大功能前先通过项目代理配置查找 GitHub 上成熟可复用的实现，记录选型与许可证。不要虚构维护者或 CODEOWNERS；审阅责任由实际项目维护者分配。
