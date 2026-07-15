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

### 4. (NOT changed) Loop / Play/Pause/Stop / Slider

用户担心被删 — **没删**, toolbar 73-103 行原封不动。截图看起来不全 = 窗口宽度不够, 水平滚动 toolbar 即可看到完整功能。

### 5. (NOT changed) Filter CAN IDs + Clear 按钮

**用途**: 全局 CAN ID 过滤, 限制 TraceViewer 渲染/匹配的 CAN ID 列表. 文本框接受 decimal 或 0x-hex, 逗号分隔 (例: `0x100, 0x200, 256`). **空 = 显示全部**. Clear 按钮一键清空.

## LoC

- **Net +6 LoC** (3 files: XAML + record + ChartSeriesFlow)
  - `TraceViewerView.xaml`: ~+3 (toggle label shortens, XAML pure)
  - `TraceChartSeries.cs`: +1 (positional param, 11 call sites unchanged)
  - `ChartSeriesFlow.cs`: +1 (named arg `Controller: new PlotController()`)
  - **Watch list XAML 重构**: -11 LoC (4 column → 2 column)
- 0 test files changed (no behavior change to App.Tests; Tracker is OxyPlot render hook invisible to unit tests)

## Test outcomes

- **App.Tests**: 819/3 SKIP/0 fail (unchanged)
- **Core.Tests**: 457/0/0 (unchanged)
- **Infrastructure.Tests**: 89/2 SKIP/0 (unchanged)
- **Full solution**: 1365 pass / 5 SKIP / 0 fail — ALL GREEN

## Lesson candidate observations

| Lesson | Status post-v3.50.4 | Notes |
|---|---|---|
| `side-panel-must-use-dock-with-minwidth-not-grid-star-when-resize-breaks-scroll-visibility` | **NEW 1/3** | v3.50.4 #1: WPF Grid star column hides DataGrid scrollbars on window resize; right-side docked TabControl with MinWidth floor keeps scroll reachability invariant |
| `oxyplot-plotview-must-explicitly-set-controller-for-tracker-tooltip` | **NEW 1/3** | v3.50.4 #2: OxyPlot `PlotView.Controller` defaults to null → no Tracker → no hover (X,Y) tooltip. Fix: bind `Controller="{Binding Controller}"` to a `new PlotController()` per series |
| `plotcontroller-type-lives-in-oxyplot-core-not-oxyplot-wpf` | **NEW 1/3** | v3.50.4 #2 类型细节: `PlotController` 在 `OxyPlot` core, 不在 `OxyPlot.Wpf`. WPF 项目加 `using OxyPlot;` 才能引用. CS0234 / CS1739 错误信号 |

## Out of scope (YAGNI)

- Watch list / Sampling Table split view (tabbed only)
- Column width 持久化
- Tab 顺序持久化
- Multi-row watch list CSV export
- OxyPlot Tracker 样式 (默认黑色十字 + 数值)

## Next (post-v3.50.4 ship)

- **v3.50.5 vault-only PATCH** (sister of W17 + W23.5-W25.5 + W26.5-W32.5 + v3.49.5 + v3.50.5): promote 3 NEW 1/3 lessons
- **v3.51**: Play/Pause/Reset cursor propagation (independent cycle)
- **W36 — next god-class refactor** (candidates: StatsViewModel 263 / DbcTokenizer 239 / DbcSendViewModel 238 / AscLocator 225 / RecordService 215)
- **v3.50.x fixture 公开问题**: 推迟 — v3.50.2 GH release 包含个人 .asc + 专有 .dbc fixture, 需决定是否改为合成数据
