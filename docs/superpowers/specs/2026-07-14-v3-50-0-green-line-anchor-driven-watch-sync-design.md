# v3.50.0 MINOR SPEC — 绿线锚点 watch list 同步时刻值

**Date**: 2026-07-14
**Target version**: v3.50.0 MINOR
**Parent**: v3.49.0 MINOR (`ba876e5` on main)
**Replaces**: v3.49.0 Q1 ship 失败的右侧 Sampling Table panel

## Context

v3.49.0 ship 的右侧 Sampling Table panel 完全不可用:
- 没有 scrubber-driven refresh (只 CollectionChanged 时刷一次)
- 没有播放驱动 (Play/Pause 时 panel 不动)
- 真值解码没接 (用 `frame.Data[0]` placeholder)

**用户原话**:
- "play/暂停/reset 改过几十版了都不起作用, 放弃"
- "我锚定一个 watch 时刻, 其他 watch 也同步锚定这同一个时刻值"
- "画绿线拦到谁谁就是锚点"

**新设计**: **每图一条绿竖线 (OxyPlot `LineAnnotation`), 拖任何一条其他图与 watch 表全部同步 X 时刻 + Latest 解码值**. 完全脱离 scrubber/Play/Pause/Reset.

## Architecture

### 新 partial on TraceViewerViewModel

`src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/GreenLineAnchorFlow.cs` (~120 LoC)

**核心 single state**:
- `double _anchorTimestampSeconds` (default = `double.NaN`)
- 当 `!double.IsNaN(t)` 表示「锚点激活」 — 所有 signal 的绿线和 Latest 列都按 t 同步
- 当 `double.NaN` 表示「无锚点」 — 退回 v3.49.0 默认 Latest 列行为

### 11th partial on TraceViewerViewModel — SigDecoder 集成

主 partial ctor 末尾订阅 `WatchedSignals.CollectionChanged`:
- Add 时查 `_dbcService.Current?.FindSignal(row.CanIdHex, row.SignalName)` 缓存到字典 `_signalByKey`
- Remove 时清缓存

### OxyPlot LineAnnotation 集成

每个 signal 的 `PlotModel` 在 refresh series 后加 1 条 `LineAnnotation`:
- `Type = LineAnnotationType.Vertical`
- `X = _anchorTimestampSeconds` (or NaN 时 not shown)
- `Color = OxyColors.Green`, `StrokeThickness = 2`, `LineStyle = LineStyle.Solid`
- `Text = ""` (no label clutter)

**共享 X 锚点**: 单个 `_anchorTimestampSeconds` 字段驱动所有 PlotModel 的绿线 X。OxyPlot 共享 X 轴 = 时间戳, 单一锚点天然同步所有 subplot 的绿线视觉位置。

### 解码逻辑 (复用 SignalDecoder)

每行 Latest 重算:
- `value = SignalDecoder.DecodeRaw(frame.Data, signal) → 工程值 (factor + offset)`
- 单条规则: row's latest = master source 该 anchor 时刻最近帧的解码值
- 帧不存在的行: `LatestValue = double.NaN` (v3.49 兼容 fallback)

### 拖动交互 (新入口)

每个 PlotView 加 `MouseLeftButtonDown` + `MouseMove` handler:
- 拖动绿线时改 `_anchorTimestampSeconds`
- 触发 `OnAnchorTimestampSecondsChanged` partial method
- 那方法推 3 件事:
  1. **所有 PlotModel** 的绿线 `X` 重置 (plumb model.Annotations)
  2. **WatchedSignals 每行** Latest 重算
  3. **WatchedSignals 每行** FrameCount 推到 anchor 时刻最近帧号

## 任务顺序

T0: SPEC + PLAN + branch
T1: WatchedSignalRow 加 `AnchorTimestampSeconds` field + DbcSignal 引用 (主 partial ctor)
T2: GreenLineAnchorFlow partial 加 `SignalDecoder` 集成 + PlotModel LineAnnotation 创建 + drag handler
T3: TraceViewerView.xaml 改 PlotView 加绿线 + 测试
T4: v3.50.0 MINOR bump + release notes
T5: Tier-3 ship

## Verification

- `dotnet build`: 0 errors
- 现有 800 + 0 + 89 + 457 tests 全部 PASS
- 新 ≥3 tests:
  - `GreenLine_DraggedTo_UpdatesAllWatchedLatest`: 拖绿线到 t=5.2 → 所有 watch Latest 跳到 5.2 解码值
  - `GreenLine_AcrossMultiTrace`: 在 2 个 source 上拖 → 2 source 的图都有绿线在同 X 位置
  - `Anchor_WhenGreenLineNaN_NoAnnotations`: `_anchorTimestampSeconds = NaN` → OxyPlot Annotations 列表为空

## Out of scope (YAGNI)

- Play/Pause/Reset 自动刷新 (用户明示放弃此路径)
- ScrubberValue 自动跟 (不耦合)
- 右侧 Sampling Table panel (废弃)
- Per-source annotations 配置面板 (写死绿线样式)
- 多个锚点 (一次只允许一个)

## Next (post-v3.50 ship)

- v3.50.5 vault-only PATCH (consolidates 1 NEW lesson candidate `green-line-anchor-driven-watch-sync`)
- W36 (next god-class refactor)
- v3.51 解决 Play/Pause/Reset 历史未解的 cursor propagation 问题 (独立 cycle, 跟 Q1 解耦)
