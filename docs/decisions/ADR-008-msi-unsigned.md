# ADR-008：WiX 5 MSI，先不购买代码签名证书

- 状态：Accepted
- 日期：2026-10-28
- 阶段：P7.2

## 背景

P5 安装器是 PowerShell + 自包含目录。企业/渠道希望 MSI；代码签名证书需要成本/采购周期。
用户明确：**没有资金买证书，能实现 MSI 就先实现**。

## 决策

- 用 **WiX Toolset 5.0.2** 构建 per-machine MSI；
- 安装 app/service/installer；注册 Windows 服务；创建 WSD IPP 队列；公共快捷方式；
- 数据目录 `%ProgramData%\PrintPal`，Permanent，卸载保留；
- MSI **不签名**，构建/安装/卸载均可用；
- 文档明确 SmartScreen/未知发布者风险；
- 后续有证书时，仅增加签名步骤，不改 MSI 结构。

## 关键实现

- 服务用 `ServiceInstall`/`ServiceControl`；
- App/Service 发布到同一目录，共享运行时；
- 队列 CA 用 `WixQuietExec` + `Impersonate="yes"`，以安装用户身份创建 WSD 队列；
- 数据 ACL 由服务启动时 `icacls` 设置；
- 快捷方式由队列后置脚本创建（公共开始菜单/桌面），避免 MSI per-user 快捷方式 ICE；
- **WixUI_InstallDir** 安装向导（欢迎/许可/目录/确认/进度/完成）；
- 版本唯一来源 `Directory.Build.props`；MSI 路径/版本全部动态；
- `RollbackQueue` CA 在失败时清理队列；
- MSI 1.0.1；WiX SDK NuGet 还原，无需单独安装 WiX。

## 后果

正面：

- 不需要证书即可交付 MSI；
- 支持 `msiexec /i /qn`、Apps & Features 卸载；
- 服务/队列/数据/快捷方式一次安装；
- 后续签名只需在 CI 加 `signtool`。

负面：

- 未签名，下载分发可能被 SmartScreen 拦；
- 无发布者信任，企业可能要求签名；
- 队列 CA 依赖 impersonation/用户管理员权限；
- 升级失败后恢复旧版本未完整回归；
- 版本由 `Directory.Build.props` 决定，CI 必须传 `PrintPalVersion`；

## 备选方案

| 方案 | 不选原因 |
|---|---|
| 继续 PowerShell 安装器 | 企业分发/标准卸载弱 |
| 自签名证书 | 不能建立公共信任，仍会警告 |
| 购买 OV/EV 证书 | 用户当前无预算；可后补 |
| Inno Setup | 不是 MSI；企业仍偏好 MSI |

## 验证

- `msiexec /i` 安装；服务 Running；队列 WSD；PrintProbe ¥6.40；
- WixUI 向导（Welcome/License/InstallDir/Verify/Progress/Exit）；
- 0.6.0 → 1.0.1 升级；`/f` 修复；无效队列 URL 触发回滚；
- `msiexec /x` 卸载；服务/队列/程序/快捷方式移除，数据保留；
- `Get-AuthenticodeSignature` 为 `NotSigned`（预期）。
