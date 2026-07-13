# W28.5 v3.42.5 SHIP — vault-only PATCH lesson-promotion consolidation (W28 cycle)

**Date**: 2026-07-13
**Status**: SHIP-READY (docs-only, no source-code changes)
**Branch**: `feature/w28-5-vault-only-patch`
**Target version**: v3.42.5 PATCH (vault-only, +0/-0 source LoC)
**Sister pattern**: W17 vault-only PATCH (v3.31.1) + W23.5-W25.5 vault-only PATCH (v3.39.5) + W26.5 vault-only PATCH (v3.40.5) + W27.5 vault-only PATCH (v3.41.5) — single-cycle consolidation of multiple deferred PATCH promotions (5th occurrence).

## D1-D5 (carried from W28.5 design)

- **D1**: 2 lesson candidates get promotion entries (1 NEW 1/3 → 2/3 + 1 6/3 consolidation since 3/3 LOCKED).
- **D2**: Forward-bump v3.42.0 → v3.42.5 (per W17 + W23.5-W25.5 + W26.5 + W27.5 vault-only PATCH convention — no source-only changes warrant PATCH, not MINOR).
- **D3**: 1 docs-only commit (sister of W17 1-commit pattern + W23.5-W25.5 + W26.5 + W27.5 1-commit patterns).
- **D4**: Tier-3 ship via PR + squash + delete-branch + tag + GH release; 0 source LoC change verification.
- **D5**: Branch name `feature/w28-5-vault-only-patch` (per W23.5-W25.5 + W26.5 + W27.5 single-PATCH consolidated naming).

## Why consolidate into 1 cycle (sister pattern precedent — 5th occurrence)

Per W17 + W23.5-W25.5 + W26.5 + W27.5 consolidated-PATCH sister precedent:

- **W17 vault-only PATCH** (v3.31.1): promoted `wc-l-vs-python-splitlines-off-by-one` 3/3 CONFIRMED in 1 atomic commit
- **W23.5-W25.5 vault-only PATCH** (v3.39.5): promoted 6 lesson candidates (consolidated 3 deferred PATCH cycles) in 1 atomic commit
- **W26.5 vault-only PATCH** (v3.40.5): promoted 3 lesson candidates (W26 cycle promotions) in 1 atomic commit
- **W27.5 vault-only PATCH** (v3.41.5): promoted 2 lesson candidates (W27 cycle promotions) in 1 atomic commit
- **W28.5 vault-only PATCH** (v3.42.5): promotes 2 lesson candidates (W28 cycle promotions) in 1 atomic commit — sister precedent continued (5th occurrence)

## 2 lesson promotions consolidated

### W28 NEW 1/3 → 2/3 (W28 cycle)

1. **`app-services-load-async-lifecycle-sister-pattern-empirical-w27-w28`** — W28 1/3 → **W28.5 2/3**
   - W27 1-of-1: W27 RecentSessionsService LoadAsync 60 LoC moves (file-IO + JsonSerializer.Deserialize + atomic temp-rename Persist helper + Mutate state + Raise PropertyChanged event)
   - W28 2-of-1: W28 DbcService LoadAsync 79 LoC moves (file-IO + File.ReadAllBytesAsync + ReadDbcText decode + DbcParser.Parse + Mutate Current Volatile.Write + Raise DbcLoaded/LoadFailed event)
   - Pattern: App/Services god-class refactors where the target class has **public `LoadAsync` async method that reads file bytes + parses/decodes content + mutates state + raises event(s)** can follow the W27+W28 2-partial decomposition pattern (PersistenceOps/LoadLifecycle + Mutators/TextDecoding).
   - Application scope: All future peakcan-host god-class refactors of App/Services classes with async file-load lifecycle.
   - Awaiting 1 more observation across any future refactor with App/Services async file-load to be locked into MASTER-LESSON-CATALOG.

### W28 6/3 since 3/3 CONFIRMED (consolidation → held in MASTER-LESSON-CATALOG)

2. **`largest-method-can-move-when-flow-is-discrete-dispatcher-not-orchestrator`** — W28 6/3 (already **3/3 CONFIRMED** since W25) → **held in MASTER-LESSON-CATALOG at W28.5**
   - W22 1-of-1 (stay): W22 RecordBatchAsync 100 LoC STAYED in main (single central orchestration pipeline)
   - W23 2-of-1 (stay): W23 OnTimerTick 151 LoC STAYED in main (single tick-loop pipeline)
   - W25 3/3 CONFIRMED (move): W25 OnChannelFrame 73 LoC **MOVED** to FrameRouting.partial.cs (fan-out + error-isolation = sharp discrete flow boundary)
   - W26 4-of-1 (move): W26 OnFrame(CanFrame) 62 LoC **MOVED** to SinkLifecycle.partial.cs (frame-arrives → callback-fanout discrete dispatcher shape, sister of W25 OnChannelFrame)
   - W27 5-of-1 (move): W27 LoadAsync 60 LoC **MOVED** to PersistenceOps.partial.cs (file-I/O lifecycle = sharp discrete flow, 2nd sister of W25 pattern)
   - W28 6-of-1 (move): W28 LoadAsync 79 LoC **MOVED** to LoadLifecycle.partial.cs (file-I/O + parsing lifecycle = sharp discrete flow, 3rd sister of W27 pattern)
   - **6 observations = 2 stays (W22+W23) + 4 moves (W25+W26+W27+W28) lock in both the "stay default" + "move allowed for discrete flow" outcomes**
   - Principle: The "largest method stays inline" sister-principle (W12 + W14 + W18 + W19 + W20 + W21 D5) is NOT absolute — large methods CAN move when they map to a **discrete flow boundary** (frame-arrives → fan-out, frame-arrives → callback-fanout, file-IO-load → parse-JSON-or-empty, file-IO-load → DbcParse-or-error, tick-fires → per-tick state machine, etc.), not when they're a single central orchestration loop.
   - **HELD in MASTER-LESSON-CATALOG** at W28.5: 6 observations (2 stays + 4 moves) continue to lock in BOTH outcomes as canonical; W27.5 already LOCKED + W28.5 held = pattern stays canonical across multiple PATCH cycles.

## Already CONFIRMED (held)

- `partial-extraction-must-use-original-code-from-head-not-fabricated-api` (W21 3/3 CONFIRMED)
- `subdirectory-partials-pattern-empirical-26-precedents` (W20 3/3 CONFIRMED)
- `partial-extraction-requires-also-verifying-struct-constructors-not-just-method-signatures` (W23 3/3 CONFIRMED, 8th observation at W28 T1+T2)
- `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` (W23 3/3 CONFIRMED, 7th confirmation at W28)
- `add-partial-keyword-to-monolithic-class-before-extraction` (W26.5 3/3 CONFIRMED, 26th confirmation at W28)
- `multi-interface-partial-class-iframesink-and-iscriptcanapi` (W26.5 2/3 — held; W26 CanApi = 1st multi-interface; W28 DbcService has 0 interfaces; observation N/A)
- `infrastructure-channel-layer-sister-pattern-empirical-w18-w25` (W25 2/3 — held; W18 + W25 observations; W26+W27+W28 are App/Services sisters, NOT Infrastructure/Channel)
- `peak-can-channel-cs8795-accessibility-modifier-blocks-cross-partial-source-generator-impl` (W18 R1 1/3 — held)
- `app-services-json-persistence-layer-sister-pattern-empirical-w22-w27` (W27.5 2/3 — held; W22 RecordService + W27 RecentSessionsService = 2 confirmations of JSON-persistence; W28 DbcService has TOML-like DBC parser, NOT JsonSerializer; observation N/A)

## Cross-partial helper visibility pattern (CONFIRMED across 3 partials per W27.5)

Per W27.5 NEW observation `cross-partial-helper-visibility-works-across-3-partials` (1/3 confirmation), W28 sister-confirms this pattern works with **2 partials** (LoadLifecycle + TextDecoding for DbcService):

- **`ReadDbcBytesAsync` + `ReadDbcText` private static helpers** (in Flow B `TextDecoding.partial.cs`) are called from Flow A `LoadLifecycle.partial.cs` (`LoadAsync` body) — partial-class cross-partial visibility handles this automatically.

This is a **2nd confirmation** (1st was W27) that the cross-partial helper visibility pattern is stable across multi-partial god-class extractions. Sister to W26 + W25 precedents.

## Source-code verification

- **No source-code changes.** `src/PeakCan.Host.Core/` + `src/PeakCan.Host.Infrastructure/` + `src/PeakCan.Host.App/` all unchanged from main @ `ff6af77` (W28 SHIP).
- `src/Directory.Build.props` v3.42.0 → v3.42.5 (4-field bump: `<Version>` + `<AssemblyVersion>` + `<FileVersion>` + `<InformationalVersion>`).
- `docs/user-manual.html` NOT modified (no user-facing change; sister of W17 + W23.5-W25.5 + W26.5 + W27.5 vault-only PATCH user-manual unchanged).

## File impact summary

| File | Change | LoC |
|---|---|---|
| `src/Directory.Build.props` | v3.42.0 → v3.42.5 (4 fields) | -8 +8 |
| `docs/superpowers/capture-decisions/2026-07-13-w28-5-vault-only-patch.md` | NEW (this file) | +155 |

Total LoC delta: +147 docs.

## Verification matrix

- `dotnet build src/PeakCan.Host.slnx`: 0 errors, 0 warnings (no source change).
- `dotnet test` (full solution): expected 0 new fails (no source change → identical to v3.42.0).
- Tag `v3.42.5` annotated at the squash-merge commit.
- GH release https://github.com/jasontaotao/peakcan-host/releases/tag/v3.42.5 published.
- Branch auto-deleted.

## Process lessons applied

- **W17 vault-only PATCH sister-precedent**: 1 consolidated cycle for multiple deferred PATCH promotions (cleaner history + clearer lesson-promotion atomicity).
- **W23.5-W25.5 vault-only PATCH sister-precedent**: 3 deferred PATCH cycles consolidated into 1 atomic commit.
- **W26.5 vault-only PATCH sister-precedent**: 1 atomic docs commit per cycle for multiple deferred PATCH promotions.
- **W27.5 vault-only PATCH sister-precedent**: 2 lesson candidates per cycle; `largest-method-can-move` LOCKED into MASTER-LESSON-CATALOG.
- **D3 VAULT-ONLY PATCH convention** (W44.0 formalized): vault metadata changes ship as PATCH when src/ unchanged; this PATCH follows the convention.
- **D2 forward-bump** convention: source-only changes warrant MINOR; docs-only warrant PATCH (per W17 + W44.0 + W23.5-W25.5 + W26.5 + W27.5 sister precedent).

## Honest deviations

- (a) **W28.5 single-PATCH cycle** (sister of W17 + W23.5-W25.5 + W26.5 + W27.5) — 1 atomic docs commit per cycle vs 2 separate PATCH bumps. Trade-off accepted per sister precedent.
- (b) **User-manual.html NOT updated** for v3.42.5 (vault-only PATCH = docs-only = user-facing unchanged). The user-manual v3.42.0 row remains authoritative; v3.42.5 is **purely a lesson-promotion version bump**, not a user-facing deliverable.

## What was captured

W28.5 SHIP closure = 2 captures dispatched: W28.5 prep + SHIP. Each per the W12-W27 pattern of `vault-pkm:pkm-capture` agent dispatched after each commit.

## Out of scope (YAGNI)

- No source-code change.
- No test change.
- No user-manual change.
- No 2 separate docs files (consolidated into this single capture-decisions).
- No MEMORY.md per-lesson entries (consolidated in this file).

## Next (post-PATCH-ship)

- **W29** — next god-class refactor candidate. Top remaining (>250 LoC) main files after W28: `StatsViewModel.cs` 263 LoC (App/ViewModels) OR `SendViewModel.cs` 257 LoC (App/ViewModels sister) OR lower-LoC App-layer god-classes in 240-249 LoC range.
- **W28.5 vault-only PATCH audit** at next session start — verify all 2 promotions landed in vault MEMORY.md + agent-memory catalog.
