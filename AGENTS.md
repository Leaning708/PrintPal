# AGENTS.md — PrintPal 仓库约定（AI / 自动化代理必读）

本文件是仓库级的协作约定。**任何 AI 代理（Claude / Codex / ZCode / Cursor / Copilot 等）在本仓库
改代码或跑测试之前都应先读本文件**；关键几条与 [docs/runbook.md](docs/runbook.md) 保持一致，
人也可以对照着看。约定变更时同步更新本文件、`docs/runbook.md` 与 `docs/CHANGELOG.md`。

---

## 0. 开发哲学：项目是逐步完善出来的，发现可改进项就当场完善

PrintPal 处于持续迭代中。开发过程中如果发现**过期、缺失或可以做得更好**的地方（skill、脚本、文档、
测试、约定），不要只记下来留给下次，尽量在**同一次任务里顺手完善**，让仓库越用越顺：

- 测试/文档过期（基线数字、命令、路径变了）→ 当场更新；
- 脚本有坑（编码、执行策略、`$PSScriptRoot`、文件锁、探测副作用）→ 修脚本本身，不要每次都绕过；
- 流程反复手敲 → 沉淀成 skill 或脚本（`.agents\skills\**` 或 `scripts\**`）；
- 约定有新决定 → 同步本文件、`docs\runbook.md`、`docs\CHANGELOG.md` 与相关 skill。

判断标准：

1. 只改与当前工作相关、低风险、可验证的部分；不确定的先说明再动；
2. 改完必须验证（跑脚本 / 测试 / 命令），并在汇报里写清楚改了什么、依据是什么；
3. 本次确实不适合改（范围大、需要用户决策、有数据风险）→ 在汇报里列出并说明原因，不要默默跳过；
4. 修复既有问题时，顺手把相关基线数字、文档、skill 一并校正，不要留下新的不一致。

这条与「保留现场」（第 1 节）一起构成本仓库的默认工作方式。

---

## 1. 最重要：测试完成后不要关闭 APP

> 用户的明确要求（2026-11-02）：**开发过程中测试完成后，不要关闭 PrintPal 的 App**，
> 用户要接着自己手工验证。请把这条当作本仓库的默认行为，而不是可选项。

必须做到：

- 测试/验证结束后，**PrintPal.App 保持运行**（窗口开着、托盘里有图标即可）；
- 不要执行 `scripts\Stop-PrintPalApp.ps1`，不要 `Stop-Process` / 任务管理器结束 `PrintPal.App`；
- 不要只因"清理环境"就停 `PrintPal.Service`，也不要关掉 FakePrinter（它是转发目标，关了以后
  用户点"转发/重试"会失败）；
- 不要删除或重置测试数据（数据库、`documents\`、`artifacts\p2\fake-printers\received\`），
  用户可能正拿这些数据验证；
- 收尾汇报里写明**仍在运行的进程、端口、测试数据位置与截图路径**，方便用户直接接手。

允许停止/重启的例外（仅限以下情况）：

1. 用户明确要求关闭、停止或重启；
2. 构建或安装/升级必须重启（正在运行的 exe 被占用、MSI 修复/升级、需要独占数据库等）——
   先说明原因，完成后**立即用测试数据重新启动 App 并保持运行**；
3. 单实例互斥或端口/数据锁导致新代码无法验证——同上，重启后保持运行。

不要因为担心"测试环境不干净"而关闭 App；本仓库的原则是**保留现场**。

## 2. 本地跑起来（带测试数据）

| 用途 | 命令 |
|---|---|
| 启动 App（开发构建，推荐） | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-PrintPalApp.ps1 -SeedDemo` |
| 启动 3 台模拟打印机 | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-FakePrinters.ps1` |
| 生成受控打印任务（真实 GDI→队列→IPP） | `.\tools\PrintPal.PrintProbe\bin\Debug\net10.0-windows\PrintPal.PrintProbe.exe --pages 5 --copies 2 --duplex long --color color --paper A4` |
| 健康检查 | `Invoke-RestMethod http://127.0.0.1:8630/health` |
| 看 App 是否在跑 | `Get-Process PrintPal.App, PrintPal.Service` |

端口 / 数据：

- App 与 Agent：`127.0.0.1:8630`（IPP 路径 `/ipp/print`）；
- Fake-A4-Color `8631` / Fake-A4-Mono `8632` / Fake-A3 `8633`（Raw 对应 9101/9102/9103）；
- 开发（standalone）数据目录：`artifacts\p3\data`；已安装服务数据目录：`C:\ProgramData\PrintPal`；
- 日志：`<数据目录>\logs\app.log`（App）/ `C:\ProgramData\PrintPal\logs\service.log`（服务）。

几个容易踩的坑：

- **执行策略**：不要直接 `.\scripts\x.ps1`（可能被拦住），统一用
  `powershell.exe -NoProfile -ExecutionPolicy Bypass -File <脚本>`。
- **`$PSScriptRoot`**：2026-11-02 起 `Diagnose/Start-*/Stop-*` 共 7 个脚本把仓库根解析移到了函数体
  （旧写法在 `[CmdletBinding()]` + `-File` 下会把 `$PSScriptRoot` 当空值报
  `Split-Path : ... empty string`）；仍可用 `-Root <仓库根>` 显式覆盖。
- **服务模式**：如果本机装过 `PrintPal.Service`（P7 MSI），App 会探测 8630 的 `/health` 并自动进入
  **服务模式**（`ServiceAgentProxy`，直接读写 `C:\ProgramData\PrintPal`）。此时 `-SeedDemo` 不生效，
  测试数据来自服务库；要回到纯开发种子模式，需先停服务（属于第 1 节的例外，用完保持 App 运行）。

## 3. 构建与测试

```powershell
dotnet build PrintPal.sln -c Debug
dotnet test PrintPal.sln -c Debug
```

- 测试通过后**不要**顺手把 App、服务或 FakePrinter 关掉（见第 1 节）；
- 逻辑改动按项目补/改 `tests\` 下的测试（Core / Ipp / Capture / Storage / Integration）。

## 4. 证据、日志与截图

- 截图、导出、临时证据统一放 `artifacts\`（已 gitignore，例如 `artifacts\app-testdata.png`）；
- 不要提交 `bin\`、`obj\`、`dist\`、`artifacts\`、`.zcode\`；
- 文档改动同步 `docs\CHANGELOG.md`；命令/流程变化同步 `docs\runbook.md`；架构变化同步
  `docs\architecture.md` 与 `docs\decisions\ADR-*.md`。

## 5. 仓库技能（.agents/skills）

项目内置 4 个 skill，放在 `.agents/skills/`（DSH 与兼容 `SKILL.md` 的代理会自动发现，随仓库提交）。
遇到对应场景先加载 skill，不要重新手写流程：

| skill | 用途 |
|---|---|
| `printpal-dev-env` | 启动/恢复本地测试环境（FakePrinter + App + 队列）、判定服务模式、造真实测试任务、**保留现场** |
| `printpal-build-test` | 构建 + 跑测试并核对基线；处理「运行中的 App 锁住 `bin\`」导致的构建降级（退出码 2） |
| `printpal-msi` | 发布/MSI 安装升级修复卸载、队列重建；只读部署状态脚本 |
| `printpal-troubleshoot` | 按打印链路分段排障 + 只读诊断证据包（`artifacts\diagnostics\`） |

维护约定：新增/修改 skill 时同步本表与 `docs\CHANGELOG.md`；skill 脚本保持 ASCII 输出、兼容
PowerShell 5.1，并且默认**只读或以"不改变现状"的方式运行**（启动类脚本只启动、不停止）。

## 6. 代码约定（简述）

- C# / .NET 10 / WPF；UI 文案为中文；`Nullable` 开启，遵循现有文件风格；
- 领域逻辑在 `PrintPal.Core`，UI 只做展示与交互；SQLite 读写集中在 `PrintPal.Storage`；
- 改公开 API 时同步改调用方与测试；提交信息沿用 `feat(ui): ...` / `fix(...)` / `docs: ...` 风格。
