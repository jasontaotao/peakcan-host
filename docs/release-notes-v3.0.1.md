# v3.0.1 PATCH — Per-signal DBC decode (2026-07-03)

## Summary

Closes the v3.0.0 V1 stub where `TraceViewerViewModel.RebuildSignalsAsync`
left the left-side signal DataGrid empty after loading ASC + DBC. Now
populates the rows with `(CAN ID, signal name)` pairs and a decoded
`LatestValue` taken from the **last** matching frame per (CAN ID, signal).

This PATCH is a focused fix — no new public surface beyond
`ITraceViewerService.LoadedFrames` (which exists only so the VM can read
the frame buffer without re-parsing the ASC). The Trace Viewer window
itself, the OxyPlot subplots, playback controls, and the DBC tab UX
are unchanged.

## Architecture

- `ITraceViewerService.LoadedFrames` (new) — exposes the in-memory frame
  buffer (`IReadOnlyList<ReplayFrame>`). The implementation returns
  `_frames` **without a defensive copy**; the contract is "callers must
  treat as read-only". This is intentional: ASC files are loaded once
  and the buffer is not mutated after `Load` returns, so a copy would
  burn CPU on every access for no observable benefit.
- `TraceViewerViewModel.RebuildSignalsAsync` (replaced) — synchronous
  decode (intentionally; see lessons). Pipeline:
  1. Early return if `_service.CurrentDbc == null`
  2. Single O(n) pass buckets `_service.LoadedFrames` by CAN ID
     (`Dictionary<uint, ReplayFrame>`; last frame wins per ID)
  3. Walk each DBC message; emit one `TraceSignalRow` per signal iff the
     bucket has ≥ 1 frame for that CAN ID
  4. `LatestValue = SignalDecoder.Decode(lastFrame.Data, sig)`
  5. Sort rows by `(CanIdHex, SignalName)` ordinal, assign to `Signals`
     `ObservableCollection`

## UI changes

None visible at the XAML level. The Trace Viewer window looks identical
to v3.0.0; the only user-observable delta is that the left DataGrid
now actually has rows after loading ASC + DBC (previously empty).

## Test delta

| Suite | v3.0.0 | v3.0.1 | Δ |
|-------|--------|--------|---|
| Core | 392 | 393 | **+1** (`TraceViewerServiceTests.LoadedFrames_ReturnsFramesAfterLoad`) |
| App | 509 | 514 | **+5** (`TraceViewerViewModelTests`: `NoDbc_NoRows`, `PopulatesOneRow`, `MultipleSignals`, `NoMatchingFrames_NoRows`, `LatestValueIsLastDecoded`) |
| Infrastructure | 84 | 84 | 0 |
| **Total** | **985 + 6 SKIP** | **991 + 6 SKIP** | **+6 net** |

Trace Viewer feature surface now: **6 + 10 = 16 tests total**.

Race-flake counter preserved (30/30+).

## Files modified

- `src/PeakCan.Host.Core/Replay/ITraceViewerService.cs`
  (+`LoadedFrames` property with XML doc)
- `src/PeakCan.Host.Core/Replay/TraceViewerService.cs`
  (+`LoadedFrames => _frames` impl, no defensive copy)
- `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs`
  (replaced no-op `RebuildSignalsAsync` with per-signal DBC decode;
  added `FormatCanIdHex` private helper for `"0x123"` vs `"0x00000123"`)
- `tests/PeakCan.Host.Core.Tests/Replay/TraceViewerServiceTests.cs`
  (+1 test for `LoadedFrames`)
- `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs`
  (+5 tests for `RebuildSignalsAsync`)

## Lessons

Three lessons surfaced during Task 2 (impl) and the Task 3 (pre-ship
review); all are inline, all are reusable.

1. **LAST-not-MAX `LatestValue` semantics is the correct default for
   the inspection use case** — when multiple frames in the recording
   have the same CAN ID, we display the value from the **last** (most
   recent) frame, not the maximum. Rationale: the user is inspecting
   "what is the signal value at end of recording?" — which is the
   last value. The per-frame history goes into the OxyPlot subplot via
   a separate code path, so the maximum is still visible in the
   timeline. **When a property is named `LatestValue` in a streaming-
   recording context, default to last-emitted, not max-observed.**

2. **Deterministic `(CAN ID, signal name)` ordinal sort is required
   for test stability AND UX** — without the sort, the row order is
   "whatever order `DbcDocument.Messages` enumerates" which depends on
   the parser internals (grouped by node, depth-first walk, etc.).
   Tests that assert specific row counts pass either way, but tests
   that assert specific row indices would be flaky. UX is also better:
   a stable ordering means the user can find a signal by remembering
   "it's the 3rd row down" across reloads. **Ordinal (not
   `CurrentCulture`) sort** — `StringComparer.Ordinal` — avoids
   locale-driven flakiness in CI.

3. **`LoadedFrames` is a "no defensive copy" contract** — the
   implementation returns `_frames` directly. The XML doc explicitly
   states "callers must treat as read-only" rather than returning
   `_frames.AsReadOnly()` or a copy. Rationale: `Load` is called once
   and the buffer is never mutated; a copy would burn CPU on every
   access for no observable benefit. **When a getter exposes internal
   state for read-only consumers and the type already implements
   `IReadOnlyList<T>`, prefer the direct reference + doc-contract
   over a defensive copy.** The risk is small here because all writers
   are within the class; if a future change adds external mutation,
   the contract needs to be revisited.

## Non-scope / follow-up items (intentional)

| Item | Why deferred |
|------|--------------|
| Subplot focus mode full impl | Still V1 stub (button + click handler no-op); remains deferred per v3.0.0 §Non-scope |
| Multi-trace comparison / diff | Out of scope per spec §2 Non-goals |
| Bookmarks / annotations | Out of scope per spec §2 Non-goals |
| PNG export | CSV only in V1 per spec §2 Non-goals |
| DBC value-table encoding in Trace Viewer | Long-term non-goal since v1.4.0 |

## Process notes

- **Branch:** `feature/v3-0-1-patch` (2 commits: aec0c32 + b2ed1ec).
- **Pre-ship review:** Task 3 — 0C / 0H / 5M, all MEDIUMs
  documentation/rationale comments, no code changes required.
- **Test isolation:** all 6 new tests are STA-safe (no `[Fact]` on the
  UI thread); they run in the standard Core/App test pipelines.
- **Ship mechanism:** Tier 3 force-update via `gh api` (parent SHA
  → tree → blobs → tree → commit → refs/tags → release). Pattern
  established in v3.0.0 ship.
