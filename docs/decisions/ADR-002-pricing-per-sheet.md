# ADR-002：按“张”计价，双面按“每份”处理奇数页

- 状态：Accepted
- 日期：2026-10-28

## 背景

打印店/办公场景中，单面与双面价格不同；双面打印时，如果单份页数为奇数，
每份最后一张纸只有一面有内容。不同份的单面最后一张不能拼成一张双面纸。

## 决策

价格规则按“张”配置：

- `SimplexPriceCents`：单面价/张；
- `DuplexPriceCents`：双面价/张。

计价按“每份”先算，再乘份数：

```
duplexPerCopy   = P / 2
simplexPerCopy  = P % 2
amountPerCopy   = duplexPerCopy * duplexPrice + simplexPerCopy * simplexPrice
amount          = amountPerCopy * N
```

单面任务：`amount = P * simplexPrice * N`。

金额用“分”（long）存储，避免浮点误差。

## 例子

A4 黑白：单面 20 分/张，双面 30 分/张。

| 场景 | 计算 | 金额 |
|---|---|---|
| 4 页 × 1 份，双面 | 2×30 | ¥0.60 |
| 5 页 × 1 份，双面 | 2×30 + 1×20 | ¥0.80 |
| 5 页 × 3 份，双面 | (2×30 + 1×20) × 3 | **¥2.40** |
| 5 页 × 3 份，错误地按总页数 15 | 7×30 + 1×20 | ❌ ¥2.30 |
| 3 页 × 2 份，双面 | (1×30 + 1×20) × 2 | **¥1.00** |
| 3 页 × 2 份，错误地按总页数 6 | 3×30 | ❌ ¥0.90 |

## 后果

- `PriceCalculator` 必须接收 `pagesPerCopy`、`copies`、`duplexMode`；
- `PrintJob` 保存 `DuplexSheetsPerCopy`、`SimplexSheetsPerCopy`、总张数、金额、公式；
- UI 的“单价”可能显示两个价格（双面 + 奇数页单面），详情展示完整公式；
- WPS 若把份数摊平成页数，`copies=1`、`P` 变大；金额仍可能正确，但“份数”显示 1。
  提供手工修正，不能自动猜测。

## 备选方案

- 按“面”计价：把双面价设为 `2 × 单面价 × 折扣` 也能表达，但奇数页仍按单面价；
  当前模型更直接，不排斥按面定价。
- 按总页数合并计算：错误，已拒绝。
