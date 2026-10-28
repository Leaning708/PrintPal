# PrintPal 变更记录

本项目按阶段推进；每个阶段结束更新本文件、`docs/architecture.md`、`docs/test-plan.md`。

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
- 验证：
  - 服务模式 5p×2 彩色双面 → **¥6.40** → FakePrinter 535,998 B；
  - MSI 安装 → 服务 Running → WSD 队列 → 5p×2 → **¥6.40**，PDF 537,334 B；
  - UI service mode；
  - MSI 卸载：服务/队列/程序/快捷方式移除，数据保留；
- 测试仍为 **98** 全绿；
- 文档：`p7-service-msi.md`、ADR-007/008、架构/安装/运行/测试/README 更新；
- 已知：MSI 未签名、无安装向导、共享 SQLite/Users Modify、未做升级/回滚/多用户压测。

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


