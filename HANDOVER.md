# 交接：大肥鱼Go / Dafeiyu-Go Launcher 1.4.9

> `1.4.9` 是品牌过渡版。可见品牌改为“大肥鱼Go / Dafeiyu-Go”，内部可执行文件名、
> 数据目录、注册表键、计划任务、快捷方式和 npm 包名保持不变。迁移边界与发布顺序
> 以 [`TRANSITION.md`](TRANSITION.md) 为准。

> 当前启动器工作版本为 `1.4.9.1`。本文件只描述当前有效状态、操作流程、风险和下一步，
> 不再保留 1.3.x 历史开发记录。

最后更新：2026-09-29

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
