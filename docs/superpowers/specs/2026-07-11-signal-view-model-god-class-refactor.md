# W5 Spec — SignalViewModel god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.App/ViewModels/SignalViewModel.cs` from 601 LoC to ~250 LoC by extracting 4 logical flow groups into partial-class files. Pure mechanical refactor — zero behavioral change, zero test changes, zero API surface change.

**Architecture:** Same W3/W4 partial-class split pattern. SignalViewModel stays a single `sealed partial class : ObservableObject, IHostedService, IDisposable` with 4 partial-class files in `src/PeakCan.Host.App/ViewModels/SignalViewModel/` directory. Main file keeps constructor, [ObservableProperty] fields, DI-injected services, public properties (Latest, ChartModel), and the IDisposable entry point's caller. Each partial file owns one logical flow group.

**Tech Stack:** C# .NET 10, WPF, CommunityToolkit.Mvvm 8.x. Git with LF line endings (per v3.18.0 PATCH `.gitattributes`).

## Global Constraints

- **Public API unchanged.** No method signatures, [RelayCommand] attributes, or [ObservableProperty] backing fields move. XAML bindings are not affected.
- **partial-class visibility.** All private methods visible across partial files; cross-flow calls stay as plain invocations (no facade, no renaming).
- **Test coverage unchanged.** No tests added, removed, or modified. Verification gate is `dotnet build` + existing test suite passing.
- **Line-ending normalized to LF.** Per v3.18.0 PATCH `.gitattributes` enforcement (proven effective by W4 merge having 0 conflicts vs W3's 12).
- **No behavioral change.** Every method body, xmldoc, comment, and whitespace moves verbatim.
- **No version bump until Task 5.** Tasks 1-4 keep `Directory.Build.props` at v3.19.0. Task 5 bumps to v3.20.0.
- **Branch**: `feature/w5-signal-view-model-god-class` (already created from `main` @ `b3b15a7` v3.19.0).
- **Spec**: this file.
- **Plan**: `docs/superpowers/plans/2026-07-11-signal-view-model-god-class-refactor.md` (created in next step).

---

## Current state (601 LoC)

`src/PeakCan.Host.App/ViewModels/SignalViewModel.cs` (v3.19.0 HEAD) has:
- 18+ methods total
- 2 DI fields: `_chartVm` (SignalChartViewModel), plus timer + lock + list state
- Public properties: `Latest` (ObservableCollection<SignalEntry>), `ChartModel`
- Implements: `ObservableObject`, `IHostedService`, `IDisposable`
- Frame ingest pipeline (SDK read thread → decode → dispatcher hop → upsert)
- Signal selection change + plot toggle + reset/dispose
- Chart plotting commands (PlotAll/PlotNone/ClearChart/ExportChartCsv)
- Filter/search (SearchText → ApplyFilter)

Threshold per `automotive-coding-standards-file-size.md`: 800 LoC ceiling. SignalViewModel is **at 75% of the ceiling**.

## Target state (~250 LoC main + 4 partials)

```
src/PeakCan.Host.App/ViewModels/SignalViewModel.cs              (~250 LoC main)
src/PeakCan.Host.App/ViewModels/SignalViewModel/
  FrameIngestFlow.cs                                           (~200 LoC) - Flow A
  SelectionFlow.cs                                             (~80 LoC)  - Flow B
  ChartFlow.cs                                                 (~60 LoC)  - Flow C
  FilterFlow.cs                                                (~40 LoC)  - Flow D
```

**Net reduction**: 601 → ~250 LoC main file (-58%); total lines unchanged (still ~601 across main + partials).

## Flow boundaries

### Flow A — FrameIngest (~200 LoC)
Owns the SDK read thread → decode → queue → UI thread upsert pipeline. The "ingest frame" surface.

**Methods**:
- `OnDrainTickProxy()` (line 179) — marshal OnDrainTick to UI thread
- `ApplyFrame(CanFrame frame, Message msg)` (line 192) — public; called by TracePipeline; CPU-light decode stays on calling thread, binding-visible upsert queued via DispatcherOperation
- `OnDrainTick(object? sender, EventArgs e)` (line 311) — timer tick handler
- `DrainPending()` (line 320) — drains pending list to UI thread
- `Upsert(SignalEntry entry)` (line 531) — upsert into Latest

**State (intra-flow)**:
- `_pendingLock` (line 133) — sync root for _pending list
- `_pending` (line 134) — List<PendingWork>
- `_drainTimer` (line 135) — System.Threading.Timer
- `PendingWork` record struct (line 137) — co-locates with consumer state

**Depends on**:
- `_chartVm` (DI field, main file) — for PlotModel updates via SignalChartViewModel
- `Latest` (state, main file) — ObservableCollection mutated on UI thread
- `OnSignalSelectionChanged` (Flow B) — called when user toggles signal

**File**: `src/PeakCan.Host.App/ViewModels/SignalViewModel/FrameIngestFlow.cs`
**Required usings**: `System.Collections.ObjectModel`, `System.Windows.Threading`, `PeakCan.Host.Core`, `PeakCan.Host.Core.Dbc`

### Flow B — Selection (~80 LoC)
Owns signal selection change handler + reset + dispose + bulk entry apply. The "user selects/changes signals" surface.

**Methods**:
- `Dispose()` (line 382) — disposes timer
- `Reset()` (line 433) — clears Latest + chart + applies filter
- `OnSignalSelectionChanged(string message, string signal, bool isSelected)` (line 446) — called from DbcViewModel
- `HandlePlotCheckboxClick(SignalEntry entry, bool isChecked)` (line 465) — user toggles plot
- `ApplyEntries(IReadOnlyList<SignalEntry> entries)` (line 557) — bulk load entries

**Depends on**:
- `_drainTimer` (intra-flow state)
- `_chartVm` (DI field, main file)
- `Latest` (state, main file)
- `ApplyFilter` (Flow D) — called from Reset
- `_pending` (Flow A) — cleared on Dispose

**File**: `src/PeakCan.Host.App/ViewModels/SignalViewModel/SelectionFlow.cs`
**Required usings**: `Microsoft.Extensions.Hosting`

### Flow C — Chart plotting (~60 LoC)
Owns the 4 chart manipulation commands. The "user manipulates chart" surface.

**Methods**:
- `ExportChartCsv()` (line 473) — save chart data to CSV
- `ClearChart()` (line 492) — clear all chart series
- `PlotAll()` (line 503) — plot all signals
- `PlotNone()` (line 518) — unplot all signals

**Depends on**:
- `_chartVm` (DI field, main file) — PlotModel + chart series
- `Latest` (state, main file) — for which signals to plot
- `Microsoft.Win32` — for SaveFileDialog in ExportChartCsv

**File**: `src/PeakCan.Host.App/ViewModels/SignalViewModel/ChartFlow.cs`
**Required usings**: `Microsoft.Win32`

### Flow D — Filter/search (~40 LoC)
Owns search text + filter apply. The "user filters signals" surface.

**Methods**:
- `OnSearchTextChanged(string value)` (line 567) — partial void change handler
- `ApplyFilter()` (line 569) — filter Latest by SearchText

**Depends on**:
- `Latest` (state, main file) — collection to filter
- `SearchText` (state, main file) — partial void change handler trigger

**File**: `src/PeakCan.Host.App/ViewModels/SignalViewModel/FilterFlow.cs`
**Required usings**: `System.Globalization`

### Main file — ctor + state + public properties (~250 LoC)
Stays as-is, minus the methods moved to partials.

**Keeps**:
- `public ObservableCollection<SignalEntry> Latest { get; }`
- `public PlotModel? ChartModel => _chartVm?.PlotModel`
- `[ObservableProperty]` fields (SearchText, etc.)
- All DI readonly fields
- Constructor (~150 lines, all DI initialization)
- `IHostedService` methods (StartAsync/StopAsync) — stays in main as the entry point

## Architecture invariants (per W3/W4 patterns)

1. **Public API unchanged**: `[RelayCommand]` attributes stay with their methods. CommunityToolkit.Mvvm source-gen needs to see them on the same declaration.
2. **partial-class visibility**: private methods are visible across partial files. Cross-flow calls stay as plain invocations.
3. **State stays in main file**: all mutable state (`Latest`, `ChartModel`, `[ObservableProperty]` fields) and DI services stay in the main file. Partial files consume them transparently.
4. **No new files outside the established directory**: `SignalViewModel/` is a sibling directory to the main file, matching the W3/W4 pattern.
5. **PendingWork record struct co-locates with Flow A**: it's the timer-queue element type. Move it INTO Flow A's partial file.

## Verification

- `dotnet build` (Debug, warn-as-error): 0 errors. 1 pre-existing unrelated `CS8602` nullable warning in `DbcService.cs:157` is acceptable.
- `dotnet test --filter SignalViewModel`: all SignalViewModel tests pass without modification.
- **Pre-existing parallel-runner flakes** (per v3.16.9.0 release notes): unchanged. Out of scope.

## Risk notes

- **R1 (low)**: Missing `using` directives. Per W3/W4 lesson `partial-class-using-directives-are-file-scoped-not-class-scoped` (5+ confirmations), every partial file MUST include the `using` directives its methods depend on. Mitigation: pre-scan each extracted method's body for type references and add corresponding usings before first build.
- **R2 (low)**: Deletion script precision. Per W3 lesson `deletion-script-must-preserve-namespace-and-using-clauses-when-removing-methods`, use line-range slicing. W4 also hit the marker-line overhead issue (deleting 1 extra line vs predicted) — adjust assertion by +1 to account for marker lines added by prior tasks.
- **R3 (low)**: `PendingWork` record struct (line 137) co-locates with Flow A consumer state. Other partial files reference this struct via partial-class visibility.
- **R4 (very low)**: `_drainTimer` and `_pending`/`_pendingLock` are intra-Flow-A state. Keep with Flow A.
- **R5 (very low)**: `ApplyFilter` (Flow D) reads from `Latest` (state, main file). FrameIngest adds to `Latest`. Filter runs after ingest. No synchronization issue.

## Out of scope (explicit YAGNI)

- **No facade pattern**: W3/W4 confirmed direct partial-class visibility is sufficient.
- **No sub-VM creation**: SignalViewModel stays a single class. No `FrameIngestController`, `ChartManager` extraction.
- **No test changes**: All existing SignalViewModel tests stay unmodified.
- **No XAML changes**: All [RelayCommand] bindings work identically.

## Tasks remaining (preview for the plan)

1. **Task 1**: Extract Flow D (Filter/search) — smallest, lowest risk, validates the line-range deletion script for SignalViewModel.
2. **Task 2**: Extract Flow C (Chart plotting).
3. **Task 3**: Extract Flow B (Selection).
4. **Task 4**: Extract Flow A (FrameIngest) — largest, most cross-flow references + state.
5. **Task 5**: Bump version + write release notes (v3.20.0 MINOR ship commit).
6. **Task 6**: Tier-3 push + tag + GH release.

Total: 6 tasks, ~5 source commits (1 per flow + 1 ship).

## Decision log

- **D1**: 4 partials with descriptive names (FrameIngest/Selection/Chart/Filter) — not letters A-D, not 5 partials. Confirmed by user.
- **D2**: Same W3/W4 pattern (no facade, no sub-VMs). Confirmed by user.
- **D3**: Branch name `feature/w5-signal-view-model-god-class`.
- **D4**: Order tasks smallest-first: D → C → B → A. W4 also used this order.
- **D5**: PendingWork struct co-locates with Flow A (its consumer state).