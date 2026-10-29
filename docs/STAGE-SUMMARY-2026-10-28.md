# PrintPal 阶段性总结（2026-10-28，1.0.1）

> 用途：阶段收口、交接、后续迭代基线
> 当前产物：Windows 服务 + unsigned MSI（WixUI 向导）
> 代码基线：Git `main` / `v1.0.1`

---

## 1. 项目目标

PrintPal 是 Windows 打印计费工具：

- 虚拟打印机捕获 WPS / Office / PrintProbe 等应用的打印任务；
- 解析纸张、彩色/黑白、单双面、页数、份数；
- 按“每张纸”计价，双面奇数页按单面纸计费；
- 汇总金额；
- 按打印机/路由规则转发到真实打印机；
- 打印机、价格、路由、设置可配置；
- 支持 Windows 10/11；无真实打印机时用 FakePrinter 验证。

---

## 2. 已完成阶段

| 阶段 | 内容 | 结果 |
|---|---|---|
| P0 | IPP 捕获可行性、Windows 队列、Win10/11 差异 | 通过 |
| P1 | Core/Ipp/Capture/Storage/Forwarding/FakePrinter + 测试 | 通过 |
| P2 | IPP 捕获服务 + 流水线 + 队列安装 | 真实队列 E2E |
| P3 | WPF UI / 托盘 / 设置 / 单实例 | 冒烟通过 |
| P4 | 异步转发 / 重试 / 超时 Unknown / 人工确认 / 目标覆盖 | E2E |
| P5 | 自包含发布 / 安装 / 卸载 / 修复 / 诊断 | E2E |
| P6 | Win10/Win11 安装版验收、性能、安全 | Win11 + Win10 PASS，MVP 0.5.0 |
| P7.1 | Windows 服务、ProgramData、UI service mode、扫描器 | 通过 |
| P7.2 | WiX MSI、WSD 队列、WixUI 向导、升级/修复/回滚、动态版本 | 1.0.1 通过 |

---

## 3. 当前架构

```
Windows Spooler
  └─ PrintPal Virtual Printer (WSD IPP)
       └─ http://127.0.0.1:8630/ipp/print
            └─ PrintPal.Service (LocalSystem, Automatic)
                 ├─ IppServer
                 ├─ PrintJobPipeline（捕获/解析/计价/路由）
                 ├─ SQLite  %ProgramData%\PrintPal
                 ├─ ForwardingWorker（IPP/Raw 9100）
                 └─ pending-job scanner
PrintPal.App (WPF)
  └─ 检测 /health → ServiceAgentProxy（service mode）
       或进程内 Agent（便携模式）
```

关键数据：

- 数据目录：`C:\ProgramData\PrintPal`；
- 数据库：`printpal.db`（SQLite WAL）；文档：`documents`；日志：`logs`；
- UI 与服务共享 DB；服务每 2 秒扫描 `Priced` 任务入队；
- 服务启动时给本机 `Users` 授予数据目录 Modify；
- 队列必须是 **WSD IPP 端口**；HTTP Internet Port 会丢失 copies/sides/color。

---

## 4. 当前版本与产物

| 项 | 值 |
|---|---|
| 版本 | 1.0.1 |
| MSI | `dist\PrintPal-1.0.1-win-x64\PrintPal-1.0.1-x64.msi`，约 75.9 MB |
| ZIP | `dist\PrintPal-1.0.1-win-x64.zip`，约 164.8 MB |
| 代码签名 | 未签名；SmartScreen 可能提示 |
| 安装 UI | WixUI_InstallDir（欢迎/许可/目录/确认/进度/完成） |
| 快捷方式 | 公共桌面 + 当前用户桌面 + 公共开始菜单 |
| 服务账号 | LocalSystem；数据 ACL 由服务设置 |
| 升级 | 0.6.0 → 1.0.1 实测 |
| 修复 | `msiexec /f` 实测 |
| 回滚 | 无效队列 URL → 1603，服务/程序/队列回滚，数据保留 |

---

## 5. 验证结果

- 自动化测试：**98/98**（Core 57、Ipp 11、Capture 10、Storage 6、Integration 14）；
- 服务/队列 5 页×2 份 A4 彩色双面：**¥6.40**，FakePrinter PDF 535,174 B；
- MSI 安装、WixUI 页面、快捷方式、升级、修复、回滚、卸载均验证；
- 大脚本测试报告：
  - `scripts/test/Run-PrintPalTests.ps1`
  - `artifacts/test-report.md`
  - `artifacts/test-report.json`

---

## 6. 关键决策

- ADR-001：自研 IPP codec/server/client；
- ADR-002：按每张纸计价，双面奇数页单面计费；
- ADR-003：AgentHost + WPF；
- ADR-004：IPP 属性兼容（PDF、gzip、media-col 等）；
- ADR-005：异步转发 + 有界重试 + Unknown；
- ADR-006：自包含发布；
- ADR-007：Windows 服务 + 共享 SQLite + 扫描器（不做 IPC）；
- ADR-008：WiX MSI，unsigned；WixUI；动态版本。

---

## 7. 已知问题 / 风险

- MSI 未签名；企业分发信任不足；
- MSI 依赖 spooler/驱动/队列这些 **有状态外部资源**，比纯文件安装复杂；
- WSD/HTTP 端口由 Windows 决定，需要恢复逻辑；
- 升级失败后自动恢复旧版本未完整回归；
- 服务/UI 共享 SQLite；多用户/高并发需压测/角色化；
- 首次安装 DB 只自带价格表，打印机/路由需 UI 配置；
- MSI 未做完整升级失败回滚、崩溃恢复、并发压测；
- 当前版本唯一来源 `Directory.Build.props`；CI 必须传版本。

---

## 8. 关于 MSI 方案

MSI 的问题主要不是 WiX 本身，而是：

1. 安装器要操作 **驱动 + 打印队列 + Windows 服务**，这些是全局有状态资源；
2. `Add-Printer -IppURL` 在 SYSTEM/用户、不同 spooler 状态下可能产生 HTTP 或 WSD 队列；
3. MSI 组件/文件版本/升级顺序对程序集版本敏感（已统一到 1.0.1）；
4. Repair/Upgrade 会重新执行队列 CA，队列状态可能变化。

建议：

- **短期**：保留 MSI，但把队列创建做成“幂等 + 恢复”；
- **中期**：MSI 只安装文件/服务/快捷方式；队列安装移到服务首次启动或独立 `SetupQueue` 工具，减少 Repair 的外部副作用；
- **替代**：如果不需要 MSI 的企业分发/组策略，Inno Setup 会更简单；但会失去 MSI 标准修复/卸载；
- **不建议**：MSIX（服务/驱动/队列不适合）。

---

## 9. 关于 PowerShell 授权

之前每个需要管理员的步骤都单独 `Start-Process -Verb RunAs`，所以每次弹 UAC。
现在：

- 单元/集成测试用 `scripts/test/Run-PrintPalTests.ps1` 一次跑完，不需要管理员；
- 需要 MSI 的回归应由同一个入口一次提权，再在内部跑子脚本；
- 日常源码修改/测试不要再逐条提权。

---

## 10. 下一步

1. 用 `Run-PrintPalTests.ps1` 作为固定回归入口；
2. 把 MSI 队列安装从 CA 迁移到服务首次启动/独立 setup 工具；
3. 补升级失败恢复旧版本、Service 崩溃恢复、SQLite 并发测试；
4. Win11 PSA、本地队列/USB、多价格表/角色、自动更新；
5. 购买证书后给 MSI/EXE 签名；
6. 建立远程 Git 仓库/CI。

---

## 11. 参考

- [README](../README.md)
- [架构](architecture.md)
- [P7 服务/MSI](p7-service-msi.md)
- [安装](installer.md)
- [运行手册](runbook.md)
- [测试计划](test-plan.md)
- [ADR-007](decisions/ADR-007-windows-service.md)
- [ADR-008](decisions/ADR-008-msi-unsigned.md)

