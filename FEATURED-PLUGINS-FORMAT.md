# 官方推荐插件 JSON 格式

本文只说明官方推荐列表和开发者管理中心“粘贴配置导入”的 JSON 格式。

## 一、两个使用位置

### 1. 仓库推荐列表

文件位置：

```text
featured-plugins.json
```

启动器从仓库 `main` 分支读取这个文件，失败时使用
`%LOCALAPPDATA%\DeepSeekHarness\FeaturedPlugins.json` 本地缓存。

### 2. 开发者管理中心单条导入

在线插件详情页的“复制配置方式”会复制一份单条配置。把这个 JSON 粘贴到：

```text
设置 → 联系五次版本号解锁 → 开发者 → 推荐管理 → 插件推荐
```

的“导入”输入框中即可（也可直接粘贴 `owner/repo` 或仓库地址，由启动器自己去读）。

## 二、根结构

```json
{
  "schemaVersion": 1,
  "updatedAtUtc": "2026-09-22T00:00:00.0000000Z",
  "items": []
}
```

字段说明：

| 字段 | 类型 | 必需 | 说明 |
|------|------|------|------|
| `schemaVersion` | number | 是 | 当前固定为 `1` |
| `updatedAtUtc` | string | 建议 | ISO 8601 UTC 时间 |
| `items` | array | 是 | 推荐条目数组 |

## 三、推荐条目字段

```json
{
  "owner": "Nagi-ovo",
  "repository": "dsh-ads",
  "description": "把 DSH 变成 2005 年门户网站。",
  "language": "TypeScript",
  "license": "MIT",
  "pushedAt": "2026-09-18T23:09:29Z",
  "category": "界面增强",
  "version": "最新",
  "stars": 630,
  "verified": true,
  "defaultBranch": "main",
  "installStatus": "recognized",
  "selectedSpecifier": "npm:@nagi-ovo/dsh-ads",
  "selectedSource": "npm",
  "installCandidates": [
    {
      "source": "npm",
      "target": "@nagi-ovo/dsh-ads",
      "action": "add",
      "specifier": "npm:@nagi-ovo/dsh-ads",
      "executable": true,
      "evidenceSource": "readme"
    }
  ],
  "imageUrl": "https://avatars.githubusercontent.com/u/123456?v=4",
  "note": "复古门户",
  "order": 2
}
```

### 必填字段

| 字段 | 类型 | 说明 |
|------|------|------|
| `owner` | string | GitHub 所有者 |
| `repository` | string | 仓库名，不含 owner |
| `installCandidates` | array | 所有可用安装方式 |
| `note` | string | 官方推荐卡片唯一显示的运营说明 |
| `order` | number | 推荐排序，数字越小越靠前 |

### 展示字段

| 字段 | 类型 | 说明 |
|------|------|------|
| `description` | string | 仓库简介 |
| `language` | string | 主语言 |
| `license` | string | SPDX 名称，例如 `MIT` |
| `pushedAt` | string | 最近提交时间，ISO 8601 |
| `category` | string | 已映射的中文分类 |
| `version` | string | 插件版本；没有正式版本时可为短 SHA 或 `最新` |
| `stars` | number | GitHub Star 数，不能凭空编造 |
| `verified` | boolean | API 的 `validation.overall == "verified"` |
| `defaultBranch` | string | 默认分支 |
| `imageUrl` | string | 优先为 GitHub 所有者头像 |

### 安装字段

| 字段 | 类型 | 说明 |
|------|------|------|
| `installStatus` | string | `recognized`、`ambiguous`、`missing` |
| `selectedSpecifier` | string | 启动默认使用的安装表达式 |
| `selectedSource` | string | 默认安装来源，例如 `github`、`npm` |
| `installCandidates` | array | 全部安装候选，不能只保留一个 |
| `sourceSha` | string | 已验证源码提交，40 位 SHA |

## 四、installCandidates

每个候选对象：

```json
{
  "source": "github",
  "target": "owner/repo",
  "action": "add",
  "specifier": "github:owner/repo#40位提交SHA",
  "executable": true,
  "evidenceSource": "readme"
}
```

字段说明：

| 字段 | 类型 | 说明 |
|------|------|------|
| `source` | string | `github`、`npm`、`readme-command` 等 |
| `target` | string | API 给出的安装目标 |
| `action` | string | 通常为 `add` |
| `specifier` | string | 真正传给 pnpm 的安装表达式 |
| `executable` | boolean | API 是否标记可执行 |
| `evidenceSource` | string | 证据来源，例如 `readme`、`manual` |

规则：

1. 保留 API 返回的全部候选，不要只留 `github:`。
2. 优先使用 `executable: true` 的候选。
3. GitHub 已验证条目优先固定到 40 位 SHA。
4. API 有 npm 候选时，不要擅自改写成 GitHub 候选。
5. 没有 API 候选时，可以手工添加一个候选，并把
   `evidenceSource` 写成 `manual`。

## 五、推荐安装表达式

### GitHub 固定提交

```text
github:owner/repo#0123456789abcdef0123456789abcdef01234567
```

### npm 包

```text
npm:package-name
npm:@scope/package-name
npm:package-name@1.2.3
```

### 仓库子目录

```text
github:owner/repo#plugins/sub-directory
```

## 六、版本提取规则

按以下顺序确定 `version`：

1. npm 候选中的明确版本，例如 `npm:pkg@1.2.3` 得到 `1.2.3`。
2. GitHub 候选中的 tag，例如 `github:owner/repo#v1.4.0`
   得到 `1.4.0`。
3. 只有固定 SHA 时，使用 SHA 前 7 位。
4. 使用 `latest` 安装但没有具体版本时，写 `最新`。
5. 完全无法判断时写 `未知`。

不要把语言、`TypeScript`、`JavaScript` 当作版本。

## 七、verified 和卡片颜色

启动器按 API 的 `validation.overall` 判断验证状态：

- `validation.overall == "verified"`：`verified: true`
- 其它状态：`verified: false`

官方推荐卡片只展示 `note`。在线插件卡片展示：

1. 已验证或未验证，绿色或黄色
2. 分类
3. 版本

## 八、后台单条导入格式

```json
{
  "schemaVersion": 1,
  "plugin": {
    "owner": "xmanrui",
    "repository": "dsh-im",
    "repositoryUrl": "https://github.com/xmanrui/dsh-im",
    "description": "把 IM 机器人接入 DSH。",
    "language": "JavaScript",
    "license": "MIT",
    "pushedAt": "2026-09-20T17:49:50Z",
    "category": "通知通讯",
    "version": "42776b5",
    "stars": 1429,
    "verified": false,
    "defaultBranch": "main",
    "sourceSha": "42776b5beb4b304afbca0c8703d647cb59614df0",
    "installStatus": "recognized",
    "selectedSpecifier": "github:xmanrui/dsh-im#42776b5beb4b304afbca0c8703d647cb59614df0",
    "selectedSource": "github",
    "installCandidates": [
      {
        "source": "github",
        "target": "xmanrui/dsh-im",
        "action": "add",
        "specifier": "github:xmanrui/dsh-im#42776b5beb4b304afbca0c8703d647cb59614df0",
        "executable": true,
        "evidenceSource": "manual"
      }
    ],
    "imageUrl": "https://avatars.githubusercontent.com/u/4094054?v=4",
    "note": "IM 多平台接入"
  }
}
```

后台保存时会自动通过 GitHub API 补齐缺失的：

- `stars`
- `imageUrl`
- `language`
- `defaultBranch`
- `pushedAt`
- `license`

因此手工条目至少应提供 `owner`、`repository`、`note` 和
`installCandidates`。

## 九、完整列表示例

```json
{
  "schemaVersion": 1,
  "updatedAtUtc": "2026-09-22T00:00:00.0000000Z",
  "items": [
    {
      "owner": "Nagi-ovo",
      "repository": "dsh-ads",
      "description": "把 DSH 变成 2005 年门户网站。",
      "language": "TypeScript",
      "license": "MIT",
      "pushedAt": "2026-09-18T23:09:29Z",
      "category": "界面增强",
      "version": "最新",
      "stars": 630,
      "verified": true,
      "defaultBranch": "main",
      "installStatus": "recognized",
      "selectedSpecifier": "npm:@nagi-ovo/dsh-ads",
      "selectedSource": "npm",
      "installCandidates": [
        {
          "source": "npm",
          "target": "@nagi-ovo/dsh-ads",
          "action": "add",
          "specifier": "npm:@nagi-ovo/dsh-ads",
          "executable": true,
          "evidenceSource": "readme"
        }
      ],
      "imageUrl": "https://avatars.githubusercontent.com/u/123456?v=4",
      "note": "复古门户",
      "order": 1
    }
  ]
}
```

## 十、提交前检查

1. JSON 能被标准解析器解析。
2. `schemaVersion` 是数字 `1`。
3. `owner`、`repository`、`note`、`installCandidates` 存在。
4. `order` 不重复并保持预期顺序。
5. `verified` 与 API 一致。
6. `stars` 和 `imageUrl` 是真实数据，不能编造。
7. `selectedSpecifier` 必须存在于 `installCandidates` 中。
8. 不要把 `github:owner/repo` 作为 API 已有 npm 候选时的唯一配置。
