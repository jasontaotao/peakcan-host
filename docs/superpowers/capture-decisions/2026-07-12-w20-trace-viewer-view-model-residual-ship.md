# W20 v3.34.0 SHIP — TraceViewerViewModel god-class RESIDUAL refactor capture-decisions

**Branch**: `feature/w20-trace-viewer-view-model-residual`
**Parent**: v3.33.0 MINOR (`d45959b` on `main`) + W19 capture-decisions (`016a04e`)
**Ship commit**: `a25a903` on `main` (squash-merged via PR #46)
**Tag**: `v3.34.0` annotated at `a25a903`
**GH release**: https://github.com/jasontaotao/peakcan-host/releases/tag/v3.34.0
**Branch**: auto-deleted by `gh pr merge --squash --delete-branch`

## D1-D8 (carried from W20 SPEC)

- **D1**: 2 NEW partials (`PlaybackFlow` + `ChartSeriesFlow`) in `TraceViewerViewModel/` + 1 standalone helper file (`Helpers/NullAscServices.cs`).
- **D2**: 18 readonly fields + 4 mutable fields + ctor + Reset + HasSources + Dispose stay in main.
- **D3**: `BuildChartSeries` no-op stub stays in main (deprecated entry point, 6 LoC).
- **D4**: Null helper classes move to top-level `Helpers/NullAscServices.cs` (NOT TraceViewerViewModel subdirectory).
- **D5**: `BuildOneChartSeriesForSource` 128 LoC stays inline per W12/W14/W18/W19 D5 sister.
- **D6**: Branch name `feature/w20-trace-viewer-view-model-residual`.
- **D7**: Order A → B → C (largest-first per sister pattern).
- **D8**: W18 R1 mitigation NOT applied — peakcan-host convention uses `private static partial` (verified by Phase 1 explore of 5 existing `[LoggerMessage]` partials).

## 6 source commits (squash-collapsed into PR #46)

1. `2d611ce` — W20 SPEC — `2026-07-12-trace-viewer-view-model-god-class-refactor.md` (194 LoC).
2. `d5764bc` — W20 PLAN — `2026-07-12-trace-viewer-view-model-god-class-refactor.md` (177 LoC).
3. `af0c210` — W20 T1 — Flow A `PlaybackFlow` extracted. Main 686 → 583 (-103 LoC, EXACT). **R1 FIX**: missing `using PeakCan.Host.App.Services.Trace;` for `TraceSource.CanIdFilter`.
4. `0874dc2` — W20 T2 — Flow B `ChartSeriesFlow` extracted. Main 583 → 363 (-220 LoC, EXACT). **R1 CRITICAL FABRICATION FIX**: first Write fabricated OxyPlot API + TraceChartSeries API → 15+ CS0103/CS1061 errors; re-extracted verbatim from HEAD via `git show HEAD:src/...cs | sed -n '305,525p'`; + `using PeakCan.Host.Core.Replay;` for `CanIdListParser`.
5. `e3b95d9` — W20 T3 — Flow C `NullAscServices` extracted to `Helpers/`. Main 363 → 335 (-28 LoC, EXACT). **R6 FIX**: added `using PeakCan.Host.App.Helpers;` to main file for ctor references at lines 184-185.
6. `b45b4ac` — W20 T4 — v3.33.0 → v3.34.0 MINOR + ~94 LoC release notes.

## Main file change (cumulative W20)

`src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` **686 → 335 LoC (-351 LoC, -51.2%)** across 2 NEW partials + 1 NEW helper file. **1st RESIDUAL split** in W3-W20 series (class already had 6 existing partials).

## LoC formula EXACT (W8.5 D7 23-locked)

All 3 transitions EXACT match to ±1 LoC tolerance:
- T1: 686 → 583 (predicted 583)
- T2: 583 → 363 (predicted 363)
- T3: 363 → 335 (predicted 334; actual 335 within ±1)

## Verification

- `dotnet build src/PeakCan.Host.App/`: 0 errors, 0 warnings (after T1 R1 fix + T2 R1 fabrication fix + T3 R6 using-fix)
- `dotnet test --filter "~TraceViewerViewModel"`: 79/79 PASS (10 critical tests: 4 `BuildOneChartSeriesForSource` + 4 `SeekAllToProportionalTime` + 2 propagation + 1 `ClearCanIdFilterCommand_RestoresFrameCount`; 69 adjacent)
- `dotnet test` (full solution): 1339 PASS + 5 SKIP hardware-dependent + 0 new fails

## Architecture milestones

- **16th god-class refactor SHIPPED** (W3-W20 series)
- **1st RESIDUAL split** — class already had 6 existing partials before W20
- **9th subdirectory-pattern deployment** (W6/W7/W8/W11/W14/W15/W17/W18/W19/W20)
- **NEW top-level `Helpers/` subdirectory** (1st deployment)
- **`subdirectory-partials-pattern-empirical-10-precedents` PROMOTED to 3/3 CONFIRMED** at T1 capture
- **`BuildOneChartSeriesForSource` 128 LoC largest-method-stays-inline** (sister of W12 D7 + W14 D8 + W18 D5 + W19 D5)
- **`BuildChartSeries` 6-LoC no-op stub stays in main** (NEW W20 D3 decision)

## CRITICAL LESSON — W20 T2 R1 fabrication

**My first Write of `ChartSeriesFlow.cs` FABRICATED PlotModel API and TraceChartSeries API** — invented `PlotModel.LegendPosition`, `PlotModel.LegendPlacement`, `LineSeries.MarkerFill/MarkerStroke`, `TraceChartSeries.LookupId/IdHex/SignalName` — ALL WRONG. Build failed with 15+ CS0103/CS1061 errors.

**Fix**: re-extracted original code from HEAD via `git show HEAD:src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs | sed -n '305,525p'` and rewrote `ChartSeriesFlow.cs` **VERBATIM**.

**NEW 1/3 lesson candidate**: `partial-extraction-must-use-original-code-from-head-not-fabricated-api` — W20 T2 first observation in W3-W20 series.

## New sister-lesson candidates (6 NEW 1/3 + 1 PROMOTED to 3/3 CONFIRMED)

### NEW 1/3 candidates

1. `partial-extraction-must-use-original-code-from-head-not-fabricated-api` (NEW 1/3, CRITICAL) — W20 T2 fabrication incident
2. `cross-partial-type-reference-needs-explicit-using-even-if-main-uses-it` (NEW 1/3 → 2/3 after T3) — W20 T1 first observation
3. `build-one-chart-series-for-source-largest-method-stays-inline-128-loc` (NEW 1/3) — W20 D5 sister
4. `obsolete-no-op-stub-stays-in-main-on-residual-split` (NEW 1/3) — W20 D3 first observation
5. `playback-helpers-cluster-stays-together-across-partials` (NEW 1/3) — W20 T1 first observation
6. `partial-class-with-private-static-LoggerMessage-cross-partial-compiles-clean` (NEW 1/3) — W20 Phase 1 observation

### PROMOTED 1/3 → 3/3 CONFIRMED

7. `subdirectory-partials-pattern-empirical-10-precedents` (W18 1/3 + W19 2/3 → **W20 3/3 CONFIRMED** at T1 capture)

### Held (awaiting next observation)

- `[ObservableProperty]-source-generator-partial-scope-bounded-by-main-file` (2/3, W16 + W19)

## What was captured

W20 SHIP closure = 7 captures dispatched: SPEC + PLAN + T1 + T2 + T3 + T4 + SHIP. Each per the W12-W19 pattern of `vault-pkm:pkm-capture` agent dispatched after each source commit.

## What was skipped

- No separate `app-get-version` test for v3.34.0 bump (release-notes body suffices per W12 D7 + W14 D8 sister).
- No 2nd verification round on Core tests (1 transient flaky confirmed as pre-existing pollution per W19 T5 + W13 T1 sister).

## Process lessons applied

- **Lesson #10** (verify each commit before proceeding): each W20 T1-T4 build + filter-test verified before commit.
- **Lesson #11** (partial-class using-directives file-scoped, not class-scoped): W20 T1 R1 + T3 R6 fixes.
- **W19 R1 first-correction** applied: re-grep boundaries before each deletion script (T1 range 267-370 verified first try, T2 range 305-525 verified, T3 range 334-363 verified).

## Honest deviations

- (a) **W20 T2 R1 fabrication**: first Write of ChartSeriesFlow.cs fabricated OxyPlot + TraceChartSeries API → 15+ build errors. Lesson captured in CRITICAL section above.
- (b) W20 T3 expected LoC delta 29, actual 28 (predicted 334, actual 335). W13 T1 2/3 loose-assertion caught it.

## Cumulative trajectory (peakcan-host god-class series)

**16 god-class refactors SHIPPED** (W3-W20):
- W3-W11 (9 refactors, App + Core)
- W12 UdsClient + W13 AscParser-round-2 + W14 ScriptEngine + W15 ReplayTimeline + W16 ReplayViewModel + W17 vault-only PATCH + W18 PeakCanChannel + W19 TraceViewModel + **W20 TraceViewerViewModel RESIDUAL**

Plus 1 vault-only PATCH (W17 wc-l-splitlines CONFIRMED promotion).

**Cumulative LoC reduction**: 15 god-class files -2038 LoC (W3-W19) + **W20 TraceViewerViewModel -351 LoC** = **-2389 LoC total** across 16 refactors.

## Next

- **W20.5 vault-only PATCH** — lesson-promotion opportunity for 6 NEW 1/3 candidates (fabrication, cross-partial-using, largest-method-128-loc, no-op-stub, playback-cluster, private-static-LoggerMessage).
- **W21** — next god-class refactor candidate. Top remaining (>350 LoC) main files: `AppShellViewModel.cs` 353 LoC (App layer), `CyclicDbcSendService.cs` 383 LoC (App/Services), `RecordService.cs` 375 LoC (App/Services), `ChannelRouter.cs` 305 LoC (Infrastructure/Channel sister of W18).