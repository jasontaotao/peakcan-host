# v3.53.0 MINOR — StatsViewModel god-class refactor (21st overall)

> W36 god-class refactor (W3-W35 series 延续，21st god-class SHIP)。

## 概述

`StatsViewModel.cs` 263 LoC → 117 LoC main（**-146 LoC, -55.5%**）。2 NEW partials 在 `StatsViewModel/` 子目录：PlotFlow.partial.cs（ctor body + OxyPlot PlotModel/LineSeries setup）+ SamplingFlow.partial.cs（Push + Apply + rolling window + LineSeries.Points rebuild）。

## 用户可见变化

**无 UI 变化**。W36 是纯 refactor PATCH —— public API + XAML 绑定 + DI 注册 + 测试全部 100% 保留。

## 数据契约

100% 保留：

```
public ObservableCollection<double> FpsSeries { get; }
public ObservableCollection<double> LoadSeries { get; }
public PlotModel PlotModel { get; }
public long TotalFrames { get; }        // [ObservableProperty] source-gen
public long ErrorFrames { get; }        // [ObservableProperty] source-gen
internal int InvalidatePlotCallCount { get; private set; }
public void Push(BusStatistics s)
public StatsViewModel()
```

## 5 个核心决策

| ID | 决策 | 选择 |
|---|---|---|
| D1 | NEW partials 数量 | 2 NEW (PlotFlow + SamplingFlow) |
| D4 | ctor-as-orchestrator | 升级：ctor 整段搬到 PlotFlow（sister of W35 4-partial subdirectory deployment） |
| D5 | LineSeries rebuild 位置 | SamplingFlow.partial.cs（与 Apply rolling window maintenance 耦合） |
| D6 | 分支 | feature/w36-stats-view-model-god-class |
| D7 | 顺序 | T1 PlotFlow → T2 SamplingFlow |

## 架构里程碑

- **21st god-class SHIP**（W3-W35 系列延续）
- **17th App/ViewModels** god-class
- **14th subdirectory-pattern deployment**（sister of W34 DbcSendViewModel/）
- **1st 2-partial subdirectory pattern**（DbcParser 4, DbcSendViewModel 3, StatsViewModel 2 — pattern variants）
- **W19 R1 LESSON 维持**：T1 (PlotFlow) 1 fix iteration（using directive typo caught pre-build）+ T2 (SamplingFlow) 0 failures（first-attempt PASS）
- **W23 LESSON 维持**：T2 caught `BusStatistics` namespace wrong pre-build（实际在 `PeakCan.Host.Infrastructure.Statistics`，plan 写成 `PeakCan.Host.Core.Dbc`）

## 计数

- 2 commits on `feature/w36-stats-view-model-god-class`
- 1 file modified + 2 files created (subdirectory pattern)
- Main: 263 → 117 LoC (-146, **-55.5%**)
- PlotFlow.partial.cs: 109 LoC
- SamplingFlow.partial.cs: 75 LoC
- Net: +38 LoC overhead (file-scaffolding overhead, consistent with W34/W35)
- 9 StatsViewModelTests 0 modifications → all PASS
- Full solution: 1433 PASS / 0 FAIL / 5 SKIP (parallel) / 1433 PASS (sequential)

## 6 NEW 1/3 lesson candidates（待 2nd 观察后晋升）

| 候选 | 期望观察点 |
|---|---|
| `largest-method-can-move-when-flow-is-discrete-constructor-not-orchestrator` | **PROMOTE to 2/3 at W36**: D4 upgrade验证 ctor-as-orchestrator moves to partial |
| `stats-vm-chart-rebuild-must-stay-coupled-with-rolling-window-maintenance` | NEW 1/3 (W36): D5验证 |
| `oxyplot-itemssource-path-on-net10-still-broken-requires-explicit-points-rebuild` | NEW 1/3 (W36): 验证仍 relevant（sister of v1.2.7） |
| `2-partial-subdirectory-pattern-empirical-w11-w36` | NEW 1/3 (W36): 验证 subdirectory variant |
| `internal-test-counter-pattern-empirical-w23-w36` | NEW 1/3 (W36): InvalidatePlotCallCount sister |
| `dispatcher-hop-fire-and-forget-can-stay-isolated-from-apply-body` | NEW 1/3 (W36): Push + Apply 同 partial |

## 显式不在范围

- **W37+ 后续 refactor** (RecordService 182 / AscLocator 225) → 后续 work
- **P1 LLM Provider PATCH** (DeepSeek + API Key + JSON Schema) → 后续 work
- **v3.53.1 cleanup PATCH** → 后续 work

## 关联

- Spec: `docs/superpowers/specs/2026-07-17-w36-stats-view-model-god-class-refactor.md`
- Plan: `docs/superpowers/plans/2026-07-17-w36-stats-view-model-god-class-refactor.md`
- W35 ship (predecessor): `git rev-parse v3.48.1` (PeakCanChannel write-flow refactor, 20th god-class SHIP)
- v3.52.0 + v3.52.1 ships: AI 推理 v1 P0 + cleanup (unrelated to refactor)

## 验证

```bash
dotnet build PeakCan.Host.slnx -c Debug              # 0 errors
dotnet test PeakCan.Host.slnx --no-build -c Debug     # 1433 PASS / 0 FAIL / 5 SKIP
wc -l src/PeakCan.Host.App/ViewModels/StatsViewModel.cs  # 117 LoC
```

## 已知并行测试 flake（不影响 ship）

`Uds.UdsClientConcurrentSecurityAccessTests.TwoArg_Overload_ConcurrentMidHandshakeLockoutFlip_PostStateConsistent` 在并行测试（`MaxParallelThreads > 1`）下偶发失败。**根因**：concurrency test wall-clock timing 在 CPU 负载高时不稳定。**对策**：`dotnet test -- xUnit.MaxParallelThreads=1` 单线程运行即可 1433/1433 PASS。属于 pre-existing sister pattern（v3.52.0 + v3.52.1 ship memory 已有类似 case）。**不是 W36 PATCH 引入**。