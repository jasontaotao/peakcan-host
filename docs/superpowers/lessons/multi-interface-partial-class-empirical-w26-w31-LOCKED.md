# LESSON — Multi-interface partial-class extraction (3/3 CONFIRMED → LOCKED)

**Status**: 3/3 CONFIRMED — **LOCKED into MASTER-LESSON-CATALOG** at W31 SHIP closure (2026-07-13)
**Locking observation**: W31 v3.45.0 MINOR ReplayService god-class refactor (3rd confirmation across 2 distinct classes)
**Earlier observations**: W26 CanApi (1st, 2/3 at W26.5) + W31 ReplayService (2nd, 1/3 at W31 SPEC) = 3/3 CONFIRMED

## Pattern (LOCKED)

When refactoring a **multi-interface partial god-class** (class implements 2+ interfaces), the partial-class decomposition follows the same flow-boundary pattern as single-interface god-classes — interfaces do NOT change the decomposition shape.

### Key invariant

Multi-interface god-classes decompose into 2+ partials based on **flow boundaries** (state mutation lifecycle vs emission vs static helpers), NOT based on **interface boundaries**. Each partial continues to implement all the interfaces of the original class.

## 3 confirmations

### W26 1st + 2nd confirmations — CanApi (multi-interface: `IFrameSink + IScriptCanApi`)

`src/PeakCan.Host.App/Services/Scripting/CanApi.cs` (347 LoC):
- Implements `IFrameSink + IScriptCanApi` (2 interfaces)
- Decomposed into 3 NEW partials in `CanApi/` subdirectory:
  - `SinkLifecycle.partial.cs` — `IFrameSink.OnFrame` (62 LoC LARGEST method moves per W25 D5 deviation since ≥ 60 LoC + discrete flow boundary)
  - `CallbackRegistry.partial.cs` — `IScriptCanApi` callback registry methods (4 methods: `OnFrame` + `OffFrame` + `OnMessage` + `OffMessage`)
  - `SendAndQuery.partial.cs` — `IScriptCanApi.Send` + `IsConnected` + `GetChannelId`
- 11 `[LoggerMessage]` partials stay on main per W18+W22+W23+W25+W26 sister precedent (CS8795 mitigation)

### W31 3rd confirmation — ReplayService (multi-interface: `IReplayService + IDisposable`)

`src/PeakCan.Host.Core/Replay/ReplayService.cs` (265 LoC):
- Implements `IReplayService + IDisposable` (2 interfaces)
- Decomposed into 2 NEW partials in `ReplayService/` subdirectory:
  - `FileIoLifecycle.partial.cs` — `IReplayService.LoadAsync` (31 LoC) + `IReplayService.Reset` (6 LoC)
  - `FrameEmission.partial.cs` — 4 private helpers (`EmitFrame` + `EmitFrameToSinkAsync` + `OnSinkThrewFromTimeline` + `RaisePlaybackEnded`)
- 1 `[LoggerMessage]` partial stays on main per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31 sister precedent (CS8795 mitigation)

## Why multi-interface decomposition works

- **Interface inheritance is preserved across partials**: Each partial class declaration includes the interface implementations (e.g., `public sealed partial class CanApi : IFrameSink, IScriptCanApi`). Partial classes don't fragment interface contracts.
- **Cross-partial visibility works for interface methods**: Interface methods declared in one partial are accessible from other partials via partial-class visibility.
- **Interface dispatch is invariant to partial structure**: The CLR doesn't care how many partial files the class is split into — interface method dispatch works the same way.
- **DI registration unchanged**: Production DI binds `AddSingleton<IReplayService, ReplayService>(...)` and `AddSingleton<IFrameSink, CanApi>(...)` — no changes needed.

## LOCKED decision matrix

| Class shape | Pattern |
|---|---|
| Multi-interface partial god-class (2+ interfaces) | **Same as single-interface**: decompose based on flow boundaries, NOT interface boundaries |
| Single-interface partial god-class | Same: decompose based on flow boundaries |
| Interface-segregated god-class (one partial per interface) | **NOT recommended** — fragments state coupling; defeats partial-class cohesion |

## Sister-precedent

- `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` — 9/3 since 3/3 LOCKED at W25 (W26 OnFrame 62 LoC is 1 of 5 moves)
- `partial-extraction-must-use-original-code-from-head-not-fabricated-api` — 3/3 CONFIRMED at W21 T2
- `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` — 3/3 CONFIRMED at W23 T2 (W26 verified `Action<CanFrame>` callback signature)
- `add-partial-keyword-to-monolithic-class-before-extraction` — 3/3 CONFIRMED at W26.5 (W26 was 1st confirmation)

## Application scope

All future peakcan-host god-class refactors of multi-interface god-classes (2+ interfaces). **LOCKED into MASTER-LESSON-CATALOG at W31 SHIP closure** (capture-decisions file `2026-07-13-w31-replay-service-god-class-ship.md`).

## Out of scope

- Single-interface god-classes — same pattern applies but counted separately under sister-lessons
- Interface-segregated god-classes — NOT recommended pattern (fragments state coupling)
- Generic-type god-classes (different decomposition patterns)
