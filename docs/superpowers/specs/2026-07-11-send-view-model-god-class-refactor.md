# W6 Spec — SendViewModel god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.App/ViewModels/SendViewModel.cs` from 533 LoC to ~250 LoC by extracting 4 logical flow groups into partial-class files. Pure mechanical refactor — zero behavioral change, zero test changes, zero API surface change.

**Architecture:** Same W3/W4/W5 partial-class split pattern. SendViewModel stays a single `sealed partial class : ObservableObject, IDisposable` with 4 partial-class files in `src/PeakCan.Host.App/ViewModels/SendViewModel/` directory. Main file keeps constructor, [ObservableProperty] fields, DI-injected services, public properties. Each partial file owns one logical flow group.

**Tech Stack:** C# .NET 10, WPF, CommunityToolkit.Mvvm 8.x. Git with LF line endings (per v3.18.0 PATCH `.gitattributes` — proven effective by W4 + W5 merges having 0 conflicts vs W3's 12).

## Global Constraints

- **Public API unchanged.** No method signatures, [RelayCommand] attributes, or [ObservableProperty] backing fields move. XAML bindings are not affected.
- **partial-class visibility.** All private methods visible across partial files; cross-flow calls stay as plain invocations (no facade, no renaming).
- **Test coverage unchanged.** No tests added, removed, or modified. Verification gate is `dotnet build` + existing test suite passing.
- **Line-ending normalized to LF.** Per v3.18.0 PATCH `.gitattributes` enforcement.
- **No behavioral change.** Every method body, xmldoc, comment, and whitespace moves verbatim.
- **No version bump until Task 5.** Tasks 1-4 keep `Directory.Build.props` at v3.20.0. Task 5 bumps to v3.21.0.
- **Branch**: `feature/w6-send-view-model-god-class` (already created from `main` @ `833cd85` v3.20.0).
- **Spec**: this file.

---

## Current state (533 LoC)

`src/PeakCan.Host.App/ViewModels/SendViewModel.cs` (v3.20.0 HEAD) has:
- 14+ methods total
- Implements: `ObservableObject`, `IDisposable`
- One-shot frame send (SendAsync)
- Cyclic (scheduled) frame send
- Frame library persistence (save/load/delete + multi-frame opener)
- 7 LoggerMessage helpers (LogInvalidId/LogInvalidData/LogSendOk/Failed/Threw/LogSaveToLibraryFailed/DeleteFromLibraryFailed)
- Rate-limit rejection handling (OnRateLimitRejectedCountChanged partial void)

Threshold per `automotive-coding-standards-file-size.md`: 800 LoC ceiling. SendViewModel is **at 67% of the ceiling**.

## Target state (~250 LoC main + 4 partials)

```
src/PeakCan.Host.App/ViewModels/SendViewModel.cs                  (~250 LoC main)
src/PeakCan.Host.App/ViewModels/SendViewModel/                      # NEW directory
  LifecycleFlow.cs                                                  (~10 LoC)   - Flow D: Dispose
  CyclicFlow.cs                                                     (~30 LoC)   - Flow B: StartCyclic + StopCyclic
  LibraryFlow.cs                                                    (~140 LoC)  - Flow C: Library + 2 log helpers
  FrameSendFlow.cs                                                  (~200 LoC)  - Flow A: SendAsync + partial void + 5 log helpers
```

**Net reduction**: 533 → ~250 LoC main file (-53%); total lines unchanged (still ~533 across main + partials).

## Flow boundaries

### Flow A — FrameSend (~200 LoC)
Owns one-shot frame send + validation + error logging. The "user clicks Send" surface.

**Methods**:
- `OnRateLimitRejectedCountChanged(long value)` (line 119) — partial void change handler
- `SendAsync()` (line 225) — one-shot frame send

**Log helpers (5 — co-located with caller)**:
- `LogInvalidId` (line 476)
- `LogInvalidData` (line 479)
- `LogSendOk` (line 482)
- `LogSendFailed` (line 485)
- `LogSendThrew` (line 488)

**Depends on**:
- `_sendService` (DI field, main file)
- `_router` (DI field, main file) — for channel access
- `Latest`/`Frame` (state, main file)
- Cyclic state (intra-cross-flow, Flow B) — SendAsync may stop cyclic on manual send

**File**: `src/PeakCan.Host.App/ViewModels/SendViewModel/FrameSendFlow.cs`
**Required usings**: `Microsoft.Extensions.Logging`, `PeakCan.Host.Core`

### Flow B — Cyclic (~30 LoC)
Owns scheduled/cyclic frame send. The "user starts a cyclic" surface.

**Methods**:
- `StartCyclic()` (line 346)
- `StopCyclic()` (line 373)

**Depends on**:
- `_sendService` (DI field, main file)
- `Latest`/`Frame` (state, main file)
- CyclicTimer/SendCyclicCommand (state, main file)

**File**: `src/PeakCan.Host.App/ViewModels/SendViewModel/CyclicFlow.cs`
**Required usings**: `CommunityToolkit.Mvvm.Input` (for [RelayCommand] attributes on commands)

### Flow C — Library (~140 LoC)
Owns frame library persistence + multi-frame opener. The "user manages saved frames" surface.

**Methods**:
- `RefreshLibrary()` (line 383)
- `SaveCurrentToLibrary(string? name)` (line 391)
- `LoadFromLibrary(SendFrameLibrary.SavedFrame? frame)` (line 436)
- `DeleteFromLibrary(SendFrameLibrary.SavedFrame? frame)` (line 449)
- `OpenMultiFrameSend()` (line 508)

**Log helpers (2 — co-located with caller per W5 helper-co-location principle)**:
- `LogSaveToLibraryFailed` (line 494)
- `LogDeleteFromLibraryFailed` (line 497)

**Depends on**:
- `_library` (DI field, main file)
- `_fileDialogs` (DI field, main file)
- `Latest`/`Frame` (state, main file)

**File**: `src/PeakCan.Host.App/ViewModels/SendViewModel/LibraryFlow.cs`
**Required usings**: `CommunityToolkit.Mvvm.Input`, `Microsoft.Extensions.Logging`, `PeakCan.Host.App.Windows` (for MultiFrameSendWindow)

### Flow D — Lifecycle (~10 LoC)
Owns IDisposable entry point.

**Methods**:
- `Dispose()` (line 211)

**Depends on**:
- Cyclic state (intra-cross-flow, Flow B) — stops cyclic on dispose

**File**: `src/PeakCan.Host.App/ViewModels/SendViewModel/LifecycleFlow.cs`
**Required usings**: minimal

### Main file — ctor + state + public properties (~250 LoC)
Stays as-is, minus the methods moved to partials.

**Keeps**:
- All `[ObservableProperty]` fields (Frame, Latest, Library, RateLimitRejectedCount, etc.)
- All DI readonly fields (_sendService, _router, _library, _fileDialogs, _logger, etc.)
- All CyclicTimer state
- Constructor
- All other state properties

## Architecture invariants (per W3/W4/W5 patterns)

1. **Public API unchanged**: `[RelayCommand]` attributes stay with their methods.
2. **partial-class visibility**: private methods are visible across partial files.
3. **State stays in main file**: all mutable state and DI services stay in the main file.
4. **No new files outside the established directory**: `SendViewModel/` is a sibling directory.
5. **Log helpers co-locate with callers** (per W5 helper-co-location principle): 5 FrameSend helpers stay with SendAsync; 2 Library helpers stay with Save/Delete.

## Verification

- `dotnet build` (Debug, warn-as-error): 0 errors.
- `dotnet test --filter SendViewModel`: all SendViewModel tests pass without modification.

## Risk notes

- **R1 (low)**: Missing `using` directives — per W3/W4/W5 CONFIRMED lesson. Pre-scan method bodies.
- **R2 (low)**: Deletion script precision — per W5 CONFIRMED lesson `deletion-script-loc-assertion-must-read-actual-current-loc-not-plan-predicted-loc`. Adjust assertions for marker line +1 per prior task.
- **R3 (very low)**: `Dispose` (Flow D) → Cyclic state (Flow B) — partial-class visible. Same pattern as W5 Dispose (Flow B) → _drainTimer (Flow A).
- **R4 (very low)**: `SendAsync` (Flow A) → Cyclic state (Flow B) — manual send stops cyclic. Cross-flow state.

## Out of scope (explicit YAGNI)

- **No facade pattern**: W3/W4/W5 confirmed direct partial-class visibility is sufficient.
- **No sub-VM creation**: SendViewModel stays a single class.
- **No test changes**: All existing SendViewModel tests stay unmodified.
- **No XAML changes**: All [RelayCommand] bindings work identically.

## Tasks remaining (preview for the plan)

1. **Task 1**: Extract Flow D (Lifecycle) — smallest, lowest risk, validates tooling.
2. **Task 2**: Extract Flow B (Cyclic).
3. **Task 3**: Extract Flow C (Library).
4. **Task 4**: Extract Flow A (FrameSend) — largest.
5. **Task 5**: Bump version + write release notes (v3.21.0 MINOR ship commit).
6. **Task 6**: Tier-3 push + tag + GH release.

Total: 6 tasks, ~5 source commits (1 per flow + 1 ship).

## Decision log

- **D1**: 4 partials with descriptive names (FrameSend/Cyclic/Library/Lifecycle) — same W5 naming pattern. Confirmed by user.
- **D2**: Same W3/W4/W5 pattern (no facade, no sub-VMs). Confirmed by user.
- **D3**: Branch name `feature/w6-send-view-model-god-class`.
- **D4**: Order tasks smallest-first: D → C → B → A. Wait — D is smallest (10 LoC), then B (30 LoC), then C (140 LoC), then A (200 LoC). Actual order: D → B → C → A (size order). User confirmed.
- **D5**: Log helpers co-locate with their callers (5 FrameSend + 2 Library).