# W36 god-class refactor — StatsViewModel (21st overall)

> 状态：W36 设计 spec（v3.52.x 系列 refactor 节奏延续）
>
> 触发条件：W24 DbcSendViewModel (shipped `32d14d5`, 20th) → 下一个 god-class 候选。W3-W34 系列累计 32 god-class ship。StatsViewModel 是 21st 候选。

## 目标

`src/PeakCan.Host.App/ViewModels/StatsViewModel.cs` 263 LoC → 拆分为 `StatsViewModel/` 子目录 + 2 NEW partials + main 保留 ~80 LoC。Public API + 测试 + DI 全部不变。

## 当前代码证据（已坐实）

- `src/PeakCan.Host.App/ViewModels/StatsViewModel.cs:263` — `public sealed partial class StatsViewModel : ObservableObject` (line 47)
- 6 readonly fields (`FpsSeries` + `LoadSeries` + `PlotModel` + `_fpsLine` + `_loadLine` + `InvalidatePlotCallCount`)
- 2 `[ObservableProperty]` backing fields (`_totalFrames` + `_errorFrames`)
- 1 const (`MaxPoints`)
- 3 methods:
  - `public StatsViewModel()` (~85 LoC, LARGEST method) — OxyPlot PlotModel + 2 LineSeries + annotations
  - `public void Push(BusStatistics s)` (~8 LoC) — dispatcher hop
  - `private void Apply(BusStatistics s)` (~35 LoC) — rolling window + LineSeries.Points rebuild
- 1 internal property (`InvalidatePlotCallCount`)
- DI 注册：`src/PeakCan.Host.App/Composition/AppHostBuilder/ViewModelsBatch2Flow.cs:181` — `services.AddSingleton<StatsViewModel>()`
- DI 消费：`src/PeakCan.Host.App/Composition/AppHostBuilder.cs:291` — `sp.GetRequiredService<StatsViewModel>()`

## D 决策（D1-D5）

| ID | 决策 | 选择 | 理由 |
|---|---|---|---|
| D1 | NEW partials 数量 | **2 NEW** (`PlotFlow.partial.cs` + `SamplingFlow.partial.cs`) | ctor 主体（PlotModel/LineSeries/annotations）归 PlotFlow；Push + Apply（采样 + 滚动窗口 + LineSeries rebuild）归 SamplingFlow。2 vs 3 平衡：避免 subdir 碎片化（W11 sister DbcParser 用 4 partials 是因为职责更分散）。 |
| D2 | Partial keyword | 已有（L47 `public sealed partial class`）—— 不改 | W19 R1 sister，无需加 keyword。 |
| D3 | 留在 main 的成员 | 4 readonly fields（FpsSeries + LoadSeries + PlotModel + InvalidatePlotCallCount）+ 2 [ObservableProperty] backing fields + 1 const + class declaration | 与 W19 + W23 sister：bindable properties + state stays in main。 |
| D4 | LARGEST method ctor | 留在 PlotFlow.partial.cs（per W12/W14/W18/W19/W20/W21/W22/W23/W24/W25/W26/W27/W28/W29/W30/W31/W32/W33/W34/W35 sister）—— "ctor body = DI wiring boilerplate, not extractable" | 但本次 ctor 是 VM 主逻辑（OxyPlot setup），不是单纯 DI wiring。**升级**：ctor 整段搬到 PlotFlow partial，符合 ctor 主逻辑 = partial extractable sister（与 W30 SendFrameLibrary 类似）。 |
| D5 | LineSeries rebuild 位置 | 移到 SamplingFlow.partial.cs（与 Apply 绑定） | Apply 的核心职责是「保持滚动窗口 + 同步 LineSeries」。两件事不能分。 |
| D6 | 分支 | `feature/w36-stats-view-model-god-class` | sister of W11-W35。 |
| D7 | 顺序 | T1 PlotFlow → T2 SamplingFlow | PlotFlow 包含 PlotModel 基础；SamplingFlow 依赖 PlotModel + LineSeries。先 T1 验证 ctor 可独立工作。 |

## LoC trajectory（公式 W8.5 D7 32-locked）

| Task | Flow | Range (TBD per Phase 1) | LoC deleted | Marker | LoC main after |
|---|---|---|---|---|---|
| T1 | A — PlotFlow | L120-205 (ctor body + PlotModel setup) | ~85 | 1 | ~178 |
| T2 | B — SamplingFlow | L213-262 (Push + Apply) | ~50 | 1 | ~128 |
| T3 | release + ship | (no source) | 0 | 0 | ~128 |
| T4 | ship | -- | -- | -- | ~128 |

**目标**：main 263 → ~128 LoC（-135 LoC, -51%）。Subdirectory 累计 2 NEW partials + main = 132 LoC distributed across 3 files（sister of W35 4-partial subdirectory deployment）。

## 架构

```text
src/PeakCan.Host.App/ViewModels/
├── StatsViewModel.cs                       (NEW: ~128 LoC, was 263 LoC)
└── StatsViewModel/                         (NEW subdirectory, sister of W34 DbcSendViewModel/)
    ├── PlotFlow.partial.cs                 (NEW: ~85 LoC, ctor body + PlotModel setup)
    └── SamplingFlow.partial.cs             (NEW: ~50 LoC, Push + Apply)
```

## W20 + W23 LESSON APPLIED

Per W20 T2 R1 fabrication incident + W23 T2 3-fix-cycle struct-constructor fabrication:

1. **Re-grep boundaries BEFORE running each deletion script** (re-verify before each T1/T2 script run).
2. **Re-extract original code from HEAD via `git show HEAD:src/...cs | sed -n '<range>p'`** for each partial's verbatim content.
3. **Verify OxyPlot types** (`PlotModel`, `LineSeries`, `DataPoint`, `OxyColor`, `LegendPosition`) — sister pattern: W19 partial extraction.
4. Verify `BusStatistics` struct (Core, immutable record) properties used in Apply: `s.TotalFrames`, `s.ErrorFrames`, `s.FramesPerSecond`, `s.BusLoadPercent`.

## 组件拆分（part 文件位置）

**Main stays** (~128 LoC):
- using block (1-3) + namespace + class xmldoc (5-46) + outer class declaration (47)
- 1 const (`MaxPoints` L54)
- 4 readonly public properties (`FpsSeries` L61 + `LoadSeries` L67 + `PlotModel` L93 + `InvalidatePlotCallCount` L104)
- 2 [ObservableProperty] backing fields (`_totalFrames` L74-75 + `_errorFrames` L81-82)
- 2 private readonly LineSeries fields (`_fpsLine` L115 + `_loadLine` L116)

**PlotFlow.partial.cs** (~85 LoC):
- public StatsViewModel() ctor body (L120-205) — full OxyPlot PlotModel + 2 LineSeries + annotations

**SamplingFlow.partial.cs** (~50 LoC):
- public void Push(BusStatistics s) (L213-220)
- private void Apply(BusStatistics s) (L228-262)

## 数据契约

不引入新 record / interface。Public API 100% 保留：
- `public ObservableCollection<double> FpsSeries { get; }`
- `public ObservableCollection<double> LoadSeries { get; }`
- `public PlotModel PlotModel { get; }`
- `internal int InvalidatePlotCallCount { get; private set; }`
- `[ObservableProperty] private long _totalFrames` → `public long TotalFrames`
- `[ObservableProperty] private long _errorFrames` → `public long ErrorFrames`
- `public void Push(BusStatistics s)`
- `public StatsViewModel()`

## 错误处理

不引入新错误处理路径。ctor 体内的 OxyPlot setup 是 deterministic 的；Push + Apply 是 fire-and-forget timer thread → UI hop。

## 测试策略（TDD RED → GREEN → IMPROVE）

- 现有 `StatsViewModelTests` 测试集（InheritPlotCallCount / Push / Apply / rolling window / dispatcher / etc.）必须全部保留 + PASS。
- 全套 1433 PASS / 0 FAIL / 5 SKIP（v3.52.1 ship baseline）不变。
- 不改 tests 文件。
- 不引入新 tests（refactor PATCH 通常零 test change）。

## 复评/不做项

- **StatsViewModel 子目录 vs 同目录**：明确用子目录（sister of W34 DbcSendViewModel/）。
- **OxyPlot DI 注入 vs ctor 内构造**：明确不 DI PlotModel（sister of SignalChartViewModel/W34）。
- **分更多 partials**（如 LegendFlow / AxisFlow）：明确不做（YAGNI；2 partials 足够）。
- **W37+ 后续 refactor**（RecordService 182 / AscLocator 225）：明确推到后续。

## 6 NEW 1/3 lesson candidates 观察

| 候选 | 期望观察点 |
|---|---|
| `largest-method-can-move-when-flow-is-discrete-constructor-not-orchestrator` | W36 1st observation: ctor 整段搬到 PlotFlow（sister of W25/W35 sister but with ctor-as-orchestrator twist） |
| `stats-vm-chart-rebuild-must-stay-coupled-with-rolling-window-maintenance` | W36 1st observation: LineSeries.Points rebuild 和 FpsSeries.Add/RemoveAt 不能分到不同 partial |
| `oxyplot-itemssource-path-on-net10-still-broken-requires-explicit-points-rebuild` | NEW at W36 (sister of v1.2.7 LESSON)：pre-existing 已记，W36 验证仍 relevant |
| `2-partial-subdirectory-pattern-empirical-w11-w36` | NEW at W36: DbcParser 用 4 partials, DbcSendViewModel 用 3, StatsViewModel 用 2 — pattern 的 variants |
| `internal-test-counter-pattern-empirical-w23-w36` | NEW at W36: InvalidatePlotCallCount 与 FilterRebuildCount/DrainCount sister |
| `dispatcher-hop-fire-and-forget-can-stay-isolated-from-apply-body` | NEW at W36: Push (dispatcher hop) 与 Apply (UI thread body) 同 partial 但职责清晰 |

## 文件 / LoC 估算

| 文件 | LoC |
|---|---|
| `src/PeakCan.Host.App/ViewModels/StatsViewModel.cs` | ~128 (was 263) |
| `src/PeakCan.Host.App/ViewModels/StatsViewModel/PlotFlow.partial.cs` | ~85 (NEW) |
| `src/PeakCan.Host.App/ViewModels/StatsViewModel/SamplingFlow.partial.cs` | ~50 (NEW) |
| **总计** | **~263** (no net change; redistributed) |

## 待 SPEC 用户复核

本文为 spec 初稿。请 review 后批准，下一步进入 writing-plans 写实施计划。