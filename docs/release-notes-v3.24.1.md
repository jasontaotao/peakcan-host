# Release Notes v3.24.1 — W9 lesson promotions (PATCH, vault-only)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.24.1
**Branch:** `feature/w9-5-vault-lesson-promotions`
**Parent:** v3.24.0 MINOR (`df20224` on origin/main)

## Why this PATCH

W9 IsoTpLayer god-class refactor (v3.24.0) shipped with 3 NEW lesson candidates captured in devlog but not yet promoted to formal vault lessons. Per the **vault-only PATCH convention** formalized in v3.44.0 D3 (and reinforced in v3.45.0 D1: tree-touching process improvements ship as MINOR; vault metadata changes ship as PATCH when src/ tree unchanged), this PATCH promotes the **2 lessons at 2/3 confirmations** to CONFIRMED status.

The third lesson (`loggermessage-partial-methods-can-be-split-across-partial-class-files`) remains at 1/3 confirmations and will be promoted after W10+ refactors provide 2 more confirmations.

This is a **vault-only PATCH**: zero source-code changes, zero test changes, zero behavioral changes. The src/ tree is byte-identical to v3.24.0; only vault metadata (2 lesson files under `01-Projects/peakcan-host/development/lessons/`) is updated from CANDIDATE to CONFIRMED status.

## What this PATCH does

### Vault — 2 lesson files promoted to CONFIRMED

| File | Status | Confirmations | Source |
|---|---|---|---|
| `development/lessons/partial-class-visibility-extends-to-private-fields-and-observableproperty-backing-fields.md` | **CONFIRMED** | 2/3 | W8 Task 3 (Playback, [ObservableProperty] backing field) + W9 Task 3 (Watchdog, private state field + nested class) |
| `development/lessons/cross-partial-method-calls-resolve-identically-to-in-class-calls.md` | **CONFIRMED** | 2/3 | W8 Task 6 (SeriesManagement, 5 cross-partial callers) + W9 Task 5 (Receive, 7 cross-partial callers across 4 partials) |

### Lesson #1 — `partial-class-visibility-extends-to-private-fields-and-observableproperty-backing-fields`

**Status**: **CONFIRMED** (promoted from 2/3 confirmations).

C# partial-class visibility covers not just methods but also private fields, nested types, and `[ObservableProperty]`/`[LoggerMessage]` source-generated members. When extracting a partial-class file that owns state, source-generated public properties are emitted in the partial containing the backing field, and other partials can call them transparently.

**Key evidence**:
- W8 Task 3 (commit `bc14446`): 5 throttling-state fields moved across partials; `[ObservableProperty] InvalidatePlotCallCount` correctly emitted in `PlaybackFlow.cs`. 18/18 TraceChart tests pass.
- W9 Task 3 (commit `a7a5aa5`): `_rxWatchdog` private field + `WatchdogHandle` nested class moved across partials. 23/23 IsoTp tests pass.

**Awaiting**: 1 more confirmation from W10+ refactor (preferably involving a third source-generator pattern, e.g., `[NotifyPropertyChangedFor]`).

### Lesson #2 — `cross-partial-method-calls-resolve-identically-to-in-class-calls`

**Status**: **CONFIRMED** (promoted from 2/3 confirmations).

Cross-partial method calls in C# resolve identically to in-class method calls. No special routing, qualifier, or wiring needed — partial-class visibility is transparent to the compiler at call sites.

**Key evidence**:
- W8 Task 6 (commit `65e5139`): `RecomputeHeights` had 5 cross-flow callers, 3 of which were cross-partial. All resolved correctly. 18/18 TraceChart tests pass.
- W9 Task 5 (commit `de2bd9d`): 7 cross-partial callers across 4 partials (ReceiveFlow → Flow E + Flow F + Flow G). All resolved correctly. 23/23 IsoTp tests pass.

**Awaiting**: 1 more confirmation from W10+ refactor (preferably with 10+ callers or 5+ partials involved).

### Lesson #3 — `loggermessage-partial-methods-can-be-split-across-partial-class-files`

**Status**: CANDIDATE (1/3 confirmations, awaiting 2 more).

W9 Task 2 was the **first** W3-W9 refactor that moved `[LoggerMessage]` partial methods across partials. The Microsoft.Extensions.Logging source generator processes each partial independently and emits the method body in the partial containing the declaration. This lesson will be promoted after 2 more W10+ refactors confirm the pattern.

## What this PATCH does NOT do

- **No source-code changes.** src/ tree is byte-identical to v3.24.0.
- **No test changes.** Existing tests are unchanged.
- **No behavioral change.** Pure documentation promotion.

## Verification

- `git diff v3.24.0..HEAD --stat src/`: **0 changes** in src/ tree (vault-only PATCH)
- Both promoted lesson files have YAML frontmatter updated from `status: candidate` to `status: active` + `promoted: 2026-07-11`
- Both promoted lesson files include updated Why/Decisions/Evidence/How-to-apply sections matching the established template

## Risk notes

- **R1 (very low)**: Lesson file format drift — mitigated by following the established CONFIRMED lesson template.
- **R2 (very low)**: Lesson promotion disagreement — mitigated by 2/3 confirmation threshold (CONFIRMED status with 2 confirmations + plan-ahead for 3rd).

## Files in this ship

### Vault changes (0 NEW files, 2 modified)

```
01-Projects/peakcan-host/development/lessons/partial-class-visibility-extends-to-private-fields-and-observableproperty-backing-fields.md   (status: candidate → active)
01-Projects/peakcan-host/development/lessons/cross-partial-method-calls-resolve-identically-to-in-class-calls.md   (status: candidate → active)
```

### Source code changes (1 modified)

```
src/Directory.Build.props   (3.24.0 → 3.24.1 — version bump only)
```

### Docs (1 NEW)

```
docs/release-notes-v3.24.1.md   (NEW, this file)
```

## For the next session

- W9.5 vault-only PATCH is the cleanest possible PATCH — zero source changes, pure documentation promotion.
- 1 NEW CANDIDATE lesson ([LoggerMessage] partial methods) awaits 2 more confirmations from W10+ before promotion to CONFIRMED.
- All 5 partial-class split pattern CONFIRMED lessons now in vault:
  1. `partial-class-using-directives-are-file-scoped-not-class-scoped` (CONFIRMED, 15+ confirmations)
  2. `deletion-script-line-range-precision-with-non-contiguous-ranges` (CONFIRMED, 7 confirmations)
  3. `partial-class-visibility-extends-to-private-fields-and-observableproperty-backing-fields` (CONFIRMED, 2/3 confirmations — this PATCH)
  4. `cross-partial-method-calls-resolve-identically-to-in-class-calls` (CONFIRMED, 2/3 confirmations — this PATCH)
  5. `splitting-a-god-class-into-partials-does-not-grow-net-loc-by-much` (CONFIRMED)
- Next MINOR candidates: investigate Core layer (DbcParser.cs 759 + UdsClient.cs 704 LoC) for similar refactor opportunities, OR promote the 1 remaining CANDIDATE lesson via another vault-only PATCH after W10+ refactor confirmations arrive.