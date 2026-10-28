# PrintPal P6 验收报告

> 日期：2026-10-28
> 版本：0.5.0（P6）；P7 1.0.1 服务/MSI 更新见 [p7-service-msi.md](p7-service-msi.md)
> 状态：**Windows 11 + Windows 10 均 PASS；MVP 0.5.0 验收通过**

---

## 1. 范围

P6 验收覆盖：

- 干净/正式安装、队列、托盘自启、卸载；
- WPS / PrintProbe 真实打印；
- 计价、页数、单双面、颜色、A4；
- 转发到 FakePrinter；
- 启动/内存/捕获延迟；
- 安全（loopback、共享、超大请求、路径穿越、单实例）；
- Win10 干净环境（P0/P6 均已通过）。

---

## 2. 自动化测试

**98 个测试全部通过**：

| 测试集 | 数量 |
|---|---|
| Core | 57 |
| Ipp | 11 |
| Capture | 10 |
| Storage | 6 |
| Integration（含 10 个 Agent 流水线） | 14 |

P6 新增安全测试：`Oversized_document_is_rejected_with_413`
（`client-error-request-entity-too-large`，handler 未收到文档）。

---

## 3. Windows 11 验收

环境：Windows 11 26300，x64，Program Files 安装。

### 3.1 安装/队列

| 项 | 结果 |
|---|---|
| 自包含包安装到 `C:\Program Files\PrintPal` | ✅ |
| 安装版 `PrintPal.App.exe` + `/health` | ✅ |
| IPP 队列 `PrintPal Virtual Printer` | ✅ Normal |
| 开始菜单/桌面快捷方式 | ✅ |
| HKCU 自启 | ✅ |
| Apps & Features 卸载项 | ✅ |

### 3.2 真实打印

| 用例 | copies | sides | color | paper | pages | 金额 | 目标 | 结果 |
|---|---|---|---|---|---|---|---|---|
| PrintProbe 5p ×2 | 2 | 双面长边 | 彩色 | A4 | 5 | **¥6.40** | Fake-A4-Color | ✅ |
| WPS 3p ×2 | 2 | 单面 | 黑白 | A4 | 3 | **¥1.20** | Fake-A4-Mono | ✅ |

- PrintProbe PDF：535,115 B；
- WPS PDF：1,652 B（文字文档），job-name `Microsoft Word - 文字文稿1 [PrintPal:…]`；
- 两个任务均 `forwarded to … on attempt 1`；
- 队列恢复 `Normal`，JobCount 0。

### 3.3 安装包脚本 E2E（host rehearsal）

用 `Run-P6-Win10.ps1` 在 Win11 主机预演完整流程：

- 安装 Program Files → 启动安装版 App（seed demo）→ 3 台 FakePrinter → 队列；
- PrintProbe 5 页×2 份彩色双面 → **¥6.40** → FakePrinter PDF 535,100 B；
- 卸载队列/程序/自启/快捷方式/卸载项；
- 结果：

```
Result : PASS
  App installed in Program Files     True
  Agent healthy on 8630              True
  Queue present and Normal           True
  App log has 6.40                   True
  App log has Succeeded              True
  FakePrinter got 5 pages            True
  PDF received                       True
```

> 该结果是 Win10 验收脚本在 Win11 上的预演；Win10 实跑结果见 §4.2。
>
> 备注：VMware `vmrun` 客户机操作不接受空密码（返回“客户机操作系统不支持空密码”），
> 因此需要在 VM 内手动运行 `Run-P6-Win10.cmd`。

---

## 4. Windows 10

### 4.1 P0 已通过（19045.6456）

| 用例 | copies | sides | color | paper | pages |
|---|---|---|---|---|---|
| 5p ×3 双面彩色 A4 | 3 | two-sided-long-edge | color | A4 | 5 |
| 3p ×2 单面黑白 A4 | 2 | one-sided | monochrome | A4 | 3 |
| 2p ×1 单面 A3 | 1 | one-sided | monochrome | A3 | 2 |
| 20p ×1 A4 | 1 | one-sided | monochrome | A4 | 20 |

Win10 需 `Add-PrinterDriver -Name 'Microsoft IPP Class Driver'`；已解决。

### 4.2 P6 已运行（PASS）

环境：Windows 10 Education 19045.6456，`Lee`。

| 项 | 结果 |
|---|---|
| 安装到 `C:\Program Files\PrintPal` | ✅ |
| 队列 `PrintPal Virtual Printer` | ✅ Normal |
| App `/health` | ✅ |
| PrintProbe 5 页×2 份彩色双面 | ✅ **¥6.40** |
| FakePrinter 收到 PDF | ✅ 566,399 B，5 页 |
| copies / sides / color / paper | 2 / TwoSidedLongEdge / Color / A4 |
| App 日志 | `forwarded to Fake-A4-Color on attempt 1` |
| 脚本结果 | **PASS**（7 项检查全 True） |
| 卸载 | ✅ 队列/程序/进程清理 |

脚本输出（节选）：

```
Result : PASS
  App installed in Program Files     True
  Agent healthy on 8630              True
  Queue present and Normal           True
  App log has 6.40                   True
  App log has Succeeded              True
  FakePrinter got 5 pages            True
  PDF received                       True
```

结果/日志已复制回主机：`artifacts/p6/win10-result/`（`result.txt`、`app.log`、
`Fake-A4-Color.out.log`、PDF、`run.log`）。

---

## 5. 性能（Win11）

| 指标 | 结果 |
|---|---|
| App 启动到 `/health` | **768 ms** |
| 启动后 Working Set | **121 MB** |
| 打印后 Working Set | 160 MB（Private 74 MB） |
| PrintProbe 命令返回 | 801 ms |
| 捕获→计价→转发（5 页） | **约 330–350 ms** |
| IPP 队列安装 | 约 4–5 s |
| 自包含包 | 316 MB / zip 139 MB |

---

## 6. 安全

| 项 | 结果 |
|---|---|
| IPP 监听地址 | `127.0.0.1:8630`（仅回环） |
| 队列共享 | `Shared=False, Published=False` |
| 超大文档 | `client-error-request-entity-too-large`，handler 未收到 |
| 路径穿越 | `DocumentStore` 测试覆盖，拒绝目录外路径 |
| 单实例 | 命名 Mutex；第二次启动提示已运行 |
| 外部网络 | 不监听 LAN；仅转发到配置的打印机 |
| 数据位置 | 每用户 `%LOCALAPPDATA%\PrintPal` |

---

## 7. 兼容性/限制

- WPS 已实测；Office 未安装，Edge 打印未自动化（PrintProbe 覆盖 GDI；P0 覆盖 Notepad/Word/WPS 路径）。
- Win10 P0/P6 均已通过。
- 安装器是 PowerShell + 自包含 App，不是 MSI/代码签名。
- 自启按用户 HKCU；数据按用户。
- 超时 `Unknown` 不自动重试；Raw 无幂等元数据。
- 包体积 316 MB / zip 139 MB。

---

## 8. 结论

- **Windows 11：PASS**（安装、队列、PrintProbe、WPS、计价、转发、卸载、性能、安全）。
- **Windows 10：P0 PASS；P6 PASS**（安装、队列、PrintProbe、¥6.40、PDF、卸载）。
- **PrintPal MVP 0.5.0 验收通过。**
- P7（可选）：Win11 PSA、Windows 本地队列转发、服务化、MSI、代码签名、自动更新。

---

## 9. 证据

| 内容 | 路径 |
|---|---|
| Win11 安装/打印/FakePrinter | `artifacts/p6/` |
| Win11 P6 脚本预演结果 | `artifacts/p6/host-script-result/result.txt` |
| WPS 日志 | `artifacts/p6/wps-print.log` |
| Win10 P0 | `dist/p0-win10/result.txt` |
| Win10 P6 结果 | `artifacts/p6/win10-result/result.txt` |
| Win10 P6 kit | `dist/p0-win10/p6/` |
| 安装包 | `dist/PrintPal-0.5.0-win-x64.zip` |
| 测试计划 | `docs/test-plan.md` |
| 安装文档 | `docs/installer.md` |
