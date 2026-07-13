# LESSON — App/Services Scripting subsystem sister-pattern (NEW 1/3 observation)

**Status**: NEW 1/3 observation at W32 SHIP closure (2026-07-13)
**Pattern name**: `app-services-scripting-sister-pattern-empirical-w14-w26-w32`
**1st observation**: W32 v3.46.0 MINOR DbcApi god-class refactor
**Awaiting**: 2 more observations across any future App/Services/Scripting god-class refactor to promote to 2/3 → 3/3 CONFIRMED → LOCKED

## Observation

`src/PeakCan.Host.App/Services/Scripting/DbcApi.cs` (279 LoC, W32 god-class candidate) was the **1st explicit App/Services/Scripting subsystem god-class refactor** designed to decompose the `DbcApi` (the JS scripting engine's DBC operations surface) into 2 NEW partials based on **Load vs Query** flow-boundary clarity.

### Sister-extraction sequence (App/Services/Scripting subsystem)

- **W14 ScriptEngine** (already partial: `ScriptEngine/ExecutionLifecycleFlow.cs` 274 LoC): owns the JS execution lifecycle + script engine runtime
- **W26 CanApi** (3 partials: SinkLifecycle + CallbackRegistry + SendAndQuery): exposes CAN-bus operations to the scripting engine
- **W32 DbcApi** (2 partials: LoadFlow + QueryFlow): exposes DBC decoding operations to the scripting engine

W14 + W26 + W32 are all App/Services/Scripting subsystem god-classes; they expose different operations to the same scripting engine (ClearScript V8 binder).

## Pattern (1st observation)

When refactoring an **App/Services/Scripting subsystem god-class** (a class that exposes operations to the JS scripting engine via the ClearScript V8 binder), decompose into **2 NEW partial-class files** using the **Load+state vs Query+decode** flow-boundary pattern:

### Partial 1 — `LoadFlow.partial.cs`

Public `async Task<object> Load(string path, CancellationToken ct = default)` method:
- **Sharp discrete flow boundary**: Load → return result envelope with 4 distinct result paths (success / LoadFailed-surfaced-error / Cancelled / Exception)
- **Delegates to internal service**: `_service.LoadAsync(path, ct).ConfigureAwait(false)` (the wrapper god-class delegates to the underlying service)
- **Reads state from main**: `_currentDocument` (Volatile.Write'd by `OnLoaded` event handler in main) + `_lastLoadError` (set by `OnFailed` event handler in main)
- **Result envelope**: anonymous object with `success` + `messageCount` + `errorCode` + `error` fields for script caller

### Partial 2 — `QueryFlow.partial.cs`

3 public query methods:
- `Decode(frame)` → signal values + cache update (writes to `_signalValues` ConcurrentDictionary in main)
- `GetSignal(messageName, signalName)` → most recent value lookup (reads `_signalValues`)
- `GetMessages()` → list all messages (reads `_currentDocument`)

## Why 2-partial decomposition works (1st observation)

- **Flow boundaries are stable**: Load+state (`LoadFlow`) + Query+decode (`QueryFlow`) cleanly cluster with zero cross-flow entanglement.
- **Event handlers stay in main**: `_service.Loaded` + `_service.Failed` event subscriptions + `OnLoaded` + `OnFailed` event handlers + `Dispose` event cleanup all stay in main (event lifecycle tied to the class instance, not to Load vs Query flow).
- **`[LoggerMessage]` partials stay on main**: 2 `[LoggerMessage]` partial declarations stay on `DbcApi` main partial declaration per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31+W32 sister precedent (CS8795 mitigation). Called from `Load` (in LoadFlow partial) — cross-partial call resolution handles this automatically.
- **Cross-partial helper visibility works for 2 partials**: 9th confirmation (1st was W22, 2nd was W23, 3rd was W26, 4th was W27, 5th was W28, 6th was W29, 7th was W30, 8th was W31, 9th is W32) that the cross-partial helper visibility pattern is stable across multi-partial god-class extractions.
- **Multi-interface sister**: W32 DbcApi is single-interface `IScriptDbcApi` (sister of W26 CanApi multi-interface `IFrameSink + IScriptCanApi` + W31 ReplayService multi-interface `IReplayService + IDisposable`). Per `multi-interface-partial-class-empirical-w26-w31-LOCKED`, the multi-interface vs single-interface decision is INDEPENDENT of flow-boundary decomposition.

## Decision matrix (D1)

| Class shape | Pattern |
|---|---|
| App/Services/Scripting god-class with **Load+state vs Query+decode** flow | **2-partial split** (LoadFlow + QueryFlow) — W32 pattern |
| App/Services/Scripting god-class with **multi-interface callback-registry** shape | **3-partial split** (SinkLifecycle + CallbackRegistry + SendAndQuery) — W26 CanApi pattern |
| App/Services god-class with **orchestration dispatch + row-encoding helpers** | **2-partial split** (SendFlow + RowBuildFlow) — see `app-services-multiframe-layer-sister-pattern-empirical-w30` |
| App/Services god-class with **JSON-persistence + lock-protected mutators + `%APPDATA%` path** | **3-partial split** (PersistenceFlow + Mutators + StaticHelpers) — see `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` |
| Core or App god-class with **public async LoadAsync** that reads file bytes + parses content + mutates state + raises event | **2-partial split** (LoadLifecycle/PersistenceOps + emission/mutator/text-decoding) — see `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28-w31-LOCKED` |
| Core/Replay subsystem god-class | **2-partial split** (FileIoLifecycle + FrameEmission) — see `core-layer-replay-subsystem-sister-pattern-empirical-w15-w22-w31` |
| Core god-class with **orchestration loop** | Stays inline or different decomposition — see W22 + W23 stays |

## Sister-precedent

- `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28-w31-LOCKED` — 3/3 CONFIRMED LOCKED at W31.5 (W32 DbcApi `Load` is sync-result-envelope, not async file-load lifecycle; observation N/A for that pattern)
- `multi-interface-partial-class-empirical-w26-w31-LOCKED` — 3/3 CONFIRMED LOCKED at W31.5 (W32 DbcApi is single-interface `IScriptDbcApi`; flow-boundary decomposition applies INDEPENDENTLY of single vs multi-interface)
- `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` — 10/3 since 3/3 LOCKED at W25 (W32 Load 73 LoC = 6th move in 10 observations)
- `partial-extraction-must-use-original-code-from-head-not-fabricated-api` — 3/3 CONFIRMED at W21 T2
- `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` — 3/3 CONFIRMED at W23 T2 (W32 = 15th observation)
- `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` — 3/3 CONFIRMED at W23 T3 (W32 = 11th confirmation)
- `add-partial-keyword-to-monolithic-class-before-extraction` — 3/3 CONFIRMED at W26.5 (W32 already partial at L20)

## Application scope

All future peakcan-host god-class refactors of App/Services/Scripting subsystem classes (classes that expose operations to the JS scripting engine via the ClearScript V8 binder). Awaiting 2 more observations across any future App/Services/Scripting god-class refactor to promote to 2/3 → 3/3 CONFIRMED → LOCKED into MASTER-LESSON-CATALOG.

## What to watch for in future refactors

- `ScriptEngine.cs` further decomposition (currently 1 partial `ExecutionLifecycleFlow.cs`; main residual may need further splitting)
- Any new App/Services/Scripting class added in future feature work
- Sister-extraction in other Scripting subsystems that adopt the Load+state vs Query+decode flow-boundary shape

## Out of scope

- App/ViewModels (different layer, different concerns — see W21+W24+W25 sister precedent)
- Core-layer god-classes (W22 + W23 + W18 + W25 + W31 sisters — different decomposition patterns)
- Infrastructure/Channel layer (W18 + W25 sister precedent — different decomposition pattern)
- App/Services stateful services with file-IO (W22 + W27 + W28 + W29 sister pattern — different decomposition shape)
- Multi-interface partial classes (different concern — see `multi-interface-partial-class-empirical-w26-w31-LOCKED`; flow-boundary decomposition applies INDEPENDENTLY)
