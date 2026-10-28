# PrintPal 测试计划与结果

> 更新：2026-10-28（P6）
> 测试资产：`tests/`、`tools/PrintPal.IppProbe`、`tools/PrintPal.PrintProbe`、`scripts/p0/`

---

## 1. 测试层次

| 层 | 内容 | 命令 |
|---|---|---|
| 单元测试 | 计价/路由/纸张/枚举；IPP 编解码；PDF/PS/OXPS 页数；SQLite | `dotnet test PrintPal.sln` |
| 集成测试 | IppServer + IppClient；Create-Job/Send-Document/gzip；转发；Agent 流水线 | 同上 |
| Windows 端到端 | 真实队列 + PrintProbe + WPS + FakePrinter | 手工/脚本 |
| Win10 端到端 | VM + 自包含 P0 包 | 手工/脚本 |

当前自动化：**98 个测试通过**：

- Core 57
- Ipp 11
- Capture 10
- Storage 6
- Integration 14（含 10 个 Agent 流水线）

---

## 2. P0：IPP 捕获可行性

### 2.1 Windows 11 26300

| 用例 | copies | sides | color | paper | pages |
|---|---|---|---|---|---|
| 5 页 × 3 份，双面长边，彩色 A4 | 3 | two-sided-long-edge | color | A4 | 5 |
| 4 页 × 3 份，双面，黑白 A4 | 3 | two-sided-long-edge | monochrome | A4 | 4 |
| 2 页 × 1 份，单面 A3 | 1 | one-sided | monochrome | A3 | 2 |
| 3 页 × 2 份，双面短边，彩色 A5 | 2 | two-sided-short-edge | color | A5 | 3 |
| 50 页 × 1 份，A4 | 1 | one-sided | monochrome | A4 | 50 |
| WPS 3 页 × 2 份 | 2 | one-sided | monochrome | A4 | 3 |
| WPS 5 页 × 2 份，彩色双面 | 2 | two-sided-long-edge | color | A4 | 5 |

### 2.2 Windows 10 22H2 19045

| 用例 | copies | sides | color | paper | pages |
|---|---|---|---|---|---|
| 5 页 × 3 份，双面长边，彩色 A4 | 3 | two-sided-long-edge | color | A4 (21000×29700) | 5 |
| 3 页 × 2 份，单面，黑白 A4 | 2 | one-sided | monochrome | A4 | 3 |
| 2 页 × 1 份，单面 A3 | 1 | one-sided | monochrome | A3 (29700×42000) | 2 |
| 20 页 × 1 份，A4 | 1 | one-sided | monochrome | A4 | 20 |

Win10 `errors.txt`：`No Universal Print errors`。

### 2.3 P0 关键结论

- 两系统都发送 PDF；Win11 可能 gzip；页数 PdfPig。
- `copies`/`sides`/`color`/`media-col` 可用；WPS copies 正确（未摊平）。
- Win10 必须先 `Add-PrinterDriver`。
- `job-media-sheets` 不是实际纸张数；计价不能用它。

详细证据：[ipp-capture.md](spikes/ipp-capture.md)。

---

## 3. P2：Agent 端到端

环境：Windows 11 26300，AgentHost 8630，Fake-A4-Color 8631，队列
“PrintPal Virtual Printer”。

| 项 | 结果 |
|---|---|
| 输入 | PrintProbe 5 页 × 2 份，A4 彩色双面 |
| Agent 日志 | `= ¥6.40 -> Fake-A4-Color [Succeeded]` |
| FakePrinter | PDF，5 页，copies=2，sides=TwoSidedLongEdge，color=Color，A4 |
| 文档大小 | 534,354 字节 |
| SHA-256 | `89542a…94cf`（完整值见 FakePrinter 日志） |
| 队列 | `PrinterStatus=Normal`，`JobCount=0` |
| 数据 | `artifacts/p2/data/printpal.db` + `documents/` |

计价核对：`(2×1.20 + 1×0.80) × 2 = ¥6.40`。

---

## 4. P3：WPF UI 冒烟

环境：Windows 11 26300，`PrintPal.App`（WPF）进程内托管 Agent，8630；队列
“PrintPal Virtual Printer”；3 台 FakePrinter。

| 项 | 结果 |
|---|---|
| App 启动 + Agent `/health` | ✅ |
| 队列安装 + 5 页×2 份、A4 彩色双面 | ✅ ¥6.40 |
| Fake-A4-Color 收到 PDF | ✅ 5 页，535,559 B |
| 打印记录列表（全部列） | ✅ |
| 今日/本周/本月汇总 | ✅ 1 单 · ¥6.40 |
| 首行自动选中 + 详情（张数/公式） | ✅ |
| 打印机页（3 台种子） | ✅ |
| 路由规则页（3 条） | ✅ |
| 价格表页（8 条，可编辑/保存） | ✅ |
| 设置页（IPP/数据/队列/自启/转发） | ✅ 已安装（截图时 QueueStatus=None；代码映射为“正常”） |
| 托盘/关闭隐藏 | 代码实现，手工冒烟 |
| 单实例/自启 | 代码实现，P6 自动化 |

UI 截图见 [ui.md](ui.md)。

> P3 没有新增自动化 UI 测试；使用 UI Automation 选择 Tab/DataItem + `PrintWindow`
> 离屏截图做冒烟，不抢用户前台焦点。

---

## 5. P4：异步转发/重试/人工确认

新增 6 个集成测试（Integration 总计 14）：

| 测试 | 场景 | 结果 |
|---|---|---|
| `Forwarding_retries_until_success` | 目标第 1 次失败、第 2 次成功 | 2 次尝试后 `Succeeded` |
| `Forwarding_marks_failed_after_max_attempts` | 目标始终失败 | 3 次尝试后 `Failed`，原因含“3 次” |
| `Forwarding_timeout_marks_unknown_without_retry` | 目标延迟 3s、超时 200ms | 1 次尝试后 `Unknown`，不自动重试 |
| `Manual_confirmation_holds_then_forwards` | `AutoForward=false` | 先 `Held`，人工入队后 `Succeeded` |
| `Manual_target_override_for_unrouted_job` | 无路由 `Unrouted` | 选择目标后 `Succeeded`，目标被覆盖 |
| `Interrupted_forwarding_job_becomes_unknown_on_restart` | 崩溃残留 `Forwarding` | 重启后 `Unknown`，不自动重试 |

测试使用 20ms 重试间隔/200ms 超时，避免拖慢 CI；生产默认 2s/10s/30s、30s 超时。
截图/UI 实测：

- `--no-auto-forward` 捕获 5 页×2 份 A4 彩色双面 → `Held`，FakePrinter 未收到；
- UI Automation 点击“转发/重试” → 第 1 次成功；
- FakePrinter 收到 PDF（5 页，534,128 B），job-name 带 `[PrintPal:<JobId>]`；
- 截图 `docs/images/p4-manual.png`。

---

## 6. P5：自包含发布/安装/卸载/修复/诊断

### 6.1 发布

| 项 | 结果 |
|---|---|
| 版本 | 0.5.0 |
| RID | win-x64，self-contained |
| 文件 | 984 个，316.2 MB |
| zip | 139.3 MB |
| App exe | 162,304 B，SHA-256 `E8E3673E…CA60` |
| 自包含启动 | ✅ `/health` 正常（目标机无需 .NET） |

### 6.2 安装（测试安装目录）

| 步骤 | 结果 |
|---|---|
| 复制 app + installer | ✅ |
| 安装清单/卸载注册表 | ✅ |
| 启动安装版 App + `/health` | ✅ |
| 安装队列 | ✅ Normal |
| 安装版打印 5 页×2 份彩色双面 | ✅ ¥6.40 |
| 安装版转发到 FakePrinter | ✅ 534,064 B，5 页，job-name 带 JobId |
| 修复删除队列后重建 | ✅ Normal |
| 自启 HKCU Run | ✅ 指向测试安装目录 |
| 开始菜单/桌面快捷方式 | ✅ |
| 诊断 txt + zip | ✅ 报告 + app.log 快照 |

### 6.3 卸载

| 项 | 结果 |
|---|---|
| 停止 App | ✅ |
| 删除队列/端口 | ✅ |
| 删除自启/快捷方式/卸载项 | ✅ |
| 删除安装目录 | ✅ |
| 保留用户数据 | ✅（`-RemoveData` 才删除） |

> P5 用 `artifacts\p5\install\PrintPal` 作为测试安装目录，避免污染真实 Program Files；
> 正式 Program Files 安装 + Win10 干净环境在 P6 验收。

---

## 7. P6：验收

### 7.1 Windows 11

| 项 | 结果 |
|---|---|
| Program Files 安装 + 队列 | ✅ Normal |
| PrintProbe 5p×2 彩色双面 | ✅ **¥6.40**，PDF 535,115 B |
| WPS 3p×2 黑白单面 | ✅ **¥1.20**，PDF 1,652 B |
| 启动到 `/health` | 768 ms |
| 启动 Working Set | 121 MB |
| 捕获→转发 | 约 330 ms |
| 仅监听 127.0.0.1:8630 | ✅ |
| 队列 Shared/Published | False/False |
| 超大文档 | ✅ 413，handler 未收到 |
| 卸载 | ✅ 队列/程序/自启/快捷方式/卸载项移除 |

P6 脚本 `Run-P6-Win10.ps1` 在 Win11 主机预演：**PASS**，PDF 535,100 B。

### 7.2 Windows 10

- P0：19045.6456 已通过（驱动/队列/PDF/属性，见 §2）。
- P6：Windows 10 Education 19045，Program Files 安装 + 队列 Normal；
  PrintProbe 5 页×2 份彩色双面 **¥6.40**，FakePrinter PDF **566,399 B**，7 项检查 **PASS**；
  卸载后队列/程序/进程清理。
- 结果：`artifacts/p6/win10-result/result.txt`。

---

## 8. P7：Windows 服务 + MSI

### 8.1 服务

| 项 | 结果 |
|---|---|
| `sc create PrintPal.Service` + start | ✅ Running / Automatic |
| 服务 `/health` | ✅ |
| 服务模式打印 5 页×2 份彩色双面 | ✅ **¥6.40** → Fake-A4-Color |
| FakePrinter PDF | 535,174 B，5 页 |
| WPF `--background` | ✅ `using service mode` |
| 关闭 UI | 服务继续运行 |

### 8.2 MSI

| 项 | 结果 |
|---|---|
| `msiexec /i /qn` | ✅ 安装成功 |
| 服务 | ✅ Running |
| 队列 | ✅ WSD IPP 端口（HTTP 会丢失属性） |
| PrintProbe 5p×2 彩色双面 | ✅ **¥6.40**，PDF 535,174 B |
| UI 服务模式 | ✅ |
| WixUI 向导 | ✅ Welcome/License/InstallDir/Verify/Progress/Exit |
| 升级 0.6.0→1.0.1 | ✅ 服务/队列/程序更新，数据保留 |
| 修复（删除 App DLL → `/f`） | ✅ DLL 恢复，服务/队列可用 |
| 回滚（无效队列 URL） | ✅ 1603；服务/程序/队列回滚，数据保留 |
| 卸载 | ✅ 服务/队列/程序/快捷方式移除，数据保留 |
| 签名 | ❌ `NotSigned`（可安装，SmartScreen 可能提示） |

> MSI 的队列 CA 以安装用户身份运行（Impersonate），并强制 WSD 端口；SYSTEM 下
> `Add-Printer -IppURL` 会创建 HTTP 端口，导致 copies/sides/color 丢失。

---

## 9. 自动化测试覆盖

### Core
- 单面/双面、偶数页、奇数页每份最后一张按单面价；
- 多份不合并单面纸；
- 通配价格规则、优先级、禁用；
- 纸张映射（A4/A3/A5/Letter/Legal/自定义）；
- 颜色/双面/格式映射；
- 路由条件、优先级、正则、默认打印机、格式兼容；
- `ApplyQuote`。

### Ipp
- 请求/响应编解码、多值属性、集合、状态消息；
- IppServer + IppClient 能力发现；
- `Print-Job`；
- `Create-Job` + `Send-Document` + gzip；
- handler 失败的 IPP 错误。

### Capture
- PDF 页数（WPS 3/5 页夹具）；
- PS DSC / showpage；
- OXPS `.fpage`；
- 文档嗅探；
- DocumentStore 保存/哈希/读取/删除/路径穿越；
- PWG 页数明确“未实现”（两个目标 OS 都发送 PDF）。

### Storage
- 任务字段往返（含纸张/颜色/双面/价格/金额）；
- 汇总与软删除；
- 打印机能力 JSON；
- 价格规则（含通配）；
- 路由规则列表/目标；
- 设置。

### Integration
- IPP 转发到 FakePrinter；
- Raw 9100；
- 连接失败；
- 捕获→计价→路由→转发全链路；
- Agent 自动转发、Unrouted、NeedsReview、SeedDemo。

---

## 10. 后续测试矩阵

### P3（已完成，补自动化）
- 打印列表列/筛选/汇总/CSV；
- 打印机增删改/测试连接；
- 路由规则编辑与样例测试；
- 价格矩阵编辑；
- 托盘/自启/单实例；
- 关闭窗口最小化、退出后队列离线。

### P4（已完成，补自动化）
- 失败重试（2s/10s/30s）；
- 超时 → `Unknown`，不重复计费；
- 目标格式不兼容；
- 人工确认模式；
- FakePrinter `--fail-rate`/`--reject`。

### P5（已完成，待干净环境）
- 自包含发布/安装/卸载/修复/诊断；
- 测试安装目录 + 正式 Program Files E2E；
- Win10 P6 已通过。

### P6（已完成）
- Win11 Program Files 安装/卸载、PrintProbe、WPS、性能、安全；
- Win10：`Run-P6-Win10.cmd` PASS；
- Office 未安装、Edge 打印未自动化。

### P7（部分完成）
- Windows 服务 + MSI（1.0.1）、WixUI 向导、升级/修复/回滚已完成；
- 未做升级失败后自动恢复旧版本、服务崩溃恢复、并发 SQLite 压测；
- 其余 P7（PSA、本地队列、价格表/角色、自动更新）未开始。
---

## 11. 已知测试缺口

- PWG 页数未实现（无真实 PWG 样本；目标 OS 发送 PDF）。
- IPP 路径无法验证“来源应用”字段（PSA 可选后端才有）。
- 未做真实物理打印机测试；FakePrinter 模拟 IPP/Raw。
- P3/P4 未做 UI 自动化回归（已做 UI Automation 冒烟 + 截图）。
- P4 重试/超时由进程内 Fake 目标验证；未做真实网络抖动/断电恢复测试。
- P5 安装验证使用测试安装目录；正式 Program Files 已验证。
- Win10 P6 已通过；Office 未安装、Edge 打印未自动化。
- P7 服务/MSI 已手工验证升级/修复/回滚；未做升级失败后恢复旧版本、服务崩溃恢复、多用户 SQLite 压测。
- P7 MSI 未签名；队列必须是 WSD，HTTP 会丢失属性；App/Service 同目录共享运行时。
- 未做多用户/服务化/自动更新测试。
