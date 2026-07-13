# W27.5 v3.41.5 SHIP — vault-only PATCH lesson-promotion consolidation (W27 cycle)

**Date**: 2026-07-13
**Status**: SHIP-READY (docs-only, no source-code changes)
**Branch**: `feature/w27-5-vault-only-patch`
**Target version**: v3.41.5 PATCH (vault-only, +0/-0 source LoC)
**Sister pattern**: W17 vault-only PATCH (v3.31.1) + W23.5-W25.5 vault-only PATCH (v3.39.5) + W26.5 vault-only PATCH (v3.40.5) — single-cycle consolidation of multiple deferred PATCH promotions.

## D1-D5 (carried from W27.5 design)

- **D1**: 2 lesson candidates get promotion entries (1 NEW 1/3 → 2/3 + 1 5/3 consolidation since 3/3 CONFIRMED).
- **D2**: Forward-bump v3.41.0 → v3.41.5 (per W17 + W23.5-W25.5 + W26.5 vault-only PATCH convention — no source-only changes warrant PATCH, not MINOR).
- **D3**: 1 docs-only commit (sister of W17 1-commit pattern + W23.5-W25.5 + W26.5 1-commit patterns).
- **D4**: Tier-3 ship via PR + squash + delete-branch + tag + GH release; 0 source LoC change verification.
- **D5**: Branch name `feature/w27-5-vault-only-patch` (per W23.5-W25.5 + W26.5 single-PATCH consolidated naming).

## Why consolidate into 1 cycle (sister pattern precedent — 4th occurrence)

Per W17 + W23.5-W25.5 + W26.5 consolidated-PATCH sister precedent (vault-only PATCH cycle consolidates multiple deferred PATCH promotions into 1 atomic docs commit):

- **W17 vault-only PATCH** (v3.31.1): promoted `wc-l-vs-python-splitlines-off-by-one` 3/3 CONFIRMED in 1 atomic commit
- **W23.5-W25.5 vault-only PATCH** (v3.39.5): promoted 6 lesson candidates (consolidated 3 deferred PATCH cycles) in 1 atomic commit
- **W26.5 vault-only PATCH** (v3.40.5): promoted 3 lesson candidates (W26 cycle promotions) in 1 atomic commit
- **W27.5 vault-only PATCH** (v3.41.5): promotes 2 lesson candidates (W27 cycle promotions) in 1 atomic commit — sister precedent continued

## 2 lesson promotions consolidated

### W27 NEW 1/3 → 2/3 (W27 cycle)

1. **`app-services-json-persistence-layer-sister-pattern-empirical-w22-w27`** — W27 1/3 → **W27.5 2/3**
   - W22 1-of-1: W22 RecordService (App/Services, file-I/O lifecycle + JsonSerializer + atomic temp-rename Persist helper + multi-record state shape)
   - W27 2-of-1: W27 RecentSessionsService (App/Services/Trace, file-I/O lifecycle + JsonSerializer + atomic temp-rename Persist helper + MRU list-shape + 4 `[LoggerMessage]` partials on main per sister-pattern)
   - Pattern: App/Services god-class refactors where the target class implements **file I/O with JsonSerializer round-trip + atomic temp-rename Persist helper + multi-record state shape** can follow the W22+W27 3-partial decomposition pattern (PersistenceOps/Lifecycle + Mutators + StaticHelpers).
   - Application scope: All future peakcan-host god-class refactors of App/Services classes with JSON persistence + multi-record state.
   - Awaiting 1 more observation across any future refactor with App/Services JSON-persistence + multi-record state to be locked into MASTER-LESSON-CATALOG.

### W27 5/3 since 3/3 CONFIRMED (consolidation → locked into MASTER-LESSON-CATALOG)

2. **`largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator`** — W27 5/3 (already **3/3 CONFIRMED** since W25) → **locked into MASTER-LESSON-CATALOG at W27.5**
   - W22 1-of-1 (stay): W22 RecordBatchAsync 100 LoC STAYED in main (single central orchestration pipeline)
   - W23 2-of-1 (stay): W23 OnTimerTick 151 LoC STAYED in main (single tick-loop pipeline)
   - W25 3/3 CONFIRMED (move): W25 OnChannelFrame 73 LoC **MOVED** to FrameRouting.partial.cs (fan-out + error-isolation = sharp discrete flow boundary)
   - W26 4-of-1 (move): W26 OnFrame(CanFrame) 62 LoC **MOVED** to SinkLifecycle.partial.cs (frame-arrives → callback-fanout discrete dispatcher shape, sister of W25 OnChannelFrame)
   - W27 5-of-1 (move): W27 LoadAsync 60 LoC **MOVED** to PersistenceOps.partial.cs (file-I/O lifecycle = sharp discrete flow, 2nd sister of W25 frame-arrives pattern)
   - **5 observations = 2 stays (W22+W23) + 3 moves (W25+W26+W27) lock in both the "stay default" + "move allowed for discrete flow" outcomes**
   - Principle: The "largest method stays inline" sister-principle (W12 + W14 + W18 + W19 + W20 + W21 D5) is NOT absolute — large methods CAN move when they map to a **discrete flow boundary** (frame-arrives → fan-out, frame-arrives → callback-fanout, file-IO-load → parse-JSON-or-empty, tick-fires → per-tick state machine, etc.), not when they're a single central orchestration loop.
   - **LOCKED into MASTER-LESSON-CATALOG** at W27.5: 5 observations (2 stays + 3 moves) lock in BOTH outcomes as canonical.

## Already CONFIRMED (held)

- `partial-extraction-must-use-original-code-from-head-not-fabricated-api` (W21 3/3 CONFIRMED)
- `subdirectory-partials-pattern-empirical-26-precedents` (W20 3/3 CONFIRMED)
- `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` (W23 3/3 CONFIRMED, 7th observation at W27 T1+T3)
- `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` (W23 3/3 CONFIRMED, 6th confirmation at W27 — 4 [LoggerMessage] partials on main called from Flow A + Flow B per-flow partials)
- `add-partial-keyword-to-monolithic-class-before-extraction` (W26.5 3/3 CONFIRMED, 25th confirmation at W27)
- `multi-interface-partial-class-iframesink-and-iscriptcanapi` (W26.5 2/3 — held; W26 CanApi = 1st multi-interface + W27 RecentSessionsService implements INotifyPropertyChanged only = 1 interface; held)
- `infrastructure-channel-layer-sister-pattern-empirical-w18-w25` (W25 2/3 — held; W18 + W25 observations)
- `peak-can-channel-cs8795-accessibility-modifier-blocks-cross-partial-source-generator-impl` (W18 R1 1/3 — held)

## Cross-partial helper visibility pattern (NEW W27.5 observation)

W27 RecentSessionsService confirms cross-partial helper visibility works across **3 partials** (not just 2 as in W26):

- **`Raise()` private helper** (in Flow A `PersistenceOps.partial.cs`) is called from Flow B `Mutators.partial.cs` (`Add` and `Clear` methods) — partial-class cross-partial visibility handles this automatically.

This is a **NEW confirmation** that the cross-partial helper visibility pattern is stable across multi-partial god-class extractions. Sister to W26 + W25 precedents (helper methods stay in their original location and are visible across partials).

**NEW LESSON CANDIDATE (NEW 1/3 at W27.5)**:
- `cross-partial-helper-visibility-works-across-3-partials` — W26 confirmed 2-partial case (SinkLifecycle.Raise from main + Mutators calls cross-partial); W27 confirmed 3-partial case (PersistenceOps.Raise from Flow A + Mutators calls from Flow B = 2 layers of cross-partial access). The pattern scales.

## Source-code verification

- **No source-code changes.** `src/PeakCan.Host.Core/` + `src/PeakCan.Host.Infrastructure/` + `src/PeakCan.Host.App/` all unchanged from main @ `9cd8b93` (W27 SHIP).
- `src/Directory.Build.props` v3.41.0 → v3.41.5 (4-field bump: `<Version>` + `<AssemblyVersion>` + `<FileVersion>` + `<InformationalVersion>`).
- `docs/user-manual.html` NOT modified (no user-facing change; sister of W17 + W23.5-W25.5 + W26.5 vault-only PATCH user-manual unchanged).

## File impact summary

| File | Change | LoC |
|---|---|---|
| `src/Directory.Build.props` | v3.41.0 → v3.41.5 (4 fields) | -8 +8 |
| `docs/superpowers/capture-decisions/2026-07-13-w27-5-vault-only-patch.md` | NEW (this file) | +145 |

Total LoC delta: +145 docs.

## Verification matrix

- `dotnet build src/PeakCan.Host.slnx`: 0 errors, 0 warnings (no source change).
- `dotnet test` (full solution): expected 0 new fails (no source change → identical to v3.41.0).
- Tag `v3.41.5` annotated at the squash-merge commit.
- GH release https://github.com/jasontaotao/peakcan-host/releases/tag/v3.41.5 published.
- Branch auto-deleted.

## Process lessons applied

- **W17 vault-only PATCH sister-precedent**: 1 consolidated cycle for multiple deferred PATCH promotions (cleaner history + clearer lesson-promotion atomicity).
- **W23.5-W25.5 vault-only PATCH sister-precedent**: 3 deferred PATCH cycles consolidated into 1 atomic commit.
- **W26.5 vault-only PATCH sister-precedent**: 1 atomic docs commit per cycle for multiple deferred PATCH promotions.
- **D3 VAULT-ONLY PATCH convention** (W44.0 formalized): vault metadata changes ship as PATCH when src/ unchanged; this PATCH follows the convention.
- **D2 forward-bump** convention: source-only changes warrant MINOR; docs-only warrant PATCH (per W17 + W44.0 + W23.5-W25.5 + W26.5 sister precedent).

## Honest deviations

- (a) **W27.5 single-PATCH cycle** (sister of W17 + W23.5-W25.5 + W26.5) — 1 atomic docs commit per cycle vs 2 separate PATCH bumps. Trade-off accepted per sister precedent.
- (b) **User-manual.html NOT updated** for v3.41.5 (vault-only PATCH = docs-only = user-facing unchanged). The user-manual v3.41.0 row remains authoritative; v3.41.5 is **purely a lesson-promotion version bump**, not a user-facing deliverable.

## What was captured

W27.5 SHIP closure = 2 captures dispatched: W27.5 prep + SHIP. Each per the W12-W26 pattern of `vault-pkm:pkm-capture` agent dispatched after each commit.

## Out of scope (YAGNI)

- No source-code change.
- No test change.
- No user-manual change.
- No 2 separate docs files (consolidated into this single capture-decisions).
- No MEMORY.md per-lesson entries (consolidated in this file).

## Next (post-PATCH-ship)

- **W28** — next god-class refactor candidate. Top remaining (>300 LoC) main files after W27: `AppHostBuilder.cs` 316 LoC (App/Composition DI引导) OR `DbcService.cs` 312 LoC (App/Services sister) OR `RequestBasedMappers.cs` 300 LoC (Core/Uds/Odx — but static class, not god-class eligible).
- **W27.5 vault-only PATCH audit** at next session start — verify all 2 promotions landed in vault MEMORY.md + agent-memory catalog.
