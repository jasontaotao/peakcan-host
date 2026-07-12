# W21 Spec — SignalChartViewModel god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.App/ViewModels/SignalChartViewModel.cs` from 378 LoC to ~132 LoC by extracting 3 NEW partial-class files (SeriesManagementFlow + FrameIngestFlow + StatisticsExportFlow) AND adding the `partial` modifier to the outer class declaration (currently `public sealed class`, NOT yet partial). Public API + 24 existing tests unchanged.

**Architecture:** Sister pattern of W5 SignalViewModel + W8 TraceChartViewModel (subdirectory + non-suffix `.cs` filenames; same-named files because of parallel namespacing). 17th god-class refactor (W3-W21 series). 11th App-layer god-class + 11th subdirectory-pattern deployment.

**Tech Stack:** C# .NET 10, WPF ViewModel with `OxyPlot` chart rendering + WPF `DispatcherTimer`. App layer.

**Plan:** [`../plans/2026-07-12-signal-chart-view-model-god-class-refactor.md`](../plans/2026-07-12-signal-chart-view-model-god-class-refactor.md)
**Branch:** `feature/w21-signal-chart-view-model-god-class` (created from `main` @ `a25a903` v3.34.0 HEAD; + W20 capture-decisions `45067e8`)

## Global Constraints

- **Public API unchanged.** All public methods, properties, `SignalStatistics` record, internal constants + test helpers preserved.
- **Add `partial` modifier to outer class** declaration at line 45 (1st edit before any extraction).
- **partial-class visibility.** All private methods + private fields visible across partial files. Each partial carries its own `using` block.
- **Test coverage unchanged.** All 24 dedicated `SignalChartViewModelTests` + 7 sister `SignalViewModelTests` + 3 `SignalViewModelClickHandlerTests` = 34 test instantiation sites pass without modification.
- **Internal access preserved.** `MaxPointsPerSeries` (internal const) + `DrainBufferForTest` (internal void) must stay accessible to tests via `InternalsVisibleTo`. Partial classes share assembly — no InternalsVisibleTo change needed.
- **Line-ending normalized to LF.**
- **No behavioral change.** Every method body, xmldoc, comment, and whitespace moves verbatim.
- **No version bump until Task 4.** Tasks 1-3 keep `src/Directory.Build.props` at v3.34.0. Task 4 bumps to v3.35.0.

## Current state (378 LoC)

`src/PeakCan.Host.App/ViewModels/SignalChartViewModel.cs` (v3.34.0 HEAD) has:

- 1 `public sealed record SignalStatistics(string, string, double, double, double, int)` (lines 48-54) — public DTO consumed by `SignalViewModel.cs:86`
- 3 `internal const` (`WindowSeconds` + `RenderIntervalMs` + `MaxPointsPerSeries`)
- 1 `private static readonly OxyColor[] Palette` (66-78, 10-entry initializer)
- 3 private readonly dictionaries: `_seriesByKey` + `_displayNames` + `_colorIndex` (81-83)
- 1 mutable int `_nextColorSlot` (84)
- 1 nullable ulong `_t0` (91)
- 1 nullable `DispatcherTimer _renderTimer` (93)
- 1 mutable `Dictionary<string, (double,double)> _pendingPoints` (188)
- 1 `public PlotModel { get; }` (96) — exposed for XAML binding
- 2 public expression-bodied properties: `HasSignals` (102) + `SignalCount` (105)
- 1 `public SignalChartViewModel()` ctor (107-132)
- 1 `public void AddSignal(string, string)` (141-162, 22 LoC)
- 1 `public void RemoveSignal(string)` (167-184, 18 LoC)
- 1 `public void AppendSample(string, double, ulong)` (205-214, 10 LoC)
- 1 `public void Reset()` (219-230, 12 LoC)
- 1 `public IReadOnlyList<SignalStatistics> GetStatistics()` (236-265, 30 LoC)
- 1 `public void ExportToCsv(string)` (272-316, **45 LoC, LARGEST method, stays inline**)
- 1 `internal void DrainBufferForTest()` (323, 1 LoC)
- 1 `private void OnRenderTick(object?, EventArgs)` (325-356, 32 LoC)
- 1 `private void EnsureTimer()` (358-370, 13 LoC)
- 1 `private void StopTimer()` (372-377, 6 LoC)

**Zero source-generator decorations** (verified by Phase 1 grep):
- No `[ObservableProperty]` backing fields
- No `[RelayCommand]` methods
- No `[LoggerMessage]` partials
- Not `IDisposable`

Threshold per `automotive-coding-standards-file-size.md`: 800 LoC ceiling. SignalChartViewModel at **47.3%** of ceiling.

## Target state (~132 LoC main + 3 partials)

```
src/PeakCan.Host.App/ViewModels/SignalChartViewModel.cs                          # main file, ~132 LoC after Task 3
src/PeakCan.Host.App/ViewModels/SignalChartViewModel/                            # NEW directory
  SeriesManagementFlow.cs                                                         # Task 1 NEW -- AddSignal + RemoveSignal + Reset (~75 LoC)
  FrameIngestFlow.cs                                                             # Task 2 NEW -- AppendSample + OnRenderTick + EnsureTimer + StopTimer + DrainBufferForTest (~85 LoC)
  StatisticsExportFlow.cs                                                         # Task 3 NEW -- GetStatistics + ExportToCsv (~80 LoC)
docs/superpowers/plans/2026-07-12-signal-chart-view-model-god-class-refactor.md  # NEW in Task 0
docs/release-notes-v3.35.0.md                                                     # NEW in Task 4
```

**Net reduction**: 378 → ~132 LoC main file (-246 LoC, -65.1%); total LoC across main + 3 partials ≈ 372 LoC (small -6 LoC overhead from per-file namespace + using directives + 3 cross-flow caller comment blocks).

## Flow boundaries (Phase 1 explore verified)

### Flow — Main file (state + consts + record + observables + ctor)

**Stays in main (~132 LoC)**:
- `using` block (1-12) + namespace (13) + class xmldoc (13-44)
- Outer class declaration (45) — **add `partial` modifier**
- `SignalStatistics` record (48-54) — public DTO
- 7 internal consts: `WindowSeconds` + `RenderIntervalMs` + `MaxPointsPerSeries` + Palette (10-entry)
- 3 dictionaries + `_nextColorSlot` + `_t0` + `_renderTimer` + `_pendingPoints` (mutable state read by all 3 partials)
- 3 public observables: `PlotModel` + `HasSignals` + `SignalCount`
- 1 ctor (107-132) — DI wiring; references `RenderIntervalMs` to construct `_renderTimer`

### Flow A — SeriesManagementFlow (~75 LoC, NEW)

**Methods**:
- `public void AddSignal(string, string)` (141-162) — 22 LoC + xmldoc
- `public void RemoveSignal(string)` (167-184) — 18 LoC + xmldoc
- `public void Reset()` (219-230) — 12 LoC + xmldoc

Touches: `_seriesByKey` + `_displayNames` + `_colorIndex` + `_nextColorSlot` + `Palette` + `_t0`.

Sister of W8 TraceChartViewModel.SeriesManagementFlow + W20 TraceViewerViewModel.ChartSeriesFlow (chart-series lifecycle cluster).

### Flow B — FrameIngestFlow (~85 LoC, NEW)

**Methods**:
- `public void AppendSample(string, double, ulong)` (205-214) — 10 LoC + extensive performance xmldoc
- `internal void DrainBufferForTest()` (323) — 1 LoC, single-line expression-bodied
- `private void OnRenderTick(object?, EventArgs)` (325-356) — 32 LoC, no xmldoc
- `private void EnsureTimer()` (358-370) — 13 LoC
- `private void StopTimer()` (372-377) — 6 LoC

Touches: `_pendingPoints` + `_t0` + `_renderTimer` + `RenderIntervalMs` + `_seriesByKey` (read).

Sister of W5 SignalViewModel.FrameIngestFlow + W12 UdsClient/FrameIngestFlow sister-pattern (frame ingestion + timer-driven processing).

### Flow C — StatisticsExportFlow (~80 LoC, NEW)

**Methods**:
- `public IReadOnlyList<SignalStatistics> GetStatistics()` (236-265) — 30 LoC + xmldoc
- `public void ExportToCsv(string)` (272-316) — **45 LoC, LARGEST method, stays inline per W12/W14/W18/W19/W20 D5 sister-principle** (single tightly-cohesive method: header writer → data writer → try/catch → finally dispose)

Read-only over `_seriesByKey` + `_displayNames` + `_t0`. No mutations.

Sister of W8 TraceChartViewModel.StatisticsFlow + W19 TraceViewModel.ExportFlow (statistics + export observation cluster).

## Architecture invariants (per W3-W20 patterns)

1. **Public API unchanged.** Same `AddSignal` / `RemoveSignal` / `AppendSample` / `Reset` / `GetStatistics` / `ExportToCsv` / `PlotModel` / `HasSignals` / `SignalCount` / `SignalStatistics` record.
2. **partial-class visibility** works for all 3 partials — private fields + methods shared via cross-partial visibility.
3. **State ownership preserved**: 3 dictionaries + 3 mutable fields + Palette + 3 observables + ctor stay in main.
4. **`internal` access preserved automatically** — partial classes share assembly; `MaxPointsPerSeries` + `DrainBufferForTest` remain `internal` without `InternalsVisibleTo` change.
5. **Largest-method stays inline** — `ExportToCsv` 45 LoC stays verbatim (sister of `AppendBatchAsync` 60 LoC W19, `BuildOneChartSeriesForSource` 128 LoC W20, `ReadLoopAsync` 75 LoC W18).
6. **Add `partial` modifier** before any extraction — 1st edit (sister of 10 prior pre-existed-partial confirmations, but W21 is 1st fresh-add case).

## Verification

- `dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore`: 0 errors, 0 warnings
- `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~SignalChartViewModel"`: 24/24 tests pass without modification
- `dotnet test --filter "FullyQualifiedName~SignalViewModel"`: sister tests pass (10 instantiation sites preserved)
- `dotnet test --no-restore --nologo -c Debug`: full solution 0 new fails

## Risk notes

- **R1 (low)**: Missing `using` directives in new partial files — pre-scan source types per W11 R1 + W20 T1 R1 fix pattern. OxyPlot types referenced in all 3 partials (`PlotModel`, `LineSeries`, `OxyColor`, `DataPoint`, `AxisPosition`) need `using OxyPlot;` + `using OxyPlot.Series;` + `using OxyPlot.Axes;` per partial.
- **R2 (very low)**: LoC formula — per W8.5 D7 CONFIRMED 23-locked. Use W19 T1 first-correction + W17 wc-l-splitlines CONFIRMED pattern.
- **R3 (very low)**: xmldoc-grep test risk — Phase 1 explore confirmed no test path-greps main file content. Tests use plain `[Fact]`, no `[Theory]`/`[TheoryData]` with xmldoc refs.
- **R4 (very low)**: `[ObservableProperty]` source-gen partial scope — **N/A** (zero `[ObservableProperty]` in SignalChartViewModel).
- **R5 (none)**: CS8795 sister-risk from W18 — **N/A** (zero `[LoggerMessage]` partials; zero source-gen decorations of any kind).
- **R6 (CRITICAL — W20 LESSON APPLIED)**: W20 T2 R1 fabrication incident — **NEVER fabricate OxyPlot API based on sister-pattern recall**. ALWAYS re-extract verbatim from HEAD via `git show HEAD:src/...cs | sed -n '<range>p'`.

## Out of scope (explicit YAGNI)

- **No facade pattern**: W3-W20 CONFIRMED direct partial-class visibility is sufficient.
- **No test changes**: All 34 SignalChartViewModel + SignalViewModel + SignalViewModelClickHandler tests stay unmodified.
- **No public/internal API surface change**.
- **No `Palette` extraction to partial** — stays in main (initialization-only; threading across 3 partials is uglier than keeping in main).
- **No CAN ID filter cluster** — verified (CAN-ID filtering lives in `TraceViewerViewModel`, not here).
- **No `SignalViewModel` refactor**: W21 scoped to SignalChartViewModel only.

## Tasks remaining (preview for the plan)

1. **Task 1**: Extract Flow A — `SeriesManagementFlow` (AddSignal + RemoveSignal + Reset, ~75 LoC).
2. **Task 2**: Extract Flow B — `FrameIngestFlow` (AppendSample + DrainBufferForTest + OnRenderTick + EnsureTimer + StopTimer, ~85 LoC).
3. **Task 3**: Extract Flow C — `StatisticsExportFlow` (GetStatistics + ExportToCsv, ~80 LoC).
4. **Task 4**: Bump version v3.34.0 → v3.35.0 + write release notes (MINOR ship commit).
5. **Task 5**: Tier-3 push + tag + GH release.

Total: 5 tasks, ~4 source commits (T0.5 + T1 + T2 + T3 + ship).

## Decision log

- **D1**: 3 NEW partials (`SeriesManagementFlow` + `FrameIngestFlow` + `StatisticsExportFlow`). Subdirectory pattern (11th deployment).
- **D2**: Add `partial` modifier to outer class declaration at line 45 (1st edit before any extraction; sister of 10 prior pre-existed-partial confirmations, but W21 is 1st fresh-add).
- **D3**: 3 dictionaries + 3 mutable fields + Palette + 3 observables + ctor + `SignalStatistics` record stay in main (state ownership preserved).
- **D4**: `MaxPointsPerSeries` (internal const) + `DrainBufferForTest` (internal void) keep `internal` modifier; partial-class inheritance makes this transparent.
- **D5**: `ExportToCsv` 45 LoC stays inline per W12/W14/W18/W19/W20 sister-principle (single tightly-cohesive method).
- **D6**: Branch name `feature/w21-signal-chart-view-model-god-class`.
- **D7**: Order A → B → C (SeriesManagement first because it owns `_seriesByKey` lifecycle state; FrameIngest second because it depends on series state; StatisticsExport last because read-only).

## Closing milestone context

This is the **17th god-class refactor** in the project (W3-W21 series). SignalChartViewModel is the **11th App-layer god-class** (after W6/W7/W8/W11/W14/W16/W19/W20). Specifically, this is the **2nd `TraceChartViewModel`-family refactor** (sister of W8 TraceChartViewModel) + the **3rd sister-of-sister-of-SignalViewModel** (sister of W5 SignalViewModel).

If W21 ships + 24 tests pass + lesson confirmations hold, next steps are W21.5 vault-only PATCH (lesson promotion if any candidates reach 3/3) OR W22 (next candidate: `DbcSendViewModel.cs` 384 LoC OR `CyclicDbcSendService.cs` 383 LoC OR `RecordService.cs` 375 LoC OR `ChannelRouter.cs` 305 LoC Infrastructure/Channel).