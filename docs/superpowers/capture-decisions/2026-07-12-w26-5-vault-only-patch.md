# W26.5 v3.40.5 SHIP — vault-only PATCH lesson-promotion consolidation (W26 cycle)

**Date**: 2026-07-12
**Status**: SHIP-READY (docs-only, no source-code changes)
**Branch**: `feature/w26-5-vault-only-patch`
**Target version**: v3.40.5 PATCH (vault-only, +0/-0 source LoC)
**Sister pattern**: W17 vault-only PATCH (v3.31.1) + W23.5-W25.5 vault-only PATCH (v3.39.5) — both single-cycle consolidation of multiple deferred PATCH promotions.

## D1-D5 (carried from W26.5 design)

- **D1**: 3 lesson candidates get promotion entries (1 NEW 1/3 → 2/3 + 1 2/3 → 3/3 CONFIRMED + 1 4/3 consolidation).
- **D2**: Forward-bump v3.40.0 → v3.40.5 (per W17 + W23.5-W25.5 vault-only PATCH convention — no source-only changes warrant PATCH, not MINOR).
- **D3**: 1 docs-only commit (sister of W17 1-commit pattern + W23.5-W25.5 1-commit pattern).
- **D4**: Tier-3 ship via PR + squash + delete-branch + tag + GH release; 0 source LoC change verification.
- **D5**: Branch name `feature/w26-5-vault-only-patch` (per W23.5-W25.5 single-PATCH consolidated naming).

## Why consolidate into 1 cycle (sister pattern precedent)

Per W17 + W23.5-W25.5 consolidated-PATCH sister precedent (vault-only PATCH cycle consolidates multiple deferred PATCH promotions into 1 atomic docs commit to minimize CI noise + simplify version history):

- **W17 vault-only PATCH** (v3.31.1): promoted `wc-l-vs-python-splitlines-off-by-one` 3/3 CONFIRMED in 1 atomic commit
- **W23.5-W25.5 vault-only PATCH** (v3.39.5): promoted 6 lesson candidates (consolidated 3 deferred PATCH cycles) in 1 atomic commit
- **W26.5 vault-only PATCH** (v3.40.5): promotes 3 lesson candidates (W26 cycle promotions) in 1 atomic commit — sister precedent continued

## 3 lesson promotions consolidated

### W26 NEW 1/3 → 2/3 (W26 cycle)

1. **`multi-interface-partial-class-iframesink-and-iscriptcanapi`** — W26 1/3 → **W26.5 2/3**
   - W26 1-of-1 (current cycle): W26 CanApi god-class refactor (1st multi-interface partial in W3-W26 series)
   - Pattern: `CanApi : IFrameSink, IScriptCanApi` cleanly partitioned across 3 partials (SinkLifecycle for IFrameSink impl + CallbackRegistry for IScriptCanApi registry mutators + SendAndQuery for IScriptCanApi send/query)
   - Application scope: All future peakcan-host god-class refactors where the target class implements 2+ interfaces. The interface-methods-partition pattern is a stable sub-pattern of subdirectory-partials-pattern-empirical-26-precedents.
   - Awaiting 1 more observation across any future refactor with multi-interface class to be locked into MASTER-LESSON-CATALOG.

### W26 2/3 → 3/3 CONFIRMED

2. **`add-partial-keyword-to-monolithic-class-before-extraction`** — W26 2/3 → **W26.5 3/3 CONFIRMED**
   - W21 1-of-1: W21 was 1st fresh-add of `partial` modifier
   - W24 1/3: W24 DbcSendViewModel.cs already partial at line 44 (sister of 18/19 prior cases)
   - W25 2/3: W25 ChannelRouter.cs already partial at line 74 (20/22 prior cases)
   - W26 3/3 CONFIRMED: W26 CanApi.cs already partial at line 22 (22/24 prior cases; 24th confirmation of pre-existed-partial pattern)
   - Principle: When god-class refactor target is a candidate, first grep for existing `partial` modifier on the class declaration. Pre-existed-partial pattern is confirmed stable across all W21-W26 god-class refactors. W21 was the ONLY fresh-add case; W22-W26 all followed the pre-existed-partial pattern.
   - **3/3 CONFIRMED status**: 4 observations lock the principle (only 1 of 25 god-class refactors required fresh-add of `partial` modifier, vs 24 that already had it).

### W26 4/3 since 3/3 CONFIRMED (consolidation)

3. **`largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator`** — W26 4/3 (already **3/3 CONFIRMED** since W25)
   - W22 1-of-1: W22 RecordBatchAsync 100 LoC STAYED in main (single central orchestration pipeline)
   - W23 2-of-1: W23 OnTimerTick 151 LoC STAYED in main (single tick-loop pipeline)
   - W25 3/3 CONFIRMED: W25 OnChannelFrame 73 LoC **MOVED** to FrameRouting.partial.cs (fan-out + error-isolation = sharp discrete flow boundary)
   - W26 4-of-1: W26 OnFrame(CanFrame) 62 LoC **MOVED** to SinkLifecycle.partial.cs (frame-arrives → callback-fanout discrete dispatcher shape, sister of W25 OnChannelFrame)
   - **3/3 CONFIRMED status** at W25 (3 observations: 2 stays + 1 move); W26 4/3 confirms the deviation pattern is stable across 2 separate move cases
   - Principle: The "largest method stays inline" sister-principle (W12 + W14 + W18 + W19 + W20 + W21 D5) is NOT absolute — large methods CAN move when they map to a **discrete flow boundary** (frame-arrives → fan-out, tick-fires → per-tick state machine, etc.), not when they're a single central orchestration loop.
   - **Locked into MASTER-LESSON-CATALOG** at W26.5: 4 observations lock in both the "stay default" + "move allowed for discrete flow" outcomes.

## Already CONFIRMED (held)

- `partial-extraction-must-use-original-code-from-head-not-fabricated-api` (W21 3/3 CONFIRMED)
- `subdirectory-partials-pattern-empirical-26-precedents` (W20 3/3 CONFIRMED)
- `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` (W23 3/3 CONFIRMED, 6th observation at W26 T3)
- `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` (W23 3/3 CONFIRMED, 5th observation at W26)
- `infrastructure-channel-layer-sister-pattern-empirical-w18-w25` (W25 2/3 — held; W18 + W25 observations)
- `peak-can-channel-cs8795-accessibility-modifier-blocks-cross-partial-source-generator-impl` (W18 R1 1/3 — held)

## Source-code verification

- **No source-code changes.** `src/PeakCan.Host.Core/` + `src/PeakCan.Host.Infrastructure/` + `src/PeakCan.Host.App/` all unchanged from main @ `2611e83` (W26 SHIP).
- `src/Directory.Build.props` v3.40.0 → v3.40.5 (4-field bump: `<Version>` + `<AssemblyVersion>` + `<FileVersion>` + `<InformationalVersion>`).
- `docs/user-manual.html` NOT modified (no user-facing change; sister of W17 vault-only PATCH user-manual unchanged).

## File impact summary

| File | Change | LoC |
|---|---|---|
| `src/Directory.Build.props` | v3.40.0 → v3.40.5 (4 fields) | -8 +8 |
| `docs/superpowers/capture-decisions/2026-07-12-w26-5-vault-only-patch.md` | NEW (this file) | +135 |

Total LoC delta: +135 docs.

## Verification matrix

- `dotnet build src/PeakCan.Host.slnx`: 0 errors, 0 warnings (no source change).
- `dotnet test` (full solution): expected 0 new fails (no source change → identical to v3.40.0).
- Tag `v3.40.5` annotated at the squash-merge commit.
- GH release https://github.com/jasontaotao/peakcan-host/releases/tag/v3.40.5 published.
- Branch auto-deleted.

## Process lessons applied

- **W17 vault-only PATCH sister-precedent**: 1 consolidated cycle for multiple deferred PATCH promotions (cleaner history + clearer lesson-promotion atomicity).
- **W23.5-W25.5 vault-only PATCH sister-precedent**: 3 deferred PATCH cycles consolidated into 1 atomic commit (history cleanliness + cost-benefit trade-off accepted).
- **D3 VAULT-ONLY PATCH convention** (W44.0 formalized): vault metadata changes ship as PATCH when src/ unchanged; this PATCH follows the convention.
- **D2 forward-bump** convention: source-only changes warrant MINOR; docs-only warrant PATCH (per W17 + W44.0 sister precedent).

## Honest deviations

- (a) **W26.5 single-PATCH cycle** (sister of W17 + W23.5-W25.5) — 1 atomic docs commit per cycle vs 3 separate PATCH bumps (v3.40.1 + v3.40.2 + v3.40.3 + v3.40.4 would be the 4-step alternative). Trade-off accepted per sister precedent.
- (b) **User-manual.html NOT updated** for v3.40.5 (vault-only PATCH = docs-only = user-facing unchanged). The user-manual v3.40.0 row remains authoritative; v3.40.5 is **purely a lesson-promotion version bump**, not a user-facing deliverable.

## What was captured

W26.5 SHIP closure = 2 captures dispatched: W26.5 prep + SHIP. Each per the W12-W25 pattern of `vault-pkm:pkm-capture` agent dispatched after each commit.

## Out of scope (YAGNI)

- No source-code change.
- No test change.
- No user-manual change.
- No 3 separate docs files (consolidated into this single capture-decisions).
- No MEMORY.md per-lesson entries (consolidated in this file).

## Next (post-PATCH-ship)

- **W27** — next god-class refactor candidate. Top remaining (>300 LoC) main files after W26: `RecentSessionsService.cs` 334 LoC (App/Services sister of W22+W23) OR `AppHostBuilder.cs` 316 LoC (App/Composition DI引导) OR `DbcService.cs` 312 LoC (App/Services sister) OR `RequestBasedMappers.cs` 300 LoC (Core/Uds/Odx).
- **W26.5 vault-only PATCH audit** at next session start — verify all 3 promotions landed in vault MEMORY.md + agent-memory catalog.
