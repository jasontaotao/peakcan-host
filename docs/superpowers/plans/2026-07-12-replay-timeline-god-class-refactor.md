# W15 Plan — ReplayTimeline god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mechanically split `src/PeakCan.Host.Core/Replay/ReplayTimeline.cs` (469 LoC) into 2 partial-class files + ~250 LoC main file. Zero behavioral change.

**Architecture:** Sister pattern to W14 ScriptEngine. Order: A (PlaybackLifecycle, 95 LoC) → B (OnTick, 178 LoC).

**Tech Stack:** C# .NET 10, Core layer.

**Spec:** [`../specs/2026-07-12-replay-timeline-god-class-refactor.md`](../specs/2026-07-12-replay-timeline-god-class-refactor.md)
**Branch:** `feature/w15-replay-timeline-god-class` (created from `main` @ `96d2458` spec commit)

## Global Constraints (carried verbatim from spec)

- API unchanged (internal sealed visibility preserved).
- partial-class visibility on private fields + private methods.
- Test coverage unchanged. No xmldoc-grep tests for ReplayTimeline.
- LF line endings.
- No behavioral change.
- No version bump until Task 3. Tasks 1-2 keep `Directory.Build.props` at v3.29.0.
- Outer class already `internal sealed partial class ReplayTimeline` at line 13.

## LoC trajectory table (per W8.5 D7 CONFIRMED + W13 T1 2/3 loose-assertion)

| Task | Flow | Range (1-indexed) | LoC deleted | Markers | LoC main after |
|---|---|---|---|---|---|
| T1 | A — PlaybackLifecycle | 143-249 (Play 143-165 + Pause 167-174 + Seek 176-190 + SetSpeed 192-222 + Stop 224-236 + PlayedTimestamp 238-249) | ~107 | 1 | ~363 |
| T2 | B — OnTick | 251-429 | 179 | 1 | ~185 |
| T3 | version bump + release notes | (no source LoC) | 0 | 0 | ~185 |
| **T4** | ship | -- | -- | -- | **~185** |

Cumulative: 469 -> ~363 -> ~185 main (range-based; precise bounds after T1 confirm via grep).

---

## Task 0: Branch + plan commit

```bash
git checkout -b feature/w15-replay-timeline-god-class main
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~Replay" --logger "console;verbosity=minimal"
git add docs/superpowers/plans/2026-07-12-replay-timeline-god-class-refactor.md
git commit -m "W15 plan: ReplayTimeline god-class refactor (2 partials + 4-task roll-out)"
```

---

## Task 1: Extract Flow A — PlaybackLifecycleFlow (~95 LoC, 6 methods)

**Files:**
- Modify: `src/PeakCan.Host.Core/Replay/ReplayTimeline.cs:143-249` (delete 6 methods + xmldocs + blank-line separators)
- Create: `src/PeakCan.Host.Core/Replay/ReplayTimeline/PlaybackLifecycleFlow.cs`

**Step 1**: Write `scripts/w15_task1_delete_playbacklifecycleflow.py` with **W13 T1 2/3 loose assertion** (±1 LoC tolerance).

**Step 2**: Run deletion. Expected: 469 - 107 + 1 ≈ 363 LoC post-marker.

**Step 3**: Create `PlaybackLifecycleFlow.cs` with verbatim extracted code (Play + Pause + Seek + SetSpeed + Stop + PlayedTimestamp — 6 methods).

**Step 4**: `dotnet build` + Replay tests pass.

**Step 5**: Commit: `W15 Task 1: extract Flow A (PlaybackLifecycle: Play+Pause+Seek+SetSpeed+Stop+PlayedTimestamp) to partial`.

---

## Task 2: Extract Flow B — OnTickFlow (single 178 LoC method)

**Files:**
- Modify: `src/PeakCan.Host.Core/Replay/ReplayTimeline.cs:251-429` (delete OnTick only)
- Create: `src/PeakCan.Host.Core/Replay/ReplayTimeline/OnTickFlow.cs`

**Step 1**: Re-grep post-T1 ranges. (Line numbers shift.)

**Step 2**: Write deletion script (W13 T1 loose assertion). Delete one contiguous range.

**Step 3**: Create `OnTickFlow.cs` with verbatim OnTick (178 LoC, stays inline per W14 D8 + W12 D7 sister).

**Step 4**: Final build + full solution test. Commit.

---

## Task 3: Bump version v3.29.0 → v3.30.0 + release notes

Mirror W13/W14 release notes format.

---

## Task 4: Tier-3 push + tag + GH release

Standard: `gh pr create` → `--squash --delete-branch` → `git tag v3.30.0` → `gh release create`.

---

## Acceptance Criteria

- [ ] `ReplayTimeline.cs` ≤ 250 LoC
- [ ] 2 partial files in `ReplayTimeline/` directory
- [ ] Outer class stays `internal sealed partial class ReplayTimeline`
- [ ] `dotnet build src/PeakCan.Host.Core/`: 0 errors
- [ ] `dotnet test --filter "~Replay"`: count unchanged, 0 new fails
- [ ] Full solution: 0 new fails
- [ ] Tag v3.30.0 + GH release published
- [ ] Branch deleted post-merge
- [ ] MEMORY.md updated
