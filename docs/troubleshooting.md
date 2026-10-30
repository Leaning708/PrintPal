# PrintPal 故障排查手册

> 最后更新：2026-10-30（初版，含 WPS 打印失败病例）
> 适用：开发、测试、装维；PrintPal 0.5.0 – 1.0.1
> 目标：先判断任务卡在链路的哪一段，再按“问题索引”处理；新问题持续追加到本文。
>
> 相关文档：[运行手册](runbook.md) · [安装/卸载/诊断](installer.md) ·
> [P7 服务/MSI](p7-service-msi.md) · [UI 说明](ui.md) · [架构](architecture.md)

---

## 0. 先判断问题在哪一段

打印链路：

```
WPS / Office / 记事本
  → Windows Spooler
  → IPP 队列（必须是 WSD 端口）
  → http://127.0.0.1:8630/ipp/print
  → PrintPal.Agent（捕获 → 计价 → 路由）
  → ForwardingWorker
  → FakePrinter / 真实打印机
```

按顺序做 5 个检查，基本能定位到段：

| # | 检查 | 命令 | 正常 | 异常去 |
|---|---|---|---|---|
| 1 | Agent 是否在跑 | `Invoke-RestMethod http://127.0.0.1:8630/health` | `status: ok` | [A4](#a4-8630-被占用--agent-没起来) |
| 2 | 队列是否存在、是否 WSD、是否 Normal | `Get-Printer 'PrintPal Virtual Printer'` | `Normal`，`PortName` 以 `WSD-` 开头 | [A2](#a2-队列-error--任务卡在-error-printing-retained) / [A3](#a3-队列不存在端口不是-wsd驱动缺失) |
| 3 | 队列和当前 Agent 是否“同一个打印机” | `.\scripts\Get-PrintPalQueueBinding.ps1` | `MATCH` | [A1](#a1-队列-uuid-与数据目录不一致最常见) |
| 4 | 打一张测试页，任务有没有进 PrintPal | 看 `app.log` 是否有 `JOB ` | 有 `JOB ...` | 没有 → [A1](#a1-队列-uuid-与数据目录不一致最常见) / [A2](#a2-队列-error--任务卡在-error-printing-retained) |
| 5 | 有 `JOB` 但状态不对 | UI 详情或 `app.log` 的状态 | `Priced`/`Succeeded` | `NeedsReview` → [B2](#b2-needsreview页数-0)；`Unrouted` → [B4](#b4-unrouted--目标打印机不对)；`Held` → [B5](#b5-held不自动转发)；`Failed`/`Unknown` → [C3](#c3-failed-vs-unknown重试策略) |

> 关键判断：**`app.log` 里没有 `JOB` 行，说明请求根本没到 PrintPal**，不要先去查计价或转发。

---

## 1. 日志、数据与证据位置

### 1.1 日志位置

| 组件 | 场景 | 路径 |
|---|---|---|
| WPF App | 开发默认（`Start-PrintPalApp.ps1`） | `artifacts\p3\data\logs\app.log` |
| WPF App | 便携安装 | `%LOCALAPPDATA%\PrintPal\logs\app.log` |
| WPF App | 服务模式 | UI 日志仍在 `%LOCALAPPDATA%\PrintPal\logs\app.log`；**数据在** `%ProgramData%\PrintPal` |
| PrintPal.Service | MSI / 服务 | `C:\ProgramData\PrintPal\logs\service.log` |
| PrintPal.AgentHost | `Start-PrintPalAgent.ps1` | `artifacts\p2\data\agent.out.log`、`agent.err.log` |
| FakePrinter | `Start-FakePrinters.ps1` | `artifacts\p2\fake-printers\<name>.out.log`、`.err.log` |
| FakePrinter 收到的文档 | 同上 | `artifacts\p2\fake-printers\received\<printer-name>\` |
| 安装器（PowerShell） | P5 | `%ProgramData%\PrintPal\install.log` |
| 诊断报告 | 安装版诊断 | `%ProgramData%\PrintPal\diagnostics-<timestamp>.txt(.zip)` |
| MSI | 安装/升级 | `msiexec /l*v <path>.log` 的输出 |
| Spooler | 系统 | 事件查看器：`Microsoft-Windows-PrintService/Admin`（默认开）、`Operational`（默认关）、`System`（服务控制管理器） |

> 服务模式下 UI 用 `ServiceAgentProxy` 读写 ProgramData 的 DB；`--data` 只影响 UI 自己的日志目录，不影响服务数据。

### 1.2 `app.log` 的 `JOB` 行怎么读

```text
[20:55:17.768] JOB <jobId> [<标签>] <文档名> <页数>p x<份数> <纸张> <颜色> <单双面> = ¥<金额> -> <目标打印机> [<状态>]
```

- 有 `JOB`：捕获、页数、计价、路由至少跑到了写库；问题在状态/转发。
- 完全没有 `JOB`：spooler / 队列 / 端口 / UUID 绑定问题（见 A 节）。
- `job <id> forwarded to ...`：转发成功。
- `job <id> attempt N failed: ...` / `failed after N attempts`：转发失败/重试。

### 1.3 数据目录

| 模式 | 数据目录 |
|---|---|
| 开发 App（脚本默认） | `artifacts\p3\data`（可用 `-Data` 覆盖） |
| 当前演示数据（本机 2026-10-30） | `artifacts\devdata`（含 15 条演示任务） |
| 便携安装 / UI 默认 | `%LOCALAPPDATA%\PrintPal` |
| Windows 服务 | `C:\ProgramData\PrintPal` |
| AgentHost（脚本默认） | `artifacts\p2\data` |
| P4 人工转发演示 | `artifacts\p4\data` |

目录内容：`printpal.db`、`documents\`、`logs\`、`app.json`（启动脚本写的 pid/端口）。
数据库关键表：`PrintJobs`、`Printers`、`RoutingRules`、`PriceRules`、`Settings`。
`Settings` 里两个关键值：`printerUuid`（IPP 打印机身份）、`autoForward`（是否自动转发）。

### 1.4 Windows 打印事件日志

```powershell
# 默认开启，通常记录作业失败
Get-WinEvent -LogName 'Microsoft-Windows-PrintService/Admin' -MaxEvents 50 |
  Select-Object TimeCreated, Id, LevelDisplayName, Message | Format-List

# Operational 默认关闭；需要管理员开启后再复现一次
wevtutil sl Microsoft-Windows-PrintService/Operational /e:true
Get-WinEvent -LogName 'Microsoft-Windows-PrintService/Operational' -MaxEvents 50 |
  Select-Object TimeCreated, Id, LevelDisplayName, Message | Format-List
```

服务启动失败：

```powershell
Get-WinEvent -FilterHashtable @{
  LogName='System'; ProviderName='Service Control Manager'; StartTime=(Get-Date).AddHours(-2)
} | Select-Object TimeCreated, Id, LevelDisplayName, Message | Format-List
```

---

## 2. 诊断命令速查

### 2.1 最基本的一组

```powershell
cd C:\Develop\code\AGI\PrintPal

# Agent
Invoke-RestMethod http://127.0.0.1:8630/health | ConvertTo-Json -Depth 4

# 队列
Get-Printer -Name 'PrintPal Virtual Printer' |
  Select-Object Name, PrinterStatus, PortName, DriverName, JobCount
Get-PrintJob -PrinterName 'PrintPal Virtual Printer' |
  Select-Object Id, DocumentName, JobStatus, SubmittedTime, TotalPages

# 进程 / 端口 / 服务
Get-Process PrintPal.App, PrintPal.Service, PrintPal.AgentHost, PrintPal.FakePrinter -ErrorAction SilentlyContinue
Get-NetTCPConnection -LocalPort 8630 -State Listen
Get-Service PrintPal.Service

# 驱动
Get-PrinterDriver -Name 'Microsoft IPP Class Driver'
```

### 2.2 队列 UUID 绑定检查（A1 专用）

```powershell
.\scripts\Get-PrintPalQueueBinding.ps1
```

输出 `MATCH` / `MISMATCH`，并给出两边 UUID。退出码：`0` 匹配、`1` 不匹配、`2` 无法检查。

### 2.3 重装队列（需要管理员）

```powershell
Restart-Service Spooler -Force
Get-Printer -Name 'PrintPal Virtual Printer' -ErrorAction SilentlyContinue | Remove-Printer
Add-Printer -IppURL http://127.0.0.1:8630/ipp/print

# Add-Printer 可能返回空对象，按名称轮询确认；必要时改名
Get-Printer | Where-Object Name -like '*PrintPal*' |
  Select-Object Name, PortName, PrinterStatus
```

也可以运行 `scripts\Install-PrintPalQueue.ps1`（会清理旧队列、装驱动、添加/改名/验证；**不重启 Spooler**）。

### 2.4 收集诊断包

```powershell
# 开发机（只读；注意它默认看 artifacts\p3\data 和 AgentHost 日志）
.\scripts\Diagnose-PrintPal.ps1 -Data .\artifacts\devdata

# 安装版
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File "C:\Program Files\PrintPal\installer\Diagnose-PrintPal.ps1" -Zip
```

### 2.5 查数据库（可选）

有 `sqlite3.exe` 时：

```powershell
sqlite3 .\artifacts\devdata\printpal.db "select Key, Value from Settings;"
sqlite3 .\artifacts\devdata\printpal.db "select DocumentName, Status, AmountCents from PrintJobs order by CreatedAtUtc desc limit 5;"
```

没有 `sqlite3` 时用 DB Browser for SQLite，或直接以 UI 详情为准。

---

## 3. 问题索引

| 现象 | 小节 |
|---|---|
| WPS/Office 打印报错，任务 `Error`、已打印 0 页，`app.log` 无 `JOB` | [A1](#a1-队列-uuid-与数据目录不一致最常见) |
| 队列 `Error`，任务 `Error, Printing` / `Retained` | [A2](#a2-队列-error--任务卡在-error-printing-retained) |
| 队列不存在 / 端口不是 WSD / 驱动缺失 | [A3](#a3-队列不存在端口不是-wsd驱动缺失) |
| 8630 被占用、App 起不来 | [A4](#a4-8630-被占用--agent-没起来) |
| 服务在跑但 UI 没数据 / `--seed-demo` 不生效 | [A5](#a5-服务模式-vs-便携模式) |
| 任务进 PrintPal 但 `NeedsReview`、页数 0 | [B2](#b2-needsreview页数-0) |
| 金额不对 | [B3](#b3-金额不对) |
| `Unrouted` / 目标打印机不对 | [B4](#b4-unrouted--目标打印机不对) |
| `Held` 不自动发 | [B5](#b5-held不自动转发) |
| 转发 `Failed` / `Unknown` | [C3](#c3-failed-vs-unknown重试策略) |
| 打印出的 PDF 文字被裁掉 / 只剩一小截 / 空白 | [B7](#b7-打印出的-pdf-文字被裁掉--只剩一小截) |
| FakePrinter 收不到 | [C1](#c1-fakeprinter-没起--端口不对) |
| App 说“已在运行”、托盘找不到 | [D1](#d1-app-已在运行单实例) / [D2](#d2-关窗后找不到托盘) |
| 服务启动失败 / MSI 安装失败 | [E2](#e2-服务启动失败) / [E3](#e3-队列安装失败--msi-回滚) |

---

## 4. 打印链路问题（A）

### A1. 队列 UUID 与数据目录不一致（最常见）

**现象**

- WPS / Office / 记事本打印失败，队列变成 `Error`，任务 `Error, Printing`，`PagesPrinted=0`；
- PrintPal 的 `app.log` 里**完全没有** `JOB` 记录；
- 换任意应用、任意文档都失败，不是 WPS 单独的问题。

**原因**

`printerUuid` 是每个数据目录第一次启动时随机生成并存进 `Settings` 的
（[PrintPalAgent.cs](../src/PrintPal.Agent/PrintPalAgent.cs#L77)），通过 IPP 的
`printer-uuid` 广播（[IppPrinterAttributes.cs](../src/PrintPal.Ipp/IppPrinterAttributes.cs#L233)）。
Windows 安装 IPP 队列时把这个 UUID 记进 WSD 端口和设备节点。**换 `--data`、删库、重置数据目录，都会换 UUID，旧队列就找不到设备。**

**诊断**

```powershell
.\scripts\Get-PrintPalQueueBinding.ps1
# Queue UUID : e07a6417-...
# Agent UUID : urn:uuid:330640dc-...
# MISMATCH: ...
```

也可以手工对比：

```powershell
$q = Get-Printer -Name 'PrintPal Virtual Printer'
(Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\Print\Monitors\WSD Port\Ports\$($q.PortName)").'Printer UUID'
```

**解决**

1. **优先**：用队列当初安装时对应的数据目录启动 App。例如队列绑定的是 `artifacts\devdata`：
   ```powershell
   .\scripts\Stop-PrintPalApp.ps1
   .\scripts\Start-PrintPalApp.ps1 -Data .\artifacts\devdata
   ```
2. 或者以管理员身份，**先**用目标数据目录启动 App，**再**重装队列（见 [2.3](#23-重装队列需要管理员)）。
3. 开发机临时办法（不改队列）：停 App 后，把目标库 `Settings.printerUuid` 改成队列的 UUID，再启动。只建议开发环境用。

**验证**

- `Get-PrintPalQueueBinding.ps1` 输出 `MATCH`；
- 打测试页，`app.log` 出现 `JOB ...`，队列回到 `Normal`。

> 2026-10-30 的实际病例见 [10.1](#101-2026-10-30wps-打印失败队列-uuid-与数据目录不一致)。

### A2. 队列 `Error` / 任务卡在 `Error, Printing, Retained`

**现象**：`Get-Printer` 显示 `PrinterStatus: Error`，`Get-PrintJob` 有一条 `Error, Printing` 或 `Error, Deleting, Printing`，`Remove-PrintJob` 也可能卡住。

**处理**

```powershell
# 1) 先尝试删任务（自己的任务通常可以删）
Remove-PrintJob -PrinterName 'PrintPal Virtual Printer' -ID <id>

# 2) 删不掉 / 队列仍 Error：管理员重启 Spooler（会短暂清空所有队列）
Restart-Service Spooler -Force

# 3) 还不行：重装队列（管理员）
Restart-Service Spooler -Force
Get-Printer 'PrintPal Virtual Printer' -ErrorAction SilentlyContinue | Remove-Printer
Add-Printer -IppURL http://127.0.0.1:8630/ipp/print
```

- `Retained` 任务在打印机恢复后可能被 spooler 自动重试（2026-10-30 实测：修复 UUID 后，之前失败的任务自动补打成功）。
- 队列 Error 往往只是失败任务的结果；先按 [A1](#a1-队列-uuid-与数据目录不一致最常见) 查根因，不要只清任务。

### A3. 队列不存在、端口不是 WSD、驱动缺失

```powershell
Get-Printer -Name 'PrintPal Virtual Printer'
Get-PrinterDriver -Name 'Microsoft IPP Class Driver'
```

- 队列不存在 → 管理员运行 `scripts\Install-PrintPalQueue.ps1`。
- `PortName` 不是 `WSD-...`（例如 `http://...`）→ HTTP 端口会丢 `copies/sides/color`，必须用 `Add-Printer -IppURL` 重建 WSD 队列。
- Win10 提示驱动缺失 → 先显式安装：
  ```powershell
  Add-PrinterDriver -Name 'Microsoft IPP Class Driver'
  ```
- 队列在但 `PrinterStatus` 不是 `Normal` → 见 [A2](#a2-队列-error--任务卡在-error-printing-retained)。

### A4. 8630 被占用 / Agent 没起来

```powershell
Get-NetTCPConnection -LocalPort 8630 -State Listen |
  Select-Object LocalAddress, LocalPort, OwningProcess
Get-Process -Id <pid>
```

常见占用者：旧的 `PrintPal.App`、`PrintPal.AgentHost`、`PrintPal.Service`。
处理：停掉旧进程/服务，再启动 App。

- `PrintPal.App` 是单实例：已有实例时再启动会弹“PrintPal 已在运行”，**新参数不生效**；先 `Stop-PrintPalApp.ps1`。
- 服务在跑时 App 会进服务模式，不会自己监听 8630（见 [A5](#a5-服务模式-vs-便携模式)）。

### A5. 服务模式 vs 便携模式

| 谁监听 8630 | 数据目录 | 备注 |
|---|---|---|
| `PrintPal.App`（便携） | `--data` 指定 | 开发默认 `artifacts\p3\data`；`--seed-demo` 生效 |
| `PrintPal.Service` | `C:\ProgramData\PrintPal` | MSI 安装后自动启动；UI 通过 `ServiceAgentProxy` 读写 |
| `PrintPal.AgentHost` | `--data` 指定 | 无界面调试宿主，默认 `artifacts\p2\data` |

App 启动时探测 `http://127.0.0.1:8630/health`：

- 返回 `status: ok` → **service mode**，日志出现 `using service mode`，`--data`/`--seed-demo` 不影响服务数据；
- 否则 → 便携模式，进程内启动 Agent。

服务模式下想给 ProgramData 写演示数据：先确保服务在跑，再用 AgentHost 指向 ProgramData、临时端口 seed（见
[人工测试 §4 方案 C](manual-test-1.0.1.md)），然后退出 AgentHost。

**不要**在服务运行时再启动便携 App 抢 8630。需要在便携模式演示，先管理员 `Stop-Service PrintPal.Service`。

### A6. Spooler 本身异常

- 所有打印机都异常、`Get-PrintJob` 不更新、队列状态不刷新 → 管理员 `Restart-Service Spooler -Force`。
- 之后检查 `Microsoft-Windows-PrintService/Admin` 与 `System` 日志（见 [1.4](#14-windows-打印事件日志)）。
- 生产环境不要轻易重启 Spooler；开发/测试机可以。

---

## 5. 捕获 / 计价（B）

### B1. `app.log` 里没有 `JOB`

不是计价问题，任务没进来。按顺序查：Agent health → 队列状态 → 队列 UUID 绑定 → 端口/驱动。
直接去 [A1](#a1-队列-uuid-与数据目录不一致最常见) / [A2](#a2-队列-error--任务卡在-error-printing-retained) / [A4](#a4-8630-被占用--agent-没起来)。

### B2. `NeedsReview`、页数 0

`StatusReason` 会写具体原因（UI 右侧详情或 DB）。

- 页数从**文档内容**解析：PDF（PdfPig）、PS、OXPS；**PWG 目前只能识别格式、算不出页数**。
- 驱动应发 `document-format=application/pdf`，可能是 gzip，PrintPal 会先解压。
- 常见原因：
  - 文档不是可解析的 PDF（PWG 任务会因页数未知进入 `NeedsReview`）；
  - PDF 损坏、加密、0 页；
  - 文档超过大小限制（默认 500 MB）。
- 处理：让应用“打印为 PDF”再试；确认 `documents\` 下有对应文件；PWG 任务暂时只能记为 `NeedsReview`。

### B3. 金额不对

计价按“张”，双面按**每份**算，奇数页最后一张按单面：

```text
每份双面张数 = P / 2
每份单面张数 = P % 2
每份金额     = (P/2) × 双面价 + (P%2) × 单面价
总金额       = 每份金额 × N
```

例：5 页 × 2 份，A4 彩色双面 = `(2×1.20 + 1×0.80) × 2 = ¥6.40`。

常见原因：

- 纸张识别失败按 A4 兜底（`IsEstimated=true`）；
- 颜色/单双面缺失按黑白/单面兜底；
- 份数被应用重复乘（看 `RawAttributes` 的 `copies` 和 `job-media-sheets`，**不要用 `job-media-sheets` 计价**）；
- 价格表被改过，或命中了别的纸张/颜色规则；
- 旧任务在旧标签/旧价格下生成。

核对：UI 详情里的“计价公式/张数”，或 `PrintJobs` 的 `PageCount/Copies/UnitPriceCents/AmountCents/PricingJson`。

### B4. `Unrouted` / 目标打印机不对

- 没有路由规则命中 → `Unrouted`，不会自动转发。
- 规则按优先级匹配纸张/颜色/单双面/页数/正则；目标打印机必须存在且启用。
- 默认打印机只在其 `Enabled=true` 时参与兜底。
- 检查 UI“路由规则”“打印机”页；或 DB 的 `RoutingRules`/`Printers`。
- 已生成的任务改路由不会自动重算，需要“选择目标转发”。

### B5. `Held`、不自动转发

- 启动参数 `--no-auto-forward`，或 `Settings.autoForward=false`，或 UI 设置页关了自动转发。
- `Held` 是等待人工确认：详情面板“转发/重试”，或“选择目标转发”。
- 检查：

  ```powershell
  sqlite3 .\artifacts\devdata\printpal.db "select * from Settings where Key='autoForward';"
  ```

### B6. 413 / 文档太大

- Kestrel 请求上限与 `MaxDocumentBytes` 默认 500 MB；超过返回 413。
- 超大 PDF 建议拆分，或调整 `AgentOptions.MaxDocumentBytes`（开发）。
- 413 不会产生 `JOB`；当前 `app.log` 不记录请求级信息，主要靠 spooler/客户端报错判断。

### B7. 打印出的 PDF 文字被裁掉 / 只剩一小截

**现象**

- 源文档正常，但捕获/转发的 PDF 页面大片空白，文字只在左/右边缘露出一小截；
- 页数、金额、路由都正常，容易误判为“打印成功”；
- 换应用（WPS/Office）都可能复现，取决于页边距。

**原因**

PrintPal 在 IPP 能力里广告的 `media-*-margin-supported` 是 **4233**（单位 1/100 mm，即 **42.33 mm**）。
Windows IPP Class Driver 会把可打印区域裁到该边距；WPS 默认页边距是 **31.75 mm**，文字落在可打印区外，被驱动裁掉。
2026-10-30 已改为 **423**（4.23 mm，正常硬件边距），见
[IppPrinterAttributes.cs](../src/PrintPal.Ipp/IppPrinterAttributes.cs#L120)。

**诊断**

1. 打开 PDF 看内容：如果文字只剩边缘，继续；
2. 检查驱动缓存的设备能力（单位 1/1000 mm）：
   ```powershell
   $v4 = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Print\Printers\PrintPal Virtual Printer').PrintQueueV4DriverDirectory
   Select-String "C:\Windows\System32\spool\V4Dirs\$v4\pdc.xml" -Pattern 'PortraitImageableSize'
   # 旧: 42330,...（42.33 mm）  新: 4230,...（4.23 mm）
   ```
3. 或用 PDF 工具看页面内容流：`BT...ET` 前有 `W* n`，裁剪框左边界在 42.33 mm，而文字从 31.75 mm 开始。

**解决**

- 升级到包含该修复的版本后重启 App/服务；驱动通常会在下次查询时刷新能力。
- 若 PDF 仍被裁，按 [2.3](#23-重装队列需要管理员) 重装队列，强制刷新 WSD/驱动能力缓存。
- 不要靠“改页边距”绕过：其他应用/文档还会踩到。

**验证**

- `media-*-margin-supported` 返回 `423`；
- WPS 打一页带文字的文档，PDF 打开后文字完整；
- **不能只看页数/金额**，必须打开 PDF 或渲染页面检查内容。

---

## 6. 转发（C）

### C1. FakePrinter 没起 / 端口不对

```powershell
foreach ($p in 8631,8632,8633) {
  try { (Invoke-RestMethod "http://127.0.0.1:$p/health" -TimeoutSec 2).ippUri }
  catch { "port $p FAILED" }
}
Get-Process PrintPal.FakePrinter
```

默认三台：

| 名称 | IPP | Raw 9100 |
|---|---|---|
| Fake-A4-Color | 8631 | 9101 |
| Fake-A4-Mono | 8632 | 9102 |
| Fake-A3 | 8633 | 9103 |

- 打印机配置里的 host/port/path 必须和 FakePrinter 一致（`127.0.0.1:8631/ipp/print`）。
- `Start-FakePrinters.ps1` **不检查已有实例**；重复运行会端口冲突并覆盖 `pids.json`。重启前先 `Stop-FakePrinters.ps1`。
- 日志与收到的 PDF 见 [1.1](#11-日志位置)。

### C2. 连接失败 / 被拒绝

```powershell
Test-NetConnection 127.0.0.1 -Port 8631
Get-NetTCPConnection -RemotePort 8631 -ErrorAction SilentlyContinue
```

- 目标未启动 → `Failed`，`StatusReason` 里有 `Connection refused` 等。
- FakePrinter 返回 4xx → 检查它是否支持该 `media`/`color`/格式；看 `<name>.out.log`。
- 目标禁用/删除 → 见 [C4](#c4-目标打印机不存在或已停用)。

### C3. `Failed` vs `Unknown`（重试策略）

默认（`AgentOptions`）：

- 最多 **4 次**（含首次），重试间隔 **2s / 10s / 30s**，单次超时 **30s**；
- 明确失败（连接拒绝、4xx/5xx）→ 重试；达到上限 → `Failed`；
- 超时 → `Unknown`，**不自动重试**，需人工判断（可能已经打到纸上）；
- 已 `Succeeded` 的任务不能重试；
- 转发 worker 串行：重试等待期间后续任务会排队。

处理：UI 详情“转发/重试”；先修目标，再重试。`Unknown` 先确认目标是否实际收到，避免重复打印。

### C4. 目标打印机不存在或已停用

`StatusReason`：`目标打印机不存在或已停用`。
检查 UI 打印机页的启用状态；路由规则按打印机 ID 指向，删除/重建打印机会让旧规则失效，需要重新选目标。

### C5. Raw 9100 的注意点

- Raw 9100 没有 job-id/幂等元数据；重试可能重复出纸。
- 优先用 IPP（有 job 属性、可查询状态）。
- 本地队列/USB 转发不在当前范围。

### C6. 用 FakePrinter 造故障

```powershell
# 收一个任务就退出
PrintPal.FakePrinter.exe --name Fake-A4-Color --ipp-port 8631 --raw-port 9101 --once

# 50% 失败
PrintPal.FakePrinter.exe --name Fake-A4-Color --ipp-port 8631 --raw-port 9101 --fail-rate 0.5

# 全部拒绝
PrintPal.FakePrinter.exe --name Fake-A4-Color --ipp-port 8631 --raw-port 9101 --reject

# 每单延迟 5 秒
PrintPal.FakePrinter.exe --name Fake-A4-Color --ipp-port 8631 --raw-port 9101 --delay-ms 5000
```

用来验证 `Failed`、重试、`Unknown` 和 UI 状态。

---

## 7. UI / App（D）

### D1. App 已在运行（单实例）

- 互斥量 `Local\PrintPal.App.SingleInstance`；第二次启动弹“PrintPal 已在运行”后退出，**新参数被忽略**。
- `Start-PrintPalApp.ps1` 发现已有 `PrintPal.App` 进程会直接 `exit 0`。
- 处理：托盘“退出”，或 `.\scripts\Stop-PrintPalApp.ps1`，再按新参数启动。

### D2. 关窗后找不到（托盘）

- 关闭窗口默认隐藏到托盘，Agent 继续运行；托盘右键“退出”才真正退出。
- `--background` 启动直接隐藏。
- 开发机可用 `Stop-PrintPalApp.ps1` 强制结束。

### D3. UI 没数据 / 数据对不上

- UI 每 2 秒刷新；先看筛选（状态/搜索/标签）是否把任务过滤掉。
- 便携模式看 `--data` 的 DB；服务模式看 `C:\ProgramData\PrintPal\printpal.db`。
- 用命令行确认 App 实际参数：
  ```powershell
  Get-CimInstance Win32_Process -Filter "Name='PrintPal.App.exe'" | Select-Object ProcessId, CommandLine
  ```
- 软删除的任务（`IsDeleted=1`）不在列表显示。
- 队列有任务但 UI 没记录 → 大概率是 UUID 绑定问题，任务没进来（[A1](#a1-队列-uuid-与数据目录不一致最常见)）。

### D4. 界面初始化失败 / 崩溃

- 看 `app.log` 的 `UI INIT FAILED` / `AGENT START FAILED`。
- 常见：8630 被占用、数据目录不可写、DB 被锁/损坏、服务与便携模式混用。
- DB 损坏先备份 `printpal.db*`，再考虑重建数据目录（会换 UUID，记得处理队列，见 A1）。

### D5. 改了代码但界面没变

- `Start-PrintPalApp.ps1` 只在 exe **不存在**时构建；改过 XAML/C# 后先：
  ```powershell
  dotnet build src\PrintPal.App\PrintPal.App.csproj -c Debug
  ```
- 确认运行的是 `src\PrintPal.App\bin\Debug\net10.0-windows\PrintPal.App.exe`，不是安装版。

---

## 8. 安装 / 升级 / 服务（E）

### E1. MSI 提示“未知发布者” / SmartScreen

- MSI **未签名**，属预期；功能不受影响。
- 内网分发可加信任，或后续购买代码签名证书。

### E2. 服务启动失败

```powershell
Get-Service PrintPal.Service
sc.exe query PrintPal.Service
Get-Content C:\ProgramData\PrintPal\logs\service.log -Tail 50
```

常见原因：

- 8630 被便携 App/AgentHost 占用；
- `C:\ProgramData\PrintPal` 权限不足（服务会给 `Users` 授 Modify，但安全软件可能拦截）；
- 程序文件缺失/被杀软隔离；
- .NET 运行时缺失（MSI 自带运行时，若手工拷文件则可能缺）。

系统日志看 `Service Control Manager` 的 7000/7009/7034。

### E3. 队列安装失败 / MSI 回滚

- 看 `%ProgramData%\PrintPal\install.log` 和 MSI `/l*v` 日志。
- MSI 用 `PRINTPAL_QUEUE_URL` 测试回滚；默认 `http://127.0.0.1:8630/ipp/print`。
- 队列必须是 WSD IPP；HTTP 端口会丢属性。
- 安装队列前先确认 App/服务 `/health` OK。

### E4. 升级 / 修复 / 卸载

- 升级：直接装更高版本 MSI（同 UpgradeCode），数据保留。
- 修复：`msiexec /f`，会重建队列/服务，数据保留。
- 卸载：默认保留 `%LOCALAPPDATA%\PrintPal` 和 `C:\ProgramData\PrintPal`；`-RemoveData` 才删用户数据。
- 卸载后队列残留 → 管理员 `Remove-Printer` + 删 WSD 端口。

### E5. Windows 10 驱动

```powershell
Add-PrinterDriver -Name 'Microsoft IPP Class Driver'
Get-PrinterDriver -Name 'Microsoft IPP Class Driver'
```

Win10 的 INF 在 DriverStore 里但被 `ExcludeFromSelect` 隐藏，必须显式安装。

### E6. 只能本机用

- Agent 只监听 `127.0.0.1:8630`；局域网其他机器连不上是设计如此。
- 需要远程先改 `BindAddress`/`AdvertiseHost` 并重装队列，当前版本未验证。

---

## 9. 开发 / 构建（F）

### F1. 构建与测试

```powershell
dotnet build PrintPal.sln -c Debug
dotnet test PrintPal.sln -c Debug
```

### F2. 启动脚本不重新构建

`Start-PrintPalApp.ps1` / `Start-FakePrinters.ps1` 只在 exe 缺失时 build；改代码后手动 `dotnet build`。

### F3. `--seed-demo` 的规则

- 价格表为空才写 8 条默认价格；
- 打印机表为空且带 `--seed-demo` 才写 3 台 FakePrinter + 3 条路由；
- 已有数据的目录不会重复/补齐；要干净演示数据就换新目录（但注意 A1 的 UUID 问题）。

### F4. 换数据目录会破坏已安装队列

见 [A1](#a1-队列-uuid-与数据目录不一致最常见)。开发时建议固定一个目录（如 `artifacts\devdata`），需要干净数据时复制目录并保留 `printerUuid`，或重装队列。

### F5. FakePrinter 多实例

- 同一 IPP/Raw 端口只能一个实例；重复启动会绑定失败。
- `artifacts\p2\fake-printers\pids.json` 会被覆盖；先 Stop 再 Start。

### F6. 发布 / MSI

```powershell
.\scripts\publish\Publish-PrintPal.ps1 -NoFakePrinter -Zip
```

详见 [P7 服务/MSI](p7-service-msi.md)。

---

## 10. 典型病例

### 10.1 2026-10-30：WPS 打印失败，队列 UUID 与数据目录不一致

**现象**

- WPS 打印报错；任务 `新建 DOCX 文档.docx`（2 页）状态 `Error, Printing`，`PagesPrinted=0`；
- 队列 `PrinterStatus=Error`，`JobCount=1`；
- `artifacts\demo\data2\logs\app.log` 只有启动行，**没有任何 `JOB`**。

**证据**

```text
队列 WSD 端口 Printer UUID : e07a6417-a4ec-4139-8b58-29215c4d3ee4
artifacts\devdata printerUuid : e07a6417-a4ec-4139-8b58-29215c4d3ee4   ← 队列是对着它装的
artifacts\demo\data2 printerUuid : 330640dc-ccd4-4f49-b3c7-c5417db43d08 ← 当前 App 用的
注册表 SWD\IPP 下只有 e07a...，没有 3306...                             ← 新身份无设备节点
```

**根因**

App 从 `devdata` 切到 `demo\data2`，`printerUuid` 变了；Windows 的 IPP 队列仍绑定旧 UUID，spooler 找不到设备，任务在到达 PrintPal 前就失败。

**解决**

用 `devdata` 重启 App：

```powershell
.\scripts\Stop-PrintPalApp.ps1 -Data .\artifacts\demo\data2
.\scripts\Start-PrintPalApp.ps1 -Data .\artifacts\devdata -SeedDemo
```

**验证**

- 之前 `Retained` 的 `document` 任务被 spooler 自动重试并成功；
- WPS 打 2 页：`JOB ... 2p x1 A4 Monochrome Simplex = ¥0.40 -> Fake-A4-Mono`，FakePrinter 收到 PDF；
- `Get-PrintPalQueueBinding.ps1` 输出 `MATCH`；队列 `Normal`。

**教训**

- 队列装好后不要随意换 `-Data`；换就重装队列；
- `app.log` 无 `JOB` 是“任务没进来”的强信号，先查绑定，不要查计价；
- 已加入 `scripts\Get-PrintPalQueueBinding.ps1` 作为固定检查项。

### 10.2 2026-10-30：WPS 打印 PDF 文字被裁（可打印边距 42.33 mm）

**现象**

- 源文档只有 `123123`，WPS 里显示正常；
- PrintPal 捕获的 PDF 第 1 页几乎空白，只在左侧露出一小截 `3`，第 2 页只露出 `312`；
- 页数 2、金额 ¥0.40、状态 `Succeeded` 都正常，单看任务列表发现不了；
- 用 PDF 渲染器确认是内容被裁，不是浏览器/字体问题。

**证据**

```text
PDF 内容流：
  ... 0 0 64 398 re W* n
  /F1 327.68 Tf
  -940.64 7657.44 Tm
  [123123] TJ

裁剪框左边 = 120 pt = 42.33 mm
文字起点   =  90 pt = 31.75 mm   ← 整段文字落在裁剪框外
```

队列缓存 `pdc.xml`：

```text
PortraitImageableSize = 42330,42330,212340,335340   (1/1000 mm → 42.33 mm)
```

代码侧：`media-*-margin-supported = 4233`（1/100 mm → 42.33 mm）。

**根因**

PrintPal 广告的可打印边距 42.33 mm 比 WPS 默认页边距 31.75 mm 大。Windows IPP Class Driver
按可打印区域裁剪页面内容，WPS 排在页边距内的文字被裁掉。

**解决**

[IppPrinterAttributes.cs](../src/PrintPal.Ipp/IppPrinterAttributes.cs#L120) 中四个边距从 `4233`
改为 `423`（4.23 mm），重建并重启 App。驱动下一次查询能力后生效；若仍被裁，重装队列刷新缓存。

**验证**

- 同一文档重新打印：PDF 内容流不再有 `W* n` 裁剪，文字 `Tm` 的 x 为正；
- 渲染第 1 页为 `1` + `123123`，第 2 页为 `22312312`，与 WPS 源文档一致；
- 新增回归测试
  `Printer_attributes_advertise_margins_that_do_not_clip_wps_text`（IPP 12 个测试通过）。

**预防**

- 打印验收必须打开 PDF 检查内容，不能只看页数/金额/`Succeeded`；
- 写 IPP 能力时注意单位：`media-*-margin-supported` 是 **1/100 mm**，1/1000 会放大 10 倍；
- 虚拟打印机应使用小边距（4.23 mm 或更小），真实打印机的硬件边距由转发目标自己处理。

### 10.3 模板（后续追加）

```markdown
### YYYY-MM-DD：<一句话现象>

**现象**：
**环境**：版本/模式/数据目录/队列端口
**证据**：日志行、UUID、命令输出
**根因**：
**解决**：
**验证**：
**预防**：
```

---

## 11. 上报问题时的最小信息集

1. 时间、操作（哪个应用、什么文档、几页几份）、是否可复现；
2. `PrintPal.App` 命令行（含 `--data`）与模式（便携/服务）；
3. `Invoke-RestMethod http://127.0.0.1:8630/health`；
4. `Get-Printer` + `Get-PrintJob`；
5. `.\scripts\Get-PrintPalQueueBinding.ps1` 完整输出；
6. `app.log` / `service.log` 末尾 50 行；
7. `Microsoft-Windows-PrintService/Admin` 相关事件；
8. FakePrinter 日志与 `received\` 文件列表；
9. 诊断包（`Diagnose-PrintPal.ps1 -Zip`）或 MSI `/l*v` 日志。

---

## 12. 维护约定与 TODO

### 12.1 新增条目的格式

每个新问题按“现象 → 环境 → 证据 → 根因 → 解决 → 验证 → 预防”追加到第 10 节，并在第 3 节索引加一行。
能固化成命令的，优先加脚本（如 `Get-PrintPalQueueBinding.ps1`），文档只保留调用方式。

### 12.2 已知缺口

- `Start-PrintPalApp.ps1` 启动前**不会**检查队列 UUID 绑定，绑定错误只能在打印失败后发现；
- `/health` 未暴露 `printerUuid`，目前要靠 `Get-PrintPalQueueBinding.ps1` 发 IPP 请求；
- `app.log` 只有 `JOB`/转发结果，没有请求级日志（IPP 方法、状态码、拒绝原因），排查“请求没进来”只能靠外部证据；
- PrintService `Operational` 默认关闭，需要管理员手动开启；
- 日志无轮转/大小限制，长期运行需关注磁盘；
- 开发版 `Diagnose-PrintPal.ps1` 面向 AgentHost（`agent.out.log`），对 WPF App 的 `app.log` 覆盖不全。

### 12.3 候选改进

- [ ] `Start-PrintPalApp.ps1` 启动前调用绑定检查，不一致时明确警告；
- [ ] `/health` 增加 `printerUuid` 和 `dataDirectory`；
- [ ] IPP 层增加可选请求日志（方法/状态/耗时），默认关闭；
- [ ] 诊断脚本自动收集 `app.log`、`service.log`、队列 UUID、事件日志；
- [ ] 固定 `printerUuid` 策略（跨数据目录复用），或提供 `--printer-uuid` 参数；
- [ ] 日志轮转。

---

## 变更记录

| 日期 | 变更 |
|---|---|
| 2026-10-30 | 初版：链路定位、日志位置、A–F 分类、WPS/UUID 病例；新增 `Get-PrintPalQueueBinding.ps1` |
| 2026-10-30 | 新增 B7/病例 10.2：可打印边距 4233→423 导致 WPS 文字被裁；补回归测试 |
