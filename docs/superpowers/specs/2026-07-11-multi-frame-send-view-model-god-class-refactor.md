# W7 Spec — MultiFrameSendViewModel god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs` from 513 LoC to ~220 LoC by extracting 5 logical flow groups into partial-class files. Pure mechanical refactor — zero behavioral change, zero test changes, zero API surface change.

**Architecture:** Same W3/W4/W5/W6 partial-class split pattern. MultiFrameSendViewModel stays a single `sealed partial class : ObservableObject, IDisposable` with 5 partial-class files in `src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel/` directory. Main file keeps constructor, [ObservableProperty] fields, DI-injected services, public properties. Each partial file owns one logical flow group.

**Tech Stack:** C# .NET 10, WPF, CommunityToolkit.Mvvm 8.x. Git with LF line endings (per v3.18.0 PATCH `.gitattributes`).

## Global Constraints

- **Public API unchanged.** No method signatures, [RelayCommand] attributes, or [ObservableProperty] backing fields move. XAML bindings are not affected.
- **partial-class visibility.** All private methods visible across partial files; cross-flow calls stay as plain invocations (no facade, no renaming).
- **Test coverage unchanged.** No tests added, removed, or modified.
- **Line-ending normalized to LF.** Per v3.18.0 PATCH `.gitattributes` enforcement.
- **No behavioral change.** Every method body, xmldoc, comment, and whitespace moves verbatim.
- **No version bump until Task 5.** Tasks 1-4 keep `Directory.Build.props` at v3.21.0. Task 5 bumps to v3.22.0.
- **Branch**: `feature/w7-multi-frame-send-view-model-god-class` (already created from `main` @ `d63e9cb` v3.21.0).
- **Spec**: this file.

---

## Current state (513 LoC)

`src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs` (v3.21.0 HEAD) has:
- 17 methods total
- Implements: `ObservableObject`, `IDisposable`
- Sequence row CRUD (Add/Remove/Duplicate/Move/Clear + OnRowsChanged)
- Sequence send loop (SendAsync + Stop)
- Sequence library (Save/Load/Delete/Replace)
- DBC integration (OnDbcLoaded + OnRateLimitRejectedCountChanged partial void)
- IDisposable entry point

Threshold per `automotive-coding-standards-file-size.md`: 800 LoC ceiling. MultiFrameSendViewModel is **at 64% of the ceiling**.

## Target state (~220 LoC main + 5 partials)

```
src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs              # main file, ~220 LoC after Task 4
src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel/                  # NEW directory
  LifecycleFlow.cs                                                       # Task 1 — Dispose
  DbcIntegrationFlow.cs                                                 # Task 2 — OnDbcLoaded + partial void
  LibraryFlow.cs                                                         # Task 3 — Save/Load/Delete/Replace
  RowManagementFlow.cs                                                   # Task 4 — Add/Remove/Duplicate/Move/Clear + OnRowsChanged + RefreshProgressMax + partial void
  SendFlow.cs                                                            # Task 5 — SendAsync + Stop
docs/superpowers/plans/2026-07-11-multi-frame-send-view-model-god-class-refactor.md   # this file
docs/release-notes-v3.22.0.md                                          # NEW in Task 5
```

**Net reduction**: 513 → ~220 LoC main file (-57%); total lines unchanged (still ~513 across main + partials).

## Flow boundaries

### Flow A — RowManagement (~120 LoC)
Owns sequence row CRUD + reorder + progress max tracking.

**Methods**:
- `OnIterationsChanged(int value)` (line 224) — partial void change handler → RefreshProgressMax
- `AddRow()` (line 227)
- `RemoveRow()` (line 234)
- `DuplicateRow()` (line 246)
- `MoveUp()` (line 265)
- `MoveDown()` (line 274)
- `ClearRows()` (line 283)
- `OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)` (line 216) — collection changed handler
- `RefreshProgressMax()` (line 219)

**Depends on**:
- `Rows` (state, main file)
- `Iterations` (state, main file)
- `OnPropertyChanged` (helper, main)

**File**: `src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel/RowManagementFlow.cs`
**Required usings**: `System.Collections.Specialized` (NotifyCollectionChangedEventArgs), `CommunityToolkit.Mvvm.ComponentModel`

### Flow B — Send (~80 LoC)
Owns sequence send loop + stop.

**Methods**:
- `SendAsync()` (line 292) — main sequence send
- `Stop()` (line 345)

**Depends on**:
- `Rows` (state, main)
- `_sendService` (DI field, main)
- CyclicTimer / SendCommand (state, main)

**File**: `src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel/SendFlow.cs`
**Required usings**: `CommunityToolkit.Mvvm.Input`, `PeakCan.Host.Core`

### Flow C — Library (~100 LoC)
Owns sequence save/load/delete + picker.

**Methods**:
- `SaveCurrent()` (line 357) — saves current sequence to library
- `LoadSaved(SequenceLibrary.SavedSequence? saved)` (line 378) — loads saved sequence
- `DeleteSaved(SequenceLibrary.SavedSequence? saved)` (line 402) — deletes saved sequence
- `ReplaceOrAddInPicker(SequenceLibrary.SavedSequence saved)` (line 423) — replaces or adds in picker

**Depends on**:
- `_libraryService` (DI field, main)
- `Rows` (state, main)
- `SavedPicker` (state, main)

**File**: `src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel/LibraryFlow.cs`
**Required usings**: `CommunityToolkit.Mvvm.Input`

### Flow D — DbcIntegration (~20 LoC)
Owns DBC load callback + rate-limit visibility hook.

**Methods**:
- `OnRateLimitRejectedCountChanged(long value)` (line 109) — partial void change handler
- `OnDbcLoaded(DbcDocument doc)` (line 202) — called when DBC is loaded

**Depends on**:
- `_dbc` (state, main)
- `RateLimitRejectedVisibility` (computed property, main)

**File**: `src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel/DbcIntegrationFlow.cs`
**Required usings**: `PeakCan.Host.Core.Dbc`

### Flow E — Lifecycle (~10 LoC)
Owns IDisposable entry point.

**Methods**:
- `Dispose()` (line 505)

**Depends on**:
- CyclicTimer / sequence-send state (Flow B, main)

**File**: `src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel/LifecycleFlow.cs`
**Required usings**: minimal

### Main file — ctor + state + public properties (~220 LoC)
Stays as-is, minus the methods moved to partials.

**Keeps**:
- All `[ObservableProperty]` fields (Rows, Iterations, SavedPicker, etc.)
- All DI readonly fields (_sendService, _libraryService, _dbc, _logger, etc.)
- All sequence-send state
- Constructor
- All other state properties

## Architecture invariants (per W3-W6 patterns)

1. **Public API unchanged**: `[RelayCommand]` attributes stay with their methods.
2. **partial-class visibility**: private methods are visible across partial files.
3. **State stays in main file**: all mutable state and DI services stay in the main file.
4. **No new files outside the established directory**: `MultiFrameSendViewModel/` is a sibling directory.

## Verification

- `dotnet build` (Debug, warn-as-error): 0 errors.
- `dotnet test --filter MultiFrameSendViewModel`: all tests pass without modification.

## Risk notes

- **R1 (low)**: Missing `using` directives — per W3-W6 CONFIRMED lesson (8+ confirmations). Pre-scan method bodies.
- **R2 (low)**: Deletion script precision — per W5 CONFIRMED lesson. Adjust assertions for marker line +1 per prior task.
- **R3 (very low)**: `Dispose` (Flow E) → sequence-send state (Flow B) — partial-class visible.

## Out of scope (explicit YAGNI)

- **No facade pattern**: W3-W6 confirmed direct partial-class visibility is sufficient.
- **No sub-VM creation**: MultiFrameSendViewModel stays a single class.
- **No test changes**: All existing tests stay unmodified.
- **No XAML changes**: All [RelayCommand] bindings work identically.

## Tasks remaining (preview for the plan)

1. **Task 1**: Extract Flow E (Lifecycle) — smallest, validates tooling.
2. **Task 2**: Extract Flow D (DbcIntegration).
3. **Task 3**: Extract Flow C (Library).
4. **Task 4**: Extract Flow B (Send).
5. **Task 5**: Extract Flow A (RowManagement) — largest.
6. **Task 6**: Bump version + write release notes (v3.22.0 MINOR ship commit).
7. **Task 7**: Tier-3 push + tag + GH release.

Total: 7 tasks, ~6 source commits (1 per flow + 1 ship).

## Decision log

- **D1**: 5 partials with descriptive names (RowManagement/Send/Library/DbcIntegration/Lifecycle) — same W5/W6 naming pattern. Confirmed by user.
- **D2**: Same W3-W6 pattern (no facade, no sub-VMs). Confirmed by user.
- **D3**: Branch name `feature/w7-multi-frame-send-view-model-god-class`.
- **D4**: Order tasks smallest-first: E → D → C → B → A. Same W4-W6 order.
- **D5**: `RefreshProgressMax` co-locates with RowManagement (it tracks row count for progress).