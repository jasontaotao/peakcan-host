# W17 Spec — vault-only PATCH: wc-l-vs-python-splitlines lesson → CONFIRMED

> **For agentic workers:** Single-task verification of 3 observations + lesson-promotion. Zero source-code changes.

## Goal

Promote the lesson candidate `wc-l-vs-python-splitlines-off-by-one-on-untrailing-newline-files-requires-loose-assertion` from **3/3 confirmation** (observations across W12 T1, W13 T1, W15 T1) to **CONFIRMED** status. Document the lesson-principle + record 3 confirming observations + apply to all peakcan-host deletion-script-locating future god-class refactors.

## 3 Confirming Observations

### Observation 1: W12 T1 (2026-07-12)

- **Context**: `scripts/w12_task1_delete_executionlifecycleflow.py` for UdsClient.cs partial split. Initial assertion `assert len(lines) == 551` failed because Python `splitlines(keepends=True)` reported 552 lines (vs `wc -l` reporting 551) due to last line lacking trailing newline.
- **Failure reproduction**: 552 ≠ 551 → assertion error.
- **Fix applied**: Loosen assertion to `551 < 552` range + W8.5 D7 formula allowed ±1 tolerance. Adjusted LoC prediction to match actual 552.

### Observation 2: W13 T1 (2026-07-12)

- **Context**: `scripts/w13_task1_delete_datalineparserflow.py` for AscParser.cs partial split. Original wc-l 513 vs Python splitlines 514 (same off-by-one).
- **Failure reproduction**: 514 ≠ 513 → assertion error.
- **Fix applied**: Apply loose-assertion pattern (`513 / 514`) and track pre-marker LoC as `len(lines)` after deletion rather than calculated. Lesson truly proven.

### Observation 3: W15 T1 (2026-07-12)

- **Context**: `scripts/w15_task1_delete_playbacklifecycleflow.py` for ReplayTimeline.cs partial split. wc-l 469 vs Python splitlines 470.
- **Failure reproduction**: Original assertion `assert original_count in (469, 470)` correctly accepted, but the post-deletion assertion `assert expected_pre_marker in (362, 363)` was too strict vs splitlines-counted 363.
- **Fix applied**: Apply loose-assertion + capture `len(lines)` post-delete.

**Pattern**: All 3 observations occurred when a C# / Python file's last line lacked `\n`. **Fix pattern**: Loosen assertions to accept ±1 LoC tolerance OR capture `len(lines)` post-delete as the actual count.

## Lesson Statement (CONFIRMED candidate)

**Title**: `wc-l-vs-python-splitlines-off-by-one-on-untrailing-newline-files-requires-loose-assertion`

**Principle**: When god-class partial-extraction deletion-script assertions compare line counts, always use **loose-assertion** (`abs(actual - expected) <= 1`) OR capture `len(lines)` post-deletion as the actual count. Never use exact equality. Files with un-trailing-newline final lines will mismatch `wc -l` (counts `\n`) vs Python `splitlines(keepends=True)` (counts elements).

**Why this matters**: W8.5 D7 LoC-trajectory formula predicts exact deletions; the off-by-one error breaks the formula's predictive confidence and forces multiple retries. The loose-assertion pattern + post-delete-count captures the formula intent without breaking on the un-trailing-newline edge case.

**Application scope**: All future peakcan-host partial-extraction refactors (W18+) and any other repo that uses Python-deletion-scripts + C# files. Sister-lesson to W8.5 D7 LoC formula.

**Awaiting sister-lesson confirmation cluster**: needs 1 more observation across any future refactor with un-trailing-newline source file to be locked into MASTER-LESSON-CATALOG. Current observation count: 3.

## Verification

- No source-code changes (vault-only PATCH).
- `dotnet build`: 0 errors expected (no changes).
- Lesson-promotion entries added to:
  - Project MEMORY.md (peakcan-host section) under the established "Lessons Cluster" index.
  - Agent-memory file at `.claude/agent-memory/lessons-confirmed/wc-l-vs-python-splitlines-3-of-3-confirmation-2026-07-12.md`.
  - Per-capture-devlog entry.

## Tasks (preview for the plan)

1. **Task 1**: Write `scripts/_vault_only_patch_wc_l_splitlines_PATCH_marker.md` capturing the 3 observations (vault-only, no Python script).
2. **Task 2**: Update `01-Projects/peakcan-host/MEMORY.md` — promote `wc-l-vs-python-splitlines-off-by-one-on-untrailing-newline-files-requires-loose-assertion` from 1/3 to CONFIRMED in any lessons catalog.
3. **Task 3**: Create `01-Projects/peakcan-host/development/capture-decisions/2026-07-12-wc-l-splitlines-3-of-3-vault-patch.md` with full lesson text + 3-observation matrix + sister-lesson relationships + application scope.
4. **Task 4**: Tier-3-ship-vault-style capture — `gh release create v3.31.1` annotation (vault-only PATCH bumps patch version, not minor, since no source change).
5. **Task 5**: Dispatch vault-pkm:pkm-capture background agents to record lesson-promotion in devlog + MEMORY + capture-decisions files.

Total: 5 tasks. Branch: `feature/vault-only-patch-wc-l-splitlines-confirmation`. PR merged to main.

## Decision log

- **D1**: vault-only PATCH (no source code change) — sister to W8.5 D7 LoC formula's earlier promotion to CONFIRMED.
- **D2**: Use v3.31.1 patch version (not minor) — convention: source-only changes warrant MINOR; doc/config-only warrant PATCH.
- **D3**: 3 confirmations are sufficient — no need for more.
- **D4**: Lesson-text format mirrors v3.5.7 D5 + W8.5 D7 promotion patterns (one-line title + multi-paragraph principle + example-confirmation matrix).
