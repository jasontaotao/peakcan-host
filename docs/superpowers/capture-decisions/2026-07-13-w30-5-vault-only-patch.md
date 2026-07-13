# W30.5 v3.44.5 SHIP — vault-only PATCH lesson-promotion consolidation (W30 cycle)

**Date**: 2026-07-13
**Status**: SHIP-READY (docs-only, no source-code changes)
**Branch**: `feature/w30-5-vault-only-patch`
**Target version**: v3.44.5 PATCH (vault-only, +0/-0 source LoC)
**Sister pattern**: W17 vault-only PATCH (v3.31.1) + W23.5-W25.5 vault-only PATCH (v3.39.5) + W26.5 vault-only PATCH (v3.40.5) + W27.5 vault-only PATCH (v3.41.5) + W28.5 vault-only PATCH (v3.42.5) + W29.5 vault-only PATCH (v3.43.5) — single-cycle consolidation of multiple deferred PATCH promotions (7th occurrence).

## D1-D5 (carried from W30.5 design)

- **D1**: 3 lesson promotions consolidated (1 NEW 1/3 documented in standalone lesson file + 1 7/3 consolidation entry in LOCKED lesson file + 1 1/3 hold confirmation).
- **D2**: Forward-bump v3.44.0 → v3.44.5 (per W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 vault-only PATCH convention — no source-only changes warrant PATCH, not MINOR).
- **D3**: 1 docs-only commit (sister of W17 1-commit pattern + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 1-commit patterns).
- **D4**: Tier-3 ship via PR + squash + delete-branch + tag + GH release; 0 source LoC change verification.
- **D5**: Branch name `feature/w30-5-vault-only-patch` (per W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 single-PATCH consolidated naming).

## Why consolidate into 1 cycle (sister pattern precedent — 7th occurrence)

Per W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 consolidated-PATCH sister precedent:

- **W17 vault-only PATCH** (v3.31.1): promoted `wc-l-vs-python-splitlines-off-by-one` 3/3 CONFIRMED in 1 atomic commit
- **W23.5-W25.5 vault-only PATCH** (v3.39.5): promoted 6 lesson candidates (consolidated 3 deferred PATCH cycles) in 1 atomic commit
- **W26.5 vault-only PATCH** (v3.40.5): promoted 3 lesson candidates (W26 cycle promotions) in 1 atomic commit
- **W27.5 vault-only PATCH** (v3.41.5): promoted 2 lesson candidates (W27 cycle promotions) in 1 atomic commit
- **W28.5 vault-only PATCH** (v3.42.5): promoted 2 lesson candidates (W28 cycle promotions) in 1 atomic commit
- **W29.5 vault-only PATCH** (v3.43.5): promoted 2 lesson candidates (W29 cycle promotions) in 1 atomic commit
- **W30.5 vault-only PATCH** (v3.44.5): promotes 3 lesson candidates (W30 cycle promotions) in 1 atomic commit — sister precedent continued (7th occurrence)

## 3 lesson promotions consolidated

### W30 NEW 1/3 → standalone lesson file created

1. **`app-services-multiframe-layer-sister-pattern-empirical-w30`** — W30 **NEW 1/3 observation** (1st observation of the pattern)
   - W30 1-of-3: W30 SequenceSendService 2-partial split (SendFlow + RowBuildFlow) — orchestration dispatch + row-encoding helpers decomposition
   - **Pattern**: App/Services/MultiFrame god-class refactors with orchestration dispatch (concurrent vs sequential mode) + row-encoding helpers can follow the W30 2-partial decomposition pattern (SendFlow + RowBuildFlow).
   - **Decision matrix** (now explicit): orchestration dispatch + row-encoding → 2-partial split (W30 pattern); JSON-persistence + lock-protected mutators + `%APPDATA%` path → 3-partial split (W22/W27/W29 pattern); TOML/DBC parser persistence → 2-partial split (W28 pattern); async file-load lifecycle → 2-partial split (W27 LoadAsync pattern).
   - Awaiting 2 more observations across any future App/Services/MultiFrame god-class refactor to promote to 2/3 → 3/3 CONFIRMED → LOCKED.
   - **Standalone lesson file**: `docs/superpowers/lessons/app-services-multiframe-layer-sister-pattern-empirical-w30-1of3.md`

### W30 7/3 since 3/3 LOCKED → consolidation entry added

2. **`largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator`** — W30 7/3 (already **3/3 CONFIRMED** at W25 T4) → **held in MASTER-LESSON-CATALOG at W30.5**
   - W22 1-of-7 (stay): W22 RecordBatchAsync 100 LoC STAYED in main (single central orchestration pipeline)
   - W23 2-of-7 (stay): W23 OnTimerTick 151 LoC STAYED in main (single tick-loop pipeline)
   - W25 3/3 CONFIRMED (move): W25 OnChannelFrame 73 LoC **MOVED** to FrameRouting.partial.cs (fan-out + error-isolation = sharp discrete flow boundary)
   - W26 4-of-7 (move): W26 OnFrame(CanFrame) 62 LoC **MOVED** to SinkLifecycle.partial.cs (frame-arrives → callback-fanout discrete dispatcher shape, sister of W25 OnChannelFrame)
   - W27 5-of-7 (move): W27 LoadAsync 60 LoC **MOVED** to PersistenceOps.partial.cs (file-I/O lifecycle = sharp discrete flow, 2nd sister of W25 pattern)
   - W28 6-of-7 (move): W28 LoadAsync 79 LoC **MOVED** to LoadLifecycle.partial.cs (file-I/O + parsing lifecycle = sharp discrete flow, 3rd sister of W27 pattern)
   - **W30 7-of-7 (move)**: W30 SendAsync 91 LoC **MOVED** to SendFlow.partial.cs (concurrent vs sequential dispatcher = sharp discrete flow, 5th move confirming W25 + W26 + W27 + W28 + W30 pattern)
   - **7 observations = 2 stays (W22 + W23) + 5 moves (W25 + W26 + W27 + W28 + W30)** lock in both the "stay default" + "move allowed for discrete flow" outcomes.
   - **HELD in MASTER-LESSON-CATALOG** at W30.5: 7 observations (2 stays + 5 moves) continue to lock in BOTH outcomes as canonical; W30.5 holds W27.5 LOCKED + W28.5 held + W30 5th move all consolidated into 1 PATCH cycle.
   - **Consolidation entry lesson file**: `docs/superpowers/lessons/largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator-LOCKED.md` (updated to include W30 7th observation as 5th move)

### W30 1/3 hold confirmation → noted in NEW 1/3 lesson file

3. **`small-god-class-no-largest-method-keeps-all-inline-default-pattern`** — W29 **NEW 1/3 observation** → W30 **HELD** at 1/3 (no new observation in W30)
   - W29 1-of-3: W29 SendFrameLibrary SaveUnlocked 24 LoC LARGEST method STAYED INLINE (per default D5 sister-principle; LARGEST method <50 LoC → no W25 D5 deviation applied)
   - W30 confirmation: W30 SequenceSendService LARGEST method 91 LoC ≥ 60 LoC → **W25 D5 deviation APPLIED correctly** (NOT default D5). This confirms W29 1/3 observation that default D5 applies to small god-classes ONLY.
   - **HELD at 1/3** at W30.5: W30 did not add a new observation of small god-class (W30 has 91 LoC LARGEST → W25 D5 deviation applies, NOT default D5). Awaiting 1 more observation across any future small-god-class refactor to promote to 2/3.
   - **Lesson catalog**: referenced in NEW 1/3 file `app-services-multiframe-layer-sister-pattern-empirical-w30-1of3.md` decision matrix as a sister-precedent.

## Already CONFIRMED (held — no new promotion needed)

- `partial-extraction-must-use-original-code-from-head-not-fabricated-api` (W21 3/3 CONFIRMED)
- `subdirectory-partials-pattern-empirical-26-precedents` (W20 3/3 CONFIRMED; W30 = 20th deployment)
- `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` (W23 3/3 CONFIRMED, 13th observation at W30 T2)
- `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` (W23 3/3 CONFIRMED, 8th confirmation at W29; W30 N/A — zero `[LoggerMessage]` partials)
- `add-partial-keyword-to-monolithic-class-before-extraction` (W26.5 3/3 CONFIRMED, 28th application at W30 T0-D2)
- `multi-interface-partial-class-iframesink-and-iscriptcanapi` (W26.5 2/3 — held; W26 CanApi = 1st multi-interface; W30 SequenceSendService has 0 interfaces; observation N/A)
- `infrastructure-channel-layer-sister-pattern-empirical-w18-w25` (W25 2/3 — held; W18 + W25 observations; W30 is App/Services/MultiFrame, NOT Infrastructure/Channel)
- `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27-w29-LOCKED` (W29.5 3/3 CONFIRMED LOCKED — held; W30 has no JSON-persistence, observation N/A)
- `app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28` (W28.5 2/3 — held; W30 SequenceSendService has no async file-load lifecycle, observation N/A)
- `peak-can-channel-cs8795-accessibility-modifier-blocks-cross-partial-source-generator-impl` (W18 R1 1/3 — held)

## Cross-partial helper visibility pattern (CONFIRMED across 2 partials at W30)

Per W27.5 NEW observation `cross-partial-helper-visibility-works-across-3-partials` (1/3 confirmation at W27.5 → held), W30 sister-confirms this pattern works with **2 partials** (SendFlow + RowBuildFlow for SequenceSendService):

- **`TryBuildRow` + `SendOneAsync` private helpers** (in Flow B `RowBuildFlow.partial.cs`) are called from Flow A `SendFlow.partial.cs` (`SendAsync` body) — partial-class cross-partial visibility handles this automatically.

This is a **7th confirmation** (1st was W22, 2nd was W23, 3rd was W26, 4th was W27, 5th was W28, 6th was W29, 7th is W30) that the cross-partial helper visibility pattern is stable across multi-partial god-class extractions.

## Source-code verification

- **No source-code changes.** `src/PeakCan.Host.Core/` + `src/PeakCan.Host.Infrastructure/` + `src/PeakCan.Host.App/` all unchanged from main @ `bf12d22` (W30 SHIP closure + capture-decisions landing).
- `src/Directory.Build.props` v3.44.0 → v3.44.5 (4-field bump: `<Version>` + `<AssemblyVersion>` + `<FileVersion>` + `<InformationalVersion>`).
- `docs/user-manual.html` NOT modified (no user-facing change; sister of W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 vault-only PATCH user-manual unchanged).

## File impact summary

| File | Change | LoC |
|---|---|---|
| `src/Directory.Build.props` | v3.44.0 → v3.44.5 (4 fields) | -8 +8 |
| `docs/superpowers/lessons/app-services-multiframe-layer-sister-pattern-empirical-w30-1of3.md` | NEW (NEW 1/3 lesson) | +98 |
| `docs/superpowers/lessons/largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator-LOCKED.md` | NEW (LOCKED 7/3 consolidation) | +106 |
| `docs/superpowers/capture-decisions/2026-07-13-w30-5-vault-only-patch.md` | NEW (this file) | +200 |

Total LoC delta: +404 docs.

## Verification matrix

- `dotnet build src/PeakCan.Host.slnx`: 0 errors, 0 warnings (no source change).
- `dotnet test` (full solution): expected 0 new fails (no source change → identical to v3.44.0).
- Tag `v3.44.5` annotated at the squash-merge commit.
- GH release https://github.com/jasontaotao/peakcan-host/releases/tag/v3.44.5 published.
- Branch auto-deleted.

## Process lessons applied

- **W17 vault-only PATCH sister-precedent**: 1 consolidated cycle for multiple deferred PATCH promotions (cleaner history + clearer lesson-promotion atomicity).
- **W23.5-W25.5 vault-only PATCH sister-precedent**: 3 deferred PATCH cycles consolidated into 1 atomic commit.
- **W26.5 vault-only PATCH sister-precedent**: 1 atomic docs commit per cycle for multiple deferred PATCH promotions.
- **W27.5 vault-only PATCH sister-precedent**: 2 lesson candidates per cycle; `largest-method-can-move` LOCKED into MASTER-LESSON-CATALOG.
- **W28.5 vault-only PATCH sister-precedent**: 2 lesson candidates per cycle (1 NEW 1/3 → 2/3 + 1 6/3 consolidation since 3/3 LOCKED).
- **W29.5 vault-only PATCH sister-precedent**: 2 lesson candidates per cycle (1 NEW 1/3 + 1 LOCKED 3/3 → standalone lesson file).
- **D3 VAULT-ONLY PATCH convention** (W44.0 formalized): vault metadata changes ship as PATCH when src/ unchanged; this PATCH follows the convention.
- **D2 forward-bump** convention: source-only changes warrant MINOR; docs-only warrant PATCH (per W17 + W44.0 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5 sister precedent).

## Honest deviations

- (a) **W30.5 single-PATCH cycle** (sister of W17 + W23.5-W25.5 + W26.5 + W27.5 + W28.5 + W29.5) — 1 atomic docs commit per cycle vs 2-3 separate PATCH bumps. Trade-off accepted per sister precedent.
- (b) **User-manual.html NOT updated** for v3.44.5 (vault-only PATCH = docs-only = user-facing unchanged). The user-manual v3.44.0 row remains authoritative; v3.44.5 is **purely a lesson-promotion version bump**, not a user-facing deliverable.
- (c) **`app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28` 2/3 held**: W30 SequenceSendService has no async file-load lifecycle (SendAsync is orchestration, not file-load) — observation N/A. The pattern awaits a future refactor of an App/Services class with async file-load lifecycle.
- (d) **`multi-interface-partial-class-iframesink-and-iscriptcanapi` 2/3 held**: W30 SequenceSendService has 0 interfaces — observation N/A. The pattern awaits a future refactor of an App/Services class with multiple interfaces.
- (e) **`small-god-class-no-largest-method-keeps-all-inline-default-pattern` 1/3 HELD** (not promoted): W30 LARGEST method 91 LoC ≥ 60 LoC → W25 D5 deviation applies, NOT default D5. W30 confirms the W29 1/3 observation that default D5 applies to small god-classes ONLY (LARGEST < 50 LoC). No new small-god-class observation in W30 to promote to 2/3.

## What was captured

W30.5 SHIP closure = 2 captures dispatched: W30.5 prep + SHIP. Each per the W12-W29 pattern of `vault-pkm:pkm-capture` agent dispatched after each commit.

## Out of scope (YAGNI)

- No source-code change.
- No test change.
- No user-manual change.
- No 2 separate docs files (consolidated into this single capture-decisions).
- No MEMORY.md per-lesson entries (consolidated in this file).

## Next (post-PATCH-ship)

- **W31** — next god-class refactor candidate. Top remaining (>250 LoC) main files after W30: `ReplayService.cs` 265 LoC (Core/Replay) OR `StatsViewModel.cs` 263 LoC (App/ViewModels) OR `DbcSendViewModel.cs` 384 LoC (App/ViewModels — sister of W24, but already partial since W24; likely needs further splitting).
- **W30.5 vault-only PATCH audit** at next session start — verify all 3 promotions landed in vault MEMORY.md + agent-memory catalog + master-lesson-catalog.
- **Memory cleanup sweep**: MEMORY.md currently >155KB (over 24.4KB auto-memory cap); consider demoting older W* entries to sibling `MEMORY-history.md` archive file at next session-end per W26.5 + W27.5 + W28.5 + W29.5 + W30.5 pattern retention.
