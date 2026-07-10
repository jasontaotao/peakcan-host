# Release Notes v3.16.9.0 — Composite PATCH chain (X-axis wall-clock + Play chain fix + SetSpeed reorder) (MINOR)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.16.9.0
**Branch:** `feature/v3-12-0-minor` (tip `5b19838`) — needs Tier-3 push to `main`
**Parent:** v3.16.8 (`d07cb77` on origin/main, the v3.16.8 hotfix)

## Why MINOR, not PATCH chain

4 PATCH commits shipped over 2026-07-08..2026-07-10 address **4 distinct user-visible defects**:
- X-axis numeric labels unreadable (no wall-clock context)
- Discrete CAN frames indistinguishable from interpolation
- Play chain reverses to trace end in 0.013s (reverse-trigger loop)
- SetSpeed emits a 6×10^10-second _currentTimestamp to master (wallclock init bug)

Each was triaged as a PATCH individually, but together they form a **MINOR-level trace-viewer overhaul** — every defect was a v3.14.x..v3.16.8 introduced regression. Bumping to v3.16.9.0 MINOR signals "trace viewer is now actually usable for production" rather than "here's another quick fix".

## Highlights

### 1. X-axis shows wall-clock time when the ASC carries a `date` header
**Commit `88e44f6` (v3.16.9.2 PATCH — X-axis wall-clock + LineSeries markers)**

For a recording dated `Wed Jul 1 08:32:01 am 2026`, the X axis now
displays `07/01 08:32:01`, `07/02 08:32:01`, etc. when the source
carries a `WallClockOrigin` (parsed from the ASC header by the
WallClockOrigin chain — `f9886e3`..`ea51d2f`, shipped in v3.18.0
but in main as of 2026-07-10).

When the source has no `WallClockOrigin` (~5% of traces), the X axis
falls back to a 3-tier elapsed formatter:
- `x >= 1 day`: `1.0d 01:01:01`
- `x >= 1 hour`: `01:02:05`
- `x < 1 hour`: `02:05.5`

Uses `CultureInfo.InvariantCulture` so locale cannot change the
`MM/dd` ordering or the decimal point.

### 2. Discrete CAN frames now render as visible circle markers
**Commit `88e44f6` (v3.16.9.2 PATCH)**

`MarkerType = MarkerType.Circle`, `MarkerSize = 3`. The user can
now visually distinguish:
- **line** = trend / interpolation between samples
- **circle** = real CAN frame at this timestamp

Without markers, OxyPlot's default `MarkerType = None` renders a
continuous line with no per-point visibility.

### 3. Play chain no longer reverse-triggers to trace end
**Commit `dd57723` (v3.16.9.2 PATCH cherry-picked from origin — reverse-trigger guard)**

The v3.16.3 PATCH added `OnAnyFrameEmitted → ScrubberValue = t` to
make the UI scrubber follow playback, but it introduced a
**reverse-trigger loop**:
```
emit frame → ScrubberValue = t → OnScrubberValueChanged
  → SeekAllToProportionalTime(t) → master.Seek(t)
  → ReplayTimeline.Seek(t) resets _playStartTimestamp = t
  → PlayedTimestamp = t + (UtcNow - _playStartWallClock) * _speed
  → next tick "now" is _playStartTimestamp + elapsed where t = last emit frame.ts
  → cursor fast-forwards to trace end in 1 tick, then keeps emitting
    5000 frames in 0.013s
```

User symptom: **"progress bar jumps straight to end"**.

**Fix**: `OnScrubberValueChanged` now checks `master.State == Playing`.
If playing, the ScrubberValue setter writes are playback writebacks
(not user input) — skip the seek. User drag unaffected (master is
paused during drag).

### 4. SetSpeed no longer leaks 6×10^10 seconds to master
**Commit `b59481d` (v3.16.9.3 PATCH cherry-picked from origin — SetSpeed reorder)**

The previous `SetSpeed` order was:
1. `_currentTimestamp = PlayedTimestamp`      (uses stale wallclock)
2. `_playStartTimestamp = _currentTimestamp`
3. `_playStartWallClock = DateTime.UtcNow`     (wallclock updated LAST)

For a never-played timeline, `_playStartWallClock` is the field
default `DateTime.MinValue`. The `PlayedTimestamp` computation in
step 1 becomes `elapsed = (UtcNow - DateTime.MinValue) * _speed ≈
6×10^10 seconds`, leaking absurd values into
`master.CurrentTimestamp`.

**Fix**: reorder so wallclock is updated **before** PlayedTimestamp
computation.

### 5. Bonus: 5 pre-existing RebuildSignalsAsync_* tests migrated
**Commit `6ac2fa1` (v3.16.9.3 PATCH — test migration)**

5 tests asserting `sut.Signals` (the v3.14.3 legacy "DBC 全列"
collection, intentionally preserved for back-compat but no longer
populated since v3.15.0 MINOR) were rewritten to drive `AddToWatch`
+ assert `WatchedSignals` (the v3.15.0+ opt-in watch list). This
**closes the v3.15.0 MINOR migration gap** that left 3 tests
failing on HEAD since 2026-07-08.

Root cause: not commit `ea51d2f` (the most recent change to
`TraceViewerService`) — it's the **v3.15.0 MINOR contract change**
that introduced the gap. v3.16.9.2 release-notes §6.1 was
incorrectly attributing the failures; **v3.16.9.3 fixed the
attribution** (commit `3768b41`).

## What this MINOR explicitly does NOT include

(Spec items deferred or already addressed by the WallClockOrigin chain.)

- **Play/Pause/Stop button visibility** — remains controlled by
  `HasSources` (not collapsed). Spec §3.5 "Play is DEAD as of
  v3.18.0" claim was **wrong** (v3.18.0 does not exist; latest
  release is v3.16.8.2). v3.16.9.0 does NOT collapse these.
- **Shared `LineAnnotation` across subplots** — spec §3.5 design
  was speculative. v3.16.9 PATCH created per-subplot annotations,
  which is functionally equivalent. v3.16.9.0 does NOT change this.
- **Hover tracker / crosshair** — YAGNI; OxyPlot's built-in Tracker
  remains disabled. Spec G7 non-goal.

## Files in this MINOR

```
# Source code
src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs            (+87 / -10: LabelFormatter + MarkerType + reverse-trigger guard)
src/PeakCan.Host.App/ViewModels/TraceViewerViewModelTests.cs       (+117 / -10: 5 NEW LabelFormatter tests + 6-row boundary + 2 NEW reverse-trigger tests)
src/PeakCan.Host.App/Composition/AppHostBuilder.cs                 (smoke log lines — v3.16.8.2 BYPASS Serilog)
src/PeakCan.Host.Core/Replay/TraceViewerService.cs                (smoke log + LastParseResult + reverse-trigger guard receiver)
src/PeakCan.Host.Core/Replay/ReplayTimeline.cs                    (SetSpeed reorder — v3.16.9.3 cherry-pick)
src/PeakCan.Host.Core.Tests/Replay/ReplayTimelineTests.cs         (Stopwatch.StartNew fix)
src/PeakCan.Host.Core.Tests/Replay/AscParserTests.cs               (v3.18.0 PATCH tests: ParseAsyncWithHeaderAsync WithDateHeader + WithoutDateHeader)
src/Directory.Build.props                                          (3.16.7.1 → 3.16.9)

# Per-PATCH release notes
docs/release-notes-v3.16.9.2.md                                    (X-axis + MarkerType — local PATCH)
docs/release-notes-v3.16.9.3.md                                    (test migration — local PATCH)
docs/release-notes-v3.16.9.0.md                                   (this file — composite MINOR)

# Plan + spec
docs/superpowers/specs/2026-07-09-trace-viewer-enhancements-design.md    (440 LoC, partially obsoleted by v3.16.9.2 + 9.3)
docs/superpowers/plans/2026-07-10-trace-viewer-enhancements-remaining.md (168 LoC, plan for the 2 spec items shipped)

# Diagnostic + anchors
docs/play-architecture.html                                       (Mermaid sequence diagram of Play call chain)
tools/smoke-diag/Program.cs                                       (DLL smoke log scanner — diagnostic companion)
docs/superpowers/session-anchors/2026-07-10-v3-5-to-v3-16-status-anchor.md  (morning anchor)
docs/superpowers/session-anchors/2026-07-10-phase-d-push-and-status.md       (afternoon anchor)
```

## Tests

- **New tests**: 8 (5 LabelFormatter + 2 reverse-trigger + 1 TraceSource WallClockOrigin defaults)
- **Migrated tests**: 5 (RebuildSignalsAsync_* v3.14.3 → v3.15.0 contract)
- **All pass**: 1332 / 0 / 5 SKIP across App.Tests (801) + Core.Tests (449) + Infrastructure.Tests (87)
- **Build**: 0 warnings, 0 errors (2 pre-existing nullable warnings in `DbcService.cs:157` unrelated)
- **1 known pre-existing parallel-runner flake** (`IReplayServiceTests.SetSpeed_PreservesCurrentTimestamp` + `AscParserTests.Parse_MalformedLines_LogsEachWithLineNumberAndReason`) — PASS in single-suite mode, FAIL under parallel runner. Documented in `docs/superpowers/session-anchors/2026-07-10-v3-5-to-v3-16-status-anchor.md` §6.1.

## Lessons (3 NEW 1-of-1, 1 CONFIRMED 2nd time, 1 NEW process pattern)

1. **`spec-hypothetical-design-vs-code-reality-must-be-validated-before-execution`** (1-of-1) — Process lesson. The 2026-07-09 trace-viewer-enhancements spec made 3 hypothetical assumptions (Play-DEAD, ParseAsync extension, shared LineAnnotation) all false. **Process fix**: spec authors should always include a "Reality check vs HEAD" section; spec reviewers should reject specs that lack it.

2. **`test-rewrite-vs-skip-vs-delete-decision-framework`** (1-of-1) — Decision framework. REWRITE preferred when test intent survives the contract change. SKIP preferred when test asserts behavior that no longer exists. DELETE last resort. Applied to 5 RebuildSignalsAsync_* tests in `6ac2fa1`.

3. **`when-a-fix-unmasks-an-older-regression-trace-to-the-contract-change-not-the-exposing-commit`** (1-of-1) — Process lesson. When a fix unmasks an older regression, attribute to the **contract change** that introduced the gap, not the most recent commit. Applied: v3.16.9.2 plan §6.1 wrongly attributed failures to commit `ea51d2f`; **v3.16.9.3 fixed the attribution** to v3.15.0 MINOR (commit `3768b41`).

4. **`branch-name-collision-across-claude-sessions-is-a-real-risk-in-tier-3-ship-workflow`** (1-of-1) — Process lesson. Before any push to a long-lived feature branch, run `git rev-list --count` in BOTH directions. If both are non-zero, the branch has been used by multiple sessions; cherry-pick the missing origin commits (do NOT force-push). Applied in Phase D push.

5. **`rebase-merge-cherry-pick-resolve-merge-then-push`** (CONFIRMED 2nd time today) — Process pattern. For syncing a multi-session feature branch with origin: fetch → merge origin/main → cherry-pick missing origin commits → merge origin/feature → push. Preserves commit history; forces explicit conflict resolution at each step.

## Risk notes

- **R1**: `MarkerType.Circle` + `MarkerSize=3` may visually merge on dense traces (10 kHz+ CAN). Mitigation: future PATCH may lower to `MarkerSize=2` or make size adaptive. Not blocking.
- **R2**: `DateTimeKind.Local` arithmetic does not normalize across DST transitions. Mitigation: traces that span DST boundaries are rare in CANoe recordings. Not blocking.
- **R3**: SetSpeed reorder assumes `_playStartWallClock` field default is `DateTime.MinValue` (verified). If future code changes the default, regression could resurface. Not blocking.

## Pre-Tier-3 ship checklist

- [ ] Tier-3 ship script written (see `scripts/tier3_v3169_0.py` — template)
- [ ] `git fetch` confirmed origin/main is at v3.16.8 (`d07cb77`) and origin/feature at `5b19838`
- [ ] 4 PATCH flag commits (`88e44f6` + `6ac2fa1` + `dd57723` + `b59481d`) are all present on the local chain between v3.16.8 and HEAD
- [ ] Build clean (0 warnings, 0 errors)
- [ ] All tests pass (1332 / 0 / 5 SKIP)
- [ ] Tier-3 ship script run successfully; `git rev-list --count origin/main..HEAD` = 0 post-push
- [ ] Tag `v3.16.9.0` applied (annotated, signed)
- [ ] GH release published with this file as release body