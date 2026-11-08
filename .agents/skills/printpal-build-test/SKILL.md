---
name: printpal-build-test
description: 构建 PrintPal 方案并跑 Core/Ipp/Capture/Storage/Integration 测试，产出聚合报告与基线对比，覆盖编码、执行策略与 MSI 配置的坑。
whenToUse: 修改 C# 代码需要回归、构建或测试失败要定位、或需要核对当前测试基线（121 通过）时使用。
---

# PrintPal 构建与测试

## 一条命令跑完

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  .\.agents\skills\printpal-build-test\scripts\Invoke-PrintPalBuildTest.ps1
```

脚本行为：定位仓库根（脚本目录向上找 `PrintPal.sln`，可用 `-RepoRoot` 覆盖）→
`dotnet build PrintPal.sln -c Debug` → 调用 `scripts\test\Run-PrintPalTests.ps1`（逐项目 TRX +
聚合报告）→ 打印结论与报告路径。

参数：`-RepoRoot <path>`、`-Configuration Debug|Release`、`-SkipBuild`、`-SkipTests`、
`-IncludeMsi`（透传给测试 runner，需要管理员，会弹一次 UAC）。

退出码：`0` 完整构建 + 测试通过；`1` 构建或测试失败；`2` 测试通过但**跳过了 solution 构建**
（App/Service 源码未编译，见下一节）。

## 运行中的 App 会锁住 bin（必读）

`PrintPal.App` / `PrintPal.FakePrinter` / `PrintPal.AgentHost` 运行时会锁住各自
`bin\Debug\...` 下的 DLL，此时整解决方案构建会报 `MSB3027/MSB3021 ... 文件被“PrintPal.App (pid)”锁定`。
而本仓库约定是**测试后不关闭 App**，所以脚本会自动降级：

- 检测到上述进程 → **跳过 solution 构建**，只构建并运行 `tests\` 下的测试项目
  （它们只引用 Core/Ipp/Capture/Storage/Forwarding/Agent，不引用 App/FakePrinter）；
- 汇总逐项目 TRX，写 `artifacts\test-report.md`，退出码 **2**；
- 此时 **App/Service/FakePrinter 源码没有被编译**——如果你改了 UI / Service / FakePrinter 代码，
  必须按 `AGENTS.md` 例外 2 处理：先向用户说明，短暂停 App，构建成功后**立即带测试数据重启并保持运行**。

```powershell
# 改了 UI/Service 后的正确顺序（示例）
.\scripts\Stop-PrintPalApp.ps1 -Root <repo>            # 说明原因后才做
.\scripts\Stop-FakePrinters.ps1 -Root <repo>
dotnet build PrintPal.sln -c Debug
.\scripts\Start-FakePrinters.ps1 -Root <repo>
.\scripts\Start-PrintPalApp.ps1 -Root <repo> -SeedDemo  # 立即恢复，保持运行
```

只改类库/测试（Core、Ipp、Capture、Storage、Forwarding、Agent、tests）时，运行中的 App 不受影响，
脚本的降级路径就是正确做法。

## 基础命令

```powershell
dotnet build PrintPal.sln -c Debug
dotnet test PrintPal.sln -c Debug

# 推荐的聚合跑法（不提示 UAC；-IncludeMsi 才走管理员路径）
.\scripts\test\Run-PrintPalTests.ps1 -Root <repo>
```

产物：

- `artifacts\test-report.json`、`artifacts\test-report.md`（汇总 + 失败详情；降级路径只写 `.md`）
- `artifacts\test-results\<Project>.trx`（逐项目，**不要**把 solution 级 TRX 当权威，后者会互相覆盖）

## 基线

**121** 个测试全部通过：Core 57 / Ipp 12 / Capture 10 / Storage 28 / Integration 14。

> 2026-11-02 修复了 `PrintPal.Storage.Tests.BillingTests` 的 5 个过期断言：提交 `bf0bd38` 把新账单
> 默认名改成「散客」后测试仍断言「客户N」。现在断言改为「散客 + 序号」，README / runbook 的基线
> 数字也校正为 114（legacy 迁移测试里的「标签N → 客户N」映射保持不变）。

- 数量变化时同步更新 `README.md`「构建与测试」、`docs\runbook.md` 第 2 节、`docs\CHANGELOG.md`；
- 新增测试按项目放 `tests\<Project>.Tests\`，命名沿用现有 `*Tests.cs` 风格；
- `dotnet test` 不需要管理员；只有 `-IncludeMsi`（MSI 回归）会提权一次。

## 配置说明

- 日常 `Debug` / `Release` **不构建 MSI**，不会拖慢构建或测试；
- 只有 `-c Installer`（专用解决方案配置）或直接构建
  `packaging\PrintPal.Msi\PrintPal.Msi.wixproj` 才会出 MSI —— 见 `printpal-msi` skill；
- 版本号只在 `Directory.Build.props` 的 `PrintPalVersion` 维护。

## 坑

- **编码**：PowerShell 5.1 会把「UTF-8 无 BOM」的中文文件按 ANSI 读成乱码。读文件/改文件用
  文件读写工具，不要根据 `Get-Content` 的乱码内容做判断；脚本注释建议保持 ASCII。
- **执行策略**：直接运行 `.\scripts\x.ps1` 可能被拦，统一加
  `powershell.exe -NoProfile -ExecutionPolicy Bypass -File <脚本>`。
- **`$PSScriptRoot` 为空**：个别脚本在 `-File` 下默认参数会失败，显式传 `-Root <repo>`。
- **不要顺手清场**：测试不碰运行中的 App，跑完**不要**关闭 App / 服务 / FakePrinter（`AGENTS.md` 第 1 节）。
  如果因为构建占用文件必须重启 App，重启后立即带测试数据启动并保持运行。
- 构建/测试产物都在 `artifacts\`、`bin\`、`obj\`（已 gitignore），不要提交。

## 收尾汇报模板

```text
Build : Debug succeeded / failed
Tests : 121 passed / 0 failed（报告 artifacts\test-report.md）
保留现场: 未关闭 App / 服务 / FakePrinter
```

## 相关文档

- `README.md`「构建与测试」、`docs\runbook.md` 第 2 节
- `docs\test-plan.md`（测试计划与用例索引）
