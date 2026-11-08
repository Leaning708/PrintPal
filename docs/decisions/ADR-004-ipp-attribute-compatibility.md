# ADR-004：Windows IPP 属性兼容集必须精确

- 状态：Accepted
- 日期：2026-10-28

## 背景

Windows 的 IPP Class Driver 对属性类型和取值非常敏感。P0 中出现过：

- 队列 Offline、任务卡 `Printing, Retained`；
- `Get-Printer-Attributes` 请求正常，但打印任务不发送；
- Application 日志（Provider `Universal Print`）报类型错误。

## 决策

`PrintPal.Ipp.IppPrinterAttributes` 固化一套 Windows 兼容属性集，并遵守：

| 规则 | 说明 |
|---|---|
| `printer-uri-supported` 必须包含 `ipp://…` | 只有 HTTP URI 会导致队列 Offline |
| `media-*-margin-supported` 用 integer | 用 range (0x33) 会报 `GetIntegerValue` |
| `finishings-*` 用 enum | 用 keyword 会报类型错误 |
| `document-format-details-supported` 用 keyword/string | 用 boolean 会报类型错误 |
| A3 关键字用 `iso_a3_297x420mm` | `iso_a3_420x297mm` 会变成自定义 `_420x297mm`，A3 任务按 A4 进来 |
| 提供 `media-size-supported` 集合 | 只有 `media-supported` 不够 |
| 提供 gzip 解压 | Windows 可能发 `compression=gzip` 的 PDF |
| 递归解析 `media-col`/`media-size` 集合 | 扁平解析会把尺寸值错误地并到 `copies` |
| `Create-Job` + `Send-Document` 关联 | Windows 实际路径，按 job-id 合并属性 |
| `Add-Printer -IppURL` 轮询 | 成功但可能不返回对象 |

## 后果

- 修改能力集/新增属性时必须回归 Windows 队列测试；
- 新增属性要确认 IPP 类型（integer/enum/keyword/boolean/collection）；
- 失败时优先看 Application 日志的 `Universal Print` 记录，而不是只看 IPP 请求；
- 保留 P0 探针作为兼容性诊断工具。

## 验证

- `tests/PrintPal.Ipp.Tests`：编解码、集合、gzip、Create-Job/Send-Document、服务端/客户端；
- P0 Windows 11/10 实测：队列 Normal、任务完成、PDF 页数正确；
- 主机端到端：5 页 × 2 份彩色双面 → ¥6.40 → FakePrinter 成功。
