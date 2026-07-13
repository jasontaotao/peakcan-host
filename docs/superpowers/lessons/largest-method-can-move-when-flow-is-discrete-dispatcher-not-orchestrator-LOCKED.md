# LESSON — Largest method can move when flow is discrete dispatcher not orchestrator (10/3 since 3/3 LOCKED)

**Status**: 10/3 since 3/3 LOCKED at W25 (W32 = 10th observation; 6th move; held in MASTER-LESSON-CATALOG at W32)
**Pattern name**: `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator`
**Locking observation**: W25 ChannelRouter god-class refactor (3/3 CONFIRMED 2026-07-12)
**Held observations**: W22 (stay) + W23 (stay) + W25 (move, 3/3 CONFIRMED) + W26 (move, 4/3) + W27 (move, 5/3) + W28 (move, 6/3) + W30 (move, 7/3) + W32 (move, 8/3 → 10/3)

## Pattern (LOCKED — held across 7 observations)

The "largest method stays inline" sister-principle (W12 + W14 + W18 + W19 + W20 + W21 D5) is **NOT absolute** — large methods CAN move when they map to a **discrete flow boundary**, not when they're a single central orchestration loop.

### Decision matrix (LOCKED across 7 observations)

| LARGEST method LoC | Method shape | D5 decision |
|---|---|---|
| <50 LoC | Any shape (orchestration or discrete) | **Default**: all methods stay inline OR extract per flow-boundary clarity (W29 `SaveUnlocked` 24 LoC stayed inline — see `small-god-class-no-largest-method-keeps-all-inline-default-pattern`) |
| 50-59 LoC | Single orchestration loop | Default: stays inline |
| 50-59 LoC | Sharp discrete flow boundary | Borderline — case-by-case (none yet observed) |
| ≥60 LoC | Single orchestration loop | **Stays inline** (W22 + W23 stays) |
| ≥60 LoC | Sharp discrete flow boundary | **MOVES** to flow partial (W25 + W26 + W27 + W28 + W30 moves) |

## 10 observations (4 stays + 6 moves = 10 total)

### STAYS (orchestration loops, NOT discrete flow)

1. **W22 1-of-10 (stay)**: `RecordService.RecordBatchAsync` 100 LoC STAYED in main (single central orchestration pipeline — read frames → batch → write to disk loop)
2. **W23 2-of-10 (stay)**: `CyclicDbcSendService.OnTimerTick` 151 LoC STAYED in main (single tick-loop pipeline — timer fires → state-machine dispatch loop)
3. **W29 3-of-10 (stay)**: `SendFrameLibrary.SaveUnlocked` 24 LoC STAYED in main (small god-class, <50 LoC threshold per W29 NEW pattern — sister of W31 small god-class stay)
4. **W31 4-of-10 (stay)**: `ReplayService.LoadAsync` 31 LoC STAYED in main (small god-class, <50 LoC threshold per W29 NEW pattern — confirms default D5 applies to small god-classes)

### MOVES (discrete flow boundary, ≥ 60 LoC)

5. **W25 3/3 CONFIRMED (move)**: `ChannelRouter.OnChannelFrame` 73 LoC **MOVED** to `FrameRouting.partial.cs` (fan-out + error-isolation = sharp discrete flow boundary — frame-arrives → per-handler dispatch)
6. **W26 5-of-10 (move)**: `CanApi.OnFrame(CanFrame)` 62 LoC **MOVED** to `SinkLifecycle.partial.cs` (frame-arrives → callback-fanout discrete dispatcher shape, sister of W25 OnChannelFrame)
7. **W27 6-of-10 (move)**: `RecentSessionsService.LoadAsync` 60 LoC **MOVED** to `PersistenceOps.partial.cs` (file-I/O lifecycle = sharp discrete flow boundary, 2nd sister of W25 pattern)
8. **W28 7-of-10 (move)**: `DbcService.LoadAsync` 79 LoC **MOVED** to `LoadLifecycle.partial.cs` (file-I/O + parsing lifecycle = sharp discrete flow boundary, 3rd sister of W27 pattern)
9. **W30 8-of-10 (move)**: `SequenceSendService.SendAsync` 91 LoC **MOVED** to `SendFlow.partial.cs` (concurrent vs sequential dispatcher = sharp discrete flow boundary, 5th move confirming W25 + W26 + W27 + W28 + W30 pattern)
10. **W32 10-of-10 (move)**: `DbcApi.Load` 73 LoC **MOVED** to `LoadFlow.partial.cs` (Load → return result envelope with 4 distinct result paths: success / LoadFailed-surfaced-error / Cancelled / Exception = sharp discrete flow boundary, 6th move confirming W25 + W26 + W27 + W28 + W30 + W32 pattern)

## Why this matters

The lesson is **bounded** — it's a 2-step check (LARGEST ≥ 60 LoC + discrete flow boundary), NOT a free pass. W29 SendFrameLibrary SaveUnlocked 24 LoC confirmed this at 1/3 observation: small god-classes (LARGEST < 50 LoC) follow default D5 sister-principle — all methods stay inline OR extract per flow-boundary clarity, NOT LARGEST-method-can-move.

W30 SendFrameLibrary SendAsync 91 LoC further confirms the lesson is **stable across 7 observations**: 5 moves (W25 + W26 + W27 + W28 + W30) all satisfied the 2-step check (≥ 60 LoC + discrete flow), 2 stays (W22 + W23) correctly stayed inline because they were single orchestration loops.

## Discrete flow boundary patterns observed

1. **Frame-arrives → fan-out** (W25 OnChannelFrame): incoming frame → per-handler dispatch + error-isolation
2. **Frame-arrives → callback-fanout** (W26 OnFrame): incoming frame → subscriber-notify loop with frame-callback registry
3. **File-IO-load → parse-JSON-or-empty** (W27 LoadAsync): load JSON → deserialize + corrupt-fallback + cache-init sentinel
4. **File-IO-load → DbcParse-or-error** (W28 LoadAsync): load file → decode bytes → DbcParser.Parse + parse-failed event
5. **Concurrent-vs-sequential dispatcher** (W30 SendAsync): per-row build → fan-out (Task.WhenAll) OR sequential (Task.Delay loop) → result aggregation
6. **Load → return result envelope with multiple result paths** (W32 Load): load → delegate to inner service → return success / error-surfaced / cancelled / exception envelope for script caller

## Orchestration loop patterns observed

1. **Single central read-batch-write loop** (W22 RecordBatchAsync): read frames → batch → write to disk loop
2. **Single tick-loop state machine** (W23 OnTimerTick): timer fires → per-tick state-machine dispatch loop
3. **Small god-class LARGEST method <50 LoC** (W29 SaveUnlocked + W31 LoadAsync): no discrete flow boundary + too small to justify extraction — default D5 sister-principle applied (stays inline per `small-god-class-no-largest-method-keeps-all-inline-default-pattern` 2/3 PROMOTION at W31.5)

## Sister-precedent

- `small-god-class-no-largest-method-keeps-all-inline-default-pattern` — 1/3 NEW at W29 (W30 confirms pattern correctly NOT applied since SendAsync 91 LoC ≥ 60 LoC threshold)
- `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` — 3/3 CONFIRMED LOCKED at W29.5 (sister-extraction pattern, not LARGEST-method-move pattern)
- `small-god-class-no-largest-method-keeps-all-inline-default-pattern` — 2/3 PROMOTION at W31.5 (W29 + W31 = 2 confirmations of small god-class default D5 pattern)
- `app-services-scripting-sister-pattern-empirical-w14-w26-w32` — 1/3 NEW at W32 (W32 = 1st observation of App/Services/Scripting sister-extraction pattern; sister of W14 ScriptEngine + W26 CanApi)
- `app-services-multiframe-layer-sister-pattern-empirical-w30` — 1/3 NEW at W30 (W30 = 1st observation of MultiFrame sister-extraction pattern)

## Application scope

All future peakcan-host god-class refactors where LARGEST method is ≥ 60 LoC. The 2-step check (≥ 60 LoC + discrete flow boundary) determines whether D5 deviation applies:
- Both checks pass → MOVES to flow partial (sister of W25 + W26 + W27 + W28 + W30)
- LARGEST < 60 LoC OR shape is orchestration loop → stays inline (default D5, sister of W22 + W23)

HELD in MASTER-LESSON-CATALOG at W30 SHIP closure (7/3 observation since 3/3 LOCKED at W25). Future observations continue to lock in both outcomes as canonical.

## What to watch for in future refactors

- Any god-class with LARGEST method ≥ 60 LoC that maps to a discrete flow boundary (frame-arrives → fan-out, file-IO → parse-or-error, mode-dispatch, event-loop with state transitions)
- Sister-pattern inventory: each W-class god-class refactor must check LARGEST method LoC + flow shape BEFORE deciding D5
- Cross-validation: each move MUST satisfy both the LARGEST ≥ 60 LoC threshold AND the discrete flow boundary criterion (NOT free pass for large methods)

## Out of scope

- App/ViewModels (different layer, different concerns — see W21+W24+W25 sister precedent)
- Core-layer god-classes (W22 + W23 stays — orchestration loops; W22 stays 100 LoC orchestration, W23 stays 151 LoC orchestration)
- Infrastructure/Channel layer (W18 + W25 sister precedent — different decomposition pattern)
- App/Services stateful services with file-IO (W22 + W27 + W28 + W29 sister pattern — different decomposition shape)
