# Release Notes v3.34.0 — TraceViewerViewModel god-class RESIDUAL refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.34.0
**Branch:** `feature/w20-trace-viewer-view-model-residual`
**Parent:** v3.33.0 MINOR (`d45959b` on origin/main + `016a04e` capture-decisions followup)

## Why this MINOR

`src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` had grown to **686 LoC** as of v3.33.0 — at **85.8% of the 800 LoC Round-1 ceiling** (the highest pre-split ratio of any W3-W20 god-class refactor target). Despite 6 existing partial-class files (Lifecycle/Session/Signal/Source/Transport/Watch — all created prior to W20), the main file residual still held 7 propagation helpers (~114 LoC), 3 chart-series helpers (~220 LoC dominated by `BuildOneChartSeriesForSource` 128 LoC), and 2 nested null helper classes (~30 LoC).

This is the **16th god-class refactor** in the project (W3-W20 series). **1st RESIDUAL split** — the class had already been partially refactored with 6 existing partials prior to W20, and W20 extracts the remaining 3 logical clusters.

## LoC trajectory (W8.5 D7 CONFIRMED formula — now 23-locked)

Per task, `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`. All 3 transitions **EXACT match** (within W13 T1 2/3 loose-assertion ±1 LoC tolerance).

| Task | Flow | Range deleted | LoC out | Main after |
|---|---|---|---|---|
| T1 | PlaybackFlow (ClearCanIdFilter + 6 propagation helpers) | 267-370 | 104 | 583 |
| T2 | ChartSeriesFlow (BuildOneChartSeriesForSource 128 LoC + FormatCanIdHex + PlotSignal) | 305-525 | 221 | 363 |
| T3 | NullAscServices (2 internal sealed helper classes) | 334-363 | 30 | 335 |
| **Total** | -- | -- | **355** | **335** |

**Net**: 686 → 335 LoC main file (**-351 LoC, -51.2%**). Total project LoC across main + 8 partials (6 existing + 2 new) + 1 helper file ≈ 1370 LoC (small +5 LoC overhead from per-file namespace + using directives).

## What this MINOR does

### Refactor — TraceViewerViewModel adds 2 NEW partials + 1 NEW standalone helper file

The class was already `public sealed partial class TraceViewerViewModel : ObservableObject, IDisposable` at line 43 (modifier pre-existed for future split, 10th confirmation of `outer-modifier-pre-applied` lesson cluster per W19 D1). Main file keeps: 18 readonly fields + 4 mutable fields + ctor + Reset + HasSources + Dispose + `BuildChartSeries` `[Obsolete]` no-op stub (kept verbatim in main per W20 D3 — extracting a 6-LoC deprecated entry point is net-negative).

**Files created**:

| File | Flow | LoC | Methods |
|---|---|---|---|
| `TraceViewerViewModel/PlaybackFlow.cs` | A — PlaybackFlow | ~104 | `ClearCanIdFilter` `[RelayCommand]` + `PropagateLoopToAllServices` + `PropagateSpeedToAllServices` + `DetachAllSourcePropertyHandlers` + `OnAnySourcePropertyChanged` + `SeekAllToProportionalTime` + `RebindMasterFromRegistry` (7 playback-propagation helpers) |
| `TraceViewerViewModel/ChartSeriesFlow.cs` | B — ChartSeriesFlow | ~221 | `BuildOneChartSeriesForSource` (128 LoC, **LARGEST single method, stays inline per W12 D7 + W14 D8 + W18 D5 + W19 D5 + W20 D5 sister**) + `FormatCanIdHex` + `PlotSignal` (3 chart-series helpers) |
| `Helpers/NullAscServices.cs` | C — NullAscServices | ~30 | `internal sealed class NullAscContentHasher : IAscContentHasher` + `internal sealed class NullAscLocator : IAscLocator` (2 no-op singleton helpers; moved to top-level Helpers/ subdirectory since they are NOT partial-class members) |

### Verification

- `dotnet build src/PeakCan.Host.App/`: **0 errors, 0 warnings** (after W20 T1 R1 fix + T2 R1 verbatim-extract fix + T3 R6 using-fix)
- `dotnet test --filter "~TraceViewerViewModel"`: **79 / 79 PASS** (10 critical tests: 4 `BuildOneChartSeriesForSource` + 4 `SeekAllToProportionalTime` + 2 propagation + 1 `ClearCanIdFilterCommand_RestoresFrameCount`; 69 adjacent tests)
- `dotnet test` (full solution): **0 new fails** (full re-run unchanged from v3.33.0 baseline)

## Lessons applied

- **W8.5 D7 CONFIRMED** LoC correction formula (**23-locked** across W12-W20) — all 3 transitions EXACT within ±1.
- **W6/W7/W8/W11/W14/W15/W17/W18/W19 sister** subdirectory pattern: **9th subdirectory-pattern deployment** (TraceViewerViewModel/PlaybackFlow.cs + ChartSeriesFlow.cs added). Per W19 T1 capture, this also PROMOTED `subdirectory-partials-pattern-empirical-10-precedents` to **3/3 CONFIRMED**.
- **W12 D7 + W14 D8 + W18 D5 + W19 D5 + W20 D5** sister: largest-method stays inline — `BuildOneChartSeriesForSource` 128 LoC (a single linear pipeline: filter resolution → frame fetch + sort → decode loop → PlotModel + bottomAxis + WallClockOrigin LabelFormatter → left axis + DataPoints → playback-cursor LineAnnotation → return `TraceChartSeries`) stays verbatim, NOT extracted further.
- **W20 D3 NEW**: obsolete no-op stub stays in main — `BuildChartSeries` (6 LoC, `[Obsolete]` since v3.14.3 PATCH, body = comment + empty body) stays in main because extracting it to ChartSeriesFlow.cs is net-negative (1 extra file for a deprecated entry point).
- **W14 R5 sister**: native-binding helpers stay together (W14 V8 binding) → W20 chart-series helpers stay together (ChartSeriesFlow isolation pattern).
- **W13 T1 2/3 loose-assertion** + **W17 wc-l-splitlines CONFIRMED** deletion-script pattern applied at all 3 scripts.
- **W19 R1 first-correction** applied (re-grep boundaries before each deletion).
- **W11 R1 fix pattern** sister: cross-file type references need explicit using (`using PeakCan.Host.App.Services.Trace;` for `TraceSource`, `using PeakCan.Host.Core.Replay;` for `CanIdListParser`, `using PeakCan.Host.App.Helpers;` for `NullAscContentHasher.Instance` after T3).

## CRITICAL LESSON — W20 T2 R1 fabrication

**My first Write of `ChartSeriesFlow.cs` FABRICATED PlotModel API and TraceChartSeries API** — invented `PlotModel.LegendPosition`, `PlotModel.LegendPlacement`, `LineSeries.MarkerFill/MarkerStroke`, `TraceChartSeries.LookupId/IdHex/SignalName` — ALL WRONG. Build failed with 15+ CS0103/CS1061 errors.

**Fix**: re-extracted original code from HEAD via `git show HEAD:src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs | sed -n '305,525p'` and rewrote `ChartSeriesFlow.cs` **VERBATIM**.

**NEW 1/3 lesson candidate**: `partial-extraction-must-use-original-code-from-head-not-fabricated-api` — W20 T2 first observation in W3-W20 series. Sister of `pasted-from-fabricated-context-doesnt-compile` lesson cluster. Held at 1/3; awaits 2 more observations.

## New sister-lesson candidates

| Lesson | Status | Observation |
|---|---|---|
| `partial-extraction-must-use-original-code-from-head-not-fabricated-api` | NEW 1/3 | W20 T2 1st observation (ChartSeriesFlow.cs fabrication → 15+ build errors → re-extract verbatim from HEAD) |
| `cross-partial-type-reference-needs-explicit-using-even-if-main-uses-it` | NEW 1/3 (W20 T1) | W20 T1: `using PeakCan.Host.App.Services.Trace;` needed for `TraceSource.CanIdFilter`; W20 T3 promoted to 2/3 via `using PeakCan.Host.App.Helpers;` for `NullAscContentHasher.Instance` |
| `subdirectory-partials-pattern-empirical-10-precedents` | 2/3 → **3/3 CONFIRMED** | W18 + W19 + W20 = 10th subdirectory deployment |
| `build-one-chart-series-for-source-largest-method-stays-inline-128-loc` | NEW 1/3 | W20 D5 + W19 D5 + W18 D5 + W14 D8 + W12 D7 sister: largest-method stays inline even at 128 LoC |
| `obsolete-no-op-stub-stays-in-main-on-residual-split` | NEW 1/3 | W20 D3 1st observation: `BuildChartSeries` 6-LoC no-op stub stays in main |
| `playback-helpers-cluster-stays-together-across-partials` | NEW 1/3 | W20 T1 1st observation: 7 playback-related propagation methods cluster together in PlaybackFlow (sister of W19 A dispatcher-cluster) |
| `partial-class-with-private-static-LoggerMessage-cross-partial-compiles-clean` | NEW 1/3 | W20 1st observation: confirms peakcan-host convention via Phase 1 explore of 5 existing `[LoggerMessage]` partials (no R1 mitigation needed) |
| `[ObservableProperty]-source-generator-partial-scope-bounded-by-main-file` | 2/3 (W16 + W19) | Held (no new `[ObservableProperty]` extraction in W20) |

## What stays the same

- Public API surface — all `[ObservableProperty]` generated properties + `[RelayCommand]` generated commands + 6 existing partials + new 2 partials unchanged.
- Test count unchanged (10 critical TraceViewerViewModel tests + 69 adjacent + 5 SKIP hardware-dependent; 0 new fails).
- WPF binding contract unchanged (no XAML changes needed).
- DI registration unchanged (`AddSingleton<TraceViewerViewModel>()` in AppHostBuilder preserved).
- `BuildChartSeries` no-op stub stays accessible at `TraceViewerViewModel.BuildChartSeries(...)` for legacy callers (deprecated since v3.14.3 PATCH).

## Next steps (post-ship)

- **W20.5 vault-only PATCH** — lesson-promotion opportunity (NEW 1/3 candidates await 2 more observations).
- **W21** — next god-class refactor candidate. Top remaining (>350 LoC) main files: `AppShellViewModel.cs` 353 LoC (App layer), `CyclicDbcSendService.cs` 383 LoC (App/Services), `RecordService.cs` 375 LoC (App/Services), `ChannelRouter.cs` 305 LoC (Infrastructure/Channel sister of W18).