# W20 Spec — TraceViewerViewModel god-class RESIDUAL refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` from 686 LoC to ~329 LoC by extracting 2 NEW partial-class files + 1 standalone helper-class file. Public API + existing test coverage unchanged. 6 existing partials (Lifecycle/Session/Signal/Source/Transport/Watch) are NOT in W20 scope.

**Architecture:** Sister pattern to W3-W19 (subdirectory + non-suffix `.cs` filenames). 16th god-class refactor (W3-W20 series). **1st "RESIDUAL" split** — the class had already been partially refactored prior to W20 (6 partials exist), and W20 extracts the remaining 3 logical clusters. The class is **already** `public sealed partial class TraceViewerViewModel : ObservableObject, IDisposable` at line 43 — no CS0260 mitigation needed.

**Tech Stack:** C# .NET 10, WPF ViewModel with `CommunityToolkit.Mvvm` `[ObservableProperty]` + `[RelayCommand]` source-generators + Microsoft.Extensions.Logging `[LoggerMessage]` source-generators. App layer.

**Plan:** [`../plans/2026-07-12-trace-viewer-view-model-god-class-refactor.md`](../plans/2026-07-12-trace-viewer-view-model-god-class-refactor.md)
**Branch:** `feature/w20-trace-viewer-view-model-residual` (created from `main` @ `d45959b` v3.33.0 HEAD; + capture-decisions `016a04e`)

## Global Constraints

- **Public API unchanged.** All existing public/internal methods, properties, events, `[ObservableProperty]` generated properties, `[RelayCommand]` generated commands, 6 existing partials remain untouched.
- **partial-class visibility.** All private methods + private fields visible across partial files. Each partial carries its own `using` block per W19 + W16 pattern.
- **Test coverage unchanged.** All 4 `BuildOneChartSeriesForSource` tests + 4 `SeekAllToProportionalTime` tests + 2 propagation tests + 1 `ClearCanIdFilter` test pass without modification.
- **Line-ending normalized to LF.**
- **No behavioral change.** Every method body, xmldoc, comment, and whitespace moves verbatim.
- **No version bump until Task 4.** Tasks 1-3 keep `src/Directory.Build.props` at v3.33.0. Task 4 bumps to v3.34.0.
- **6 existing partials NOT in scope** — only the residual main file methods are extracted.
- **W18 R1 mitigation NOT needed** — peakcan-host convention uses `private static partial` for cross-partial `[LoggerMessage]` partials; W20 follows the same convention.

## Current state (686 LoC, after 6 prior partials)

`src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` (v3.33.0 HEAD) has:

**Residual methods in main** (W20 in scope):
- `ClearCanIdFilter` `[RelayCommand]` (lines 270-272, 3 LoC)
- `PropagateLoopToAllServices` (273-278, 6 LoC)
- `PropagateSpeedToAllServices` (279-289, 11 LoC)
- `DetachAllSourcePropertyHandlers` (291-303, 13 LoC)
- `OnAnySourcePropertyChanged` (304-323, 20 LoC)
- `SeekAllToProportionalTime` (324-349, 26 LoC)
- `RebindMasterFromRegistry` (350-382, 33 LoC)
- `BuildChartSeries` `[Obsolete]` no-op stub (400-406, 7 LoC) — **stays in main** per W20 D3
- `BuildOneChartSeriesForSource` (422-549, **128 LoC, LARGEST method, stays inline** per W20 D5)
- `FormatCanIdHex` (558-579, 22 LoC)
- `PlotSignal` (581-642, 62 LoC)
- 2 nested helper classes: `NullAscContentHasher` (665-678, 14 LoC) + `NullAscLocator` (680-684, 5 LoC)

**Stays in main** (W20 out of scope):
- Class xmldoc + declaration (1-43)
- 18 readonly fields + 4 mutable fields (49-133)
- 4 ObservableCollection/VM properties (135-152)
- ctor (165-232) — DI wiring references null helpers (via fully-qualified name after T3)
- `Reset` (234-261) + `HasSources` (262-269)
- `Dispose` (644-664)

**Already in 6 partials** (NOT in W20 scope, stable):
- `LifecycleFlow.cs` (79 LoC) — AttachAllServiceHandlers + DetachAllServiceHandlers + OnMasterPlaybackEnded + OnAnyFrameEmitted
- `SessionFlow.cs` (303 LoC) — Save/OpenSession + BuildSnapshot + ApplySnapshot + 1 `[LoggerMessage]` `LogHashFailed`
- `SignalFlow.cs` (215 LoC) — RebuildSignalsCore + RefreshFrameCounts + BucketFramesByCanId + BuildSignalRowsFromDbcOnly
- `SourceFlow.cs` (303 LoC) — AddTraceAsync + RemoveTraceAsync + SetMaster + 4 `[LoggerMessage]` partials (LogLoadFailed + LogSourceMissing + LogRelocated + LogBundleDbcLoadFailedInline)
- `TransportFlow.cs` (97 LoC) — Play + Pause + Stop + SeekTo
- `WatchFlow.cs` (374 LoC) — TogglePlot + SetPlotOptIn + AddToWatch + PlotSignalFromTableRow

**5 existing `[LoggerMessage]` partials** (cross-partial verified by Phase 1 explore): all use `private static partial` modifier. `LogHashFailed` declared in `SessionFlow.cs:156-157` is called from `SourceFlow.cs` snapshot-load paths via cross-partial visibility. **No CS8795 trigger** because callers are in other partials of the SAME class.

Threshold per `automotive-coding-standards-file-size.md`: 800 LoC ceiling. TraceViewerViewModel main file at **85.8%** of ceiling — must split to comply.

## Target state (~329 LoC main + 2 partials + 1 helper file)

```
src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs                          # main file, ~329 LoC after Task 3
src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/                            # EXISTING subdirectory (6 partials already here)
  PlaybackFlow.cs                                                                # Task 1 NEW -- 7 propagation helpers (~114 LoC)
  ChartSeriesFlow.cs                                                             # Task 2 NEW -- 3 chart-series helpers (~220 LoC)
src/PeakCan.Host.App/Helpers/NullAscServices.cs                                  # Task 3 NEW -- 2 null helper classes standalone (~22 LoC)
docs/superpowers/plans/2026-07-12-trace-viewer-view-model-god-class-refactor.md  # NEW in Task 0
docs/release-notes-v3.34.0.md                                                     # NEW in Task 4
```

**Net reduction**: 686 → ~329 LoC main file (-357 LoC, -52.0%); total LoC across main + 2 partials + 1 helper file = ~685 (small -1 LoC overhead from per-file namespace + using directives).

## Flow boundaries

### Flow — Main file (state + ctor + dispose + no-op stub)

**Stays in main**:
- `using` block (lines 1-15) + `namespace PeakCan.Host.App.ViewModels` (16)
- Class xmldoc + outer class declaration (lines 17-43)
- 18 readonly fields + 4 mutable fields (49-133)
- 4 ObservableCollection/VM properties (135-152)
- ctor (165-232) — DI wiring; references `NullAscContentHasher.Instance` + `NullAscLocator.Instance` (after T3, these become fully-qualified references to `Helpers.NullAscServices.NullAscContentHasher.Instance`)
- `Reset` (234-261) + `HasSources` (262-269)
- `BuildChartSeries` no-op stub `[Obsolete]` (400-406) — **stays in main** per W20 D3 (extracting a 6-LoC `[Obsolete]` no-op to a separate partial file is net-negative)
- `Dispose` (644-664)

### Flow A — PlaybackFlow (~114 LoC, NEW)

**Methods**:
- `[RelayCommand] private void ClearCanIdFilter()` (lines 270-272) — `[RelayCommand]` attribute travels with method (W19 T3 sister)
- `private void PropagateLoopToAllServices()` (273-278)
- `private void PropagateSpeedToAllServices()` (279-289)
- `private void DetachAllSourcePropertyHandlers()` (291-303)
- `private void OnAnySourcePropertyChanged(object?, PropertyChangedEventArgs)` (304-323)
- `private void SeekAllToProportionalTime(double masterT)` (324-349)
- `private void RebindMasterFromRegistry()` (350-382)

**Depends on**:
- 6 readonly fields: `_loop` + `_speed` + `_canIdFilter` + `_allServices` + `_masterService` + `_registry`
- 2 source-gen properties: `Loop` + `Speed` + `CanIdFilter` (main, source-gen partial)
- 6 existing partials: called via cross-partial visibility (LifecycleFlow calls `OnAnyFrameEmitted`; TransportFlow calls `OnLoopChanged` + `OnSpeedChanged`; SourceFlow calls `RebindMasterFromRegistry` + `DetachAllSourcePropertyHandlers` + `OnAnySourcePropertyChanged`; LifecycleFlow calls `SeekAllToProportionalTime`)

**Rationale for grouping**: All 7 methods relate to playback propagation — when master/scrubber/loop/speed/cansid-filter change, propagate to all services + handle source property changes + rebind master. Sister of W19 A dispatcher-cluster (mutable-state coupling methods stay together per W14 D2 + W3 R3).

### Flow B — ChartSeriesFlow (~220 LoC, NEW)

**Methods**:
- `private void BuildOneChartSeriesForSource(...)` (lines 422-549) — **128 LoC, LARGEST method, stays inline per W12 D7 + W14 D8 + W18 D5 + W19 D5 + W20 D5 sister-principle**
- `private static string FormatCanIdHex(uint id)` (558-579)
- `public void PlotSignal(TraceChartSeries series)` (581-642)

**Depends on**:
- `Signals` collection (main, ObservableCollection)
- `WatchedSignals` collection (main)
- `ChartViewModel` property (main, TraceChartViewModel)
- 6 existing partials: WatchFlow calls `FormatCanIdHex` + `BuildOneChartSeriesForSource` (cross-partial)

**Rationale for grouping**: All 3 methods relate to building OxyPlot chart series for one (source, signal) pair. Sister of W19 B (UI-bound formatting cluster). `BuildChartSeries` no-op stub stays in main per W20 D3 (deprecated entry point, 6 LoC).

### Flow C — NullAscServices standalone helper file (~22 LoC, NEW)

**Classes** (moved out of TraceViewerViewModel.cs to `Helpers/NullAscServices.cs`):
- `internal sealed class NullAscContentHasher : IAscContentHasher` (665-678, 14 LoC)
- `internal sealed class NullAscLocator : IAscLocator` (680-684, 5 LoC)

**Rationale for grouping**: Both are 0-or-singleton implementations of internal service interfaces used as test/DI defaults. Sister of W14 dispose-helper-class extraction pattern. Move to top-level Helpers/ (NOT TraceViewerViewModel subdirectory) because they're not partial-class members.

**After T3**: `TraceViewerViewModel.cs` ctor (lines 184-185) references change from `NullAscContentHasher.Instance` to `PeakCan.Host.App.Helpers.NullAscContentHasher.Instance` (fully-qualified).

## Architecture invariants (per W3-W19 patterns)

1. **Public API unchanged.** Same `[ObservableProperty]` generated properties + `[RelayCommand]` generated commands + 6 existing partials + new 2 partials.
2. **partial-class visibility** works for `[RelayCommand]` source-gen — `[RelayCommand]` on `ClearCanIdFilter` stays with method, source-gen emits `ClearCanIdFilterCommand` in main partial (per source-gen scope rule).
3. **State ownership preserved**: 18 readonly fields + 4 mutable fields + ctor + Reset + HasSources + Dispose stay in main.
4. **5 `[LoggerMessage]` partials** (existing) stay in their declaring partials (SessionFlow + SourceFlow). W20 does NOT touch any `[LoggerMessage]` partials.
5. **`BuildOneChartSeriesForSource` 128 LoC stays inline** per W12 D7 + W14 D8 + W18 D5 + W19 D5 + W20 D5 sister-principle (single linear pipeline, sub-extraction would balloon call signature).
6. **`BuildChartSeries` no-op stub stays in main** per W20 D3 (extracting a deprecated 6-LoC no-op is net-negative).
7. **`ClearCanIdFilter` moves to PlaybackFlow.cs** with its `[RelayCommand]` attribute intact (W19 T3 + W19 D2 sister).
8. **Null helper classes move to standalone file** per W20 D4 — they are not partial-class members and don't belong in TraceViewerViewModel subdirectory.

## Verification

- `dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore`: 0 errors, 0 warnings
- `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~TraceViewerViewModel"`: 10+ tests pass without modification
- `dotnet test --no-restore --nologo -c Debug`: full solution 0 new fails

## Risk notes

- **R1 (low)**: Missing `using` directives in new partial files — pre-scan source types per W11 R1 fix pattern. Phase 1 explore confirmed `[RelayCommand]` needs `using CommunityToolkit.Mvvm.Input;` + `using PropertyChangedEventArgs;` needs `System.ComponentModel;`.
- **R2 (very low)**: LoC formula — per W8.5 D7 CONFIRMED 20-locked. Use W19 T1 first-correction + W17 wc-l-splitlines CONFIRMED pattern.
- **R3 (very low)**: xmldoc-grep test risk — Phase 1 explore confirmed no test path-greps main file content. Tests reference methods by name only (C# cref resolver follows class type, not file path).
- **R4 (very low)**: `[ObservableProperty]` source-gen partial scope — no `[ObservableProperty]` extraction in W20; all 18 fields stay in main.
- **R5 (none)**: CS8795 sister-risk from W18 — **N/A** for W20 because peakcan-host convention uses `private static partial` (no R1 mitigation needed). W20 does NOT touch any `[LoggerMessage]` partials.
- **R6 (very low)**: Null helper class reference update — `TraceViewerViewModel.cs:184-185` references `NullAscContentHasher.Instance` + `NullAscLocator.Instance` need to become fully-qualified after T3. Sister of W11 R1 fix pattern (add using namespace).

## Out of scope (explicit YAGNI)

- **No facade pattern**: W3-W19 CONFIRMED direct partial-class visibility is sufficient.
- **No test changes**: All existing tests stay unmodified.
- **No public/internal API surface change**.
- **No `BuildChartSeries` no-op stub deletion**: W20 doesn't deprecate the entry point; that's a separate future task.
- **No 6 existing partials modification**: Lifecycle/Session/Signal/Source/Transport/Watch are stable.
- **No ChannelRouter refactor**: W20 scoped to TraceViewerViewModel only.

## Tasks remaining (preview for the plan)

1. **Task 1**: Extract Flow A — `PlaybackFlow` (7 methods: ClearCanIdFilter + 6 propagation helpers, ~114 LoC).
2. **Task 2**: Extract Flow B — `ChartSeriesFlow` (3 methods: BuildOneChartSeriesForSource 128 LoC + FormatCanIdHex + PlotSignal, ~220 LoC).
3. **Task 3**: Extract Flow C — `NullAscServices` standalone helper (2 internal sealed classes, ~22 LoC) to top-level Helpers/.
4. **Task 4**: Bump version v3.33.0 → v3.34.0 + write release notes (MINOR ship commit).
5. **Task 5**: Tier-3 push + tag + GH release.

Total: 5 tasks, ~4 source commits (T1 + T2 + T3 + ship).

## Decision log

- **D1**: 2 NEW partials (`PlaybackFlow` + `ChartSeriesFlow`) + 1 standalone helper file (`NullAscServices`). Subdirectory pattern for partials (10th subdirectory deployment); top-level Helpers/ for null classes.
- **D2**: 18 readonly fields + 4 mutable fields + ctor + Reset + HasSources + Dispose stay in main (state ownership preserved).
- **D3**: `BuildChartSeries` no-op stub stays in main (deprecated entry point, extracting 6-LoC no-op is net-negative).
- **D4**: Null helper classes move to standalone `Helpers/NullAscServices.cs` file (NOT TraceViewerViewModel subdirectory) — they are not partial-class members.
- **D5**: `BuildOneChartSeriesForSource` 128 LoC stays inline per W12 D7 + W14 D8 + W18 D5 + W19 D5 + W20 D5 sister-principle.
- **D6**: Branch name `feature/w20-trace-viewer-view-model-residual`.
- **D7**: Order tasks: **A (PlaybackFlow) → B (ChartSeriesFlow) → C (NullAscServices)** — A first (largest cluster at 114 LoC, also exercises most cross-partial references); B second (chart-series cluster); C last (smallest, top-level file move).
- **D8**: W18 R1 mitigation NOT applied — peakcan-host convention uses `private static partial` (verified by Phase 1 explore of 5 existing `[LoggerMessage]` partials).

## Closing milestone context

This is the **16th god-class refactor** in the project (W3-W20 series). TraceViewerViewModel is the **9th App-layer god-class** (after W6/W7/W8/W11/W14/W16/W19 + AppShellViewModel at 353 LoC which is the next likely candidate). Specifically, this is the **1st RESIDUAL split** — the class had already been partially refactored prior to W20 with 6 existing partials.

If W20 ships + 10+ tests pass + lesson confirmations hold, next steps are W20.5 vault-only PATCH (lesson promotion if any candidates reach 3/3) OR W21 (next candidate: `AppShellViewModel.cs` 353 LoC App layer, OR `CyclicDbcSendService.cs` 383 LoC App/Services, OR `RecordService.cs` 375 LoC App/Services, OR `ChannelRouter.cs` 305 LoC Infrastructure/Channel).