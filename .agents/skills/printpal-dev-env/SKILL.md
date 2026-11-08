---
name: printpal-dev-env
description: 启动并保留 PrintPal 本地测试环境（FakePrinter + WPF App + 虚拟队列），判定服务模式/独立模式，造真实测试任务，收尾时保留现场不关闭。
whenToUse: 需要把 PrintPal 跑起来看 UI、造测试数据、恢复用户可手工验证的现场，或排查「App 没起来 / 界面没数据 / 8630 被占用 / 单实例」时使用。
---

# PrintPal 本地开发环境

## 先记住两条硬约束

1. **测试完成后不关闭 App**（仓库约定，见 `AGENTS.md` 第 1 节）：不要 `Stop-Process`、
   不要跑 `scripts\Stop-PrintPalApp.ps1`、不要停 `PrintPal.Service`、不要关 FakePrinter。
   用户要接着手工验证；本项目原则是**保留现场**。
2. **单实例**：`PrintPal.App` 用进程内互斥 `Local\PrintPal.App.SingleInstance`，第二个实例会弹
   「PrintPal 已在运行」并退出。要换配置只能先停（属上面约定里的例外），成功后**立即恢复运行**。

## 环境事实

| 项 | 值 |
|---|---|
| App / Agent IPP | `http://127.0.0.1:8630/ipp/print`，健康检查 `GET /health` |
| FakePrinter | `8631` Fake-A4-Color / `8632` Fake-A4-Mono / `8633` Fake-A3（Raw 9101/9102/9103） |
| 开发数据 | `<repo>\artifacts\p3\data`（`Start-PrintPalApp.ps1 -Data` 可覆盖） |
| 服务数据 | `C:\ProgramData\PrintPal` |
| UI 日志 | **跟随 `--data` 目录**：开发启动（默认 `-Data artifacts\p3\data`）写在 `artifacts\p3\data\logs\app.log`，服务模式也一样；裸启动/安装版默认才是 `%LOCALAPPDATA%\PrintPal\logs\app.log`（`App.xaml.cs` 用 `startup.Agent.DataDirectory` 建日志目录） |
| 服务日志 | `C:\ProgramData\PrintPal\logs\service.log` |
| FakePrinter 日志/产物 | `artifacts\p2\fake-printers\*.out.log`，收到的 PDF 在 `received\<name>\` |

**模式判定**（决定数据在哪、`-SeedDemo` 是否有用）：

- 服务在跑且 `8630/health` 返回 ok → App 进入**服务模式**：`ServiceAgentProxy` 直接读写
  `C:\ProgramData\PrintPal`；`-SeedDemo` **不生效**，数据和队列都以服务库为准（见 troubleshooting A5）。
- 否则 App 自己托管 Agent（**独立模式**）：数据在 `--data` 目录，`-SeedDemo` 会 seed 3 台 FakePrinter +
  3 条路由 + 默认价目表。

## 一键启动（幂等，推荐）

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  .\.agents\skills\printpal-dev-env\scripts\Start-PrintPalTestEnv.ps1
```

脚本做的事（**只启动、不停止任何东西**）：

1. 从脚本位置向上找含 `PrintPal.sln` 的仓库根（可用 `-RepoRoot` 指定）；
2. 8631–8633 有 FakePrinter 未就绪 → 调用 `scripts\Start-FakePrinters.ps1 -Root <repo>`；
3. 没有 `PrintPal.App` 进程 → 调用 `scripts\Start-PrintPalApp.ps1 -Root <repo> -SeedDemo`；
4. 等 `/health` 就绪、等 App 进程与窗口出现，打印模式 / 进程 / 端口 / 数据 / 队列汇总，并提示「测试后不要关闭」。

> 2026-11-04 修复：脚本不再用 `& powershell.exe` 或 `Start-Process -Wait` 调仓库脚本——FakePrinter/App
> 这些长期运行的孙进程会继承调用方的 stdout 管道，在 AI 代理/CI 这类管道宿主里会一直等不到 EOF
> 而“卡住”。现在改为 `Start-Process`（不重定向输出，走 ShellExecute）启动子脚本，再用 `/health`
> 轮询确认结果；App 启动后还会等进程和窗口出现，避免汇总里误报 `NOT RUNNING`。

参数：`-RepoRoot <path>`、`-Port 8630`、`-NoSeedDemo`、`-SkipFakePrinters`、`-SkipApp`、`-WaitSeconds 30`。

## 手动等价流程

```powershell
# 1) 模拟打印机（转发目标）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-FakePrinters.ps1 -Root <repo>

# 2) App（带种子数据）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-PrintPalApp.ps1 -Root <repo> -SeedDemo

# 3) 验证
Invoke-RestMethod http://127.0.0.1:8630/health
Get-Process PrintPal.App, PrintPal.Service, PrintPal.FakePrinter -ErrorAction SilentlyContinue
Get-Printer -Name 'PrintPal Virtual Printer' | Select-Object PrinterStatus, PortName
```

## 造一条真实测试任务

走「队列 → IPP → 捕获 → 计价 → 路由 → 转发」全链路，比直接写库更可信：

```powershell
.\tools\PrintPal.PrintProbe\bin\Debug\net10.0-windows\PrintPal.PrintProbe.exe `
  --printer 'PrintPal Virtual Printer' --pages 5 --copies 2 --duplex long --color color --paper A4 `
  --name '测试-A4彩色双面-5页x2份'
```

期望：App 收银台出现任务、金额 **¥6.40**、状态「已转发」；
`artifacts\p2\fake-printers\received\Fake-A4-Color\` 新增一个 PDF；
`app.log` 出现 `JOB ... = ¥6.40 -> Fake-A4-Color [Succeeded]`。

常用规格与金额（默认价目）：A4 黑白单面 1 页 ×1 = ¥0.20；A4 彩色双面 5 页 ×2 份 = ¥6.40；
A3 彩色双面 3 页 ×1 = ¥4.00。计价公式见 `README.md`「计价模型」。

## 常见坑

- **执行策略**：直接 `.\scripts\x.ps1` 可能被拦；统一
  `powershell.exe -NoProfile -ExecutionPolicy Bypass -File <脚本>`。
- **`$PSScriptRoot` 已修复**：2026-11-02 起 `Diagnose/Start-*/Stop-*` 共 7 个脚本把仓库根解析移到函数体，
  `-File` 调用不再需要 `-Root`；旧安装包或旧脚本仍可能报 `Split-Path : ... empty string`，补
  `-Root <repo>` 兜底。
- **8630 占用排查**：`Get-NetTCPConnection -LocalPort 8630` 有时返回空，用
  `netstat -ano | Select-String ':8630'` 更可靠；占用者通常是 `PrintPal.Service` 或上一个 App/AgentHost（A4）。
- **关窗只是隐藏到托盘**（D2）：`Get-Process` 仍在不代表窗口可见；需要时把窗口置前。
- **UI 没数据**：先确认 App 处于哪种模式、读的是哪个库（A5/D3），别在错的库上找任务。
- 装过 MSI 的机器上，`-SeedDemo` 通常没用（服务模式），需要种子数据得先停服务再起独立模式，
  用完按约定把 App 留在运行状态。
- **构建/测试与运行中的 App 冲突**：App/FakePrinter 会锁住自己的 `bin\`，整解决方案构建会报
  `MSB3027/MSB3021`。只改类库时用 `printpal-build-test` skill 的降级路径即可；改 UI/Service 时需要
  按约定例外短暂停止再构建，构建后立即用本脚本恢复运行。

## 收尾汇报模板

```text
App        : pid <id>（服务模式 / 独立模式），窗口标题 <title>
Service    : PrintPal.Service <状态>，端口 8630 <ok/fail>
FakePrinter: 8631/8632/8633 <ok/缺失>
数据       : <C:\ProgramData\PrintPal 或 artifacts\p3\data>，队列 <Normal>
保留现场   : App 未关闭，可直接手工验证
```

## 相关文档

- `AGENTS.md`（保留现场约定）、`docs\runbook.md` 第 0/3 节
- `docs\troubleshooting.md` A4 / A5 / D1 / D2 / D3
- `README.md`「P3 WPF UI」「计价模型」
