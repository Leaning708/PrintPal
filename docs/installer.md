# PrintPal 安装、卸载与诊断（P5）

> 更新：2026-10-28
> 适用：Windows 10 22H2+ / Windows 11 x64
> 安装包由 `scripts/publish/Publish-PrintPal.ps1` 生成，自包含 .NET 运行时，无需目标机安装 .NET。
> 当前版本：1.0.1；目录约 279 MB（App/Service 共享运行时）。

---

## 0. P7：Windows 服务 + MSI（推荐）

P7 新增 **机器级 MSI** 和 **Windows 服务**，APP 关闭/未登录也能收任务。

```powershell
# 安装（管理员）
msiexec.exe /i .\dist\PrintPal-1.0.1-win-x64\PrintPal-1.0.1-x64.msi /qn /norestart

# 卸载
msiexec.exe /x .\dist\PrintPal-1.0.1-win-x64\PrintPal-1.0.1-x64.msi /qn /norestart

# 修复
msiexec.exe /f .\dist\PrintPal-1.0.1-win-x64\PrintPal-1.0.1-x64.msi /qn
```

普通双击/不带 `/qn` 安装会显示 **WixUI_InstallDir 向导**：欢迎 → 许可 → 安装目录 → 确认 → 进度 → 完成。
升级直接安装更高版本 MSI（同 UpgradeCode）；已回归 0.6.0→1.0.1。队列安装失败时 MSI 回滚服务/程序/队列，数据保留。

| 项 | 路径/值 |
|---|---|
| 程序 | `C:\Program Files\PrintPal`（App/Service 同目录） |
| 服务 | `PrintPal.Service`（LocalSystem，自动） |
| 数据 | `C:\ProgramData\PrintPal`（卸载保留） |
| 队列 | `PrintPal Virtual Printer`（必须是 WSD IPP 端口） |
| 快捷方式 | 公共开始菜单 + 公共桌面 |
| 健康 | `http://127.0.0.1:8630/health` |

**无证书**：MSI 未签名，本机/内网可正常安装；浏览器/邮件分发可能显示 SmartScreen
“未知发布者”。不需要购买证书即可使用；签名是后续信任增强。

兼容说明：

- 旧 PowerShell 安装器（§2–§6）仍可用，但它启动的是 **便携/进程内 Agent**，不是服务；
- MSI 使用 WixUI_InstallDir：欢迎/许可/安装目录/确认/进度/完成；
- 快捷方式写入公共桌面、当前用户桌面和开始菜单，目标使用实际安装目录。
- 服务模式的数据在 `%ProgramData%\PrintPal`，便携模式在 `%LOCALAPPDATA%\PrintPal`；
- 两种模式不要同时运行在同一端口。

完整 P7 说明见 [p7-service-msi.md](p7-service-msi.md)、
[ADR-007](decisions/ADR-007-windows-service.md)、
[ADR-008](decisions/ADR-008-msi-unsigned.md)。

---

## 1. 便携安装包结构

```
PrintPal-1.0.1-win-x64/
  app/                         # App + Service 共享运行时（PrintPal.App.exe / PrintPal.Service.exe）
  fake-printer/                # 测试用 FakePrinter（可选，发布时 -NoFakePrinter 可省）
  installer/
    PrintPal.Common.psm1
    Install-PrintPal.ps1
    Uninstall-PrintPal.ps1
    Repair-PrintPal.ps1
    Diagnose-PrintPal.ps1
    PrintPal.MsiQueue.ps1
    PrintPal.MsiQueueRemove.ps1
  docs/
    README.md
    installer.md
    architecture.md
    runbook.md
    p7-service-msi.md
    ui.md
    CHANGELOG.md
  PrintPal-1.0.1-x64.msi       # 机器级 MSI，WixUI 向导（未签名）
  Install-PrintPal.cmd
  Uninstall-PrintPal.cmd
  VERSION.txt
  publish-info.json
```

---

## 2. 安装

双击 `Install-PrintPal.cmd`，或管理员 PowerShell：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\installer\Install-PrintPal.ps1
```

安装器会：

1. 请求管理员权限（未提权时自动 UAC）；
2. 复制 `app\` 到 `C:\Program Files\PrintPal`；
3. 启动 `PrintPal.App.exe --background`（8630）；
4. 安装 Microsoft IPP Class Driver（Win10 必需）；
5. 创建 `PrintPal Virtual Printer` IPP 队列；
6. 创建开始菜单/桌面快捷方式；
7. 写入当前用户 HKCU Run 自启；
8. 写入卸载注册表项（Apps & Features）；
9. 写 `install.json` 和安装日志。

常用参数：

```powershell
# 只安装文件，不建队列/不自启/不建快捷方式（测试/打包）
.\installer\Install-PrintPal.ps1 -NoStart -SkipQueue -SkipAutostart -NoShortcuts

# 自定义安装目录（测试）
.\installer\Install-PrintPal.ps1 -InstallDir C:\Temp\PrintPal -SkipAutostart -NoShortcuts

# 只修复队列（App 已在运行）
.\installer\Install-PrintPal.ps1 -SkipFileCopy -SkipAutostart -NoShortcuts -NoStart

# 静默
.\installer\Install-PrintPal.ps1 -Quiet
```

参数：`-InstallDir`、`-Source`、`-QueueName`、`-Port`、`-NoStart`、`-SkipQueue`、
`-SkipAutostart`、`-NoShortcuts`、`-SkipFileCopy`、`-Quiet`。

---

## 3. 路径与数据

| 项 | 路径 |
|---|---|
| 程序 | `C:\Program Files\PrintPal`（App/Service 同目录） |
| 数据（默认） | `%LOCALAPPDATA%\PrintPal` |
| 数据库 | `%LOCALAPPDATA%\PrintPal\printpal.db` |
| 文档 | `%LOCALAPPDATA%\PrintPal\documents\` |
| 日志 | `%LOCALAPPDATA%\PrintPal\logs\app.log` |
| 安装日志 | `%ProgramData%\PrintPal\install.log` |
| 队列 | `PrintPal Virtual Printer` |
| IPP | `http://127.0.0.1:8630/ipp/print` |

数据按用户隔离；安装目录是机器级。默认 **卸载保留数据**，`-RemoveData` 才删除。

---

## 4. 卸载

双击 `Uninstall-PrintPal.cmd`，或：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\installer\Uninstall-PrintPal.ps1
```

卸载器会：

1. 停止 `PrintPal.App`；
2. 删除队列和 IPP 端口；
3. 删除 HKCU 自启、快捷方式、卸载注册表项；
4. 删除 `C:\Program Files\PrintPal`；
5. 默认保留 `%LOCALAPPDATA%\PrintPal`（数据/日志）。

删除数据：

```powershell
.\installer\Uninstall-PrintPal.ps1 -RemoveData
```

也可以从“设置 → 应用 → 已安装的应用 → PrintPal”卸载（注册表 UninstallString）。

---

## 5. 修复

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File "C:\Program Files\PrintPal\installer\Repair-PrintPal.ps1"
```

修复会重新创建队列、自启、快捷方式和卸载项；程序文件缺失时：

```powershell
.\installer\Repair-PrintPal.ps1 -Source .\app
```

参数：`-InstallDir`、`-Source`、`-QueueName`、`-Port`、`-SkipAutostart`、`-NoShortcuts`、`-NoStart`、`-Quiet`。

---

## 6. 诊断

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File "C:\Program Files\PrintPal\installer\Diagnose-PrintPal.ps1" -Zip
```

生成：

```
%ProgramData%\PrintPal\diagnostics-<timestamp>.txt(.zip)
```

包含：OS、管理员、安装清单、App 版本/SHA、进程、健康、驱动、队列、打印作业、自启、
端口、数据目录、文档数量、`app.log`/`install.log` 末尾。

---

## 7. Windows 10

- 安装器会自动 `Add-PrinterDriver -Name 'Microsoft IPP Class Driver'`；
- 驱动 INF 在 DriverStore 中但被 `ExcludeFromSelect` 隐藏，必须显式安装；
- `Add-Printer -IppURL` 可能返回空对象，安装器按名称轮询；
- 自包含包不要求目标机安装 .NET 10。

---

## 8. 升级

1. 退出 PrintPal（托盘“退出”）；
2. 运行新包 `Install-PrintPal.ps1`；
3. 安装器停止旧进程、覆盖程序文件、重建队列、保留数据；
4. 版本以 `VERSION.txt` / `install.json` 为准。

---

## 9. 已知限制（P5）

- 自启写入当前用户 HKCU；多用户机器需各用户首次启动或管理员另行配置。
- 数据默认按用户，不随安装目录迁移。
- 安装器为 PowerShell（自包含 App，不是 MSI）；MSI/签名在 P7。
- 卸载不会删除用户数据，除非显式 `-RemoveData`。
- 自包含包约 316 MB（zip 139 MB）；P7 再评估裁剪/单文件。
- P5 在主机完成安装/卸载/修复验证；Win10 干净环境验收在 P6。



