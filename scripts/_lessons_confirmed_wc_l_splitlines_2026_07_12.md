# vault-only PATCH — wc-l-vs-python-splitlines CONFIRMED (3 observations)

**Date**: 2026-07-12
**Spec**: docs/superpowers/specs/2026-07-12-vault-only-patch-wc-l-splitlines-confirmed.md
**Plan**: docs/superpowers/plans/2026-07-12-vault-only-patch-wc-l-splitlines-confirmed.md
**Marker**: scripts/_vault_only_patch_wc_l_splitlines_PATCH_marker.md
**Status**: 3/3 → **CONFIRMED**
**Tag**: v3.31.1

## Lesson

**Title**: `wc-l-vs-python-splitlines-off-by-one-on-untrailing-newline-files-requires-loose-assertion`

**Principle**: When god-class partial-extraction deletion-script assertions compare line counts, always use **loose-assertion** (`abs(actual - expected) <= 1`) OR capture `len(lines)` post-delete as the actual count. Files with un-trailing-newline final lines will mismatch `wc -l` (counts `\n`) vs Python `splitlines(keepends=True)` (counts elements).

## 3 Confirming Observations

### Observation 1: W12 T1 (2026-07-12, commit `5fdf8c9`)

- **Refactor**: W12 UdsClient god-class partial split. `scripts/w12_task1_delete_executionlifecycleflow.py` against `src/PeakCan.Host.Core/Uds/UdsClient.cs`.
- **Symptom**: `wc -l` reported 548 → asserted `len(lines) == 548` failed because Python reported 549 (last line un-trailing-newline).
- **Fix**: Loosened assertion to `assert original_count in (548, 549)`.

### Observation 2: W13 T1 (2026-07-12, pre-script-commit)

- **Refactor**: W13 AscParser god-class partial split. `scripts/w13_task1_delete_datalineparserflow.py` against `src/PeakCan.Host.Core/Replay/AscParser.cs`.
- **Symptom**: `wc -l` reported 513 → `wc -l` showed 513 but Python splitlines reported 514.
- **Fix**: Reset the script with explicit `assert original_count in (513, 514)` + accept ±1 LoC tolerance for post-marker assertions.

### Observation 3: W15 T1 (2026-07-12, commit `85025a2`)

- **Refactor**: W15 ReplayTimeline god-class partial split. `scripts/w15_task1_delete_playbacklifecycleflow.py` against `src/PeakCan.Host.Core/Replay/ReplayTimeline.cs`.
- **Symptom**: `wc -l` reported 469 → Python splitlines 470. Initial assertion `assert original_count == 469` failed.
- **Fix**: Loose-assertion `assert original_count in (469, 470)` + post-marker `assert len(lines) in (388, 389, 390)`.

## Pattern Root Cause

3 out of 3 god-class refactors in this series that used `wc -l` count assertions hit the un-trailing-newline off-by-one bug.

The peakcan-host `.gitattributes` normalizes CRLF→LF on commit, but **does not enforce trailing newline**. C# / .NET tooling (e.g. `dotnet format`) typically writes a trailing `\n`, but **manual edits via Edit tool and some Unix-via-Git-Bash pipelines occasionally strip it**, creating the discrepancy.

## Application Scope

All future peakcan-host partial-extraction refactors (W18+) and any other repo that uses Python-deletion-scripts + C# files. **Sister-lesson to W8.5 D7 LoC formula** (already CONFIRMED with 15 transitions).

## Pattern Mature Implementation (Python)

```python
# GOOD pattern: loose assertion + capture actual post-delete count
import sys
sys.stdout.reconfigure(encoding="utf-8")

MAIN = Path(r"D:/.../some-file.cs")
content = MAIN.read_text(encoding="utf-8")
# Don't normalize trailing newlines -- just note that splitlines may differ from wc -l.
lines = content.splitlines(keepends=True)

# Pre-deletion -- accept ±1 splitlines tolerance:
assert len(lines) in (469, 470), f"Expected 469/470 LoC, got {len(lines)}"

# Mid-deletion:
del lines[start:end]

# Capture post-deletion actual count:
actual_post = len(lines)
print(f"Post-del LoC: {actual_post}")

# Post-deletion -- accept ±1 marker-tolerance:
assert len(lines) in (388, 389, 390), f"Expected 388-390 LoC, got {len(lines)}"

MAIN.write_text("".join(lines), encoding="utf-8")
```

## State Transition Log

| Date | State | Trigger |
|---|---|---|
| 2026-07-12 T1 (W12) | 1/3 candidate | First observation — ScriptEngine `len(lines)` failed |
| 2026-07-12 T1 (W13) | 2/3 candidate | Second observation — AscParser `original_count` failed |
| 2026-07-12 T1 (W15) | 3/3 candidate | Third observation — ReplayTimeline `len(lines)` post-marker |
| 2026-07-12 T1 (W17) | **CONFIRMED** | This PATCH |

## Cross-References

- Capture-decisions: `01-Projects/peakcan-host/development/capture-decisions/2026-07-12-wc-l-splitlines-3-of-3-vault-patch.md`
- Agent-memory (committed): `scripts/_lessons_confirmed_wc_l_splitlines_2026_07_12.md` (this file)
- Spec: `docs/superpowers/specs/2026-07-12-vault-only-patch-wc-l-splitlines-confirmed.md`
- Plan: `docs/superpowers/plans/2026-07-12-vault-only-patch-wc-l-splitlines-confirmed.md`
- Sister-lessons: W8.5 D7 LoC formula (CONFIRMED), W3 R3 mutable-state coupling (2/3)

## Zero-Source-Code Footprint

This PATCH bumps Directory.Build.props v3.31.0 → v3.31.1 (PATCH version, not MINOR — convention: doc/config-only = PATCH). No `.cs` files modified. No test files modified.
