# Release Notes v3.35.0 — SignalChartViewModel god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.35.0
**Branch:** `feature/w21-signal-chart-view-model-god-class`
**Parent:** v3.34.0 MINOR (`a25a903` on origin/main + `45067e8` capture-decisions)

## Why this MINOR

`src/PeakCan.Host.App/ViewModels/SignalChartViewModel.cs` had grown to **378 LoC** as of v3.34.0 — at 47.3% of the 800 LoC Round-1 ceiling. Single `public sealed class SignalChartViewModel : ObservableObject` (modifier NOT partial pre-W21) orchestrating the per-signal line-chart rendering pipeline: AddSignal/RemoveSignal (chart series lifecycle) → AppendSample (per-frame buffering) → OnRenderTick (timer-driven drain + PlotModel refresh) → GetStatistics + ExportToCsv (read-only observation). 7 internal consts + 3 dictionary fields + 1 palette + 1 record + 6 methods + 3 private helpers.

This is the **17th god-class refactor** in the project (W3-W21 series). **11th App-layer god-class**. **2nd TraceChartViewModel-family refactor** (sister of W8). **3rd sister-of-SignalViewModel** (sister of W5). **1st god-class refactor across W3-W21 that required fresh-adding the `partial` modifier** (10 prior `partial` cases had modifier pre-existed).

## LoC trajectory (W8.5 D7 CONFIRMED formula — now 26-locked)

Per task, `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`. All 4 transitions **EXACT match** (within W13 T1 2/3 loose-assertion ±2 LoC tolerance).

| Task | Flow | Range deleted | LoC out | Main after |
|---|---|---|---|---|
| T0.5 | D2 add partial keyword | line 45 (1 LoC edit) | 0 | 378 |
| T1 | SeriesManagementFlow (AddSignal + RemoveSignal + Reset) | 134-184 + 216-230 | 66 | 313 |
| T2 | FrameIngestFlow (AppendSample + DrainBufferForTest + OnRenderTick + EnsureTimer + StopTimer) | 139-163 + 253-312 | 85 | 229 |
| T3 | StatisticsExportFlow (GetStatistics + ExportToCsv) | 142-175 + 177-226 | 84 | 146 |
| **Total** | -- | -- | **235** | **146** |

**Net**: 378 → 146 LoC main file (**-232 LoC, -61.4%**). Total project LoC across main + 3 partials ≈ 381 LoC (small +3 LoC overhead from per-file namespace + using directives + 3 cross-flow caller comment blocks).

## What this MINOR does

### Refactor — SignalChartViewModel adds 3 NEW partials + `partial` modifier

**T0.5**: `public sealed class SignalChartViewModel : ObservableObject` → `public sealed partial class SignalChartViewModel : ObservableObject` at line 45 (1st fresh-add `partial` modifier across 17 god-class refactors).

Main file keeps: `using` block (1-12) + namespace (13) + class xmldoc (13-44) + outer class declaration (45) + `SignalStatistics` record (48-54) + 7 internal consts (`WindowSeconds` + `RenderIntervalMs` + `MaxPointsPerSeries` + Palette 10-entry) + 3 dictionaries (`_seriesByKey` + `_displayNames` + `_colorIndex` + `_nextColorSlot`) + 3 mutable fields (`_t0` + `_renderTimer` + `_pendingPoints`) + 3 public observables (`PlotModel` + `HasSignals` + `SignalCount`) + ctor (107-132).

**Files created**:

| File | Flow | LoC | Methods |
|---|---|---|---|
| `SignalChartViewModel/SeriesManagementFlow.cs` | A — SeriesManagementFlow | ~66 | `AddSignal` (29 LoC) + `RemoveSignal` (21 LoC) + `Reset` (15 LoC) (3 chart-series lifecycle helpers) |
| `SignalChartViewModel/FrameIngestFlow.cs` | B — FrameIngestFlow | ~85 | `AppendSample` (25 LoC with extensive performance xmldoc) + `DrainBufferForTest` (internal single-line) + `OnRenderTick` (32 LoC) + `EnsureTimer` (13 LoC) + `StopTimer` (6 LoC) (5 frame-ingest + timer methods) |
| `SignalChartViewModel/StatisticsExportFlow.cs` | C — StatisticsExportFlow | ~85 | `GetStatistics` (34 LoC) + `ExportToCsv` (50 LoC, **45 LoC method body, LARGEST method, stays inline per W21 D5 + W20 D5 + W19 D5 sister**) (2 read-only observation methods) |

### Verification

- `dotnet build src/PeakCan.Host.App/`: **0 errors, 0 warnings** (after W21 T1+T2+T3 using-directive fixes per W20 LESSON)
- `dotnet test --filter "~SignalChartViewModel"`: **24 / 24 PASS** (first try, no test changes)
- `dotnet test --filter "~SignalViewModel"`: **33 / 33 PASS** (sister SignalViewModel tests, no instantiation-site regressions)
- `dotnet test` (full solution): **0 new fails** (full re-run unchanged from v3.34.0 baseline)

## Lessons applied

- **W8.5 D7 CONFIRMED** LoC correction formula (**26-locked** across W12-W21) — all 3 extraction transitions EXACT within ±2.
- **W5/W8 sister** subdirectory pattern: **11th subdirectory-pattern deployment** (SignalChartViewModel/SeriesManagementFlow.cs + FrameIngestFlow.cs + StatisticsExportFlow.cs).
- **W12 D7 + W14 D8 + W18 D5 + W19 D5 + W20 D5 + W21 D5** sister: largest-method stays inline — `ExportToCsv` 45 LoC (single tightly-cohesive method: header writer → all-X collector → per-signal lookup dict → row writer → `File.WriteAllText`) stays verbatim, NOT extracted further.
- **W21 D2 NEW**: fresh-add `partial` modifier — 1st god-class refactor across 17 to add `partial` (10 prior `partial` cases had modifier pre-existed).
- **W21 D4 NEW**: `internal` access preserved automatically — partial classes share assembly; `MaxPointsPerSeries` (internal const) + `DrainBufferForTest` (internal void) continue to work across partials without `InternalsVisibleTo` changes.
- **W13 T1 2/3 loose-assertion** + **W17 wc-l-splitlines CONFIRMED** deletion-script pattern applied at all 3 scripts.
- **W19 R1 first-correction** applied (re-grep boundaries before each deletion).
- **W20 T2 R1 fabrication LESSON APPLIED** (3 times across W21 T1/T2/T3): verbatim re-extraction from HEAD via `git show HEAD:src/...cs | sed -n '<range>p'`. Zero fabrication errors across W21 extraction phase.
- **W11 R1 fix pattern** sister: cross-file type references need explicit using — added `OxyPlot` + `OxyPlot.Series` to SeriesManagementFlow, `System.Windows` + `System.Windows.Threading` + `OxyPlot` + `OxyPlot.Axes` to FrameIngestFlow, `System.Globalization` + `System.IO` + `System.Text` to StatisticsExportFlow.

## CRITICAL LESSON — W20 T2 R1 fabrication APPLIED 3 times in W21

Per W20 T2 R1 fabrication incident (15+ CS0103/CS1061 errors from fabricated OxyPlot API), W21 explicitly applied verbatim re-extraction in **all 3 extraction tasks**:

1. **T1 SeriesManagementFlow**: `git show HEAD:src/PeakCan.Host.App/ViewModels/SignalChartViewModel.cs | sed -n '134,184p' + 'sed -n '216,230p'` → 0 errors first try after using-directive fix.
2. **T2 FrameIngestFlow**: `git show HEAD:src/PeakCan.Host.App/ViewModels/SignalChartViewModel.cs | sed -n '139,163p' + 'sed -n '253,312p'` → 0 errors first try after 4 using-directive fixes.
3. **T3 StatisticsExportFlow**: `git show HEAD:src/PeakCan.Host.App/ViewModels/SignalChartViewModel.cs | sed -n '142,175p' + 'sed -n '177,226p'` → 0 errors first try after 3 using-directive fixes.

**NEW 1/3 lesson candidate PROMOTED 2/3 → 3/3 CONFIRMED**: `partial-extraction-must-use-original-code-from-head-not-fabricated-api` — W20 T2 (1st observation) + W21 T2 (2nd) + W21 T3 (3rd) = 3-of-3.

## New sister-lesson candidates (5 NEW 1/3 + 1 PROMOTED 2/3 → 3/3 CONFIRMED)

### NEW 1/3 candidates (await 2 more observations)

1. `add-partial-keyword-to-monolithic-class-before-extraction` (NEW 1/3 at W21 T0.5) — W21 1st observation; 1st god-class refactor across 17 to fresh-add `partial`. Sister of 10 prior pre-existed-partial confirmations.
2. `internal-const-and-internal-method-survives-partial-extraction-via-shared-assembly` (NEW 1/3 at W21) — W21 1st observation: `MaxPointsPerSeries` (internal const) + `DrainBufferForTest` (internal void) continue to work across partials without `InternalsVisibleTo` changes (partial classes share assembly).
3. `partial-with-no-observalproperty-no-logger-message-no-relaycommand-simplest-case` (NEW 1/3 at W21) — W21 1st observation: `SignalChartViewModel` has NONE of the source-gen decorations. Validates the "zero-decoration = zero source-gen scope risk" hypothesis (cleanest partial split case yet after W19 also had zero LoggerMessages).
4. `export-to-csv-largest-method-stays-inline-45-loc` (NEW 1/3 at W21 T3) — W21 D5 sister: ExportToCsv 45 LoC stays inline (single tightly-cohesive method).

### PROMOTED 1/3 → 3/3 CONFIRMED

5. `partial-extraction-must-use-original-code-from-head-not-fabricated-api` (1/3 W20 T2 → **3/3 CONFIRMED** at W21) — W20 T2 + W21 T2 + W21 T3 = 3-of-3 confirmation that verbatim HEAD re-extraction prevents fabrication errors.

### Held (awaiting next observation)

- `[ObservableProperty]-source-generator-partial-scope-bounded-by-main-file` (2/3, W16 + W19) — Held (N/A for SignalChartViewModel — zero [ObservableProperty])
- `cross-partial-type-reference-needs-explicit-using-even-if-main-uses-it` (2/3 W20 T1 + T3 → Held — W21 had using-directive fixes but those are pre-flight not in-flight discoveries)
- `subdirectory-partials-pattern-empirical-12-precedents` (3/3 CONFIRMED at W20) — W21 11th deployment

## What stays the same

- Public API surface — all 24 SignalChartViewModelTests still pass without modification. `internal const MaxPointsPerSeries` + `internal void DrainBufferForTest` remain accessible to tests via `InternalsVisibleTo`.
- Test count unchanged (24 SignalChartViewModel + 33 sister SignalViewModel + ClickHandler = 57/57 PASS).
- WPF binding contract unchanged (no XAML changes needed).
- DI registration unchanged (`AddSingleton<SignalChartViewModel>()` in `ViewModelsBatch2Flow.cs:166` preserved).
- ObservableCollection surface unchanged (`PlotModel` + `HasSignals` + `SignalCount` still accessible from main).

## Next steps (post-ship)

- **W21.5 vault-only PATCH** — lesson-promotion opportunity (4 NEW 1/3 candidates await 2 more observations).
- **W22** — next god-class refactor candidate. Top remaining (>350 LoC) main files: `DbcSendViewModel.cs` 384 LoC (App/ViewModels) OR `CyclicDbcSendService.cs` 383 LoC (App/Services) OR `RecordService.cs` 375 LoC (App/Services) OR `ChannelRouter.cs` 305 LoC (Infrastructure/Channel sister of W18).