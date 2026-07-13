# LESSON — App/Services MultiFrame layer sister-pattern (NEW 1/3 observation)

**Status**: NEW 1/3 observation at W30 SHIP closure (2026-07-13)
**Pattern name**: `app-services-multiframe-layer-sister-pattern-empirical-w30`
**1st observation**: W30 v3.44.0 MINOR SequenceSendService god-class refactor
**Awaiting**: 2 more observations across any future App/Services/MultiFrame god-class refactor to promote to 2/3 → 3/3 CONFIRMED → LOCKED

## Observation

`src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs` (266 LoC, W30 god-class candidate) was the **1st App/Services/MultiFrame layer god-class refactor**. It decomposed into **2 NEW partials** in `SequenceSendService/` subdirectory:

### Partial 1 — `SendFlow.partial.cs` (~157 LoC)

1 public async method `SendAsync(IReadOnlyList<MultiFrameSequenceRow>, Mode, int delayMs, int iterations, IProgress<int>?, CancellationToken)` returning `Task<Result>`:
- **91 LoC LARGEST method** (W25 D5 deviation APPLIED 5th time)
- Sharp discrete flow boundary: **concurrent (Task.WhenAll fan-out) vs sequential (Task.Delay loop) dispatcher**
- Calls 2 cross-partial helpers from `RowBuildFlow.partial.cs` via partial-class visibility

### Partial 2 — `RowBuildFlow.partial.cs` (~157 LoC)

2 private helpers:
- `TryBuildRow(MultiFrameSequenceRow, out CanFrame, out string?)` (76 LoC) — dispatches on `MultiFrameSequenceRow.Kind` (Raw vs Dbc) to build a `CanFrame` + per-row error string
- `SendOneAsync(CanFrame, CancellationToken)` (16 LoC) — delegates to `_sendService.SendAsync` with exception-isolation (catches non-cancellation exceptions, returns false; re-throws `OperationCanceledException`)

## Why 2-partial decomposition works (1st observation)

- **Flow boundaries are stable**: send orchestration (`SendFlow`) + row-encoding (`RowBuildFlow`) cleanly cluster with zero cross-flow entanglement.
- **`SendAsync` LARGEST method 91 LoC eligible for W25 D5 deviation**: ≥ 60 LoC threshold + sharp discrete flow boundary (concurrent vs sequential dispatcher) = MOVES to `SendFlow.partial.cs`.
- **`TryBuildRow` + `SendOneAsync` helpers cluster naturally**: both touch the same `_dbcEncodeService` + `_dbcService` + `_sendService` fields + the `MultiFrameSequenceRow` + `CanFrame` + `CanId` type ecosystem.
- **All public API stays on `SendFlow.partial.cs`**: callers see unchanged `SendAsync` signature; the public API surface is the orchestration boundary, not the row-encoding internals.
- **`[LoggerMessage]` partials N/A**: zero `[LoggerMessage]` declarations in W30 — no CS8795 risk.
- **Cross-partial helper visibility works for 2 partials**: 7th confirmation (1st was W22, 2nd was W23, 3rd was W26, 4th was W27, 5th was W28, 6th was W29, 7th is W30) that the cross-partial helper visibility pattern is stable across multi-partial god-class extractions.

## Sister-extraction sequence

W22 (RecordService, 2 partials: Lifecycle + Mutators) → W23 (CyclicDbcSendService, 2 partials: TickLifecycle + CyclicOps) → W26 (CanApi, 3 partials: SinkLifecycle + CallbackRegistry + SendAndQuery) → W27 (RecentSessionsService, 3 partials: PersistenceOps + Mutators + StaticHelpers) → W28 (DbcService, 2 partials: LoadLifecycle + TextDecoding) → W29 (SendFrameLibrary, 3 partials: PersistenceFlow + Mutators + StaticHelpers) → **W30 (SequenceSendService, 2 partials: SendFlow + RowBuildFlow)**.

W30 is **different from W22-W29** in shape:
- **W22-W29 sisters** were all **stateful services** with file-IO lifecycle (RecordService playback records + RecentSessionsService JSON state + DbcService DBC parser + SendFrameLibrary saved frames)
- **W30** is an **orchestration service** with no file-IO — pure dispatcher pattern (build frames → send in concurrent/sequential mode)
- **W30 decomposition** = orchestration flow (SendFlow) + row-encoding helper (RowBuildFlow), distinct from the file-IO + mutators + static-helpers split of W22/W27/W29

## Decision matrix (D1)

| Class shape | Pattern |
|---|---|
| App/Services god-class with **orchestration dispatch** (concurrent vs sequential mode) + row-encoding helpers | **2-partial split** (SendFlow + RowBuildFlow) — W30 pattern |
| App/Services god-class with JSON-persistence + lock-protected mutators + `%APPDATA%` path | **3-partial split** (PersistenceFlow + Mutators + StaticHelpers) — see `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` |
| App/Services god-class with TOML/DBC parser persistence | **2-partial split** (LoadLifecycle + TextDecoding) — see W28 DbcService |
| App/Services god-class with async file-load lifecycle | **2-partial split** (PersistenceOps + Mutators) — see W27 RecentSessionsService LoadAsync |

## Sister-precedent

- `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` — 3/3 CONFIRMED LOCKED at W29.5
- `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` — 7/3 since 3/3 LOCKED at W25 (W30 = 5th move confirming 7 observations)
- `small-god-class-no-largest-method-keeps-all-inline-default-pattern` — 1/3 NEW at W29 (W30 confirms pattern correctly NOT applied since SendAsync 91 LoC ≥ 60 LoC threshold)
- `partial-extraction-must-use-original-code-from-head-not-fabricated-api` — 3/3 CONFIRMED at W21 T2 (W30 = 35th + 36th cumulative applications)
- `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` — 3/3 CONFIRMED at W23 T2 (W30 = 13th observation)
- `add-partial-keyword-to-monolithic-class-before-extraction` — 3/3 CONFIRMED at W26.5 (W30 T0-D2 = 28th application)

## Application scope

All future peakcan-host god-class refactors of App/Services/MultiFrame classes (orchestration dispatch patterns with row-encoding helpers). Awaiting 2 more observations across any future App/Services/MultiFrame god-class to promote to 2/3 → 3/3 CONFIRMED → LOCKED into MASTER-LESSON-CATALOG.

## What to watch for in future refactors

- `MultiFrameSendViewModel.cs` (App/ViewModels — W6 sister, already partial since W6 with `CyclicFlow.cs` + `FrameSendFlow.cs` etc. — likely needs further splitting if main residual >250 LoC)
- Any new App/Services/MultiFrame class added in future feature work
- Sister-extraction in other App/Services subdirectories that adopt the orchestration-dispatch + row-encoding helper shape

## Out of scope

- App/ViewModels (different layer, different concerns — see W21+W24+W25 sister precedent)
- Core-layer god-classes (W22 + W23 stays — orchestration loops)
- Infrastructure/Channel layer (W18 + W25 sister precedent — different decomposition pattern)
- App/Services stateful services with file-IO (W22 + W27 + W28 + W29 sister pattern — different decomposition shape)
