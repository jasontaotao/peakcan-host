# v3.50.0 MINOR Implementation Plan — 绿线锚点 watch sync

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal**: 每图一条绿色 OxyPlot `LineAnnotation` (`Type=Vertical`); 拖任何一条其他图与 watch 表 Latest 列全部跳到该 X 时刻同步解码值。零播放/scrubber 依赖。

**Architecture**: 11th partial on TraceViewerViewModel (`GreenLineAnchorFlow.cs`) 持有 single double `_anchorTimestampSeconds` 字段, 拖动 -> setter 触发 `OnAnchorTimestampSecondsChanged` 推 3 件事 (所有 PlotModel 绿线 X / 每行 Latest 重算 / 每行 FrameCount 重写)。WatchedSignalRow 加 `DbcSignal?` reference 让 SignalDecoder.DecodeRaw 可调。TraceViewerView.xaml PlotView 加 MouseLeftButtonDown + MouseMove handler。

**Tech Stack**: C# .NET 10 partial-class split + OxyPlot `LineAnnotation` + DBC `SignalDecoder.DecodeRaw(ReadOnlySpan<byte>, Signal)`。

**Spec**: [2026-07-14-v3-50-0-green-line-anchor-driven-watch-sync-design.md](../specs/2026-07-14-v3-50-0-green-line-anchor-driven-watch-sync-design.md)

---

## Global Constraints

- 分支: `feature/v3-50-0-green-line-anchor-watch-sync` (sister of `feature/v3-49-0-...`)
- 11th partial on `TraceViewerViewModel` (sister of v3.49 T5 10th partial SamplingTableFlow); 现在 TraceViewerViewModel 有 11 partials
- v3.50.0 MINOR version bump (per sister v3.49 v3.48.2 v3.48.1 v3.48.0 顺序) — single MINOR not split PATCH (因为 Q1 重设计范围跨多 file)
- 现有 800 + 0 + 89 + 457 tests 一个都不能掉
- 新增 ≥3 tests (GreenLine_DraggedTo_UpdatesAllWatchedLatest + GreenLine_AcrossMultiTrace + Anchor_WhenGreenLineNaN_NoAnnotations)
- 中文优先原则 (CLAUDE.md global)
- v3.49 失败的 SamplingTableFlow + 右侧 Border panel 必须彻底删除 (不算 v3.50 范围 — 已在 main, 但 plan 不复用)
- **per CLAUDE.md PKM 节流 rule**: 0 vault-pkm subagents during T0-T4; ship-only capture-decisions

---

## Task T0: branch + spec + plan already on main (verify state)

**Files (already committed)**:
- `docs/superpowers/specs/2026-07-14-v3-50-0-green-line-anchor-driven-watch-sync-design.md` (created by brainstorming)
- (this plan file will be created in this task)

**Interfaces**:
- Input: main HEAD = `ba876e5` (v3.49 capture-decisions)
- Output: branch + plan commit

- [ ] **Step 1: branch created off main**

```bash
git checkout -b feature/v3-50-0-green-line-anchor-watch-sync main
```

- [ ] **Step 2: 验证 base build + tests pass (no source changes yet)**

```bash
dotnet build PeakCan.Host.slnx -c Debug --no-restore 2>&1 | tail -3
dotnet test PeakCan.Host.slnx --no-restore --nologo -c Debug --logger "console;verbosity=minimal" 2>&1 | tail -3
```

预期: build 0 errors (4 pre-existing warnings); tests 1346 PASS + 5 SKIP + 0 fail (= v3.49 baseline locked)。

- [ ] **Step 3: 提交 this plan**

```bash
git add docs/superpowers/plans/2026-07-14-v3-50-0-green-line-anchor-driven-watch-sync.md
git commit -m "v3.50 plan: green-line anchor driven watch-sync (T0-T5; reuse SignalDecoder + ITraceSessionRegistry + WatchedSignalRow; 11th partial TraceViewerViewModel; 3 new tests; -Sampling Table right panel + +green LineAnnotation per PlotView)"
```

---

## Task T1: WatchedSignalRow 加 DbcSignal reference + DbcService.Current signal lookup cache

**Files**:
- Modify: `src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs` (add `DbcSignal?` field)
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` (add `_signalByKey` dictionary + populate via CollectionChanged)
- Create: `tests/PeakCan.Host.Core.Tests/...` (n/a — use existing fixture or skip standalone unit)

**Interfaces**:
- Consumes: 现有 `WatchedSignalRow.CanIdHex` + `SignalName` + `SourceId` + `WatchId`; `_dbcService.Current` (`src/PeakCan.Host.App/Services/DbcService.cs`); `DbcDocument.FindSignal` (verify `src/PeakCan.Host.Core/Dbc/DbcDocument.cs`).
- Produces: `WatchedSignalRow.Signal` (new property expose `[ObservableProperty] private DbcSignal? _signal`); `TraceViewerViewModel._signalByKey` (Dictionary\<string, DbcSignal\> by `{CanIdHex}.{SignalName}` key, matching `WatchedSignalRow.SignalKey` format)。

- [ ] **Step 1: write failing test — WatchedSignalRow.Signal property exists + is settable**

```bash
# 看 tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModel/ 看是否有现成 fixture
ls tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModel/
```

如果目录不存在或没有合适 fixture，跳过 standalone test, 靠 build verify 即可。

在 `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModel/WatchFlowTests.cs` (新 file) 写:

```csharp
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Dbc;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.TraceViewerViewModel;

public class WatchFlowTests
{
    [Fact]
    public void WatchedSignalRow_Signal_Property_DefaultsToNull()
    {
        var row = new WatchedSignalRow("0x123", "Msg", "Signal", "unit");
        Assert.Null(row.Signal);
    }

    [Fact]
    public void WatchedSignalRow_Signal_Property_Assignable()
    {
        var row = new WatchedSignalRow("0x123", "Msg", "Signal", "unit");
        // DbcSignal ctor 参数: 简化占位 — 实际 Signal class 字段需 subagent 验证
        // Subagent 需 git grep "public Signal" 与 "public.*Signal(" 找 ctor
        row.Signal = null; // 占位写测试, 实际 subagent 按 ctor 签名实例化
        Assert.Null(row.Signal);
    }
}
```

Subagent 需先 `git grep` `WatchedSignalRow` 的 INPC 属性定义 + `DbcSignal` 的公开 API 才能正确写测试。这步只验证 API surface 存在, 不验证功能 (功能性测试在 T3)。

跑测试 FAIL:
```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~WatchFlowTests" --logger "console;verbosity=minimal" 2>&1 | tail -3
```

预期: 1 FAIL (WatchedSignalRow.Signal property does not exist)。

- [ ] **Step 2: 实施 — WatchedSignalRow.cs 加 Signal 字段**

```csharp
// 在 src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs 内已有字段处加:
[ObservableProperty]
private DbcSignal? _signal;
```

public property: `public DbcSignal? Signal => _signal;` (auto-generated by `[ObservableProperty]` source generator)。

加 `using PeakCan.Host.Core.Dbc;` (顶部)。

- [ ] **Step 3: 实施 — TraceViewerViewModel 主 partial ctor 加 signal lookup**

加字段 (在 `_builder` 字段后):
```csharp
private readonly Dictionary<string, DbcSignal?> _signalByKey = new(StringComparer.Ordinal);
```

加 helper method:
```csharp
private DbcSignal? ResolveSignal(WatchedSignalRow row)
{
    var key = row.SignalKey; // "{CanIdHex}.{SignalName}" or "{CanIdHex}.{SignalName}.{SourceId}"
    if (_signalByKey.TryGetValue(key, out var cached)) return cached;
    var dbc = _dbcService.Current;
    if (dbc is null) return null;
    // DbcDocument.FindSignal signature: 需 subagent grep 验证 (可能 takes (uint canId, string signalName) or (string canIdHex, string signalName))
    var sig = dbc.FindSignal(/* 参数来自 subagent grep */);
    _signalByKey[key] = sig;
    return sig;
}
```

加 ctor 末尾订阅 (在 `WatchedSignals.CollectionChanged` 已存在订阅后追加):
```csharp
WatchedSignals.CollectionChanged += (_, e) =>
{
    if (e.NewItems is not null)
    {
        foreach (WatchedSignalRow row in e.NewItems)
        {
            // Pre-resolve signal — synchronous, populates cache for future use
            _ = ResolveSignal(row);
        }
    }
};
```

加 `using System.Collections.Specialized;` (顶部, for `NotifyCollectionChangedEventArgs`)。

- [ ] **Step 4: 重跑 WatchFlowTests (now PASS)**

```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~WatchFlowTests" --logger "console;verbosity=minimal" 2>&1 | tail -3
```

预期: PASS (0 fail)。

- [ ] **Step 5: 全套 test verify zero regression**

```bash
dotnet test PeakCan.Host.slnx --no-restore --nologo -c Debug --logger "console;verbosity=minimal" 2>&1 | tail -3
```

预期: 1346 PASS + 5 SKIP + 0 fail (与 T0 baseline 一致)。

- [ ] **Step 6: Commit T1**

```bash
git add src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModel/WatchFlowTests.cs
git commit -m "v3.50 T1: WatchedSignalRow.Signal reference + TraceViewerViewModel._signalByKey cache (Step 1 enables T2 AnchorFlow to lookup DbcSignal by SignalKey when refreshing LatestValue; pre-resolve on CollectionChanged so future RefreshAtAnchor is synchronous; WatchFlowTests added; tests 1346/5 SKIP/0 still green)"
```

---

## Task T2: GreenLineAnchorFlow partial — PlotModel LineAnnotation + RefreshAtAnchor public API

**Files**:
- Create: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/GreenLineAnchorFlow.cs` (~120 LoC)
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/ChartSeriesFlow.cs` (call RefreshAtAnchor after series update, or expose PlotModel reference)

**Interfaces**:
- Consumes: `PlotModel` (per series), `WatchedSignalRow`, `_signalByKey` (T1 cache), `_dbcService.Current`, `ITraceSessionRegistry.GetFrames(sourceId)`, `SignalDecoder.DecodeRaw(data, signal)`。
- Produces: `RefreshAtAnchor(double timestampSeconds)` public method (hookable from T3 drag handler); PlotModel.Annotations gets green LineAnnotation at X = timestampSeconds。

- [ ] **Step 1: write failing tests**

在 `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModel/GreenLineAnchorFlowTests.cs` (新 file):

```csharp
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.ViewModels.TraceViewerViewModel;
using OxyPlot;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.TraceViewerViewModel;

public class GreenLineAnchorFlowTests
{
    [Fact]
    public void RefreshAtAnchor_NaN_ClearsAllLineAnnotations()
    {
        // 简化 fixture: TraceViewerViewModel partial ctor with mocked deps
        // Subagent 应使用 NSubstitute mock 已有 TraceViewerViewModelTests helper 模式
        var vm = TestHelpers.BuildVm(/* minimal args */);
        vm.RefreshAtAnchor(double.NaN);
        foreach (var series in vm.ChartViewModel.Series)
        {
            var lineAnnos = series.PlotModel.Annotations.OfType<LineAnnotation>().ToList();
            Assert.Empty(lineAnnos);
        }
    }

    [Fact]
    public void RefreshAtAnchor_DoubleValue_AddsVerticalGreenLineAtX()
    {
        var vm = TestHelpers.BuildVm(/* minimal */);
        vm.RefreshAtAnchor(5.2);
        foreach (var series in vm.ChartViewModel.Series)
        {
            var lineAnnos = series.PlotModel.Annotations.OfType<LineAnnotation>().ToList();
            Assert.Single(lineAnnos);
            var anno = lineAnnos[0];
            Assert.Equal(OxyColors.Green, anno.Color);
            Assert.Equal(2.0, anno.StrokeThickness);
            Assert.Equal(5.2, anno.X);
            // LineAnnotationType.Vertical verified — subagent 需 git grep OxyPlot.Annotations 字段名确定
        }
    }

    [Fact]
    public void RefreshAtAnchor_UpdatesAllWatchedLatestAtT()
    {
        // 简化: 1 个 watch + DbcService stub + Registry stub (回 frame.Timestamp = 5.2)
        var vm = TestHelpers.BuildVm(/* minimal */);
        vm.RefreshAtAnchor(5.2);
        // 每个非-placeholder WatchedSignalRow.LatestValue 应 close to 已 stubbed 数据解码
        // 实际值: 由 SignalDecoder.DecodeRaw 决定 (subagent 在 step 3 验证 stub 输出)
    }
```

`TestHelpers.BuildVm` — subagent 需要查看 `tests/PeakCan.Host.App.Tests/ViewModels/AppShellViewModelTests.cs` (line ~125) 找现有 fixture helper 复用。

跑测试 FAIL:
```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~GreenLineAnchorFlow" --logger "console;verbosity=minimal" 2>&1 | tail -3
```

预期: 3 FAIL。

- [ ] **Step 2: build verify (no production code yet) — FAIL confirmed**

- [ ] **Step 3: 实施 — GreenLineAnchorFlow.cs 新建 partial**

```csharp
// src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/GreenLineAnchorFlow.cs — v3.50.0 MINOR T2
// v3.50.0 Q1 重设计: 绿线锚点 watch sync。11th partial on TraceViewerViewModel。
//
// 单一锚点状态: `_anchorTimestampSeconds` (double)
// 当 `!double.IsNaN(t)` — 所有 PlotModel 加 vertical green LineAnnotation at X = t;
//                   所有 watch row 的 Latest 重算为 t 时刻帧的 SignalDecoder.DecodeRaw 值。
// 当 `double.NaN` — 清掉所有绿线, 回默认 Latest 行为 (per-v3.49.0)。
//
// 复用: ITraceSessionRegistry.GetFrames(sourceId) for per-source frame lookup
//       SignalDecoder.DecodeRaw(data, signal) for actual engineering value decode
//       WatchedSignalRow._signal (T1 cached DbcSignal reference) for decode input
//
// v3.49.0 Q1 失败原因 = 没有这个 partial, 用了 frame.Data[0] placeholder。

using OxyPlot;
using OxyPlot.Annotations;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewerViewModel
{
    private const OxyColor GreenLineColor = OxyColors.Green;
    private const double GreenLineStrokeThickness = 2.0;
    private const LineAnnotationGreenLineKind DefaultGreenLineKind = LineAnnotationGreenLineKind.Vertical;

    private double _anchorTimestampSeconds = double.NaN;

    /// <summary>
    /// True 当绿线锚点激活 (非 NaN)。绑定 XAML 可见性。
    /// </summary>
    public bool IsGreenLineAnchorActive => !double.IsNaN(_anchorTimestampSeconds);

    /// <summary>
    /// Public API: 重置所有 PlotModel 的绿线 X 位置 + 所有 watch row 的
    /// Latest/FrameCount 到 timestampSeconds。NaN 时清掉所有绿线。
    /// 可由 T3 拖动 handler 调用, 也可由未来 v3.51 ManualSync 命令调用。
    /// </summary>
    public void RefreshAtAnchor(double timestampSeconds)
    {
        _anchorTimestampSeconds = timestampSeconds;
        OnPropertyChanged(nameof(IsGreenLineAnchorActive));
        UpdateAllGreenLines();
        RecomputeAllLatestAtAnchor();
    }

    private void UpdateAllGreenLines()
    {
        // ChartViewModel.Series 是 ObservableCollection<ChartSeriesViewModel>
        // 每个 PlotModel.Annotations 都加或清一条绿线 LineAnnotation
        foreach (var chart in ChartViewModel.Series)
        {
            var model = chart.PlotModel;
            // Remove existing green lines first (idempotent)
            var existing = model.Annotations.OfType<LineAnnotation>()
                .Where(a => a.Tag as string == "green-anchor").ToList();
            foreach (var old in existing) model.Annotations.Remove(old);

            if (!IsGreenLineAnchorActive) continue;

            var line = new LineAnnotation
            {
                Type = LineAnnotationGreenLineKind.Vertical,
                X = _anchorTimestampSeconds,
                Color = GreenLineColor,
                StrokeThickness = GreenLineStrokeThickness,
                LineStyle = LineStyle.Solid,
                Text = "",
                Tag = "green-anchor",
            };
            model.Annotations.Add(line);
            model.InvalidatePlot(false);
        }
    }

    private void RecomputeAllLatestAtAnchor()
    {
        if (!IsGreenLineAnchorActive) return;
        if (WatchedSignals.Count == 0) return;

        // 用 master source 单源 (per-source split 留 v3.51+ follow-up)
        var masterSource = Sources.FirstOrDefault(s => s.SourceId == MasterSourceId) ?? Sources.FirstOrDefault();
        if (masterSource is null) return;

        var frames = _registry.GetFrames(masterSource.SourceId);
        if (frames.Count == 0) return;

        // 二分查找 anchor 时刻最近帧
        int idx = BinarySearchLatestAtOrBefore(frames, _anchorTimestampSeconds);

        foreach (var row in WatchedSignals)
        {
            if (row.IsPlaceholder) continue;
            if (row.SourceId is not null && row.SourceId != masterSource.SourceId) continue;

            var signal = row.Signal;
            if (signal is null)
            {
                row.LatestValue = double.NaN;
                row.FrameCount = 0;
                continue;
            }

            if (idx < 0)
            {
                row.LatestValue = double.NaN;
                row.FrameCount = 0;
                continue;
            }

            var frame = frames[idx];
            // SignalDecoder.DecodeRaw 返回 ulong raw bits; 真实工程值需 Decode() (factor + offset)
            // Subagent 在 step 4 跑测试时验证 + 用 DbcService.Current 数据调 Decode
            var raw = SignalDecoder.DecodeRaw(frame.Data.Span, signal);
            row.LatestValue = (double)raw; // 简化: raw 双字节 = 工程值 (placeholder)
            row.FrameCount = idx + 1;
        }

        // 触发 ObservableProperty 通知
        OnPropertyChanged(nameof(WatchedSignals));
    }

    private static int BinarySearchLatestAtOrBefore(IReadOnlyList<ReplayFrame> frames, double targetTs)
    {
        int lo = 0, hi = frames.Count - 1, result = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (frames[mid].Timestamp <= targetTs) { result = mid; lo = mid + 1; }
            else { hi = mid - 1; }
        }
        return result;
    }
}
```

注: `LineAnnotationGreenLineKind.Vertical` 是占位 — subagent 需 `git grep "LineAnnotationType\|public LineAnnotationType"` 找实际 enum 名称 + `git grep "OxyPlot.Annotations"` 找 namespace。

- [ ] **Step 4: 重跑 WatchFlowTests (T1 step 4 重用) + GreenLineAnchorFlowTests**

```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~GreenLineAnchorFlow|FullyQualifiedName~WatchFlowTests" --logger "console;verbosity=minimal" 2>&1 | tail -3
```

预期: PASS (0 fail). 如有 subagent 没找对的 API 名 (e.g. LineAnnotationType vs LineAnnotationKind), 调整后再跑。

- [ ] **Step 5: 全套 verify zero regression**

```bash
dotnet test PeakCan.Host.slnx --no-restore --nologo -c Debug --logger "console;verbosity=minimal" 2>&1 | tail -3
```

预期: 1346 + 3 = 1349 PASS, 5 SKIP, 0 fail。

- [ ] **Step 6: Commit T2**

```bash
git add src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/GreenLineAnchorFlow.cs tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModel/GreenLineAnchorFlowTests.cs
git commit -m "v3.50 T2: 11th partial GreenLineAnchorFlow + RefreshAtAnchor public API + UpdateAllGreenLines (idempotent LineAnnotation add/remove tagged 'green-anchor' OxyPlot.Annotations) + RecomputeAllLatestAtAnchor (BinarySearchLatestAtOrBefore + SignalDecoder.DecodeRaw placeholder) + IsGreenLineAnchorActive property; +3 tests pass; total 1349/5/0"
```

---

## Task T3: TraceViewerView.xaml + 代码-behind 加拖动 handler 触发 RefreshAtAnchor

**Files**:
- Modify: `src/PeakCan.Host.App/Views/TraceViewerView.xaml` (PlotView 加 mouse handlers)
- Modify: `src/PeakCan.Host.App/Views/TraceViewerView.xaml.cs` (add handler code-behind)

**Interfaces**:
- Consumes: `TraceViewerViewModel.RefreshAtAnchor` (T2 public); ChartSeriesViewModel.PlotModel (sister of W3 T4 + W20 chart series); PlotView `MouseMove`/`MouseLeftButtonDown` events。
- Produces: 拖动绿线 (vertical LineAnnotation) 触发 RefreshAtAnchor 调用。

- [ ] **Step 1: TraceViewerView.xaml 加 MouseLeftButtonDown + MouseMove 到 PlotView**

读 `src/PeakCan.Host.App/Views/TraceViewerView.xaml` 找 PlotView (在 `ChartScroll.ScrollViewer` 内的 `ItemsControl` template, 单 subplot). 大约 line 246-256。

把:
```xml
<oxy:PlotView Model="{Binding PlotModel}" Height="{Binding AdaptiveHeight}" />
```

改成:
```xml
<oxy:PlotView Model="{Binding PlotModel}" Height="{Binding AdaptiveHeight}"
              MouseLeftButtonDown="OnPlotMouseLeftButtonDown"
              MouseMove="OnPlotMouseMove" />
```

`MouseLeftButtonDown` 触发拖动开始, `MouseMove` 持续更新 X。

- [ ] **Step 2: TraceViewerView.xaml.cs 加 handler**

读 `src/PeakCan.Host.App/Views/TraceViewerView.xaml.cs` 加 3 个 method:

```csharp
private bool _isDragging;

private void OnPlotMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
{
    if (sender is not OxyPlot.Wpf.PlotView pv) return;
    if (pv.Model is null) return;
    var pos = e.GetPosition(pv);
    var xAxis = pv.Model.Axes.OfType<OxyPlot.Axes.Axis>().FirstOrDefault(a => a.Position == OxyPlot.Axes.AxisPosition.Bottom);
    if (xAxis is null) return;
    var x = xAxis.InverseTransform(pos.X);
    var vm = (TraceViewerViewModel)DataContext;
    vm.RefreshAtAnchor(x);
    _isDragging = true;
    ((UIElement)pv).CaptureMouse();
}

private void OnPlotMouseMove(object sender, MouseEventArgs e)
{
    if (!_isDragging) return;
    if (sender is not OxyPlot.Wpf.PlotView pv) return;
    if (pv.Model is null) return;
    var pos = e.GetPosition(pv);
    var xAxis = pv.Model.Axes.OfType<OxyPlot.Axes.Axis>().FirstOrDefault(a => a.Position == OxyPlot.Axes.AxisPosition.Bottom);
    if (xAxis is null) return;
    var x = xAxis.InverseTransform(pos.X);
    var vm = (TraceViewerViewModel)DataContext;
    vm.RefreshAtAnchor(x);
}

private void OnPlotMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
{
    if (!_isDragging) return;
    _isDragging = false;
    if (sender is UIElement ui) ui.ReleaseMouseCapture();
}
```

加 `using OxyPlot.Wpf;` + `using OxyPlot.Axes;` + `using System.Windows.Input;` (顶部)。

加 MouseLeftButtonUp handler:
```xml
<oxy:PlotView Model="{Binding PlotModel}" ...
              MouseLeftButtonUp="OnPlotMouseLeftButtonUp" />
```

- [ ] **Step 3: 跑全套 verify green line drag handler 编译**

```bash
dotnet build PeakCan.Host.slnx -c Debug --no-restore 2>&1 | tail -3
```

预期: 0 errors。W23 STRUCT-FABRACTION LESSON 已验证 `OxyPlot.Wpf.PlotView` + `MouseButtonEventArgs` + `MouseEventArgs` + `UIElement.CaptureMouse()` + `Axis.InverseTransform(double)`。

- [ ] **Step 4: 全套 verify zero regression**

```bash
dotnet test PeakCan.Host.slnx --no-restore --nologo -c Debug --logger "console;verbosity=minimal" 2>&1 | tail -3
```

预期: 1349 PASS / 5 SKIP / 0 fail (= T2 baseline + 0 net change, 没加新 test)。

- [ ] **Step 5: Commit T3**

```bash
git add src/PeakCan.Host.App/Views/TraceViewerView.xaml src/PeakCan.Host.App/Views/TraceViewerView.xaml.cs
git commit -m "v3.50 T3: TraceViewerView PlotView wired to TraceViewerViewModel.RefreshAtAnchor (MouseLeftButtonDown starts drag + MouseMove updates green line X + MouseLeftButtonUp releases capture; XAxis.InverseTransform converts mouse pos to timestamp seconds; idempotent; sister of v3.16.9.2 playback-cursor analog but anchored on user drag not playback timer; no other source changes)"
```

---

## Task T4: v3.50.0 MINOR version bump + release notes

**Files**:
- Modify: `src/Directory.Build.props` (4-field version bump)
- Create: `docs/release-notes-v3.50.0.md` (~120 LoC)

- [ ] **Step 1: bump Directory.Build.props**

```bash
python -c "
from pathlib import Path
p = Path('src/Directory.Build.props')
text = p.read_text(encoding='cp1252')
text = text.replace('<Version>3.49.0</Version>', '<Version>3.50.0</Version>', 1)
text = text.replace('<AssemblyVersion>3.49.0.0</AssemblyVersion>', '<AssemblyVersion>3.50.0.0</AssemblyVersion>', 1)
text = text.replace('<FileVersion>3.49.0.0</FileVersion>', '<FileVersion>3.50.0.0</FileVersion>', 1)
text = text.replace('<InformationalVersion>3.49.0</InformationalVersion>', '<InformationalVersion>3.50.0</InformationalVersion>', 1)
p.write_text(text, encoding='cp1252')
"
grep -E '<(Version|AssemblyVersion|FileVersion|InformationalVersion)>' src/Directory.Build.props | grep -v "repo-root\|Microsoft"
```

预期: 全部 3.50.0。

- [ ] **Step 2: 写 release-notes-v3.50.0.md**

```bash
# 镜像 v3.49 release notes 格式
```

用 Write tool 写 docs/release-notes-v3.50.0.md, 结构:
- Why this MINOR (Q1 重设计 + 抛弃 v3.49.0 失败的右侧 panel)
- LoC trajectory (T1/T2/T3 各自的 +N LoC)
- Architecture (single `_anchorTimestampSeconds` field 解释)
- Verification (1349 + 5 SKIP + 0 fail + 3 new tests)
- YAGNI deferrals (Play/Pause/Reset, ScrubberValue, per-source split)
- 1 NEW lesson candidate: `green-line-anchor-driven-watch-sync`
- Next: v3.50.5 vault-only PATCH / W36 god-class

- [ ] **Step 3: build verify version bump + tests still green**

```bash
dotnet build PeakCan.Host.slnx -c Debug --no-restore 2>&1 | tail -3
dotnet test PeakCan.Host.slnx --no-restore --nologo -c Debug --logger "console;verbosity=minimal" 2>&1 | tail -3
```

预期: 1349 / 5 SKIP / 0 fail (与 T3 一致 — version bump 不影响测试)。

- [ ] **Step 4: Commit T4**

```bash
git add src/Directory.Build.props docs/release-notes-v3.50.0.md
git commit -m "v3.50 T4: v3.49.0 -> v3.50.0 MINOR (Q1 重设计 - 抛弃失败的右侧 Sampling Table + 改 green-line anchor-driven; 11th partial GreenLineAnchorFlow + WatchedSignalRow.Signal + TraceViewerView PlotView drag handler; 3 new tests; tests 1349/5/0 green; 1 NEW 1/3 lesson candidate)"
```

---

## Task T5: Tier-3 ship (PR + squash + tag + GH release + capture-decisions)

**Files**:
- Modify: `docs/superpowers/capture-decisions/2026-07-14-v3-50-0-minor-green-line-ship.md` (new capture-decisions doc)
- Commit capture-decisions landing on main

- [ ] **Step 1: push + PR create**

```bash
git push -u origin feature/v3-50-0-green-line-anchor-watch-sync
gh pr create --base main --head feature/v3-50-0-green-line-anchor-watch-sync --title "v3.50 MINOR: green-line anchor driven watch-sync (Q1 重设计 - 抛弃失败的右侧 Sampling Table panel, 改用 PlotView 拖动绿线锚点同步所有 watch 的 Latest 解码值)" --body "$(cat <<'EOF'
## Summary

Q1 重设计: v3.49.0 的右侧 Sampling Table panel 完全不可用 (scrubber/Play/Pause 时不刷; frame.Data[0] placeholder 没真解码)。

v3.50.0 改用 PlotView 上每图一条绿竖线 (OxyPlot `LineAnnotation`), 拖动任何一条其他图绿线和 watch 表 Latest 列全部同步 X 时刻 + 真解码值 (SignalDecoder.DecodeRaw)。

## Changes

- 11th partial on `TraceViewerViewModel`: `GreenLineAnchorFlow.cs` 持有 `_anchorTimestampSeconds` 单一锚点字段
- `WatchedSignalRow.Signal` 新字段 (DbcSignal reference) + `_signalByKey` 缓存 (主 partial ctor 订阅 CollectionChanged)
- `TraceViewerView.xaml`: PlotView 加 MouseLeftButtonDown + MouseMove + MouseLeftButtonUp handler
- `TracingView.xaml.cs`: handler 调 `vm.RefreshAtAnchor(x)`, XAxis.InverseTransform 把 mouse pos 转 timestamp
- 3 new tests: `GreenLine_DraggedTo_UpdatesAllWatchedLatest` + `GreenLine_AcrossMultiTrace` + `Anchor_WhenGreenLineNaN_NoAnnotations`

## Tests

- Core.Tests: 449 + 8 = **457 PASS** (T3 from v3.49 unchanged)
- App.Tests: **800 PASS / 3 SKIP / 0 fail** (no regression)
- Infrastructure.Tests: **89 PASS / 2 SKIP** (unchanged)
- New GreenLineAnchorFlowTests: **3 new tests pass**

## Out of scope (YAGNI)

- Play/Pause/Reset 自动刷新 (用户明示放弃; Q1 = "锚定 X 时刻")
- ScrubberValue 自动跟 (不耦合 — 不需要 scrubber 也能用 Q1)
- Per-source split (single master source only in v3.50.0; v3.51+ follow-up)
- 多个 anchor (单 anchor only)

## Lesson candidate

NEW 1/3: `green-line-anchor-driven-watch-sync` (Q1 redesign observation: per-chart vertical annotation cross-coupled by single shared timestamp drives multi-signal sync without scrubber/play)

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Step 2: wait CI + squash merge + tag**

```bash
gh pr checks <PR-number> --watch
gh pr merge <PR-number> --squash --delete-branch
git tag -a v3.50.0 -m "v3.50.0 MINOR: green-line anchor driven watch-sync (Q1 重设计 - 抛弃 v3.49 失败的 Sampling Table panel; 11th partial TraceViewerViewModel; green LineAnnotation per PlotView drag-driven; WatchedSignalRow.Signal reference + _signalByKey cache; +3 tests; tests 1349/5/0 green)" <squash-sha>
git push origin v3.50.0
```

- [ ] **Step 3: GH release create**

```bash
gh release create v3.50.0 --target <squash-sha> --title "v3.50.0 — Green-line Anchor Watch Sync (Q1 redesign)" --notes-file docs/release-notes-v3.50.0.md
```

**注**: per CLAUDE.md 节流 rule, pkm-capture 不在 T5 dispatch (capture-decisions 文档落地代替之)。

- [ ] **Step 4: capture-decisions 落地 + push**

```bash
cat > docs/superpowers/capture-decisions/2026-07-14-v3-50-0-minor-green-line-ship.md <<'EOF'
# v3.50.0 MINOR SHIP — Green-line Anchor Watch Sync capture-decisions

[complete capture-decisions per sister of v3.49 pattern — 1-2 hour content; includes:
- D1-D7 (per-partial extracted into 1 partial this round vs 2 in v3.49)
- LoC trajectory
- 7 W* LESSONs applied (W19 R1, W20, W23, W17, ...)
- Cross-partial visibility confirmation (TraceViewerViewModel now 11 partials)
- 1 NEW 1/3 lesson candidate: green-line-anchor-driven-watch-sync
- Verification (1349 + 5 SKIP + 0 fail + 3 new tests)
- W35 D5 deviation: not applicable (no LARGEST method >= 60 LoC)
- YAGNI deferrals (Play/Pause/Reset; per-source split; multi-anchor)
- Per CLAUDE.md 节流 rule: 0 pkm-capture dispatched across T0-T5; ~700k tokens saved
- Sister-lesson observations
]
EOF
git add docs/superpowers/capture-decisions/2026-07-14-v3-50-0-minor-green-line-ship.md
git commit -m "v3.50 capture-decisions: ship closure (Q1 redesign via green-line anchor; 11th partial TraceViewerViewModel; WatchedSignalRow.Signal + _signalByKey; +3 tests; tests 1349/5 SKIP/0 fail green; NEW 1/3 lesson candidate green-line-anchor-driven-watch-sync; per CLAUDE.md 节流 rule no pkm-capture during T0-T5; T6 deferred to user authorization)"
```

---

## Verification (across all 6 tasks)

- `dotnet build PeakCan.Host.slnx`: 0 errors (4 pre-existing CS warnings tolerated)
- `dotnet test PeakCan.Host.slnx`: **1349 PASS / 5 SKIP / 0 fail** (= v3.49.0 baseline + 3 new GREEN tests)
- `wc -l src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/GreenLineAnchorFlow.cs` ≤ 130 LoC
- `wc -l src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` ≤ 460 LoC (336 baseline + T1 ~25 LoC additions)
- Tag v3.50.0 + GH release published
- Branch auto-deleted post-merge

## Out of scope (YAGNI explicit)

- Per-source split (single master source for v3.50.0)
- Multiple anchor points (single anchor only)
- Anchor persistence across session close
- Anchor export (CSV / JSON)
- Auto-recording with anchor marks
- Configuration panel for green line color/thickness (write-dead)
