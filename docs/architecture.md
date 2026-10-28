# PrintPal 架构设计（Living Document）

> 版本：v0.9（P7 服务 + MSI 1.0.1；其它 P7 可选）
> 更新：2026-10-28
> 本文是持续维护的架构文档；每个阶段结束后更新“当前状态 / 已知限制 / 下一步”。

---

## 1. 目标与范围

PrintPal 在 Windows 10 / Windows 11 终端上：

1. 提供一台 **“PrintPal Virtual Printer”** 队列；
2. 捕获用户从 WPS / Office / 记事本 / Edge 打印的任务；
3. 在 APP 中展示打印列表：纸张、颜色、单双面、页数、份数、单价、金额；
4. 按路由规则自动转发到真实打印机；
5. APP 内配置打印机、路由规则、价格表；支持汇总和 CSV 导出。

当前阶段已实现 **捕获 → 计价 → 路由 → 转发 → 记录** 的后台链路；WPF UI 在 P3。

---

## 2. 平台约束（P0 实测）

| 约束 | 影响 |
|---|---|
| Windows 10/11 都支持 **Microsoft IPP Class Driver** | 统一用 IPP 队列捕获，无需自研驱动 |
| Win10 的 IPP 驱动被 `ExcludeFromSelect` 隐藏 | 安装器必须先 `Add-PrinterDriver` |
| 两系统都发送 **PDF**（Win11 可能 gzip） | 页数用 PdfPig；必须处理 `compression=gzip` |
| Windows 使用 `Create-Job` + `Send-Document` | 服务端必须关联两个请求的 job-id |
| IPP 属性类型敏感 | 见 ADR-004；属性类型错误会让队列 Offline/任务卡住 |
| `Add-Printer -IppURL` 成功但可能返回空对象 | 安装器按队列名轮询，不能以返回值判断失败 |
| WPS 在 IPP 队列下 `copies` 正确 | 金额可信；仍保留手工修正兜底 |
| 应用未运行时队列 Offline | MVP 用托盘自启；后续可服务化 |

P0 证据见 [IPP 捕获报告](spikes/ipp-capture.md)。

---

## 3. 运行时架构

```
┌────────────────────────── Windows 10 / 11 终端 ──────────────────────────┐
│                                                                          │
│  WPS / Office / 记事本 / Edge                                             │
│        │                                                                 │
│        ▼                                                                 │
│  "PrintPal Virtual Printer" 队列                                        │
│  Microsoft IPP Class Driver                                              │
│        │  IPP: Validate-Job / Create-Job / Send-Document (gzip PDF)      │
│        ▼                                                                 │
│  ┌──────────────── PrintPal Agent（P3/P4: WPF 进程内托管）───────────┐  │
│  │  IppServer (Kestrel, 127.0.0.1:8630/ipp/print)                      │  │
│  │    ├─ 能力响应（Windows 兼容属性集）                                │  │
│  │    ├─ 文档解压/嗅探 → DocumentStore（PDF/PS/OXPS）                  │  │
│  │    ├─ PdfPig 页数                                                   │  │
│  │    ├─ PrintJobPipeline：属性归一化 → 计价 → 路由 → SQLite → 入队     │  │
│  │    ├─ ForwardingWorker：有界重试 / 超时 Unknown / 人工确认           │  │
│  │    └─ PrintJobForwarder：IPP / Raw 9100                             │  │
│  │  SQLite: PrintJobs / Printers / PriceRules / RoutingRules / Settings│  │
│  └─────────────────────────────────────────────────────────────────────┘  │
│        ▲                                                                 │
│        │ 读取 SQLite / 修改设置 / 查看记录 / 人工转发 / 目标覆盖         │
│  ┌─────┴─────────────────────────────────────────────────────────────┐  │
│  │ PrintPal.App (WPF)：打印记录 / 打印机 / 路由 / 价格 / 设置 / 托盘  │  │
│  └───────────────────────────────────────────────────────────────────┘  │
│        │                                                                 │
│        ▼                                                                 │
│  真实打印机（IPP / Raw 9100） 或 FakePrinter（本机模拟）                 │
└──────────────────────────────────────────────────────────────────────────┘
```

P3 起，`PrintPal.App`（WPF）在同一个进程内托管 `PrintPalAgent`；UI 只读取 SQLite /
Agent 服务，不直接处理 IPP。`PrintPal.AgentHost` 保留为 P2/无界面/自动化验证宿主。

### 3.1 组件职责

| 组件 | 职责 |
|---|---|
| `PrintPal.Ipp` | IPP 编解码、客户端、Kestrel 服务端、Windows 兼容能力集 |
| `PrintPal.Capture` | 文档嗅探、PDF/PS/OXPS 页数、文档存储与 SHA-256 |
| `PrintPal.Core` | 纸张/颜色/单双面、计价、路由规则、领域模型 |
| `PrintPal.Storage` | SQLite 持久化与汇总 |
| `PrintPal.Forwarding` | IPP `Print-Job`、Raw 9100、失败结果 |
| `PrintPal.Agent` | 捕获流水线、后台 `ForwardingWorker`、Agent 生命周期、数据库/文档/服务器组装、演示数据种子 |
| `PrintPal.Service`（P7） | Windows 服务宿主：LocalSystem、ProgramData、开机自启 |
| `PrintPal.AgentHost` | P2 无界面宿主；P3 后保留用于自动化/无界面验证 |
| `PrintPal.FakePrinter` | 模拟真实打印机：IPP + Raw、多实例、控制台输出 |
| `PrintPal.App`（P3） | WPF UI：打印列表/打印机/路由/价格/设置、托盘、自启、单实例、人工转发/目标覆盖 |

---

## 4. 捕获与作业流水线

### 4.1 IPP 操作

服务端实现：

- `Get-Printer-Attributes`
- `Validate-Job`
- `Print-Job`
- `Create-Job` + `Send-Document`（Windows 实际路径）
- `Get-Job-Attributes`
- `Cancel-Job`

Windows 的 `Send-Document` 可能带 `compression=gzip`；服务端解压后再交给流水线。
`Create-Job` 与 `Send-Document` 通过 `job-id` 关联；属性合并后得到完整作业。

### 4.2 字段归一化

| 字段 | 来源 | 缺失时 |
|---|---|---|
| 纸张 | `media` 或 `media-col` 的 `media-size`（1/100 mm） | A4 + `IsEstimated` |
| 颜色 | `print-color-mode` | Monochrome + `IsEstimated` |
| 单双面 | `sides` | Simplex + `IsEstimated` |
| 份数 | `copies` | 1 + `IsEstimated` |
| 页数 | PDF（PdfPig）/ PS / OXPS | 0 → `NeedsReview`，不自动转发 |
| 文档名/用户 | `job-name` / `requesting-user-name` | 默认值 |

### 4.3 状态

```
Captured → Priced → Forwarding → Succeeded
                     ↓ 明确失败且未达上限
                   Priced（退避重试）
                     ↓ 达到上限
                   Failed
                     ↓ 超时
                   Unknown（不自动重试）
AutoForward=false → Held（人工转发）
```

其它：`NeedsReview`（页数未知/无价格）、`Unrouted`（无规则/无兼容默认）、`Canceled`。
`StatusReason` 记录第几次失败、目标错误或超时说明。

### 4.4 文档存储

`%LOCALAPPDATA%\PrintPal\documents\<guid>.<ext>`（Agent 可配置数据目录）。
文件名用 GUID；展示名单独存 DB，避免路径注入。SHA-256 用于转发校验。

---

## 5. 计价

### 5.1 模型

价格按“张”配置：

- `SimplexPriceCents`：单面价/张；
- `DuplexPriceCents`：双面价/张。

双面任务按 **每份** 计算，奇数页最后一张按单面价：

```
duplexPerCopy   = P / 2
simplexPerCopy  = P % 2
amountPerCopy   = duplexPerCopy * duplexPrice + simplexPerCopy * simplexPrice
amount          = amountPerCopy * N
```

**不能**先算 `P × N` 再除 2：不同份的单面纸不能拼成一张双面纸。

### 5.2 默认价目（CNY，分/张）

| 纸张 | 黑白单面 | 黑白双面 | 彩色单面 | 彩色双面 |
|---|---|---|---|---|
| A4 | 20 | 30 | 80 | 120 |
| A3 | 40 | 60 | 160 | 240 |
| A5 | 15 | 22 | 60 | 90 |
| Letter/Legal/自定义 | 按 A4 | 按 A4 | 按 A4 | 按 A4 |

缺价格 → `NeedsReview`，不自动转发。

详见 [ADR-002](decisions/ADR-002-pricing-per-sheet.md)。

---

## 6. 路由

`RoutingRule` 条件（AND；同类多值 OR；空 = 任意）：

- 纸张、颜色、单双面；
- 页数范围（每份页数）；
- 来源应用（正则，IPP 路径暂为 null）；
- 文档名（正则）；
- 目标打印机、优先级、启用。

`RoutingEngine` 按优先级取第一条匹配且**目标格式兼容**的规则；无匹配用默认打印机；
仍无 → `Unrouted`。格式兼容：

- IPP：能力集的 `document-format-supported`；未知能力视为兼容（测试连接后补全）；
- Raw：`FormatPreference`（Auto/PDF/PWG/PS）。

---

## 7. 转发

捕获与转发解耦：

- `PrintJobPipeline` 完成捕获/计价/路由后写 SQLite，并把 JobId 放入 `ForwardingWorker` 队列；
- IPP 请求立即返回，不等待目标打印机或重试延迟；
- `ForwardingWorker` 串行消费，执行有界重试。

策略（默认）：

| 项 | 值 |
|---|---|
| 最大尝试次数 | 4（首次 + 3 次重试） |
| 重试间隔 | 2s / 10s / 30s |
| 单次超时 | 30s |
| 明确失败 | 自动重试 |
| 超时 | `Unknown`，**不自动重试** |
| 成功 | `Succeeded` + `ForwardedAtUtc` |

- `IppForwarder`：`Print-Job`，发送 PDF/PWG/PS + copies/sides/color/media；
  `job-name` 追加 `[PrintPal:<JobId>]` 作为幂等关联；
- `Raw9100Forwarder`：TCP 9100 原样发送；无作业名元数据，超时同样按 `Unknown` 处理；
- `AutoForward=false`：状态 `Held`，不自动入队；
- UI 可“转发/重试”或“选择目标转发”；应用启动只恢复 `Priced` 且有目标的任务，
  崩溃残留的 `Forwarding` 置 `Unknown`（不自动重试）。

详见 [ADR-005](decisions/ADR-005-forwarding-retry.md)。

---

## 8. 数据模型（SQLite）

| 表 | 关键字段 |
|---|---|
| `PrintJobs` | Id、时间、文档名/用户、格式/路径/SHA、纸张/颜色/单双面、Copies、PageCount、单面/双面价、张数、金额、TargetPrinterId、Status、IsEstimated、PricingJson、RawAttributesJson |
| `Printers` | Id、Name、Protocol、Host、Port、Path、FormatPreference、Enabled、IsDefault、CapabilitiesJson、LastTest* |
| `PriceRules` | PaperSize、ColorMode、SimplexPriceCents、DuplexPriceCents、Priority、Enabled |
| `RoutingRules` | 条件 JSON、目标、优先级、Enabled |
| `Settings` | Key/Value（如 autoForward、printerUuid） |

金额以“分”为单位（long），无浮点误差。`TotalPages`/`BillableSides` 为计算属性，不重复存储。

---

## 9. 部署与生命周期

### 9.1 安装队列

开发/手动：

1. Win10：`Add-PrinterDriver -Name 'Microsoft IPP Class Driver'`；
2. 启动 Agent（8630）；
3. `Add-Printer -IppURL http://127.0.0.1:8630/ipp/print`；
4. 按名称轮询（`Add-Printer` 可能返回空对象），必要时 `Rename-Printer`；
5. `Get-Printer` 验证。

P5 安装器 `Install-PrintPal.ps1` 自动完成上述步骤，并额外：

- 自包含发布复制到 `C:\Program Files\PrintPal`；
- 开始菜单/桌面快捷方式；
- 当前用户 HKCU Run 自启；
- 卸载注册表项（Apps & Features）；
- `install.json`、`install.log`；
- 修复/诊断/卸载脚本。

详见 [安装文档](installer.md)。

### 9.2 Agent 生命周期

- P2：`PrintPal.AgentHost.exe` 控制台，Ctrl+C 停止；脚本 `Start/Stop-PrintPalAgent.ps1`。
- P3：`PrintPal.App.exe`（WPF）进程内托管 Agent；关闭窗口最小化到托盘，托盘“退出”才停止；
  支持开机自启（HKCU Run，`--background`）、单实例。
- P4：Agent 内新增后台 `ForwardingWorker`；捕获请求立即返回，转发/重试异步执行；
  应用启动恢复 `Priced` 任务，`Held/Failed/Unknown` 由用户决定。
- P5：自包含安装器/卸载/修复/诊断；程序在 Program Files，数据按用户放在
  `%LOCALAPPDATA%\PrintPal`；HKCU 自启。
- P7：新增 `PrintPal.Service`（LocalSystem，自动启动，数据 `%ProgramData%\PrintPal`）；
  WPF UI 探测 `/health` 后使用 `ServiceAgentProxy`，共享 SQLite；服务扫描器处理 UI 的
  `Priced` 任务。另提供 WiX MSI（unsigned）。
- Agent 未运行时队列 Offline；服务模式已解决大部分场景，P7 可进一步改 IPC/服务化。

---

## 10. 当前状态与验证

| 项 | 状态 |
|---|---|
| P0 IPP 捕获（Win11/Win10） | ✅ |
| P1 领域/IPP/捕获/存储/转发/FakePrinter | ✅ 2026-10-28（87 测试） |
| P2 Agent 流水线 + 队列安装脚本 | ✅ 2026-10-28，真实队列 E2E（91 测试） |
| P3 WPF UI（记录/打印机/路由/价格/设置/托盘） | ✅ 2026-10-28，队列 E2E + UI 截图 |
| P4 异步转发/有界重试/超时/人工确认/目标覆盖 | ✅ 2026-10-28（97 测试） |
| P5 自包含发布/安装/卸载/修复/诊断 | ✅ 2026-10-28（主机安装 E2E） |
| P6 Win11 验收（安装/打印/性能/安全） | ✅ 2026-10-28（98 测试；¥6.40 / WPS ¥1.20） |
| P6 Win10 验收 | ✅ 2026-10-28（19045；PASS；¥6.40） |
| P7 Windows 服务 + MSI（unsigned） | ✅ 2026-10-28（1.0.1；WixUI 向导；升级/修复/回滚） |
| P7 其它（PSA/本地队列/价格表/自动更新） | 可选，未开始 |

P2/P3/P4 E2E 实测：5 页 × 2 份、A4 彩色双面 → **¥6.40** → Fake-A4-Color 收到 PDF
（P2 534,354 B / P3 535,559 B / P4 534,128 B，5 页）→ `Succeeded`；P4 还验证了
`--no-auto-forward` 捕获为 Held、UI“转发/重试”成功、FakePrinter 收到带
`[PrintPal:<JobId>]` 的 job-name。

P5 安装 E2E（测试安装目录）：自包含 App 安装 → 队列 Normal → 打印 5 页×2 份 →
**¥6.40** → 安装版 App 转发到 FakePrinter（534,064 B）→ 修复重建队列 → 卸载移除
队列/程序/自启/快捷方式/卸载项，保留用户数据。

P6 Win11：正式 Program Files 安装 → PrintProbe 5 页×2 份彩色双面 **¥6.40**（535,115 B）→
WPS 3 页×2 份黑白单面 **¥1.20**（1,652 B）→ 启动 768 ms / 121 MB → 仅回环监听 →
卸载。Win10 P0 通过；P6 在 19045 安装版打印 **¥6.40**，FakePrinter PDF **566,399 B**，
7 项检查全 PASS。详见 [验收报告](acceptance.md)。

---

## 11. 已知限制 / 待决策

- PWG 页数未实现（Win10/11 都发送 PDF）；PWG 仅保留格式识别，页数 → `NeedsReview`。
- IPP 路径拿不到来源应用名；PSA 可选后端才能拿到。
- Agent 未运行时队列 Offline；P7 服务化。
- Windows 本地/USB 队列转发未实现；MVP 只做 IPP/Raw 网络打印机。
- 价格默认 CNY；多币种/多价格表在 P7 扩展。
- 转发 worker 串行；重试延迟期间后续任务会等待。P7 可增加并发/持久化队列。
- 超时置 `Unknown` 不自动重试；需人工判断是否重打。
- Raw 9100 无作业名元数据，协议层无法幂等。
- 重试次数/间隔暂未暴露到 UI 设置。
- P5 安装器是 PowerShell（自包含 App），不是 MSI/签名；P7 再做 MSI/代码签名。
- 自启写入当前用户 HKCU；多用户机器需各用户首次启动或管理员另行配置。
- 数据按用户放在 `%LOCALAPPDATA%\PrintPal`，不随安装目录迁移。
- P7 App/Service 同目录共享运行时；包约 279 MB，MSI 约 75.9 MB。
- P7 安装向导为 WixUI_InstallDir（欢迎/许可/目录/确认/进度/完成）。
- P3/P4 未做 UI 自动化回归；已有手工/UI Automation 冒烟与截图，P6 补自动化。
- Win10 P6 已通过；Office 未安装、Edge 打印未自动化。
- P6 自动化新增超大文档 413 测试；单实例/托盘为代码 + 手工冒烟。
- P7 服务与 UI 共享 ProgramData SQLite（Users Modify）；多用户隐私/高并发需角色/压测。
- P7 MSI 未签名；SmartScreen 可能提示未知发布者；已加 WixUI_InstallDir 向导。
- P7 App/Service 合并到同一目录共享运行时；MSI 约 75.9 MB。
- P7 升级 0.6.0→1.0.1、修复、失败回滚已回归；升级失败恢复旧版本未覆盖。
- P7 队列必须是 WSD IPP 端口；HTTP 端口会丢失 copies/sides/color。
- P7 服务模式 UI 通过共享 DB + 扫描器协作，尚未改 IPC；后续可升级。

---

## 12. 路线图

| 阶段 | 目标 |
|---|---|
| P3 ✅ | WPF UI（打印列表/打印机/路由/价格/设置）、托盘、自启、单实例 |
| P4 ✅ | 异步转发、有界重试、超时 Unknown、人工确认、目标覆盖 |
| P5 ✅ | 自包含发布、安装/卸载/修复/诊断 |
| P6 ✅ | Win10/Win11 E2E、性能、安全、验收 |
| P7（部分） | ✅ Windows 服务 + MSI（1.0.1，WixUI/升级/修复/回滚）；可选 PSA、本地队列、价格表/角色、自动更新 |

---

## 13. 相关文档

- [P0 IPP 捕获报告](spikes/ipp-capture.md)
- [P0 环境记录](spikes/environment.md)
- [测试计划](test-plan.md)
- [运行手册](runbook.md)
- [安装/卸载/诊断](installer.md)
- [P7 服务/MSI](p7-service-msi.md)
- [P6 验收报告](acceptance.md)
- [UI 说明](ui.md)
- [变更记录](CHANGELOG.md)
- [ADR-001 IPP 捕获](decisions/ADR-001-ipp-capture.md)
- [ADR-002 计价](decisions/ADR-002-pricing-per-sheet.md)
- [ADR-003 Agent/UI 分离](decisions/ADR-003-agent-host-and-ui.md)
- [ADR-004 属性兼容](decisions/ADR-004-ipp-attribute-compatibility.md)
- [ADR-005 异步转发/重试](decisions/ADR-005-forwarding-retry.md)
- [ADR-006 发布/安装](decisions/ADR-006-packaging-and-install.md)
- [ADR-007 Windows 服务](decisions/ADR-007-windows-service.md)
- [ADR-008 MSI/unsigned](decisions/ADR-008-msi-unsigned.md)

