# ADR-006：自包含目录发布 + 机器级程序 + 用户级数据/自启

- 状态：Accepted
- 日期：2026-10-28
- 阶段：P5

## 背景

P5 需要把“脚本 + 构建输出”变成可安装、可卸载、可修复、可诊断的交付物，目标机
Windows 10/11 不应要求预装 .NET 10。安装还涉及管理员权限的 IPP 队列和驱动。

## 决策

### 1. 自包含目录发布（不是单文件）

- `dotnet publish -c Release -r win-x64 --self-contained true`；
- 输出目录 `app/`，不启用 `PublishSingleFile`；
- `PublishTrimmed`/`PublishReadyToRun` 暂不启用（WPF/ASP.NET Core 裁剪风险）；
- 目标机无需 .NET 运行时。

理由：WPF + ASP.NET Core + SQLite 原生库在单文件模式下需要解压/原生库提取，启动、
杀软误报和诊断都更复杂；目录发布更可预测。代价是体积大（约 316 MB，zip 139 MB）。

### 2. 程序机器级，数据用户级

| 项 | 位置 |
|---|---|
| 程序 | `C:\Program Files\PrintPal` |
| 数据 | `%LOCALAPPDATA%\PrintPal` |
| 队列 | 机器级 `PrintPal Virtual Printer` |
| 自启 | 当前用户 HKCU Run |

理由：队列/IPP 驱动需要管理员、是机器级资源；打印记录/价格/路由是用户数据，放
LocalAppData 避免 Program Files 写权限问题，也便于每用户独立。

### 3. 安装器用 PowerShell + 卸载注册表

- `Install-PrintPal.ps1`：复制、队列、驱动、快捷方式、HKCU 自启、卸载项；
- `Uninstall-PrintPal.ps1`：停进程、删队列/端口、自启/快捷方式/卸载项、程序目录；
- `Repair-PrintPal.ps1`：复用 Install 的 `-SkipFileCopy` 修复队列/自启/快捷方式；
- `Diagnose-PrintPal.ps1`：只读报告，`-Zip` 打包日志；
- 写 `HKLM\...\Uninstall\PrintPal`，Apps & Features 可卸载；
- 卸载默认保留 `%LOCALAPPDATA%\PrintPal`，`-RemoveData` 才删除。

### 4. 队列安装

安装器启动已安装的 `PrintPal.App.exe --background`，等待 `/health`，再
`Add-Printer -IppURL`；按名称轮询（API 可能返回空对象）；Win10 先
`Add-PrinterDriver`。

## 后果

正面：

- 目标机零 .NET 依赖；
- 安装/卸载/修复/诊断可脚本化、可审计；
- 程序与数据分离，升级不丢数据；
- 队列是机器级，所有用户可用。

负面：

- 包体积大；P7 可做裁剪/单文件评估；
- 不是 MSI/签名安装包；P7 再做 MSI/代码签名；
- HKCU 自启只覆盖安装用户；多用户需各自启动或管理员配置；
- 数据按用户，不随安装目录迁移。

## 备选方案

| 方案 | 不选原因 |
|---|---|
| 框架依赖发布 | 目标机需 .NET 10 Desktop/ASP.NET Core Runtime |
| 单文件发布 | WPF/ASP.NET Core 原生库提取、启动/杀软风险 |
| MSI/WiX | P5 周期长；PowerShell 已能完成安装/卸载/修复 |
| 程序装 LocalAppData | 队列/IPP 驱动仍需管理员；机器级程序更符合直觉 |
| 数据放 Program Files | 普通用户无写权限；每用户数据隔离差 |

## 验证

- 自包含 App 启动 + `/health`；
- 测试安装目录：复制 → 队列 Normal → 安装版打印/转发 → 修复重建队列 →
  卸载移除队列/程序/自启/快捷方式/卸载项，保留数据；
- 诊断报告/zip 生成；
- Win10 干净环境验收留给 P6（P0 已证明 Win10 队列/驱动路径）。
