# LESSON — Small god-class (LARGEST method <50 LoC) keeps all methods inline by default (1/3 NEW observation)

**Status**: NEW 1/3 observation at W29 SHIP closure (2026-07-13)
**Pattern name**: `small-god-class-no-largest-method-keeps-all-inline-default-pattern`
**1st observation**: W29 v3.43.0 MINOR SendFrameLibrary god-class refactor
**Awaiting**: 2 more observations across any future small-god-class refactor to promote to 2/3 → 3/3 CONFIRMED

## Observation

`src/PeakCan.Host.App/Services/SendFrameLibrary.cs` (276 LoC, W29 god-class candidate) had:
- **LARGEST method**: `SaveUnlocked` 24 LoC (atomic temp+rename with `File.Move(tmp, _path, overwrite: true)` + `try/catch IOException|UnauthorizedAccessException|SecurityException` cleanup)
- All other methods: <50 LoC individually (`EnsureLoaded` 9 LoC + `LoadUnlocked` 13 LoC + `Add` 11 LoC + `Remove` 14 LoC + `Save(IEnumerable)` 11 LoC + `Save()` 10 LoC + `Load` 6 LoC + `Count` getter 9 LoC + `DefaultPath` 4 LoC)

**Decision**: NO `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` (W25 D5 deviation) applied. All 9 methods stayed inline OR extracted per **flow-boundary clarity**, NOT LARGEST-method-can-move.

## Default D5 sister-principle applied

Per W12 + W14 + W18 + W19 + W20 + W21 + W22 + W23 D5 default sister-principle: **largest method stays inline** UNLESS:
1. LARGEST method ≥ 60 LoC, AND
2. The LARGEST method maps to a **sharp discrete flow boundary** (frame-arrives → fan-out, file-IO-load → parse-or-empty, tick-fires → per-tick state machine, etc.), AND
3. The method is NOT a single central orchestration loop (those stay inline).

For small god-classes where LARGEST method <50 LoC, the W25 D5 deviation criteria fail on (1) → **NO LARGEST-method-move deviation applied**.

## Why this matters

W29 confirms that the **W25 D5 deviation is bounded** — it's a 2-step check (LARGEST ≥ 60 LoC + discrete flow boundary), NOT a free pass. The "largest method can move" lesson (6/3 since 3/3 LOCKED at W25) explicitly enumerates both outcomes:
- 2 stays (W22 RecordBatchAsync 100 LoC + W23 OnTimerTick 151 LoC — both orchestration loops, stayed inline)
- 4 moves (W25 OnChannelFrame 73 LoC + W26 OnFrame 62 LoC + W27 LoadAsync 60 LoC + W28 LoadAsync 79 LoC — all ≥ 60 LoC + discrete flow boundary)

W29 SendFrameLibrary SaveUnlocked 24 LoC is **too small** to qualify for the deviation → default D5 sister-principle applied → all methods stay inline (or extracted per flow-boundary clarity, not LARGEST-method-can-move).

## Decision matrix (D5)

| LARGEST method LoC | Method shape | D5 decision |
|---|---|---|
| <50 LoC | Any shape (orchestration or discrete) | **Default**: all methods stay inline OR extract per flow-boundary clarity |
| 50-59 LoC | Single orchestration loop | Default: stays inline |
| 50-59 LoC | Sharp discrete flow boundary | Borderline — case-by-case (none yet observed) |
| ≥60 LoC | Single orchestration loop | **Stays inline** (W22 + W23 stays) |
| ≥60 LoC | Sharp discrete flow boundary | **MOVES** to flow partial (W25 + W26 + W27 + W28 moves) |

## Sister-lesson

- `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` — 6/3 since 3/3 LOCKED at W25; HELD at W29 confirms the **bounded** interpretation (≥60 LoC threshold + discrete flow shape).
- `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` — 3/3 CONFIRMED at W29 SHIP closure; sister extraction pattern.

## Application scope

All future peakcan-host god-class refactors of small god-classes (LARGEST method <50 LoC). Awaiting 2 more observations across any future small-god-class refactor to promote to 2/3 → 3/3 CONFIRMED → LOCKED into MASTER-LESSON-CATALOG.

## What to watch for in future refactors

- `SequenceSendService.cs` 266 LoC (App/Services/MultiFrame — W30 candidate)
- `ReplayService.cs` 265 LoC (Core/Replay — W30 candidate)
- `StatsViewModel.cs` 263 LoC (App/ViewModels — W30 candidate)
- `SendViewModel.cs` 257 LoC (App/ViewModels — W30 candidate)
- Lower-LoC App-layer god-classes in 240-249 LoC range

For each W30+ candidate, the LARGEST method LoC should be checked first. If <50 LoC → W29 NEW pattern applies (default D5, no LARGEST-method deviation).

## Out of scope

- App/ViewModels (different layer, different concerns — see W21+W24+W25 sister precedent)
- Core-layer god-classes (W22 + W23 stays — orchestration loops)
- Infrastructure/Channel layer (W18 + W25 sister precedent — different decomposition pattern)
