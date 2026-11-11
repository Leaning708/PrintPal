# ADR-012：自定义配色（种子色派生，方案 A）

- 状态：**已确认并实施（2026-11-11，方案 A + 方案 B）**
- 日期：2026-11-11
- 关联：`docs/ui.md` §12（设置）、`ThemeManager` / `ThemePaletteBuilder`

## 1. 背景

界面的颜色已经全部走 token（`Theme.xaml` 的 Brush + `DynamicResource`），
`ThemeManager` 在运行时替换共享 Brush 即可切主题，因此"用户自定义配色"具备实现基础。
但直接开放全部 token 编辑会带来两个问题：

1. 用户容易配出低对比度、脏色、语义色错乱（例如把"失败"配成绿色）；
2. 需要维护 60+ 个 token 的设置 UI，复杂度和后续维护成本都高。

## 2. 决策

采用 **方案 A：种子色派生**：

- 用户只选 4 个"种子"：**明暗模式、主色（Accent）、页面底色风格、侧栏风格**；
- 其余 token（文字/线条/侧栏/卡片/chip/极简/语义色…）由 `ThemePaletteBuilder` 按固定公式派生；
- 主色过亮/过暗时自动微调；主按钮文字按对比度自动选白/黑（`AccentForegroundBrush`）；
- 预设 10 套主题保持不变（后续可改为"预设种子"）；
- 持久化：`Settings.uiTheme = custom` + `Settings.uiThemeCustom`（JSON，带 version）；
- 预览：打开对话框时应用派生主题，取消/关闭还原打开前的主题。

## 3. 理由

- 派生公式保证基本的可读性与一致性（对比度单测兜底），用户不可能把界面调"烂"；
- 实现量可控：一个派生器 + 一个对话框，不需要 60 个取色器；
- 与现有 token 体系天然兼容：仍然是"替换同名 Brush"，不改任何控件模板。

## 4. 后果

- 方案 B 已补上：高级模式可逐 token 覆盖（`ThemeCustomization.Overrides`，JSON 持久化，版本 2），
  支持取色器/HEX/单行还原/一键清空、对比度提示、配色 JSON 导入导出；语义色仍可覆盖但不再强制固定；
- 自由度提高后，低对比度组合需要用户自己负责——界面给出对比度提示但不阻止保存；
- 托盘菜单是 WinForms 自绘，暂未接入自定义 token（已知限制，后续补齐）；
- 深色主题下主按钮文字改为按对比度自动选色，内置深色主题也随之改善（原先白字在浅主色上对比度不足）。

## 5. 备选方案（未采用）

- **方案 B：全 token 编辑 + 导入/导出**：自由度最高，已作为第二轮迭代补充实现；
- **不提供自定义，只加预设**：无法满足门店品牌色需求。

## 6. 方案 B 补充（2026-11-11）

- `ThemeCustomization.Overrides`（`Dictionary<token, hex>`）在派生完成后逐项覆盖，版本升到 2；
- 高级模式 UI 按 8 个分组列出全部 60+ token：取色器（WinForms ColorDialog）、HEX 输入、单行还原、
  清空覆盖；改动实时预览；
- 侧栏渐变起止（`SidebarTop/SidebarBottom`）也可单独改；
- 导出为 JSON 文件供其他机器导入；导入失败给出提示且不影响当前主题；
- 对比度提示：主文字/页面 ≥ 4.5、次要文字/卡片 ≥ 4.5、弱化文字/卡片 ≥ 3、主按钮文字/主色 ≥ 3，低于阈值显示警告；
- 尚未做：从 `themes\*.json` 目录自动发现主题文件（导入即用已覆盖主要场景）。
## 7. 多自定义主题与视觉统一（2026-11-11 第三轮）

- **主题库**：`ThemeLibrary` 保存 `List<ThemeCustomization>` + `SelectedId`，整库存 `Settings.uiCustomThemes`；
  旧版单条 `uiThemeCustom` 迁移为一条名为「自定义」的主题；
- 每个主题有 `Id`（GUID）；设置页「自定义」分类与浅色/深色并列，磁贴按 Id 切换，支持新建（种子=当前主题）、
  编辑（含改名）、删除（二次确认）；
- 删除当前主题时回到主题库里的下一个，库空了回落到默认预设；
- 配色对话框去系统标题栏（`WindowStyle=None` + 自绘），提示框统一走 `MessageDialog`，
  颜色选择用自绘 `ColorPickerWindow`，避免系统弹窗破坏风格；
- 托盘菜单（WinForms 自绘）的 Surface/Hover/Border/Text/Accent/Disabled 从主题 token 派生，
  在主题切换后通过 `RefreshTrayMenuColors()` 刷新并 `Invalidate`。