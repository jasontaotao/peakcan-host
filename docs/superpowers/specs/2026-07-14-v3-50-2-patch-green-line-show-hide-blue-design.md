# v3.50.2 PATCH SPEC — 绿线 show/hide 按钮 + 蓝色差值线 + Delta 列

**Date**: 2026-07-14
**Target version**: v3.50.2 PATCH
**Parent**: v3.50.0 MINOR (`7f8035d` on `main`)

## Context

v3.50.0 MINOR 加了绿线锚点机制（单 `_anchorTimestampSeconds` 字段 + 拖动 PlotView 设 anchor + 同步所有 chart + watch row Latest/FrameCount）。用户后续要：

1. **绿线 show/hide 按钮** —— 工具栏 toggle, 控制 green-anchor LineAnnotation 可见性, Latest 同步逻辑不变
2. **蓝色比较线** —— 第二个锚点字段 `_blueAnchorTimestampSeconds`, 跟绿线一样可拖
3. **Delta 列** —— watch list 加 `Delta = Latest(蓝) - Latest(绿)`

## Architecture

### 新 partial on TraceViewerViewModel: `BlueLineAnchorFlow.cs` (12th partial)

mirror of v3.50 `GreenLineAnchorFlow.cs`:
- `private double _blueAnchorTimestampSeconds = double.NaN`
- `public bool IsBlueLineAnchorActive => !double.IsNaN(_blueAnchorTimestampSeconds)`
- `public void RefreshAtAnchorBlue(double ts)` — 唯一 mutation 入口
- `private void UpdateAllBlueLines()` — 加/移除 `LineAnnotation` tagged `"blue-anchor"`, Color = `OxyColors.Blue`
- `private void RecomputeAllLatestAtBlueAnchor()` — binary search master source frames + SignalDecoder.Decode
- `public void SetGreenLinesVisible(bool visible)` — 设 `_isGreenLineVisible`, 更新现有 green-anchor LineAnnotation IsVisible
- `public bool IsGreenLineVisible { get; private set; } = true;` (默认 true)

### `WatchedSignalRow.cs` 加 3 个新 properties (plain property 模式, sister of v3.50 `Signal`)

```csharp
public double BlueLatestValue { get => _blueLatestValue; set => SetProperty(ref _blueLatestValue, value); }
public int BlueFrameCount { get => _blueFrameCount; set => SetProperty(ref _blueFrameCount, value); }
public double DeltaValue => BlueLatestValue - LatestValue; // computed
```

### `TraceViewerView.xaml` 改

- 工具栏加 `<ToggleButton Content="绿线" IsChecked="{Binding IsGreenLineVisible, Mode=TwoWay}" />`
- PlotView 加 `PreviewMouseRightButtonDown` 触发蓝线 anchor 设置 (左键 = 绿, 右键 = 蓝)
- watch list DataGrid 加 `Delta` 列 binding `{Binding DeltaValue, StringFormat=F2}`

### `TraceViewerView.xaml.cs` 改

- 拆分 `OnPlotViewMouseDown` 为左右键分别触发
- 新增 `OnPlotViewMouseDownBlue` handler

## 任务顺序

- **T0**: SPEC + PLAN + branch (`feature/v3-50-2-patch-green-line-show-hide-blue`)
- **T1**: BlueLineAnchorFlow.cs partial (12th) + SetGreenLinesVisible method (in GreenLineAnchorFlow) + 3 new tests
- **T2**: WatchedSignalRow 加 BlueLatestValue + BlueFrameCount + DeltaValue + TraceViewerViewModel 字段初始化
- **T3**: XAML 工具栏绿线 toggle + PlotView 右键蓝线 + Delta 列
- **T4**: v3.50.0 → v3.50.2 PATCH + release notes + capture-decisions
- **T5**: Tier-3 ship

## Verification

- `dotnet build`: 0 errors
- 现有 806 App + 457 Core + 89 Infra tests 全部 PASS
- 新 ≥3 tests:
  - `BlueLine_DraggedTo_UpdatesBlueWatchedLatest`
  - `SetGreenLinesVisible_False_HidesAnnotations`
  - `DeltaValue_IsBlueMinusGreen`
- `grep -n "blue-anchor" src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/` → 1+ hit

## Out of scope (YAGNI)

- 蓝线 + 绿线差值的颜色配置 (写死 OxyColors.Blue)
- 多个蓝线 (单蓝线 single anchor field)
- 蓝线 + ScrubberValue 联动
- 蓝线持久化到 .tmtrace

## Lesson candidate observations

| Lesson | Status | Notes |
|---|---|---|
| `multi-anchor-comparison-line-on-same-plot` | **NEW 1/3** | v3.50.2 = 1st observation: 同一 PlotView 多个垂直 LineAnnotation (green + blue) 各自独立 anchor, 共享 X 轴但独立 drag, Latest 同步通过分离的 `_anchorTimestampSeconds` + `_blueAnchorTimestampSeconds` 字段 + 独立的 update/delete pass |
| `soft-hide-anchor-via-lineannotation-isvisible` | **NEW 1/3** | v3.50.2 = 1st observation: toggle LineAnnotation 可见性而非 delete-then-recreate, 保留 FrameCount / Latest 状态连续, 隐藏期间 anchor state 仍同步更新, show 时立刻显回 |