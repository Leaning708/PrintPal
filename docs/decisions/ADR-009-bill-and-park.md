# ADR-009：账单与挂起（替代“标签 + 会话”双层模型）

- 状态：Accepted
- 日期：2026-10-30

## 背景

收银台的真实场景是：店主给客户 A 打印到一半，客户 B 插队。此时 A 的账不能结，也不能丢，
必须先把 A 的单挂起，给 B 开一张新单，等 A 回来再继续。

原模型用「标签（BillingTag）+ 会话（BillingSession）」表达这件事，问题有三个：

1. **概念错位**：店主说的是“这一单、这位客户”，界面却叫“标签”，也不区分“当前正在记账”和“已挂起”；
2. **空标签堆积**：每次结账都无条件新建下一个标签，`标签1..标签N` 会不断堆积，且和客户没有对应关系；
3. **无法纠错**：打印在捕获瞬间就归属当前标签，B 的任务落在 A 头上后没有任何补救手段。

同时“会话”这层是冗余的：每张标签最多一个进行中的会话，结账后会话只用来存快照。

## 决策

### 1. 用「账单」替代「标签 + 会话」

```csharp
public sealed class Bill {
    Guid Id; int SequenceNumber; string Name; string? Note;
    DateTimeOffset CreatedAtUtc;
    BillStatus Status;            // Active | Parked | Closed
    DateTimeOffset? ParkedAtUtc;  // 最近一次挂起时间（挂起区排序/展示）
    DateTimeOffset? ClosedAtUtc;
    int JobCount; int TotalPages; long AmountCents;   // 结账快照
}
```

- 一张账单 = 一个客户的一次消费；`PrintJob.BillId` 直接指账单，`BillingSessionId` 删除。
- 结账快照写在账单行上（`ClosedAtUtc/JobCount/TotalPages/AmountCents`），已结账账单只读。
- `PrintJob.BillNameSnapshot` 只用于任务列表/日志展示，权威名称是 `Bills.Name`。

### 2. 全局恰好一张 Active 账单

- 不用 `Settings.activeBillingTagId/SessionId` 两个键（它们可能互相矛盾），
  活动状态直接是 `Bills.Status='Active'`，并用部分唯一索引兜底：

```sql
CREATE UNIQUE INDEX UX_Bills_Active ON Bills(Status) WHERE Status = 'Active';
```

- `EnsureActiveBillAsync()`：没有 Active 时建一张 `客户N`；并发冲突（服务与 UI 同时启动）时重读一次。
- 账单编号用 `Settings.billSequence` 单调递增，删除账单不会复用编号。

### 3. 挂起 / 取回 / 结账

| 动作 | 事务内容 |
|---|---|
| 挂起 | 当前账单 `0 单` → 拒绝（`NoBillJobsException`）；否则置 `Parked` + `ParkedAtUtc`，再新建并激活一张空账单；顺带清理其它 0 单的非活动账单 |
| 取回 | 目标必须是 `Parked`；当前 Active `0 单` → 直接删除，有单 → 置 `Parked`；目标置 `Active` |
| 结账 | 有未完成任务 → `CheckoutBlockedException`；否则写快照 → `Closed`，再取**最近挂起**（`ParkedAtUtc DESC`）置 `Active`，没有就新建 |

没有“新建账单”按钮：挂起天然产生下一张，结账/取回都会收敛到“恰好一张 Active”。
空账单不写历史（结账 0 单直接丢弃），所以历史里不会出现 0 单记录。

### 4. 错单纠正：「移到其他账单」

捕获归属在打印瞬间决定，B 的任务可能先落进 A 的账单。任务详情提供 `MoveJobAsync`：

- 目标必须是未结账账单；`Forwarding` 状态与已结账账单拒绝；
- `createNewActive = true` 时新建账单并切换为当前（当前账单转 `Parked`），用于“这位客户开始新的一单”；
- 归属改到目标账单后，两边金额/单数立即变化。

### 5. UI 形态

- **计费条只在「打印记录」页显示**：其它页面不需要“当前账单/结账”，页面高度让给列表。
- 计费条 = 当前账单（双击改名）+ 挂起区 chip 条（点击取回）+ 本单合计 + `挂起` / `结账`；
- 简洁模式在标题栏放 chip 条；极简模式去掉品牌，把位置留给当前账单名和 chip 条；
- 去掉改名/删除按钮：改名 = 双击账单名；空账单自动清理，不再需要删除按钮。
- **历史结账**：默认只显示已结账账单列表（支持时间范围 + 关键字筛选），点某一行才打开右侧明细，
  明细可关闭，刷新不会自动打开。

## 破坏性改动与迁移

应用仍在早期设计阶段、没有正式数据，因此允许改类名/表名/列名。
`InitializeAsync` 里做一次性迁移（幂等）：

1. `BillingTags LEFT JOIN BillingSessions` → `Bills`：
   会话 `Closed` 或标签已归档 → `Closed`（带快照）；等于旧 `activeBillingTagId` → `Active`；其余 → `Parked`；
   归档且没有会话的空标签直接丢弃；
2. 自动名 `标签N` → `客户N`（只改严格匹配自动名的行，手工命名不动）；
3. `PrintJobs`：`BillingTagId → BillId`、`TagNameSnapshot → BillNameSnapshot`、删除 `BillingSessionId`；
4. `DROP TABLE BillingSessions / BillingTags`，删除旧的 `activeBilling*` 设置。

迁移失败时启动直接报错，不带半迁移的库运行。

## 后果

- 领域、存储、Agent、UI、测试全部改用账单语义；表名 `Bills`、列 `BillId`、`BillNameSnapshot`。
- 打印任务只归属当前账单；挂起账单里的任务照常被转发 worker 处理（转发与账单状态无关）。
- 捕获瞬间归属的局限仍在：现场用「移到其他账单」补救，不做捕获时的二次确认（会打断收银）。
- 历史账单只读，任务不能移入/移出；金额以结账快照为准。

## 备选方案

- **保留“标签”只改行为**：改动小，但“标签”不表达账单/挂起，店主认知成本高，已拒绝。
- **保留会话表**：多一张表、多一次 join，只是存结账快照；快照放账单行更简单，已拒绝。
- **用 Settings 记录活动账单**：与 `Bills.Status` 冗余且可能不一致；唯一索引更可靠，已拒绝。
- **捕获时弹窗确认账单**：更准确但打断收银，已拒绝；改为事后「移到其他账单」。
