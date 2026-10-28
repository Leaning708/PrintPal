# P0：IPP 虚拟打印机捕获可行性验证

> 日期：2026-10-28（Asia/Shanghai）
> 环境：Windows 11 26300 主机 + Windows 10 22H2 19045 VM
> 结论：**Windows 11 与 Windows 10 均已实测通过，P0 网关通过。**
> 证据目录：`artifacts/p0/capture/`、`artifacts/p0/*.log`、`dist/p0-win10/result.*`

---

## 1. 要回答的问题

1. Windows 能否把一个本机 HTTP IPP 服务添加为打印机队列？
2. 用户打印后，Windows 实际发给服务的是什么（方法、属性、文档格式）？
3. 能否拿到 **页数**（从 PDL 解析）、**份数**、**单双面**、**纸张**、**颜色**？
4. 这个链路能否支撑 PrintPal 的计价、路由、转发？

---

## 2. 方法

- `tools/PrintPal.IppProbe`：Kestrel 监听 `127.0.0.1:8630/ipp/print`，实现
  `Get-Printer-Attributes`、`Validate-Job`、`Print-Job`、`Create-Job`、
  `Send-Document`、`Get-Job-Attributes`、`Cancel-Job`。
  保存请求 JSON、文档、PDF 页数、SHA-256。
- `tools/PrintPal.PrintProbe`：WinForms `PrintDocument`，可控页数、份数、单双面、颜色、纸张，
  走与真实应用相同的 GDI → Spooler → IPP Class Driver 路径。
- `scripts/p0/Test-WpsPrint.ps1`：WPS Writer COM（`KWps.Application`）创建 3/5 页文档，
  设置 `ActivePrinter`，通过 `PrintOut` 指定份数，真实应用路径。
- `scripts/p0/Reset-And-Install-P0Queue.ps1`：重启 Spooler、删除/重建队列，刷新驱动能力缓存。
- Windows 侧：`Add-Printer -IppURL http://127.0.0.1:8630/ipp/print`，
  随后 `Rename-Printer` 为“PrintPal 虚拟打印机”。

---

## 3. 实测结果（Windows 11）

### 3.1 打印任务矩阵

| # | 来源 | 请求设置 | 捕获 copies | 捕获 sides | 捕获 color | 捕获 paper | PDF 页数 | 结果 |
|---|---|---|---|---|---|---|---|---|
| C1 | PrintProbe | 5 页 × 3 份，双面长边，彩色，A4 | **3** | **two-sided-long-edge** | **color** | **A4** (20999×29699) | **5** | ✅ |
| C2 | PrintProbe | 4 页 × 3 份，双面长边，黑白，A4 | 3 | two-sided-long-edge | monochrome | A4 | 4 | ✅ |
| C3a | PrintProbe | 5 页 × 1 份，单面，黑白，A3 | 1 | one-sided | monochrome | **A4（错误）** | 5 | ⚠️ 媒体关键字问题，见 4.4 |
| C3b | PrintProbe | 2 页 × 1 份，单面，黑白，A3（修复后） | 1 | one-sided | monochrome | **A3** (29699×41999) | 2 | ✅ |
| C4 | PrintProbe | 3 页 × 2 份，双面短边，彩色，A5 | 2 | two-sided-short-edge | color | A5 (14800×20999) | 3 | ✅ |
| C7 | PrintProbe | 50 页 × 1 份，单面，黑白，A4 | 1 | one-sided | monochrome | A4 | **50** | ✅ |
| W1 | **WPS Writer** | 3 页 × 2 份，单面，黑白，A4 | **2** | one-sided | monochrome | A4 | **3** | ✅ **未摊平** |
| W2 | **WPS Writer** | 5 页 × 2 份，双面长边，彩色，A4（队列默认双面/彩色） | **2** | **two-sided-long-edge** | **color** | A4 | **5** | ✅ |

**关键发现：**

- **WPS 在 IPP Class Driver 下正确传递 `copies`**（W1/W2 均为 2），没有出现旧 PSA 调研里
  “3 页 × 2 份变成 6 页、copies=1”的摊平现象。旧结论是应用 + 驱动/对话框组合的结果，不能
  直接套到 IPP 路线。
- 页数来自**解压后的 PDF**（PdfPig），不是 IPP 属性；5/4/50/3 页均准确。
- 单双面、颜色、纸张、份数均来自 IPP 作业属性；纸张来自 `media-col` 的嵌套 `media-size`。

### 3.2 文档格式

- `document-format = application/pdf`
- `compression = gzip`
- 文档是 **gzip 压缩的 PDF**；解压后 PDF 头为 `%PDF-1.7`。
- 页数用 PdfPig `NumberOfPages` 统计；50 页文档解压后 2,709,113 字节，正常。

### 3.3 Windows 实际发送的 IPP 操作

对一个任务，驱动会发送：

```
Get-Printer-Attributes   (能力查询，建队列/刷新/状态，大量重复)
Validate-Job             (作业属性预校验)
Create-Job               (创建作业，带全部作业属性)
Send-Document            (document-format=application/pdf, compression=gzip, last-document=true)
Get-Job-Attributes       (查询 job-state)
Get-Printer-Attributes   (定期状态轮询)
```

统计：9 个任务期间共 123 次 `Get-Printer-Attributes`、9 次 `Validate-Job`、9 次
`Create-Job`、9 次 `Send-Document`、9 次 `Get-Job-Attributes`。
实现时必须能快速、稳定地响应能力查询。

### 3.4 作业属性（Create-Job 请求，C1 为例）

| 属性 | 值 | 用途 |
|---|---|---|
| `job-name` | P0 5p x3 duplex color v6 | 展示/路由 |
| `requesting-user-name` | `LEE-WIN\Lee` | 展示 |
| `copies` | **3** | 份数 |
| `sides` | **two-sided-long-edge** | 单双面 |
| `print-color-mode` | **color** | 颜色 |
| `print-quality` | 4 | 分辨率/质量 |
| `printer-resolution` | 600x600dpi | — |
| `orientation-requested` | 3（纵向） | 方向 |
| `job-pages-per-set` | 5 | 每份页数（可作为页数交叉校验） |
| `job-impressions` | 5 | **每份**印张，不是总印张 |
| `job-media-sheets` | 15 | **页数 × 份数**，不是实际纸张数（双面下仍为 15，不是 9） |
| `media-col` | `media-size{x=20999,y=29699}`, `media-type=stationery` | 纸张 |

> **计价不要用 `job-media-sheets`。** 双面 5 页 × 3 份时它是 15（面数），不是
> `2 张双面 + 1 张单面 = 3 张/份 × 3 = 9 张`。PrintPal 必须按“每份页数 → 每份双面/单面张数
> → × 份数”自己算。

### 3.5 纸张识别

`media-col` 集合结构：

```
media-col
 ├─ media-type = stationery
 └─ media-size
      ├─ x-dimension = 20999 (1/100 mm)
      └─ y-dimension = 29699
```

- A4 → 20999×29699；A5 → 14800×20999；A3 → 29699×41999。
- 1/100 mm 单位；容差 ±300 可稳定映射到 A4/A3/A5/Letter/Legal。
- 如果驱动送 `media` 关键字，也可直接使用。

---

## 3.6 Windows 10 22H2 实测结果

环境：Windows 10 教育版 build **19045.6456**，VMware VM，自包含 P0 包。

### 3.6.1 与 Win11 的差异：IPP Class Driver 默认不显示

`Get-PrinterDriver` 初始没有 `Microsoft IPP Class Driver`。驱动 INF 实际在 DriverStore 中：

```
C:\Windows\System32\DriverStore\FileRepository\prnms012.inf_amd64_0c1cc538f0358883\Amd64\MSIPP.xml
```

Win10 把 IPP Class Driver 的 `ExcludeFromSelect` 写进 INF，所以“添加打印机 → 厂商/型号”里看不到它。
显式安装即可：

```powershell
Add-PrinterDriver -Name 'Microsoft IPP Class Driver'
```

安装后 `Get-PrinterDriver` 显示 v4 驱动，队列可以正常创建。
**生产安装器必须在 Win10 上先检测并执行这一步。**

### 3.6.2 捕获结果

| # | 来源 | 请求设置 | 捕获 copies | 捕获 sides | 捕获 color | 捕获 paper | PDF 页数 | 文档大小 |
|---|---|---|---|---|---|---|---|---|
| W10-1 | PrintProbe | 5 页 × 3 份，双面长边，彩色，A4 | **3** | **two-sided-long-edge** | **color** | **A4** (21000×29700) | **5** | PDF 567,872 B |
| W10-2 | PrintProbe | 3 页 × 2 份，单面，黑白，A4 | 2 | one-sided | monochrome | A4 (21000×29700) | 3 | PDF 564,698 B |
| W10-3 | PrintProbe | 2 页 × 1 份，单面，黑白，A3 | 1 | one-sided | monochrome | **A3** (29700×42000) | 2 | PDF 375,604 B |
| W10-4 | PrintProbe | 20 页 × 1 份，单面，黑白，A4 | 1 | one-sided | monochrome | A4 (21000×29700) | **20** | PDF 1,147,686 B |

- Windows 10 发送的也是 **PDF**（未 gzip，`compression` 为空）；PdfPig 页数准确。
- 纸张尺寸比 Win11 更“整”：A4 精确 21000×29700，A3 29700×42000。
- `errors.txt`：`No Universal Print errors in the last 20 minutes.`，没有属性类型错误。
- 队列创建：`Add-Printer -IppURL` 在 Win10 19045 上也可用；它创建了队列但**不返回对象**，
  后续 legacy `Add-Printer` 报“指定的打印机已存在”；按名称轮询即可。与 Win11 行为一致。
- 队列端口仍是 IPP Port（WSD Port Monitor 下），数据路径是 IPP，不需要 WSD。

**Win10 P0 通过。**

---

## 4. 踩坑与修复（全部有实测证据）

### 4.1 `printer-uri-supported` 必须是 `ipp://`

最初只返回 `http://127.0.0.1:8630/ipp/print`，队列被标记为 **Offline / Error**，任务卡在
“Error, Printing, Retained”，`Size=0`。加入 `ipp://127.0.0.1:8630/ipp/print`（并保留 HTTP URL）
后，任务正常进入 `Create-Job`/`Send-Document`。

**生产实现：** `printer-uri-supported` 至少包含请求所用的 `ipp://` URI；
`uri-security-supported`/`uri-authentication-supported` 与 URI 一一对应。

### 4.2 IPP 属性类型必须精确

Windows 的 IPP 解析错误在 **Application 日志**（Provider `Universal Print`）里，
例如：

```
GetIntegerValue (media-left-margin-supported) - not an integer type: 0x33
GetIntegerValue (finishings-default) - not an integer type: 0x44
GetStringValue (document-format-details-supported) - not a string type: 0x22
```

修复：

| 属性 | 错误类型 | 正确类型 |
|---|---|---|
| `media-left/right/top/bottom-margin-supported` | rangeOfInteger (0x33) | **integer (0x21)** |
| `finishings-default` / `finishings-supported` | keyword (0x44) | **enum (0x23)**，值为 3（none） |
| `document-format-details-supported` | boolean (0x22) | **keyword/string (0x44)** |

修复后 Application 日志不再出现这些错误，任务完成。

### 4.3 `media-size-supported` 集合是必需的

只给 `media-supported` 关键字不够。Windows 用 `media-size-supported` 集合（`x-dimension`/
`y-dimension`，1/100 mm）建立打印对话框和渲染尺寸。加入 A4/A3/A5/Letter/Legal 后，
队列稳定为 Normal。

### 4.4 A3 关键字方向

最初写 `iso_a3_420x297mm`，驱动生成的是一个自定义选项
`ns0001:_420x297mm`，打印 A3 时实际收到 A4（20999×29699）。
改成 **`iso_a3_297x420mm`**（纵向尺寸）后，`Get-PrintConfiguration` 中出现 `ISOA3`，
A3 任务正确捕获为 29699×41999。

**生产实现：** 所有标准媒体关键字用“纵向”尺寸，例如
`iso_a4_210x297mm`、`iso_a3_297x420mm`、`iso_a5_148x210mm`。

### 4.5 gzip 与集合解析

- Windows 以 `compression=gzip` 发送 PDF；必须先 `GZipStream` 解压，再嗅探/保存/数页。
- `media-col` 是嵌套集合；扁平解析会把 `media-size` 的 x/y 值错误地追加到 `copies` 上
  （日志里出现 `copies = 3 | <10 bytes> | ... | 20999 | ... | 29699`）。
  必须实现递归集合解析。
- `Create-Job` 与 `Send-Document` 分离：作业属性在 Create-Job，文档在 Send-Document；
  必须按 `job-id` 关联。

### 4.6 `Add-Printer -IppURL` 返回空对象

在 build 26300 上，`Add-Printer -IppURL` **成功创建队列但不返回对象**。
安装脚本不能以返回值判断失败；应按队列名轮询 `Get-Printer`，再 `Rename-Printer`。
队列的实际端口是 **IPP Port**（WSD Port Monitor 下），数据路径是 IPP；不需要实现 WSD。

### 4.7 卡住的任务

失败的 P0 迭代会让队列进入 `Error, Printing, Retained`，任务 `Size=0`。
P0 用 `Restart-Service Spooler` + 删除队列解决；生产安装/卸载不应重启 Spooler，
只在诊断脚本中作为最后手段。

### 4.8 队列离线不代表 IPP 服务不可达

驱动会持续 `Get-Printer-Attributes`（状态轮询）；队列 Offline 时任务可能仍在
Spooler 里等待。排查顺序：IPP 响应类型 → `printer-uri-supported` → 队列/端口状态 →
Application 日志的 `Universal Print` 错误。

---

## 5. P0 网关判定

| 项 | 结果 |
|---|---|
| Windows 11 建本机 IPP 队列 | ✅ |
| Windows 11 捕获任务 | ✅ |
| 页数（PDF 解析） | ✅ 2/3/4/5/50 页 |
| 份数 | ✅ 1/2/3 |
| 单双面 | ✅ one-sided / two-sided-long-edge / two-sided-short-edge |
| 颜色 | ✅ monochrome / color |
| 纸张 | ✅ A4/A5/A3（A3 需纵向关键字） |
| 真实应用 WPS | ✅ copies 正确，未摊平 |
| **Windows 10 22H2** | ✅ 队列 + PDF + 页数 + 份数/单双面/颜色/纸张全部通过 |
| **Windows 10 驱动前置** | ✅ 需显式 `Add-PrinterDriver -Name 'Microsoft IPP Class Driver'` |

**结论：Windows 11 + Windows 10 均通过，IPP-first 架构成立，进入 P1。**

Win10 发送的也是 PDF（不是 PWG），页数解析路径与 Win11 相同；PWG 解析器仍保留作为
未来版本/其他驱动的兜底。Win10 的唯一额外步骤是安装 IPP Class Driver（INF 在
DriverStore 中，但被 `ExcludeFromSelect` 隐藏）。

---

## 6. 对 P1 的直接要求

1. `PrintPal.Ipp` 必须实现：
   - 递归集合解析/编码（`media-col`、`media-size`、`media-size-supported`）；
   - gzip 解压（`compression` 属性）；
   - `Validate-Job` / `Create-Job` / `Send-Document` / `Get-Job-Attributes`；
   - 精确属性类型（见 4.2）与 `ipp://` URI。
2. 捕获服务必须按 `job-id` 关联 Create-Job 与 Send-Document。
3. 页数：PDF 用 PdfPig；PWG 用页头/页数解析（Win10 可能的格式）；PS/OXPS 作为可选。
4. 纸张：优先 `media`，否则 `media-col`/`media-size`（1/100 mm，容差 ±300）。
5. 队列安装：
   - Win11：`Add-Printer -IppURL` + 轮询 + Rename；
   - Win10：先 `Add-PrinterDriver -Name 'Microsoft IPP Class Driver'`，再 `Add-Printer -IppURL`
     （或 legacy `-PortName`），同样轮询 + Rename；
   - 两者都需要管理员。
6. 计价：只用 `pagesPerCopy`、`copies`、`sides`、`color`、`paper`，**不要用**
   `job-media-sheets`/`job-impressions` 作为账单总数。
7. WPS：IPP 路径下 copies 正确；仍保留手工修正作为兜底。
8. Win10 安装器必须处理 IPP 驱动：`prnms012.inf` 在 DriverStore 中但被 `ExcludeFromSelect`
   隐藏，显式 `Add-PrinterDriver` 即可。

---

## 7. 证据

| 文件 | 内容 |
|---|---|
| `artifacts/p0/capture/requests.jsonl` | 每次 IPP 请求（方法、属性、集合） |
| `artifacts/p0/capture/jobs.jsonl` | 每个任务：属性、文档格式、页数、SHA-256、纸张 |
| `artifacts/p0/capture/*.pdf` | 实际收到的 PDF |
| `artifacts/p0/capture/probe.log` | 探针控制台日志 |
| `artifacts/p0/install-queue.log` | 队列安装 transcript |
| `artifacts/p0/reset-install.log` | P0 重置/重建 transcript |
| `artifacts/p0/wps-print.log` | WPS 单面测试 transcript |
| `artifacts/p0/wps-print-duplex.log` | WPS 双面测试 transcript |

---

## 8. 待办

- [x] Windows 10 22H2 VM：运行自包含 P0 包，确认 `Add-PrinterDriver`、队列、PDF、页数、copies/sides/color/paper。
- [x] Win10 发送格式：PDF（未 gzip），与 Win11 相同；PWG 解析器保留为兜底。
- [ ] 生产捕获服务把本探针的属性集固化为 `PrinterCapabilities`，并加单元测试。
- [ ] 研究 `media-col-database`/`media-col-default` 是否需要补齐（当前未提供，队列可用；打印对话框媒体列表已由 `media-size-supported` 生成）。
- [ ] 安装器在 Win10 上自动检测并安装 IPP Class Driver（`Add-PrinterDriver`），失败时给出明确提示。
