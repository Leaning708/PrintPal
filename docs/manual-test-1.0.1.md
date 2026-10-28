# PrintPal 1.0.1 人工测试指南

> 适用：Windows 10 22H2+ / Windows 11 x64
> 安装包：1.0.1（unsigned MSI）
> 目的：编译打包 → 安装 → 用虚拟打印机捕获 → 看到金额 → 转发到 FakePrinter/真实打印机

---

## 1. 编译和打包

在仓库根目录打开 **管理员 PowerShell**：

```powershell
cd C:\Develop\code\AGI\PrintPal

# 推荐：一条命令完成发布 app+service、构建 MSI、打 zip
.\scripts\publish\Publish-PrintPal.ps1 -NoFakePrinter -Zip
```

输出：

```text
dist\PrintPal-1.0.1-win-x64\
  PrintPal-1.0.1-x64.msi       ← 安装包，约 75.9 MB
  app\                         ← App + Service 共享运行时
  installer\                   ← MSI 队列/快捷方式脚本
  docs\
PrintPal-1.0.1-win-x64.zip    ← 约 164.8 MB
```

如果还要测试 FakePrinter，去掉 `-NoFakePrinter`：

```powershell
.\scripts\publish\Publish-PrintPal.ps1 -Zip
```

这样会额外生成 `fake-printer\`。

指定版本：

```powershell
.\scripts\publish\Publish-PrintPal.ps1 -Version 1.0.1 -NoFakePrinter -Zip
```

只构建 MSI（已发布过 app/service 时）：

```powershell
dotnet build .\packaging\PrintPal.Msi\PrintPal.Msi.wixproj -c Release -p:PrintPalVersion=1.0.1
```

> MSI 未签名。双击/安装时 SmartScreen 可能提示“未知发布者”，选择“仍要运行”即可。

---

## 2. 安装

### 2.1 图形向导（推荐人工测试）

双击：

```text
dist\PrintPal-1.0.1-win-x64\PrintPal-1.0.1-x64.msi
```

向导页面：

```text
Welcome → License → InstallDir → Verify → Progress → Finish
```

可以修改安装目录；默认：

```text
C:\Program Files\PrintPal
```

### 2.2 静默安装

```powershell
msiexec.exe /i .\dist\PrintPal-1.0.1-win-x64\PrintPal-1.0.1-x64.msi /qn /norestart
```

### 2.3 安装后检查

```powershell
Get-Service PrintPal.Service
Invoke-RestMethod http://127.0.0.1:8630/health
Get-Printer -Name 'PrintPal Virtual Printer' | Format-List Name,PortName,PrinterStatus
```

期望：

- 服务 `Running`、`Automatic`；
- `/health` 返回 `status: ok`；
- 队列 `PrintPal Virtual Printer`，**PortName 以 `WSD-` 开头**；
- 桌面/开始菜单有 PrintPal 快捷方式。

如果 PortName 是 `http://...`，说明队列走错端口，不要继续计价测试；重新安装或执行队列安装脚本（见 §6）。

安装路径：

| 项 | 路径 |
|---|---|
| 程序/服务 | `C:\Program Files\PrintPal` |
| 数据 | `C:\ProgramData\PrintPal` |
| 数据库 | `C:\ProgramData\PrintPal\printpal.db` |
| 服务日志 | `C:\ProgramData\PrintPal\logs\service.log` |
| MSI 队列日志 | `C:\ProgramData\PrintPal\msi-queue.log` |
| UI 日志（service mode） | `%LOCALAPPDATA%\PrintPal\logs\app.log` |

---

## 3. 虚拟打印服务使用方式

安装后 **不需要手工启动服务**：

```text
Windows Spooler
  → PrintPal Virtual Printer（WSD IPP）
  → http://127.0.0.1:8630/ipp/print
  → PrintPal.Service
  → SQLite + 捕获/计价/路由/转发
```

- 服务名：`PrintPal.Service`；
- 自动启动，LocalSystem；
- 只监听 `127.0.0.1:8630`；
- 关闭 UI 后服务继续收任务；
- UI 从开始菜单/桌面启动，会自动进入 **service mode**。

手工检查/控制服务：

```powershell
Get-Service PrintPal.Service
Start-Service PrintPal.Service
Stop-Service PrintPal.Service
Restart-Service PrintPal.Service
```

> 不需要运行 `PrintPal.AgentHost`；它只是无界面调试宿主。服务版直接由 Windows 服务托管。

---

## 4. 准备测试目标打印机

### 方案 A：FakePrinter（没有真实打印机时）

```powershell
cd C:\Develop\code\AGI\PrintPal
.\scripts\Start-FakePrinters.ps1 -Output .\artifacts\manual\fake
```

会启动：

| 名称 | 协议 | 地址 |
|---|---|---|
| Fake-A4-Color | IPP | `http://127.0.0.1:8631/ipp/print` |
| Fake-A4-Mono | IPP | `http://127.0.0.1:8632/ipp/print` |
| Fake-A3 | IPP | `http://127.0.0.1:8633/ipp/print` |

收到的 PDF 在：

```text
artifacts\manual\fake\received\
```

停止：

```powershell
.\scripts\Stop-FakePrinters.ps1
```

### 方案 B：UI 里手工添加目标打印机

打开 PrintPal：

- “打印机”页 → 新增；
- 名称、协议（IPP/Raw 9100）、主机、端口、资源路径；
- FakePrinter 用上面的地址；
- 保存后启用。

### 方案 C：批量写入 3 台 FakePrinter + 路由（手动测试快捷方式）

先确保服务在跑、FakePrinter 在跑，然后：

```powershell
cd C:\Develop\code\AGI\PrintPal

dotnet run --project .\src\PrintPal.AgentHost -- `
  --data "$env:ProgramData\PrintPal" `
  --seed-demo `
  --port 8641 `
  --no-auto-forward
```

看到 `http://127.0.0.1:8641/health` OK 后按 `Ctrl+C`。

- 8641 只是临时健康端口，不会和服务的 8630 冲突；
- 只会在数据库没有打印机时写 3 台 FakePrinter + 默认路由；
- 之后服务/UI 会读取同一份 ProgramData 数据库。

> 也可以完全在 UI 手工配置，不运行 AgentHost。

---

## 5. 打印测试

### 5.1 用 PrintProbe（推荐，最可控）

构建：

```powershell
dotnet build .\tools\PrintPal.PrintProbe\PrintPal.PrintProbe.csproj -c Release
```

运行：

```powershell
.\tools\PrintPal.PrintProbe\bin\Release\net10.0-windows\PrintPal.PrintProbe.exe `
  --printer "PrintPal Virtual Printer" `
  --pages 5 `
  --copies 2 `
  --duplex long `
  --color color `
  --paper A4 `
  --name "manual 5p x2 color duplex"
```

期望：

- PrintPal 任务列表新增一条；
- 5 页 × 2 份、A4、彩色、双面；
- 默认价格：A4 彩色双面 1.20/张、单面 0.80/张；
- 每份：2 张双面 × 1.20 + 1 张单面 × 0.80 = 3.20；
- 2 份：**¥6.40**；
- 路由到 Fake-A4-Color；
- FakePrinter 收到 PDF，约 535 KB、5 页，job-name 带 `[PrintPal:<id>]`。

### 5.2 用 WPS / Office / 记事本

1. 打开文档；
2. 打印；
3. 打印机选择 `PrintPal Virtual Printer`；
4. 设置份数/单双面/彩色；
5. 打印；
6. 回 PrintPal → “打印任务”查看记录和金额。

WPS 可能把“份数”合并成页数（由驱动决定）；以任务列表捕获的 **页数/份数** 为准。

### 5.3 自动/手动转发

在“设置”页：

- 自动转发开启：捕获后直接进入转发；
- 自动转发关闭：任务为 **Held**，在任务列表点“转发”或“选择目标”。

---

## 6. 常见检查 / 修复

### 队列端口不是 WSD

```powershell
Get-Printer -Name 'PrintPal Virtual Printer' | Format-List Name,PortName
```

必须是：

```text
WSD-xxxxxxxx-....
```

如果是 `http://127.0.0.1:8630/ipp/print`：

```powershell
# 管理员 PowerShell；服务必须先跑起来
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\install\PrintPal.MsiQueue.ps1
```

或者重新安装 MSI。

### 查看服务日志

```powershell
Get-Content "$env:ProgramData\PrintPal\logs\service.log" -Tail 50
Get-Content "$env:ProgramData\PrintPal\msi-queue.log" -Tail 50
```

### 健康检查

```powershell
Invoke-RestMethod http://127.0.0.1:8630/health | ConvertTo-Json
```

### MSI 修复

```powershell
msiexec.exe /f .\dist\PrintPal-1.0.1-win-x64\PrintPal-1.0.1-x64.msi /qn
```

修复会保留数据；队列已经是 WSD 时会跳过重建。

---

## 7. 卸载

图形：

```text
设置 → 应用 → 已安装的应用 → PrintPal → 卸载
```

或：

```powershell
msiexec.exe /x .\dist\PrintPal-1.0.1-win-x64\PrintPal-1.0.1-x64.msi /qn /norestart
```

卸载删除服务/队列/程序/快捷方式，**保留** `C:\ProgramData\PrintPal`。

彻底清理测试数据（谨慎）：

```powershell
Remove-Item "$env:ProgramData\PrintPal" -Recurse -Force
Remove-Item "$env:LOCALAPPDATA\PrintPal" -Recurse -Force
```

---

## 8. 人工测试记录建议

| 项 | 期望 | 实际 |
|---|---|---|
| 服务 Running | ✅ | |
| 队列 WSD | ✅ | |
| 桌面/开始菜单快捷方式 | ✅ | |
| PrintProbe 5p×2 彩色双面 | ¥6.40 | |
| FakePrinter PDF | 收到，5 页 | |
| WPS/Office 打印 | 有记录 | |
| 关闭 UI 后打印 | 服务仍收任务 | |
| 自动转发关闭 → Held → 手工转发 | 成功 | |
| Repair | 成功，数据/快捷方式保留 | |
| 卸载 | 服务/队列/程序移除，数据保留 | |
