# LESSON — App/Services JSON-persistence layer sister-pattern (3/3 CONFIRMED → LOCKED)

**Status**: 3/3 CONFIRMED — **LOCKED into MASTER-LESSON-CATALOG** at W29 SHIP
**Locking observation**: W29 v3.43.0 MINOR SendFrameLibrary god-class refactor (2026-07-13)
**Earlier observations**: W22 RecordService (1/3) + W27 RecentSessionsService (2/3) + W29 SendFrameLibrary (3/3)

## Pattern (LOCKED)

When refactoring an **App/Services god-class** whose responsibility is **persisting user-facing JSON state** (saved frames, recent sessions, recorded traces, etc.) to `%APPDATA%\<AppName>\*.json`, decompose into **3 partial-class files** using the **persistence + mutator + static-helper** flow-boundary pattern:

### Partial 1 — `PersistenceFlow.partial.cs` (or `PersistenceOps.partial.cs` / `LoadLifecycle.partial.cs`)

Private file-IO lifecycle helpers:
- `EnsureLoaded()` — cache-init sentinel (early-return if `_cachedCount >= 0` or equivalent)
- `LoadUnlocked()` (or `LoadAsync()` for virtual async variant) — JSON deserialize with corrupt-fallback
- `SaveUnlocked()` (or `SaveAsync()` for virtual async variant) — atomic temp-file + `File.Move(tmp, _path, overwrite: true)` pattern

### Partial 2 — `Mutators.partial.cs`

Public mutator methods that take the `lock (_gate)`:
- `Load()` + `Save(...)` + `Save()` parameterless + `Add(...)` + `Remove(...)` + `Count` getter
- Each mutator calls cross-partial helpers from `PersistenceFlow.partial.cs` (`EnsureLoaded` + `LoadUnlocked` + `SaveUnlocked`)
- All methods stay <50 LoC individually → default D5 sister-principle (NO W25 D5 deviation)

### Partial 3 — `StaticHelpers.partial.cs`

`private static string DefaultPath()` resolves `%APPDATA%\<AppName>\<file>.json` via `Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)` + `Path.Combine(appData, "PeakCan.Host", "<file>.json")`.

## Sister-extraction sequence

W22 (RecordService, 2 partials: Lifecycle + Mutators) → W27 (RecentSessionsService, 3 partials: PersistenceOps + Mutators + StaticHelpers) → W29 (SendFrameLibrary, 3 partials: PersistenceFlow + Mutators + StaticHelpers).

## Why 3-partial decomposition works (3/3 observations)

- **Flow boundaries are stable**: file-IO (`PersistenceFlow`) + lock-gated mutators (`Mutators`) + static path resolution (`StaticHelpers`) cleanly cluster with zero cross-flow entanglement.
- **All public API stays on `Mutators.partial.cs`**: callers see unchanged signatures; the public API surface is the lock-gated boundary, not the persistence internals.
- **`[LoggerMessage]` partials stay on main**: per W18 + W22 + W23 + W25 + W26 + W27 + W28 + W29 sister precedent (CS8795 mitigation). 2 `[LoggerMessage]` partials (`LogCorrupt` + `LogSaveUnlockedFailed`) called from `LoadUnlocked` + `SaveUnlocked` in `PersistenceFlow` all compile clean via cross-partial call resolution.
- **Cross-partial helper visibility works for 3 partials**: 5th confirmation (1st was W22 with 2 partials, 2nd was W27 with 3 partials, 3rd is W29 with 3 partials).

## LOCKED decision matrix

| Class shape | Pattern |
|---|---|
| App/Services god-class with JSON-persistence + lock-protected mutators + `Environment.SpecialFolder.ApplicationData` path | **3-partial split** (PersistenceFlow + Mutators + StaticHelpers) |
| App/Services god-class with TOML/DBC parser persistence | 2-partial split (LoadLifecycle + TextDecoding) — see W28 DbcService |
| App/Services god-class with async file-load lifecycle | 2-partial split (PersistenceOps + Mutators) — see W27 RecentSessionsService LoadAsync |

## Sister-precedent

- `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` — 3/3 CONFIRMED at W23 T3
- `partial-extraction-must-use-original-code-from-head-not-fabricated-api` — 3/3 CONFIRMED at W21 T2
- `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` — 3/3 CONFIRMED at W23 T2
- `subdirectory-partials-pattern-empirical-26-precedents` — 3/3 CONFIRMED at W20 T3
- `add-partial-keyword-to-monolithic-class-before-extraction` — 3/3 CONFIRMED at W26.5
- `multi-interface-partial-class-iframesink-and-iscriptcanapi` — 2/3 HELD at W26.5

## Application scope

All future peakcan-host god-class refactors of App/Services classes with JSON-persistence + lock-protected mutators + `%APPDATA%` path resolution. LOCKED into MASTER-LESSON-CATALOG at W29 SHIP closure (capture-decisions file `2026-07-13-w29-send-frame-library-god-class-ship.md`).

## Out of scope

- Core-layer persistence (different layer, different concerns — see W22 Core precedent where partial shape is 2 not 3)
- Infrastructure/Channel layer sister-pattern (see `infrastructure-channel-layer-sister-pattern-empirical-w18-w25` 2/3 HELD at W25)
- App/ViewModels layer (no persistence concerns at ViewModel layer)
