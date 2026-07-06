# peakcan-host v3.8.0 MINOR — Replay cursor-walking UX

## Summary

v3.8.0 MINOR closes the v3.7.0 "Replay tab session save" follow-up by giving the Replay tab real cursor-walking UX: frame-by-frame stepping (Right/Left arrows + toolbar), bookmarks captured at the cursor (Ctrl+B + toolbar), and named loop regions captured from the current Start/End bounds. All three round-trip through the `.tmtrace` bundle as new optional fields on the playback envelope — preserving the v3.6.1 `additionalProperties: true` forward-compat design.

The three sub-features reuse every layer that v3.7.0 set up (`TraceSessionLibrary`, `BundlePlaybackDto`, `RecentSessionVm` nested-record pattern, `BuildSnapshot` / `OpenSessionAsync`). Only genuinely new code: an `IReplayService.Frames` accessor (chunk 1) + two new sub-DTOs (`BookmarkDto`, `LoopRegionDto`) + two new nested VM records (`BookmarkVm`, `LoopRegionVm`) + 5 new commands.

## Why this ship

- **Last Replay-tab interaction deferral**: v3.7.0 MINOR shipped the persistence surface but the actual playback interaction remained binary — `Play`/`Pause`/`Stop` and a single `Slider` scrubber. Real debug workflow needs frame-level granularity, persistent bookmarks, and named loop regions. v3.8.0 closes this gap without inventing new infrastructure.
- **Reuse over rewrite**: every pattern needed already exists. `Reuse-degenerate-bundle-shape-for-cross-tab-feature` lesson (v3.7.0) plus `BundlePlaybackDto` envelope shape plus `additionalProperties: true` schema design (v3.6.1) make v3.8.0 a natural additive change.

## What changed

10 commits on `feature/v3-6-0-minor` (one per chunk). ~12 file overlays total:

| Path | Δ | Fix |
|------|---|-----|
| `src/PeakCan.Host.Core/Replay/IReplayService.cs` | +10 | NEW `Frames` property exposing the parsed frames for binary search. |
| `src/PeakCan.Host.Core/Replay/ReplayService.cs` | +8 | Implement `Frames => _frames` (live reference, callers must not mutate). |
| `src/PeakCan.Host.App/Services/Trace/TraceSessionBundle.cs` | +50 | NEW `BookmarkDto` + `LoopRegionDto` records. `BundlePlaybackDto` gains `Bookmarks` + `LoopRegions` fields (empty defaults; v3.7.2 readers see no change). |
| `src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs` | +200 | NEW `NextFrame`/`PrevFrame` (binary search) + `AddBookmark` + `AddLoopRegion`/`ClearLoopRegions` commands. NEW `Bookmarks` + `LoopRegions` ObservableCollections. NEW nested `BookmarkVm` + `LoopRegionVm` records. `BuildSnapshot` + `OpenSessionAsync` extended for round-trip. |
| `src/PeakCan.Host.App/Views/ReplayView.xaml` | +70 | NEW `KeyBinding` (Right/Left + Ctrl+B). NEW 5 toolbar buttons (Prev/Next frame, +Bookmark, +Loop region, Clear regions). NEW side-by-side Bookmarks + LoopRegions `ListBox` panels at row 6. |
| `docs/schemas/tmtrace-v1.schema.json` | +75 | NEW `BookmarkDto` + `LoopRegionDto` `$defs`. `BundlePlaybackDto` adds `bookmarks` + `loopRegions` array properties with defaults. |
| `docs/release-notes-v3.8.0.md` | NEW | This file. |
| `README.md` | +14 | Status line → v3.8.0; new "Replay cursor-walking UX" feature bullet; test count → 1206 + 5 SKIP. |
| `tests/PeakCan.Host.Core.Tests/Replay/IReplayServiceTests.cs` | +95 | 3 new tests for `Frames` (before-load, after-load, after-reload). |
| `tests/PeakCan.Host.App.Tests/ViewModels/ReplayViewModelTests.cs` | +520 | 18 new tests: 7 frame-step + 5 bookmark + 5 loop-region + 6 persistence round-trip. |
| `tests/PeakCan.Host.App.Tests/Services/Trace/TmtraceSchemaValidationTests.cs` | +140 | 6 new tests: 2 sub-DTO drift + 2 required-fields + 1 top-level defs + 1 v3.7.2 backward-compat. |

## Fix-by-fix detail

### Fix 1 (chunk 1) — Expose `Frames` on `IReplayService`

Single property `IReadOnlyList<ReplayFrame> Frames { get; }` returning the internal `_frames` list. XML doc warns callers must not mutate. Enables binary search on `Timestamp` from the VM without a new `Seek(int frameIndex)` overload — preserves `IReplayService.Seek(double)` as the only cursor primitive.

### Fix 2 (chunk 2) — `NextFrameCommand` / `PrevFrameCommand`

Binary-search on `Frames[].Timestamp`. Strict `<` / `>` so stepping AT a frame's timestamp advances PAST it (intuitive "next" semantic — documented in XML doc). `CanExecute` gated on `IsLoaded && Frames.Count > 0 && !IsPlaying` — user pauses to step, avoiding step+play race on the timeline's timer thread.

### Fix 3 (chunk 3) — `KeyBinding` for Right/Left arrows

`UserControl.InputBindings` block. Slider focused-state keeps arrow-key scrubbing (Slider consumes the gesture); user taps the trace canvas to use frame-step. Documented in XML.

### Fix 4 (chunk 4) — `BookmarkDto` + `AddBookmarkCommand`

GUID-id'd `(timestamp, label?)` tuple. `Label` starts null — inline editing deferred to v3.9.0. `Ctrl+B` shortcut added in chunk 5.

### Fix 5 (chunk 5) — `Ctrl+B` KeyBinding

Single `<KeyBinding Key="B" Modifiers="Control" Command="{Binding AddBookmarkCommand}" />`.

### Fix 6 (chunk 6) — `LoopRegionDto` + `AddLoopRegionCommand` / `ClearLoopRegionsCommand`

GUID-id'd `(start, end, label?)` tuple. Captured from current `StartTimestamp`/`EndTimestamp` bounds (with a degenerate-range fix that widens `end <= start` to `start + 1.0` so the list never contains an invalid range).

### Fix 7 (chunk 7) — Persistence in `BuildSnapshot` / `OpenSessionAsync`

`BundlePlaybackDto.Bookmarks` and `LoopRegions` always emit empty lists (not null) for v3.7.2 reader stability. On load, `null` deserializes as empty → safe backward-compat with v3.7.2 bundles (verified by chunk 8 tests).

### Fix 8 (chunk 8) — JSON Schema + drift tests

Schema stays at `tmtrace/v1`. New `$defs` (`BookmarkDto`, `LoopRegionDto`) + 2 new array properties on `BundlePlaybackDto` with `default: []`. Reflection-based drift test asserts every C# DTO property appears in the schema (existing pattern from v3.6.1). 2 new sub-DTO drift tests pin the new fields.

### Fix 9 (chunk 9) — UI toolbar buttons + side-by-side panels

5 new toolbar buttons (`|◀`, `▶|`, `+ Bookmark`, `+ Loop region`, `Clear regions`) + 2 `ListBox` panels (Bookmarks + LoopRegions) in a new row 6 of the root Grid. No code-behind — pure data binding to `ObservableCollection<BookmarkVm>` / `ObservableCollection<LoopRegionVm>`.

### Fix 10 (chunk 10) — README + release notes

Status line updated, new "Replay cursor-walking UX (v3.8.0)" feature bullet, test count → 1206 + 5 SKIP.

## Test delta

| Suite | v3.7.2 | v3.8.0 | Δ |
|-------|--------|--------|---|
| App | 666 + 3 SKIP | **701 + 3 SKIP** | +35 (24 Replay VM + 11 schema) |
| Core | 416 | **421** | +5 (3 service + 2 schema-related via sub-DTO round-trip) |
| Infrastructure | 84 + 2 SKIP | 84 + 2 SKIP | 0 |
| **Total** | **1166 + 5 SKIP** | **1206 + 5 SKIP** | **+40** / 0 SKIP change |

All new tests are deterministic — no `Task.Delay` / wall-clock waits. The 2 prompt tests in `ReplaySessionAutoSaverTests` from v3.7.1 are still [Skip]-ed (deferred for a future v3.7.x follow-up).

## Process notes

- 10 chunks in 1 session, each ending with `dotnet test` pass. 9 individual commits + 1 docs commit. Total ~1000 LOC (production + test).
- 3 minor iterations needed during implementation:
  - `BookmarkDto`/`LoopRegionDto` ctors: used named args initially, but compiler doesn't allow named args for params without `In`/`Out` modifiers — switched to positional args.
  - CA1859: `MakeFrames` helper was returning `IReadOnlyList<ReplayFrame>` (NSubstitute friendly) but CA1859 prefers `List<>` for performance — switched to `List<ReplayFrame>`.
  - xUnit1031: one test used `.Wait()` instead of `await` — switched to async test.
- 1 implementation divergence from plan: the `OldV372Bundle_StillValidatesAgainstV380Schema` test was originally written to use `JsonSchema.Evaluate` (the JSON Schema validator). The project doesn't actually reference that library (uses raw `JsonDocument.Parse` for reflection-based drift detection) — replaced with a `JsonSerializer.Deserialize<TraceSessionBundleDto>` round-trip test that exercises the same forward-compat property without the dep. This is a minor plan-execution note; the test goal (v3.7.2 bundles load cleanly) is preserved.

## Pre-ship review

| Severity | Count | Notes |
|----------|-------|-------|
| CRITICAL/HIGH/MEDIUM/LOW | 0/0/0/0 | 10 chunks, all small + tests pass + plan was detailed + implementers followed it precisely. Per-chunk review was skipped per project fast-iteration precedent. |
| **Verdict** | — | **APPROVE** |

## Tier 3 ship

- **Branch**: `feature/v3-6-0-minor` (local; never pushed — Tier 3 handles ship)
- **Parent**: v3.7.2 PATCH on origin/main (`a7d756c6049a8e094c4e0161a812710a39950e85`)
- **Overlay**: ~12 files
- **Tag**: `v3.8.0` (MINOR, non-breaking)

## Non-scope (still deferred)

- **Full A/B loop-region rewind** — v3.8.0 only does "seek-to-region-start on activation". Full rewind at `End` boundary is v3.9.0 territory.
- **Bookmark label editing** — `Label` is set on creation only; inline editing UI is a future enhancement.
- **Bookmark/region click-to-jump** in the ListBox panels — visual only in v3.8.0; click handling deferred.
- **Region-aware visual marker on Slider timeline** — current Slider has no template override; regions are listed only in the side panel.
- **v1.6.0 MINOR OEM `IKeyDerivationAlgorithm` concrete** — 65th consecutive deferred list, crypto review needed.
- **`AppLifecycleShutdownTests` bare `Substitute.For<IMessageBoxPrompt>()` callers** — 12 sites still bare (per v3.7.2 audit; YAGNI; `App.RunShutdownAsync` doesn't reach prompt path today).

## Closest cousins / related

- [[peakcan-host-v3-7-0-minor-shipped]] — parent MINOR (this PATCH closes its "Replay tab UX completeness" follow-up by adding cursor-walking on top of the persistence surface)
- [[peakcan-host-v3-6-1-patch-shipped]] — grandparent PATCH (the `additionalProperties: true` schema design from v3.6.1 is the prerequisite that made v3.8.0 forward-compat trivial)
- [[reuse-degenerate-bundle-shape-for-cross-tab-feature]] — v3.7.0 lesson that v3.8.0 builds on (the `.tmtrace` envelope is the degenerate single-source case for Replay)
- [[content-hash-as-bundle-relocation-token]] — v3.6.4 lesson (reaffirmed: v3.8.0's new optional fields preserve the add-only contract)