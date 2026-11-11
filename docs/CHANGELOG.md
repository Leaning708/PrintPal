# PrintPal 变更记录

本项目按阶段推进；每个阶段结束更新本文件、`docs/architecture.md`、`docs/test-plan.md`。

---

## 2026-11-11 — 侧栏与主界面间距收窄

- 分隔条列宽 `6 → 3`、`MainSurface` 左边距 `3 → 1`：收起为图标模式时，导航色带从 73px 收到 68px，
  图标相对色带中心的偏移从约 4.5px 降到约 2px（弱化"右侧间隙更大"的观感）；
- 保留卡片效果（其余三边仍是 3px 导航色边），展开态布局略微更紧凑；
- 拖拽条 3px 仍可正常拖动（已实测展开/收起）；
- 另：删除此前用于确认位置的临时红线标记。

## 2026-11-11 — 右键菜单删除项上方分隔线

- 打印机、收银台（任务）、打印流水三个右键菜单的「删除」项上方统一显示分隔线；
- 新增 `MenuSeparator` 样式并修复原 `Separator` 因高度=1 + 内边距导致线条被裁切、实际不可见的问题；
- 文档：`docs/ui.md` 无需变更（菜单项未变）。

## 2026-11-11 — 打印机列表右键菜单

- 打印机行右键菜单：**编辑、测试连接、启用/停用、删除**（与工具栏能力一致）；
- 菜单项「启用/停用」按当前状态动态显示；停用后状态列立即「已停用」，重新启用会立即探活一次；
- 右键自动选中该行；列表空白处不弹菜单；
- 文档：`docs/ui.md` §6 增加右键菜单说明。

## 2026-11-11 — 打印机列表/编辑窗口细节打磨

- 列表：名称列不再带协议副标题，「协议」独立为第二列；「状态」列居中对齐、垂直居中，
  只显示 pill（详细时间放 tooltip）；其余列保持默认位置与对齐。
- 必填项红色 `*` 与标签之间留 3px 间距（新增 `RequiredStar` 样式）；价格编辑同步。
- 重新打开打印机编辑窗口会恢复上一次测试结果（`连接成功 · 日期时间` / `连接失败 · 上次成功 日期时间`），
  不再固定显示「尚未检测」。
- 文档：`docs/ui.md` §6 同步列布局与状态列说明。

## 2026-11-11 — 打印机状态自动检测 + 编辑窗口收敛

- 列表「最近测试」「启用」合并为「**状态**」列（两行：pill + 详细日期）：
  - 在线：本次检测时间；连接失败：`上次成功 日期`（从未成功只显示「连接失败」）；
  - 未检测（原「未测试」）；已停用（优先于其它状态，停用保存后立即生效）。
- **自动检测**：App 启动立即一轮 + 每 60 秒一轮；仅启用中的打印机；IPP 用 `Get-Printer-Attributes`（5s 超时），
  Raw 用 TCP（3s）；结果只更新状态与时间，不自动修改打印能力；服务不做健康检查。
- 数据：`Printers` 新增 `LastSuccessAtUtc`（失败时保留最后一次成功时间）。
- 编辑窗口：
  - IPP 下「文档格式」置灰并固定「自动（IPP 协商）」，Raw 9100 才可选；
  - 「IPP 路径」改名「**资源路径**」，仅 IPP 显示（Raw 时隐藏，仍需要地址与端口）；
  - 「检测到的能力」改为四行：格式 / 颜色 / 纸张 / 单双面；
  - 测试结果带详细日期：成功 `连接成功 · yyyy-MM-dd HH:mm:ss`，失败 `连接失败 · 上次成功 yyyy-MM-dd HH:mm`；
  - 必填项统一加红色 `*`（名称、协议、主机/IP、端口、纸张、颜色、单双面；价格编辑的单/双面价同步）。
- 校验：打印能力三项必填校验抽到 Core（`PrinterCapabilityValidation`），新增单测。
- 不做升级迁移：开发库一次性 `ALTER TABLE` 加列，正式安装为全新库。
- 测试：Core 80 / Ipp 12 / Capture 10 / Storage 30 / Integration 15 全过。

## 2026-11-11 — 打印机页收敛：打印能力 + 去掉列表能力列 + 不做升级迁移

- 打印机列表移除「路由能力」「检测能力」两列，只保留名称 / 地址 / 最近测试 / 启用。
- 打印机详情页「路由能力」更名为「**打印能力**」；纸张复选框移除「任意纸张」，至少勾选一种纸张；
  颜色 / 单双面至少各勾选一种。
- 检测结果自动填充：测试连接成功后**自动填充**打印能力；按钮改为「按检测结果自动填充」，可随时重新套用。
- 「检测到的能力」改为**每类独占一行**（纸张 / 颜色 / 单双面）。
- **不做升级迁移**：产品尚未正式使用，删库重装即可；移除价格表收紧与旧路由规则合并的迁移代码，
  新库直接按当前 schema 建表（`PriceRules` 唯一索引、`Printers.RoutingCapabilityJson` 都在建表脚本里）。
- 测试：Core 68 / Storage 29 / Integration 15 全过（删除两个只验证迁移路径的用例）。

## 2026-11-11 — 打印机能力并入打印机页（取消路由规则页）

- 路由规则页、编辑窗口、导航项**全部移除**；能力声明合并进打印机编辑窗口：
  勾选支持的纸张（A3/A4/A5 或任意）、颜色（黑白/彩色）、单双面（单面/双面）。
- 能力语义改为**产品模型（笛卡尔积）**：勾了「A4/A5 + 彩色」即代表 A4/A5 都支持彩色，
  不再精确声明单个组合；一台机一份能力，天然没有重复声明问题。
- 数据库：`Printers` 新增 `RoutingCapabilityJson`；升级时把旧 `RoutingRules` 按打印机合并
  （并集）写入后 **DROP TABLE**；无旧规则则按检测能力预填，仍无则「未声明（不参与路由）」。
- 路由逻辑不变：候选集 = 能力覆盖的启用打印机；无候选 → `Unrouted`（无默认打印机）；
  候选内按「同单优先 → 跨账单均衡」挑选。
- 打印机页新增「路由能力」列（未声明会提示），编辑窗口新增能力勾选区与「按检测结果填充」。
- 人工选择目标只列候选集内打印机；设置页转发说明同步。
- 测试：Core 68 / Storage 31 / Integration 15 全过（新增能力圆整、旧规则迁移用例）。
  设计见 `docs/decisions/ADR-011-capability-on-printer.md`（取代 ADR-010 的存储方式）。

## 2026-11-11 — 账单亲和路由：规则即能力声明 + 价格表收紧

- **路由规则 = 打印机能力声明**：去掉优先级、每份页数范围、来源应用正则、文档名正则；同一打印机不允许重复声明
  同一 `(纸张, 颜色, 单双面)` 组合，保存时校验并报冲突；规则名按「打印机 · 纸张 · 颜色 · 单双面」自动生成。
- **去掉默认打印机**：没有任何规则命中 → 任务 `Unrouted`；人工选择目标只列出规则声明支持的打印机，不支持的置灰/不出现。
- **选择策略改为「候选集 + 同单优先 + 跨账单均衡」**：规则命中的目标打印机取并集；优先本单已用机器（任务数多/最近使用），
  否则在候选里选其它未结账单占用最少（0 优先）→ 最久未使用 → 名称序，实现"不同账单尽量不同机"。
- **能力限制**：规则编辑时若打印机能力已检测，纸张/颜色/单双面只能勾选支持项；未检测（Raw 9100）不限制。
- **价格表收紧**：唯一键 `(纸张, 颜色)`、去优先级/启用/“其他纸张”通配行，默认表 6 行；
  缺价任务 `NeedsReview`（「未配置该纸张/颜色的价格」）并阻止结账、提示补价。
- **升级迁移**：自动删除价格表通配行、拆分“任意颜色”行、同组合去重、建唯一索引；
  没有任何路由规则时按已检测能力自动生成能力声明规则，避免升级后全部未路由。
- 设置→转发新增「同一账单优先使用同一台打印机」（`billAffinity`，默认开）。
- 数据库：新增 `PrintPalDatabase.GetPrinterUsageSnapshotAsync`（本单/其它未结账单/最近使用）；旧列保留但废弃。
- 测试：Core 71、Storage 30、Integration 15（新增同单同机集成用例与迁移用例）。设计见 `docs/decisions/ADR-010-bill-affinity-routing.md`。

## 2026-11-11 — 双面简化：移除长边/短边参数

- 打印链路不再区分长边/短边：`DuplexMode` 仍保留 Unknown / Simplex / TwoSidedLongEdge / TwoSidedShortEdge 四个值，
  但入库、匹配、展示前统一 `Canonical()` 成双面（TwoSidedLongEdge）；IPP `sides` 统一发 `two-sided-long-edge`，
  PrintTicket 双面统一 `TWOSIDEDLONGEDGE`；旧数据里的 `two-sided-short-edge` / `PSK:TWOSIDEDSHORTEDGE` 读入即归一。
- 路由规则：编辑窗口的双面条件从「长边 / 短边」两个勾选合并为「单面 / 双面」两个；判定改为 `IsDuplex()`，
  任意双面规则匹配任意双面任务；规则摘要显示「单面 / 双面」，不再带装订边。
- 打印详情与流水规格统一显示「A4 · 彩色 · 双面」；维度统计把长边/短边合并为同一「双面」行。
- 测试同步：Core 63 条（新增 6 条双面归一/匹配用例），Storage 规格断言改为「双面」。

---

## 2026-11-11 — 界面字体 + 打印流水列表改版

- 设置「外观」新增**界面字体**下拉（本机已安装字体，默认「Microsoft YaHei UI」），选中立即生效并持久化
  `uiFontFamily`；界面字体抽成主题 token `UiFontFamily`，图标（Segoe MDL2）与等宽数值（Consolas）不跟随。
- 修复「改字体后左侧导航栏不跟」：`UiFontFamily` 定义在合并字典里，只在 `Application.Resources` 根上覆盖
  不会通知已解析的 `DynamicResource`；改为直接改合并字典条目，并给现有窗口设本地 `FontFamily` 兜底继承链。
- 打印流水列表：移除「类型」列，列序改为 流水号 / 文档 / 所属账单 / 时间 / 金额 / 状态 / 目标打印机；
  所属账单未归属时显示告警色；状态列改用收银台同款**方形色块 + 圆点**（全局 `StatusPillTemplate` 一起改）。
- 其他视觉修正：未结账单 chip 序号对比度、挂起弹窗与 `+N` 弹窗的行号颜色、`+N` 按钮变窄、
  打印流水「所属账单」垂直居中、设置卡片标题与内容间距、表格行悬停统一 `HoverFillBrush`。

---

## 2026-11-11 — 统计页等权重伸缩 + 侧边栏可拖动收起

- 统计页：每日明细 / 维度统计 / 统计图三张卡片**等权重**伸缩（左右 1:1、上下各占一半），
  统计图去掉固定 300 高、画布随卡片高度重绘；两张表列宽改星号 + `MinWidth`，窄窗口出现横向滚动条。
- 侧边栏：去掉展开/收起按钮，改为**右边沿拖动**（64–360px，< 170 自动切图标模式，松手吸附 64 或 ≥180），
  宽度持久化 `sidebarWidth`（兼容旧的 `sidebarCollapsed`）；窗口 < 1000 强制图标模式；
  图标在窄栏内水平居中；拖动区不可见（不画分隔线、悬停不高亮）。
- `docs/ui.md` 同步第 4、5、7、9、13 节。

---

## 2026-11-10 — 撤回表格表头底色

- 表格表头恢复为 `Transparent`（与改版前一致）：紧凑/完整模式的表头都没有底色，
  只靠底部 1px 发丝线与数据行分隔；行悬停（`HoverFillBrush`）等其它改版内容保持不变。
- 删除只服务表头色带的主题 token `HeaderBarBrush`（`Theme.xaml` 定义 + `ThemeManager.Make()`
  里的中性灰计算），避免留下不再生效的配置项。
- 同步 `docs/ui.md` 第 10 节。

---

## 2026-11-09 — 修复：极简模式在 Win10 上的白色边角

- 极简模式的卡片与顶部 3px 色条原本是固定 14px 圆角（按 Win11 的 DWM 圆角窗口设计）。
  Win10 窗口是直角，色条两端的圆角会露出白色的窗口背景，四角看起来像“白边凸出来”。
- 新增 `UpdateMinimalCornerRadii()`：Win11 保持 14px 圆角，Win10 上卡片与色条都改为直角；
  色条宽度与窗口齐边（`MainWindow.xaml` 给色条加了 `x:Name="MinimalTopBar"`）。
- 同步 `docs/ui.md` 第 11 节。

---

## 2026-11-09 — 紧凑模式界面改版（窗口边界 / 卡片分层 / 收银台视觉重心）

- **窗口边界**：新增 `WindowFrame`（`MainWindow.xaml`）。Win10 上给无边框窗口画 1px 描边，
  Win11 由 DWM 负责圆角+阴影所以不重复画（最大化、极简模式也不画），
  解决「紧凑模式放在白底 APP 上看不出窗口边界」的问题。厚度由 `UpdateWindowFrame()` 按
  OS / 模式 / 最大化状态切换。
- **卡片化**：紧凑模式窗口底色改为页面背景色，内容是一张 6px 内缩的白卡（上下圆角 6），
  标题栏 44→46px；标题栏 / 结账栏 / 表格用 1px 画框与发丝线分层。
- **表头色带**：新增主题 token `HeaderBarBrush`（`CardBrush` + 中性灰混合，10 套主题自动适配，
  不跟主题色相走，避免整屏同色发“脏”），`GridColumnHeaderStyle` 改用它；行悬停统一为
  `HoverFillBrush`。
- **收银台视觉重心**：金额 22→24px、与按钮间距 14→18px；`结账` 84×34→96×36 主按钮，
  `挂起` 降级为 `SubtleButton`；任务行高 30→34；金额列连同表头改右对齐；
  「模式/置顶」与窗口按钮之间加竖分隔线。
- 同步 `docs/ui.md` 第 10 节与截图 `docs/images/ui-simple.png`。

---

## 2026-11-08 — 文档校正：开发启动时 UI 日志位置

- `printpal-dev-env` skill 的环境表原文写「服务模式 UI 日志仍在 `%LOCALAPPDATA%\PrintPal\logs\app.log`」，
  与实际不符：`App.xaml.cs` 用 `startup.Agent.DataDirectory` 建日志目录，日志**跟随 `--data`**。
  开发启动（`Start-PrintPalApp.ps1` 默认 `artifacts\p3\data`）时日志在
  `artifacts\p3\data\logs\app.log`，服务模式也一样；只有裸启动 / 安装版默认才落在
  `%LOCALAPPDATA%\PrintPal\logs\app.log`。已按实测更正（2026-11-08 22:49 启动后该文件有最新写入）。

---

## 2026-11-08 — 许可证改为 GPL-3.0

- 新增 `LICENSE`（GNU GPL v3），仓库以 **GPL-3.0** 发布；
- `README.md` 增加「许可证」一节，指向 `LICENSE`；
- MSI 许可页（`packaging/PrintPal.Msi/Ui.wxs`）仍是内联通用文本，未随许可证改动，
  如需与 GPL 一致可后续调整。

---

## 2026-11-07 — 安装器：产品图标、卸载向导、卸载删队列、进度条说明

- **向导显示产品图标**：`packaging/PrintPal.Msi/PrintPal.ico`（从 `PrintPal.App.exe` 提取），
  MSI 里加 `Binary Id="PrintPalProductIcon"`，欢迎/维护/卸载页左上角显示 32×32 产品图标。
  （标题栏/任务栏图标仍由 msiexec 提供——纯 MSI 内部 UI 改不了，要改得上 Burn bootstrapper。）
- **卸载/修复不再套安装向导**：
  - `Installed` 时显示 `MaintenanceDlg`（修复 / 卸载 / 取消）；
  - `REMOVE~="ALL"` 时显示 `RemoveDlg` 确认页（说明会删程序、服务、打印队列，数据保留）；
  - 完成页按 `PRINTPAL_ACTION` 显示「安装完成」或「卸载完成」。
- **卸载时真的删打印队列**：`Remove-PrintPalQueue` 原来是 `Remove-Printer -ErrorAction Continue`
  失败就静默跳过（日志里 `0x80070001`）；现在失败会用 `printui.dll,PrintUIEntry /dl` 兜底、
  等 spooler 异步删除完成（最多 20s）、再清理端口，并写日志。
- **进度条"到头又从头"**：这是 Windows Installer 内部 UI 的固有行为（rollback/安装脚本多趟进度），
  官方（Rob Mensching）确认只有外部 UI handler / Burn 才能变成一条进度；
  我们保留「当前步骤」文案，不做假进度。需要单条进度条的话再上 Setup.exe（Burn）。
- **版本 1.0.3**：1.0.2→1.0.2 同版本不会被 MajorUpgrade 识别，必须升版本才能覆盖安装。
- 文档：`installer.md`、`printpal-msi` skill 同步。

---

## 2026-11-06（四）— 进度条不动、失败页按钮没反应、队列仍建不出来的修复

- **进度条一直是灰的**：`ProgressDlg` 的 `ProgressBar` 少了
  `<Subscribe Event="SetProgress" Attribute="Progress" />`，MSI 推的进度没人接收；
  已补上（EventMapping 里可见 ProgressDlg/ProgressBar → SetProgress）。
- **失败页按钮点了没反应**：`WixQuietExec` 作为「即时」自定义动作在 UI 里起不到进程
  （日志里只有 "Doing action"，没有 WixQuietExec 输出）。改为官方推荐给按钮用的
  **`WixShellExec`**（ShellExecute）：「打开日志文件」= 用默认程序打开
  `[MsiLogFileLocation]`，「打开日志文件夹」= 打开 `%TEMP%`。
- **失败时自动把日志放到公共桌面**：新增回滚动作 `CopyLogOnFailure`
  （rollback + `cmd /c copy` → `[%PUBLIC]\Desktop\PrintPal-InstallLog.txt`），
  用户不用再一层层翻 `AppData\Local\Temp`。
- **队列仍失败（日志确认为 `Add-Printer -IppURL` 拒绝访问）**：
  - 等 `printui/Remove-Printer` 的异步删除真正完成（最多 20s），避免撞上旧队列；
  - 清理没有被任何打印机引用的 `WSD-*` 残留端口（多次失败安装会留下）；
  - **拿不到 WSD IPP 端口时不再让整包回滚**：保留 HTTP 端口队列并写 WARNING，
    安装继续完成（否则用户什么都装不上）；若后续发现份数/双面/彩色丢失再继续查。
- 说明：`Impersonate` 从 yes 改 no 的上一版并没有解决队列问题（SYSTEM 下同样拒绝访问），
  因此这次改为「能装上 + 记清楚原因 + 失败不阻断」。
- 文档：`installer.md` 失败取日志小节、`printpal-msi` skill 同步。

---

## 2026-11-06（三）— 修复双击安装必然失败（队列 CA 权限）＋ 版本升到 1.0.2

- **根因（MSI 日志 + msi-queue.log 确认）**：队列自定义动作 `InstallQueue` 用的是
  `Impersonate="yes"`，双击 MSI 时继承的是**未提权的用户令牌**，于是
  `Add-Printer -IppURL` 报「对指定资源的访问已被拒绝」、`Remove-Printer` 报 0x80070001；
  脚本回退到 `Add-Printer -Name ... -PortName http://...` 把队列建在 **HTTP 端口**上，
  最后抛「used HTTP port ... no WSD IPP queue could be created」→ CA 返回 1603 → 整包回滚。
  从提权 shell 安装（10-28 的验证路径）不会暴露这个问题。
- **修复**：`InstallQueue` 改为 `Impersonate="no"`（以 SYSTEM 运行，与 `RemoveQueue`/
  `RollbackQueue` 一致）；`PrintPal.MsiQueue.ps1` 在 SYSTEM 下跳过「当前用户桌面」快捷方式
  （公共桌面 + 开始菜单仍然创建），避免写到 systemprofile。
- **兜底**：`Add-Printer -IppURL` 仍先用 `http://`（历史验证可拿到 WSD 端口）；
  若拿到的是 HTTP 端口，再用 `ipp://` scheme 重试一次。
- **版本 1.0.2**：`Directory.Build.props` 1.0.1 → 1.0.2。MajorUpgrade 的 `VersionMax=1.0.2`
  才会把 1.0.1 当升级移除；同版本重装不会被识别为升级（会并列安装两份产品）。
- **失败页改进**（用户反馈「打开日志按钮没反应 / 日志路径不能复制 / 目录一层层点很难受」）：
  - 日志路径放进**可选中复制**的 Edit 控件（`MsiLogFileLocation`）；
  - 「复制到桌面」按钮：`CopyMsiLog` CA（`cmd /c copy`）在桌面生成 `PrintPal-InstallLog.txt`；
  - 「打开日志文件夹」按钮：`OpenLogFolder` CA（`explorer.exe /select`），不再用 notepad
    打开（之前 WixQuietExec 起 notepad 没有可见反馈）；
  - 继续显示队列日志 `C:\ProgramData\PrintPal\msi-queue.log` 和命令行取证提示。
- **队列脚本诊断增强**：等服务健康 180s、每 10s 记录服务状态、服务未 Running 时 `Start-Service`，
  失败时记录异常类型 / 出错位置 / 服务状态。
- 文档：`README.md`、`installer.md`、`p7-service-msi.md`、`runbook.md` 的产物路径与版本示例
  更新为 1.0.2；`printpal-msi` skill 同步（含失败日志获取方式）。

---

## 2026-11-06（二）— MSI 向导改为中文单语，补齐失败诊断与安装进度明细

- **替换 WixUI_InstallDir，改为自绘中文向导**：`packaging/PrintPal.Msi/Ui.wxs`
  用 WiX 核心 `UI/Dialog/Control` 实现 欢迎 → 许可 → 安装目录 → 确认 → 进度 → 完成；
  中文单语（上一版尝试过中英双语，用户要求只保留中文，已去掉 `_en` 页面与语言选择页）。
- **失败页给得出信息**（`FatalError`）：
  - MSI 始终写日志（`Property MsiLogging=voicewarmupx`），失败页直接显示
    `[MsiLogFileLocation]` 路径与队列日志 `C:\ProgramData\PrintPal\msi-queue.log`；
  - 新增「打开日志」按钮（`SetOpenMsiLog` + `OpenMsiLog`，WixQuietExec 调 notepad）；
  - 日志路径为空时提示用 `msiexec /i 安装包.msi /l*v %TEMP%\printpal-install.log` 重装。
- **安装进度明细**：`ActionText`（WiX `ProgressText`）表补齐中文步骤文案
  （复制程序文件 / 启动 Windows 服务 / 创建并验证打印队列 / 完成安装 …），
  进度页显示「当前步骤：xxx」+ 进度条 + 主要步骤说明。
- **队列脚本更耐等**：`PrintPal.MsiQueue.ps1` 等服务健康从 45s 放宽到 180s，
  每 10s 记录一次服务状态，服务未 Running 时尝试 `Start-Service`，
  失败日志补充异常类型、出错位置与服务状态（便于定位 1603）。
- **数据库改为中文代码页**：`Package` 增加 `Language="2052" Codepage="936"`
  （默认 1252 存不下中文，编译报 WIX0311）。
- **许可文本内联**，`packaging/PrintPal.Msi/License.rtf` 删除；勾选「我接受」后才能下一步
  （`LICENSEACCEPTED`，大写才是公开属性）。
- **安装目录仍可选**：`WIXUI_INSTALLDIR`（Indirect PathEdit + `SetTargetPath`）+ 浏览对话框。
- **依赖精简**：不再需要 `WixToolset.UI.wixext`（只留 `WixToolset.Util.wixext` 提供 WixQuietExec）。
- 验证：`msiexec /i` 实跑向导并逐页截图（欢迎/许可/目录/确认/取消）；管理安装解包 642 文件；
  MSI 元数据（ProductLanguage 2052、简体中文对话框、ProgressText 表、MsiLogging）核对。
---

## 2026-11-06 — 滚动条跟随每套主题色调、极简模式选择器配色、通知滚动真正贴边

- **滚动条跟随每套主题的色调**：`ThemeManager` 不再让所有浅色主题共用一个蓝灰、所有深色主题共用一个深灰，
  而是按每套主题的 `MutedBrush` 与 `CardBrush` 混合派生 `ScrollThumbBrush / Hover / Pressed` 三态
  （暖橙主题是暖灰、墨绿主题是绿灰、深蓝主题是蓝灰……），浅色/深色主题之间切换时滚动条颜色都会变化。
- **修复：极简模式模式选择器颜色不跟随主题**。`SetDarkModeBar` 里硬编码的深蓝/深灰改为主题 token：
  激活项用 `MinimalChipActiveBrush` + 白色图标，非激活项透明（露出外层 `MinimalChipBrush` 容器）、
  图标用 `MinimalMutedBrush`；浅色主题下不再出现突兀的深色按钮，与完整/紧凑模式的模式切换器同一套思路。
- **修复：极简通知滚动仍然过头**。上一轮只把文本改成 `HorizontalAlignment="Left"`，但 TextBlock 直接放在
  Grid 里仍会被按可视区宽度测量/裁切，`DesiredSize` 只有 243px，平移 `-(textWidth-hostWidth)` 后整块移出可视区。
  本轮把 TextBlock 放进 `StackPanel`（横向、无限宽度），文本按自然宽度排版，`Measure(infinity)` 得到真实宽度，
  滚动到最后一个字贴住右边缘即反向，不再出现大片空白。
- **修复：模式切换/恢复尺寸后窗口右侧和底部出现大黑边**。从极简模式（316×186）切回完整/紧凑模式、
  或从侧车恢复窗口尺寸时，根 Grid 可能仍停留在最小尺寸（1200×620），窗口加大的区域没有绘制，
  露出黑色背景。在 `SetModeAsync` 末尾于 `DispatcherPriority.Loaded` 再强制 `InvalidateMeasure/Arrange + UpdateLayout`
  一次，确保根 Grid 撑满窗口。
- 测试：全量 **121 passed**（改动集中在 App 层）。
- 文档：`docs/ui.md` §0/§9/§11 同步滚动条与极简模式选择器的描述。

---

## 2026-11-05（三）— 10 套主题（浅色/深色）、滚动条与极简模式跟随主题、通知滚动修正

- **主题扩展为 10 套**：浅色 5 套（默认蓝 / 墨绿 / 暖橙 / 紫罗兰 / 石墨）+ 深色 5 套（深蓝 / 深绿 / 深橙 / 深紫 / 深灰）；
  设置页「外观」按「浅色 / 深色」分组展示。`ThemeManager` 改为 token 字典 + 浅/深基底调色板，
  新增 `IsDark` 标记；`Theme.xaml` 补齐 `InsetBackground/InsetBorder/SubtleButtonBackground/GhostHover/
  ScrollThumb(3 态)/Tooltip(3 项)/PillNeutral(2 项)/Overlay/ChartToggle/DangerBorder/WarningBorder/
  HoverOverlay/Minimal*` 等 token，并把 XAML 里残留的硬编码颜色全部改为 `DynamicResource`。
- **滚动条跟随主题**：滚动条滑块默认/悬停/拖动三态改用 `ScrollThumbBrush/Hover/Pressed`，
  深色主题下自动变深，不再固定浅灰。
- **极简模式跟随主题**：卡片背景、顶部色条、账单名/金额文字、未结账单 chip、挂起/结账按钮、
  模式切换按钮全部改用主题 token；浅色主题是浅色卡片 + 深色文字，深色主题是深色卡片 + 浅色文字。
- **修复：极简通知滚动过头**。根因是通知文本被拉伸到可视区宽度（243px），平移 `-(textWidth-hostWidth)`
  后整块移出可视区。修复：文本 `HorizontalAlignment="Left"` 保持自然宽度，滚动距离按
  `MinimalPrintNoticeViewport.ActualWidth` 计算，滚到最后一个字刚好贴住右边缘即反向。
- **修复：切换主题后右上角模式切换按钮颜色不跟随**（上一轮已修，本轮随 token 化一并覆盖深色主题）。
- 测试：全量 **121 passed**（改动集中在 App 层）。
- 文档：`docs/ui.md` §0/§9/§11、README、`docs/test-plan.md` 同步；新增 `docs/images/ui-dark.png`，
  重生成 `docs/images/ui-settings.png`、`ui-theme.png`。

---

## 2026-11-05（二）— 通知滚动/裁字、收银台拖拽回弹、模式切换配色、浅色侧边栏主题

- **修复：极简通知超长文本不滚动**。改为对 `TranslateTransform.X` 直接 `BeginAnimation`（等布局完成后再量宽度），
  `AutoReverse + RepeatBehavior.Forever` 做**来回反复滚动**（0 → 末尾 → 0，带 SineEase 缓动），短文本不滚动。
- **修复：通知条圆角裁掉最后一个字**。文本容器右侧加 10px 边距，滚动到末尾时末字符完整可见。
- **修复：收银台打印详情拖拽后回弹**。上一版用「拖拽标记」跳过刷新，但刷新路径仍会写列宽；
  现改为**只在显示/隐藏切换时设置列宽**，1 秒自动刷新完全不触碰宽度（历史账单明细同方案），
  拖拽结果通过 `DragDelta` 实时写入 `_detailsWidth`、`DragCompleted` 持久化。
- **修复：切换主题后右上角模式切换按钮颜色不跟随**。`Theme_Click` 里补调 `UpdateModeSwitchButtons()`，
  重新从主题资源解析激活/非激活配色。
- **优化：左侧导航配色**。主题从「深色近黑外壳」改为**浅色渐变侧边栏**（每套主题各自的浅色调），
  导航文字/悬停/选中、工作台标签、数量徽标、底部状态卡、Logo 文字全部改为主题 token（`Theme.xaml`
  新增 `SidebarNavText/Hover/Active`、`SidebarSection`、`SidebarCount(Text)`、`SidebarText`、`SidebarIndicator`），
  5 套主题配色同步调整；`ThemeManager` 增加对应字段与应用逻辑。
- 测试：全量 **121 passed**（改动集中在 App 层）。
- 文档：`docs/ui.md` §0/§9/§11、README 同步；重生成 `docs/images/ui-main.png`、`ui-details.png`、
  `ui-history.png`、`ui-flows.png`、`ui-stats.png`、`ui-settings.png`、`ui-theme.png`（浅色侧边栏）。

---

## 2026-11-05 — 修复拖拽回弹 / 极简窗口拖动，流水分页页码与极简通知强化

- **修复：拖拽打印详情 / 账单明细后窗口回弹**。根因是 1 秒自动刷新调用
  `UpdatePrintDetailsPanel` / `UpdateHistoryDetailsPanel` 时把列宽重置为旧的 `_detailsWidth`，
  拖拽过程中就被弹回。修复：新增 `DragStarted/DragDelta` 处理，拖拽期间不重置列宽，
  并实时把当前宽度写回字段；`DragCompleted` 先清拖拽标记再保存。
- **修复：极简模式顶部无法拖动窗口**。根因是拖拽处理器挂在 header Grid 上，而顶部 3px 色条和
  14px 外边距不在其命中区域，且外层 Grid 无背景不可命中。修复：外层 Grid 设 `Background="Transparent"`
  并挂 `MouseLeftButtonDown`，整个空白区域（含顶部色条、按钮上方）都可拖动；双击账单名改名时阻止冒泡。
- **打印流水分页增强**：新增页码按钮（1、2、3…，超过 7 页用「…」省略）与「跳至 [ ] 页」输入框
  （回车或点「跳转」直达），当前页高亮；搜索回第 1 页、追溯自动跳页逻辑保留。
- **极简模式打印通知强化**：通知条改为琥珀色（#F59E0B）深色文字、加粗，左侧固定喇叭图标，
  高度 34px（窗口临时加高 42px），滚动区域扣除图标宽度；仍为 12 秒后自动收起。
- 测试：全量 **121 passed**（改动集中在 App 层）。
- 文档：`docs/ui.md` §4/§11、README、`docs/test-plan.md` 同步；重生成 `docs/images/ui-flows.png`，
  新增 `docs/images/ui-minimal-notice.png`。

---

## 2026-11-04 — 体验优化：明细拖拽、流水分页、统计重排、主题切换、极简打印通知

- **历史结账账单明细宽度可拖拽**：新增 `HistoryDetailsSplitter`，明细列改为像素宽度
  （默认 460，范围 320–900），拖拽结束写入 `Settings.historyDetailsWidth`；切换账单/刷新/重启保持。
- **打印流水分页**：`ApplyFlowFilter` 改为「过滤 → 分页 → 绑定」，底部工具栏显示「第 x / y 页」，
  每页 20/50/100 条（默认 50），上一页/下一页边界禁用；搜索回到第 1 页，追溯自动跳到目标页。
- **统计页重排**：Grid 行顺序改为 标题 → KPI → 每日明细/维度统计（`*` 自适应）→ 统计图（最下方固定高度）。
- **主题切换**：新增 `Themes/ThemeManager.cs`（5 套主题：默认蓝/墨绿/暖橙/紫罗兰/石墨），
  设置页新增「外观」卡片；主题 token 在 `Theme.xaml` 中由 `StaticResource` 改为 `DynamicResource`，
  运行时替换 Brush 资源即可全局刷新；选择写入 `Settings.uiTheme`，启动时应用。
- **极简模式打印通知**：`ReloadAsync` 按 `CreatedAtUtc` 检测新任务，极简模式下在窗口下方显示
  滚动 marquee「文档名 → 目标打印机 · 金额 · 状态」，窗口临时加高 34px，12 秒后自动收起并恢复高度。
- 测试：全量 **121 passed**（改动集中在 App 层）。
- 文档：`docs/ui.md` §3/§4/§5/§9/§11、README、`docs/test-plan.md` 同步；新增 `docs/images/ui-theme.png`，
  重生成 `docs/images/ui-stats.png`、`docs/images/ui-flows.png`。

---

## 2026-11-04 — 修复 `Start-PrintPalTestEnv.ps1` 在管道宿主中“卡住”

- 现象：在 AI 代理/CI 等 stdout 是管道的宿主里运行一键启动脚本，启动 FakePrinter 后脚本不再返回
  （宿主一直等不到 EOF），App 也没被拉起。
- 根因：脚本用 `& powershell.exe -File ...` 调仓库脚本，而 `Start-FakePrinters.ps1` / `Start-PrintPalApp.ps1`
  会 `Start-Process` 出长期运行的 FakePrinter/App；这些孙进程继承了调用方的 stdout 管道句柄，
  管道宿主读不到 EOF 就一直等待。`Start-Process -Wait` 在 Windows PowerShell 5.1 下还会连子进程树一起等。
- 修复：改为 `Start-Process`（不重定向输出，走 ShellExecute）启动子脚本，子进程不再继承管道；
  启动结果改用 `/health` 轮询确认；App 启动后额外等待进程与窗口出现，避免汇总误报 `NOT RUNNING`。
- 验证：FakePrinter 停止后运行脚本 → 3 台打印机恢复、脚本正常返回；App 停止后运行脚本 →
  App 重新启动（窗口 `PrintPal`）、汇总正确；两种路径均无卡住。
- 文档：`printpal-dev-env` skill 补充修复说明。

---

## 2026-11-03 — 统计页布局与交互打磨：图表切换、悬停取值、日期选择器样式、加载过渡

- **单页布局**：统计页改为 Grid 行布局（标题 / KPI / 统计图 / 明细），去掉外层滚动条；
  每日明细与维度统计卡片自适应剩余高度，内部滚动。
- **统计图合并 + 切换**：趋势折线与时段分布合并到同一张卡片，用「趋势 / 时段分布」分段按钮切换，
  不再同时占两块位置；趋势模式下才显示指标下拉。
- **趋势悬停取值**：鼠标在图上移动时显示竖向参考线与「日期 · 数值」气泡（跟随最近数据点）。
- **自定义日期选择器样式**：新增 DatePicker / Calendar / CalendarDayButton / CalendarButton 主题样式
  （圆角、主题色选中、日历图标），与 App 整体风格一致。
- **自定义区间刷新时机**：只改开始日期时不刷新（提示「请选择结束日期后更新统计」），
  选完结束日期才更新；直接改结束日期立即更新。
- **列对齐**：每日明细的账单/任务/页数/张数、维度统计的任务/张数改为居中（日期/维度/取值保持左对齐）。
- **加载过渡**：统计耗时超过 200ms 时显示「正在统计…」遮罩（本地 SQLite 通常很快，避免闪烁）。
- 测试：全量 **121 passed**（改动集中在 App 层）。
- 文档：`docs/ui.md` 第 5 节、README、`docs/test-plan.md` 同步；重生成 `docs/images/ui-stats.png`。

---

## 2026-11-03 — 流水/统计体验完善：只展示文件流水、追溯定位任务、流水号复制、自定义区间与趋势图

- **打印流水**：
  - 列表只展示文件（打印任务）流水，账单流水仍保留在库中用于追溯与统计；移除类型筛选；
  - 追溯已结账账单时，跳到历史结账后**自动打开账单明细并选中对应打印任务**（高亮 + 滚动到可见）；
  - 列「账单」更名「所属账单」。
- **收银台**：
  - 打印详情顶部新增**文件流水号**（等宽字体 + 复制按钮）；
  - 收银条「当前账单」小字右侧新增**复制账单流水号**小图标（不占额外空间）。
- **统计**：
  - 新增**自定义时间段**：开始/结束日期选择器（日历可切换月/年），选完开始日期自动弹出结束日期选择；
    区间最长一年（366 天），超出自动截断并提示；
  - 新增**趋势折线图**：按天展示营业额/账单数/任务数/页数/张数（可切换），中间无数据的天按 0 补齐，
    数据点悬停显示日期与数值；
  - 空数据时统计卡片保持原有高度并显示「当前范围内没有数据」，不再塌缩。
- 存储：新增 `GetJobFlowNumbersAsync`（任务→流水号映射）与 `GetBillFlowNoAsync`（账单流水号）。
- 测试：全量 **121 passed**（本次改动集中在 App 层与两个只读查询，Storage 测试不变）。
- 文档：`docs/ui.md` 第 2/4/5 节、README、`docs/test-plan.md` 手工用例同步；重生成 `ui-flows.png`、`ui-stats.png`。

---

## 2026-11-03 — 新增「打印流水」与「统计」页

- 数据模型：新增 `PrintFlows` 表与 `PrintFlow` / 统计 DTO（`DailyPrintStat`、`FlowDimensionStat`、`HourPrintStat`）。
  - 编号规则：账单 = 本地 `yyyyMMddHHmmss` + 3 位毫秒（17 位，如 `20261102223812001`）；
    文件 = 账单号 + 3 位序号（20 位，如 `20261102223812001001`），同一账单内递增；
  - 建账单 / 新任务入库时自动写流水；任务状态/金额/目标变化刷新快照；任务换账单时按新账单重新编号；
  - `EnsureFlowsAsync` 在启动与打开流水页时补齐缺失流水（覆盖旧版服务/安装包写入的数据），可重复执行；
  - 手动删除流水为软删除（编号不复用），不影响账单与任务本身。
- 新页面「打印流水」：类型筛选、关键字搜索、复制流水号、右键手动删除；
  双击/右键追溯——未结账单跳收银台、已结账跳历史结账、账单已删除弹窗显示快照。
- 新页面「统计」：范围（今天/近 7 天/近 30 天/全部）、KPI（营业额/账单数/任务/页数/张数/客单价）、
  每日明细、维度统计（纸张/颜色/单双面/目标打印机）、0–23 点时段分布、CSV 导出（UTF-8 BOM）；
  待结账单金额单独提示、不计入营业额。
- 导航：新增「打印流水」「统计」两项（收银台 / 历史结账 / 打印流水 / 统计 / 打印机 / 路由规则 / 价格表 / 设置）。
- 测试：新增 `FlowTests` 7 个（编号规则、快照刷新、账单删除后追溯、手动删除、换账单重新编号、
  每日/维度统计、缺失流水补齐）；全量 **121 passed**（Storage 28）。
- 文档：README、`docs/ui.md`（新增第 4/5 节并重排后续章节）、`docs/architecture.md`（数据模型与统计查询）、
  `docs/runbook.md`（基线 121）；新增截图 `docs/images/ui-flows.png`、`ui-stats.png`。
- 说明：App 与服务共享同一数据库；已安装的旧版 `PrintPal.Service`（1.0.1）不会主动写流水，
  它捕获的任务会在 App 启动/打开流水页时自动补编号；升级服务后由服务端直接写入。

---

## 2026-11-02 — 打印详情：可调宽度、悬停完整值、目录复制；清理启动缩窗调试代码

- 打印详情支持**拖拽调宽**：列表与详情之间新增 `GridSplitter`（光标 SizeWE），宽度存
  `Settings.detailsWidth`（默认 360，范围 260–760）；切换任务 / 自动刷新不重置，重启后恢复。
- **超长省略项悬停显示完整内容**：文件名、目录、SHA-256 均带 ToolTip（完整值）。
- `目录` 改为**换行完整显示**（不再省略），右侧新增「复制完整内容」小图标，一键复制完整文件路径；
  同时把 `文件名`/`SHA-256` 的工具提示改为对应完整值（之前目录行误显示完整路径）。
- 去掉详情里的**计价公式长文本块**，只保留单面价 / 双面价与本任务金额；金额核对仍可用
  「复制详情」得到完整文本。
- `转发时间` 补全年份（`yyyy-MM-dd HH:mm:ss`）。
- **修复误操作根源**：`InitializeAsync` 里残留的 TEMP 缩窗诊断（每次启动把窗口按 20px 递减到 720 并写
  `%TEMP%\pp-verify\shrink.log`）已删除 —— 它不是任何文档/约定的要求，是 D6 滚动条排查遗留，
  之前"每次迭代窗口变小"就是它造成的。
- `artifacts/tools/uia-shot.ps1`（UI 取证脚本）改为默认**不改变窗口大小**，需要固定尺寸时显式传 `-Resize`。
- 验证：拖拽后 `Settings.detailsWidth = 440`，切换任务宽度不变；目录复制按钮经 UI 自动化实测复制出完整路径；
  目录 / SHA / 文件名悬停均弹出完整值；114 个测试仍全绿。

---

## 2026-11-02 — 打印详情面板重构（分组布局 + 突出金额 + 复制详情）

- 右侧「打印详情」原先是整块等宽纯文本（`TextBox` 文本转储），信息平铺、长路径/SHA 折行难读；改为结构化视图：
  - 顶部：文档名 + `用户 · 时间`；状态胶囊与「本任务金额」并排突出显示；
  - `状态说明`：仅在失败/未知等有原因时出现，按状态着色（失败红、未知橙）；
  - `计价`：单面价 / 双面价 + 计价公式；`规格`：纸张、颜色、单双面、页数（页/份 = 面）、张数；
    `转发`：目标打印机、转发时间；`文档`：格式、大小、文件名、目录、SHA-256（长文本省略 + 悬停完整值）；
  - 标题栏新增「复制详情」按钮，复制整段纯文本（保留原 `DetailsText` 作为排障输出）；图标按钮补
    `AutomationProperties.Name`（可访问性与 UI 自动化）。
- 实现：详情面板改为绑定 `JobRow` 的结构化视图模型（`Rows.cs` 新增 `DetailLine` 与派生字段），
  新增 `DetailLineTemplate` 与分组标题样式；移除不再使用的 `DetailsBox` 样式。
- 文档与图片：新增 [docs/images/ui-details.png](images/ui-details.png)，更新 `docs/ui.md` 第 2 节与 README P3 段。
- 验证：114 个测试仍全绿；UI 多次截图核对（已转发 / 失败两种状态、受损原因框、复制按钮经 UI 自动化实测）。
  构建 XAML 期间 App 曾短暂停止（AGENTS.md 例外 2），每次构建后都立即用测试数据重启并保持运行。
- 沉淀：`artifacts/tools/uia-shot.ps1`（UIAutomation 选中行 + 窗口置前/缩放 + 截图）用于后续 UI 回归取证。

---

## 2026-11-02 — 新增 4 个仓库技能（`.agents/skills`）

- 新增 `.agents/skills/`（DSH 与兼容 `SKILL.md` 的代理自动发现，随仓库提交）：
  - `printpal-dev-env`：一键启动/恢复本地测试环境（FakePrinter + App + 队列）、服务模式 vs 独立模式判定、
    造真实测试任务、收尾保留现场；脚本 `Start-PrintPalTestEnv.ps1`（只启动、不停止任何进程）；
  - `printpal-build-test`：构建 + 测试 + 基线核对；脚本 `Invoke-PrintPalBuildTest.ps1`
    （检测运行中的 App/FakePrinter 锁 `bin\` 时自动降级为只构建/测试测试项目，退出码 0/1/2）；
  - `printpal-msi`：发布/MSI 安装升级修复卸载、队列重建；脚本 `Get-PrintPalDeploymentState.ps1`（只读）；
  - `printpal-troubleshoot`：按打印链路分段排障；脚本 `Collect-PrintPalDiagnostics.ps1`（只读，
    证据包写入 `artifacts\diagnostics\<时间戳>\summary.md`）。
- `AGENTS.md` 新增「5. 仓库技能」索引；`README.md` 参考区加技能与代理约定链接。
- 技能脚本统一：ASCII 输出、PS 5.1 兼容、只读或"只启动不停止"，与「保留现场」约定一致。

## 2026-11-02 — 修复测试基线 + 脚本健壮性 + 「持续完善」约定

- **修复 5 个过期测试**：`PrintPal.Storage.Tests.BillingTests` 仍断言旧账单名「客户N」，
  而 `bf0bd38` 起新账单默认名是「散客」。断言改为「散客 + 序号」
  （`Ensure_active_creates_bill_with_default_name` 等 5 处），legacy 迁移测试的
  「标签N → 客户N」映射保持不变。实测 **114 passed / 0 failed**。
- **校正基线文档**：`docs/runbook.md` 第 2 节 103 → 114（Storage 10 → 21）；
  `printpal-build-test` skill 基线同步为 114，并记录本次修复原因。
- **修仓库脚本的 `$PSScriptRoot` 缺陷**：`[CmdletBinding()]` + `powershell.exe -File` 下参数默认值
  求值时 `$PSScriptRoot` 为空，`Diagnose/Start-App/Start-Agent/Start-FakePrinters/Stop-*` 共 7 个脚本
  会直接报 `Split-Path : Cannot bind argument ... empty string`。改为在脚本体内解析仓库根
  （`$PSScriptRoot` → `$PSCommandPath` → 当前目录，`-Root` 仍可覆盖）。实测
  `Diagnose-PrintPal.ps1`、`Start-PrintPalApp.ps1` 不再需要 `-Root`。
- **新增 AGENTS.md「0. 开发哲学：逐步完善」**：发现过期/可改进的 skill、脚本、文档、测试就在同一次
  任务里改掉并验证，改不动要说明原因；修复问题时同步校正相关基线数字，避免留下新的不一致。
- 本次验证仍遵守「保留现场」：App / Service / FakePrinter 全程未关闭，`/health` = ok。

---

## 2026-11-02 — 开发约定：测试后保留 App 运行；本轮测试数据就绪

- 新增仓库根目录 [AGENTS.md](../AGENTS.md)：代理级协作约定（首选命令、端口/数据目录、
  构建与测试、证据目录、代码风格），并明确 **测试完成后不关闭 App**；
  例外只有三种：用户明确要求、构建/安装升级必须重启、单实例或端口冲突——例外情形先说明原因，
  完成后**立即带测试数据重新启动并保持运行**；
- `docs/runbook.md` 新增「0. 开发约定：测试后保留现场」，并在 3.2 补充"已安装服务时 App 走服务模式、
  `-SeedDemo` 不生效"的说明；`README.md`「构建与测试」下补充同一条约定；
- `scripts/Stop-PrintPalApp.ps1` 帮助文本加提示：测试/验证结束后不要运行本脚本；
- 本次环境与数据：`PrintPal.Service` 在跑（8630）；启动 3 台 FakePrinter（8631/8632/8633）后，
  经已安装队列打印 3 个受控任务——A4 黑白单面 1 页 ¥0.20、A4 彩色双面 5 页×2 份 ¥6.40、
  A3 彩色双面 3 页 ¥4.00，全部 `Succeeded`，FakePrinter 均收到 PDF；
  App 以服务模式读取 `C:\ProgramData\PrintPal` 并保持运行，供人工继续验证。

---

## 2026-11-01 — 「打印记录」页更名「收银台」，任务详情更名「打印详情」

- 页面与导航更名：侧栏项、页面标题/副标题（`账单收银与打印转发`）同步；
  与「历史结账」形成「进行中 / 已归档」的成对命名。
- 代码全量更名：`JobsGrid` → `CashierGrid`、`CompactJobsGrid` → `CompactCashierGrid`、
  `JobDetailsCard/Box/Column` → `PrintDetails*`、`UpdateJobDetailsPanel`、`CashierNavCountText`（角标）等；
  任务数据模型（`JobRow`/`PrintJob`）与操作方法名保持不变（语义仍为打印任务）。
- 数据库无页面相关键，无需迁移。

---

## 2026-11-01 — 任务表列宽与滚动条（未完项）

- 任务表各列 MinWidth 整体收窄（合计 552 → 444），常规窗口宽度下横向滚动条不再触发。
- 缩小窗口时横向滚动条仍会闪现（最终宽度足够时也闪）——**未修复**，
  排查过程与后续方向见 [troubleshooting.md D6](troubleshooting.md#d6-窗口缩放时任务表横向滚动条闪现未修复已知问题)。

## 2026-11-01 — 交互细节优化（刷新/设置/双击编辑/输入框）

- 数据刷新定时器 2s → 1s。
- 设置页「检查队列」点击先显示「检查中 ...」过渡状态，队列查询移到后台线程，
  检查期间按钮禁用；启动/定时路径的即时查询抽为 `FindQueueStatus()` 复用。
- 全局输入框文字距左收紧（Padding 10→6）。定位并记录：自定义 TextBox 模板会把
  Padding 双倍应用（ScrollViewer Margin + 原生缩进），实际渲染 = 2×Padding+边框，
  搜索框按折算值设置。
- 搜索框（收银台/历史结账）：图标与占位文字间距收紧；占位文字改为「空且未聚焦」时显示，
  点列表空白处让搜索框失焦、占位重现，避免占位文字与输入光标重叠。
- 打印机/路由规则列表双击行进编辑页；价格表新增 `PriceEditWindow` 价格编辑窗
  （双击非价格列打开，纸张/颜色只读展示，价格列保留行内编辑），「启用」表头居中对齐复选框。

---

## 2026-11-01 — 各模式窗口位置/大小记忆（侧车文件）

- **每个模式独立记忆位置与大小**：切换与重启（含强杀进程）都恢复到该模式上次的位置。
- 位置/大小改存侧车文件 `%LOCALAPPDATA%\PrintPal\window-bounds.json`（同步写盘，
  避开数据库 WAL/锁时序）；数据库 `*Left/*Top` 键废弃。
- 保存时机：模式切换、关窗/退出、窗口移动/缩放后 500ms 防抖。
- **修复**：`SetModeAsync` 中 `SaveModeBounds` 被 `_applyingMode` 标志短路导致
  模式切换从不保存位置的问题（保存挪到标志置位之前）。
- 恢复后钳制在虚拟屏幕内（显示器变更时防止整窗消失）。

## 2026-11-01 — 收银台视觉微调

- 收银台导航图标更换为钱币字形（原诊断字形）。
- 挂起 / 结账按钮文字放大加粗（16px Bold）并增加字间距，去掉短暂的图标方案。
- 未结账单左右分割线上下各缩短 2.5px（共 5px），随内容垂直居中（完整/紧凑模式）。
- 右键菜单「删除记录」文字改为危险红色（`DangerBrush`）。

---

## 2026-11-01 — 任务操作改右键菜单，详情面板只读化

- 完整/紧凑模式任务列表新增右键菜单：`转发 / 重试`、`选择目标`、`打开文档`、`打开所在目录`、
  `移到其他账单`、`删除记录`；右键任务行先选中该行；2 秒自动刷新不再打断菜单操作
  （刷新后两个列表的选中状态都会恢复）。
- `打开文档` 改为系统默认程序直接打开；新增 `打开所在目录`（资源管理器定位该文件，
  文件已删则打开目录；目录也失效则提示）。
- 任务详情面板只保留信息展示，操作按钮全部移除（入口即右键菜单）；
  点列表空白处自动关闭详情面板，点表头排序不影响面板状态。
- 按钮聚焦描边移除：修复置顶按钮点击后蓝色边框常驻、直到点击其他按钮才消失的问题。

---

## 2026-11-01 — 「简洁模式」更名为「紧凑模式」

- 代码与界面全量更名：`UiMode.Simple` → `Compact`、`SimpleLayout` → `CompactLayout`、
  `SimpleJobsGrid` → `CompactJobsGrid` 等标识符与按钮提示 / 自动化名称 / 托盘菜单文案同步。
- 持久化键更名：`simpleWidth/Height` → `compactWidth/Height`、`topmost.simple` → `topmost.compact`；
  旧设置值 `uiMode="Simple"` 启动时自动映射为 `Compact`，历史键自然失效（走新默认值）。
- 模式语义不变：紧凑模式 = 贴边紧凑布局的精简工作台。

---

## 2026-11-01 — 简洁模式紧凑化

- 去卡片化贴边布局：账单条与任务表直接贴边（移除四周留白、圆角卡片与阴影），
  用 1px 发丝线分隔；任务列表起始高度提前约 40px，左右各多出 16px 宽度。
- 文档列不再显示主机名（悬停仍可看），行高 46→30；账单卡去掉「当前账单 / 未结账单 /
  本单合计」小字，改单行布局。
- 金额 30→22px、挂起/结账按钮 42→34px、标题栏 58→44px、表头 40→34px。
- 默认窗口 940×640 → 776×436（最小 760×500 → 700×420）；
  账单名称列宽 100→80，名称与账单列表的左右间距对齐（12px）。

---

## 2026-10-31 — 未结账单切换器（三模式统一）

- **交互重构**：完整/简洁/极简三模式的挂起区统一改为「未结账单切换器」——
  列表 = 当前账单 + 全部挂起账单，按建单顺序稳定编号（1、2、3…）；当前账单蓝色高亮，
  点击其他账单即切换（旧账单原地挂起，列表顺序不变），点当前账单无动作。
- **溢出折叠 +N**：chip 一行放满后其余折叠进「+N」，点击弹出全部账单浮层
  （序号 / 名称 / 挂起时间 / 金额右对齐，整行点击切换，当前行高亮）；三模式均不再用横向滚动条。
- **简洁模式**：未结账单与「当前账单 / 本单合计」平行展示（同完整模式三列），
  金额上方补「本单合计」标签。
- **极简模式**：换小号深色「+N」；列表右侧分割线移除（右侧无内容）；当前账单 chip 高亮。
- **新账单默认名「散客」**（原 `客户N`），列表靠序号区分，双击仍可改名。
- **防闪烁**：切换/金额变动原地更新 chip 属性（不重建容器），有增删时新容器默认隐藏；
  溢出先按离屏原型估宽、再按真实布局 ±1 收敛校正——不再出现整批闪现或「有空位却少显示」。
- 溢出逻辑泛化为 `ChipOverflowStrip`，三种模式共用一套实现。

---

## 2026-10-30 — 账单 UI 细节修复（二）

- 恢复完整模式收银条的「当前账单 + 名称 + 金额·单数·张数」：名称是双击改名的唯一入口，不能删；
  只去掉标题栏重复的刷新按钮。
- 简洁模式改为：标题栏只留品牌与窗口按钮（空白处可拖动窗口），挂起账单 chip 移到下方账单卡，
  彻底避开「标题栏 Caption 吞点击」的问题——chip 现在在普通客户区，点击可正常取回账单。
- 简洁模式去掉「本次 N 单 · M 张」小字（与金额重复）。
- 「金额」列表头右对齐，与金额数值对齐（完整/简洁/历史列表/历史明细）。
- 列宽可拖动调整：所有固定宽度列的最小宽度从「等于宽度」下调到约 70%，之前只能拖宽、拖窄无效。

---
## 2026-10-30 — 账单 UI 细节修复

- 完整模式：去掉标题栏重复的刷新按钮与收银条左侧的「当前账单 + 名称 + 金额·单数·张数」，
  收银条只保留挂起区、本单合计与「挂起 / 结账」。
- 修复简洁模式无法切换账单：标题栏内的挂起 chip 区域补 `WindowChrome.IsHitTestVisibleInChrome`，
  不再被窗口拖动区吞掉点击（已用真实鼠标点击验证切换生效）。
- 修复「编辑账单」弹框文字发虚：改为固定尺寸（去掉 `SizeToContent`）、加 `UseLayoutRounding` /
  `SnapsToDevicePixels` / `TextOptions.TextFormattingMode=Display`，卡片改用无阴影的 `FlatCardBorder`
  （避免 Effect 位图化），并与 `移到其他账单` 弹框保持一致。
- 修复金额列右对齐但表头居中：抽出 `GridColumnHeaderStyle` / `AmountColumnHeaderStyle`，
  完整 / 简洁 / 历史列表与历史明细的「金额」表头改为右对齐，与金额数值对齐。

---

## 2026-10-30 — 标签改为「账单 + 挂起」

- **领域/存储破坏性重构**（ADR-009）：`BillingTag + BillingSession` → `Bill`（`Active / Parked / Closed`）；
  新表 `Bills`，`PrintJobs.BillingTagId/BillingSessionId/TagNameSnapshot` → `BillId/BillNameSnapshot`；
  `UX_Bills_Active` 部分唯一索引保证全局恰好一张当前账单；自动名 `标签N` → `客户N`。
- **一次性迁移**：旧库启动时自动迁到 `Bills`（已结账快照、活动账单、任务归属），归档空标签丢弃；
  迁移失败直接报错，不半迁移运行。
- **挂起 / 取回 / 结账**：挂起当前账单并自动开新账单；点挂起区 chip 取回；结账后自动取回最近挂起的账单；
  空账单自动清理，不再堆积。
- **任务改挂账单**：任务详情新增 `移到其他账单`（可选已有账单或新建并切换），用于插队场景的错单纠正。
- **计费条只在「打印记录」页**：历史/打印机/路由/价格/设置页不再显示当前账单与结账。
- **历史结账**：列表默认占满整行、默认不显示明细；新增 `全部时间 / 今天 / 近 7 天 / 近 30 天` 与
  账单名/备注关键字筛选；点行开明细、可关闭；`标签` 列改名 `账单`。
- **三种模式**：简洁模式标题栏放挂起 chip 条；极简模式去掉 PrintPal 品牌，改为当前账单名 + 挂起 chip 条 +
  等宽「挂起 / 结账」；改名统一改为双击账单名，去掉改名/删除按钮。
- 测试：`PrintPal.Storage.Tests` 计费相关重写为账单语义（13 个用例，含旧库迁移）；
  `dotnet test` 全绿。

---

## 2026-10-30 — 任务详情面板可关闭

- 打印记录右侧「任务详情」卡片标题栏右上角新增关闭按钮（`关闭详情面板`）：
  关闭后详情整块隐藏，列表占满整行，不保留窄条。
- 关闭后单击任意任务行重新打开详情，并同步该任务内容；关闭状态只在当前会话内有效，
  不受 2 秒自动刷新影响，重启后恢复为单击任务即显示。
- 无自动化测试变更（纯 UI 行为）；`PrintPal.App` 构建通过，界面实测截图见
  `artifacts/ui-details-open.png` / `artifacts/ui-details-closed.png`。

---

## 2026-10-30 — WPS 打印裁切修复

- 修复 IPP 能力 `media-*-margin-supported`：`4233`（42.33 mm）→ `423`（4.23 mm）。
  该值过大会让 Microsoft IPP Class Driver 把 WPS 页边距（31.75 mm）内的文字裁掉，
  捕获的 PDF 看起来空白或只剩一小截。
- 新增回归测试 `Printer_attributes_advertise_margins_that_do_not_clip_wps_text`；IPP 测试 12 个通过。
- 验证：WPS 2 页文档 `123123` / `22312312` 打印后 PDF 内容完整；页数、金额、路由不变。
- 文档：`docs/troubleshooting.md` 新增 B7 与病例 10.2。

---

## 2026-10-28 — P0/P1/P2

### P0：环境与 IPP 捕获可行性

- 主机：Windows 11 26300；Windows 10 22H2 19045 VM。
- 建立 `tools/PrintPal.IppProbe`（IPP 探针）与 `tools/PrintPal.PrintProbe`（可控打印）。
- Win11/Win10 均验证：
  - 本机 IPP 队列；
  - `Create-Job` + `Send-Document`（gzip/未压缩 PDF）；
  - PDF 页数、copies、sides、color、media-col；
  - WPS copies 正确（未摊平）；
  - A3 关键字 `iso_a3_297x420mm` 修复。
- Win10 需显式 `Add-PrinterDriver -Name 'Microsoft IPP Class Driver'`。
- 文档：`docs/spikes/environment.md`、`docs/spikes/ipp-capture.md`。
- Win10 自包含验证包：`dist/p0-win10/`（result.txt/result.json/errors.txt）。

### P1：领域、IPP、捕获、存储、转发、FakePrinter

- 解决方案与项目结构：
  `Core` / `Ipp` / `Capture` / `Storage` / `Forwarding` / `FakePrinter` + 测试。
- 计价：按“张”，双面按“每份”处理奇数页（ADR-002）。
- IPP：编解码、集合、gzip、客户端、Kestrel 服务端、Windows 兼容能力集。
- 捕获：PDF（PdfPig）/PS/OXPS 页数；PWG 明确未实现。
- 存储：SQLite 任务/打印机/价格/路由/设置。
- 转发：IPP `Print-Job` + Raw 9100。
- FakePrinter：IPP + Raw、多实例、`--once/--json/--fail-rate`。
- 测试：87 个通过（Core 57 / Ipp 10 / Capture 10 / Storage 6 / Integration 4）。
- 文档：根 `README.md`。

### P2：Agent 流水线 + 队列安装脚本

- 新增 `PrintPal.Agent`：
  - `PrintJobPipeline`：文档保存 → 属性归一化 → 计价 → 路由 → SQLite → 可选转发；
  - `PrintPalAgent`：DB/文档/单价/路由 seed、IppServer 生命周期；
  - `PrintPalIppJobHandler`。
- 新增 `PrintPal.AgentHost` 控制台宿主：
  - `--port/--data/--bind/--host/--seed-demo/--no-auto-forward`。
- 脚本：
  - `Start/Stop-FakePrinters.ps1`（3 台模拟打印机）；
  - `Start/Stop-PrintPalAgent.ps1`；
  - `Install-PrintPalQueue.ps1`（Win10 先 Add-PrinterDriver；轮询/Rename/验证）；
  - `Uninstall-PrintPalQueue.ps1`；
  - `Diagnose-PrintPal.ps1`。
- 集成测试新增 4 个 Agent 流水线测试，总数 **91**。
- 端到端实测：
  - 5 页 × 2 份，A4 彩色双面 → **¥6.40**；
  - 路由到 Fake-A4-Color，收到 PDF（5 页，534,354 B，SHA-256 `89542a…94cf`）；
  - 队列 `Normal`，JobCount 0。
- 文档：
  - `docs/architecture.md`
  - `docs/runbook.md`
  - `docs/test-plan.md`
  - `docs/decisions/ADR-001..004`
  - 本文件。

### P3：WPF UI（2026-10-28）

- 新增 `PrintPal.App`（WPF，net10.0-windows）：
  - 进程内托管 `PrintPalAgent`；启动即监听 8630；
  - 打印记录：今日/本周/本月汇总、日期/状态/搜索筛选、全部要求列（纸张/颜色/单双面/
    页数/份数/单价/金额）、首行自动选中、详情（张数/计价明细/SHA/路径）、打开文档、
    软删除、CSV 导出；
  - 打印机：增删改、测试连接（IPP 能力 / Raw TCP）、设为默认、启用；
  - 路由规则：增删改、纸张/颜色/单双面/页数/正则/目标/优先级；
  - 价格表：8 条默认规则可编辑，单面/双面价（元→分），保存；
  - 设置：自动转发、开机自启（HKCU Run）、数据/日志目录、队列检查、复制安装命令；
  - 托盘：关闭窗口隐藏、双击恢复、托盘退出；单实例互斥体；`--background` 后台启动；
  - 日志重定向到 `<data>\logs\app.log`。
- 脚本：`Start-PrintPalApp.ps1`、`Stop-PrintPalApp.ps1`；`Diagnose` 增加 WPF 进程。
- UI 冒烟：
  - 真实队列 + 5 页 × 2 份 A4 彩色双面 → **¥6.40**；
  - Fake-A4-Color 收到 PDF（5 页，535,559 B）；
  - 列表/汇总/详情/打印机/路由/价格/设置页均验证；
  - 截图见 `docs/images/` 与 [ui.md](ui.md)。
- P3 时为 91 个自动化测试（UI 用手工 + UI Automation 冒烟）。
- 已知：UI 代码后置、无 UI 自动化回归、队列状态需手动检查、P5 再做安装器/自启安装目录。

### P4：异步转发/重试/人工确认（2026-10-28）

- `ForwardingWorker`：捕获与转发解耦，后台队列串行执行，IPP 请求立即返回；
- 有界重试：默认 4 次尝试、2s/10s/30s 退避、30s 单次超时（`AgentOptions` 可配）；
- 明确失败自动重试；达到上限 `Failed`；超时 `Unknown`，不自动重试；
- IPP job-name 追加 `[PrintPal:<JobId>]`；重试沿用同一 JobId；
- 人工确认：`AutoForward=false` → `Held`；UI“转发/重试”按当前目标入队；
- 目标覆盖：UI“选择目标转发”可为 Unrouted/Failed/Unknown 等任务选目标后转发；
- 启动时恢复 `Priced` 且有目标的任务；Held/Failed/Unknown 需人工操作；
- UI：详情面板新增“转发/重试”“选择目标转发”；状态过滤新增“未知”；
- 测试：Integration 14（新增 6 个 P4 测试），总计 **97** 全绿；
- P4 E2E：`--no-auto-forward` 捕获 5 页×2 份彩色双面为 Held；UI 点击“转发/重试”后
  第 1 次成功；FakePrinter 收到 PDF（534,128 B），job-name 带 JobId；
- 文档：`ADR-005`、架构/测试计划/UI/运行手册/README 更新；
- 已知：worker 串行、重试参数未暴露 UI、超时 Unknown 需人工判断、Raw 无幂等元数据。

### P5：自包含发布/安装/卸载/修复/诊断（2026-10-28）

- 版本 `0.5.0`，`win-x64` 自包含目录发布（目标机无需 .NET）；
- 包：984 个文件，316.2 MB；zip 139.3 MB；App exe SHA-256 `E8E3673E…CA60`；
- 新增 `scripts/publish/Publish-PrintPal.ps1`：发布 App + FakePrinter，复制 installer/docs，
  生成 `publish-info.json`/`VERSION.txt`/`Install-PrintPal.cmd`，可 `-Zip`；
- 新增 `scripts/install`：
  - `Install-PrintPal.ps1`：复制到 Program Files、启动 App、装驱动/队列、快捷方式、
    HKCU 自启、卸载注册表、install.json；
  - `Uninstall-PrintPal.ps1`：停进程、删队列/端口/自启/快捷方式/卸载项/程序目录；
    默认保留 `%LOCALAPPDATA%\PrintPal`，`-RemoveData` 才删除；
  - `Repair-PrintPal.ps1`：复用 Install 的 `-SkipFileCopy` 修复队列/自启/快捷方式；
  - `Diagnose-PrintPal.ps1`：安装/队列/健康/数据/日志报告，`-Zip` 打包；
  - `PrintPal.Common.psm1`：共享函数；
- 安装 E2E（测试安装目录）：
  - 自包含 App 启动 + `/health`；
  - 安装队列 Normal；
  - 安装版打印 5 页×2 份彩色双面 → **¥6.40** → FakePrinter 534,064 B；
  - 修复重建队列；自启/快捷方式验证；诊断 txt+zip；卸载清理、数据保留；
- App 日志改为 `FileShare.ReadWrite`，诊断可直接读取/快照运行中的日志；
- 测试仍为 **97** 个自动化测试；P5 以手工安装 E2E 为主；
- 已知：PowerShell 安装器不是 MSI/签名；自启按用户 HKCU；数据按用户；包体积大；
  Win10 干净环境验收在 P6。

### P6：验收（2026-10-28）

- 自动化测试 **98** 个全绿；新增超大文档 413 安全测试；
- Windows 11 正式 Program Files 安装/队列/卸载：
  - PrintProbe 5 页×2 份彩色双面 → **¥6.40**，PDF 535,115 B；
  - WPS 3 页×2 份黑白单面 → **¥1.20**，PDF 1,652 B；
  - 启动到 `/health` 768 ms；启动 Working Set 121 MB；捕获→转发约 330 ms；
  - 仅监听 `127.0.0.1:8630`；队列不共享；超大请求 413；
- `Run-P6-Win10.ps1` 在 Win11 主机预演 PASS（安装→打印→校验→卸载）；
- Windows 10 P0 已通过；P6 在 Windows 10 Education 19045 运行 **PASS**：
  - Program Files 安装 + 队列 Normal；
  - PrintProbe 5 页×2 份彩色双面 **¥6.40**；
  - FakePrinter PDF **566,399 B**，5 页，copies=2，TwoSidedLongEdge，Color，A4；
  - 7 项检查全 True；卸载后队列/程序/进程清理；
  - 结果：`artifacts/p6/win10-result/result.txt`；
- 新增 [P6 验收报告](acceptance.md)；
- **MVP 0.5.0 验收通过**（Win11 + Win10）；
- 未覆盖：Office 未安装、Edge 打印未自动化。

### P7：Windows 服务 + MSI（2026-10-28）

- 新增 `PrintPal.Service`（Windows Worker Service，`PrintPal.Service`）：
  - LocalSystem、自动启动、监听 127.0.0.1:8630；
  - 数据 `C:\ProgramData\PrintPal`；服务启动时 `icacls` 给 Users Modify；
  - 关闭 UI/未登录仍收任务；
- Agent：
  - `IPrintPalBackend`、`ServiceAgentProxy`、`PrintJobActions`；
  - pending-job 扫描器每 2s 入队 Priced 任务；
  - `ForwardingWorker` 入队去重；pipeline 每单读取 `autoForward`；
- UI：
  - 启动探测 `/health`，服务在跑则 service mode；否则便携模式；
  - 共享 ProgramData SQLite；
- 新增 `packaging/PrintPal.Msi`（WiX 5.0.2）：
  - per-machine MSI，程序/服务/队列/公共快捷方式/数据目录；
  - 服务 `ServiceInstall`/`ServiceControl`；
  - 队列 CA `Impersonate="yes"` + 强制 WSD IPP 端口；
  - 数据目录 Permanent，卸载保留；
  - **未签名**：可安装/卸载；无证书；SmartScreen 可能提示；
- 关键修复：SYSTEM 下 `Add-Printer -IppURL` 会建 HTTP 端口，丢失
  copies/sides/color；改为用户 impersonation + 清理副本 + WSD 强制；
- 图标：新增 PrintPal.ico（多尺寸），嵌入 App/Service exe、WPF 窗口、托盘、快捷方式和 ARP DisplayIcon；
- 验证：
  - 服务模式 5p×2 彩色双面 → **¥6.40** → FakePrinter 535,998 B；
  - MSI 安装 → 服务 Running → WSD 队列 → 5p×2 → **¥6.40**，PDF 537,334 B；
  - UI service mode；
  - MSI 卸载：服务/队列/程序/快捷方式移除，数据保留；
- 测试仍为 **98** 全绿；
- 文档：`p7-service-msi.md`、ADR-007/008、架构/安装/运行/测试/README 更新；
- 已知：MSI 未签名、无安装向导、共享 SQLite/Users Modify、未做升级/回滚/多用户压测。

### P8：标签计费 + 三模式（2026-10-29）

- 新增 **标签计费**：
  - 默认 `标签1`、`标签2`……，店主可改名/备注；
  - 每个标签一个 Open 会话，切换标签不关闭其他会话；
  - 始终存在当前标签；结账自动归档并切换下一个标签，没有则创建 `标签N`；
  - 结账只计 `Succeeded` 任务；存在待处理任务时拒绝；
  - 历史会话快照金额/页数/标签名。
- 新增 **完整 / 简洁 / 极简** 三模式：
  - 完整：配置 Tab + 文件列表 + 历史；
  - 简洁：标签条 + 金额 + 文件列表 + 新建/结账；
  - 极简：仅金额/标签/结账，无文件列表，默认右下角；
  - 每个模式独立保存位置/尺寸和置顶开关。
- 隐私：
  - 移除主界面今日/本周/本月全店统计；
  - 主界面和极简只显示当前标签本次金额；
  - 历史结账放完整模式。
- 命名：UI 统一为 `PrintPal`，移除“打印助手”。
- DB：
  - `BillingTags`、`BillingSessions` 表；
  - `PrintJobs` 增加 `BillingTagId/BillingSessionId/TagNameSnapshot`；
  - 旧库自动加列；
  - 标签/会话/结账使用事务。
- 测试：新增标签/结账测试；总计 **102** 全绿。
- 本轮不构建 MSI；用本地进程内 Agent 验证。

### P9.1：界面微调（2026-10-29）

- 极简模式去掉“本次金额”标签与单数显示，只保留标签、金额和结账；
- 简洁模式文件列表去掉时间列；
- 完整/简洁任务列表把“页数 · 份数”拆为“页数”“份数”两列；
- 置顶按钮由文字改为图钉图标（E718 未置顶 / E840 已置顶），提示文字随状态切换；
- 完整模式打印记录列表去掉时间列，时间仅在任务详情中展示；
- 完整模式任务详情改为单击任务后在右侧显示，未选中时不占空间（去掉自动选中首行）；
- 完整模式筛选栏去掉“本标签”下拉（列表本就只显示当前标签）；
- 完整模式状态栏右侧改为显示 `PrintPal <版本号>`；
- 完整模式主面板圆角改为同心 2 px（外层 DWM 圆角 8 − 内缩 6），修复四角边框比上下边框宽的问题；
- 完整模式侧栏品牌去掉“打印计费工作台”副标题；品牌 Logo 与页面标题下移，Logo 顶部与标题文字齐平；
- 完整模式顶栏行高 64→72，页面副标题与下方卡片间距加大，拖拽热区 CaptionHeight 同步 78；
- 完整/简洁模式的满高卡片按圆角裁剪内容，修复表格行与空状态遮罩盖住卡片底部圆角的问题；
  页面区域底部增加 12 px 边距，卡片与状态栏明显分隔，不再有“超出边界”的观感；
- 三种模式的切换从下拉框改为平铺的图标按钮组（当前模式高亮），刷新按钮去掉文字只留图标；
- 窗口控制按钮（最小化/最大化/关闭）从 46×64 通栏改为 34×34 紧凑按钮，悬浮背景与图标按钮一致；
  置顶按钮并入窗口按钮同一组，移除中间分隔线；
- 刷新按钮移到模式按钮组左侧；头部右侧留空与卡片边距对齐（完整 26 px、简洁 16 px）；
  窗口按钮字形统一回 10 px，修复最小化与最大化/关闭粗细不一；
- 切换到完整/简洁模式时窗口自动居中于主屏工作区（极简模式保持右下角记忆位置）；
- 简洁模式移除底部“最后刷新”状态行，列表卡片直接到底。

### P9：客户端界面整体重构（2026-10-29）

- 布局：
  - 深色左侧导航（打印记录 / 历史结账 / 打印机 / 路由规则 / 价格表 / 设置）；
  - 完整模式改为深色外壳 + 内缩 6 px 的浅色圆角主面板，导航栏上下延伸包裹主面板；
    主面板四角 8 px 圆角（与 Windows 11 外层一致），并使用 `RectangleGeometry` 裁剪内容；
  - 顶部收银条固定标签、合计金额和结账，不再堆叠多条工具栏；
  - 打印记录改为左侧列表 + 右侧任务详情；历史结账改为批次/明细双栏。
- 视觉：
  - 新增 `Themes/Theme.xaml` 统一色板、字体、卡片、按钮、输入框、下拉框、复选框、
    表格、状态胶囊、滚动条和对话框；
  - 表格行高 46、无竖线、悬浮/选中浅色、金额右对齐加粗；窄窗口下保持列宽并显示横向滚动条；
  - 任务/打印机/路由/历史状态使用彩色胶囊；
  - 空列表显示图标与引导文案。
- 窗口：
  - 无边框自定义标题栏、拖动/双击最大化、缩放边框、Windows 11 圆角；
  - 处理 `WM_GETMINMAXINFO`，最大化严格贴合任务栏上方工作区；
  - 三种模式共用视觉，极简模式整理为 316×186 深色悬浮卡。
- 行为：
  - 2 秒自动刷新后保留任务/打印机/路由/历史批次选中与表格滚动位置；
  - 任务默认按时间倒序；历史批次时间改为日期+时间双行；
  - 对话框统一卡片布局，小屏幕自动限制高度。
- 验证：`dotnet test PrintPal.sln -c Debug` 102 全绿；截图更新到 `docs/images/ui-*.png`，
  说明见 [ui.md](ui.md)。

---

### P7.1：MSI 向导 + 升级/修复/回滚 + 动态版本（1.0.1，2026-10-28）

- MSI 增加 **WixUI_InstallDir** 安装向导：
  - Welcome → License → InstallDir → Verify → Progress → Exit；
  - 支持选择安装目录；License.rtf；
- App/Service 发布到同一 `app\` 目录，共享 .NET/WPF/ASP.NET 运行时：
  - 去掉独立 `service\` 和约 113 MB 重复运行库；
  - MSI 从约 118.9 MB 降到 **75.9 MB**；zip 约 164.8 MB；
- 去掉版本/路径硬编码：
  - `Directory.Build.props` 的 `PrintPalVersion=1.0.1` 为唯一来源；
  - MSI 的 OutputName/OutputPath/PublishDir/ProductVersion 全动态；
  - `Publish-PrintPal.ps1 -Version` 覆盖；发布脚本直接构建 MSI/zip；
- 升级/修复/回滚：
  - 0.6.0 → 1.0.1 升级成功；服务/队列/程序更新，数据保留；
  - 删除 App DLL 后 `msiexec /f` 恢复，服务/队列可用；
  - 无效 `PRINTPAL_QUEUE_URL` 触发 1603；服务/程序/队列回滚，数据保留；
  - 新增 `RollbackQueue` CA；队列安装条件支持修复；
  - 版本从 0.6.x/1.0.0 程序集升到 1.0.1，避免 MSI 文件版本降级漏拷贝；
- 图标：新增 PrintPal.ico（多尺寸），嵌入 App/Service exe、WPF 窗口、托盘、快捷方式和 ARP DisplayIcon；
- 验证：
  - 1.0.1 服务/队列 5p×2 彩色双面 **¥6.40**，PDF 535,174 B；
  - WixUI 向导日志确认所有页面；
  - 98 测试全绿；
- 修复：MSI Repair/升级时队列已是 WSD 则跳过重建；快捷方式写公共桌面+当前用户桌面+开始菜单。
- 新增：`scripts/test/Run-PrintPalTests.ps1` 单入口聚合测试报告；阶段总结。
- 已知：MSI 未签名；升级失败后恢复旧版本未覆盖；共享 SQLite/Users Modify。

---

## 未发布 / 下一步

### P7 可选

- Win11 PSA、Windows 本地队列/USB 转发、多价格表/多币种/角色、打印释放/配额、自动更新；
- 代码签名、服务/UI IPC、SQLite 并发压测、升级失败自动恢复旧版本。

---

## 约定

- 金额单位：分（long）。
- 计价：按“每份”先算双面/单面张数，再乘份数。
- 文档/证据变更：同步更新 `docs/architecture.md`、`docs/test-plan.md`、本文件。
- `参考资料/` 只读；不提交 `artifacts/`、`dist/`、`received/`。




