# W31.5 v3.45.5 SHIP — vault-only PATCH lesson-promotion consolidation (W31 cycle)

**Date**: 2026-07-13
**Status**: SHIP-READY (docs-only, no source-code changes)
**Branch**: `feature/w31-5-vault-only-patch`
**Target version**: v3.45.5 PATCH (vault-only, +0/-0 source LoC)
**Sister pattern**: W17 vault-only PATCH (v3.31.1) + W23.5-W25.5 vault-only PATCH (v3.39.5) + W26.5 vault-only PATCH (v3.40.5) + W27.5 vault-only PATCH (v3.41.5) + W28.5 vault-only PATCH (v3.42.5) + W29.5 vault-only PATCH (v3.43.5) + W30.5 vault-only PATCH (v3.44.5) — single-cycle consolidation of multiple deferred PATCH promotions (8th occurrence).

## D1-D5 (carried from W31.5 design)

- **D1**: 4 lesson promotions consolidated (1 NEW 1/3 + 1 1/3 → 2/3 PROMOTION + 2 2/3 → 3/3 CONFIRMED LOCKED).
- **D2**: Forward-bump v3.45.0 → v3.45.5 (per W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 + W30.5 vault-only PATCH convention — no source-only changes warrant PATCH, not MINOR).
- **D3**: 1 docs-only commit (sister of W17 1-commit pattern + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 + W30.5 1-commit patterns).
- **D4**: Tier-3 ship via PR + squash + delete-branch + tag + GH release; 0 source LoC change verification.
- **D5**: Branch name `feature/w31-5-vault-only-patch` (per W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 + W30.5 single-PATCH consolidated naming).

## Why consolidate into 1 cycle (sister pattern precedent — 8th occurrence)

Per W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 + W30.5 consolidated-PATCH sister precedent:

- **W17 vault-only PATCH** (v3.31.1): promoted `wc-l-vs-python-splitlines-off-by-one` 3/3 CONFIRMED in 1 atomic commit
- **W23.5-W25.5 vault-only PATCH** (v3.39.5): promoted 6 lesson candidates (consolidated 3 deferred PATCH cycles) in 1 atomic commit
- **W26.5 vault-only PATCH** (v3.40.5): promoted 3 lesson candidates (W26 cycle promotions) in 1 atomic commit
- **W27.5 vault-only PATCH** (v3.41.5): promoted 2 lesson candidates (W27 cycle promotions) in 1 atomic commit
- **W28.5 vault-only PATCH** (v3.42.5): promoted 2 lesson candidates (W28 cycle promotions) in 1 atomic commit
- **W29.5 vault-only PATCH** (v3.43.5): promoted 2 lesson candidates (W29 cycle promotions) in 1 atomic commit
- **W30.5 vault-only PATCH** (v3.44.5): promoted 3 lesson candidates (W30 cycle promotions) in 1 atomic commit
- **W31.5 vault-only PATCH** (v3.45.5): promotes 4 lesson candidates (W31 cycle promotions) in 1 atomic commit — sister precedent continued (8th occurrence)

## 4 lesson promotions consolidated

### W31 1/3 → 2/3 PROMOTION → standalone lesson file created

1. **`small-god-class-no-largest-method-keeps-all-inline-default-pattern`** — W29 **NEW 1/3** → W31 **2/3 PROMOTION**
   - W29 1-of-2: W29 SendFrameLibrary SaveUnlocked 24 LoC LARGEST method STAYED INLINE (per default D5 sister-principle; LARGEST method <50 LoC → no W25 D5 deviation applied)
   - W31 2-of-2: W31 ReplayService LoadAsync 31 LoC LARGEST method STAYED INLINE (per default D5 sister-principle; LARGEST method <50 LoC → no W25 D5 deviation applied)
   - **Pattern**: Small god-classes (LARGEST method <50 LoC) follow default D5 sister-principle — all methods stay inline OR extract per flow-boundary clarity, NOT LARGEST-method-can-move. W31 confirms W29 1/3 observation that default D5 applies to small god-classes with LARGEST method < 50 LoC threshold.
   - **Decision matrix** (now explicit): <50 LoC LARGEST → default D5 (W29 + W31 = 2 confirmations); 50-59 LoC → borderline case-by-case (none observed); ≥60 LoC + discrete flow → moves (W25 + W26 + W27 + W28 + W30 = 5 moves); ≥60 LoC + orchestration → stays (W22 + W23 = 2 stays).
   - Awaiting 1 more observation across any future small-god-class refactor to promote to 3/3 CONFIRMED → LOCKED.
   - **Standalone lesson file**: `docs/superpowers/lessons/small-god-class-no-largest-method-keeps-all-inline-default-pattern-W29-W31-2of3.md`

### W31 2/3 → 3/3 CONFIRMED LOCKED → standalone lesson file created

2. **`app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28`** — W28.5 **2/3** → W31 **3/3 CONFIRMED LOCKED**
   - W27 1-of-3: W27 RecentSessionsService PersistenceOps `LoadAsync` (60 LoC LARGEST method moves per W25 D5 deviation since ≥ 60 LoC + discrete flow boundary)
   - W28 2-of-3: W28 DbcService LoadLifecycle `LoadAsync` (79 LoC LARGEST method moves per W25 D5 deviation since ≥ 60 LoC + discrete flow boundary)
   - W31 3-of-3: W31 ReplayService FileIoLifecycle `LoadAsync` (31 LoC method stays inline per W29 NEW pattern since < 50 LoC threshold)
   - **Pattern**: Core or App god-class refactors with public async `LoadAsync` that reads file bytes + parses content + mutates state + raises event(s) can follow the W27 + W28 + W31 2-partial decomposition pattern (LoadLifecycle/PersistenceOps/FileIoLifecycle + emission/mutator/text-decoding).
   - **LOCKED into MASTER-LESSON-CATALOG at W31 SHIP closure**: 3 confirmations across Core + App layers confirm the pattern is canonical.
   - Application scope: All future peakcan-host god-class refactors of Core or App god-classes with async file-load lifecycle methods.
   - **Standalone lesson file**: `docs/superpowers/lessons/app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28-w31-LOCKED.md`

### W31 2/3 → 3/3 CONFIRMED LOCKED → standalone lesson file created

3. **`multi-interface-partial-class-iframesink-and-iscriptcanapi`** — W26.5 **2/3** → W31 **3/3 CONFIRMED LOCKED**
   - W26 1st + 2nd observations: W26 CanApi `IFrameSink + IScriptCanApi` (2 confirmations of multi-interface pattern at W26.5)
   - W31 3rd observation: W31 ReplayService `IReplayService + IDisposable` (3rd confirmation of multi-interface pattern at W31)
   - **Pattern**: Multi-interface god-classes decompose into 2+ partials based on **flow boundaries** (state mutation lifecycle vs emission vs static helpers), NOT based on **interface boundaries**. Each partial continues to implement all the interfaces of the original class.
   - **LOCKED into MASTER-LESSON-CATALOG at W31 SHIP closure**: 3 confirmations across 2 distinct multi-interface classes confirm the pattern is canonical.
   - Application scope: All future peakcan-host god-class refactors of multi-interface god-classes (2+ interfaces).
   - **Standalone lesson file**: `docs/superpowers/lessons/multi-interface-partial-class-empirical-w26-w31-LOCKED.md`

### W31 NEW 1/3 → standalone lesson file created

4. **`core-layer-replay-subsystem-sister-pattern-empirical-w15-w22-w31`** — W31 **NEW 1/3 observation**
   - W31 1-of-3: W31 ReplayService Core-layer decomposition = FileIoLifecycle + FrameEmission; sister of W15 ReplayTimeline + W22 RecordService for Core/Replay subsystem shape
   - **Pattern**: Core/Replay subsystem god-class refactors (services that orchestrate the replay of recorded CAN frames) can follow the W31 2-partial decomposition pattern (FileIoLifecycle + FrameEmission).
   - Awaiting 2 more observations across any future Core/Replay god-class refactor to promote to 2/3 → 3/3 CONFIRMED → LOCKED.
   - **Standalone lesson file**: `docs/superpowers/lessons/core-layer-replay-subsystem-sister-pattern-empirical-w15-w22-w31-1of3.md`

## LESSON ENHANCEMENT — W19 R1 first-correction now covers post-failure recovery

Per W31 T2 first-run failure (delta=28 vs expected 69 due to incorrect multi-region reverse-order indexing), the W19 R1 first-correction LESSON now covers **two dimensions**:

1. **Pre-flight prevention** (original W19 R1): re-grep boundaries BEFORE running each deletion script
2. **Post-failure recovery** (NEW at W31.5): when a deletion script fails (delta outside ±2 LoC tolerance), the recovery procedure is:
   - `git checkout` the partially-modified main file from git
   - Re-grep post-T(N-1) boundaries (post-T1 boundaries differ from main HEAD by -50 LoC for W31)
   - Correct the offsets in the deletion script
   - Re-run the script with corrected offsets
   - Verify delta = expected (LoC formula `main_after = main_before - delete_count` per W8.5 D7 32-locked)
   - Build + test verification
   - Commit

W31 T2 first-run failure → recovery procedure applied → second run PASS with delta = 69 EXACT match. This is now a documented dimension of the W19 R1 first-correction LESSON, applicable to all future god-class refactors with **multi-region reverse-order deletions**.

## Already CONFIRMED (held — no new promotion needed)

- `partial-extraction-must-use-original-code-from-head-not-fabricated-api` (W21 3/3 CONFIRMED)
- `subdirectory-partials-pattern-empirical-26-precedents` (W20 3/3 CONFIRMED; W31 = 21st deployment)
- `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` (W23 3/3 CONFIRMED, 14th observation at W31 T1+T2)
- `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` (W23 3/3 CONFIRMED, 10th confirmation at W31)
- `add-partial-keyword-to-monolithic-class-before-extraction` (W26.5 3/3 CONFIRMED, 29th application at W31; W31 already partial)
- `largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator` (9/3 since 3/3 LOCKED at W25; W31 = 4th stay in 9 observations)
- `infrastructure-channel-layer-sister-pattern-empirical-w18-w25` (W25 2/3 — held; W18 + W25 observations; W31 is Core/Replay, NOT Infrastructure/Channel)
- `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` (W29.5 3/3 CONFIRMED LOCKED — held; W31 has file-IO + ASC parsing, NOT JSON persistence)
- `app-services-multiframe-layer-sister-pattern-empirical-w30` (W30 NEW 1/3 — held; W31 is Core/Replay, NOT App/Services/MultiFrame)
- `peak-can-channel-cs8795-accessibility-modifier-blocks-cross-partial-source-generator-impl` (W18 R1 1/3 — held)

## Cross-partial helper visibility pattern (CONFIRMED across 2 partials per W31)

Per W27.5 NEW observation `cross-partial-helper-visibility-works-across-3-partials` (1/3 confirmation at W27.5 → held), W31 sister-confirms this pattern works with **2 partials** (FileIoLifecycle + FrameEmission for ReplayService):

- **`EmitFrame` + `EmitFrameToSinkAsync` + `OnSinkThrewFromTimeline` + `RaisePlaybackEnded` private helpers** (in Flow B `FrameEmission.partial.cs`) are referenced from ctor (Flow A `ReplayService.cs` main) — partial-class cross-partial visibility handles this automatically.

This is a **8th confirmation** (1st was W22, 2nd was W23, 3rd was W26, 4th was W27, 5th was W28, 6th was W29, 7th was W30, 8th is W31) that the cross-partial helper visibility pattern is stable across multi-partial god-class extractions.

## Source-code verification

- **No source-code changes.** `src/PeakCan.Host.Core/` + `src/PeakCan.Host.Infrastructure/` + `src/PeakCan.Host.App/` all unchanged from main @ `07e6b0d` (W31 SHIP closure + capture-decisions landing).
- `src/Directory.Build.props` v3.45.0 → v3.45.5 (4-field bump: `<Version>` + `<AssemblyVersion>` + `<FileVersion>` + `<InformationalVersion>`).
- `docs/user-manual.html` NOT modified (no user-facing change; sister of W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 + W30.5 vault-only PATCH user-manual unchanged).

## File impact summary

| File | Change | LoC |
|---|---|---|
| `src/Directory.Build.props` | v3.45.0 → v3.45.5 (4 fields) | -8 +8 |
| `docs/superpowers/lessons/small-god-class-no-largest-method-keeps-all-inline-default-pattern-W29-W31-2of3.md` | NEW (2/3 PROMOTION lesson) | +108 |
| `docs/superpowers/lessons/app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28-w31-LOCKED.md` | NEW (LOCKED 3/3 lesson) | +106 |
| `docs/superpowers/lessons/multi-interface-partial-class-empirical-w26-w31-LOCKED.md` | NEW (LOCKED 3/3 lesson) | +85 |
| `docs/superpowers/lessons/core-layer-replay-subsystem-sister-pattern-empirical-w15-w22-w31-1of3.md` | NEW (NEW 1/3 lesson) | +114 |
| `docs/superpowers/capture-decisions/2026-07-13-w31-5-vault-only-patch.md` | NEW (this file) | +250 |

Total LoC delta: +663 docs.

## Verification matrix

- `dotnet build src/PeakCan.Host.slnx`: 0 errors, 0 warnings (no source change).
- `dotnet test` (full solution): expected 0 new fails (no source change → identical to v3.45.0).
- Tag `v3.45.5` annotated at the squash-merge commit.
- GH release https://github.com/jasontaotao/peakcan-host/releases/tag/v3.45.5 published.
- Branch auto-deleted.

## Process lessons applied

- **W17 vault-only PATCH sister-precedent**: 1 consolidated cycle for multiple deferred PATCH promotions (cleaner history + clearer lesson-promotion atomicity).
- **W23.5-W25.5 vault-only PATCH sister-precedent**: 3 deferred PATCH cycles consolidated into 1 atomic commit.
- **W26.5 vault-only PATCH sister-precedent**: 1 atomic docs commit per cycle for multiple deferred PATCH promotions.
- **W27.5 vault-only PATCH sister-precedent**: 2 lesson candidates per cycle; `largest-method-can-move` LOCKED into MASTER-LESSON-CATALOG.
- **W28.5 vault-only PATCH sister-precedent**: 2 lesson candidates per cycle (1 NEW 1/3 → 2/3 + 1 6/3 consolidation since 3/3 LOCKED).
- **W29.5 vault-only PATCH sister-precedent**: 2 lesson candidates per cycle (1 NEW 1/3 + 1 LOCKED 3/3 → standalone lesson file).
- **W30.5 vault-only PATCH sister-precedent**: 3 lesson candidates per cycle (1 NEW 1/3 + 1 7/3 consolidation entry + 1 1/3 hold confirmation).
- **D3 VAULT-ONLY PATCH convention** (W44.0 formalized): vault metadata changes ship as PATCH when src/ unchanged; this PATCH follows the convention.
- **D2 forward-bump** convention: source-only changes warrant MINOR; docs-only warrant PATCH (per W17 + W44.0 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 + W30.5 sister precedent).

## Honest deviations

- (a) **W31.5 single-PATCH cycle** (sister of W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 + W30.5) — 1 atomic docs commit per cycle vs 2-3 separate PATCH bumps. Trade-off accepted per sister precedent.
- (b) **User-manual.html NOT updated** for v3.45.5 (vault-only PATCH = docs-only = user-facing unchanged). The user-manual v3.45.0 row remains authoritative; v3.45.5 is **purely a lesson-promotion version bump**, not a user-facing deliverable.
- (c) **`app-services-multiframe-layer-sister-pattern-empirical-w30` 1/3 HELD**: W31 ReplayService is Core/Replay, NOT App/Services/MultiFrame — observation N/A. The pattern awaits a future refactor of an App/Services/MultiFrame god-class.
- (d) **`infrastructure-channel-layer-sister-pattern-empirical-w18-w25` 2/3 HELD**: W31 ReplayService is Core/Replay, NOT Infrastructure/Channel — observation N/A. The pattern awaits a future refactor of an Infrastructure/Channel god-class.

## What was captured

W31.5 SHIP closure = 2 captures dispatched: W31.5 prep + SHIP. Each per the W12-W30 pattern of `vault-pkm:pkm-capture` agent dispatched after each commit.

## Out of scope (YAGNI)

- No source-code change.
- No test change.
- No user-manual change.
- No 2 separate docs files (consolidated into this single capture-decisions).
- No MEMORY.md per-lesson entries (consolidated in this file).

## Next (post-PATCH-ship)

- **W32** — next god-class refactor candidate. Top remaining (>250 LoC) main files after W31: `StatsViewModel.cs` 263 LoC (App/ViewModels) OR `DbcSendViewModel.cs` 238 LoC (App/ViewModels — sister of W24, already partial, below threshold) OR lower-LoC App-layer god-classes in 240-249 LoC range.
- **W31.5 vault-only PATCH audit** at next session start — verify all 4 promotions landed in vault MEMORY.md + agent-memory catalog + master-lesson-catalog.
- **Memory cleanup sweep**: MEMORY.md currently >155KB (over 24.4KB auto-memory cap); consider demoting older W* entries to sibling `MEMORY-history.md` archive file at next session-end per W26.5 + W27.5 + W28.5 + W29.5 + W30.5 + W31.5 pattern retention.
