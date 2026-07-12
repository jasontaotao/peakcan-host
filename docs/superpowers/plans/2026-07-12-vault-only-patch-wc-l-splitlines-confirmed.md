# W17 Plan — vault-only PATCH: wc-l-vs-python-splitlines → CONFIRMED

> **For agentic workers:** Single vault-only PATCH cycle. Zero source-code changes.

## Goal

Promote `wc-l-vs-python-splitlines-off-by-one-on-untrailing-newline-files-requires-loose-assertion` from 3/3 to CONFIRMED. Document + tier-3-ship-vault.

## Steps

### Step 1: Write marker .md

Create `scripts/_vault_only_patch_wc_l_splitlines_PATCH_marker.md` (vault-only):

```
# vault-only PATCH marker — wc-l-vs-python-splitlines → CONFIRMED (3 observations)

**Date**: 2026-07-12
**Spec**: docs/superpowers/specs/2026-07-12-vault-only-patch-wc-l-splitlines-confirmed.md
**Lesson ID**: wc-l-vs-python-splitlines-off-by-one-on-untrailing-newline-files-requires-loose-assertion
**Status**: 3/3 → CONFIRMED
**Branch**: feature/vault-only-patch-wc-l-splitlines-confirmation
**Tag**: v3.31.1

## 3 Confirming Observations

| # | Date | Commit | File | Fix pattern |
|---|---|---|---|---|
| 1 | 2026-07-12 | `5fdf8c9` (W12 T1) | `src/PeakCan.Host.App/Services/Scripting/ScriptEngine.cs` prior | loosened assertion `551 < 552` |
| 2 | 2026-07-12 | (W13 T1) | `src/PeakCan.Host.Core/Replay/AscParser.cs` | loose-assertion pattern applied |
| 3 | 2026-07-12 | `85025a2` (W15 T1) | `src/PeakCan.Host.Core/Replay/ReplayTimeline.cs` | post-delete `len(lines)` captured as actual |

## Lesson PRINCIPLE

When god-class partial-extraction deletion-script assertions compare line counts, use loose-assertion (`abs(actual - expected) <= 1`) OR capture `len(lines)` post-delete as actual. Files with un-trailing-newline final lines will mismatch `wc -l` (counts `\n`) vs Python `splitlines(keepends=True)` (counts elements).

## Application scope

All future peakcan-host partial-extraction refactors (W18+) and any other repo that uses Python-deletion-scripts + C# files. Sister-lesson to W8.5 D7 LoC formula (already CONFIRMED).
```

### Step 2: Update vault

#### 2a: MEMORY.md

Append lesson promotion line at peakcan-host section.

#### 2b: Capture-decisions file

Create `01-Projects/peakcan-host/development/capture-decisions/2026-07-12-wc-l-splitlines-3-of-3-vault-patch.md` with full lesson text + 3-observation matrix + sister-lesson relationships + scope.

#### 2c: Agent-memory file

Create `.claude/agent-memory/lessons-confirmed/wc-l-vs-python-splitlines-confirmed-2026-07-12.md` with the canonical CONFIRMED lesson record.

### Step 3: Bump version + PR + tier-3-ship-vault

- Update `src/Directory.Build.props`: v3.31.0 → v3.31.1 (PATCH).
- Commit + push + PR + squash-merge + tag v3.31.1.
- NO GH release (vault-only PATCH — pure doc-only).

### Step 4: Captures

Dispatch 3 vault-pkm:pkm-capture agents:
- W17 T1 (marker file shipped)
- W17 T2 (vault updates shipped)
- W17 final SHIP capture (vault-only PATCH closed)

## Acceptance Criteria

- [ ] Marker file `scripts/_vault_only_patch_wc_l_splitlines_PATCH_marker.md` exists
- [ ] MEMORY.md has new lesson-promotion line at peakcan-host section
- [ ] Capture-decisions file at `01-Projects/peakcan-host/development/capture-decisions/2026-07-12-wc-l-splitlines-3-of-3-vault-patch.md` exists
- [ ] Agent-memory file at `.claude/agent-memory/lessons-confirmed/wc-l-vs-python-splitlines-confirmed-2026-07-12.md` exists
- [ ] Directory.Build.props = v3.31.1
- [ ] Tag v3.31.1 on merge commit
- [ ] Branch `feature/vault-only-patch-wc-l-splitlines-confirmation` deleted post-merge
- [ ] No source-code changes (zero, vault-only)
