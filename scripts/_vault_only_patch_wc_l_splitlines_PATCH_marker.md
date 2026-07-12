# vault-only PATCH marker — wc-l-vs-python-splitlines → CONFIRMED (3 observations)

**Date**: 2026-07-12
**Spec**: docs/superpowers/specs/2026-07-12-vault-only-patch-wc-l-splitlines-confirmed.md
**Plan**: docs/superpowers/plans/2026-07-12-vault-only-patch-wc-l-splitlines-confirmed.md
**Lesson ID**: wc-l-vs-python-splitlines-off-by-one-on-untrailing-newline-files-requires-loose-assertion
**Status**: 3/3 → **CONFIRMED**
**Branch**: feature/vault-only-patch-wc-l-splitlines-confirmation
**Tag**: v3.31.1
**Source-code changes**: ZERO (vault-only)

## 3 Confirming Observations

| # | Date | Commit | Refactor | File affected | Original assertion (failed) | Fix pattern applied |
|---|---|---|---|---|---|---|
| 1 | 2026-07-12 | `5fdf8c9` | W12 T1 (ScriptEngine partial split, originally planned name "UdsClient" — actually mis-attributed) | `src/PeakCan.Host.App/Services/Scripting/ScriptEngine.cs` (Step 1 pre-state was 548 LoC) | `assert len(lines) == 551` (post-delete count) | loose-assertion: `assert 550 < len(lines) < 552` accepted |
| 2 | 2026-07-12 | (W13 T1 ExecutionLifecycle — pre-script-commit) | W13 T1 (AscParser partial split) | `src/PeakCan.Host.Core/Replay/AscParser.cs` (pre-state 513 LoC) | `assert original_count == 513` failed (`splitlines` reports 514) | loose-assertion: `assert original_count in (513, 514)`; post-delete count captured as `len(lines)` |
| 3 | 2026-07-12 | `85025a2` (W15 T1 PlaybackLifecycle) | W15 T1 (ReplayTimeline partial split) | `src/PeakCan.Host.Core/Replay/ReplayTimeline.cs` (pre-state 469 LoC) | `assert original_count in (468, 469)` acceptable but post-marker assertion `assert len(lines) == 391` strict | loose-assertion tolerance accepted; post-marker `assert len(lines) in (390, 391, 392)` |

**Pattern**: 3 out of 3 god-class refactors in this series that used `wc -l` count assertions hit the un-trailing-newline off-by-one bug. The peakcan-host `.gitattributes` normalizes CRLF→LF on commit, but **does not enforce trailing newline** — relying on individual files to terminate their last line with `\n`. C# / .NET tooling (e.g. `dotnet format`) typically writes a trailing `\n`, but **manual edits via Edit tool and some Unix-via-Git-Bash pipelines occasionally strip it**, creating the discrepancy.

## Lesson PRINCIPLE

When god-class partial-extraction deletion-script assertions compare line counts, use **loose-assertion** (`abs(actual - expected) <= 1`) OR capture `len(lines)` post-delete as the actual count. Files with un-trailing-newline final lines will mismatch `wc -l` (counts `\n`) vs Python `splitlines(keepends=True)` (counts elements).

## Why this matters

W8.5 D7 LoC-trajectory formula predicts exact deletions; the off-by-one error breaks the formula's predictive confidence and forces multiple retries. The loose-assertion pattern + post-delete-count captures the formula intent without breaking on the un-trailing-newline edge case. Each retry adds ~30s to the agent's wall-clock; over the 13 partial-extraction tasks in W12-W16, this would have been 6.5 minutes of wasted time without the lesson.

## Application scope

All future peakcan-host partial-extraction refactors (W18+) and any other repo that uses Python-deletion-scripts + C# files. **Sister-lesson to W8.5 D7 LoC formula** (already CONFIRMED). Both lessons promoted from 1/3 → CONFIRMED via parallel observation tracks.

## Lesson State Transitions

| Date | State | Trigger |
|---|---|---|
| 2026-07-12 T1 (W12) | 1/3 candidate | First observation — ScriptEngine `len(lines)` failed |
| 2026-07-12 T1 (W13) | 2/3 candidate | Second observation — AscParser `original_count` failed |
| 2026-07-12 T1 (W15) | 3/3 candidate | Third observation — ReplayTimeline `len(lines)` post-marker |
| 2026-07-12 T1 (W17) | **CONFIRMED** | This patch — synthesis + tier-3-ship |

## Quick Reference (for future refactors)

```python
# GOOD: loose assertion pattern
content = MAIN.read_text(encoding="utf-8")
if not content.endswith("\n"):
    content += "\n"  # normalize — but actually DON'T normalize, just loosen assert
lines = content.splitlines(keepends=True)
assert original_count in (469, 470)  # accept splitlines-off-by-one tolerance
# ... do deletion ...
assert len(lines) in (388, 389, 390)  # post-marker tolerance

# BETTER: capture actual count
actual_pre = len(lines)
del lines[start:end]
actual_post = len(lines)
# assertion becomes abs(actual_post - expected_post) <= 1
```

## Related Lessons (peakcan-host sister-lesson catalog)

| Lesson | Status | Sister | Observation count |
|---|---|---|---|
| W8.5 D7 LoC formula | CONFIRMED | (master) | 15 transitions |
| **wc-l-vs-python-splitlines** | **CONFIRMED (this patch)** | W8.5 D7 | 3 observations |
| xmldoc-grep-test-breaks-when-partial-class-split | 1/3 | W12 T4 | 1 observation |
| execution-lifecycle-cluster-must-not-be-split-across-partials | 2/3 | W3 R3 | 2 observations |
| internal-sealed-partial-class-modifier-doesnt-constrain | 1/3 | W10+W13+W14 | 1 observation |
| replay-view-model-manual-properties-with-partial-class-visibility | 1/3 | W16 T1 | 1 observation |
| sibling-file-pattern-vs-subdirectory-predecessors-set-precedent | 1/3 | W16 | 1 observation |
