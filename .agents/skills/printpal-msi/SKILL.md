---
name: printpal-msi
description: 构建 PrintPal 发布包与 MSI，安装/升级/修复/卸载并重建 IPP 队列，只读核对部署状态、已装版本与队列绑定。
whenToUse: 需要出 MSI/zip 包、在本机安装/升级/修复/卸载、重建虚拟打印机队列，或核对已安装版本、服务状态、队列绑定时使用。
---

# PrintPal 打包、MSI 与队列

## 安全边界（先读）

- 安装/升级/修复/卸载、重建队列都会**停掉或替换 PrintPal.Service 与队列**，也可能要求关掉 App。
  这属于 `AGENTS.md` 第 1 节约定的**例外**：先向用户说明原因，完成后**立即带测试数据重新启动 App
  并保持运行**。
- **卸载**会删队列、停/删服务、删程序（默认保留 `C:\ProgramData\PrintPal` 数据）——必须用户明确要求才做。
- 任何会改系统的命令（msiexec、Install/Uninstall 队列脚本）都需要管理员；不要静默提权试探。

## 第一步：先看现状（只读，安全）

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  .\.agents\skills\printpal-msi\scripts\Get-PrintPalDeploymentState.ps1
```

输出：仓库/dist 里的 MSI 与 zip、注册表里的已装产品与版本、`C:\Program Files\PrintPal` 文件版本、
服务状态/启动类型/路径、队列（名称/驱动/端口/WSD/状态/任务数）、队列 UUID 绑定结果、
App/Service/FakePrinter 进程、`/health`、数据目录与数据库大小，最后给出针对性提示。

## 构建

```powershell
# 发布 app+service、构建 MSI、打 zip（一条命令）
.\scripts\publish\Publish-PrintPal.ps1 -NoFakePrinter -Zip

# 只发布，不出 MSI
.\scripts\publish\Publish-PrintPal.ps1 -NoFakePrinter -NoMsi

# 指定版本（版本唯一来源是 Directory.Build.props 的 PrintPalVersion）
.\scripts\publish\Publish-PrintPal.ps1 -Version 1.0.3 -NoFakePrinter -Zip

# 只构建 MSI（App/Service 已发布到 dist 时）
dotnet build .\packaging\PrintPal.Msi\PrintPal.Msi.wixproj -c Release -p:PrintPalVersion=1.0.3
.\Build-Installer.cmd
```

产物：`dist\PrintPal-<version>-win-x64\PrintPal-<version>-x64.msi`（约 72.5 MB，未签名）与同名 zip。
日常 `Debug`/`Release` 配置不构建 MSI；`-c Installer` 才是专用配置。

## 安装 / 升级 / 修复 / 卸载

```powershell
# 图形向导（人工验证推荐；第一步选语言，中文/English，默认中文）
msiexec.exe /i .\dist\PrintPal-1.0.3-win-x64\PrintPal-1.0.3-x64.msi

# 静默安装
msiexec.exe /i .\dist\PrintPal-1.0.3-win-x64\PrintPal-1.0.3-x64.msi /qn /norestart

# 修复（队列/服务/程序被删坏时）
msiexec.exe /f .\dist\PrintPal-1.0.3-win-x64\PrintPal-1.0.3-x64.msi /qn

# 卸载（默认保留数据）
msiexec.exe /x .\dist\PrintPal-1.0.3-win-x64\PrintPal-1.0.3-x64.msi /qn /norestart

# 记日志（排查安装失败必加）
msiexec.exe /i <msi> /qn /norestart /l*v .\artifacts\msi-install.log
```

- 固定 `UpgradeCode` + `MajorUpgrade`，升级会替换服务/队列/程序并保留数据；
- 安装失败（如 1603）会走 `RollbackQueue` 回滚，数据保留；
- MSI **未签名**，SmartScreen 可能提示「未知发布者」，选「仍要运行」即可。

### 中文向导与失败诊断（1.0.1 起）

页面顺序：**欢迎 → 许可（勾选后才能下一步）→ 安装目录（可浏览）→ 确认 → 进度 → 完成**。
`packaging\PrintPal.Msi\Ui.wxs` 自绘中文单语；`Package` 里 `Language="2052" Codepage="936"`
是中文能进 MSI 的前提（否则 WIX0311）。

- 进度页的「当前步骤」来自 `ProgressText`（ActionText 表）：改文案就改 `Ui.wxs` 里那组
  `ProgressText Action="InstallFiles" Message="复制程序文件"` 之类的行；
- 失败页 `FatalError` 显示 `[MsiLogFileLocation]`（`Property MsiLogging=voicewarmupx` 保证有日志，
  路径放在可复制的 Edit 里）+ 队列日志 `C:\ProgramData\PrintPal\msi-queue.log`；
  两个按钮：「复制到桌面」（`CopyMsiLog` CA → 桌面 `PrintPal-InstallLog.txt`）和
  「打开日志文件夹」（`OpenLogFolder` CA → `explorer /select`）。
- 让用户点「复制到桌面」再把桌面那个文件和 `msi-queue.log` 发回来即可，不用敲命令。
- **队列 CA 用 `Impersonate="no"`（SYSTEM）**；注意：SYSTEM 下 `Add-Printer -IppURL` 仍可能
  报「访问被拒绝」（本机 2026-11-06 实测），所以脚本已改成：等异步删除完成 → 清理无主的
  `WSD-*` 端口 → 拿不到 WSD 端口时退回 HTTP 端口队列并写 WARNING（不再 1603 回滚）。
- **UI 按钮不要用 WixQuietExec**（即时 CA 起不到进程，日志里只有 "Doing action"）；
  打开文件/文件夹用 `WixShellExec`（`WixShellExecTarget` 属性）。
- 失败日志：回滚动作 `CopyLogOnFailure` 会把日志复制到公共桌面的
  `PrintPal-InstallLog.txt`；失败页也有可复制的路径和两个 `WixShellExec` 按钮。
- 进度条：`ProgressBar` 必须带 `<Subscribe Event="SetProgress" Attribute="Progress" />`，
  否则条一直是灰的；**"到头又从头"是 Windows Installer 内部 UI 的多趟进度，改不了**
  （官方答复：只能外部 UI handler / Burn）。
- 产品图标：`Control Type="Icon"` 的 `Text` 指向 **Binary**（不是 Icon 表），
  所以要先 `Binary Id="PrintPalProductIcon" SourceFile="PrintPal.ico"`。
- 卸载/修复：`Installed` 时要有 `MaintenanceDlg`，`REMOVE~="ALL"` 时要有 `RemoveDlg`，
  否则卸载会走安装向导；完成页用 `PRINTPAL_ACTION` 区分安装/卸载文案。
- 卸载删队列：`Remove-Printer` 失败要 `printui.dll,PrintUIEntry /dl` 兜底，并等异步删除完成。

```powershell
# 预勾许可，直接翻页看后面的页面（公开属性必须全大写）
msiexec.exe /i <msi> LICENSEACCEPTED=1
```

验证方式：跑 `/i` 后逐页截图（存 `artifacts\ui-check\`），在「确认」页**不要按回车**（会开始安装），
改按 Esc → 取消 → 是 → 完成退出；`msiexec /i` 只走 UI 不落盘，取消后注册表/服务不变。

## 队列（IPP 虚拟打印机）

```powershell
# 安装/重建队列（管理员；会清理旧队列、装 IPP Class Driver、强制 WSD 端口）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install-PrintPalQueue.ps1

# 卸载队列（管理员）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Uninstall-PrintPalQueue.ps1

# UUID 绑定检查（只读）：0 MATCH / 1 MISMATCH / 2 无法检查
.\scripts\Get-PrintPalQueueBinding.ps1
```

- 队列**必须是 WSD 端口**；HTTP 端口会丢失 `copies` / `sides` / `print-color-mode`；
- Win10 可能要先 `Add-PrinterDriver -Name 'Microsoft IPP Class Driver'`；
- 队列与 Agent 的 `printerUuid` 不一致时，任务在 spooler 就失败，`app.log` 里**没有 JOB**（A1）。

## 安装 E2E 验收清单

1. `Get-Service PrintPal.Service` → `Running`；`Get-Printer 'PrintPal Virtual Printer'` → `Normal`；
2. 造一条真实任务（需要 FakePrinter 在线或改成真实打印机）：
   `PrintProbe --pages 5 --copies 2 --duplex long --color color --paper A4` → App **¥6.40** → FakePrinter
   收到 5 页 PDF；
3. 升级后：版本号变化、数据保留、队列仍 `Normal`；
4. 修复后：被删的 `PrintPal.App.dll` 恢复、服务/队列可用；
5. 卸载后：队列与服务移除、程序目录清理、`C:\ProgramData\PrintPal` 数据保留。

## 常见故障映射

| 现象 | 去处 |
|---|---|
| 打印报错、`app.log` 无 JOB | A1（UUID 绑定）/ A2（队列 Error） |
| 队列 Error、任务 Retained | A2 |
| 队列不存在 / 端口不是 WSD / 驱动缺失 | A3，重跑 `Install-PrintPalQueue.ps1` 或 MSI 修复 |
| 8630 被占用、App 起不来 | A4 |
| 服务在跑但 UI 没数据 | A5 |
| MSI 安装失败 1603 | 看 `/l*v` 日志，队列安装失败会回滚 |

## 收尾

- 按 `AGENTS.md`：安装/升级后**立即启动 App 并保持运行**（`printpal-dev-env` skill 可一键拉起）；
- 汇报：已装版本、服务状态、队列状态、App 是否在运行、数据是否保留。

## 相关文档

- `docs\p7-service-msi.md`、`docs\installer.md`、`docs\runbook.md`
- `docs\troubleshooting.md` A1–A6
