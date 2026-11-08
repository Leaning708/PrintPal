---
name: printpal-troubleshoot
description: 按打印链路分段定位 PrintPal 故障（spooler/队列→捕获→计价→转发→UI），收集只读诊断证据包并映射到 troubleshooting.md 的 A/B/C/D 小节。
whenToUse: 队列 Error、任务不转发或 Failed/Unknown、app.log 没有 JOB、界面没数据或卡顿等异常，需要系统排查时使用。
---

# PrintPal 故障排查

## 原则

1. **先取证再动手**：先跑只读诊断，别一上来就重装队列、清数据或重装 MSI；
2. 诊断脚本**不修改任何状态**（不停止/不启动/不写库），可以放心反复跑；
3. 每个结论都要落到证据（日志行、队列状态、DB 记录、端口占用），不要凭感觉；
4. 修好之后按 `AGENTS.md` 保留现场：App / 服务 / FakePrinter 都别关。

## 打印链路

```text
WPS / Office / 记事本
  → Windows Spooler
  → IPP 队列（必须是 WSD 端口）
  → http://127.0.0.1:8630/ipp/print
  → PrintPal.Agent（捕获 → 计价 → 路由）
  → ForwardingWorker
  → FakePrinter / 真实打印机
```

## 5 步定位

| # | 检查 | 命令 | 正常 | 异常 |
|---|---|---|---|---|
| 1 | Agent 在跑吗 | `Invoke-RestMethod http://127.0.0.1:8630/health` | `status: ok` | A4 |
| 2 | 队列存在/是 WSD/正常吗 | `Get-Printer 'PrintPal Virtual Printer'` | `Normal`，`PortName` 以 `WSD-` 开头 | A2 / A3 |
| 3 | 队列和 Agent 是同一个打印机吗 | `.\scripts\Get-PrintPalQueueBinding.ps1` | `MATCH` | A1 |
| 4 | 打一页，`app.log` 有没有 `JOB` | 看日志 | 有 `JOB ...` | A1 / A2 |
| 5 | 有 JOB 但状态不对 | UI 详情或日志 | `Priced`/`Succeeded` | B/C 段 |

> 关键判断：**`app.log` 里没有 `JOB` 行 = 请求根本没到 PrintPal**，不要先去查计价或转发。

## 一键证据包（只读）

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  .\.agents\skills\printpal-troubleshoot\scripts\Collect-PrintPalDiagnostics.ps1
```

输出到 `artifacts\diagnostics\<yyyyMMdd-HHmmss>\summary.md`，包含：

- 环境（OS/PS 版本/仓库/git commit）与进程、服务状态；
- 端口监听（8630–8633、9101–9103）与占用 PID，`/health` 响应；
- 队列（名称/驱动/端口/WSD/状态/任务列表）与 UUID 绑定结果；
- 数据目录（`artifacts\p3\data`、`C:\ProgramData\PrintPal`、`%LOCALAPPDATA%\PrintPal`）文件清单与 DB/WAL 大小；
- 各日志尾部（`app.log`、`service.log`、FakePrinter 日志）并抽出 `JOB` / `ERROR` / `WARN` 行；
- Windows 打印事件（`Microsoft-Windows-PrintService/Admin`）与服务控制管理器错误。

参数：`-RepoRoot`、`-Port 8630`、`-Output <dir>`、`-TailLines 60`、`-SkipWindowsEvents`。

## 分段判断树

**A 段：任务没进 PrintPal（无 JOB）**

- A1 队列 UUID 与数据目录不一致（最常见）：`Get-PrintPalQueueBinding.ps1` 返回 `MISMATCH`；
  重装队列（`scripts\Install-PrintPalQueue.ps1`）或改回原来的数据目录。
- A2 队列 `Error`、任务 `Error, Printing, Retained`：清队列任务/重启 spooler 后重试，反复出现查驱动。
- A3 队列不存在 / 端口不是 WSD / 驱动缺失：装 `Microsoft IPP Class Driver` 并重建队列。
- A4 8630 被占用：`netstat -ano | Select-String ':8630'` 找出 PID；旧 App/AgentHost 退出后再起。
- A5 服务模式 vs 便携模式：服务在跑时 UI 走 `ServiceAgentProxy`，读 `C:\ProgramData\PrintPal`，
  `--seed-demo` 不生效 ——「界面没数据」常是模式/数据目录搞错。

**B 段：捕获/计价有 JOB 但不对**

- B1 日志里没有任何 JOB → 回 A 段。
- B2 `NeedsReview`、页数 0：文档不是可解析 PDF（压缩/加密/格式），看 `DocumentFormat`。
- B3 金额不对：按 `README.md`「计价模型」手算——双面按**每份**算，奇数页最后一张按单面价，再乘份数。
- B4 `Unrouted` / 目标不对：检查路由规则（纸张/颜色/单双面/页数/优先级）与打印机启用状态。
- B5 `Held` 不自动转发：`--no-auto-forward` 或设置里的自动转发被关；UI 里点「转发/重试」。
- B6 413 文档太大 / B7 PDF 被裁：见 troubleshooting B6/B7。

**C 段：转发失败**

- C1 FakePrinter 没起 / 端口不对：`/health` 8631–8633，或按打印机端口核对。
- C2 连接被拒：IP/端口错、机器没监听。
- C3 `Failed` vs `Unknown`：明确失败会按 2s/10s/30s 重试到上限；**超时 `Unknown` 不自动重试**，需人工判断。
- C4 目标打印机被删除/停用。
- C5 Raw 9100 无幂等元数据，注意重复打印。
- C6 造故障：FakePrinter `--reject`、`--fail-rate 1`、`--delay-ms`。

**D 段：UI**

- D1 单实例：已在运行时再启动会弹「PrintPal 已在运行」。
- D2 关窗只是隐藏到托盘，不是退出（D2 找窗口）。
- D3 UI 没数据 / 对不上：先确认模式与数据目录（A5），再看 DB 是否有记录。
- D4 界面初始化失败/崩溃：看 `%LOCALAPPDATA%\PrintPal\logs\app.log` 的 `UI INIT FAILED`。
- D6 窗口缩放时任务表横向滚动条闪现（已知未修复）。
- D7 窗口位置/大小恢复异常（侧车文件 `window-bounds.json`）。
- D8 自定义 TextBox 模板双倍 Padding（输入框缩进异常）。

## 日志位置与 `JOB` 行

| 组件 | 路径 |
|---|---|
| WPF App（开发） | `artifacts\p3\data\logs\app.log` |
| WPF App（服务模式 UI） | `%LOCALAPPDATA%\PrintPal\logs\app.log` |
| PrintPal.Service | `C:\ProgramData\PrintPal\logs\service.log` |
| FakePrinter | `artifacts\p2\fake-printers\<name>.out.log`，收到的 PDF 在 `received\<name>\` |

```text
[20:55:17.768] JOB <jobId> [<标签>] <文档名> <页数>p x<份数> <纸张> <颜色> <单双面> = ¥<金额> -> <目标打印机> [<状态>]
job <id> forwarded to <printer> on attempt 1
job <id> attempt N failed: ...   /   failed after N attempts
```

## 提交问题时的证据清单

- `artifacts\diagnostics\<ts>\summary.md`；
- 复现步骤 + 打印规格（页数/份数/纸张/单双面/颜色）+ 来源应用；
- 队列状态截图/输出、`app.log` 中对应 `JOB` 行与前后 20 行；
- 如果是转发问题：目标打印机配置与 FakePrinter 是否在线。

## 相关文档

- `docs\troubleshooting.md`（A1–A6 / B1–B7 / C1–C6 / D1–D8 全文与修复步骤）
- `docs\runbook.md`、`AGENTS.md`
