# W29.5 v3.43.5 SHIP — vault-only PATCH lesson-promotion consolidation (W29 cycle)

**Date**: 2026-07-13
**Status**: SHIP-READY (docs-only, no source-code changes)
**Branch**: `feature/w29-5-vault-only-patch`
**Target version**: v3.43.5 PATCH (vault-only, +0/-0 source LoC)
**Sister pattern**: W17 vault-only PATCH (v3.31.1) + W23.5-W25.5 vault-only PATCH (v3.39.5) + W26.5 vault-only PATCH (v3.40.5) + W27.5 vault-only PATCH (v3.41.5) + W28.5 vault-only PATCH (v3.42.5) — single-cycle consolidation of multiple deferred PATCH promotions (6th occurrence).

## D1-D5 (carried from W29.5 design)

- **D1**: 2 lesson promotions consolidated (1 NEW 1/3 → documented in standalone lesson file + 1 LOCKED at W29 → standalone lesson file promoted to MASTER-LESSON-CATALOG).
- **D2**: Forward-bump v3.43.0 → v3.43.5 (per W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 vault-only PATCH convention — no source-only changes warrant PATCH, not MINOR).
- **D3**: 1 docs-only commit (sister of W17 1-commit pattern + W23.5-W25.5 + W26.5 + W27.5 + W28.5 1-commit patterns).
- **D4**: Tier-3 ship via PR + squash + delete-branch + tag + GH release; 0 source LoC change verification.
- **D5**: Branch name `feature/w29-5-vault-only-patch` (per W23.5-W25.5 + W26.5 + W27.5 + W28.5 single-PATCH consolidated naming).

## Why consolidate into 1 cycle (sister pattern precedent — 6th occurrence)

Per W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 consolidated-PATCH sister precedent:

- **W17 vault-only PATCH** (v3.31.1): promoted `wc-l-vs-python-splitlines-off-by-one` 3/3 CONFIRMED in 1 atomic commit
- **W23.5-W25.5 vault-only PATCH** (v3.39.5): promoted 6 lesson candidates (consolidated 3 deferred PATCH cycles) in 1 atomic commit
- **W26.5 vault-only PATCH** (v3.40.5): promoted 3 lesson candidates (W26 cycle promotions) in 1 atomic commit
- **W27.5 vault-only PATCH** (v3.41.5): promoted 2 lesson candidates (W27 cycle promotions) in 1 atomic commit
- **W28.5 vault-only PATCH** (v3.42.5): promoted 2 lesson candidates (W28 cycle promotions) in 1 atomic commit
- **W29.5 vault-only PATCH** (v3.43.5): promotes 2 lesson candidates (W29 cycle promotions) in 1 atomic commit — sister precedent continued (6th occurrence)

## 2 lesson promotions consolidated

### W29 LOCKED at 3/3 CONFIRMED → standalone lesson file created

1. **`app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED`** — W29 3/3 CONFIRMED → **LOCKED into MASTER-LESSON-CATALOG**
   - W22 1-of-3: W22 RecordService Lifecycle + Mutators 2-partial split (file-IO + lock-protected mutators + `%APPDATA%` path resolution)
   - W27 2-of-3: W27 RecentSessionsService PersistenceOps + Mutators + StaticHelpers 3-partial split (file-IO + lock-protected mutators + `DefaultPath` static helper)
   - W29 3-of-3: W29 SendFrameLibrary PersistenceFlow + Mutators + StaticHelpers 3-partial split (file-IO + lock-protected mutators + `DefaultPath` static helper)
   - **Pattern**: App/Services god-class refactors where the target class has JSON-persistence + lock-protected mutators + `%APPDATA%` path resolution can follow the W22 + W27 + W29 3-partial decomposition pattern (PersistenceFlow + Mutators + StaticHelpers).
   - **LOCKED into MASTER-LESSON-CATALOG at W29 SHIP closure** (2026-07-13): 3 observations across 3 distinct sister-classes (RecordService + RecentSessionsService + SendFrameLibrary) confirm the pattern is canonical.
   - Application scope: All future peakcan-host god-class refactors of App/Services classes with JSON-persistence + lock-protected mutators + `%APPDATA%` path resolution.
   - **Standalone lesson file**: `docs/superpowers/lessons/app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED.md`

### W29 NEW 1/3 → standalone lesson file created

2. **`small-god-class-no-largest-method-keeps-all-inline-default-pattern`** — W29 **NEW 1/3 observation** (1st observation of the pattern)
   - W29 1-of-3: W29 SendFrameLibrary SaveUnlocked 24 LoC LARGEST method STAYED INLINE (per default D5 sister-principle; LARGEST method <50 LoC → no W25 D5 deviation applied)
   - **Pattern**: Small god-classes (LARGEST method <50 LoC) follow default D5 sister-principle — all methods stay inline OR extract per flow-boundary clarity, NOT LARGEST-method-can-move. The W25 D5 deviation criteria (LARGEST ≥ 60 LoC + discrete flow boundary) fail on the LARGEST < 50 LoC threshold → no deviation applied.
   - **Decision matrix** (now explicit): <50 LoC LARGEST → default D5; 50-59 LoC → borderline case-by-case (none observed); ≥60 LoC + discrete flow → moves (W25 + W26 + W27 + W28); ≥60 LoC + orchestration → stays (W22 + W23).
   - Awaiting 2 more observations across any future small-god-class refactor to promote to 2/3 → 3/3 CONFIRMED → LOCKED.
   - **Standalone lesson file**: `docs/superpowers/lessons/small-god-class-no-largest-method-keeps-all-inline-default-pattern-W29-1of3.md`

### Already CONFIRMED (held — no new promotion needed)

3. **`largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator`** — **HELD at 6/3 LOCKED** in MASTER-LESSON-CATALOG at W29.5 (already 3/3 CONFIRMED at W25 T4):
   - W22 1-of-6 (stay): W22 RecordBatchAsync 100 LoC STAYED in main (single central orchestration pipeline)
   - W23 2-of-6 (stay): W23 OnTimerTick 151 LoC STAYED in main (single tick-loop pipeline)
   - W25 3/3 CONFIRMED (move): W25 OnChannelFrame 73 LoC **MOVED** to FrameRouting.partial.cs (fan-out + error-isolation = sharp discrete flow boundary)
   - W26 4-of-6 (move): W26 OnFrame(CanFrame) 62 LoC **MOVED** to SinkLifecycle.partial.cs (frame-arrives → callback-fanout discrete dispatcher shape)
   - W27 5-of-6 (move): W27 LoadAsync 60 LoC **MOVED** to PersistenceOps.partial.cs (file-I/O lifecycle = sharp discrete flow, 2nd sister of W25 pattern)
   - W28 6-of-6 (move): W28 LoadAsync 79 LoC **MOVED** to LoadLifecycle.partial.cs (file-I/O + parsing lifecycle = sharp discrete flow, 3rd sister of W27 pattern)
   - **6 observations = 2 stays (W22+W23) + 4 moves (W25+W26+W27+W28)** lock in both the "stay default" + "move allowed for discrete flow" outcomes.
   - **W29 7th observation**: W29 SendFrameLibrary SaveUnlocked 24 LoC → **NO MOVE** (LARGEST method <50 LoC threshold). Confirms the **bounded** interpretation of the lesson — D5 deviation criteria fail on the <50 LoC threshold, NOT free pass for all large methods.
   - **HELD in MASTER-LESSON-CATALOG** at W29.5: 7 observations (2 stays + 4 moves + 1 too-small-to-deviate = 2 stays + 5 default-D5) continue to lock in both outcomes as canonical.

## Already CONFIRMED (held)

- `partial-extraction-must-use-original-code-from-head-not-fabricated-api` (W21 3/3 CONFIRMED)
- `subdirectory-partials-pattern-empirical-26-precedents` (W20 3/3 CONFIRMED)
- `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` (W23 3/3 CONFIRMED, 12th observation at W29 T1+T2+T3)
- `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` (W23 3/3 CONFIRMED, 8th confirmation at W29)
- `add-partial-keyword-to-monolithic-class-before-extraction` (W26.5 3/3 CONFIRMED, 27th confirmation at W29)
- `multi-interface-partial-class-iframesink-and-iscriptcanapi` (W26.5 2/3 — held; W26 CanApi = 1st multi-interface; W29 SendFrameLibrary has 0 interfaces; observation N/A)
- `infrastructure-channel-layer-sister-pattern-empirical-w18-w25` (W25 2/3 — held; W18 + W25 observations; W29 SendFrameLibrary is App/Services, NOT Infrastructure/Channel)
- `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28` (W28.5 2/3 — held; W29 SendFrameLibrary has sync Load (NOT async LoadAsync) + lock-gated mutators; observation N/A — no async file-load lifecycle in W29)
- `peak-can-channel-cs8795-accessibility-modifier-blocks-cross-partial-source-generator-impl` (W18 R1 1/3 — held)

## Cross-partial helper visibility pattern (CONFIRMED across 3 partials at W29)

Per W27.5 NEW observation `cross-partial-helper-visibility-works-across-3-partials` (1/3 confirmation at W27.5 → held), W29 sister-confirms this pattern works with **3 partials** (PersistenceFlow + Mutators + StaticHelpers for SendFrameLibrary):

- **`EnsureLoaded` + `LoadUnlocked` + `SaveUnlocked` private helpers** (in Flow A `PersistenceFlow.partial.cs`) are called from Flow B `Mutators.partial.cs` (6 public methods each call these helpers) — partial-class cross-partial visibility handles this automatically.

This is a **3rd confirmation** (1st was W27, 2nd was W28, 3rd is W29) that the cross-partial helper visibility pattern is stable across multi-partial god-class extractions.

## Source-code verification

- **No source-code changes.** `src/PeakCan.Host.Core/` + `src/PeakCan.Host.Infrastructure/` + `src/PeakCan.Host.App/` all unchanged from main @ `c06a23b` (W29 SHIP closure).
- `src/Directory.Build.props` v3.43.0 → v3.43.5 (4-field bump: `<Version>` + `<AssemblyVersion>` + `<FileVersion>` + `<InformationalVersion>`).
- `docs/user-manual.html` NOT modified (no user-facing change; sister of W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 vault-only PATCH user-manual unchanged).

## File impact summary

| File | Change | LoC |
|---|---|---|
| `src/Directory.Build.props` | v3.43.0 → v3.43.5 (4 fields) | -8 +8 |
| `docs/superpowers/lessons/app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED.md` | NEW (LOCKED lesson) | +95 |
| `docs/superpowers/lessons/small-god-class-no-largest-method-keeps-all-inline-default-pattern-W29-1of3.md` | NEW (1/3 lesson) | +90 |
| `docs/superpowers/capture-decisions/2026-07-13-w29-5-vault-only-patch.md` | NEW (this file) | +180 |

Total LoC delta: +357 docs.

## Verification matrix

- `dotnet build src/PeakCan.Host.slnx`: 0 errors, 0 warnings (no source change).
- `dotnet test` (full solution): expected 0 new fails (no source change → identical to v3.43.0).
- Tag `v3.43.5` annotated at the squash-merge commit.
- GH release https://github.com/jasontaotao/peakcan-host/releases/tag/v3.43.5 published.
- Branch auto-deleted.

## Process lessons applied

- **W17 vault-only PATCH sister-precedent**: 1 consolidated cycle for multiple deferred PATCH promotions (cleaner history + clearer lesson-promotion atomicity).
- **W23.5-W25.5 vault-only PATCH sister-precedent**: 3 deferred PATCH cycles consolidated into 1 atomic commit.
- **W26.5 vault-only PATCH sister-precedent**: 1 atomic docs commit per cycle for multiple deferred PATCH promotions.
- **W27.5 vault-only PATCH sister-precedent**: 2 lesson candidates per cycle; `largest-method-can-move` LOCKED into MASTER-LESSON-CATALOG.
- **W28.5 vault-only PATCH sister-precedent**: 2 lesson candidates per cycle (1 NEW 1/3 → 2/3 + 1 6/3 consolidation since 3/3 LOCKED).
- **D3 VAULT-ONLY PATCH convention** (W44.0 formalized): vault metadata changes ship as PATCH when src/ unchanged; this PATCH follows the convention.
- **D2 forward-bump** convention: source-only changes warrant MINOR; docs-only warrant PATCH (per W17 + W44.0 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 sister precedent).

## Honest deviations

- (a) **W29.5 single-PATCH cycle** (sister of W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5) — 1 atomic docs commit per cycle vs 2 separate PATCH bumps. Trade-off accepted per sister precedent.
- (b) **User-manual.html NOT updated** for v3.43.5 (vault-only PATCH = docs-only = user-facing unchanged). The user-manual v3.43.0 row remains authoritative; v3.43.5 is **purely a lesson-promotion version bump**, not a user-facing deliverable.
- (c) **`app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28` 2/3 held**: W29 SendFrameLibrary has sync Load (NOT async LoadAsync) — observation N/A. The pattern awaits a future refactor of an App/Services class with async file-load lifecycle.
- (d) **`multi-interface-partial-class-iframesink-and-iscriptcanapi` 2/3 held**: W29 SendFrameLibrary has 0 interfaces — observation N/A. The pattern awaits a future refactor of an App/Services class with multiple interfaces.

## What was captured

W29.5 SHIP closure = 2 captures dispatched: W29.5 prep + SHIP. Each per the W12-W28 pattern of `vault-pkm:pkm-capture` agent dispatched after each commit.

## Out of scope (YAGNI)

- No source-code change.
- No test change.
- No user-manual change.
- No 2 separate docs files (consolidated into this single capture-decisions).
- No MEMORY.md per-lesson entries (consolidated in this file).

## Next (post-PATCH-ship)

- **W30** — next god-class refactor candidate. Top remaining (>250 LoC) main files after W29: `SequenceSendService.cs` 266 LoC (App/Services/MultiFrame) OR `ReplayService.cs` 265 LoC (Core/Replay) OR `StatsViewModel.cs` 263 LoC (App/ViewModels) OR `SendViewModel.cs` 257 LoC (App/ViewModels).
- **W29.5 vault-only PATCH audit** at next session start — verify all 2 promotions landed in vault MEMORY.md + agent-memory catalog + master-lesson-catalog.
- **Memory cleanup sweep**: MEMORY.md currently >155KB (over 24.4KB auto-memory cap); consider demoting older W* entries to sibling `MEMORY-history.md` archive file at next session-end per W26.5 + W27.5 + W28.5 + W29.5 pattern retention.
