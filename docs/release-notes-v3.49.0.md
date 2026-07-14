# Release Notes v3.49.0 — ASC 单源 + Sampling Table + Recording 合并 (MINOR)

**Released**: pending T7
**Tag**: v3.49.0
**Branch**: `feature/v3-49-0-minor-asc-sampling-recording`
**Parent**: v3.48.2 PATCH (`fe40d63` on `main`)

## Why this MINOR

3 个用户痛点一次性处理:
- **Q3** ASC writer (`RecordService/Format.partial.cs`) 和 parser (`AscParser/`) 之前各自硬编码格式字符串 — drift 风险。v3.49 抽到 `Core/Replay/AscFormat.cs` 单源,6 个 round-trip test 锁住契约。
- **Q2** Recording 控件作为独立 tab 在 AppShell 里 — 与 v3.16.6 PATCH 把 Trace Viewer 移到非模态窗口后,operator 需要在 2 个窗口间切换。v3.49 把 Recording Expander 内嵌到 Trace Viewer 窗口底部。
- **Q1** Trace Viewer operator 同时观测 10+ 多个信号时,没有"在 scrubber time T 时刻所有信号值"的统一视图。v3.49 加右侧 "Sampling Table" panel。

## LoC trajectory

| Task | Flow | LoC 变化 | Main commit |
|---|---|---|---|
| T1 | Q3: 新建 AscFormat.cs (264 LoC) | +264 | `9cfe110` |
| T2 | Q3: 3 partials delegate 到 AscFormat | -194 net | `aab6ec1` |
| T3 | Q3: 6 round-trip test | +188 | `609d760` |
| T4 | Q2: Recording tab → Trace Viewer Expander | -45d / +45i | `7ab48da` |
| T5 | Q1: SamplingTable 9th partial + right panel | +150 | `dc1e4ca` |
| T6 | v3.48.2 → v3.49.0 + release notes | (no src) | this commit |

## Test outcomes

- **Core.Tests**: 449 → 457 (+8 round-trip tests)
- **App.Tests**: 800 PASS / 3 SKIP / 0 fail (transient flaky 1x retry per W34 sister pattern)
- **Infrastructure.Tests**: 89/2/0 (unchanged)

## Architecture

**Q3 ASC single-source-of-truth**: `src/PeakCan.Host.Core/Replay/AscFormat.cs` 新建 (264 LoC static class),暴露:
- Writer 端: `WriteHeader` / `WriteFooter` / `WriteDataLine` / `FormatFlagsCompact`
- Parser 端: `TryParseDataLine` / `TryParseDateHeader` / `LineIsSectionDelimiter`

`RecordService/Format.partial.cs` 67→30 LoC;`AscParser/DataLineParserFlow.cs` 172→16 LoC;`AscParser/ParseLinesFlow.cs` 114→73 LoC。**净 -194 LoC**(比 plan 预估 -50 多,因为 subagent 也清理了部分历史冗余)。6 个 round-trip tests (classic/FD/BRS+ESI/Error flag/multi-frame monotonic × 3 sizes/header parse)。

**Q2 Recording tab → Trace Viewer**: 删除 AppShell menu item `Record` + `ShowRecordCommand` + `AppShellViewModel.RecordViewModel` ctor 参数 + 缓存字段。`RecordViewModel` 仍 singleton DI。新增 9th partial `TraceViewerViewModel/Recording.partial.cs` (公开 `RecordingViewModel` 属性)。`TraceViewerView.xaml` 加 `<rec:RecordView>` EmbeddedExpander (默认 collapsed)。`AppHostBuilder` 不再 wire RecordViewModel 进 AppShell。

**Q1 Sampling Table**: 10th partial `TraceViewerViewModel/SamplingTableFlow.cs` (~140 LoC) + `SamplingTableRow` record (`CanIdHex` / `MessageName` / `SignalName` / `Unit` / `Value`)。`RefreshSamplingTable()` public API,通过主 partial ctor 内 `WatchedSignals.CollectionChanged += (_, _) => RefreshSamplingTable();` hook 实现自动刷新 (避免与 `TransportFlow.OnScrubberValueChanged` partial method 冲突)。`TraceViewerView.xaml` 加 `Grid.Column="3"` Border + `Visibility="{Binding HasWatchedSignals, Converter=...}"` + DataGrid (Signal / Unit / Value 三列)。

## Notable YAGNI deferrals (留 v3.50 follow-up)

- **ScrubberValue-debounced refresh**: `OnScrubberValueChanged` 与 `TransportFlow` 冲突。v3.49 走 `WatchedSignals.CollectionChanged` 路径。follow-up:在 `TransportFlow.OnScrubberValueChanged` 末尾添加 `RefreshSamplingTable()` 调用。
- **IDbcDecoder 真实信号值提取**: v3.49 占位 `frame.Data[0]` 单字节比例值。follow-up:WatchedSignalRow 暴露 `DbcSignal` 引用,在 RefreshSamplingTable 中调 `IDbcDecoder.Decode(signal, frame.Data)`。
- **Per-source split**: ITraceViewerService 只暴露全局 `LoadedFrames`。follow-up:`ITraceViewerService.GetFrames(string sourceId)` + `MasterSourceId`-pinned `SamplingTableRow.SamplingSourceId` 列。
- **CSV 导出 / 相关矩阵 / auto-record / Vector ASC 'd'/'l' tokens 等**: spec [Out of scope] 章节已列。

## Lesson candidate observations

| Lesson | Status post-v3.49 | Notes |
|---|---|---|
| `infrastructure-channel-layer-sister-pattern-empirical-w18-w25-LOCKED` | N/A | v3.49 是 App/ViewModels layer |
| `second-cycle-god-class-refactor-empirical-w28-w29-w35` | N/A | v3.49 没改 god-class (主 part 一直 <800 LoC) |
| `cross-format-spec-extracted-into-shared-library` | **NEW 1/3** | v3.49 = 1st observation: writer + parser share static class |
| `recording-controls-moved-within-trace-viewer` | **NEW 1/3** | v3.49 = 1st observation: tab consolidation into conceptual owner window |
| `sampling-table-panel-shared-cursor-across-multiple-signals` | **NEW 1/3** | v3.49 = 1st observation: master-source-driven per-signal value lookup (YAGNI'd ScrubberValue-debounced version) |

## Out of scope (YAGNI)

- CSV 导出 Sampling Table / 相关矩阵 / auto-record / Vector tokens — deferred to future PATCH
- Per-window XAML `Icon=` (still v3.48.2 PATCH's pending follow-up)

## Next (post-v3.49 ship)

- **v3.49.5 vault-only PATCH** (sister of W17 + W23.5-W25.5 + W26.5-W32.5): 1 docs-only commit consolidating 3 NEW 1/3 lesson candidates
- **v3.50.0 MINOR**: ScrubberValue debounce + IDbcDecoder 真实提取 + per-source split (above 3 YAGNI items)
