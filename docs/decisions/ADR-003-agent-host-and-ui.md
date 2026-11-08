# ADR-003：Agent 作为类库，P2 用控制台宿主，P3 用 WPF 托管

- 状态：Accepted
- 日期：2026-10-28

## 背景

打印捕获、计价、路由、转发、SQLite 是长期后台能力；UI 是另一层。P2 需要先验证后台
链路，P3 再做 WPF UI。若一开始把全部逻辑写进 WPF 窗口，后台难以测试、也难以服务化。

## 决策

- `PrintPal.Agent`（类库）承载：
  - `AgentOptions`
  - `PrintJobPipeline`
  - `PrintPalAgent`（初始化 DB、seed、IppServer、生命周期）
  - `PrintPalIppJobHandler`
- `PrintPal.AgentHost`（控制台）只是启动 Agent 的薄宿主，用于 P2 验证；
- P3 的 `PrintPal.App`（WPF，`net10.0-windows`）在进程内托管 `PrintPalAgent`；
- 队列捕获、计价、路由、转发不依赖 UI 框架。

## P3 落地（2026-10-28）

- `PrintPal.App` 已实现，启动即托管 Agent（8630），关闭窗口隐藏到托盘；
- UI 使用代码后置（code-behind）+ 简单行模型，未引入 MVVM 框架；
- 记录/打印机/路由/价格/设置五页、托盘、单实例、HKCU 自启已可用；
- 后续如页面复杂，可在不改 Agent 的前提下替换为 MVVM。

## 后果

正面：

- 后台链路可用集成测试覆盖（已有 4 个 Agent 测试）；
- 控制台宿主可快速做 Win10/Win11 验证；
- P3 UI 可替换宿主而不重写业务；
- P7 可把 Agent 拆成 Windows 服务/独立进程。

负面：

- P2 阶段需要手动启动 AgentHost；P3 起由 WPF 托管并提供托盘/自启；
- 单进程 MVP 中，UI 退出 = 队列 Offline；P7 服务化解决。
- P3 UI 代码后置，后续若复杂化再重构为 MVVM；不影响 Agent。

## 备选方案

- 一开始做 Windows 服务 + UI 客户端：更重，P2 不需要；
- 全部逻辑放 WPF：难测试，UI 生命周期与打印生命周期耦合。
