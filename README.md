# 大肥鱼Go / Dafeiyu-Go

**面向 [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) 的 Windows 安装、启动与管理工具。**
一句话:双击一下,DSH 服务就起来了,托盘里住着,余额随时看。

<sub>Dafeiyu-Go · Windows 托盘启动器 · 一键起服务 · 原生菜单 · 余额与告警 · 中英双语</sub>

> **1.4.9 过渡版**：界面和发布仓库先切换为“大肥鱼Go / Dafeiyu-Go”，
> 但 `DeepSeek Harness.exe`、数据目录、注册表键、计划任务和 npm 包名保持不变。
> 详细兼容边界见 [`TRANSITION.md`](TRANSITION.md)。

---

## 这是什么

DSH 本体是个跑在本机的 Web 服务(`127.0.0.1:8787`),平时要么开个终端敲命令,要么写批处理。

这个启动器把它变成一个正常 Windows 程序:

- **双击就起**:自动找 DSH 目录和 node,拉起服务,托盘常驻
- **提权启动**:以管理员运行,DSH 服务也随之拿到管理员令牌
- **原生托盘菜单**:云母背景、Segoe Fluent 图标、Win11 加载动画
- **余额显示与告警**:托盘菜单顶部直接看余额,到阈值弹通知
- **API Key 管理**:内置设置窗,DPAPI 加密存在本机
- **开机自启**:菜单里一键开关,静默驻留托盘,不弹浏览器
- **Windows 通知**:起停、失败、余额告警都走系统原生气泡

---

## 下载与安装

### 推荐:用安装程序

**DSH Installer** 会帮你检测环境、补 Node、装 DSH 本体、再把这个启动器铺好,
连桌面快捷方式和开机自启都一起配了。去那边仓库看说明就行。

### 手动:直接下 zip

1. 到 [Releases](https://github.com/YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Click-To-Run/releases) 下最新的
   `DeepSeekHarness-<版本>.zip`
2. 解压到 **DSH 根目录下** 的 `DeepSeek Harness\` 里,变成这样:

   ```
   <你的 DSH 目录>\
   ├─ node_modules\@deepseek-ai\dsh\...     ← DSH 本体
   ├─ logs\
   └─ DeepSeek Harness\                      ← 解压到这里
      ├─ DeepSeek Harness.exe
      └─ DeepSeek Harness.Core.exe
   ```

3. 双击 `DeepSeek Harness.exe`,同意一次 UAC

> 启动器会自己找 DSH 目录:先看环境变量 `DSH_ROOT`,再看同目录的 `launcher.json`,
> 再从自己所在目录往上找 `node_modules\@deepseek-ai\dsh\lib\bin.js`。
> 所以只要放在 DSH 根目录下面一层,它就能自己认出来。
> 想手动指定,就在同目录放个 `launcher.json`:
> `{ "dshRoot": "D:\\DSH", "nodePath": "C:\\Program Files\\nodejs\\node.exe" }`

---

## 系统要求

| 项 | 要求 |
|----|------|
| 系统 | **Windows 10 1809(build 17763)及以上**;Win11 全系 |
| 运行库 | **.NET 8 桌面运行时** + **Windows App Runtime 1.8** |
| 其他 | 本机有一份能跑的 DSH(含 `node_modules\@deepseek-ai\dsh`)和 node |

WinUI 3 的硬门槛就是 1809,低于这个版本起不来 —— 安装程序会直接劝退。

> **已经在 Win10 1809(build 17763)的干净虚拟机上实测通过**:便携 Node + npm 装 DSH 本体 +
> 启动器起服务 + 拿到带 token 的地址(HTTP 200)。Win11 上当然也没问题。

---

## 怎么用

托盘图标右键,出来的是自绘菜单:

```
余额：¥xx.xx              ← 点它改 API Key
────────────────
打开页面                   ← 浏览器打开 127.0.0.1:8787
重启 DSH 服务
开机自启动  ✓
────────────────
强行终止
退出
```

- **单击托盘图标 / 双击**:直接打开页面
- **余额**:启动时、每次开菜单、每 60 秒各刷一次
- **告警阈值**(可改):今日花费 ¥15 / 余额 ¥10 / ¥5 / ¥1
- **开机自启**:写 Run 键 + 启动文件夹,并带 `--no-browser`,开机只驻留托盘

### 命令行参数

| 参数 | 作用 |
|------|------|
| `--no-browser` / `--silent` / `--tray` / `--startup` | 静默自启:起服务、驻留托盘,但不自动弹浏览器 |

---

## 常见问题

**双击没反应?**
先看有没有弹 UAC —— 启动器清单里声明了 `requireAdministrator`,拒绝提权就起不来。
如果弹的是「没有找到 Node.js」或「没有找到 DeepSeek Harness 本体」,那就是环境没配好,
对话框里会写明该怎么办。日志在 `%LOCALAPPDATA%\DeepSeekHarness\launcher.log`。

**托盘有图标,但"打开页面"打不开 / 显示 `dsh web authentication required`?**
DSH 的网页地址带一个访问 token,而且这个地址是服务起来之后过几秒才写出来的。
启动器会等它(最多 25 秒),拿不到就不会自动开页面;你点「打开页面」时会再读一次。
如果还是提示拿不到地址,用「重启 DSH 服务」重来一次,或稍等几秒再点。
想手动确认:`type <你的DSH目录>\logs\last-url.txt`,把那一整行(带 `?token=`)贴进浏览器。

**托盘右键菜单里的图标是空心方框?**
那是字体缺失。菜单图标优先用 Windows 11 的 `Segoe Fluent Icons`,系统里没有就自动退到
Windows 10 自带的 `Segoe MDL2 Assets`。两套字体的字形是兼容的,理论上不会缺图标 ——
如果你确实看到方框,把 `%LOCALAPPDATA%\DeepSeekHarness\launcher.log` 发过来。

**提示 SmartScreen 已阻止?**
没做代码签名,首次运行会拦一下:点"更多信息" → "仍要运行"。

**换了 DSH 目录就不认了?**
删掉 `%LOCALAPPDATA%\DeepSeekHarness\launcher-path.txt`,或在 `launcher.json` 里写明 `dshRoot`。

**和 RTSS / MSI Afterburner 冲突?**
这俩会往图形进程里注入钩子,已知会让托盘菜单偶尔闪烁。游戏时无所谓,平时可以关掉注入。

---

## 从源码构建

```powershell
# 需要 .NET 8 SDK + Windows App SDK 1.8
cd source
.\build-winui.ps1 -OutputDirectory .\dist-1.3.10

# 外层引导程序(.NET Framework,声明 requireAdministrator)
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:winexe /platform:x64 /optimize+ `
  "/win32icon:.\DeepSeekHarness.ico" "/win32manifest:.\RuntimeBootstrap.manifest" `
  "/out:.\dist-1.3.10\DeepSeek Harness.exe" .\RuntimeBootstrap.cs
```

一键发版(编译 + 打包 + 算哈希 + 更新 `manifest.json`):

```powershell
.\release.ps1
```

产物自检(查关键文件、运行库、有没有写死开发机路径、有没有 `.old` 残留):

```powershell
.\verify.ps1
```

发版完整流程见 [`RELEASE.md`](RELEASE.md)。

---

## 目录结构

```
DeepSeek Starter\
├─ source\
│  ├─ WinUIProgram.cs        主程序:托盘、菜单、服务管理、余额(全在这)
│  ├─ LauncherLocator.cs     找 DSH 根目录和 node.exe
│  ├─ BalanceSupport.cs      余额查询与 API Key 存储
│  ├─ BalanceAlerts.cs       余额告警阈值
│  ├─ RuntimeBootstrap.cs    外层引导程序(检查运行库后拉起主程序)
│  ├─ build-winui.ps1        构建脚本
│  └─ Program.cs             旧 WinForms 版尸体,不参与编译
├─ manifest.json             给 DSH Installer 读的发布清单
├─ release.ps1               一键发版
├─ verify.ps1                产物自检
└─ RELEASE.md                发版与分发说明
```

---

## 与其他项目的关系

| 项目 | 关系 |
|------|------|
| [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) | 被启动的本体 |
| **Dafeiyu-Go Setup** | 安装程序,读本仓库的 `manifest.json` 下载启动器并部署 |
| RivaTuner / RTSS | 无关系,但注入图形钩子时可能互相影响 |

启动器不打包进安装程序,安装程序每次现下最新版 —— 所以**这个仓库发新版,装机的人立刻就能拿到**。

---

## English

**A Windows tray launcher for [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness).**
Double-click once, the DSH web service comes up and lives in your tray with your balance on hand.

### Features

- One-click start: finds your DSH folder and node automatically, then launches the service
- Runs elevated (the DSH service inherits the admin token)
- Native tray menu: Mica backdrop, Segoe Fluent icons, Win11 loading animation
- Balance display with threshold alerts
- API key stored locally with DPAPI
- Toggle "start with Windows" right from the menu (silent, no browser popup)

### Requirements

- **Windows 10 1809 (build 17763) or later** — WinUI 3 hard limit
- **.NET 8 Desktop Runtime** and **Windows App Runtime 1.8**
- A working local DSH install plus node

### Install

Grab `DeepSeekHarness-<version>.zip` from
[Releases](https://github.com/YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Click-To-Run/releases) and extract it into
`<your DSH root>\DeepSeek Harness\`, then run `DeepSeek Harness.exe`.

The launcher locates DSH by checking `DSH_ROOT`, then a sibling `launcher.json`,
then walking up from its own folder looking for `node_modules\@deepseek-ai\dsh\lib\bin.js`.

### Menu

```
Balance: ¥xx.xx        (click to set your API key)
────────────
Open page
Restart DSH service
Start with Windows  ✓
────────────
Force stop
Quit
```

### Command line

| Argument | Effect |
|----------|--------|
| `--no-browser` / `--silent` / `--tray` / `--startup` | Start the service and stay in the tray without opening a browser |

### Build

```powershell
cd source
.\build-winui.ps1 -OutputDirectory .\dist-1.3.10
```

Release automation: `.\release.ps1`. Artifact self-check: `.\verify.ps1`.
See [`RELEASE.md`](RELEASE.md) for the full release flow.

### License

MIT

---

## Code signing policy

**代码签名政策**

**本项目目前不做代码签名。** SignPath Foundation 的免费签名申请未通过，官方构建不带数字签名；SmartScreen 的提示请按上一节的办法过掉。详见 [SIGNING.md](SIGNING.md)。

This project currently ships **unsigned** builds. The free signing application to SignPath Foundation was declined, so official builds carry no digital signature; see [SIGNING.md](SIGNING.md).

- Committers and reviewers / 提交与审查: [YunxiRamito](https://github.com/YunxiRamito)
- Approvers / 批准人: [YunxiRamito](https://github.com/YunxiRamito)

**Privacy policy / 隐私政策**: 本程序不收集、不上传用户数据。启动器只在你配置了 API Key 之后,用你自己的 Key 向 DeepSeek 官方 API 查询账户余额;更新检查只发起匿名下载请求。

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.
