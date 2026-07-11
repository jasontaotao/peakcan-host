# W9 IsoTpLayer god-class refactor — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer.cs` from 806 LoC to ~150 LoC by extracting 7 logical flow groups into partial-class files. Pure mechanical refactor — zero behavioral change, zero test changes, zero API surface change.

**Architecture:** Same W3-W8 partial-class split pattern, applied to Core layer. IsoTpLayer stays a single `sealed partial class : IDisposable` with 7 partial-class files in `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer/` directory. Main file keeps state fields, public properties, constants, nested types (`WatchdogHandle` nested class — wait, NO; WatchdogHandle moves to WatchdogFlow per D6), DI readonly fields, constructors, and `Dispose`. Each partial file owns one logical flow group. Tasks ordered smallest-flow-first (F → G → E → B → D → C → A).

**Tech Stack:** C# .NET 10, Microsoft.Extensions.Logging.Abstractions. Core layer (no WPF/MVVM). Git with LF line endings (per v3.18.0 PATCH `.gitattributes`).

## Global Constraints

- **Public API unchanged.** No method signatures, properties, or nested types move.
- **partial-class visibility.** All private methods + private fields visible across partial files; cross-flow calls stay as plain invocations.
- **Test coverage unchanged.** No tests added, removed, or modified.
- **Line-ending normalized to LF.** Per v3.18.0 PATCH `.gitattributes` enforcement.
- **No behavioral change.** Every method body, xmldoc, comment, and whitespace moves verbatim.
- **No version bump until Task 8.** Tasks 1-7 keep `src/Directory.Build.props` at v3.23.1. Task 8 bumps to v3.24.0.
- **Branch**: `feature/w9-isotp-layer-god-class` (already created from `main` @ `f2376f5` v3.23.1).
- **Spec**: `docs/superpowers/specs/2026-07-11-isotp-layer-god-class-refactor.md`.

---

## File Structure

```
src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer.cs                              # main file, ~150 LoC after Task 7
src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer/                                 # NEW directory
  FlowControlFlow.cs                                                         # Task 1 — HandleFlowControl + SendFlowControl (~30 LoC, smallest)
  LoggingFlow.cs                                                             # Task 2 — 3 [LoggerMessage] partial methods (~30 LoC)
  WatchdogFlow.cs                                                            # Task 3 — StartReceiveWatchdog + CancelReceiveWatchdog + WatchdogHandle + _rxWatchdog (~110 LoC)
  SendFlow.cs                                                                # Task 4 — SendMessageAsync + SendSingleFrameAsync + SendCanFrameAsync + SendCanFrame (~95 LoC)
  ReceiveFlow.cs                                                             # Task 5 — ProcessFrame + 4 HandleXxx methods (~110 LoC)
  MultiFrameTransportFlow.cs                                                 # Task 6 — SendMultiFrameAsync + StMinToTimeSpan + WaitForFlowControlAsync (~165 LoC, largest)
  LifecycleFlow.cs                                                           # Task 7 — ctors + Dispose + Reset + constants + properties + event (~155 LoC)
docs/superpowers/plans/2026-07-11-isotp-layer-god-class-refactor.md   # this file
docs/release-notes-v3.24.0.md                                              # NEW in Task 8
```

---

## Cumulative method-line ranges (anchors for all 7 extraction tasks)

Pre-Task-1 file: 806 LoC (commit `5ca82c2`). All deletion scripts delete by line-range slicing per the W3-W7 proven pattern.

**W5 + W8.5 D7 lessons applied**: Each task inserts a "Flow X marker" comment line into the main file. Subsequent tasks must account for the +1 marker line per prior task. **CORRECT formula** (per W8.5 CONFIRMED `plan-LoC-trajectory-table-must-account-for-deletion-not-just-marker-addition` lesson):

```
LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker
```

NOT `LoC_base + n markers` (the wrong formula used in W8 plan).

| Task starts after | Expected LoC | Note |
|---|---|---|
| Task 1 (initial) | 806 | Base |
| Task 2 | 806 - 30 (T1 deleted HandleFlowControl+SendFlowControl) + 1 (marker) = **777** | |
| Task 3 | 777 - 30 (T2 deleted 3 Log methods) + 1 = **748** | |
| Task 4 | 748 - 110 (T3 deleted Watchdog methods + class + field) + 1 = **639** | |
| Task 5 | 639 - 95 (T4 deleted Send methods) + 1 = **545** | |
| Task 6 | 545 - 110 (T5 deleted Receive methods) + 1 = **436** | |
| Task 7 | 436 - 165 (T6 deleted MultiFrameTransport methods) + 1 = **272** | |
| Task 8 | (version bump, no LoC change) | |

**Note**: The exact deletion line ranges need verification by reading the file before each task. Method bodies and xmldoc blocks have line numbers that may shift if prior tasks' line ranges are non-contiguous with the methods being deleted in the current task.

---

### Task 1: Extract Flow F → `FlowControlFlow.cs` (smallest first)

**Files:**
- Create: `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer/FlowControlFlow.cs`
- Modify: `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer.cs` (delete `HandleFlowControl` lines ~723-734 + `SendFlowControl` lines ~736-747)
- Create: `scripts/w9_task1_delete_flowcontrolflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `_txLock`, `_txWaitingForFc`, `_txBlockSize`, `_txStMin`, `_config`, `SendCanFrame`
- Produces: 2 private void methods

**Pre-conditions**:
- Branch `feature/w9-isotp-layer-god-class` checked out
- `git status` clean
- `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer.cs` is 806 LoC

- [ ] **Step 1: Read main file lines 720-750 to capture exact verbatim content of HandleFlowControl + SendFlowControl**

Use Read tool with offset 720, limit 30.

- [ ] **Step 2: Create the partial file `FlowControlFlow.cs`**

Header + verbatim content. Required usings: minimal (Core types visible from any Core namespace).

- [ ] **Step 3: Write the deletion script**

Pattern: identical to W8 Task 1 lifecycle script. Read current line range, assert `original_count == 806`, delete range, insert marker before closing `}` of class, assert structural invariants (`namespace PeakCan.Host.Core.Uds.IsoTp;`, `public sealed partial class IsoTpLayer`, etc.).

- [ ] **Step 4: Run the deletion script + build + test**

```bash
python scripts/w9_task1_delete_flowcontrolflow.py
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~IsoTp"
```

Expected: 0 errors; tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer.cs src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer/FlowControlFlow.cs scripts/w9_task1_delete_flowcontrolflow.py
git commit -m "refactor(isotp): extract Flow F (FlowControl) to partial class (W9 Task 1)"
```

---

### Task 2: Extract Flow G → `LoggingFlow.cs`

**Files:**
- Create: `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer/LoggingFlow.cs`
- Modify: `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer.cs` (delete 3 `[LoggerMessage]` partial methods lines ~763-787)
- Create: `scripts/w9_task2_delete_loggingflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `ILogger`, `LogLevel`, `LoggerMessageAttribute`
- Produces: 3 static partial void methods

**Pre-conditions**:
- Task 1 committed. Main file at 777 LoC.
- Run `wc -l src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer.cs` — must be 777 before this task.

**Step 1**: Read main file around lines 760-790 (after Task 1, ranges shifted by +1 marker, but for contiguous range after the prior deletion the shift may be more nuanced).

**Step 2**: Create `LoggingFlow.cs`. Required usings:
- `Microsoft.Extensions.Logging`

**Step 3**: Write the deletion script (single range covering 3 methods, post-Task-1 expected LoC = 777).

**Step 4**: Run + build + test. Assert `original_count == 777`.

**Step 5**: Commit with message `refactor(isotp): extract Flow G (Logging) to partial class (W9 Task 2)`.

---

### Task 3: Extract Flow E → `WatchdogFlow.cs` (INCLUDES nested class + private field — R3 extension)

**Files:**
- Create: `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer/WatchdogFlow.cs`
- Modify: `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer.cs` (delete `_rxWatchdog` field xmldoc + field lines ~37-118 + `WatchdogHandle` nested class lines ~121-157 + `StartReceiveWatchdog` lines ~569-621 + `CancelReceiveWatchdog` lines ~623-647)
- Create: `scripts/w9_task3_delete_watchdogflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `_rxLock`, `_rxInProgress`, `_rxBuffer`, `_receiveTimeout`, `_watchdogDisposalDeferredCount`
- Produces: 1 private field + 1 nested class + 2 private void methods

**Pre-conditions**:
- Task 2 committed. Main file at 748 LoC.

**R3 mitigation**: This is the FIRST W9 refactor (and second W3-W9) moving private state fields + nested classes. The `_rxWatchdog` field is private and only read/written by `StartReceiveWatchdog` / `CancelReceiveWatchdog` / `WatchdogHandle.Cts`. The `WatchdogHandle` nested class is only constructed by `StartReceiveWatchdog`. partial-class visibility makes this transparent.

**Step 1**: Read main file around lines 37-160 + 565-650 (after Task 2, ranges shifted by +2 markers).

**Step 2**: Create `WatchdogFlow.cs`. Required usings:
- `System.Threading` (CancellationTokenSource, Interlocked, ThreadPool)

**Step 3**: Write the deletion script (3 non-contiguous ranges — `_rxWatchdog` field block at top, `WatchdogHandle` nested class middle, `StartReceiveWatchdog` + `CancelReceiveWatchdog` methods lower).

**Step 4**: Run + build + test. **Verify tests still observe `_watchdogDisposalDeferredCount` increments** — confirms partial-class visibility works for both private field moves + nested class moves.

**Step 5**: Commit with message `refactor(isotp): extract Flow E (Watchdog) to partial class (W9 Task 3)`.

---

### Task 4: Extract Flow B → `SendFlow.cs`

**Files:**
- Create: `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer/SendFlow.cs`
- Modify: `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer.cs` (delete `SendMessageAsync` lines ~225-250 + `SendSingleFrameAsync` lines ~310-315 + `SendCanFrameAsync` lines ~317-367 + `SendCanFrame` lines ~749-761)
- Create: `scripts/w9_task4_delete_sendflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `_config`, `_sendFrame`, `_sendFrameAsync`, `_logger`, `SendFailureCount`, `SendMultiFrameAsync` (Flow C)
- Produces: 4 methods (1 public + 3 private)

**Pre-conditions**:
- Task 3 committed. Main file at 639 LoC.

**Step 1**: Read main file around lines 220-370 + 745-765 (after Task 3, ranges shifted by +3 markers).

**Step 2**: Create `SendFlow.cs`. Required usings:
- `System.Threading`
- `System.Threading.Tasks`

**Step 3**: Write the deletion script (2 non-contiguous ranges — `SendMessageAsync` near top, `SendSingleFrameAsync` + `SendCanFrameAsync` middle, `SendCanFrame` lower).

**Step 4**: Run + build + test. **Verify send-callback test (IsoTpSendFailedException on failure) still works** — confirms `SendCanFrameAsync` exception path is preserved.

**Step 5**: Commit with message `refactor(isotp): extract Flow B (Send) to partial class (W9 Task 4)`.

---

### Task 5: Extract Flow D → `ReceiveFlow.cs`

**Files:**
- Create: `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer/ReceiveFlow.cs`
- Modify: `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer.cs` (delete `ProcessFrame` lines ~252-282 + `HandleSingleFrame` lines ~516-520 + `HandleFirstFrame` lines ~522-567 + `HandleConsecutiveFrame` lines ~649-671 + `HandleConsecutiveFrameLocked` lines ~673-721)
- Create: `scripts/w9_task5_delete_receiveflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `_config`, `_rxLock`, `_rxInProgress`, `_rxBuffer`, `_rxExpectedLength`, `_rxReceivedLength`, `_rxExpectedSequence`, `_logger`, `LogIsoTpFfLengthTooLarge` (Flow G), `LogIsoTpHandlerFailed` (Flow G), `CancelReceiveWatchdog` (Flow E), `StartReceiveWatchdog` (Flow E), `SendFlowControl` (Flow F), `MessageReceived` event
- Produces: 5 methods (1 public + 4 private)

**Pre-conditions**:
- Task 4 committed. Main file at 545 LoC.

**R4 cross-partial callers (5+ cross-partial method calls validated here):**
- `HandleFirstFrame` → `LogIsoTpFfLengthTooLarge` (Flow G, cross-partial)
- `HandleFirstFrame` → `CancelReceiveWatchdog` (Flow E, cross-partial)
- `HandleFirstFrame` → `StartReceiveWatchdog` (Flow E, cross-partial)
- `HandleFirstFrame` → `SendFlowControl` (Flow F, cross-partial)
- `HandleConsecutiveFrame` → `LogIsoTpHandlerFailed` (Flow G, cross-partial)
- `HandleConsecutiveFrameLocked` → `CancelReceiveWatchdog` (Flow E, cross-partial)
- `HandleConsecutiveFrameLocked` → `StartReceiveWatchdog` (Flow E, cross-partial)

**Step 1**: Read main file around lines 250-285 + 510-725 (after Task 4, ranges shifted by +4 markers).

**Step 2**: Create `ReceiveFlow.cs`. Required usings: minimal.

**Step 3**: Write the deletion script (3 non-contiguous ranges — `ProcessFrame` near top, `HandleSingleFrame` + `HandleFirstFrame` middle, `HandleConsecutiveFrame` + `HandleConsecutiveFrameLocked` lower).

**Step 4**: Run + build + test. **Verify reassembly state machine tests** (FF→CF→CF→complete) work — confirms cross-partial calls resolve correctly.

**Step 5**: Commit with message `refactor(isotp): extract Flow D (Receive) to partial class (W9 Task 5)`.

---

### Task 6: Extract Flow C → `MultiFrameTransportFlow.cs` (largest, second-to-last)

**Files:**
- Create: `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer/MultiFrameTransportFlow.cs`
- Modify: `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer.cs` (delete `SendMultiFrameAsync` lines ~369-463 + `StMinToTimeSpan` lines ~465-484 + `WaitForFlowControlAsync` lines ~486-514)
- Create: `scripts/w9_task6_delete_multiframetransportflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): `_sendGate`, `_txLock`, `_txWaitingForFc`, `_txBlockSize`, `_txStMin`, `_flowControlTimeout`, `SendCanFrameAsync` (Flow B)
- Produces: 1 private async Task method + 1 private static helper + 1 private async Task<bool> method

**Pre-conditions**:
- Task 5 committed. Main file at 436 LoC.

**Step 1**: Read main file around lines 365-520 (after Task 5, ranges shifted by +5 markers).

**Step 2**: Create `MultiFrameTransportFlow.cs`. Required usings:
- `System.Threading`

**Step 3**: Write the deletion script (single range covering 3 methods, post-Task-5 expected LoC = 436).

**Step 4**: Run + build + test. **Verify multi-frame transport tests** (FF→FC wait→CFs→complete, BS gate) work — confirms cross-partial `SendCanFrameAsync` calls work.

**Step 5**: Commit with message `refactor(isotp): extract Flow C (MultiFrameTransport) to partial class (W9 Task 6)`.

---

### Task 7: Extract Flow A → `LifecycleFlow.cs` (xmldoc-heavy, last)

**Files:**
- Create: `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer/LifecycleFlow.cs`
- Modify: `src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer.cs` (delete ctors lines ~192-223 + `Reset` lines ~284-298 + `Dispose` lines ~300-308)
- Create: `scripts/w9_task7_delete_lifecycleflow.py`

**Interfaces:**
- Consumes (from main file, partial-class visible): DI fields, constants (defined in main), all state
- Produces: 2 ctors + 1 public void method + 1 public void Dispose method

**Pre-conditions**:
- Task 6 committed. Main file at 272 LoC.

**NOTE**: Lifecycle methods (ctors + Reset + Dispose) are in main but their bodies reference ALL the partial methods (e.g., `Reset` calls `CancelReceiveWatchdog` which is now in WatchdogFlow partial). partial-class visibility handles this transparently.

**Step 1**: Read main file around lines 190-310 (after Task 6, ranges shifted by +6 markers).

**Step 2**: Create `LifecycleFlow.cs`. Required usings: minimal.

**Step 3**: Write the deletion script (single range, post-Task-6 expected LoC = 272).

**Step 4**: Run + build + test. **Verify ctor + Dispose + Reset tests** work — confirms lifecycle methods correctly call cross-partial methods (e.g., `Reset` → `CancelReceiveWatchdog` cross-partial).

**Step 5**: Commit with message `refactor(isotp): extract Flow A (Lifecycle) to partial class (W9 Task 7)`.

**Final main file size after Task 7**: ~272 - 155 (Lifecycle deletions: ctors + Reset + Dispose + related xmldoc) + 1 (marker) = **~118 LoC target** (exceeds -150 LoC target slightly; can be improved by also moving CanIdConfig record to a separate file in a future PATCH).

---

### Task 8: Bump version v3.23.1 → v3.24.0 + write release notes

**Files:**
- Modify: `src/Directory.Build.props` (3.23.1 → 3.24.0 for Version + AssemblyVersion + FileVersion + InformationalVersion)
- Create: `docs/release-notes-v3.24.0.md` (modeled after `docs/release-notes-v3.23.0.md`)

**Pre-conditions**:
- Task 7 committed. Main file at ~118 LoC (target hit/exceeded).

- [ ] **Step 1: Update `src/Directory.Build.props`**

Bump all 4 version fields from `3.23.1` → `3.24.0` (or `3.24.0.0` for the 3-version fields).

- [ ] **Step 2: Write release notes**

Title: `# Release Notes v3.24.0 — IsoTpLayer god-class refactor (MINOR)`. Mirror W7/W8 release notes structure: Why this MINOR, What this MINOR does (split into 7 partials with method tables), What this MINOR does NOT do, Verification (dotnet build, dotnet test, LoC reduction), Risk notes (R1-R4), Files in this ship (8 commits), For the next session.

- [ ] **Step 3: Commit**

```bash
git add src/Directory.Build.props docs/release-notes-v3.24.0.md
git commit -m "chore(release): bump version to v3.24.0 + add release notes

W9 ships: 7 god-class extractions (Flows F, G, E, B, D, C, A).

Main file: 806 -> ~118 LoC (-688 LoC, -85.4%).
7 partial-class files in IsoTpLayer/ directory.
7th god-class refactor, FIRST in Core layer (W3-W8 were App layer ViewModels/).

Tests: IsoTp pass; build clean."
```

---

### Task 9: Tier-3 ship (annotated tag + push + GH release)

Same 5-step Tier-3 script used for v3.17.0/19.0/20.0/21.0/22.0/23.0/23.1.

- [ ] **Step 1: Tag annotated at the version-bump commit**

```bash
git tag -a v3.24.0 -m "v3.24.0 MINOR: IsoTpLayer god-class refactor (W9)"
```

- [ ] **Step 2: Push branch + tag to origin**

```bash
git push origin feature/w9-isotp-layer-god-class
git push origin v3.24.0
```

- [ ] **Step 3: Create GH release**

```bash
gh release create v3.24.0 --target feature/w9-isotp-layer-god-class --title "v3.24.0 MINOR: IsoTpLayer god-class refactor" --notes-file docs/release-notes-v3.24.0.md
```

- [ ] **Step 4: Verify GH release**

```bash
gh release view v3.24.0
```

Expected: 1 asset (source tarball auto-generated), release notes render correctly.

- [ ] **Step 5: Final verification**

```bash
git log --oneline -1 origin/main
git tag --list "v3.24*"
```

Expected: tag exists, branch pushed, ready for PR.

---

## Verification summary

After Task 9 completes:

- `dotnet build` (Debug, warn-as-error): 0 errors
- `dotnet test --filter IsoTp`: all pre-existing tests pass without modification
- Main file `IsoTpLayer.cs`: 806 → ~118 LoC (-688 LoC, -85.4%)
- 7 partial-class files created in `IsoTpLayer/` directory
- Branch `feature/w9-isotp-layer-god-class` at version-bump commit
- Tag `v3.24.0` annotated and pushed
- GH release published

## Out of scope (explicit YAGNI)

- **No facade pattern**: W3-W8+W8.5 confirmed direct partial-class visibility is sufficient.
- **No sub-class creation**: IsoTpLayer stays a single class.
- **No test changes**: All existing tests stay unmodified.
- **No XAML changes**: N/A (Core layer, no UI).
- **No public/internal API surface change**: No new public methods, no removed methods, no renamed members.

## Decision log

- **D1**: 7 partials with descriptive names (Lifecycle/Send/MultiFrameTransport/Receive/FlowControl/Watchdog/Logging) — same W3-W8 naming pattern adapted to Core layer.
- **D2**: Same W3-W8 pattern (no facade, no sub-classes).
- **D3**: Branch name `feature/w9-isotp-layer-god-class`.
- **D4**: Order tasks smallest-first: F (30) → G (30) → E (110) → B (95) → D (110) → C (165) → A (155).
- **D5**: `_rxWatchdog` co-locates with `WatchdogFlow` per W8 R3 lesson extension.
- **D6**: Nested `CanIdConfig` record + nested `WatchdogHandle` class behavior: `CanIdConfig` stays in main (public API surface, like W8's `TraceChartStatistics`); `WatchdogHandle` moves with `WatchdogFlow` (only used by Watchdog methods).
- **D7**: Plan LoC-trajectory-table formula explicitly applied per W8.5 PATCH CONFIRMED lesson: `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`.

## Closing milestone context

This is the **7th god-class refactor** in the project, and the **first in Core layer** (W3-W8 were all App layer). It extends the partial-class split pattern to a non-MVVM class with nested types and `[LoggerMessage]` source-gen methods, providing additional evidence for the W8 R3 (partial-class-with-state-fields) and R4 (cross-partial-method-calls) CANDIDATE lessons.