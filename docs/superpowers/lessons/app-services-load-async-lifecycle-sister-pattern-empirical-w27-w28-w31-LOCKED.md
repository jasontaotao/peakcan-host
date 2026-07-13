# LESSON — App/Services load-async lifecycle sister-pattern (3/3 CONFIRMED → LOCKED)

**Status**: 3/3 CONFIRMED — **LOCKED into MASTER-LESSON-CATALOG** at W31 SHIP closure (2026-07-13)
**Locking observation**: W31 v3.45.0 MINOR ReplayService god-class refactor (3rd confirmation)
**Earlier observations**: W27 RecentSessionsService (1st) + W28 DbcService (2nd) + W31 ReplayService (3rd)

## Pattern (LOCKED)

When refactoring a god-class with a **public `LoadAsync` async method that reads file bytes + parses/decodes content + mutates state + raises event(s)**, decompose into **2 NEW partial-class files** using the **persistence + emission** flow-boundary pattern:

### Partial 1 — `LoadLifecycle.partial.cs` (or `PersistenceOps.partial.cs` / `FileIoLifecycle.partial.cs`)

Public `async Task LoadAsync(string path, CancellationToken ct = default)` method:
- **Defensive reset on entry**: clear state + push to timeline BEFORE the parse attempt (so a failed load leaves the service in a clean "no file loaded" state)
- **File-IO + parse**: open file → parse frames/content → wrap exceptions into load-exception type
- **State mutation**: assign parsed result to backing field
- **Event/signal raise**: notify subscribers of load completion (or no event for silent async load)

### Partial 2 — `FrameEmission.partial.cs` (or `Mutators.partial.cs` / `TextDecoding.partial.cs`)

Helper methods that touch the same state fields:
- Frame emission (for replay-state) OR row encoding helpers (for JSON persistence) OR text decoding (for DBC parsing)
- Mutators that take a lock and call cross-partial helpers from `LoadLifecycle.partial.cs`

## 3 confirmations

### W27 1-of-3 — RecentSessionsService

`src/PeakCan.Host.App/Services/Trace/RecentSessionsService/PersistenceOps.partial.cs`:
- Contains `public Task LoadAsync(int maxCount)` (60 LoC LARGEST method)
- File-IO lifecycle: enumerate recent-session JSON files + deserialize + corrupt-fallback + Mutate state + Raise PropertyChanged event

### W28 2-of-3 — DbcService

`src/PeakCan.Host.App/Services/DbcService/LoadLifecycle.partial.cs`:
- Contains `public virtual async Task LoadAsync(string path)` (79 LoC LARGEST method, moves per W25 D5 deviation since ≥ 60 LoC + discrete flow boundary)
- File-IO lifecycle: `File.ReadAllBytesAsync` + `ReadDbcText` (BOM detection + UTF-8/OEM/Latin-1 fallback) + `DbcParser.Parse` + Mutate `Current` `Volatile.Write` + Raise `DbcLoaded`/`LoadFailed` event

### W31 3-of-3 — ReplayService (LOCKS the pattern)

`src/PeakCan.Host.Core/Replay/ReplayService/FileIoLifecycle.partial.cs`:
- Contains `public async Task LoadAsync(string path, CancellationToken ct = default)` (31 LoC method)
- File-IO lifecycle: defensive-reset-on-entry + `File.OpenRead(PathNormalizer.Normalize(path))` + `AscParser.ParseAsync(fs, ct)` + Mutate `_frames` + delegate to `_timeline.SetFrames(_frames)`

## Why 3-observation LOCK works

- **Core + App coverage**: W27 + W28 are App/Services sisters (RecentSessionsService + DbcService); W31 is Core/Replay sister (ReplayService). The pattern works across both App-layer and Core-layer god-classes.
- **State-mutation pattern**: all 3 confirmations follow the same shape — defensive reset on entry → file-IO → parse/decode → mutate state field → raise event/signal.
- **LARGEST method ≥ 60 LoC**: W27 (60 LoC) + W28 (79 LoC) meet the W25 D5 deviation threshold; W31 (31 LoC) does NOT (per W29 NEW `small-god-class-no-largest-method` 1/3 → 2/3 pattern). The pattern works regardless of whether the LARGEST method moves or stays inline.
- **Async-load shape**: all 3 confirmations are `async Task LoadAsync(...)` with `CancellationToken ct = default` parameter — consistent async-load signature.
- **Exception-wrapping**: all 3 confirmations wrap file-IO + parse exceptions into a domain-specific exception type (`ReplayLoadException` for W31, similar pattern for W27 + W28).

## LOCKED decision matrix

| Class shape | Pattern |
|---|---|
| Core or App god-class with **public async Task LoadAsync** that reads file bytes + parses content + mutates state + raises event | **2-partial split** (LoadLifecycle/PersistenceOps + emission/mutator/text-decoding) |
| App/Services god-class with synchronous file-IO load (no async) | Different pattern — see `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` |
| App/Services god-class with orchestration dispatch + row-encoding helpers | Different pattern — see `app-services-multiframe-layer-sister-pattern-empirical-w30` |
| Core/Replay subsystem with file-IO lifecycle + frame emission | Different pattern — see `core-layer-replay-subsystem-sister-pattern-empirical-w15-w22-w31` |

## Sister-precedent

- `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` — 9/3 since 3/3 LOCKED at W25 (W27 + W28 are 2 of 5 moves; W31 is a small-god-class stay)
- `small-god-class-no-largest-method-keeps-all-inline-default-pattern` — 1/3 → 2/3 PROMOTION at W31 (W31 confirms default D5 applies to small god-classes)
- `partial-extraction-must-use-original-code-from-head-not-fabricated-api` — 3/3 CONFIRMED at W21 T2
- `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` — 3/3 CONFIRMED at W23 T2
- `add-partial-keyword-to-monolithic-class-before-extraction` — 3/3 CONFIRMED at W26.5
- `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` — 3/3 CONFIRMED at W23 T3 (W31 is 10th confirmation)

## Application scope

All future peakcan-host god-class refactors of Core or App god-classes with public async file-load lifecycle methods. **LOCKED into MASTER-LESSON-CATALOG at W31 SHIP closure** (capture-decisions file `2026-07-13-w31-replay-service-god-class-ship.md`).

## Out of scope

- Synchronous file-IO load (no async) — see `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED`
- Orchestration dispatch + row-encoding helpers — see `app-services-multiframe-layer-sister-pattern-empirical-w30`
- Core/Replay subsystem decomposition — see `core-layer-replay-subsystem-sister-pattern-empirical-w15-w22-w31`
- Non-CAN-bus file-IO (different exception-wrapping + state-mutation patterns)
