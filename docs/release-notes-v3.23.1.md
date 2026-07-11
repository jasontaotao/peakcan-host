# Release Notes v3.23.1 — W8 lesson promotions (PATCH, vault-only)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.23.1
**Branch:** `feature/w8-5-vault-lesson-promotions`
**Parent:** v3.23.0 MINOR (`badfd75` on origin/main)

## Why this PATCH

W8 TraceChartViewModel god-class refactor (v3.23.0) shipped with 3 NEW lesson candidates captured in devlog but not yet promoted to formal vault lessons. Per the **vault-only PATCH convention** formalized in v3.44.0 D3 (and reinforced in v3.45.0 D1: tree-touching process improvements ship as MINOR; vault metadata changes ship as PATCH when src/ tree is unchanged), this PATCH promotes those candidates to formal vault lesson files.

This is a **vault-only PATCH**: zero source-code changes, zero test changes, zero behavioral changes. The src/ tree is byte-identical to v3.23.0; only vault metadata (3 lesson files under `01-Projects/peakcan-host/development/lessons/`) is added.

## What this PATCH does

### Vault — 3 lesson files promoted

| File | Status | Confirmations | Source |
|---|---|---|---|
| `development/lessons/plan-loc-trajectory-table-must-account-for-deletion-not-just-marker-addition.md` | **CONFIRMED** | 3/3 | W8 Tasks 1, 2, 5 (TraceChartViewModel) |
| `development/lessons/partial-class-visibility-extends-to-private-fields-and-observableproperty-backing-fields.md` | **CANDIDATE** | 1/3 | W8 Task 3 (Playback, R3 VALIDATED) |
| `development/lessons/cross-partial-method-calls-resolve-identically-to-in-class-calls.md` | **CANDIDATE** | 1/3 | W8 Task 6 (SeriesManagement, R4 VALIDATED) |

### Lesson #1 — `plan-LoC-trajectory-table-must-account-for-deletion-not-just-marker-addition`

**Status**: **CONFIRMED** (promoted from 3/3 confirmations).

The W8 TraceChartViewModel plan's per-task LoC trajectory table used the formula `LoC_n = LoC_base + n markers`, which only accounts for marker additions. The correct formula is `LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker`. The W8 plan's table was off by 36 LoC after Task 1, 79 LoC after Task 2, and 297 LoC after Task 5 — all caught and corrected in real time by the deletion scripts' `assert original_count == ...` assertions (which used actual `wc -l` values).

**Why this matters**: future god-class refactor plans must compute the trajectory table using the deletion-aware formula. The plan table should be marked as a *prediction* (sanity check), not a *constraint* — deletion scripts assert against actual `wc -l`.

### Lesson #2 — `partial-class-visibility-extends-to-private-fields-and-observableproperty-backing-fields`

**Status**: CANDIDATE (1/3 confirmations, awaiting 2 more from W9+).

W8 Task 3 was the **first** W3-W8 god-class extraction that moved private state fields across partials (the 5 throttling-state fields for `UpdatePlaybackCursor`, including `[ObservableProperty] private int _invalidatePlotCallCount`). Risk R3 was: would CommunityToolkit.Mvvm's source generator correctly emit the public `InvalidatePlotCallCount` property if the backing field is in a different partial than the rest of the class?

**Outcome**: ✓ VALIDATED. Build clean, 18/18 TraceChart tests pass, throttle test still observes increments. The source generator processes each partial independently and emits the property + `OnXxxChanged` partial void hook in the partial containing the backing field.

**Awaiting**: 2 more confirmations from W9+ refactors that move private state fields across partials.

### Lesson #3 — `cross-partial-method-calls-resolve-identically-to-in-class-calls`

**Status**: CANDIDATE (1/3 confirmations, awaiting 2 more from W9+).

W8 Task 6 was the **first** W3-W8 extraction with explicit cross-partial caller enumeration as a risk (R4). `RecomputeHeights` had 5 cross-flow callers, 3 of which were cross-partial (from Flow D's `ToggleCollapse`/`SetFocus` and Flow F's `ApplyViewports`). Risk R4 was: would all 5 callers correctly resolve to the now-partial method?

**Outcome**: ✓ VALIDATED. Build clean, 18/18 TraceChart tests pass. Partial-class visibility is transparent at call sites — no `this.M()`, no namespace qualifier, no special routing.

**Awaiting**: 2 more confirmations from W9+ refactors with explicit cross-partial caller enumeration.

## What this PATCH does NOT do

- **No source-code changes.** src/ tree is byte-identical to v3.23.0.
- **No test changes.** Existing tests are unchanged.
- **No behavioral change.** Pure documentation promotion.
- **No new dependency versions, no new NuGet packages, no new assemblies.**

## Verification

- `git diff v3.23.0..HEAD --stat src/`: **0 changes** in src/ tree (vault-only PATCH)
- All 3 lesson files have YAML frontmatter with status field (CONFIRMED or CANDIDATE)
- All 3 lesson files include Why/Decisions/Evidence/How-to-apply sections matching the established template

## Risk notes

- **R1 (very low)**: Lesson file format drift — mitigated by following the established `partial-class-using-directives-are-file-scoped-not-class-scoped.md` template (YAML frontmatter + status + sections).
- **R2 (very low)**: Lesson promotion disagreement — mitigated by strict 3/3 confirmation threshold for CONFIRMED status; CANDIDATE status is used below threshold.

## Files in this ship

### Vault changes (3 NEW files, 0 modified)

```
01-Projects/peakcan-host/development/lessons/plan-loc-trajectory-table-must-account-for-deletion-not-just-marker-addition.md   (NEW, ~6 KB)
01-Projects/peakcan-host/development/lessons/partial-class-visibility-extends-to-private-fields-and-observableproperty-backing-fields.md   (NEW, ~5 KB)
01-Projects/peakcan-host/development/lessons/cross-partial-method-calls-resolve-identically-to-in-class-calls.md   (NEW, ~5 KB)
```

### Source code changes (1 modified)

```
src/Directory.Build.props   (3.23.0 → 3.23.1 — version bump only)
```

### Docs (1 NEW)

```
docs/release-notes-v3.23.1.md   (NEW, this file)
```

## For the next session

- W8.5 vault-only PATCH is the cleanest possible PATCH — zero source changes, pure documentation promotion.
- 2 NEW CANDIDATE lessons (R3 + R4) await 2 more confirmations each from W9+ before promotion to CONFIRMED.
- The plan-LoC-trajectory-table lesson (CONFIRMED) should be applied retroactively to **all** W3-W7 god-class refactor plans (they all had the same flawed formula in their per-task trajectory tables — though none caused execution errors because the deletion scripts asserted on actual `wc -l`).
- Next MINOR candidates: the 5 god-class refactor candidates outside `ViewModels/` identified in the W8 closeout scan (IsoTpLayer 806 + DbcParser 759 + AppHostBuilder 744 + UdsClient 704 + ScriptEngine 548 LoC).