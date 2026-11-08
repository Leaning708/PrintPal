# P0 环境检查记录

> 日期：2026-10-28（Asia/Shanghai）
> 目的：确认 PrintPal 的开发/验证环境是否满足 Win10 + Win11 的目标，并记录可复现的事实。

---

## 1. 主机（Windows 11）

| 项 | 实测值 | 结论 |
|---|---|---|
| 产品名 | Windows 11 专业版 | — |
| Build | **10.0.26300（26H2）** x64 | 满足 Win11；PSA 可选能力也在 |
| 内存/磁盘 | C: 约 408 GB 可用 | 充足 |
| .NET SDK | **10.0.401** | 使用 .NET 10 LTS |
| .NET 运行时 | 8.0.22 / 8.0.31 / 10.0.5 / 10.0.12 | 自包含发布不依赖目标机 |
| Visual Studio | Community 2026 18.10 | 可选 PSA/MSIX 用 |
| Windows SDK | 10.0.26100.0 / 10.0.28000.0 | 可选 PSA/MSIX 用 |
| NuGet | nuget.org 可访问 | 依赖可还原 |
| PowerShell | Windows PowerShell 5.1（`pwsh` 不在 PATH） | 脚本按 5.1 兼容编写 |
| Spooler | Running / Automatic | 正常 |
| 管理员 | 当前 shell 非提升；UAC 提升可用 | 安装队列需要 UAC |
| 物理打印机 | 无 | 由 FakePrinter 模拟 |

### 1.1 已安装的打印机相关组件

| 项 | 实测值 |
|---|---|
| 打印机 | Microsoft Print to PDF、导出为 WPS PDF、Demo PSA 虚拟打印机 |
| 驱动 | **Microsoft IPP Class Driver v4 已安装** |
| IPP 驱动文件 | `C:\Windows\System32\DriverStore\FileRepository\prnms012.inf_amd64_8a4a1b56f33e44b4\MSIPP.xml` |
| 端口 | COM1-4、FILE:、LPT1-3、PORTPROMPT:、Kingsoft、Demo PSA Virtual Monitor |
| 空闲端口 | 8630–8640、9100–9110 均空闲 |

### 1.2 应用

| 应用 | 实测 |
|---|---|
| WPS Office | 已安装；`KWps.Application` COM 可用（WPS Writer） |
| Microsoft Office | 目录存在，但未注册 `Word.Application` COM；不作为 P0 依赖 |
| 记事本/写字板/Edge | 可用 |

---

## 2. Windows 10 虚拟机

| 项 | 实测值 |
|---|---|
| 虚拟化 | VMware Workstation **17.6.3 build-24583834**，`vmrun` 可用 |
| VMX | `C:\Virtual Machine\Win10\Win10.vmx` |
| 系统 | **Windows 10 Education 22H2，build 19045.6456，64 位** |
| 资源 | 2 vCPU / 2 GB RAM / NAT / EFI |
| VMware Tools | 12.5.0（已安装，`vmrun` 客户端操作可用） |
| 状态 | 干净关机（`cleanShutdown = TRUE`），当前未运行 |
| 用途 | Win10 验收；P0 前建快照 `pre-printpal-20261028` |
| 注意 | 2 GB 内存只用于验证；不在 VM 内构建；通过共享文件夹/主机 HTTP 分发自包含包 |

### 2.1 Win10 IPP 驱动

`Get-PrinterDriver` 初始没有 `Microsoft IPP Class Driver`，但驱动 INF 已在 DriverStore：

```
C:\Windows\System32\DriverStore\FileRepository\prnms012.inf_amd64_0c1cc538f0358883\Amd64\MSIPP.xml
```

Win10 的 INF 用 `ExcludeFromSelect` 把它从“添加打印机 → 厂商/型号”里隐藏。
显式安装后即可建 IPP 队列：

```powershell
Add-PrinterDriver -Name 'Microsoft IPP Class Driver'
```

已实测通过（详见 `docs/spikes/ipp-capture.md` 3.6）。生产安装器必须在 Win10 上执行这一步。

### 2.2 共享文件夹

VMX 已增加共享文件夹（宿主机 `dist\p0-win10`，客户机名 `p0-win10`）：

```
\\vmware-host\Shared Folders\p0-win10
```

共享文件夹配置前的 VMX 备份：`C:\Virtual Machine\Win10\Win10.vmx.printpal-backup`。

**启动/快照命令（需要时）：**

```powershell
# 快照（关机状态）
& 'C:\Program Files (x86)\VMware\VMware Workstation\vmrun.exe' -T ws snapshot `
    'C:\Virtual Machine\Win10\Win10.vmx' pre-printpal

# 启动（GUI，便于登录）
& 'C:\Program Files (x86)\VMware\VMware Workstation\vmrun.exe' -T ws start `
    'C:\Virtual Machine\Win10\Win10.vmx'
```

---

## 3. 工作区

| 项 | 实测 |
|---|---|
| 工作目录 | `C:\Develop\code\AGI\PrintPal` |
| 初始内容 | 只有 `参考资料/`（只读，不修改） |
| 新代码 | `tools/`、`scripts/`、`docs/`、`artifacts/`（P0）；后续 `src/`、`tests/` |
| 文件权限 | 当前会话完全权限；此前 `workspace-write` 因 ACL 无法授权，不影响开发 |
| 网络 | NuGet 可访问；VM 使用 NAT |

---

## 4. 结论

**开发环境满足要求。**

- Win11 主机：.NET 10 SDK、Microsoft IPP Class Driver、WPS、无物理打印机 → 可完成 IPP 捕获 + FakePrinter 全链路验证。
- Win10 22H2 VM：可启动/快照；需在 VM 内登录后运行自包含 P0 包，验证 `Add-Printer` 兼容写法与 PWG/PDF 捕获。
- 唯一的外部依赖是 Win10 VM 的登录/执行；不需要真实打印机。

---

## 5. 证据与相关脚本

- `tools/PrintPal.IppProbe/`：IPP 诊断探针。
- `tools/PrintPal.PrintProbe/`：可控页数/份数/单双面/颜色/纸张的 GDI 打印工具。
- `scripts/p0/Reset-And-Install-P0Queue.ps1`：重启 Spooler + 删除/重建探针队列（P0 迭代用）。
- `scripts/p0/Test-WpsPrint.ps1`：WPS Writer COM 真实打印测试。
- `artifacts/p0/capture/`：IPP 请求、任务 JSON、保存的 PDF、日志。
