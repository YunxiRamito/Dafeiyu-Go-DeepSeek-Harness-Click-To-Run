# 代码签名 / Code signing

## 现状:不签名(2026-09-27 起)

**SignPath Foundation 的免费签名申请未通过。** 所以官方构建**不带数字签名**,
发布说明里也不再提"等待签名审批"这类话。

对用户的影响和处理办法:

- 首次运行会弹「Windows 已保护你的电脑」(SmartScreen) —— 这是未签名软件的**预期行为**,
  不代表文件有害:点「更多信息」→「仍要运行」即可;或者右键 exe →「属性」→ 勾「解除锁定」。
- 想先核对再运行:对比 Release 说明里给出的 SHA256。

**Official builds are unsigned.** The free signing application to SignPath
Foundation was declined, so releases carry no Authenticode signature. SmartScreen
will warn on first run; that is expected for unsigned software. Verify the file
against the SHA256 in the release notes before running if you prefer.

## 当前本机发版流程（2026-10-13）

- 用户要求启动器改为本机回归、构建与发布；GitHub workflow 只保留手动验证，不再由 tag 自动发 npm / Release，也不执行 SignPath。
- 当前本机包仍未签名。ZIP 和下载哈希校验确认分发内容一致，不代表 Authenticode 签名。
- 下方 SignPath 配置仅为历史参考，不能仅配置变量就声称本机包已签名；重新接入签名服务时需明确实现签名及验证步骤。

## 将来要签名的话

- 可选:SignPath 付费项目 / Azure Trusted Signing / 其他提供 Authenticode + 时间戳的服务。
- 需要:代码签名证书(EV 或 OV)、CI 变量与密钥、以及"内部文件与整包各签一次"的顺序。
- 唯一不能破的规矩:**签名是打包的最后一环**,签完不要再改动任何字节,否则签名失效。

## 附:原 SignPath 配置说明(仅供将来参考)

1. Apply at <https://signpath.org/apply>.
2. Create a SignPath project for this repository.
3. Link the project to the predefined **GitHub.com** trusted build system.
4. Create an artifact configuration named `launcher-release` using
   [`.signpath/launcher-artifact-configuration.xml`](.signpath/launcher-artifact-configuration.xml).
5. Create a release signing policy, for example `release-signing`.
6. Create an API token for a user that can submit requests to this project.

Repository variables: `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG`,
`SIGNPATH_SIGNING_POLICY_SLUG`, `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG`; secret:
`SIGNPATH_API_TOKEN`.
