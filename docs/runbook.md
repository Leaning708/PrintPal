# PrintPal 运行手册（Runbook）

> 更新：2026-10-28（P7 服务/MSI）
> 适用于开发机与 Windows 10/11 验证机。

---

## 1. 前置条件

| 项 | 要求 |
|---|---|
| Windows | Windows 10 22H2+ / Windows 11 |
| .NET | .NET 10 SDK（开发/构建）；发布后为自包含 |
| 权限 | 安装/卸载队列需要管理员（UAC） |
| 驱动 | Microsoft IPP Class Driver（Win10 需显式 `Add-PrinterDriver`） |
| 网络 | 本机回环；FakePrinter 默认只监听 127.0.0.1 |

Windows 10 如果提示“禁止运行脚本”，用：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File <script.ps1>
```

---

## 2. 构建与测试

```powershell
cd C:\Develop\code\AGI\PrintPal
dotnet build PrintPal.sln -c Debug
dotnet test PrintPal.sln -c Debug
```

当前测试：Core 57 / Ipp 11 / Capture 10 / Storage 6 / Integration 14 = **98**。

---

## 3. P2/P3 演示（本机 FakePrinter）

### 3.1 启动三台模拟打印机

```powershell
.\scripts\Start-FakePrinters.ps1
```

会启动：

| 名称 | IPP | Raw | 说明 |
|---|---|---|---|
| Fake-A4-Color | 8631 | 9101 | A4，支持黑白/彩色 |
| Fake-A4-Mono | 8632 | 9102 | A4，黑白 |
| Fake-A3 | 8633 | 9103 | A3/A4 |

日志：`artifacts\p2\fake-printers\<name>.out.log`；收到的文档：
`artifacts\p2\fake-printers\received\<name>\`。

### 3.2 启动 PrintPal Agent / WPF App

**P2 无界面宿主：**

```powershell
.\scripts\Start-PrintPalAgent.ps1
```

**P3 WPF App（推荐，进程内托管 Agent）：**

```powershell
.\scripts\Start-PrintPalApp.ps1 -SeedDemo
```

默认：

- IPP：`ipp://127.0.0.1:8630/ipp/print`
- P2 数据：`artifacts\p2\data`
- P3 数据：`artifacts\p3\data`
- 自动转发：开
- 种子：3 台 FakePrinter + 3 条路由 + 默认价格表

P2 日志：`artifacts\p2\data\agent.out.log` / `agent.err.log`。
P3 日志：`artifacts\p3\data\logs\app.log`。

P3 界面说明见 [ui.md](ui.md)。

### 3.3 安装虚拟打印机队列（管理员）

在**管理员 PowerShell**中：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Install-PrintPalQueue.ps1
```

脚本会：

1. 检查 Agent `/health`；
2. 检查/安装 Microsoft IPP Class Driver；
3. 创建 “PrintPal Virtual Printer” 队列；
4. 按名称轮询（`Add-Printer -IppURL` 可能返回空对象）；
5. `Get-Printer` 验证。

### 3.4 打印验证

用 P0 的可控打印工具（5 页 × 2 份，A4 彩色双面）：

```powershell
.\tools\PrintPal.PrintProbe\bin\Debug\net10.0-windows\PrintPal.PrintProbe.exe `
  --printer "PrintPal Virtual Printer" `
  --pages 5 --copies 2 --duplex long --color color --paper A4 `
  --name "P2 E2E 5p x2 duplex color A4"
```

预期：

- Agent 日志：
  `JOB ... 5p x2 A4 Color TwoSidedLongEdge = ¥6.40 -> Fake-A4-Color [Succeeded]`
- FakePrinter 日志出现 JSON：`pages:5, copies:2, sides:TwoSidedLongEdge, color:Color, paper:A4`
- `received\Fake-A4-Color\*.pdf` 生成；
- 队列 `JobCount` 回到 0，`PrinterStatus=Normal`。

### 3.5 停止/清理

```powershell
# 管理员：卸载队列
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Uninstall-PrintPalQueue.ps1

.\scripts\Stop-PrintPalAgent.ps1
.\scripts\Stop-PrintPalApp.ps1
.\scripts\Stop-FakePrinters.ps1
```

### 3.6 P3 UI 冒烟

启动 App 后：

1. 安装队列（3.3），打印（3.4）；
2. 打开 App：“打印记录”应出现任务，汇总今日/本周/本月更新，首行自动选中并显示详情；
3. 切换“打印机/路由规则/价格表/设置”检查数据；
4. 设置页点“检查队列”，应显示“已安装（正常）”；
5. 关闭窗口应隐藏到托盘，Agent 继续运行；托盘“退出”停止。

截图与页面说明见 [ui.md](ui.md)。

### 3.7 P4 人工确认/重试

关闭自动转发启动：

```powershell
.\scripts\Start-PrintPalApp.ps1 -SeedDemo -NoAutoForward
```

打印 5 页 × 2 份彩色双面后：

1. 任务状态为 **Held（待确认）**，FakePrinter 不应收到；
2. 选中任务，点“转发/重试”按当前目标入队；
3. 需要换目标时点“选择目标转发”；
4. 2 秒内 UI 显示“转发中 → 已转发/失败/未知”；
5. 日志 `artifacts\p4\data\logs\app.log` 出现
   `job <id> forwarded to <printer> on attempt 1`。

状态与策略：

| 状态 | 含义 |
|---|---|
| Held | 等待人工确认 |
| Priced | 已计价/路由，等待或重试中 |
| Forwarding | 正在发送 |
| Succeeded | 已转发 |
| Failed | 达到重试上限 |
| Unknown | 超时，结果未知；不自动重试 |

失败重试可把 FakePrinter 换成 `--reject` 或 `--fail-rate 1`（在 UI 里新增/修改打印机指向对应端口），
观察 `attempt 1 failed` → `retrying` → `failed after N attempts`。

---

## 4. P5 安装/卸载/修复/诊断

完整说明见 [installer.md](installer.md)。

### 4.1 生成自包含包

```powershell
.\scripts\publish\Publish-PrintPal.ps1 -Zip
```

输出：

```
dist\PrintPal-0.5.0-win-x64\
  app\                 # 自包含 App
  fake-printer\        # 测试用
  installer\
  docs\
  Install-PrintPal.cmd
  Uninstall-PrintPal.cmd
dist\PrintPal-0.5.0-win-x64.zip
```

### 4.2 安装

双击 `Install-PrintPal.cmd`，或管理员 PowerShell：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\dist\PrintPal-0.5.0-win-x64\installer\Install-PrintPal.ps1
```

安装后：

- 程序：`C:\Program Files\PrintPal`
- 数据：`%LOCALAPPDATA%\PrintPal`
- 队列：`PrintPal Virtual Printer`
- 自启：HKCU Run
- 快捷方式：开始菜单/桌面

测试安装目录（不污染 Program Files）：

```powershell
.\installer\Install-PrintPal.ps1 -InstallDir C:\Temp\PrintPal `
  -SkipQueue -SkipAutostart -NoShortcuts -NoStart
# 启动该目录 App 后，再只装队列：
.\installer\Install-PrintPal.ps1 -InstallDir C:\Temp\PrintPal `
  -SkipFileCopy -SkipAutostart -NoShortcuts -NoStart
```

### 4.3 修复/诊断/卸载

```powershell
# 修复队列/自启/快捷方式；程序文件缺失时加 -Source .\app
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File "C:\Program Files\PrintPal\installer\Repair-PrintPal.ps1"

# 诊断（txt + zip）
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File "C:\Program Files\PrintPal\installer\Diagnose-PrintPal.ps1" -Zip

# 卸载（默认保留数据；加 -RemoveData 删除）
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File "C:\Program Files\PrintPal\installer\Uninstall-PrintPal.ps1"
```

---

## 5. Windows 10 验证

### 5.1 P6 验收包

共享文件夹：

```
\\vmware-host\Shared Folders\p0-win10\p6\
  PrintPal-0.5.0-win-x64.zip
  print\PrintPal.PrintProbe.exe
  Run-P6-Win10.cmd
  Run-P6-Win10.ps1
  README-P6-Win10.md
```

在 VM 内双击 `Run-P6-Win10.cmd`（管理员 UAC），脚本会：

> 注：VMware `vmrun` 客户机操作不支持空密码；Win10 账号 Lee 无密码时只能手动运行。

1. 复制/解压到 `C:\p6-win10`；
2. 安装 Program Files → 启动 App（seed demo）→ 3 台 FakePrinter → 队列；
3. PrintProbe 5 页×2 份彩色双面；
4. 校验 ¥6.40 / 5 页 / PDF；
5. 卸载并保留数据；
6. 结果写入 `p6\result\result.txt`（或 `C:\p6-win10\result`）。

`Result : PASS` 后把 `result.txt` / `app.log` / `run.log` 带回主机。

> P6 已在 Windows 10 Education 19045 运行：**PASS**，¥6.40，PDF 566,399 B。
> 结果已复制到 `artifacts\p6\win10-result\`。

### 5.2 手动驱动/队列检查

1. 在 VM 内确认/安装驱动：

```powershell
Add-PrinterDriver -Name 'Microsoft IPP Class Driver'
Get-PrinterDriver -Name 'Microsoft IPP Class Driver'
```

2. 把 Agent/FakePrinter 发布为自包含（或使用已构建输出）并复制到 VM；
3. 启动 FakePrinter/Agent（同上）；
4. 用管理员 PowerShell 执行 `Install-PrintPalQueue.ps1`；
5. 从记事本/WordPad/WPS 打印，检查 Agent 日志和 FakePrinter 输出。

Win10 的 `prnms012.inf` 在 DriverStore 中但被 `ExcludeFromSelect` 隐藏，必须显式
`Add-PrinterDriver`。

---

## 6. 诊断

```powershell
.\scripts\Diagnose-PrintPal.ps1
```

输出：驱动、队列、Agent `/health`、进程、数据目录、最近日志、队列作业。

常见问题：

| 现象 | 排查 |
|---|---|
| 队列 Offline/Error | Agent 是否运行；`/health`；端口 8630；Application 日志 `Universal Print` 属性类型错误 |
| `Add-PrinterDriver` 失败 | 确认 `C:\Windows\System32\DriverStore\FileRepository\prnms012.inf_*` 存在；必要时 `pnputil /add-driver` |
| 任务卡 `Printing, Retained` | 先看 Agent 日志；P0 阶段可用 `Restart-Service Spooler` + 重装队列；不要在生产安装器里重启 Spooler |
| 端口被占用 | `Get-NetTCPConnection -LocalPort 8630`；停掉占用进程或改 `--port` 并重装队列 |
| 页数 0 / NeedsReview | 文档不是可解析 PDF；检查 `agent.out.log` / P3 `logs\app.log` 的 page count 详情 |
| 转发失败 | FakePrinter 是否运行；`FakePrinter.out.log`；目标 IPP 能力/格式是否兼容 |
| P3 界面无数据 | App 是否启动；`/health`；`logs\app.log`；首行是否选中 |
| P3 端口 8630 被占用 | 先退出 AgentHost/旧 App；`Get-NetTCPConnection -LocalPort 8630` |
| P3 关闭窗口后找不到 | 程序在托盘；双击托盘图标或右键“打开 PrintPal” |
| Win10 脚本乱码/解析失败 | 使用 UTF-8 BOM 或纯 ASCII 脚本；`-ExecutionPolicy Bypass` |

---

## 7. 数据与日志

```
%LOCALAPPDATA%\PrintPal\            # 生产默认（AgentOptions）
  printpal.db
  documents\<guid>.pdf
  logs\
```

P2 演示使用 `artifacts\p2\data\`：

- `agent.out.log` / `agent.err.log`
- `printpal.db`
- `documents\`
- `agent.json`（pid/端口）

P3 演示使用 `artifacts\p3\data\`：

- `logs\app.log`
- `printpal.db` / `documents\`
- `app.json`（pid/端口）

P4 演示使用 `artifacts\p4\data\`（同样结构；另有 `install-queue.log`、`uninstall-queue.log`）。

P5 安装：

- 程序：`C:\Program Files\PrintPal`
- 数据：`%LOCALAPPDATA%\PrintPal`
- 安装日志：`%ProgramData%\PrintPal\install.log`
- 诊断：`%ProgramData%\PrintPal\diagnostics-*.txt(.zip)`
- 测试安装：`artifacts\p5\install\PrintPal`、`artifacts\p5\install-data`

FakePrinter：

- `artifacts\p2\fake-printers\<name>.out.log`
- `artifacts\p2\fake-printers\received\<name>\`
- P3 演示对应 `artifacts\p3\fake-printers\...`
- P4 演示对应 `artifacts\p4\fake-printers\...`

---

## 8. 开发约定

- 不改 `参考资料/`；新代码在 `src/`、`tests/`、`scripts/`、`docs/`。
- 每次阶段结束更新：`docs/architecture.md`、`docs/test-plan.md`、`docs/CHANGELOG.md`。
- 新增关键决策写 ADR（`docs/decisions/`）。
- 所有金额用“分”；计价按“每份”计算双面奇数页。
- 不把真实打印测试数据提交到仓库（`artifacts/` 已忽略）。

---

## 9. P7 服务/MSI

### 9.1 构建

```powershell
# 发布 app+service、构建 MSI、打 zip
.\scripts\publish\Publish-PrintPal.ps1 -NoFakePrinter -Zip

# 只发布（不构建 MSI）
.\scripts\publish\Publish-PrintPal.ps1 -NoFakePrinter -NoMsi

# 指定版本
.\scripts\publish\Publish-PrintPal.ps1 -Version 1.0.1 -NoFakePrinter -Zip
```

输出：`dist\PrintPal-1.0.1-win-x64\PrintPal-1.0.1-x64.msi`（未签名）。

### 9.2 安装/卸载/修复

```powershell
# 普通安装：WixUI 向导
msiexec.exe /i .\dist\PrintPal-1.0.1-win-x64\PrintPal-1.0.1-x64.msi

# 静默安装
msiexec.exe /i .\dist\PrintPal-1.0.1-win-x64\PrintPal-1.0.1-x64.msi /qn /norestart
Get-Service PrintPal.Service
Get-Printer -Name 'PrintPal Virtual Printer' | Select Name,PortName
Invoke-RestMethod http://127.0.0.1:8630/health

# 修复
msiexec.exe /f .\dist\PrintPal-1.0.1-win-x64\PrintPal-1.0.1-x64.msi /qn

# 卸载
msiexec.exe /x .\dist\PrintPal-1.0.1-win-x64\PrintPal-1.0.1-x64.msi /qn /norestart
```

- 服务数据：`C:\ProgramData\PrintPal`；
- 队列端口必须是 **WSD-...**（HTTP 端口会丢失 copies/sides/color）；
- 数据文件夹/记录卸载保留；
- 升级：直接安装更高版本 MSI（同 UpgradeCode）；已回归 0.6.0→1.0.1；
- 回滚：队列安装失败时 MSI 回滚服务/程序/队列，数据保留；
- MSI 未签名：SmartScreen 可能提示“未知发布者”，功能不受影响。

### 9.3 服务模式 UI

APP 启动时探测 `/health`：

- 服务 Running → 日志 `using service mode`，UI 直接读写 ProgramData DB；
- 服务未运行 → 便携模式（进程内 Agent）。

服务模式关闭 UI 不影响收任务；UI 的“转发/重试”由服务扫描器入队。

