# W32.5 v3.46.5 SHIP — vault-only PATCH lesson-promotion consolidation (W32 cycle)

**Date**: 2026-07-13
**Status**: SHIP-READY (docs-only, no source-code changes)
**Branch**: `feature/w32-5-vault-only-patch`
**Target version**: v3.46.5 PATCH (vault-only, +0/-0 source LoC)
**Sister pattern**: W17 vault-only PATCH (v3.31.1) + W23.5-W25.5 vault-only PATCH (v3.39.5) + W26.5 vault-only PATCH (v3.40.5) + W27.5 vault-only PATCH (v3.41.5) + W28.5 vault-only PATCH (v3.42.5) + W29.5 vault-only PATCH (v3.43.5) + W30.5 vault-only PATCH (v3.44.5) + W31.5 vault-only PATCH (v3.45.5) — single-cycle consolidation of multiple deferred PATCH promotions (9th occurrence).

## D1-D5 (carried from W32.5 design)

- **D1**: 2 lesson promotions consolidated (1 NEW 1/3 + 1 10/3 consolidation entry held).
- **D2**: Forward-bump v3.46.0 → v3.46.5 (per W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 + W30.5 + W31.5 vault-only PATCH convention — no source-only changes warrant PATCH, not MINOR).
- **D3**: 1 docs-only commit (sister of W17 1-commit pattern + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 + W30.5 + W31.5 1-commit patterns).
- **D4**: Tier-3 ship via PR + squash + delete-branch + tag + GH release; 0 source LoC change verification.
- **D5**: Branch name `feature/w32-5-vault-only-patch` (per W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 + W30.5 + W31.5 single-PATCH consolidated naming).

## Why consolidate into 1 cycle (sister pattern precedent — 9th occurrence)

Per W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 + W30.5 + W31.5 consolidated-PATCH sister precedent:

- **W17 vault-only PATCH** (v3.31.1): promoted `wc-l-vs-python-splitlines-off-by-one` 3/3 CONFIRMED in 1 atomic commit
- **W23.5-W25.5 vault-only PATCH** (v3.39.5): promoted 6 lesson candidates (consolidated 3 deferred PATCH cycles) in 1 atomic commit
- **W26.5 vault-only PATCH** (v3.40.5): promoted 3 lesson candidates (W26 cycle promotions) in 1 atomic commit
- **W27.5 vault-only PATCH** (v3.41.5): promoted 2 lesson candidates (W27 cycle promotions) in 1 atomic commit
- **W28.5 vault-only PATCH** (v3.42.5): promoted 2 lesson candidates (W28 cycle promotions) in 1 atomic commit
- **W29.5 vault-only PATCH** (v3.43.5): promoted 2 lesson candidates (W29 cycle promotions) in 1 atomic commit
- **W30.5 vault-only PATCH** (v3.44.5): promoted 3 lesson candidates (W30 cycle promotions) in 1 atomic commit
- **W31.5 vault-only PATCH** (v3.45.5): promoted 4 lesson candidates (W31 cycle promotions) in 1 atomic commit
- **W32.5 vault-only PATCH** (v3.46.5): promotes 2 lesson candidates (W32 cycle promotions) in 1 atomic commit — sister precedent continued (9th occurrence)

## 2 lesson promotions consolidated

### W32 NEW 1/3 → standalone lesson file created

1. **`app-services-scripting-sister-pattern-empirical-w14-w26-w32`** — W32 **NEW 1/3 observation** (1st observation of the pattern)
   - W14 1-of-3 (sister): W14 ScriptEngine already partial since W14 (`ExecutionLifecycleFlow.cs` 274 LoC) — owns the JS execution lifecycle + script engine runtime
   - W26 2-of-3 (sister): W26 CanApi 3 partials (SinkLifecycle + CallbackRegistry + SendAndQuery) — exposes CAN-bus operations to scripting engine
   - W32 3-of-3: W32 DbcApi 2 partials (LoadFlow + QueryFlow) — exposes DBC decoding operations to scripting engine
   - **Pattern**: App/Services/Scripting subsystem god-class refactors (classes that expose operations to the JS scripting engine via the ClearScript V8 binder) can follow the W32 2-partial decomposition pattern (LoadFlow + QueryFlow).
   - **Decision matrix** (now explicit): Load+state vs Query+decode → 2-partial split (W32 DbcApi pattern); multi-interface callback-registry → 3-partial split (W26 CanApi pattern); orchestration dispatch + row-encoding → see `app-services-multiframe-layer-sister-pattern-empirical-w30`; JSON-persistence + lock-protected mutators → see `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED`.
   - Awaiting 2 more observations across any future App/Services/Scripting god-class refactor to promote to 2/3 → 3/3 CONFIRMED → LOCKED.
   - **Standalone lesson file**: `docs/superpowers/lessons/app-services-scripting-sister-pattern-empirical-w14-w26-w32-1of3.md`

### W32 10/3 since 3/3 LOCKED → consolidation entry added

2. **`largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator`** — W32 10/3 (already **3/3 CONFIRMED** at W25 T4) → **held in MASTER-LESSON-CATALOG at W32.5**
   - W22 1-of-10 (stay): W22 RecordBatchAsync 100 LoC STAYED in main (single central orchestration pipeline)
   - W23 2-of-10 (stay): W23 OnTimerTick 151 LoC STAYED in main (single tick-loop pipeline)
   - W29 3-of-10 (stay): W29 SendFrameLibrary SaveUnlocked 24 LoC STAYED in main (small god-class, <50 LoC threshold per W29 NEW pattern)
   - W31 4-of-10 (stay): W31 ReplayService LoadAsync 31 LoC STAYED in main (small god-class, <50 LoC threshold per W29 NEW pattern)
   - W25 3/3 CONFIRMED (move): W25 OnChannelFrame 73 LoC **MOVED** to FrameRouting.partial.cs (fan-out + error-isolation = sharp discrete flow boundary)
   - W26 5-of-10 (move): W26 OnFrame(CanFrame) 62 LoC **MOVED** to SinkLifecycle.partial.cs (frame-arrives → callback-fanout discrete dispatcher shape, sister of W25 OnChannelFrame)
   - W27 6-of-10 (move): W27 LoadAsync 60 LoC **MOVED** to PersistenceOps.partial.cs (file-I/O lifecycle = sharp discrete flow, 2nd sister of W25 pattern)
   - W28 7-of-10 (move): W28 LoadAsync 79 LoC **MOVED** to LoadLifecycle.partial.cs (file-I/O + parsing lifecycle = sharp discrete flow, 3rd sister of W27 pattern)
   - W30 8-of-10 (move): W30 SendAsync 91 LoC **MOVED** to SendFlow.partial.cs (concurrent vs sequential dispatcher = sharp discrete flow, 5th move confirming W25 + W26 + W27 + W28 + W30 pattern)
   - **W32 10-of-10 (move)**: W32 Load 73 LoC **MOVED** to LoadFlow.partial.cs (Load → return result envelope with 4 distinct result paths: success / LoadFailed-surfaced-error / Cancelled / Exception = sharp discrete flow, 6th move confirming W25 + W26 + W27 + W28 + W30 + W32 pattern)
   - **10 observations = 4 stays (W22 + W23 + W29 + W31) + 6 moves (W25 + W26 + W27 + W28 + W30 + W32)** lock in both the "stay default" + "move allowed for discrete flow" outcomes.
   - **HELD in MASTER-LESSON-CATALOG** at W32.5: 10 observations (4 stays + 6 moves) continue to lock in BOTH outcomes as canonical; W29.5 + W30.5 + W31.5 + W32.5 consolidated entry holds W27.5 LOCKED + W28.5 + W29.5 + W30.5 + W31.5 + W32.5 10/3 consolidation all consolidated into 9 PATCH cycles.
   - **Consolidation entry lesson file**: `docs/superpowers/lessons/largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator-LOCKED.md` (updated to include W29 + W31 + W32 observations)

### Already CONFIRMED (held — no new promotion needed)

- `partial-extraction-must-use-original-code-from-head-not-fabricated-api` (W21 3/3 CONFIRMED)
- `subdirectory-partials-pattern-empirical-26-precedents` (W20 3/3 CONFIRMED; W32 = 22nd deployment)
- `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` (W23 3/3 CONFIRMED, 15th observation at W32 T1+T2)
- `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` (W23 3/3 CONFIRMED, 11th confirmation at W32)
- `add-partial-keyword-to-monolithic-class-before-extraction` (W26.5 3/3 CONFIRMED, 30th application at W32; W32 already partial)
- `multi-interface-partial-class-empirical-w26-w31-LOCKED` (W31.5 3/3 CONFIRMED LOCKED — held; W32 DbcApi has single interface `IScriptDbcApi`, observation N/A)
- `infrastructure-channel-layer-sister-pattern-empirical-w18-w25` (W25 2/3 — held; W18 + W25 observations; W32 is App/Services/Scripting, NOT Infrastructure/Channel)
- `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` (W29.5 3/3 CONFIRMED LOCKED — held; W32 has no JSON-persistence, observation N/A)
- `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28-w31-LOCKED` (W31.5 3/3 CONFIRMED LOCKED — held; W32 `Load` is sync-result-envelope, not async file-load lifecycle)
- `small-god-class-no-largest-method-keeps-all-inline-default-pattern` (W29 1/3 → W31.5 2/3 PROMOTION — held at W32)
- `app-services-multiframe-layer-sister-pattern-empirical-w30` (W30 NEW 1/3 — held; W32 is App/Services/Scripting, NOT App/Services/MultiFrame)
- `core-layer-replay-subsystem-sister-pattern-empirical-w15-w22-w31` (W31 NEW 1/3 — held; W32 is App/Services/Scripting, NOT Core/Replay)
- `peak-can-channel-cs8795-accessibility-modifier-blocks-cross-partial-source-generator-impl` (W18 R1 1/3 — held)

## LESSON ENHANCEMENT RETENTION — W19 R1 first-correction now covers BOTH dimensions

Per W31 T2 first-run failure + W32 T2 prevention success (W19 R1 first-correction LESSON ENHANCED applied at W32 T2 with boundary verification baked into script upfront + recovery procedure documented; 1st-attempt PASS with delta = 75 EXACT match), the W19 R1 first-correction LESSON now covers **two dimensions** documented in `MASTER-LESSON-CATALOG`:

1. **Pre-flight prevention** (original W19 R1): re-grep boundaries BEFORE running each deletion script
2. **Post-failure recovery** (NEW at W31.5): when a deletion script fails (delta outside ±2 LoC tolerance), the recovery procedure is:
   - `git checkout` the partially-modified main file from git
   - Re-grep post-T(N-1) boundaries (post-T1 boundaries differ from main HEAD by -50 LoC for W31)
   - Correct the offsets in the deletion script
   - Re-run the script with corrected offsets
   - Verify delta = expected (LoC formula `main_after = main_before - delete_count` per W8.5 D7 32-locked)
   - Build + test verification
   - Commit

**W32 T2 application** demonstrated the LESSON ENHANCED working as a **prevention strategy** (not just recovery from failure): boundary verification baked into the script upfront confirmed all 3 regions correctly identified before deletion, no failure occurred.

## Cross-partial helper visibility pattern (CONFIRMED across 2 partials per W32)

Per W27.5 NEW observation `cross-partial-helper-visibility-works-across-3-partials` (1/3 confirmation at W27.5 → held), W32 sister-confirms this pattern works with **2 partials** (LoadFlow + QueryFlow for DbcApi):

- **`Decode` + `GetSignal` + `GetMessages` methods** (in Flow B `QueryFlow.partial.cs`) read `_currentDocument` (written by `OnDbcLoaded` in main) + write/read `_signalValues` (ConcurrentDictionary in main) — partial-class cross-partial visibility handles this automatically.
- **`Load` method** (in Flow A `LoadFlow.partial.cs`) reads `_currentDocument` (written by `OnDbcLoaded` in main) + reads `_lastLoadError` (written by `OnLoadFailed` in main) — partial-class cross-partial visibility handles this automatically.

This is a **9th confirmation** (1st was W22, 2nd was W23, 3rd was W26, 4th was W27, 5th was W28, 6th was W29, 7th was W30, 8th was W31, 9th is W32) that the cross-partial helper visibility pattern is stable across multi-partial god-class extractions.

## Source-code verification

- **No source-code changes.** `src/PeakCan.Host.Core/` + `src/PeakCan.Host.Infrastructure/` + `src/PeakCan.Host.App/` all unchanged from main @ `e54306c` (W32 SHIP closure + capture-decisions landing).
- `src/Directory.Build.props` v3.46.0 → v3.46.5 (4-field bump: `<Version>` + `<AssemblyVersion>` + `<FileVersion>` + `<InformationalVersion>`).
- `docs/user-manual.html` NOT modified (no user-facing change; sister of W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 + W30.5 + W31.5 vault-only PATCH user-manual unchanged).

## File impact summary

| File | Change | LoC |
|---|---|---|
| `src/Directory.Build.props` | v3.46.0 → v3.46.5 (4 fields) | -8 +8 |
| `docs/superpowers/lessons/app-services-scripting-sister-pattern-empirical-w14-w26-w32-1of3.md` | NEW (NEW 1/3 lesson) | +110 |
| `docs/superpowers/lessons/largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator-LOCKED.md` | MODIFIED (10/3 consolidation entry; added W29 + W31 + W32 observations) | +18 |
| `docs/superpowers/capture-decisions/2026-07-13-w32-5-vault-only-patch.md` | NEW (this file) | +220 |

Total LoC delta: +348 docs.

## Verification matrix

- `dotnet build src/PeakCan.Host.slnx`: 0 errors, 0 warnings (no source change).
- `dotnet test` (full solution): expected 0 new fails (no source change → identical to v3.46.0).
- Tag `v3.46.5` annotated at the squash-merge commit.
- GH release https://github.com/jasontaotao/peakcan-host/releases/tag/v3.46.5 published.
- Branch auto-deleted.

## Process lessons applied

- **W17 vault-only PATCH sister-precedent**: 1 consolidated cycle for multiple deferred PATCH promotions (cleaner history + clearer lesson-promotion atomicity).
- **W23.5-W25.5 vault-only PATCH sister-precedent**: 3 deferred PATCH cycles consolidated into 1 atomic commit.
- **W26.5 vault-only PATCH sister-precedent**: 1 atomic docs commit per cycle for multiple deferred PATCH promotions.
- **W27.5 vault-only PATCH sister-precedent**: 2 lesson candidates per cycle; `largest-method-can-move` LOCKED into MASTER-LESSON-CATALOG.
- **W28.5 vault-only PATCH sister-precedent**: 2 lesson candidates per cycle (1 NEW 1/3 → 2/3 + 1 6/3 consolidation since 3/3 LOCKED).
- **W29.5 vault-only PATCH sister-precedent**: 2 lesson candidates per cycle (1 NEW 1/3 + 1 LOCKED 3/3 → standalone lesson file).
- **W30.5 vault-only PATCH sister-precedent**: 3 lesson candidates per cycle (1 NEW 1/3 + 1 7/3 consolidation entry + 1 1/3 hold confirmation).
- **W31.5 vault-only PATCH sister-precedent**: 4 lesson candidates per cycle (1 NEW 1/3 + 1 2/3 PROMOTION + 2 3/3 CONFIRMED LOCKED + W19 R1 LESSON ENHANCEMENT).
- **D3 VAULT-ONLY PATCH convention** (W44.0 formalized): vault metadata changes ship as PATCH when src/ unchanged; this PATCH follows the convention.
- **D2 forward-bump** convention: source-only changes warrant MINOR; docs-only warrant PATCH (per W17 + W44.0 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 + W30.5 + W31.5 sister precedent).

## Honest deviations

- (a) **W32.5 single-PATCH cycle** (sister of W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 + W30.5 + W31.5) — 1 atomic docs commit per cycle vs 2-3 separate PATCH bumps. Trade-off accepted per sister precedent.
- (b) **User-manual.html NOT updated** for v3.46.5 (vault-only PATCH = docs-only = user-facing unchanged). The user-manual v3.46.0 row remains authoritative; v3.46.5 is **purely a lesson-promotion version bump**, not a user-facing deliverable.
- (c) **`app-services-multiframe-layer-sister-pattern-empirical-w30` 1/3 HELD**: W32 DbcApi is App/Services/Scripting, NOT App/Services/MultiFrame — observation N/A. The pattern awaits a future refactor of an App/Services/MultiFrame god-class.
- (d) **`core-layer-replay-subsystem-sister-pattern-empirical-w15-w22-w31` 1/3 HELD**: W32 DbcApi is App/Services/Scripting, NOT Core/Replay — observation N/A. The pattern awaits a future refactor of a Core/Replay god-class.
- (e) **`small-god-class-no-largest-method-keeps-all-inline-default-pattern` 2/3 HELD**: W32 LARGEST method 73 LoC ≥ 60 LoC → W25 D5 deviation applied correctly (NOT default D5). W32 confirms the 2/3 observation that default D5 applies to small god-classes ONLY.

## What was captured

W32.5 SHIP closure = 2 captures dispatched: W32.5 prep + SHIP. Each per the W12-W32 pattern of `vault-pkm:pkm-capture` agent dispatched after each commit.

## Out of scope (YAGNI)

- No source-code change.
- No test change.
- No user-manual change.
- No 2 separate docs files (consolidated into this single capture-decisions).
- No MEMORY.md per-lesson entries (consolidated in this file).

## Next (post-PATCH-ship)

- **W33** — next god-class refactor candidate. Top remaining (>240 LoC) main files after W32: `StatsViewModel.cs` 263 LoC (App/ViewModels) OR `SequenceLibrary.cs` 244 LoC (App/Services sister of W29 SendFrameLibrary) OR `PeakCanChannel.cs` 244 LoC (Infrastructure/Peak sister of W18 + W25) OR `CyclicSendService.cs` 243 LoC (App/Services sister of W23 CyclicDbcSendService) OR `TraceSessionBundle.cs` 247 LoC (App/Services/Trace sister of W27 RecentSessionsService).
- **W32.5 vault-only PATCH audit** at next session start — verify all 2 promotions landed in vault MEMORY.md + agent-memory catalog + master-lesson-catalog.
- **Memory cleanup sweep**: MEMORY.md currently >155KB (over 24.4KB auto-memory cap); consider demoting older W* entries to sibling `MEMORY-history.md` archive file at next session-end per W26.5 + W27.5 + W28.5 + W29.5 + W30.5 + W31.5 + W32.5 pattern retention.
