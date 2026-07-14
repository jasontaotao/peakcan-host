# v3.50.1 PATCH SPEC — Recording revert (Recording tab 回 AppShell)

**Date**: 2026-07-14
**Target version**: v3.50.1 PATCH
**Parent**: v3.50.0 MINOR (`7f8035d` on `main`)
**Sister of**: v3.49.0 MINOR Q2 (`7ab48da`, now reverted)

## Context

v3.49.0 MINOR Q2 把 Recording 控件从 AppShell 独立 menu/tab 搬进了 Trace Viewer 窗口底部的折叠 Expander。理由：operator 录制时常常想看 trace 上下文, 2 窗口切换痛。

但这个设计 **违反了语义边界**：

- **Trace Viewer 语义 = 离线回放** —— 加载 .asc 文件 + DBC 解码 + 多 trace overlay + 绿线锚点 (v3.50) + Recording 没参与这条 pipeline
- **Recording 语义 = 实时录制** —— 通过 `PeakCanChannel` 抓 live bus frames → 写 .asc 文件, 跟"看历史 trace"完全不同 lifecycle
- **同窗口 ≠ 同语义** —— 把 Recording 塞进 Trace Viewer 窗口 = 视觉/逻辑混淆: "在 Trace Viewer 里录了一段, 是不是自动加进 chart subplots?" 不会, 它只是借了个窝

**用户原话**: "recording 这个设计你没改啊, 我觉得是 bug, trace viewer 本身是回放, 跟录制数据的业务根本不挂钩"

## Revert scope (来自 v3.49 Q2 commit `7ab48da`)

### 删

1. **`src/PeakCan.Host.App/Views/TraceViewerView.xaml`** — 删 Recording Expander (lines 182-191) + `xmlns:rec="clr-namespace:PeakCan.Host.App.Views"` (line 13)
2. **`src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/Recording.partial.cs`** — 整个 9th partial 删除 (19 LoC)
3. **`src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs`** — 删 `RecordViewModel? recordingViewModel = null` ctor param + `RecordingViewModel = recordingViewModel` ctor 末尾赋值 + 所有 v3.49.0 MINOR Q2 注释
4. **`src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs`** — 加回 `RecordViewModel _recordViewModel` 字段 + ctor 参数 + ctor 赋值
5. **`src/PeakCan.Host.App/ViewModels/AppShellViewModel/ViewSwitchFlow.cs`** — 加回 `[RelayCommand] private void ShowRecord()` 方法 (ViewSwitcher.Show pattern)
6. **`src/PeakCan.Host.App/AppShell.xaml`** — 加回 `<MenuItem Header="Record" Command="{Binding ShowRecordCommand}" />` (插在 `Replay` 前, 即 line 39 前)
7. **`src/PeakCan.Host.App/Composition/AppHostBuilder.cs`** — 加回 `sp.GetRequiredService<RecordViewModel>()` (插回 ctor 参数列表 udsViewModel 之后, line 291 处)

### 测试调整

8. **`tests/.../AppShellViewModelTests.cs`** — 加回 6 处 `new RecordViewModel(new RecordService(...), NullLogger<RecordViewModel>.Instance)` 参数
9. **`tests/.../AppShellViewModelMessageBoxPromptTests.cs`** — 加回 1 处 RecordViewModel 参数
10. **`tests/.../Windows/UdsWindowTests.cs`** — 加回 1 处 RecordViewModel 参数
11. **`tests/.../AppShellViewModelTests.cs`** — 加回 `[Fact] ShowRecordCommand_Is_Not_Null_And_Can_Execute` 测试 (被 v3.49 删了)

## 任务顺序

- **T0**: SPEC + PLAN + branch (`feature/v3-50-1-patch-recording-revert`)
- **T1**: TraceViewerView.xaml 删 Expander + xmlns
- **T2**: TraceViewerViewModel/Recording.partial.cs 整个删 + TraceViewerViewModel.cs ctor 参数 + 赋值删
- **T3**: AppShell.xaml 加 Record menu + AppShellViewModel 加字段/ctor 参数 + ViewSwitchFlow.cs 加 ShowRecord + AppHostBuilder 加 RecordViewModel 注册
- **T4**: 测试调整 (加回 RecordViewModel 实例 + ShowRecordCommand 测试) + version bump v3.50.0 → v3.50.1 + release notes
- **T5**: Tier-3 ship

## Verification

- `dotnet build`: 0 errors
- 现有 806 App + 457 Core + 89 Infra tests 全部 PASS (含加回的 ShowRecordCommand_Is_Not_Null_And_Can_Execute → 1 net new test)
- `grep -rn "Recording\|RecordingViewModel" src/PeakCan.Host.App/Views/TraceViewerView.xaml src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/` → 0 hits (Recording 字眼从 TraceViewer 完全消失)
- `grep -rn "ShowRecordCommand\|Record" src/PeakCan.Host.App/AppShell.xaml` → 1 hit (Record menu item 加回)
- `grep -n "RecordViewModel" src/PeakCan.Host.App/Composition/AppHostBuilder.cs` → 1 hit (注册恢复)

## Out of scope (YAGNI)

- RecordService / RecordViewModel / RecordView 本身的代码 —— 完全不动, 只是重新绑位置
- Recording 控件本身的 UI 改动 (默认值, 默认文件名, 录制时长等) —— 完全不动
- Trace Viewer 窗口其他内容 (绿线锚点, watch list, chart subplots) —— 完全不动
- 用户授权 / 录制策略 —— 完全不动

## Sister-pattern (v3.49 → v3.50.1 完整 revert)

- v3.49 Q2 commit `7ab48da` = 净 +0 LoC (45 +/45 -) —— 这次 revert 应该是 -0 LoC net (mirror image)
- 但 +11 LoC 测试 (ShowRecordCommand_Is_Not_Null_And_Can_Execute 测试加回) —— 实际 +11 LoC
- 净 **+11 LoC**

## Lesson candidate observation

| Lesson | Status | Notes |
|---|---|---|
| `recording-controls-moved-within-trace-viewer` (v3.49 NEW 1/3) | **DEFERRED / SUPERSEDED** | v3.49 Q2 design reversed in v3.50.1. Lesson archived as "v3.49 ship design superseded by v3.50.1 revert". Sister of v3.50's `sampling-table-panel-shared-cursor-across-multiple-signals` which was DEFERRED for the same reason. |
| `recording-and-playback-must-not-share-window` | **NEW 1/3** | v3.50.1 = 1st observation: Trace Viewer (offline .asc playback) and Recording (live bus capture) have different data sources + lifecycle + DI consumers; sharing a window conflates the two concerns. Revert to separate AppShell menu + Trace Viewer window. |

## Next (post-v3.50.1 ship)

- **v3.50.2 PATCH-B**: 绿线 show/hide 按钮 + 蓝色差值线 + watch list Delta 列 (独立 cycle, 不绑 Recording revert)
- **v3.50.5 vault-only PATCH**: consolidate v3.50 + v3.50.1 NEW 1/3 lessons
- **W36 — next god-class refactor** (candidates: StatsViewModel 263 / DbcTokenizer 239 / DbcSendViewModel 238 / AscLocator 225 / RecordService 215)