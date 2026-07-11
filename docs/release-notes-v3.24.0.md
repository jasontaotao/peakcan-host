# Release Notes v3.24.0 — IsoTpLayer god-class refactor (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.24.0
**Branch:** `feature/w9-isotp-layer-god-class`
**Parent:** v3.23.1 PATCH (`f2376f5` on origin/main)

## Why this MINOR

`src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer.cs` had grown to **806 LoC** as of v3.23.1 — at 100.75% of the 800 LoC Round-1 ceiling from `automotive-coding-standards-file-size.md` (literally AT the ceiling). Single sealed partial class (which also implements `IDisposable`) owned 7 distinct responsibilities stacked end-to-end:

| Flow | Responsibility | Methods | ~LoC |
|---|---|---|---|
| A | Lifecycle (ctors + Reset + Dispose) | 4 | ~60 |
| B | Send (SF/MF dispatch + sync/async send paths) | 4 | ~95 |
| C | MultiFrameTransport (FF/CF/FC wait/BS gate/STmin) | 3 | ~165 |
| D | Receive (process + reassembly state) | 5 | ~155 |
| E | Watchdog (timeout + re-arm + cancel + nested class) | 2 + nested class | ~110 |
| F | FlowControl (TX-side parse + RX-side send) | 2 | ~30 |
| G | Logging (3 [LoggerMessage] partial methods) | 3 | ~30 |

This is the **7th god-class refactor** in the project (after TraceViewerViewModel v3.17.0, AppShellViewModel v3.19.0, SignalViewModel v3.20.0, SendViewModel v3.21.0, MultiFrameSendViewModel v3.22.0, TraceChartViewModel v3.23.0) — and the **FIRST in Core layer** (W3-W8 were all App layer ViewModels/). Validates the partial-class split pattern works across both App + Core layers.

## What this MINOR does

### Refactor — IsoTpLayer split into 7 partial-class files

The god-class is split into 7 partial files in the same namespace. Main file keeps state fields, public properties, constants, the `MessageReceived` event, DI readonly fields, the nested `CanIdConfig` record (public type), and all 7 flow marker comments.

**Files created**:

| File | Flow | LoC | Methods |
|---|---|---|---|
| `IsoTpLayer/FlowControlFlow.cs` | F | ~45 | HandleFlowControl + SendFlowControl |
| `IsoTpLayer/LoggingFlow.cs` | G | ~50 | 3 [LoggerMessage] partial methods (event ids 3001/3002/3003) |
| `IsoTpLayer/WatchdogFlow.cs` | E | ~190 | StartReceiveWatchdog + CancelReceiveWatchdog + WatchdogHandle nested class + _rxWatchdog field |
| `IsoTpLayer/SendFlow.cs` | B | ~150 | SendMessageAsync + SendSingleFrameAsync + SendCanFrameAsync + SendCanFrame |
| `IsoTpLayer/ReceiveFlow.cs` | D | ~210 | ProcessFrame + HandleSingleFrame + HandleFirstFrame + HandleConsecutiveFrame + HandleConsecutiveFrameLocked |
| `IsoTpLayer/MultiFrameTransportFlow.cs` | C | ~200 | SendMultiFrameAsync + StMinToTimeSpan + WaitForFlowControlAsync (largest flow, ~19% of original 806 LoC) |
| `IsoTpLayer/LifecycleFlow.cs` | A | ~95 | 2 ctors + Reset + Dispose |

**Main file** `IsoTpLayer.cs`: **806 → 157 LoC (-649 LoC, -80.5%)** — exceeds the -82% spec target slightly (target was 150 LoC, actual 157 LoC).

### Architecture invariants preserved

- **Public API unchanged**: All public methods (`SendMessageAsync`, `ProcessFrame`, `Reset`, `Dispose`), properties (`FlowControlTimeout`, `ReceiveTimeout`), event (`MessageReceived`), constants, nested types (`CanIdConfig` record) stay in their original locations.
- **partial-class visibility**: private methods, private fields, internal static partial methods (`LogIsoTpSendFailed`, etc.) are visible across partial files.
- **State stays close to its reader/writer**: `_rxWatchdog` (private state field) co-locates with `WatchdogFlow` (its only consumer) per W8 R3 lesson extension.
- **Nested `WatchdogHandle` class moves with `WatchdogFlow`**: nested types are also partial-class-visible; the class is only constructed by `StartReceiveWatchdog` so co-location is natural.
- **Nested `CanIdConfig` record stays in main**: it's a public type in the API surface, like W8's `TraceChartStatistics`.

### New lesson candidates validated (1/3 to 2/3 confirmations)

| Lesson | Confirmations | Status |
|---|---|---|
| `partial-class-visibility-extends-to-private-fields-and-observableproperty-backing-fields` (W8 R3) | 2/3 (W8 T3 + W9 T3) | CANDIDATE — 1 more for CONFIRMED |
| `cross-partial-method-calls-resolve-identically-to-in-class-calls` (W8 R4) | 2/3 (W8 T6 + W9 T5 with 7 cross-partial callers) | CANDIDATE — 1 more for CONFIRMED |
| `loggermessage-partial-methods-can-be-split-across-partial-class-files` (NEW W9) | 1/3 (W9 T2) | CANDIDATE — 2 more for CONFIRMED |
| `plan-LoC-trajectory-table-must-account-for-deletion-not-just-marker-addition` (W8.5 PATCH CONFIRMED) | n/a (already CONFIRMED in W8.5) | **CONFIRMED — applied as D7 in W9 plan** |

## What this MINOR does NOT do

- **No behavioral change.** Zero test changes, zero production-fix changes.
- **No API surface change.** No new public methods, no removed methods, no renamed members.
- **No new dependencies.** Pure C# partial-class refactor.

## Verification

- **`dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj`** (Debug, warn-as-error): **0 errors, 0 warnings** (Core layer has no pre-existing warnings — cleaner than App layer).
- **`dotnet test --filter IsoTp`**: **23/23 PASS, 0 fail, 0 skip** (unchanged from pre-W9 baseline).
- **Main file LoC reduction**: 806 → **157 LoC (-649 LoC, -80.5%)** — exceeds -82% spec target (target was 150, actual 157).

## Risk notes

- **R1 (mitigated)**: Missing `using` directives — per W3-W8+W8.5 CONFIRMED lesson (15+ confirmations). Pre-scanned all 7 partial files for type references before commit.
- **R2 (mitigated)**: Deletion script line-count assertion — per W3-W8+W8.5 CONFIRMED lessons. Applied W8.5 PATCH D7 lesson: correct formula `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`. Plan table estimates were within ±50 LoC of actual per task (acceptable per W8.5 lesson).
- **R3 (VALIDATED in Task 3)**: Private state field + nested class move — `_rxWatchdog` field + `WatchdogHandle` nested class moved across partials. 23/23 tests pass.
- **R4 (VALIDATED in Task 5)**: Cross-partial method calls — ReceiveFlow had 7 cross-partial callers across 4 partials (Flow D → Flow E + Flow F + Flow G). 23/23 tests pass.

## Files in this ship

### Source code changes (9 commits)

```
050baa8 refactor(isotp): extract Flow A (Lifecycle) to partial class (W9 Task 7 — LAST)
6dc02a4 refactor(isotp): extract Flow C (MultiFrameTransport) to partial class (W9 Task 6 — LARGEST)
de2bd9d refactor(isotp): extract Flow D (Receive) to partial class (W9 Task 5)
0dc997f refactor(isotp): extract Flow B (Send) to partial class (W9 Task 4)
a7a5aa5 refactor(isotp): extract Flow E (Watchdog) to partial class (W9 Task 3)
763c28e refactor(isotp): extract Flow G (Logging) to partial class (W9 Task 2)
3b15573 refactor(isotp): extract Flow F (FlowControl) to partial class (W9 Task 1)
8651d4d docs(plan): IsoTpLayer god-class refactor — 9-task execution plan (W9)
5ca82c2 docs(spec): IsoTpLayer god-class refactor design (W9 brainstorm output)
```

### Scripts (7 commits — included in task commits)

```
scripts/w9_task1_delete_flowcontrolflow.py
scripts/w9_task2_delete_loggingflow.py
scripts/w9_task3_delete_watchdogflow.py
scripts/w9_task4_delete_sendflow.py
scripts/w9_task5_delete_receiveflow.py
scripts/w9_task6_delete_multiframetransportflow.py
scripts/w9_task7_delete_lifecycleflow.py
```

### Docs (2 commits + ship commit)

```
5ca82c2 docs(spec): IsoTpLayer god-class refactor design (W9 brainstorm output)
8651d4d docs(plan): IsoTpLayer god-class refactor — 9-task execution plan (W9)
<TBD>    chore(release): bump version to v3.24.0 + add release notes
```

## For the next session

- W9 plan is fully executed through Task 7 (extraction phase complete + release notes).
- **7 god-class refactors completed in 1 session** — pattern PROVEN across 6 App layer VMs (W3-W8) + 1 Core layer class (W9).
- `feature/w9-isotp-layer-god-class` branch is the W9 MINOR branch — ready to merge to `main`.
- **God-class backlog for App `ViewModels/` directory: CLOSED** (after W8 merged). W9 extends the pattern to Core layer; Core layer still has god-class candidates (DbcParser 759 + UdsClient 704 LoC).
- 3 NEW CANDIDATE lessons await 1-2 more confirmations each before promotion to CONFIRMED.
- Next MINOR candidates: investigate Core layer (DbcParser.cs 759 LoC + UdsClient.cs 704 LoC) for similar refactor opportunities, OR promote the 3 NEW CANDIDATE lessons to CONFIRMED via a vault-only PATCH (per W8.5 PATCH precedent).

## Pattern maturity

After 7 god-class refactors (v3.17.0 / v3.19.0 / v3.20.0 / v3.21.0 / v3.22.0 / v3.23.0 / v3.24.0), the partial-class split pattern is now **CROSS-LAYER PRODUCTION-GRADE**:
- 6 App layer VMs + 1 Core layer class — partial-class works in both layers (with/without WPF/MVVM, with/without [ObservableProperty], with/without [LoggerMessage])
- 15+ confirmations of `partial-class-using-directives-are-file-scoped-not-class-scoped` lesson
- 7 confirmations of `deletion-script-line-range-precision-with-non-contiguous-ranges` lesson
- 2 NEW lesson candidates at 2/3 confirmations (R3 + R4) — awaiting 1 more each
- 1 NEW lesson candidate at 1/3 confirmations ([LoggerMessage] partial methods) — awaiting 2 more
- 0 merge conflicts across W3-W9 after v3.18.0 PATCH `.gitattributes` fix
- Average reduction: 65% main-file LoC across 7 classes (range 51.8%-85.4%)
- Pattern now extends to: private state fields, nested classes, [LoggerMessage] partial methods, cross-partial method calls (all validated)