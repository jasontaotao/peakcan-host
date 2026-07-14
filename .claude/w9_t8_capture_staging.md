---
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
- Public API surface is UNCHANGED (zero new public methods, zero removed methods).
- Behavior is UNCHANGED (23/23 tests pass before and after).
- This is a god-class refactor, not a feature addition -- MINOR (architectural refactor) is more appropriate than PATCH (which is reserved for fixes per project convention).

## Release notes (NEW file)

**Path**: `docs/release-notes-v3.24.0.md` (~135 LoC, NEW)

**Sections**:
1. **Header**: Title, branch, parent, tag, release date TBD (pending T9).
2. **Why this MINOR**: 806 LoC at 100.75% of 800 LoC ceiling; 7 flows; 7th god-class refactor in project history; 1st in Core layer.
3. **What this MINOR does**: 7 partial-class files table (file/flow/LoC/methods); main file 806 -> 157 (-649, -80.5%) -- exceeds -82% target slightly; architecture invariants preserved.
4. **What this MINOR does NOT do**: No behavioral change; no API surface change; no new dependencies.
5. **New lesson candidates validated**: 3 CANDIDATE lessons (1/3 to 2/3 confirmations).
6. **Verification**: dotnet build 0 errors, 0 warnings; dotnet test 23/23 IsoTp PASS; main file 806 -> 157 LoC (-80.5%).
7. **Risk notes**: R1 mitigated (15+ confirmations); R2 mitigated (W8.5 D7 applied); R3 VALIDATED in Task 3; R4 VALIDATED in Task 5.
8. **Files in this ship**: 9 source commits + 7 scripts + 2 docs commits + ship commit; complete commit list.
9. **For the next session**: Plan fully executed through T7; ready for T9; god-class backlog for App `ViewModels/` CLOSED; Core layer candidates (DbcParser 759 + UdsClient 704) pending.
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
| `MultiFrameTransportFlow.cs` | C | ~200 | Send
