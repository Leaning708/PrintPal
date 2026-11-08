# ADR-001：用本机 IPP 队列 + IPP 服务端捕获打印任务

- 状态：Accepted
- 日期：2026-10-28
- 决策者：PrintPal 开发

## 背景

需要同时支持 Windows 10 22H2 与 Windows 11，并捕获 WPS/Office 等应用的打印任务。
参考资料已验证 PSA（Print Support App）虚拟打印机只在 Windows 11 24H2+ 可用，且需要
MSIX + WinRT 组件；Windows 10 只能走自研打印驱动或其它机制。自研 64 位驱动需要 EV 证书 +
微软认证签名，成本和周期都高。

## 决策

采用 **本机 IPP 队列 + 自研 IPP 服务端**：

- 用系统自带的 `Microsoft IPP Class Driver` 创建指向 `http://127.0.0.1:8630/ipp/print` 的队列；
- PrintPal Agent 实现 IPP 服务端，接收 `Create-Job`/`Send-Document` 或 `Print-Job`；
- Windows 把文档渲染为 PDF（Win11 可能 gzip），PrintPal 用 PdfPig 解析页数；
- 从 IPP 作业属性读取 copies/sides/color/media-col。

## 为什么可行

P0 实测（Win11 26300 + Win10 19045）：

- 两系统都能创建队列并捕获；
- 两系统实际发送 PDF；页数可解析；
- `copies`/`sides`/`color`/`media-col` 都可拿到；
- WPS 在 IPP 路径下正确传递 `copies`，没有旧 PSA 调研中的“内容倍乘”。

## 后果

正面：

- 无需自研驱动/签名；
- Win10/Win11 共用一套捕获代码；
- 与转发协议一致（IPP），FakePrinter 可复用同一套协议栈；
- 不依赖 Windows App SDK / MSIX / PSA 版本。

负面/约束：

- Win10 需要安装器显式 `Add-PrinterDriver -Name 'Microsoft IPP Class Driver'`；
- IPP 属性类型必须精确匹配 Windows 解析器（见 ADR-004）；
- Agent 不运行时队列 Offline（MVP 用托盘自启缓解）；
- IPP 路径拿不到“来源应用名”；
- 页数依赖 PDL 解析；PWG 页数暂未实现（目标 OS 都发 PDF）。

## 备选方案

| 方案 | 不选原因 |
|---|---|
| PSA（Win11）+ 自研驱动（Win10） | Win10 驱动需签名；两套代码；成本高 |
| 只支持 Win11 PSA | 不满足 Win10 目标 |
| Electron/Tauri UI + 原生打印组件 | 仍需原生队列/驱动；体积/启动代价 |
| PS Class Driver + 本机 TCP 9100 | 属性/颜色/份数保真度不如 IPP；作为后备保留 |

## 证据

- `docs/spikes/ipp-capture.md`
- `dist/p0-win10/result.txt`
- `artifacts/p0/capture/`
