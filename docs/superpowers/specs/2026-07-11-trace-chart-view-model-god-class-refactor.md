# W8 Spec — TraceChartViewModel god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs` from 435 LoC to ~210 LoC by extracting 6 logical flow groups into partial-class files. Pure mechanical refactor — zero behavioral change, zero test changes, zero API surface change.

**Architecture:** Same W3-W7 partial-class split pattern. TraceChartViewModel stays a single `sealed partial class : ObservableObject` with 6 partial-class files in `src/PeakCan.Host.App/ViewModels/TraceChartViewModel/` directory. Main file keeps state fields, public properties, constants, and the throttling-state block shared by Flow B. Each partial file owns one logical flow group.

**Tech Stack:** C# .NET 10, WPF, CommunityToolkit.Mvvm 8.x, OxyPlot. Git with LF line endings (per v3.18.0 PATCH `.gitattributes`).

## Global Constraints

- **Public API unchanged.** No method signatures, properties, or [ObservableProperty] backing fields move. XAML bindings are not affected.
- **partial-class visibility.** All private methods visible across partial files; cross-flow calls stay as plain invocations (no facade, no renaming).
- **Test coverage unchanged.** No tests added, removed, or modified.
- **Line-ending normalized to LF.** Per v3.18.0 PATCH `.gitattributes` enforcement.
- **No behavioral change.** Every method body, xmldoc, comment, and whitespace moves verbatim.
- **No version bump until Task 7.** Tasks 1-6 keep `Directory.Build.props` at v3.22.0. Task 7 bumps to v3.23.0.
- **Branch**: `feature/w8-trace-chart-view-model-god-class` (already created from `main` @ `9316053` v3.22.0).
- **Spec**: this file.

---

## Current state (435 LoC)

`src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs` (v3.22.0 HEAD) has:
- 14 methods total + 1 nested record type
- Implements: `ObservableObject`
- Series management (Add/Remove + RecomputeHeights + Compute)
- Playback cursor (UpdatePlaybackCursor + throttling state + SetTotalDuration)
- Statistics and CSV export (GetStatistics + ExportToCsv)
- Focus and collapse (ToggleCollapse + SetFocus)
- Axis sync (SyncXAxis + SyncYAxes)
- Viewport bundle save/restore (CaptureViewports + ApplyViewports)

Threshold per `automotive-coding-standards-file-size.md`: 800 LoC ceiling. TraceChartViewModel is **at 54% of the ceiling** — lower absolute % than W3-W7 candidates, but is the last remaining god-class in `ViewModels/` and the natural completion of the session's refactor series.

## Target state (~210 LoC main + 6 partials)

```
src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs                    # main file, ~210 LoC after Task 6
src/PeakCan.Host.App/ViewModels/TraceChartViewModel/                        # NEW directory
  FocusCollapseFlow.cs                                                     # Task 1 — ToggleCollapse + SetFocus (~35 LoC, smallest)
  StatisticsFlow.cs                                                        # Task 2 — GetStatistics + ExportToCsv (~45 LoC)
  PlaybackFlow.cs                                                          # Task 3 — UpdatePlaybackCursor + SetTotalDuration (~50 LoC + throttling state)
  AxisSyncFlow.cs                                                          # Task 4 — SyncXAxis + SyncYAxes (~70 LoC)
  ViewportFlow.cs                                                          # Task 5 — CaptureViewports + ApplyViewports (~80 LoC)
  SeriesManagementFlow.cs                                                  # Task 6 — AddSeries + RemoveSeries + RecomputeHeights + Compute (~75 LoC, largest)
docs/superpowers/plans/2026-07-11-trace-chart-view-model-god-class-refactor.md   # NEW in Task 0 (plan written alongside spec)
docs/release-notes-v3.23.0.md                                              # NEW in Task 7
```

**Net reduction**: 435 → ~210 LoC main file (-52%); total lines unchanged (still ~435 across main + partials).

## Flow boundaries

### Flow A — SeriesManagement (~75 LoC)
Owns the Series collection lifecycle and adaptive subplot height algorithm.

**Methods**:
- `AddSeries(TraceChartSeries s)` (line 64)
- `RemoveSeries(TraceChartSeries s)` (line 70)
- `RecomputeHeights()` (line 245)
- `Compute(TraceChartSeries s, int visibleCount, bool focusModeActive, double h)` (line 270) — internal static helper

**Depends on**:
- `Series` (collection, main file)
- `_chartAreaHeight` (state, main file)
- `MinSubplotHeight` / `MaxSubplotHeight` / `CollapsedSubplotHeight` (constants, main file)

**File**: `src/PeakCan.Host.App/ViewModels/TraceChartViewModel/SeriesManagementFlow.cs`
**Required usings**: none beyond what main has

### Flow B — Playback (~50 LoC + throttling state)
Owns playback cursor movement with throttling (v3.16.9 + v3.16.9.1 PATCH).

**Methods**:
- `UpdatePlaybackCursor(double x)` (line 122)
- `SetTotalDuration(double seconds)` (line 157)

**Throttling state co-locates** (per W3-W7 helper-co-location principle):
- `_lastCursorInvalidateTicks` (line 110)
- `_lastCursorX` (line 111)
- `CursorInvalidateIntervalMs` const (line 112)
- `StopwatchTicksToMs` const (line 113)
- `_invalidatePlotCallCount` [ObservableProperty] (line 119)

**Depends on**:
- `PlaybackCursorX` (property, main)
- `Series` (collection, main)
- `InvalidatePlotCallCount` (property, main)

**File**: `src/PeakCan.Host.App/ViewModels/TraceChartViewModel/PlaybackFlow.cs`
**Required usings**: `System.Diagnostics` (Stopwatch), `CommunityToolkit.Mvvm.ComponentModel` (ObservableProperty)

### Flow C — StatisticsAndExport (~45 LoC)
Owns per-series statistics enumeration and CSV export.

**Methods**:
- `GetStatistics()` (line 159) — IEnumerable yield
- `ExportToCsv(string filePath)` (line 175)

**Depends on**:
- `Series` (collection, main)

**File**: `src/PeakCan.Host.App/ViewModels/TraceChartViewModel/StatisticsFlow.cs`
**Required usings**: `System.Globalization` (CultureInfo), `System.IO` (File), `System.Text` (StringBuilder)

### Flow D — FocusCollapse (~35 LoC, smallest)
Owns per-series focus/collapse state mutation.

**Methods**:
- `ToggleCollapse(TraceChartSeries s)` (line 203)
- `SetFocus(TraceChartSeries s)` (line 221)

**Depends on**:
- `Series` (collection, main)

**File**: `src/PeakCan.Host.App/ViewModels/TraceChartViewModel/FocusCollapseFlow.cs`
**Required usings**: none beyond what main has

### Flow E — AxisSync (~70 LoC)
Owns cross-series axis coordination.

**Methods**:
- `SyncXAxis(double minimum, double maximum)` (line 288)
- `SyncYAxes()` (line 318)

**Depends on**:
- `Series` (collection, main)

**File**: `src/PeakCan.Host.App/ViewModels/TraceChartViewModel/AxisSyncFlow.cs`
**Required usings**: `OxyPlot.Axes` (LinearAxis, AxisPosition)

### Flow F — ViewportBundle (~80 LoC)
Owns viewport snapshot save/restore for session bundles (v3.5.0 MINOR).

**Methods**:
- `CaptureViewports()` (line 359) — returns `IReadOnlyList<BundleViewportDto>`
- `ApplyViewports(IEnumerable<BundleViewportDto> viewports)` (line 396)

**Depends on**:
- `Series` (collection, main)

**File**: `src/PeakCan.Host.App/ViewModels/TraceChartViewModel/ViewportFlow.cs`
**Required usings**: `PeakCan.Host.App.Services.Trace` (BundleViewportDto)

### Main file — fields + state + properties + constants (~210 LoC)
Stays as-is, minus the methods moved to partials. Keeps throttling state in main? **DECISION**: throttling state co-locates with PlaybackFlow (its only reader + writer), per W3-W7 helper-co-location principle.

**Keeps**:
- `TraceChartStatistics` nested record (line 16) — public type, stays at top of main
- `_playbackCursorX`, `_totalDuration`, `_chartAreaHeight` private fields (lines 26-28)
- `MinSubplotHeight` / `MaxSubplotHeight` / `CollapsedSubplotHeight` constants (lines 31-33)
- `Series` collection (line 35)
- `PlaybackCursorX` / `TotalDuration` / `ChartAreaHeight` properties (lines 36-62)
- The dead-code DELETED comment block (lines 19-24) — historical context for v3.3.1 PATCH

**Moves to partials**:
- Throttling state + `UpdatePlaybackCursor` + `SetTotalDuration` → Flow B PlaybackFlow.cs

## Architecture invariants (per W3-W7 patterns)

1. **Public API unchanged**: No public method, property, or nested type moves.
2. **partial-class visibility**: private methods and private fields are visible across partial files.
3. **State stays close to its reader/writer**: Throttling state co-locates with PlaybackFlow (its only consumer).
4. **No new files outside the established directory**: `TraceChartViewModel/` is a sibling directory.
5. **Nested record `TraceChartStatistics` stays in main** — it's a public type returned by `GetStatistics()`, and the public API surface includes its definition location.

## Verification

- `dotnet build` (Debug, warn-as-error): 0 errors.
- `dotnet test --filter TraceChart`: all tests pass without modification.

## Risk notes

- **R1 (low)**: Missing `using` directives — per W3-W7 CONFIRMED lesson (11+ confirmations across W3-W7). Pre-scan method bodies for type references.
- **R2 (low)**: Deletion script precision — per W4/W5/W6 CONFIRMED lesson. Adjust assertions for marker line +1 per prior task.
- **R3 (low)**: Throttling state placement — this is the first W3-W8 refactor that moves private state fields to a partial. The 5 fields (`_lastCursorInvalidateTicks`, `_lastCursorX`, `CursorInvalidateIntervalMs`, `StopwatchTicksToMs`, `_invalidatePlotCallCount`) are private and only read/written by `UpdatePlaybackCursor` and the test via `InvalidatePlotCallCount`. partial-class visibility makes this transparent to compile + test.
- **R4 (very low)**: `RecomputeHeights` (Flow A) is called by AddSeries/RemoveSeries (Flow A), `ChartAreaHeight.set` (main), `ToggleCollapse` (Flow D), `SetFocus` (Flow D), `ApplyViewports` (Flow F) — 5 cross-flow callers. Plain invocations via partial-class visibility.

## Out of scope (explicit YAGNI)

- **No facade pattern**: W3-W7 confirmed direct partial-class visibility is sufficient.
- **No sub-VM creation**: TraceChartViewModel stays a single class.
- **No test changes**: All existing tests stay unmodified.
- **No XAML changes**: All bindings work identically.
- **No public/internal API surface change**: No new public methods, no removed methods, no renamed members.

## Tasks remaining (preview for the plan)

1. **Task 1**: Extract Flow D (FocusCollapse) — smallest, validates tooling on the smallest block first.
2. **Task 2**: Extract Flow C (StatisticsAndExport).
3. **Task 3**: Extract Flow B (Playback) — includes throttling state move (R3).
4. **Task 4**: Extract Flow E (AxisSync).
5. **Task 5**: Extract Flow F (ViewportBundle).
6. **Task 6**: Extract Flow A (SeriesManagement) — largest, depends on Flow D callers (ToggleCollapse, SetFocus) being in place first.
7. **Task 7**: Bump version + write release notes (v3.23.0 MINOR ship commit).
8. **Task 8**: Tier-3 push + tag + GH release.

Total: 8 tasks, ~7 source commits (1 per flow + 1 ship).

## Decision log

- **D1**: 6 partials with descriptive names (SeriesManagement/Playback/Statistics/FocusCollapse/AxisSync/Viewport) — same W5/W6/W7 naming pattern. Confirmed by user.
- **D2**: Same W3-W7 pattern (no facade, no sub-VMs). Confirmed by user.
- **D3**: Branch name `feature/w8-trace-chart-view-model-god-class`.
- **D4**: Order tasks smallest-first: D → C → B → E → F → A. D (35) validates deletion-script pattern on smallest slice first. A (75) is largest so goes last after the pattern is validated.
- **D5**: Throttling state co-locates with PlaybackFlow per W3-W7 helper-co-location principle — Flow B is the only consumer.
- **D6**: Nested record `TraceChartStatistics` stays in main file — it's a public type in the VM's API surface.