# W16 Plan — ReplayViewModel god-class refactor

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mechanically split `src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs` (462 LoC) into 2 new sibling partial files + ~272 LoC main file. Zero behavioral change.

**Architecture:** Sister of W3-W15 partial-class split. **Unique**: sibling-file pattern (NOT subdirectory) because 4 existing `.partial.cs` siblings already follow it. Order: A → B.

**Tech Stack:** C# .NET 10 + CommunityToolkit.Mvvm, App layer.

**Spec:** [`../specs/2026-07-12-replay-view-model-god-class-refactor.md`](../specs/2026-07-12-replay-view-model-god-class-refactor.md)
**Branch:** `feature/w16-replay-view-model-god-class` (created from `main` @ `86f5ce5` spec commit)

## Global Constraints

- Public API unchanged (`[ObservableProperty]` properties + `[RelayCommand]` commands all preserved with identical names + types).
- partial-class visibility on private fields + private methods.
- Test coverage unchanged. No xmldoc-grep tests.
- LF line endings.
- No behavioral change.
- No version bump until Task 3.
- Outer class already `public sealed partial class ReplayViewModel` at line 40 (4 existing partials).

## LoC trajectory (W8.5 D7 14-locked + W13 T1 2/3 loose assertion)

| Task | Flow | Range (1-indexed) | LoC deleted | Markers | LoC main after |
|---|---|---|---|---|---|
| T1 | A — RangeFilter | 163-235 (~73 LoC, includes properties + IsValidRange + RangeFilterError + xmldoc) | 73 | 1 | ~390 |
| T2 | B — PlaybackEvents | 305-417 (5 handlers + Dispose, including ctor-tail-blank-line) | 113 | 1 | ~278 |
| T3 | v3.30.0 -> v3.31.0 | (no source) | 0 | 0 | ~278 |
| T4 | ship | -- | -- | -- | ~278 |

---

## Task 0: Branch + plan commit

```bash
git checkout -b feature/w16-replay-view-model-god-class main
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~Replay" --logger "console;verbosity=minimal"
git add docs/superpowers/plans/2026-07-12-replay-view-model-god-class-refactor.md
git commit -m "W16 plan: ReplayViewModel god-class refactor (2 partials + 4-task roll-out)"
```

---

## Task 1: Extract Flow A — RangeFilter.partial.cs (4 members)

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs:163-235` (delete StartTimestamp + EndTimestamp + `_rangeFilterError` backing field + `IsValidRange` + xmldoc + blank lines)
- Create: `src/PeakCan.Host.App/ViewModels/ReplayViewModel.RangeFilter.partial.cs`

**Range plan** (1-indexed): lines 163-235 (~73 LoC). The 4 members are:
- `_startTimestamp` backing field (line 163-170)
- `StartTimestamp` public property (line 171-194)
- `_endTimestamp` backing field (line 194-199)
- `EndTimestamp` public property (line 200-223)
- `IsValidRange` static helper (line 225-231)
- `_rangeFilterError` backing field (line 233-235?)

**Step 1**: Write `scripts/w16_task1_delete_rangefilter.py` with W13 T1 2/3 loose assertion. CRITICAL: verify by re-grep that the exact line range covers all 4 members without orphaning markers.

**Step 2**: Run deletion. Expected: 462 - 73 + 1 ≈ 390 LoC post-marker.

**Step 3**: Create `ReplayViewModel.RangeFilter.partial.cs` with verbatim extracted code (using modern C# file-scoped namespace + top-level partial class pattern matching the existing 4 siblings).

**Step 4**: `dotnet build src/PeakCan.Host.App/`. Expected: 0 errors. If `[ObservableProperty]` source-generator fails with the cross-partial `[ObservableProperty]` backing field in main + setter/property in Flow A pattern, fall back to: **move the `[ObservableProperty]` backing field to Flow A** (keeping the property/handler in Flow A). This is the W16 D2 R3 mitigation.

**Step 5**: Replay tests pass.

**Step 6**: Commit: `W16 Task 1: extract Flow A (RangeFilter: StartTimestamp+EndTimestamp+IsValidRange+RangeFilterError) to partial`.

---

## Task 2: Extract Flow B — PlaybackEvents.partial.cs (5 handlers + Dispose)

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs:305-417` (delete 5 handlers + Dispose, ~113 LoC)
- Create: `src/PeakCan.Host.App/ViewModels/ReplayViewModel.PlaybackEvents.partial.cs`

**Range plan**: lines 305-417. Includes:
- `OnRecentSessionsPropertyChanged` handler (311-312)
- `OnFrameEmitted` handler (320-333)
- `OnPlaybackEnded` handler (342-354)
- `ApplyPlaybackEnded` (356-364)
- `OnLoopRewound` handler (375-387)
- `Dispose` (407-417)

**Step 1**: Re-grep post-T1.

**Step 2**: Write `scripts/w16_task2_delete_playbackevents.py`. Apply R3 mitigation: if `Dispose` needs to stay in main due to subscription state, split further. (Per D6: Dispose stays with event handlers.)

**Step 3**: Create `ReplayViewModel.PlaybackEvents.partial.cs` with verbatim code.

**Step 4**: Build + test. Final main ~278 LoC.

**Step 5**: Commit: `W16 Task 2: extract Flow B (PlaybackEvents: 5 handlers + Dispose) to partial`.

---

## Task 3: Bump version v3.30.0 → v3.31.0 + release notes

Mirror W15 release notes format.

---

## Task 4: Tier-3 push + tag + GH release

Standard: `gh pr create` → `--squash --delete-branch` → `git tag v3.31.0` → `gh release create`.

---

## Acceptance Criteria

- [ ] `ReplayViewModel.cs` ≤ 350 LoC (target ~278)
- [ ] 2 NEW partial files: `RangeFilter.partial.cs` + `PlaybackEvents.partial.cs`
- [ ] 4 existing partials unchanged
- [ ] All 6 partials (4 existing + 2 new) declare `public sealed partial class ReplayViewModel`
- [ ] `dotnet build src/PeakCan.Host.App/`: 0 errors
- [ ] `dotnet test --filter "~Replay"`: count unchanged, 0 new fails (1 transient flaky expected)
- [ ] Full solution `dotnet test`: 0 new fails on 2-of-3 metric
- [ ] Tag v3.31.0 + GH release published
- [ ] Branch deleted post-merge
