# Release Notes v3.50.4 — Watch list 右侧侧栏 PATCH (always-visible panel)

**Released**: pending ship
**Tag**: v3.50.4
**Branch**: `feature/v3-50-4-patch-watch-list-right-panel`
**Parent**: v3.50.3 PATCH (`2943f94` on `main`)

## Why this PATCH

User 截图反馈: watch list 滚动栏放左侧 column 0，**窗口缩小时 DataGrid 水平/垂直滚动条被遮，找不到**。原话: "watch list 的滚动栏能放到显示窗口的右边么（别窗口大小改变了就找不到了）"。

## What this PATCH does

**Watch list 布局重构**: 从左列 → 右侧 TabControl 侧栏 (always visible)。

| 改前 (v3.50.3) | 改后 (v3.50.4) |
|---|---|
| Column 0: Watch List DataGrid | Column 0: Chart subplots (expanding) |
| Column 2: Chart subplots | Column 2: TabControl [Watch List \| Sampling Table] |
| Column 4: Sampling Table |  |

**新右侧 TabControl 容纳两个 tab**:
- **Watch List tab** (默认激活): 8 列 DataGrid (CAN ID / Signal / Plot / N / Latest / Δ / Blue) + 单位列已并入 Signal 显示（节省列宽）
- **Sampling Table tab**: 3 列 DataGrid (Signal / Unit / Value)，v3.49.0 Q1 panel

**关键设计点**:
- TabControl 永远挂在 column 2 (DockPanel 右侧)，窗口 resize 不影响
- GridSplitter 在 column 1，用户可拖 column 2 宽度 (默认 360px, MinWidth 280)
- Column 0 (chart subplots) `Width=3*` 占满剩余空间
- Tab 默认选 Watch List (用户主要看的就是这个)
- Sampling Table 仍保留 `HasWatchedSignals` 可见性绑定

## LoC

- **Net -11 LoC** (`TraceViewerView.xaml`: 旧布局 4 个 column + 2 splitter = 复杂；新布局 2 column + 1 splitter = 简洁)
- 1 file modified: `src/PeakCan.Host.App/Views/TraceViewerView.xaml`
- 0 source files (XAML-only change)
- 0 test files (UI layout; no behavior change)

## Test outcomes

- **App.Tests**: 819/3 SKIP/0 fail (unchanged — XAML-only, no behavior change)
- **Core.Tests**: 457/0/0 (unchanged)
- **Infrastructure.Tests**: 89/2 SKIP/0 (unchanged)
- **Full solution**: 1365 pass / 5 SKIP / 0 fail — ALL GREEN

## Lesson candidate observations

| Lesson | Status post-v3.50.4 | Notes |
|---|---|---|
| `side-panel-must-use-dock-with-minwidth-not-grid-star-when-resize-breaks-scroll-visibility` | **NEW 1/3** | v3.50.4 = 1st observation: WPF Window resize + Grid ColumnDefinition star width can hide DataGrid internal scrollbars; right-side docked TabControl with MinWidth floor keeps scroll reachability invariant to window width |

## Out of scope (YAGNI)

- Column width 持久化到 .tmtrace
- Tab 顺序持久化
- Watch List / Sampling Table split view (currently tabbed)
- Multi-row watch list export to CSV

## Next (post-v3.50.4 ship)

- **v3.50.5 vault-only PATCH** (sister of W17 + W23.5-W25.5 + W26.5-W32.5 + v3.49.5 + v3.50.5): promote 1 NEW 1/3 lesson
- **v3.51**: Play/Pause/Reset cursor propagation (independent cycle)
- **W36 — next god-class refactor** (candidates: StatsViewModel 263 / DbcTokenizer 239 / DbcSendViewModel 238 / AscLocator 225 / RecordService 215)
- **v3.50.x fixture 公开问题**: 推迟 — v3.50.2 GH release 包含个人 .asc + 专有 .dbc fixture, 需决定是否改为合成数据
