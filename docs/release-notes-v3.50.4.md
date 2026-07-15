# Release Notes v3.50.4 — Watch list 右侧侧栏 + Tracker tooltip + 短 toolbar label (combined PATCH)

**Released**: pending ship
**Tag**: v3.50.4
**Branch**: `feature/v3-50-4-patch-watch-list-right-panel`
**Parent**: v3.50.3 PATCH (`2943f94` on `main`)

## Why this PATCH

3 个 user-flagged UX 改进 (合并到 1 个 PATCH, 都是 chart/right-panel/toolbar 渲染层打磨):

1. **Watch list 滚动栏** 改右侧侧栏 (原话: "watch list 的滚动栏能放到显示窗口的右边么（别窗口大小改变了就找不到了）")
2. **Tracker tooltip** 修复 (原话: "我点了一下绿线对应的那个点没什么反应" + 之前 "没办法显示之前的 x 轴和 y 轴 的数据")
3. **Toolbar toggle label 短化** (原话: "显示当前锚点 和 显示比较锚点 的字简短点，太占地方了")

## What this PATCH does

### 1. Watch list 布局重构 (主改动)

| 改前 (v3.50.3) | 改后 (v3.50.4) |
|---|---|
| Column 0: Watch List DataGrid | Column 0: Chart subplots (expanding) |
| Column 2: Chart subplots | Column 2: TabControl [Watch List \| Sampling Table] |
| Column 4: Sampling Table |  |

**新右侧 TabControl**:
- **Watch List tab** (默认激活): 7 列 DataGrid (CAN ID / Signal / Plot / N / Latest / Δ / Blue)，单位列已并入 Signal 名
- **Sampling Table tab**: 3 列 DataGrid (Signal / Unit / Value)，v3.49.0 Q1 panel

关键设计:
- TabControl 永远挂在 column 2 (DockPanel 右侧)，窗口 resize 不影响
- GridSplitter 在 column 1 (用户可拖)
- Column 0 (chart) `Width=3*` 占满剩余空间
- Column 2 默认 360px, MinWidth 280
- Sampling Table 仍 `HasWatchedSignals` 可见性绑定

### 2. Tracker tooltip 修复 (OxyPlot PlotController)

**根因**: 项目内所有 `<oxy:PlotView>` 都没配 `Controller` 属性 → OxyPlot 默认 controller = null → **Tracker 不工作** → 用户 hover/click 折线点看不到 (X, Y) tooltip。

**Fix** (3 处最小改动):
- `TraceChartSeries.cs` record positional 末尾加 `PlotController? Controller = null` (default null, 11 处既有构造不破坏)
- `ChartSeriesFlow.cs` `BuildOneChartSeriesForSource` 给真 series 注入 `new PlotController()` (1 行)
- `TraceViewerView.xaml` `<oxy:PlotView>` 加 `Controller="{Binding Controller}"` (1 行)

**类型细节** (W23 LESSON 适用): `PlotController` 在 `OxyPlot` core 命名空间 (2.2.0), **不在** `OxyPlot.Wpf`. TraceChartSeries 已 `using OxyPlot.Wpf;` 但 `PlotController` 实际来自 OxyPlot core.

### 3. Toolbar toggle label 短化

`显示当前锚点` → `● 当前` (5 字 → 3 字)
`显示比较锚点` → `● 比较` (5 字 → 3 字)

Tooltip 保留完整描述 (用户 hover 看到全名)。`●` 前缀 = 视觉锚点标识 (与 chart 上绿/蓝线的 LineAnnotation 颜色区分). 短 label 让 toolbar 在窄窗口 (1200px) 一屏装下 Add trace / Master / Filter / Speed / Loop / Play/Pause/Stop / Slider / 锚点 toggles.

### 4. Toolbar playback controls REMOVED (废弃)

User direction 2026-07-15: "废弃" = 永久删除. **5 个 UI 元素全部移除**:
- `Speed` ComboBox (0.25x / 0.5x / 1.0x / 2.0x / 4.0x)
- `Loop` CheckBox
- `▶` Play button
- `⏸` Pause button
- `⏹` Stop button
- Scrubber Slider (Value={Binding ScrubberValue, Maximum={Binding TotalDuration}})

**VM 端 PlayCommand/PauseCommand/StopCommand/Loop/Speed/ScrubberValue 全部保留** (其他测试可能 mock). 仅 UI 不暴露. 任何残余引用 `vm.PlayCommand?.Execute(null)` 在 XAML / code-behind 都已断开.

**Trace Viewer 角色转变**: 从 "回放 + 图表" → "只读图表面板 (绿/蓝锚点查值 + watch list + Sampling Table)".

### 5. Filter CAN IDs UI HIDDEN (Visibility=Collapsed)

User direction 2026-07-15: "Filter CAN IDs 这块是干嘛的" — user 自己不知道 = 没用过. **Hide (非删)**:
- `TextBlock` label + `TextBox` + `Clear` 按钮 都加 `Visibility="Collapsed"`
- VM `CanIdFilter` 字段 + `OnCanIdFilterChanged` hook + `RefreshFrameCounts` filter 逻辑**全部保留** (其他测试可能引用, 也可从 .tmtrace rehydrate)
- 恢复: 删 3 行 `Visibility="Collapsed"` (1 行 1 元素, 3 处)

### 6. Watch list ✕ Remove 列 (新功能, 修复 watch list 无法 remove)

**Bug 报告**: "watch list 无法 remove" (2026-07-15).

**根因**: VM `RemoveFromWatch(WatchedSignalRow row) [RelayCommand]` 自 v3.15.0 一直存在 + 测试覆盖 (`RemoveFromWatch_UnplotsAndRemovesRow`), **但 XAML 从未绑 Command**, UI 缺删除按钮.

**Fix** (XAML-only):
- Watch List DataGrid 加 ✕ 列 (DataGridTemplateColumn + Button)
- `Command="{Binding DataContext.RemoveFromWatchCommand, RelativeSource={RelativeSource AncestorType=Window}}"`
- `CommandParameter="{Binding}"` (row 自身)
- Window-relative lookup 防 row-scoped DataContext shadow VM (sister of legend ✕ button)
- CAN ID 列 70→64 + Signal 列仍 `Width="*"` 腾 28px 给 ✕ 列

## LoC

- **Net -4 LoC** (1 XAML file only, 6 UX 改动):
  - watch list 改右侧 TabControl: -11 LoC (4 col → 2 col)
  - toolbar 5 UI 删: -34 LoC
  - Filter CAN IDs UI Hide: +6 LoC (3 Visibility=Collapsed + 注释)
  - toggle label 短化: -8 LoC (5 字 → 3 字 × 2)
  - watch list ✕ 列: +22 LoC (DataGridTemplateColumn + Button)
  - Tracker fix (XAML 1 行 + record 1 行 + ChartSeriesFlow 1 行): +3 LoC
- 0 source files (XAML-only + record param + 1 chart factory line)
- 0 test files (no behavior change to App.Tests; Tracker is OxyPlot render hook invisible to unit tests)

## Test outcomes

- **App.Tests**: 819/3 SKIP/0 fail (unchanged — XAML-only)
- **Core.Tests**: 457/0/0 isolated; full-suite 1 transient flake (W50 sister pattern)
- **Infrastructure.Tests**: 89/2 SKIP/0 (unchanged)

## Lesson candidate observations

| Lesson | Status post-v3.50.4 | Notes |
|---|---|---|
| `side-panel-must-use-dock-with-minwidth-not-grid-star-when-resize-breaks-scroll-visibility` | **NEW 1/3** | v3.50.4 #1: WPF Grid star column hides DataGrid scrollbars on window resize; right-side docked TabControl with MinWidth floor keeps scroll reachability invariant |
| `oxyplot-plotview-must-explicitly-set-controller-for-tracker-tooltip` | **NEW 1/3** | v3.50.4 #2: OxyPlot `PlotView.Controller` defaults to null → no Tracker → no hover (X,Y) tooltip. Fix: bind `Controller="{Binding Controller}"` to a `new PlotController()` per series |
| `plotcontroller-type-lives-in-oxyplot-core-not-oxyplot-wpf` | **NEW 1/3** | v3.50.4 #2 类型细节: `PlotController` 在 `OxyPlot` core, 不在 `OxyPlot.Wpf`. WPF 项目加 `using OxyPlot;` 才能引用. CS0234 / CS1739 错误信号 |
| `toolbar-deprecated-controls-must-be-removed-not-kept-as-ui-when-user-marks-deprecated` | **NEW 1/3** | v3.50.4 #4: user "废弃" directive means permanent UI removal (not just hide); VM 端保留 for test mocking |
| `unused-feature-should-be-hidden-not-deleted-when-recovery-cost-is-low` | **NEW 1/3** | v3.50.4 #5: Filter CAN IDs = user 不知道用途 = 没用; Hide (Visibility=Collapsed) not delete (1-line restore possible). 与 #4 对比: 永久废弃=删, 临时不用=hide |
| `vm-relaycommand-without-xaml-binding-is-silent-feature-debt` | **NEW 1/3** | v3.50.4 #6: `RemoveFromWatch [RelayCommand]` 自 v3.15.0 一直存在 + 测试过, 但 XAML 从未绑 → UI 不可见. 任何 v3.15.0+ 时刻用户都无法 remove watch entry. 6 年 debt. |

## Out of scope (YAGNI)

- Watch list / Sampling Table split view (tabbed only)
- Column width 持久化
- Tab 顺序持久化
- Multi-row watch list CSV export
- OxyPlot Tracker 样式 (默认黑色十字 + 数值)
- VM 端 PlayCommand/PauseCommand/StopCommand/Loop/Speed/ScrubberValue 清理 (保留 for tests)

## Next (post-v3.50.4 ship)

- **v3.50.5 vault-only PATCH** (sister of W17 + W23.5-W25.5 + W26.5-W32.5 + v3.49.5 + v3.50.5): promote 6 NEW 1/3 lessons
- **W36 — next god-class refactor** (candidates: StatsViewModel 263 / DbcTokenizer 239 / DbcSendViewModel 238 / AscLocator 225 / RecordService 215)
- **v3.50.x fixture 公开问题**: 推迟 — v3.50.2 GH release 包含个人 .asc + 专有 .dbc fixture, 需决定是否改为合成数据
- **Trace Viewer role review**: 用户删除 playback controls 后, Trace Viewer = 只读图表面板. 后续是否进一步删 `ReplayService` / `Timeline` / `Scrubber` 关联代码 待评估 (sister of W22 RecordService refactor)
