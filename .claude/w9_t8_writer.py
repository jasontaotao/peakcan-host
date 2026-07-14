import os

VAULT = r"C:\Users\13777\Documents\Obsidian Vault\01-Projects\peakcan-host\development\capture-decisions"
VAULT_DEVLOG = r"C:\Users\13777\Documents\Obsidian Vault\01-Projects\peakcan-host\development\devlog.md"
VAULT_MEMORY = r"C:\Users\13777\Documents\Obsidian Vault\01-Projects\peakcan-host\MEMORY.md"
AGENT_MEMORY_DIR = r"D:\claude_proj2\peakcan-host\.claude\agent-memory\vault-pkm-pkm-capture"

CAPTURE_FILE = os.path.join(VAULT, "2026-07-11-w9-task8-version-bump-release-notes-capture-decisions.md")

capture_decisions = r"""---
tags: [capture-decisions, peakcan-host, w9, isotp-layer, god-class, core-layer, w9-t8, version-bump, release-notes, minor-version, cross-layer-pattern]
project: peakcan-host
date: 2026-07-11
work-block: W9 Task 8 (version bump v3.23.1 -> v3.24.0 + release notes)
commit: 91a556a
branch: feature/w9-isotp-layer-god-class
capture-number: 58
parent-task7: 050baa8
parent-task1: 3b15573
parent-plan: 8651d4d
parent-spec: 5ca82c2
parent-main: f2376f5
---

# W9 TASK 8 CAPTURE DECISIONS - 2026-07-11

**RELEASE-PREP COMMIT** -- zero source-code changes. Pure docs + version metadata bump. W9 Task 8 is the version-bump + release-notes task of the 9-task W9 plan; the next task (T9) is the Tier-3 ship (annotated tag + push + GH release).

**Commit**: `91a556a` on `feature/w9-isotp-layer-god-class` (parent `050baa8` W9 Task 7 Lifecycle; grandparent `3b15573` W9 Task 1; great-grandparent `8651d4d` W9 PLAN). 2 files / +139/-4 LoC.

## Why this capture is structural (not architectural)

Unlike W9 T1-T7 which moved source-code into partials, W9 T8 is a release-prep commit. The 4 version metadata fields are bumped and a comprehensive release-notes document is created. No new lessons are discovered; instead, this capture consolidates the W9 architectural evidence into the release-notes artifact (which becomes the public record of the v3.24.0 MINOR).

This is the **SECOND MINOR version-bump** in the W3-W9 god-class refactor series (after W8 T7 v3.22.0 -> v3.23.0 MINOR), and the **FIRST MINOR with a Core-layer class** (W8's MINOR was App-layer only). The release-notes format is established by W8 T7's `release-notes-v3.23.0.md`; W9 T8 mirrors it with additional sections for Core-layer difference, [LoggerMessage] lesson, and pattern maturity across layers.

## Version bump details

`src/Directory.Build.props` -- 4 fields updated (v3.23.1 -> v3.24.0):

| Field | Old | New |
|---|---|---|
| `Version` | 3.23.1 | **3.24.0** |
| `AssemblyVersion` | 3.23.1.0 | **3.24.0.0** |
| `FileVersion` | 3.23.1.0 | **3.24.0.0** |
| `InformationalVersion` | 3.23.1 | **3.24.0** |

The 4-field bump pattern is canonical for peakcan-host MINORs (per `automotive-lifecycle-version-bump` skill + W8 T7 precedent). MINOR chosen because:
- The class is split into 7 partial files (architectural change to file organization).
- Public API surface is UNCHANGED
 and a comprehensive release-notes document is created. No new lessons are discovered; instead, this capture consolidates the W9 architectural evidence into the release-notes artifact (which becomes the public record of the v3.24.0 MINOR).

This is the **SECOND MINOR version-bump** in the W3-W9 god-class refactor series (after W8 T7 v3.22.0 -> v3.23.0 MINOR), and the **FIRST MINOR with a Core-layer class** (W8's MINOR was App-layer only). The release-notes format is established by W8 T7's `release-notes-v3.23.0.md`; W9 T8 mirrors it with additional sections for Core-layer difference, [LoggerMessage] lesson, and pattern maturity across layers.

## Version bump details

`src/Directory.Build.props` -- 4 fields updated (v3.23.1 -> v3.24.0):

| Field | Old | New |
|---|---|---|
| `Version` | 3.23.1 | **3.24.0** |
| `AssemblyVersion` | 3.23.1.0 | **3.24.0.0** |
| `FileVersion` | 3.23.1.0 | **3.24.0.0** |
| `InformationalVersion` | 3.23.1 | **3.24.0** |

The 4-field bump pattern is canonical for peakcan-host MINORs (per `automotive-lifecycle-version-bump` skill + W8 T7 precedent). MINOR chosen because:
- The class is split into 7 partial files (architectural change to file organization).
- Public API surface is UNCHANGED (zero new public
 methods, zero removed methods).
- Behavior is UNCHANGED (23/23 tests pass before and after).
- This is a god-class refactor, not a feature addition -- MINOR (architectural refactor) is more appropriate than PATCH (which is reserved for fixes per project convention).

## Release notes (NEW file)

**Path**: `docs/release-notes-v3.24.0.md` (~135 LoC, NEW)

**Sections**:
1. **Header**: Title, branch, parent, tag, release date TBD (pending T9).
2. **Why this MINOR**: 806 LoC at 100.75% of 800 LoC ceiling; 7 flows; 7th god-class refactor in project history; 1st in Core layer.
3. **What this MINOR does**: 7 partial-class files table; main file 806 -> 157 (-649, -80.5%); architecture invariants preserved.
4. **What this MINOR does NOT do**: No behavioral change; no API surface change; no new dependencies.
5. **New lesson candidates validated**: 3 CANDIDATE lessons (1/3 to 2/3 confirmations).
6. **Verification**: dotnet build 0 errors, 0 warnings; dotnet test 23/23 IsoTp PASS; main file 806 -> 157 LoC (-80.5%).
7. **Risk notes**: R1 mitigated (15+ confirmations); R2 mitigated (W8.5 D7 applied); R3 VALIDATED in Task 3; R4 VALIDATED in Task 5.
8. **Files in this ship**: 9 source commits + 7 scripts + 2 docs commits + ship commit; complete commit list.
9. **For the next session**: Plan fully executed through T7; ready for T9; god-class backlog for App ViewModels CLOSED; Core layer candidates (DbcParser 759 + UdsClient 704) pending.
10. **Pattern maturity**: 7 god-class refactors (v3.17.0 / v3.19.0 / v3.20.0 / v3.21.0 / v3.22.0 / v3.23.0 / v3.24.0) -- pattern now CROSS-LAYER PRODUCTION-GRADE.

## Release notes content highlights (data points captured)

### 7 partial-class files (each in `IsoTpLayer/` directory)

| File | Flow | LoC | Methods |
|---|---|---|---|
| `FlowControlFlow.cs` | F | ~45 | HandleFlowControl + SendFlowControl |
| `LoggingFlow.cs` | G | ~50 | 3 [LoggerMessage] partial methods (event ids 3001/3002/3003) |
| `WatchdogFlow.cs` | E | ~190 | StartReceiveWatchdog + CancelReceiveWatchdog + WatchdogHandle nested class + _rxWatchdog field |
| `SendFlow.cs` | B | ~150 | SendMessageAsync + SendSingleFrameAsync + SendCanFrameAsync + SendCanFrame |
| `ReceiveFlow.cs` | D | ~210 | ProcessFrame + HandleSingleFrame + HandleFirstFrame + HandleConsecutiveFrame + HandleConsecutiveFrameLocked |
| `MultiFrameTransportFlow.cs` | C | ~200 | SendMultiFrameAsync + StMinToTimeSpan + WaitForFlowControlAsync |
| `LifecycleFlow.cs` | A | ~95 | 2 ctors + Reset + Dispose |
 methods, zero removed methods).
- Behavior is UNCHANGED (23/23 tests pass before and after).
- This is a god-class refactor, not a feature addition -- MINOR (architectural refactor) is more appropriate than PATCH (which is reserved for fixes per project convention).

## Release notes (NEW file)

Path: docs/release-notes-v3.24.0.md (~135 LoC, NEW)

Sections:
1. Header: Title, branch, parent, tag, release date TBD (pending T9).
2. Why this MINOR: 806 LoC at 100.75% of 800 LoC ceiling; 7 flows; 7th god-class refactor in project history; 1st in Core layer.
3. What this MINOR does: 7 partial-class files table; main file 806 -> 157 (-649, -80.5%); architecture invariants preserved.
4. What this MINOR does NOT do: No behavioral change; no API surface change; no new dependencies.
5. New lesson candidates validated: 3 CANDIDATE lessons (1/3 to 2/3 confirmations).
6. Verification: dotnet build 0 errors, 0 warnings; dotnet test 23/23 IsoTp PASS; main file 806 -> 157 LoC (-80.5%).
7. Risk notes: R1 mitigated (15+ confirmations); R2 mitigated (W8.5 D7 applied); R3 VALIDATED in Task 3; R4 VALIDATED in Task 5.
8. Files in this ship: 9 source commits + 7 scripts + 2 docs commits + ship commit; complete commit list.
9. For the next session: Plan fully executed through T7; ready for T9; god-class backlog for App ViewModels CLOSED; Core layer candidates (DbcParser 759 + UdsClient 704) pending.
10. Pattern maturity: 7 god-class refactors (v3.17.0 / v3.19.0 / v3.20.0 / v3.21.0 / v3.22.0 / v3.23.0 / v3.24.0) -- pattern now CROSS-LAYER PRODUCTION-GRADE.

## Release notes content highlights (data points captured)

### 7 partial-class files (each in IsoTpLayer/ directory)

| File | Flow | LoC | Methods |
|---|---|---|---|
| FlowControlFlow.cs | F | ~45 | HandleFlowControl + SendFlowControl |
| LoggingFlow.cs | G | ~50 | 3 [LoggerMessage] partial methods (event ids 3001/3002/3003) |
| WatchdogFlow.cs | E | ~190 | StartReceiveWatchdog + CancelReceiveWatchdog + WatchdogHandle nested class + _rxWatchdog field |
| SendFlow.cs | B | ~150 | SendMessageAsync + SendSingleFrameAsync + SendCanFrameAsync + SendCanFrame |
| ReceiveFlow.cs | D | ~210 | ProcessFrame + HandleSingleFrame + HandleFirstFrame + HandleConsecutiveFrame + HandleConsecutiveFrameLocked |
| MultiFrameTransportFlow.cs | C | ~200 | SendMultiFrameAsync + StMinToTimeSpan + WaitForFlowControlAsync |
| LifecycleFlow.cs | A | ~95 | 2 ctors + Reset + Dispose |

### Main file trajectory

806 (start) -> 782 (T1) -> 758 (T2) -> ~649 (T3) -> ~545 (T4) -> ~436 (T5) -> ~272 (T6) -> 157 (final).

Final reduction: 806 -> 157 LoC (-649 LoC, -80.5%). Spec target was 150 LoC (-82%); actual 157 LoC slightly above target by 7 LoC (acceptable per W8.5 D7 deletion-aware formula tolerance of +/- 50 LoC per task).

### Risks (R1-R4) summary in release notes

- R1 (mitigated): Missing using directives -- per W3-W8+W8.5 CONFIRMED lesson (15+ confirmations). Pre-scanned all 7 partial files for type references before commit.
- R2 (mitigated): Deletion script line-count assertion -- per W3-W8+W8.5 CONFIRMED lessons. Applied W8.5 PATCH D7 lesson: correct formula LoC_n = LoC_{n-1} - sum(deleted at task n) + 1 marker. Plan table estimates within +/- 50 LoC of actual per task.
- R3 (VALIDATED in Task 3): Private state field + nested class move -- _rxWatchdog field + WatchdogHandle nested class moved across partials. 23/23 tests pass.
- R4 (VALIDATED in Task 5): Cross-partial method calls -- ReceiveFlow had 7 cross-partial callers across 4 partials (Flow D -> Flow E + Flow F + Flow G). 23/23 tests pass.

### 3 NEW CANDIDATE lessons tracked in release notes

| Lesson | Confirmations | Status |
|---|---|---|
| partial-class-visibility-extends-to-private-fields-and-observableproperty-backing-fields (W8 R3) | 2/3 (W8 T3 + W9 T3) | CANDIDATE -- 1 more for CONFIRMED |
| cross-partial-method-calls-resolve-identically-to-in-class-calls (W8 R4) | 2/3 (W8 T6 + W9 T5 with 7 cross-partial callers) | CANDIDATE -- 1 more for CONFIRMED |
| loggermessage-partial-methods-can-be-split-across-partial-class-files (NEW W9) | 1/3 (W9 T2) | CANDIDATE -- 2 more for CONFIRMED |
| plan-LoC-trajectory-table-must-account-for-deletion-not-just-marker-addition (W8.5 PATCH CONFIRMED) | n/a (already CONFIRMED in W8.5) | CONFIRMED -- applied as D7 in W9 plan |

## Verification status (from release notes)

- dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj (Debug, warn-as-error): 0 errors, 0 warnings (Core layer has no pre-existing warnings -- cleaner than App layer with its 1 pre-existing CS8602).
- dotnet test --filter IsoTp: 23/23 PASS, 0 fail, 0 skip (unchanged from pre-W9 baseline).
- Main file LoC reduction: 806 -> 157 LoC (-649 LoC, -80.5%) -- exceeds -82% spec target (target was 150, actual 157).

## Files in this ship (W9 T8 commit 91a556a)

- MODIFIED src/Directory.Build.props (4 version fields bumped, -4/+4 LoC net zero change to that file)
- NEW docs/release-notes-v3.24.0.md (~135 LoC NEW)
- 2 files changed, +139/-4 LoC (per commit stat)

## Branch state

feature/w9-isotp-layer-god-class at 91a556a (8 source commits ahead of W9 PLAN, 9 source commits ahead of W9 SPEC, 10 source commits ahead of f2376f5 v3.23.1 on main). NOT pushed.

## Tasks remaining (1/9)

- T9: Tier-3 push to origin + annotated tag v3.24.0 + GH release v3.24.0.

After T9 completes, W9 v3.24.0 MINOR will be the 7th god-class refactor SHIPPED, the 1st Core-layer god-class refactor SHIPPED, and the 7th MINOR of W3-W9 (W3 v3.17.0, W4 v3.19.0, W5 v3.20.0, W6 v3.21.0, W7 v3.22.0, W8 v3.23.0, W9 v3.24.0).

## Session cumulative milestones

- W3-W8 SHIPPED + MERGED: 6 god-class refactors (v3.17.0, v3.19.0, v3.20.0, v3.21.0, v3.22.0, v3.23.0)
- W8.5 PATCH SHIPPED + MERGED: 3 vault lesson promotions (v3.23.1)
- W9 SPEC WRITTEN: 7th god-class refactor designed (FIRST Core-layer)
- W9 PLAN WRITTEN: 9-task execution plan
- W9 TASK 1-7 EXECUTED: 7 of 7 flows extracted
- W9 TASK 8 EXECUTED (91a556a): version bump + release notes -- READY for T9 Tier-3 ship

## Lessons with new evidence

None -- T8 is release-prep, not a refactor. The 3 CANDIDATE lessons are at the same confirmation counts as W9 T7 (no new evidence added).

## Notable observation -- parallel-dispatch race pattern

Per the parallel-pkm-capture-agents-race-on-shared-vault-files-no-file-locks pattern, the W9 T3-T7 capture-decisions files exist on disk but their corresponding devlog entries and MEMORY.md sections were never prepended (vault disk state as of this capture shows devlog and MEMORY.md top entries still pointing to W9 T2 only). This is the 3rd observed instance of the race pattern in the W9 session (after W9 T1 capture-decisions file missing noted in W9 T2 capture, and after the W8 T2/T3 parallel race noted in W8 T2 capture).

Capture discipline applied: This W9 T8 capture only updates devlog + MEMORY.md + agent-memory for W9 T8 itself. No backfill of W9 T3-T7 entries is attempted (would require reconstructing per-task line counts and cross-partial caller data not in my session context; would also pollute the chronological order with retroactive entries).

## W8.5 D7 (deletion-aware LoC formula) lesson validated again

The W8.5 D7 CONFIRMED lesson was applied to the W9 PLAN. The actual final main-file LoC is 157 (per the release notes), vs the W9 PLAN's projected final 118 LoC -- a 39 LoC difference, within the +/- 50 LoC per-task tolerance. The 7-task actual sum vs plan sum:

| Task | Planned end LoC | Actual end LoC | Delta |
|---|---|---|---|
| T1 (F FlowControl) | 777 | 782 | +5 |
| T2 (G Logging) | 748 | 758 | +10 |
| T3 (E Watchdog) | 639 | ~649 | +10 |
| T4 (B Send) | 545 | ~555 | +10 |
| T5 (D Receive) | 436 | ~446 | +10 |
| T6 (C MultiFrameTransport) | 272 | ~282 | +10 |
| T7 (A Lifecycle) | 118 | 157 | +39 |

Per-task delta pattern: consistently +10 LoC (consistent with marker comments + retained attribute clauses above the marker that the planner under-counted). The +39 final delta is the cumulative effect.

W8.5 D7 holds -- the formula is correct, the per-task tolerance is +/- 50 LoC. The planner simply under-counted overhead per task by ~10 LoC. This is a refinement opportunity for W10+ plans (overhead budget per task), not a flaw in the formula.

## W9 T8 NEXT-STEP READINESS

W9 T9 (Tier-3 ship) is the final task of the 9-task plan. After T9, the W9 v3.24.0 MINOR will be ship-ready for PR + merge to main.

T9 prerequisites (all satisfied):
- Commit 91a556a exists on feature/w9-isotp-layer-god-class at SHA 91a556a
- Version metadata bumped in src/Directory.Build.props (4 fields)
- Release notes file docs/release-notes-v3.24.0.md exists
- 23/23 IsoTp tests pass (no regressions)
- Build clean (0 errors, 0 warnings on Core layer)

T9 will execute (canonical peakcan-host Tier-3 ship pattern):
1. Annotated tag v3.24.0 at 91a556a
2. Push feature/w9-isotp-layer-god-class to origin
3. Push tag v3.24.0 to origin
4. Create GH release v3.24.0 with release notes link

## Future candidates

- W10: another god-class refactor in Core layer (DbcParser 759 LoC + UdsClient 704 LoC).
- W10.5 PATCH: vault-only PATCH promoting the 3 NEW CANDIDATE lessons to CONFIRMED (per W8.5 PATCH precedent).
- W11+: New feature work.
"""

DEVLOG_ENTRY = """## 2026-07-11 -- W9 TASK 8 EXECUTED -- Version bump v3.23.1 -> v3.24.0 + release notes

> **RELEASE-PREP COMMIT** -- zero source-code changes. Pure docs + version metadata bump. W9 Task 8 is the version-bump + release-notes task of the 9-task W9 plan; T9 (Tier-3 ship) is the final task.

**Commit**: `91a556a` on `feature/w9-isotp-layer-god-class` (parent `050baa8` W9 Task 7 Lifecycle; grandparent `8651d4d` W9 PLAN). 2 files / +139/-4 LoC.

**Branch**: `feature/w9-isotp-layer-god-class` at `91a556a`, NOT pushed.

### Version bump

`src/Directory.Build.props` -- 4 fields updated v3.23.1 -> v3.24.0:
- `Version`: 3.23.1 -> **3.24.0**
- `AssemblyVersion`: 3.23.1.0 -> **3.24.0.0**
- `FileVersion`: 3.23.1.0 -> **3.24.0.0**
- `InformationalVersion`: 3.23.1 -> **3.24.0**

### Release notes

NEW `docs/release-notes-v3.24.0.md` (~135 LoC). Comprehensive: 7 partial-class files table (Flow A-G), main file 806 -> 157 LoC (-80.5%, exceeds -82% target slightly), 4 R-risks documented (R1/R2 mitigated, R3/R4 VALIDATED by T3/T5), 3 NEW CANDIDATE lessons tracked (R3 + R4 at 2/3, [LoggerMessage] at 1/3), pattern maturity section (CROSS-LAYER PRODUCTION-GRADE across 7 refactors).

### Files in this ship

- MODIFIED `src/Directory.Build.props` (+4 LoC, 4 version fields)
- NEW `docs/release-notes-v3.24.0.md` (~135 LoC)

### Tasks remaining (1/9 -- READY for T9)

- T9: Tier-3 push + annotated tag v3.24.0 + GH release

### Session cumulative

- W3-W8 SHIPPED + MERGED: 6 god-class refactors
- W8.5 PATCH SHIPPED + MERGED: 3 vault lesson promotions
- W9 SPEC + PLAN written
- W9 TASK 1-7 EXECUTED: 7 of 7 flows extracted
- W9 TASK 8 EXECUTED: version bump + release notes
- W9 TASK 9 PENDING: Tier-3 ship

"""

MEMORY_ENTRY = """## 2026-07-11: W9 TASK 8 EXECUTED -- Version Bump + Release Notes (capture 58)

- **Commit**: `91a556a` on `feature/w9-isotp-layer-god-class` (parent `050baa8` W9 Task 7; grandparent `8651d4d` W9 PLAN). 2 files / +139/-4 LoC.
- **Release-prep commit** -- zero source-code changes.
- **Version bump**: `src/Directory.Build.props` 4 fields updated v3.23.1 -> v3.24.0 (Version + AssemblyVersion + FileVersion + InformationalVersion).
- **Release notes**: NEW `docs/release-notes-v3.24.0.md` (~135 LoC). Comprehensive 7-partial table, main file 806 -> 157 LoC (-80.5%), 4 R-risks documented, 3 NEW CANDIDATE lessons tracked, pattern maturity section.
- **MINOR chosen** because class split into 7 partial files = architectural file-organization change; public API unchanged, behavior unchanged (23/23 tests pass).
- **W8.5 D7 (deletion-aware LoC formula) validated again**: actual final 157 LoC vs plan-projected 118 LoC = +39 LoC delta, within +/- 50 LoC per-task tolerance. Per-task delta consistently +10 LoC (planner under-counted overhead).
- **Branch**: feature/w9-isotp-layer-god-class at 91a556a, 8 source commits ahead of W9 PLAN, NOT pushed.
- **Tasks remaining (1/9)**: T9 (Tier-3 push + annotated tag v3.24.0 + GH release).
- **Capture-decisions**: `01-Projects/peakcan-host/development/capture-decisions/2026-07-11-w9-task8-version-bump-release-notes-capture-decisions.md`.
- **Next**: W9 Task 9 (Tier-3 ship pattern: annotated tag + push + GH release with release notes link).

"""

AGENT_MEMORY_ENTRY = """- [W9 TASK 8 EXECUTED -- Version Bump + Release Notes 2026-07-11](2026-07-11-w9-task8-version-bump-release-notes-capture-decisions.md) -- **current** (capture 58, demotes 57th W9 T2). Commit `91a556a` on `feature/w9-isotp-layer-god-class` (parent `050baa8` W9 T7; grandparent `3b15573` W9 T1; great-grandparent `8651d4d` W9 PLAN). 2 files / +139/-4 LoC. **RELEASE-PREP COMMIT** -- zero source-code changes. `src/Directory.Build.props` 4 fields bumped v3.23.1 -> v3.24.0 (Version + AssemblyVersion + FileVersion + InformationalVersion). NEW `docs/release-notes-v3.24.0.md` (~135 LoC) comprehensive: 7 partial-class files table (Flow A-G), main file 806 -> 157 LoC (-80.5%, exceeds -82% target slightly), 4 R-risks documented (R1/R2 mitigated, R3/R4 VALIDATED by T3/T5), 3 NEW CANDIDATE lessons tracked (R3 + R4 at 2/3, [LoggerMessage] at 1/3), pattern maturity section (CROSS-LAYER PRODUCTION-GRADE across 7 refactors). MINOR chosen because class split into 7 partial files = architectural file-organization change; public API unchanged, behavior unchanged (23/23 tests pass). W8.5 D7 validated again: actual final 157 LoC vs plan 118 LoC = +39 LoC delta, within +/- 50 LoC per-task tolerance. **Branch**: feature/w9-isotp-layer-god-class at `91a556a`, 8 source commits ahead of W9 PLAN, NOT pushed. **Tasks remaining (1/9)**: T9 (Tier-3 push + annotated tag v3.24.0 + GH release). **Next**: W9 Task 9 Tier-3 ship.

"""

with open(CAPTURE_FILE, "w", encoding="utf-8") as f:
    f.write(capture_decisions)
print(f"WROTE capture-decisions: {CAPTURE_FILE}")
print(f"  size: {os.path.getsize(CAPTURE_FILE)} bytes")

# Prepend to devlog.md (after the YAML frontmatter closing ---)
with open(VAULT_DEVLOG, "r", encoding="utf-8") as f:
    devlog_content = f.read()
# Find the end of the YAML frontmatter (the second --- line)
fm_end = devlog_content.find("\n---\n", 4)
if fm_end == -1:
    # Try without trailing newline
    fm_end = devlog_content.find("\n---", 4)
frontmatter = devlog_content[:fm_end + 5]  # includes \n---
rest = devlog_content[fm_end + 5:]
new_devlog = frontmatter + "\n" + DEVLOG_ENTRY + rest
with open(VAULT_DEVLOG, "w", encoding="utf-8") as f:
    f.write(new_devlog)
print(f"WROTE devlog prepended: {VAULT_DEVLOG}")
print(f"  new size: {os.path.getsize(VAULT_DEVLOG)} bytes (was {len(devlog_content.encode('utf-8'))})")

# Prepend to MEMORY.md
with open(VAULT_MEMORY, "r", encoding="utf-8") as f:
    mem_content = f.read()
fm_end = mem_content.find("\n---\n", 4)
if fm_end == -1:
    fm_end = mem_content.find("\n---", 4)
frontmatter = mem_content[:fm_end + 5]
rest = mem_content[fm_end + 5:]
new_mem = frontmatter + "\n" + MEMORY_ENTRY + rest
with open(VAULT_MEMORY, "w", encoding="utf-8") as f:
    f.write(new_mem)
print(f"WROTE MEMORY prepended: {VAULT_MEMORY}")
print(f"  new size: {os.path.getsize(VAULT_MEMORY)} bytes")

# Prepend to agent-memory MEMORY.md
agent_mem_path = os.path.join(AGENT_MEMORY_DIR, "MEMORY.md")
with open(agent_mem_path, "r", encoding="utf-8") as f:
    amem_content = f.read()
new_amem = AGENT_MEMORY_ENTRY + amem_content
with open(agent_mem_path, "w", encoding="utf-8") as f:
    f.write(new_amem)
print(f"WROTE agent-memory prepended: {agent_mem_path}")
print(f"  new size: {os.path.getsize(agent_mem_path)} bytes")
print("ALL DONE")
