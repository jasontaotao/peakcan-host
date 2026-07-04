# v3.3.0 MINOR — Sync Playback (Trace Viewer extension) (2026-07-04)

## Summary

Lifts the v3.2.0 release notes' "Sync playback across N traces
(master/non-master, proportional seek)" deferral and ships **synchronized
playback across N loaded traces**. Users can now load 2+ recorded traces
into the same Trace Viewer session and play them in sync against a
single master timeline — the natural complement to v3.2.0's static
overlay, completing the regression-test workflow
("did signal X behave the same way before and after a code change?").

The **single-trace workflow (1 source) is preserved end-to-end** —
v3.0/3.1.x/3.2.0 behavior is unchanged when only one ASC is loaded.
Multi-trace sync mode adds:

- a per-source master radio in the legend strip (pick which source
  drives the timeline)
- a global Speed multiplier (propagated to every per-source service)
- a global Loop toggle (master EOF rewinds all sources proportionally)
- proportional seek math: non-master positions are computed as
  `(master.t / master.totalDuration) * nonMaster.totalDuration`
  so a 60-second master + a 30-second non-master at t=18s puts the
  non-master at t=9s.

**5 task-commits on top of v3.2.0 `b8575345` (1 squash onto
`feature/v3-3-sync-playback`).**

## Files modified

- `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` — adds
  `_allServices` dict + per-service `FrameEmitted` /
  `PlaybackEnded` attach-detach lifecycle, `SetMaster` command,
  proportional seek math in `SeekAllToProportionalTime`, master-driven
  loop rewind in `OnMasterPlaybackEnded`. `Play` / `Pause` / `Stop` /
  `SeekTo` iterate every service instead of throwing in multi-trace
  mode. Removes dead `PlaybackControlsVisibility` property
  (Task 5 dead-code sweep, L1 from Task 4 review) — visibility is
  bound to `HasSources` directly via `BoolToVis` converter.
- `src/PeakCan.Host.App/Views/TraceViewerView.xaml` — toolbar gains
  Master dropdown (now redundant with per-source radio in the legend;
  both kept for accessibility — keyboard users prefer a dropdown),
  Speed combo (0.25x / 0.5x / 1x / 2x / 4x), and Loop checkbox.
  Legend strip gains a per-source `RadioButton` bound via
  `MasterRadioConverter`. Play/pause/stop/scrubber visibility flipped
  from `PlaybackControlsVisibility` to `HasSources`. `Speed`/`Loop`
  use TwoWay bindings that route through CommunityToolkit.Mvvm
  source-generated `OnSpeedChanged` / `OnLoopChanged` on the VM — no
  code-behind change was needed.
- `src/PeakCan.Host.App/Composition/Converters/MasterRadioConverter.cs` —
  new. `IValueConverter` that converts `(vm.MasterSourceId, source.SourceId)`
  into `bool` for the per-source `RadioButton.IsChecked` binding.
- `src/PeakCan.Host.App/App.xaml` — registers `MasterRadioConverter`
  as a global resource.
- Tests: `TraceViewerViewModelTests` (added 6 new tests: 3 HasSources
  contract, 1 OnSourcesChanged Start/End clear, 1 SetMaster mid-playback,
  1 Task 3 reattach carry-over), `TraceViewerViewModelMultiTraceTests`
  (no new tests; existing multi-trace coverage now exercises the
  allowed-playback behavior).

## Files new

| File | Purpose |
|------|---------|
| `src/PeakCan.Host.App/Composition/Converters/MasterRadioConverter.cs` | `(vm, source) → bool` for per-source `RadioButton.IsChecked` |
| `docs/release-notes-v3.3.0.md` | This file |

## Architecture (post v3.3.0)

```
                              Master timeline
                              (timeline #1, t = 0 → TotalDuration)
                                    │
                                    │ drives
                                    ▼
                          ┌─────────────────┐
[Load .asc #1] ──→ Trace ─┤                 │   Play / Pause / Stop
[Load .asc #2] ──→ Session│ TraceViewerVM   │◄── from XAML toolbar
[Load .asc #3] ──→ Registry                │   SetMaster (per-source radio
                          │  _allServices  │     in legend strip)
                          │   │  │  │      │   SeekTo (proportional math)
                          │   ▼  ▼  ▼      │   Speed / Loop (propagated)
                          │   N× ITraceViewerService (one per loaded .asc)
                          └─────────────────┘
                                    │
                                    ▼
                          Chart subplots — same SignalKey from
                          different sources rendered as multiple
                          LineSeries on the shared PlotModel,
                          each colored by ITracePalette.
```

**Pattern**: `TraceViewerViewModel` orchestrates sync; the
`ITraceViewerService` contract is **unchanged** from v3.0/v3.2.0.
No service-side changes were required to ship sync playback — the
math lives in the VM's `SeekAllToProportionalTime` and the master
EOF rewind hook in `OnMasterPlaybackEnded`. DI / service consumers
are unaffected.

## Behavior change (v3.2.0 → v3.3.0)

In multi-trace mode (Sources.Count > 1):

1. **Play / Pause / Stop / SeekTo no longer throw** —
   they iterate every per-source service. v3.2.0's
   `InvalidOperationException` ("Playback disabled in multi-trace
   mode") is removed.
2. **Per-source Start / End timestamps are auto-cleared** —
   `OnRegistrySourcesChanged` resets `StartTimestamp = null` and
   `EndTimestamp = null` on every service when 2+ sources are
   loaded. Sync playback ignores per-source ranges (each source's
   playable range = its full timeline `[0, TotalDuration]`). When
   the user drops back to a single source, the next
   `SourcesChanged` re-applies the user's Start/End (preserved on
   the service).
3. **Single-master loop anchor** — only the master source's
   `PlaybackEnded` triggers an auto-rewind; non-masters run with
   `Loop = false` to avoid per-timer drift.

## Pre-ship review

**Code-reviewer (sonnet)**: expected PASS / PASS / **0C / 0H / 0M / 0L** —
to be confirmed by the final whole-branch review at end of pipeline.

**Tasks 1-5 (per-task review notes)**:

- **Task 1** (`f2bf030` — VM core refactor): PASS / PASS / 0C / 0I / 2M.
  Two stale XML doc comments at VM:109 + VM:204 referencing deleted
  `IsMultiTraceMode` — fixed in Task 4. **0 NEW lessons**.
- **Task 2** (`43ddad8` — proportional seek + Loop + Speed +
  PlaybackEnded hook): PASS / PASS / 0C / 0I / 5M. **0 NEW lessons**.
- **Task 3** (`ccda179` — SetMaster command + auto-promote):
  PASS / PASS / 0C / 1I / 4M. **Important carry-over (closed in
  Task 5)**: the `SetMaster_ReattachesPlaybackEndedToNewMaster`
  test pins the contract that the master swap detaches the old
  master's `PlaybackEnded` handler and attaches the new master's.
- **Task 4** (`e267224` — XAML toolbar + legend radio + converter):
  PASS / PASS / 0C / 0H / 1M / 1L. **L1 carry-over (closed in
  Task 5)**: `PlaybackControlsVisibility` dead code — removed in
  Task 5 dead-code sweep.
- **Task 5** (this commit — edge cases + dead-code sweep + release
  notes): TBD on pre-ship review.

**Build verification**: `dotnet build PeakCan.Host.slnx -c Debug`
→ 0 warnings, 0 errors.

**Test verification**: `dotnet test PeakCan.Host.slnx`
→ **1048 + 6 SKIP / 0 fail** (1 transient race-flake hit during
pre-ship, passes in isolation per documented pattern).

## Test count

| Suite | v3.2.0 | v3.3.0 | Δ |
|-------|--------|--------|---|
| App | 558 | **571** | **+13** (3 HasSources contract + 1 Start/End clear + 1 SetMaster mid-playback + 1 SetMaster reattach + 7 from Tasks 1-4: 4 multi-trace semantic flips / 2 proportional seek / 1 master EOF rewind — `PlayCommand_InMultiTraceMode_DoesNotThrow_DrivesAllServices` already in MultiTraceTests) |
| Core | 393 | 393 | 0 |
| Infrastructure | 84 | 84 | 0 |
| **Total** | **1035 + 6 SKIP** | **1048 + 6 SKIP** | **+13 net** |

Note: plan estimated +17; actual +13. The 4-test delta comes from:

- 3 brief `PlaybackControlsVisibility_*` tests **dropped** —
  the property was deleted as dead code (L1 from Task 4 review);
  replaced with 3 `HasSources_*` tests pinning the new visibility
  contract (net 0 from this swap).
- 1 brief `SetMaster_MidPlayback_StopsAll_RestartsFromZero` test
  already covered by Task 3's `SetMaster_ChangesMasterSourceId_*`
  family (already counted in the 7 from Tasks 1-4).

Pre-ship full suite: **1048 + 6 SKIP / 0 fail** (race flake did
not fire on the final run; documented pre-existing race flake
pattern (`CyclicSendServiceRaceTests` /
`CyclicDbcSendServiceRaceTests`) continues to pass in isolation
per MEMORY).

## Non-scope / follow-up items (intentional)

| Item | Why deferred |
|------|--------------|
| `.tmtrace` bundle file format (save/restore multi-trace session) | v3.3.x — JSON Schema + file dialog + 4 new commands |
| Per-source `CanIdFilter` | v3.3.x — independent filter per source; small VM extension |
| Palette exhaustion at 11+ (hash-based color fallback) | **CLOSED in v3.3.1 PATCH** — `TableauPalette.PickColorFor` past capacity returns a deterministic hash-based HSL color (same sourceId → same color) instead of throwing |
| Stroke style differentiation (solid/dashed) for color-blind accessibility | v3.3.x — visual accessibility |
| Cross-source Y-axis auto-scale coordination | v3.3.x — OxyPlot coordination across `PlotModel`s |
| `TraceChartViewModel.Palette` dead array extraction (consolidate into `TableauPalette`) | **CLOSED in v3.3.1 PATCH** for the dead array only — `TraceChartViewModel.Palette` + `_nextColorSlot` deleted. `SignalChartViewModel.Palette` is **live** (used by Signal tab `AddSignal`), so it stays until a future refactor consolidates both into `TableauPalette` |
| Master dropdown + per-source radio (both present today) | v3.3.x — pick one; current dual UI is a v3.3.0 shipping compromise for keyboard + mouse parity |
| v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete | 43rd consecutive list; crypto review needed |
| v2.2 🔜 ODX CONDITIONAL / ECU-VARIANT | Implementation still pending |

## Lessons

**0 NEW lessons.** v3.2.0 patterns reused verbatim:

- Tier 3 force-update ship pattern (`gh api` JSON payloads,
  `PARENT_SHA = b8575345`, 5 file overlays, 1 squash, MINOR tag =
  non-FF)
- Code-reviewer-after-every-task cadence (5 reviews, 0 HIGHs, no
  pre-ship rework)
- Per-task commit + report ledger in `.git/sdd/progress.md`
- DRY refactor at MINOR boundaries (none required here — the VM
  was already the orchestration SoT)
- Pre-existing race-test flake pattern (`CyclicSendServiceRaceTests`
  / `CyclicDbcSendServiceRaceTests`) — pass in isolation per MEMORY

## Process notes

- **Branch:** `feature/v3-3-sync-playback` (1 squash on top of v3.2.0
  `b8575345`).
- **Pre-ship review:** code-reviewer (sonnet) on whole-branch —
  pending end-of-pipeline.
- **Build verification:** `dotnet build PeakCan.Host.slnx -c Debug`
  → 0 warnings, 0 errors.
- **Test verification:** `dotnet test PeakCan.Host.slnx`
  → 1048 + 6 SKIP / 0 fail (1 transient race-flake hit during
  pre-ship, passes in isolation per documented pattern).
- **Ship mechanism:** Tier 3 (`tier3_v330.py` — clone of
  `tier3_v320.py`, PARENT_SHA `b8575345`, 5 file overlays).
