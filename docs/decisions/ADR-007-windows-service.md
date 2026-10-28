# ADR-007：Windows 服务托管 Agent，UI 共享 SQLite + 扫描器

- 状态：Accepted
- 日期：2026-10-28
- 阶段：P7.1

## 背景

P3–P6 的 Agent 跑在 WPF 进程内，用户关闭 UI 或未登录时队列 Offline。打印店/前台需要
“APP 关了也能收任务”。

## 决策

- 新增 `PrintPal.Service`（.NET Worker，`UseWindowsService`）托管 `PrintPalAgent`；
- 服务名 `PrintPal.Service`，LocalSystem，自动启动，监听 `127.0.0.1:8630`；
- 数据从用户目录迁到 `%ProgramData%\PrintPal`；
- 服务启动时用 `icacls` 给本机 `Users` 授 Modify（SID 本地化安全）；
- UI 通过 `/health` 探测服务：
  - 服务在跑 → `ServiceAgentProxy`；
  - 不在 → 便携模式，进程内 Agent；
- P7 先不做 IPC：UI 直接读写共享 SQLite，服务每 2 秒扫描 `Priced` 任务入队；
- 手动重试/目标覆盖只改 DB 状态，服务扫描器负责入队；
- `ForwardingWorker` 用 `ConcurrentDictionary` 去重；
- `PrintJobPipeline` 每单读取 DB `autoForward` 设置。

## 后果

正面：

- UI 关闭/未登录仍能收任务；
- 不需要新的 IPC/协议/序列化层，P7 交付快；
- UI 与服务共享现有 DB/表结构，迁移量小；
- 服务可独立升级/重启。

负面：

- UI 与服务共享 SQLite（WAL），多用户/高并发需压测；
- `ProgramData` 对 Users Modify，隐私/权限需后续角色化；
- 队列 CA 需要 impersonation 才能得到 WSD IPP 端口；
- 服务 LocalSystem 运行，文档/数据 ACL 需谨慎；
- 后续若要 SaaS/多租户，应改命名管道/gRPC + API 层。

## 备选方案

| 方案 | 不选原因 |
|---|---|
| 命名管道/gRPC + UI 全走 API | P7 周期大；当前 DB 共享足够 |
| 每用户服务实例 | 队列/驱动是机器级；复杂 |
| 继续进程内 + 托盘自启 | 关 UI/未登录丢任务 |
| Windows 服务 + UI 只读 DB | UI 不能改价格/路由；不可行 |

## 验证

- 服务手工安装/启动，打印 5 页×2 份 → ¥6.40 → FakePrinter；
- UI `--background` 日志显示 service mode；
- 服务关闭/重启；数据保留。
