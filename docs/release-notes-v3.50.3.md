# Release Notes v3.50.3 — Recording 公共位置 PATCH-A (revert v3.49 Q2)

**Released**: pending ship
**Tag**: v3.50.3
**Branch**: `feature/v3-50-3-patch-recording-revert`
**Parent**: v3.50.2 PATCH (`27a56cd` on `main`)

## Why this PATCH

v3.49 Q2 (`7ab48da`) 把 Recording 控件嵌进 TraceViewer 右侧 Expander → 用户必须先开 Trace Viewer 才能录。
v3.50.1 PATCH-A `014c548` 早已做完 revert 但从未 merge 到 main。

用户原话: "**提到一个公共的界面上去**"。

## What this PATCH does

Recording 控件从 TraceViewer 移回 **AppShell 公共界面**:
- AppShell 工具栏 `● Record` ToggleButton (XAML 顶部 ToolBar, 常驻)
- AppShell 底部 docked Expander (`DockPanel.Dock=Bottom`), 一直挂着, IsExpanded 跟 ToggleButton 双向绑
- TraceViewer 移除 Recording Expander + xmlns:rec
- TraceViewerViewModel ctor 删除 `RecordViewModel` 参数
- 删 `TraceViewerViewModel/Recording.partial.cs` (9th partial, 19 LoC)

## Architecture

| File | Delta | Notes |
|---|---|---|
| `AppShell.xaml` | +26 | ToolBar Record ToggleButton + 底部 Expander |
| `AppShellViewModel.cs` | +31 | `_recordingPanel` field + `IsRecordingPanelOpen` [ObservableProperty] + `ToggleRecordingPanel` [RelayCommand] + `RecordingPanel` 公开属性 |
| `TraceViewerViewModel.cs` | -14 | 删 RecordViewModel ctor param + RecordingViewModel assignment |
| `TraceViewerView.xaml` | -16 | 删 Recording Expander + xmlns:rec |
| `TraceViewerViewModel/Recording.partial.cs` | **DELETE** | 9th partial (19 LoC) |
| `AppHostBuilder.cs` | +2 | RecordViewModel 仍 wire, 现喂 AppShell panel (不是 TraceViewer) |
| `Directory.Build.props` | version | 3.50.2 → 3.50.3 (4 fields) |

**Net: +29 LoC** (从 +100/-43 cherry-pick + version bump)。

## LoC trajectory

- v3.50.2 (main): 103290 insertions cumulative
- v3.50.3 net delta: +100 cherry-pick -43 (1 commit squashed) + 4 version lines = **+61 net**

## Test outcomes

- **App.Tests**: 819/3 SKIP/0 fail (unchanged — `ShowRecordCommand` test 已替换为 `RecordingPanel_Is_Wired_And_ToggleCommand_Opens`, cherry-pick 带来)
- **Core.Tests**: 456/0/0 isolated; full suite 偶发 transient flake 1-2 fail (W23.5-W25.5+W27.5+W28+W34+W35+W50 sister pattern — 单独跑全过)
- **Infrastructure.Tests**: 89/2 SKIP/0 (unchanged)

## Cherry-pick notes

- 1 commit (`014c548` from `feature/v3-50-1-patch-recording-public-toolbar`) cherry-picked to `feature/v3-50-3-patch-recording-revert` from main `27a56cd`
- Auto-merge clean: `TraceViewerViewModel.cs` + `TraceViewerView.xaml` 与 v3.50.2 蓝线改动无 overlap
- Conflict: 0

## Lesson candidate observations

| Lesson | Status post-v3.50.3 | Notes |
|---|---|---|
| `recording-and-playback-must-not-share-window` | **PROMOTED 2/3** (from NEW 1/3 at v3.50.1) | v3.50.3 = 2nd observation: Recording 控件跟 TraceViewer 解耦; 跨窗口 tool 比同窗口 Expander 更易发现 |
| `recording-controls-moved-within-trace-viewer` | **SUPERSEDED** | v3.49 Q2 失败设计, v3.50.3 PATCH 关闭 |

## Out of scope (YAGNI)

- Recording 状态持久化到 .tmtrace
- Recording hotkey (e.g. F9 start/stop)
- AppShell Recording panel 折叠/展开动画
- Recording 配置 (file path, max size) UI

## Next (post-v3.50.3 ship)

- **v3.50.4 vault-only PATCH** (sister of W17 + W23.5-W25.5 + W26.5-W32.5 + v3.49.5 + v3.50.5): promote 1 NEW + archive 1 SUPERSEDED
- **v3.51**: 解决 Play/Pause/Reset 历史未解 cursor propagation 问题 (独立 cycle)
- **W36 — next god-class refactor** (candidates: StatsViewModel 263 / DbcTokenizer 239 / DbcSendViewModel 238 / AscLocator 225 / RecordService 215)
