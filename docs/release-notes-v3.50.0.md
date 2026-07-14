# Release Notes v3.50.0 — 绿线锚点 watch 同步 (MINOR)

**Released**: pending T5
**Tag**: v3.50.0
**Branch**: `feature/v3-50-0-green-line-anchor-watch-sync`
**Parent**: v3.49.0 MINOR (`ba876e5` on `main`)

## Why this MINOR

v3.49.0 Q1 ship 的右侧 Sampling Table panel **完全不可用** —— 用户原话:

- "play/暂停/reset 改过几十版了都不起作用, 放弃"
- "我锚定一个 watch 时刻, 其他 watch 也同步锚定这同一个时刻值"
- "画绿线拦到谁谁就是锚点"

v3.49.0 panel 失败三连:
1. 只在 `WatchedSignals.CollectionChanged` 时刷一次,scrubber / Play / Pause 时 panel 不动。
2. 真值解码没接,用了 `frame.Data[0]` placeholder 当 single-byte ratio。
3. 没有"在 T 时刻所有信号值"的视觉统一视图。

v3.50.0 完全脱离 scrubber/Play/Pause/Reset,**用户拖绿线** = 唯一的同步入口:

- 每个 chart subplot 加一条 OxyPlot 绿色竖线 (`LineAnnotation` tagged `green-anchor`)
- 拖任意一条绿线 → 单一 anchor timestamp 驱动**所有** subplot 绿线 + 所有 watch row 的 `LatestValue` / `FrameCount`
- 解码用真 `SignalDecoder.Decode(frame.Data, signal)`(Factor + Offset 物理量),不再 placeholder
- 单 anchor 字段 (`_anchorTimestampSeconds`) + NaN = 清空所有绿线

## Architecture

**11th partial on TraceViewerViewModel**:`src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/GreenLineAnchorFlow.cs`(196 LoC)。单一锚点状态 `private double _anchorTimestampSeconds = double.NaN`,三个 public surface:

- `IsGreenLineAnchorActive` (bool) — XAML 可见性 gate
- `RefreshAtAnchor(double ts)` — 唯一 mutation 入口,idempotent add/remove LineAnnotation
- `UpdateAllGreenLines()` / `RecomputeAllLatestAtAnchor()` — 内部派生

**WatchedSignalRow.Signal** 缓存 DBC signal 引用:plain property setter 模式 (`SetProperty(ref _signal, value)`),**不用** `[ObservableProperty]` —— 因为 CommunityToolkit.Mvvm 源码生成器把 `.g.cs` 写到 XAML temp csproj,后者拉不进 `PeakCan.Host.Core.dll`。Ctor 末尾订阅 `WatchedSignals.CollectionChanged` 做 `_signalByKey` 字典缓存。

**PlotView 拖动 handler** (`TraceViewerView.xaml.cs`):
- `PreviewMouseLeftButtonDown/Up/Move` 三个 handler
- `Background="Transparent"` XAML 必需 —— 否则 PlotView 收不到鼠标事件
- 鼠标 capture,drag 期间 `_isDraggingGreenLine` gate 防止 hover 误触发
- `XAxis.InverseTransform(pos.X)` → timestamp seconds → `vm.RefreshAtAnchor(t)`

**真实信号解码**:master source frames via `ITraceSessionRegistry.GetFrames(masterSource.SourceId)`,binary search 到 anchor 时刻最近帧,调 `SignalDecoder.Decode(frame.Data, signal)` —— `Factor + Offset` 物理量,replaces v3.49.0 `frame.Data[0]` placeholder。

## LoC trajectory

| Task | Flow | LoC 变化 | Commit |
|---|---|---|---|
| T0 | SPEC + PLAN + branch | (docs only) | `bcc6f74` (spec) + `236cb98` (plan) |
| T1 | WatchedSignalRow.Signal 引用 + _signalByKey 缓存 | (主 partial 内 inline) | `d780e91` |
| T2 | 11th partial GreenLineAnchorFlow + RefreshAtAnchor API + 3 tests | +196 | `a16a15f` |
| T3 | TraceViewerView.xaml + xaml.cs drag handlers | +88 / -1 | `1d29bb2` |
| T4 | v3.49.0 → v3.50.0 + release notes | (no src) | this commit |

## Test outcomes

- **Core.Tests**: 457/0/0(unchanged,v3.49 ASC round-trip test 套不动)
- **App.Tests**: 803/3 SKIP/0 fail(filter 86/0/0 TraceViewer + GreenLine)
- **Infrastructure.Tests**: 89/2 SKIP/0(unchanged,hardware-dependent)
- **W23 STRUCT-FABRACTION LESSON 应用**: `SignalDecoder.Decode` 完整路径 (`global::PeakCan.Host.Core.Dbc.SignalDecoder.Decode`) —— W23 教训:XAML temp csproj 源生成器无法通过 `using` 拉 Core 类型
- **Binary search 改名 `BinarySearchLatestAtOrBeforeAnchor`**:避 CS0111 与 v3.49 SamplingTableFlow 同名 static 方法

## Notable YAGNI deferrals (sister of v3.49.0)

- **Play/Pause/Reset 自动刷新** —— 用户明示放弃,不再回到这条路径
- **ScrubberValue 自动跟** —— 不耦合,绿线拖动是唯一入口
- **右侧 Sampling Table panel** —— v3.49 ship 的 panel 标记废弃,不删代码(回退路径)
- **Per-source 锚点 split** —— 单 master source only,per-source 锚点推 v3.51+
- **多个锚点** —— 单 anchor 字段,1 个锚点 only
- **Anchor 时刻 visual marker 在 watch list 上** —— 仅图表绿线 + row Latest/FrameCount,不在 watch row 加额外列

## Lesson candidate observations

| Lesson | Status post-v3.50 | Notes |
|---|---|---|
| `cross-format-spec-extracted-into-shared-library` | N/A | v3.50 不动 AscFormat |
| `sampling-table-panel-shared-cursor-across-multiple-signals` | **DEFERRED**:v3.50 改用 anchor 模式,原 lesson 描述成"已 ship 但失败"|YAGNI'd 路径正式废弃,新观察替代 |
| `green-line-anchor-driven-watch-sync` | **NEW 1/3** | v3.50 = 1st observation: 单一 _anchorTimestampSeconds + NaN gate + idempotent LineAnnotation tagged 'green-anchor' + drag handler at PlotView level + real SignalDecoder.Decode |
| `plotview-drag-handler-requires-transparent-background` | **NEW 1/3** | v3.50 = 1st observation: WPF PlotView 默认 Background=null 让鼠标事件穿透,handler 完全不触发 |
| `mvvm-source-gen-xaml-temp-csproj-cant-pull-core-types` | **NEW 1/3** | v3.50 = 1st observation: CommunityToolkit.Mvvm `[ObservableProperty]` 生成的 partial .g.cs 落到 obj/*wpftmp.csproj,该 csproj 无法 reference PeakCan.Host.Core.dll → 用 global:: 还是不行,只能改用 plain property + SetProperty |

## Out of scope (YAGNI)

- 锚点持久化 (.tmtrace 不会保存 anchor timestamp)
- 锚点历史撤销栈
- 多 trace 同步锚点(目前只 master source)
- 锚点 + scrubber 联动(用户明示脱钩)
- Sampling Table 右侧 panel 移除(代码保留作为回退,YAGNI 删)

## Next (post-v3.50 ship)

- **v3.50.5 vault-only PATCH**(sister of v3.49.5): 3 NEW 1/3 lesson candidate + 更新 1 DEFERRED lesson
- **W36 — 下一个 god-class refactor**:候选 StatsViewModel 263 / DbcTokenizer 239 / DbcSendViewModel 238 / AscLocator 225 / RecordService 215 LoC(全部 >200 LoC main)
- **v3.51 解决 Play/Pause/Reset 历史未解 cursor propagation 问题**(独立 cycle,跟 Q1 脱钩)