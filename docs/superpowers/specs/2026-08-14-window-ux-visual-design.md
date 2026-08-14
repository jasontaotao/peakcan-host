# Phase 2 — 视觉：浅色工程风 · 令牌统一

> 日期：2026-08-14 · 分支：`window-ux-phase0` · 前置：[2026-08-14-window-ux-design.md](./2026-08-14-window-ux-design.md)（Phase 0/1 已 ship）
> Mockup：[../mockups/2026-08-14-window-ux-visual-mockup.html](../mockups/2026-08-14-window-ux-visual-mockup.html)（2026-08-14 user 已认可配色）

## 1. 目标

给 PeakCan.Host 建立**统一的浅色工程视觉体系**，三件事：

1. **语义令牌**：把散落在 15 个 XAML 的硬编码色（hex + 命名色）抽到 `Themes/Colors.xaml`（语义命名 SolidColorBrush），全应用替换，消灭裸 hex。
2. **图标**：工具栏/菜单/窗口/HIL 模式图标从 emoji 换成 Windows 11 自带 **Segoe Fluent Icons** 单色字形（颜色跟随文本/主题）。
3. **布局持久化**：AppShell 的 splitter 位置 / 右栏宽 / 主右 tab 选中项持久化到 `%APPDATA%/PeakCan.Host/layout.json`，重启还原。

**基调**：浅色工程专业工具（对标 PCAN-View / CANalyzer），强调色 **#0B5CAD**，脚本编辑器（WebView2）保持深色（代码编辑器惯例），输出面板改浅色控制台。

## 2. 非目标

- **不做双主题 / 深色主题**：单浅色主题，令牌用 `StaticResource`（若将来加深色再迁 `DynamicResource`）。
- **不重排布局结构**：网格/间距/字体大小层级不变——本轮只统一颜色体系 + 图标 + 布局持久化。
- **不改窗口几何持久化**：已由 `WindowStateStore`（P0-1）承担；本轮只做 AppShell **内**布局。
- **不动 WebView2 脚本编辑器内部**（深色保留）；不做 AvalonDock 完整 docking。
- **不改 Core / Infrastructure 接口契约**（NetArchTest）；可新增 App 层类型。

## 3. 现状（已验证）

- `App.xaml` 仅注册 converter，**无任何颜色/样式字典**；无 `Themes/` 目录。
- 硬编码色散落 **15 个 XAML**（含 P1-2 新增的 `ConnectionSettingsWindow`），**hex 与命名色（`Gray`/`Red`/`Green`/`Blue`/`White`/`Transparent` 等）混杂**。主要 hex：`#1E1E1E` ×5 = 4 处用途（脚本编辑器底[保留]/脚本输出面板底/UDS 输出日志底/MultiFrame RowDetails 底）、`#F8F8F8/#FAFAFA/#F4F4F4/#F0F0F0/#EEEEEE`（表头/次级底）、`#CCCCCC`（边框）、`#FFF8E1/#D4A72C/#7D4E00`（限流 chip）、`#1A7F37`（已连接绿）、`#D62728`（错误红）、`#1565C0/#0066CC`（信息蓝）、`#6e7781`（灰文字）、`#D4D4D4`（深色底上的浅字）。
- **帧状态行底色（要令牌化，不是数据色）**：TraceView 用 `DataTrigger` 按帧属性给行上背景色——`IsError → #FFCDD2`、`IsFd → #E3F2FD`、`IsHighlighted → #FFFDE7`。
- **真正保留的数据色**：TraceViewerView 的**图表锚点/系列色**（命名色 `Blue`/`Green`/`Red`/`White`，比较锚点 + 图例）与 ChatPanel 的**消息类型 chip 色**（`#DCF8C6` 等）——这些是数据驱动，不入令牌表。
- emoji 图标分布：`✕ ×6`、`● ×5`、`→ ×4`、`▶ ×4`、`▼ ×4`、`🔍 ×4`、`⚙ ×3`、`⏹ ×3`、`💾 ×3`、`📂 ×2`、`▲ ×2`、`🔗 ×2`、`◀ ⏸ 🤖 ⚡ ✨ ←` 各 1；HIL 模式图标在 `HilModeToIconConverter.cs`（C# 返回 emoji 字符串 `📼🔌💻🔗❓`）。
- 持久化先例：`RecentSessionsService`/`WindowStateStore`（`%APPDATA%/PeakCan.Host/*.json`，`{version,...}` + 原子 tmp+rename + 损坏容错 + `MaxLoadFileBytes` 上限）。

## 4. 设计决策

### D1 单浅色主题 + StaticResource

不做双主题。令牌一律 `StaticResource`（编译期解析、更快）；`Colors.xaml` 合并进 `App.xaml` 的 `Application.Resources`。将来若加深色主题，`StaticResource → DynamicResource` 是机械替换。

### D2 语义令牌（Colors.xaml）

只允许语义名（`CanvasBg`/`Surface`/`TextPrimary`/`Accent`…），XAML 禁止裸 hex/命名色。15 个 XAML 的硬编码色按 §7 映射表机械替换。命名分组：`表面/边框/文本/强调/状态/控制台`。

### D3 图标 = Segoe Fluent Icons 单色字形

用 Windows 11 自带 **Segoe Fluent Icons** 字体，单色字形，`Foreground` 随文本/主题。emoji → Fluent 字形按 §6 映射。**字形码点（codepoint）在 P2-2 用 `GlyphTypeface` 从已安装字体解析并验证**（不在 spec 写死，防机型差异）。

### D4 输出/日志控制台改浅色

脚本视图底部**输出面板** + UDS 窗口底部**输出日志**（`#1E1E1E/#D4D4D4`）→ `ConsoleBg #FBFBFC / ConsoleFg #24292F`，等宽字；语义色输出（发送/通过/错误）用 `ConsoleAccent/Ok/Error`。**WebView2 脚本编辑器保持深色不变**；MultiFrame 的 RowDetails 表单面板（`#1E1E1E`）改浅色 `SurfaceSubtle`（它是表单，非代码编辑器）。

### D5 轻量布局持久化（LayoutStateStore）

新增 `LayoutStateStore`（沿用 `WindowStateStore` 模式），持久化 AppShell：主↔右面板 splitter 列宽、右栏宽度、`SelectedMainTabIndex`、`SelectedRightTabIndex`。AppShell `SourceInitialized` 恢复 + `Closing` 保存（与 AppShell 几何持久化一致）。

## 5. 令牌定义（Themes/Colors.xaml 完整清单）

均为 `SolidColorBrush`（`x:Key` 见下表）。`Font*` 为 `FontFamily` 资源。

| 组 | Key | 值 | 用途 |
|---|---|---|---|
| 表面 | `CanvasBg` | #F3F4F6 | 窗口/工作区底 |
| 表面 | `Surface` | #FFFFFF | 面板/卡片/表格底 |
| 表面 | `SurfaceSubtle` | #F7F8FA | 表头/次级底 |
| 表面 | `RowAlternate` | #F7F8FA | 表格斑马行 |
| 表面 | `RowHover` | #EDF2F9 | 行悬停（蓝调） |
| 表面 | `RowSelected` | #DCECFB | 行选中（强调色调） |
| 边框 | `Border` | #D4D9DF | 控件/面板边框 |
| 边框 | `BorderSubtle` | #E4E8EC | 输入框/轻边框 |
| 边框 | `Divider` | #E9EDF1 | 表格行分隔线 |
| 文本 | `TextPrimary` | #1B1F24 | 主文本 |
| 文本 | `TextSecondary` | #5A6470 | 次要/表头/说明 |
| 文本 | `TextDisabled` | #9AA3AE | 禁用态 |
| 文本 | `TextOnAccent` | #FFFFFF | 强调底上的文字 |
| 强调 | `Accent` | #0B5CAD | 选中 tab/按钮/链接/信息 |
| 强调 | `AccentHover` | #094B8F | 悬停 |
| 强调 | `AccentPressed` | #073D75 | 按下 |
| 状态 | `Ok` | #1A7F37 | 已连接/成功 |
| 状态 | `OkBg` | #E6F4EA | 成功 chip 底 |
| 状态 | `WarnText` | #8A5B00 | 警告文字 |
| 状态 | `WarnBg` | #FFF6E0 | 警告 chip 底 |
| 状态 | `WarnBorder` | #E3B64B | 警告 chip 边 |
| 状态 | `Error` | #D62728 | 错误/停止 |
| 状态 | `ErrorBg` | #FDEBEC | 错误 chip 底 |
| 状态 | `Info` | = Accent #0B5CAD | 信息/链接/FD 标识 |
| 状态 | `FrameBgFd` | #E3F2FD | 追踪 FD 帧行底 |
| 状态 | `FrameBgError` | #FFCDD2 | 追踪错误帧行底 |
| 状态 | `FrameBgHighlight` | #FFFDE7 | 追踪高亮行底 |
| 控制台 | `ConsoleBg` | #FBFBFC | 浅色输出面板底 |
| 控制台 | `ConsoleFg` | #24292F | 控制台正文 |
| 控制台 | `ConsoleAccent` | #0550AE | 控制台高亮/发送 |
| 排版 | `FontMono` | Consolas | ID/hex/DLC/时间列 |
| 排版 | `FontUI` | Segoe UI | 界面正文 |

**保留的数据色（不入令牌表）**：TraceViewerView 图表锚点/系列色（`Blue`/`Green`/`Red`/`White`）、ChatPanel 消息类型 chip 色（`#DCF8C6` 等）、以及 `Transparent`。**帧状态行底色**（`FrameBgFd/Error/Highlight`）已入令牌表（§5），不得再裸写。

## 6. 图标映射（emoji → Segoe Fluent Icons）

单色字形，`Foreground` 继承文本/主题。映射表（意图级；**码点在 P2-2 从已安装字体解析验证**）：

| 现状 | 意图/位置 | 目标 Fluent 字形（近似） |
|---|---|---|
| ⚙ 设备设置 | AppShell 工具栏 | Settings |
| ● 录制 | AppShell 工具栏 Toggle | Record / Recording |
| ▶ 运行 | Script/Replay/Trace 工具栏 | Play |
| ⏹ 停止 | Script/Trace/MultiFrame | Stop |
| ⏸ 暂停 | Trace 工具栏 | Pause |
| 📂 打开 | Script/EcuScriptEditor | OpenFolder |
| 💾 保存 | Script/EcuScriptEditor/MultiFrame | Save |
| ✕ 关闭/清除 | TraceViewer/ChatPanel | Dismiss |
| 🔍 搜索 | ChatPanel | Search |
| ▲ 上移 / ▼ 下移 | MultiFrame/Uds | ArrowUp / ArrowDown |
| → ← | 分页/前进后退 | ChevronRight / ChevronLeft |
| 🔗 | UDS 窗口 | Link |
| 🤖 | AI 聊天 | Bot |
| ⚡ | ChatPanel | Flash |
| ✨ | EcuScriptEditor | Sparkle |
| HIL 模式 📼🔌💻🔗❓ | `HilModeToIconConverter.cs` | Replay/Plug/Laptop/Link/Help |

**HIL 模式图标是转换器 C# 返回的字符串**——改成返回 Segoe Fluent codepoint 字符串，ComboBox ItemTemplate 里套 `FontFamily="Segoe Fluent Icons"` 的 TextBlock。若某意图（如 📼 磁带）无对应字形，P2-2 选最接近字形并在 spec 决策记录里注明。

## 7. 硬编码色 → 令牌映射（机械替换清单）

| 现硬编码 | 出现处 | → 令牌 |
|---|---|---|
| #F8F8F8 / #FAFAFA / #F4F4F4 / #F0F0F0 / #EEEEEE | 表格斑马/表头/次级底（Signal/Trace/Dbc/ChatPanel 等） | `RowAlternate` / `SurfaceSubtle` |
| #CCCCCC | 边框/splitter/统计卡边（Signal/MultiFrame/EcuScriptEditor） | `Border` / `BorderSubtle` |
| #1E1E1E（输出面板底）+ #D4D4D4（浅字） | ScriptView 输出 / UdsWindow 输出日志 | `ConsoleBg` / `ConsoleFg` |
| #1E1E1E（WebView2 编辑器底） | ScriptView 编辑器 | **保留**（D4） |
| #1E1E1E（RowDetails 表单面板底）+ #444 边 | MultiFrame 行详情 | `SurfaceSubtle` / `Border` |
| #FFF8E1 / #D4A72C / #7D4E00 | 限流 chip（Send/MultiFrame） | `WarnBg` / `WarnBorder` / `WarnText` |
| #1A7F37 | 已连接（AppShell） | `Ok`（值不变） |
| #D62728 | 错误（TraceViewerView） | `Error`（值不变） |
| `Red` / `DarkRed` | 错误文本（Send/Replay/Hil/EcuScriptEditor）、编辑器错误 | `Error` |
| #1565C0 / #0066CC | 信息/链接/选中（TraceViewerView/ChatPanel） | `Accent` / `Info` |
| #6e7781 | 灰文字（TraceView 计数等） | `TextSecondary` |
| `Gray` / `DarkGray` | 注释列/次级文字/边框（Dbc/Hil/Script/Uds/DbcTreePicker/ConnectionSettings/…） | `TextSecondary` / `Border`（按用途） |
| #E3F2FD | TraceView `IsFd` 行底 | `FrameBgFd` |
| #FFCDD2 | TraceView `IsError` 行底 | `FrameBgError` |
| #FFFDE7 | TraceView `IsHighlighted` 行底 | `FrameBgHighlight` |
| `White` / `Transparent` | 表头/透明底 | 保留（`Surface`/`Transparent`） |
| `Blue` / `Green` / `Red`（图表锚点） | TraceViewerView 比较锚点/图例 | **保留（数据色）** |
| #DCF8C6 等 | ChatPanel 消息类型 chip | **保留（数据色）** |

## 8. 实施清单

### Phase 2 — 视觉（P2）

- **P2-1 令牌字典**：建 `Themes/Colors.xaml`（§5 全表），`App.xaml` 合并进 `Application.Resources`；新增令牌存在性测试（每个视图引用的 `{StaticResource}` 令牌都定义于 Colors.xaml）。
- **P2-2 图标**：Segoe Fluent Icons 字形解析表（`GlyphTypeface` 解析码点）+ 映射测试（映射表完整性、码点可解析）；HIL 模式转换器改返回 codepoint。
- **P2-3 AppShell 镀铬**：菜单/工具栏/状态栏/tab/双 TabControl/splitter 换令牌（含按钮 `Accent`、`AccentHover`/`Pressed`、连接态 `Ok`、录制 `Error`）。
- **P2-4 视图换令牌**：按 §7 映射替换 §3 所列 15 个含硬编码色的 XAML（Trace/Dbc/Send/Signal/Script/Replay/Hil + TraceViewerView/ChatPanel + 窗口 Uds/MultiFrame/EcuScriptEditor/DbcTreePicker/ConnectionSettings）。
- **P2-5 浅色控制台**：ScriptView 输出面板 → `ConsoleBg/ConsoleFg/ConsoleAccent` + 语义色输出（Ok/Error）。
- **P2-6 布局持久化**：`LayoutStateStore`（schema `layout/v1`：splitter 列宽/右栏宽/`SelectedMainTabIndex`/`SelectedRightTabIndex`；原子写 + 损坏容错 + `MaxLoadFileBytes`）+ `AppShellViewModel` 暴露属性 + AppShell 启动恢复/关闭保存；单测（round-trip/原子/容错，镜像 `WindowStateStoreTests`）+ AppShell 布局恢复 STA 测试。

**依赖**：P2-1 是 P2-3/4/5 的前置（令牌必须先存在）；P2-2 独立；P2-6 独立于 P2-3/4（但 AppShell 布局字段与 P2-3 的 tab 属性同处，建议 P2-6 在 P2-3 后做避免冲突）。

## 9. 验收标准

- [ ] 15 个 XAML 无裸 hex/命名色（§5 保留的数据色除外）；全部引用 Colors.xaml 语义令牌
- [ ] 界面仍为浅色工程观感，主窗口/表格/状态芯片/语义色整体协调（对照 mockup）
- [ ] 工具栏/菜单/HIL 模式图标为单色 Fluent 字形，随主题着色
- [ ] 输出面板为浅色控制台；WebView2 脚本编辑器保持深色
- [ ] 重启后 AppShell 的 splitter 位置/右栏宽/选中 tab 还原
- [ ] 测试：App.Tests 全绿；令牌存在性 / 图标映射 / LayoutStateStore / AppShell 布局恢复测试通过

## 10. 决策记录

**已拍板（2026-08-14 user）**：方向 = 浅色工程风·令牌统一；图标 = Segoe Fluent 字形；布局持久化 = 轻量（LayoutStateStore）；输出面板 = 浅色控制台（WebView2 编辑器保留深色）；强调色 = 工程深蓝 **#0B5CAD**。Mockup `2026-08-14-window-ux-visual-mockup.html` 已认可。

**开放项**：HIL 📼 磁带无对应 Fluent 字形 → P2-2 选最接近（如 Replay/Video）并记录。
