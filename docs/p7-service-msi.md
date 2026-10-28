# P7：Windows 服务 + MSI 安装包

> 日期：2026-10-28
> 版本：1.0.1
> 范围：P7.1 Windows 服务化、P7.2 MSI（unsigned）
> 证书：**未购买、未签名**；MSI 可正常安装，但下载分发时可能出现 SmartScreen “未知发布者”。

---

## 1. 已实现

### 1.1 Windows 服务

新增 `PrintPal.Service`（.NET Worker + `UseWindowsService`）：

```
Windows Spooler → IPP 127.0.0.1:8630 → PrintPal.Service
                                         ├─ IppServer
                                         ├─ PrintJobPipeline
                                         ├─ ForwardingWorker
                                         ├─ pending-job scanner
                                         └─ SQLite %ProgramData%\PrintPal
PrintPal.App（WPF）→ 读取/写入同一 SQLite → 显示/配置/人工转发
```

- 服务名：`PrintPal.Service`；LocalSystem；自动启动；
- 数据：`C:\ProgramData\PrintPal`；服务启动时给本机 `Users` 授 Modify；
- 关闭 UI 也能继续收任务；
- UI 探测 `/health`：服务在跑用 `ServiceAgentProxy`，否则便携模式；
- 服务扫描器每 2 秒处理 UI 写入的 `Priced` 任务；`ForwardingWorker` 去重。

### 1.2 MSI：合并运行时 + 安装向导

- WiX Toolset **5.0.2**；机器级安装；
- **App 和 Service 发布到同一 `app\` 目录**，共享一套 .NET/WPF/ASP.NET 运行时：
  - `PrintPal.App.exe`、`PrintPal.Service.exe` 都在 `C:\Program Files\PrintPal\`；
  - 不再有独立的 `service\` 目录 / 第二套 113 MB 运行库；
- 安装向导：**WixUI_InstallDir**
  - Welcome → License → Install Directory → Verify → Progress → Finish；
  - 用户可选择安装目录；
- 安装 Windows 服务、WSD IPP 队列、公共 + 当前用户快捷方式、数据目录；
- 卸载：删队列、停/删服务、删程序/快捷方式，保留数据。

产物（1.0.1）：

```
dist\PrintPal-1.0.1-win-x64\PrintPal-1.0.1-x64.msi   约 75.9 MB
dist\PrintPal-1.0.1-win-x64.zip                       约 164.8 MB
```

### 1.3 动态版本 / 动态路径

版本只在一个地方维护：

```xml
<!-- Directory.Build.props -->
<PrintPalVersion Condition="'$(PrintPalVersion)' == ''">1.0.1</PrintPalVersion>
```

- App/Service/所有类库统一使用 `$(PrintPalVersion)`；
- MSI 项目的 `OutputName / OutputPath / PublishDir / ProductVersion` 全部由
  `$(PrintPalVersion)` 生成；
- 发布脚本支持 `-Version` 覆盖：
  ```powershell
  .\scripts\publish\Publish-PrintPal.ps1 -Version 1.0.1 -NoFakePrinter
  ```
- 不再需要手工修改 MSI 里的 `0.6.x` 路径/版本。

### 1.4 升级 / 修复 / 回滚

- **升级**：固定 `UpgradeCode` + `MajorUpgrade`；`Schedule="afterInstallValidate"`；
  - 0.6.0 → 1.0.1 实测成功；
  - 服务、队列、程序更新，数据保留；
- **修复**：`msiexec /f`；
  - 安装后删除 `PrintPal.App.dll`，修复后恢复；
  - 服务/队列重新可用；
- **回滚**：
  - 安装流程增加 `RollbackQueue`（`Execute="rollback"`），失败时删除队列；
  - 用无效 `PRINTPAL_QUEUE_URL` 触发队列安装失败；
  - MSI 返回 1603，服务/程序/队列被回滚，`C:\ProgramData\PrintPal` 数据保留；
- 队列安装条件改为 `NOT REMOVE~="ALL"`，修复时也会重建队列。

### 1.5 队列必须是 WSD IPP 端口

- 队列 CA 以安装用户身份（`Impersonate="yes"`）运行；
- 清理所有 `PrintPal Virtual Printer*`（含 Windows 生成的“副本”）；
- HTTP 队列 + WSD 副本时，删除 HTTP 队列、保留并重命名 WSD 队列；
- 强制 WSD 端口，HTTP 端口会丢失 `copies/sides/color`；
- 支持 `PRINTPAL_QUEUE_URL` 属性（默认 `http://127.0.0.1:8630/ipp/print`），用于部署/回滚测试。

---

## 2. 构建

```powershell
# 一条命令：发布 app+service，构建 MSI，打 zip
.\scripts\publish\Publish-PrintPal.ps1 -NoFakePrinter -Zip

# 只发布，不构建 MSI
.\scripts\publish\Publish-PrintPal.ps1 -NoFakePrinter -NoMsi

# 指定版本
.\scripts\publish\Publish-PrintPal.ps1 -Version 1.0.1 -NoFakePrinter -Zip
```

---

## 3. 安装/卸载

```powershell
# 静默安装（管理员）
msiexec.exe /i .\dist\PrintPal-1.0.1-win-x64\PrintPal-1.0.1-x64.msi /qn /norestart

# 普通安装：显示 WixUI 向导
msiexec.exe /i .\dist\PrintPal-1.0.1-win-x64\PrintPal-1.0.1-x64.msi

# 修复
msiexec.exe /f .\dist\PrintPal-1.0.1-win-x64\PrintPal-1.0.1-x64.msi /qn

# 卸载
msiexec.exe /x .\dist\PrintPal-1.0.1-win-x64\PrintPal-1.0.1-x64.msi /qn /norestart
```

安装后：

- 服务：`Get-Service PrintPal.Service`；
- 队列：`Get-Printer -Name 'PrintPal Virtual Printer'`，端口应为 `WSD-...`；
- 健康：`Invoke-RestMethod http://127.0.0.1:8630/health`；
- 数据：`C:\ProgramData\PrintPal`；
- 快捷方式：公共开始菜单 + 公共桌面 + 当前用户桌面；目标指向实际安装目录（支持自定义路径）。

### 无证书

- MSI **未签名**（`Get-AuthenticodeSignature` 为 `NotSigned`）；
- 本机/内网安装正常；
- 浏览器/邮件分发可能显示“未知发布者”/SmartScreen；
- 不需要购买证书即可构建、安装、使用；签名是后续信任增强。

---

## 4. 验证结果（1.0.1）

| 项 | 结果 |
|---|---|
| 自动测试 | 98/98 |
| 升级 0.6.0 → 1.0.1 | ✅ 服务/队列/程序更新，数据保留 |
| 修复（删除 App DLL 后 `/f`） | ✅ DLL 恢复，服务/队列可用 |
| 回滚（无效队列 URL） | ✅ 1603；服务/程序/队列回滚，数据保留 |
| WixUI 向导 | ✅ Welcome/License/InstallDir/Verify/Progress/Exit |
| 服务模式打印 5p×2 彩色双面 | ✅ **¥6.40** → Fake-A4-Color，PDF 535,174 B |
| UI 服务模式 | ✅ `using service mode` |
| 桌面快捷方式 | ✅ 当前用户桌面 + 公共桌面；开始菜单 |
| MSI 签名 | ❌ `NotSigned`（预期） |

---

## 5. 已知限制

- MSI 无代码签名；SmartScreen/企业信任需证书；
- 未做 **升级失败后自动恢复旧版本** 的完整回归；本次回滚测试为“全新安装失败”；
- MSI 未做多用户/高并发安装、Service 崩溃恢复、SQLite 并发压测；
- 服务 LocalSystem + ProgramData Users Modify；多用户隐私需角色化；
- 首次 service DB 为空（价格表自带，打印机/路由需 UI 配置）；
- UI 为 WixUI_InstallDir + 简单 License；未做自定义品牌图；
- `Directory.Build.props` 是版本唯一来源；发布脚本/CI 必须传 `-p:PrintPalVersion` 或 `-Version`。

---

## 6. 相关文件

- `src/PrintPal.Service/`：Windows 服务
- `src/PrintPal.Agent/IPrintPalBackend.cs`、`ServiceAgentProxy.cs`、`PrintJobActions.cs`
- `src/PrintPal.App/App.xaml.cs`：服务探测/代理
- `Directory.Build.props`：版本唯一来源
- `packaging/PrintPal.Msi/`：WiX MSI（Package.wxs、License.rtf）
- `scripts/publish/Publish-PrintPal.ps1`：发布 + MSI + zip
- `scripts/install/PrintPal.MsiQueue.ps1`、`PrintPal.MsiQueueRemove.ps1`
- `docs/decisions/ADR-007-windows-service.md`
- `docs/decisions/ADR-008-msi-unsigned.md`


