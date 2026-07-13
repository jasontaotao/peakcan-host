# LESSON — Small god-class (LARGEST method <50 LoC) keeps all methods inline by default (1/3 → 2/3 PROMOTION)

**Status**: 1/3 → 2/3 PROMOTION at W31 SHIP closure (2026-07-13)
**Pattern name**: `small-god-class-no-largest-method-keeps-all-inline-default-pattern`
**2 observations**: W29 v3.43.0 MINOR SendFrameLibrary (1st) + W31 v3.45.0 MINOR ReplayService (2nd)
**Awaiting**: 1 more observation across any future small-god-class refactor to promote to 3/3 CONFIRMED → LOCKED into MASTER-LESSON-CATALOG

## 2 observations

### W29 1-of-2 — SendFrameLibrary god-class refactor

`src/PeakCan.Host.App/Services/SendFrameLibrary.cs` (276 LoC, W29 god-class candidate) had:
- **LARGEST method**: `SaveUnlocked` 24 LoC (atomic temp+rename with `File.Move(tmp, _path, overwrite: true)` + `try/catch IOException|UnauthorizedAccessException|SecurityException` cleanup)
- All other methods: <50 LoC individually (`EnsureLoaded` 9 LoC + `LoadUnlocked` 13 LoC + `Add` 11 LoC + `Remove` 14 LoC + `Save(IEnumerable)` 11 LoC + `Save()` 10 LoC + `Load` 6 LoC + `Count` getter 9 LoC + `DefaultPath` 4 LoC)

**Decision**: NO `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` (W25 D5 deviation) applied. All 9 methods stayed inline OR extracted per **flow-boundary clarity**, NOT LARGEST-method-can-move.

### W31 2-of-2 — ReplayService god-class refactor (CONFIRMS W29 1/3 observation)

`src/PeakCan.Host.Core/Replay/ReplayService.cs` (265 LoC, W31 god-class candidate) had:
- **LARGEST method**: `LoadAsync` 31 LoC (defensive-reset-on-entry + file open + AscParser.ParseAsync + exception-wrapping into ReplayLoadException)
- **2nd-largest**: `EmitFrame` 39 LoC (tri-state CAN-ID filter + Task.Run fire-and-forget sink dispatch + FrameEmitted event raise)

**Decision**: NO `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` (W25 D5 deviation) applied. Both LARGEST methods < 50 LoC → default D5 sister-principle applied. Decomposed into 2 NEW partials (`FileIoLifecycle` + `FrameEmission`) per **flow-boundary clarity** (file-IO lifecycle vs frame emission cluster).

## Default D5 sister-principle applied

Per W12 + W14 + W18 + W19 + W20 + W21 + W22 + W23 D5 default sister-principle: **largest method stays inline** UNLESS:
1. LARGEST method ≥ 60 LoC, AND
2. The LARGEST method maps to a **sharp discrete flow boundary** (frame-arrives → fan-out, file-IO-load → parse-or-empty, tick-fires → per-tick state machine, etc.), AND
3. The method is NOT a single central orchestration loop (those stay inline).

For small god-classes where LARGEST method <50 LoC, the W25 D5 deviation criteria fail on (1) → **NO LARGEST-method-move deviation applied**.

## Decision matrix (D5)

| LARGEST method LoC | Method shape | D5 decision | Observations |
|---|---|---|---|
| **<50 LoC** | Any shape (orchestration or discrete) | **Default**: all methods stay inline OR extract per flow-boundary clarity | **W29 (24 LoC) + W31 (31 LoC) = 2 confirmations** |
| 50-59 LoC | Single orchestration loop | Default: stays inline | (none yet observed) |
| 50-59 LoC | Sharp discrete flow boundary | Borderline — case-by-case | (none yet observed) |
| ≥60 LoC | Single orchestration loop | **Stays inline** | W22 (100 LoC RecordBatchAsync) + W23 (151 LoC OnTimerTick) = 2 stays |
| ≥60 LoC | Sharp discrete flow boundary | **MOVES** to flow partial | W25 (73 LoC) + W26 (62 LoC) + W27 (60 LoC) + W28 (79 LoC) + W30 (91 LoC) = 5 moves |

## Why W31 confirms W29 1/3 observation

W31 ReplayService LARGEST method 31 LoC is **also** < 50 LoC threshold, so default D5 sister-principle applies. Both god-classes had LARGEST methods that could **plausibly** be considered for W25 D5 deviation (`SaveUnlocked` 24 LoC is sharp discrete flow = file-IO atomic-rename; `LoadAsync` 31 LoC is sharp discrete flow = file-IO + parse lifecycle), but the **bounded interpretation** of the W25 D5 deviation requires LARGEST ≥ 60 LoC threshold as a hard floor.

W31 confirms the **bounded interpretation** is correct: W25 D5 deviation is NOT a free pass for any sharp discrete flow method — it's strictly bounded by LARGEST ≥ 60 LoC threshold.

## Sister-lesson (LOCKED pattern)

- `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` — 9/3 since 3/3 LOCKED at W25; held at W31 (W31 confirms bounded interpretation; 5 moves + 4 stays including 2 small-god-class stays)
- `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` — 3/3 CONFIRMED LOCKED at W29.5
- `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28-w31-LOCKED` — 3/3 CONFIRMED LOCKED at W31
- `app-services-multiframe-layer-sister-pattern-empirical-w30` — 1/3 NEW at W30

## Application scope

All future peakcan-host god-class refactors of small god-classes (LARGEST method <50 LoC). Awaiting 1 more observation across any future small-god-class refactor to promote to 3/3 CONFIRMED → LOCKED into MASTER-LESSON-CATALOG.

## What to watch for in future refactors

- `StatsViewModel.cs` 263 LoC (App/ViewModels — W32 candidate, LARGEST method = ctor 84 LoC, which is ≥ 60 LoC → W25 D5 deviation criteria check applies; NOT a small god-class)
- `DbcSendViewModel.cs` 238 LoC (App/ViewModels — sister of W24, already partial, below threshold)
- Any future god-class where LARGEST method < 50 LoC → this pattern applies
- Any future god-class where LARGEST method is between 50-59 LoC → borderline case, needs case-by-case judgment

## Out of scope

- App/ViewModels (different layer, different concerns — see W21+W24+W25 sister precedent)
- Core-layer god-classes (W22 + W23 stays — orchestration loops; W31 ReplayService is a small god-class per W29 pattern)
- Infrastructure/Channel layer (W18 + W25 sister precedent — different decomposition pattern)
- App/Services stateful services with file-IO (W22 + W27 + W28 + W29 sister pattern — different decomposition shape)
