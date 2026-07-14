# Release Notes v3.50.1 — Recording revert (Recording tab 回 AppShell) (PATCH)

**Released**: pending ship
**Tag**: v3.50.1
**Branch**: `feature/v3-50-1-patch-recording-revert`
**Parent**: v3.50.0 MINOR (`7f8035d` on `main`)
**Reverts**: v3.49.0 MINOR Q2 (`7ab48da`)

## Why this PATCH

v3.49.0 MINOR Q2 把 Recording 控件从 AppShell 独立 menu 搬进了 Trace Viewer 窗口底部的折叠 Expander。理由：operator 录制时常看 trace 上下文，2 窗口切换痛。

但这个设计 **违反了语义边界**：

- **Trace Viewer 语义 = 离线回放** —— 加载 .asc + DBC 解码 + 多 trace overlay + 绿线锚点 (v3.50)
- **Recording 语义 = 实时录制** —— 通过 `PeakCanChannel` 抓 live bus frames → 写 .asc 文件
- **同窗口 ≠ 同语义** —— 把 Recording 塞进 Trace Viewer 窗口 = 视觉/逻辑混淆

**用户原话**: "recording 这个设计你没改啊, 我觉得是 bug, trace viewer 本身是回放, 跟录制数据的业务根本不挂钩"

## Revert scope (mirror-image of v3.49 Q2 commit `7ab48da`)

| File | Change |
|---|---|
| `src/PeakCan.Host.App/Views/TraceViewerView.xaml` | -12 LoC: 删 Recording Expander + `xmlns:rec` |
| `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/Recording.partial.cs` | -19 LoC: 整个 9th partial 删除 (10 partials → 9 partials) |
| `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` | -14 LoC: 删 RecordViewModel ctor param + RecordingViewModel 赋值 + v3.49 Q2 注释 |
| `src/PeakCan.Host.App/AppShell.xaml` | +6 LoC: 加回 `<MenuItem Header="Record" Command="{Binding ShowRecordCommand}" />` |
| `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs` | +9 LoC: 加回 _recordViewModel 字段 + ctor 参数 + 赋值 |
| `src/PeakCan.Host.App/ViewModels/AppShellViewModel/ViewSwitchFlow.cs` | +15 LoC: 加回 ShowRecord [RelayCommand] (ViewSwitcher.Show pattern) |
| `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` | +4 LoC: 加回 `sp.GetRequiredService<RecordViewModel>()` |
| `tests/.../AppShellViewModelTests.cs` | +24 LoC: 加回 6 处 RecordViewModel ctor arg + ShowRecordCommand_Is_Not_Null_And_Can_Execute test |
| `tests/.../AppShellViewModelMessageBoxPromptTests.cs` | +3 LoC: 加回 1 处 RecordViewModel ctor arg |
| `tests/.../Windows/UdsWindowTests.cs` | +3 LoC: 加回 1 处 RecordViewModel ctor arg |

**Net: +19 LoC** (主要来自 tests + Record 注释 + ShowRecordCommand 测试加回)

## Test outcomes

- **Core.Tests**: 456/0/0 (transient flaky 1x on AscParserTests.Parse_MalformedLines_LogsEachWithLineNumberAndReason — pre-existing in main, sister of W23.5-W25.5+W27.5+W28+W34+W35 PATCH pattern; 5x isolated runs all PASS)
- **App.Tests**: 807/3 SKIP/0 fail (+1 new ShowRecordCommand_Is_Not_Null_And_Can_Execute test)
- **Infrastructure.Tests**: 89/2 SKIP/0 (unchanged)

## Architecture milestones

- **34th ship cycle overall** (= 31 god-class + v3.49 + v3.50 + v3.50.1)
- **10 → 9 partials** on TraceViewerViewModel (removed Recording.partial.cs)
- **Recording restored to AppShell View menu** (v1.2.11 PATCH Item 6 design preserved)
- **AppShell ctor chain** restored: 11 VM args (was 10 in v3.49+v3.50)

## Notable YAGNI deferrals

- Recording 控件本身的 UI 改动 —— 不动
- RecordService / RecordViewModel / RecordView 代码 —— 完全不动, 只是重新绑位置
- Trace Viewer 窗口其他内容 (绿线锚点, watch list, chart subplots, Reset bug fix) —— 完全不动

## Lesson candidate observations

| Lesson | Status post-v3.50.1 | Notes |
|---|---|---|
| `recording-controls-moved-within-trace-viewer` (v3.49 NEW 1/3) | **DEFERRED / SUPERSEDED** | v3.49 Q2 design reversed in v3.50.1; lesson archived as "v3.49 ship design superseded by v3.50.1 revert" |
| `recording-and-playback-must-not-share-window` | **NEW 1/3** | v3.50.1 = 1st observation: Trace Viewer (offline .asc playback) and Recording (live bus capture) have different data sources + lifecycle + DI consumers; sharing a window conflates the two concerns |
| `green-line-anchor-driven-watch-sync` (v3.50 NEW 1/3) | unchanged | v3.50 untouched by v3.50.1 |
| `plotview-drag-handler-requires-transparent-background` (v3.50 NEW 1/3) | unchanged | v3.50 untouched |
| `mvvm-source-gen-xaml-temp-csproj-cant-pull-core-types` (v3.50 NEW 1/3) | unchanged | v3.50 untouched |
| `reset-must-clear-all-mutable-vm-state-for-singleton-vm-reuse` (v3.50 NEW 1/3) | unchanged | v3.50 untouched |

## Out of scope (YAGNI)

- Recording 控件本身的任何 UI 改动
- Trace Viewer 窗口任何改动 (保留 v3.50 绿线锚点 + Reset bug fix)
- RecordingService 接口或 DI lifetime 调整

## Next (post-v3.50.1 ship)

- **v3.50.2 PATCH-B**: 绿线 show/hide 按钮 + 蓝色差值线 + watch list Delta 列 (独立 cycle, 不绑 Recording revert)
- **v3.50.5 vault-only PATCH**: consolidate 5 NEW 1/3 lessons from v3.50 + v3.50.1
- **W36 — next god-class refactor** (candidates: StatsViewModel 263 / DbcTokenizer 239 / DbcSendViewModel 238 / AscLocator 225 / RecordService 215)