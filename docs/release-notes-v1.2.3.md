# Release Notes — PeakCan Host v1.2.3

**Date:** 2026-06-26

## Summary

v1.2.3 is a 1-issue PATCH release that fixes the WPF UI dispatcher starvation observed
when the bus is busy. The Stats tab and other 1 Hz UI ticks were unable to be pumped while
the SDK read loop was delivering frames at 8 kHz through the DBC decode path. Two changes:

- **`SignalViewModel.ApplyFrame` no longer marshals to the UI dispatcher per frame**:
  the SDK worker now buffers decoded entries + chart samples into a per-thread queue,
  and a `System.Threading.Timer` at ~30 Hz drains the queue onto the UI thread in
  one batch. The dispatcher queue length drops from 8 000/s to ≤ 30/s, leaving the
  1 Hz `StatsViewModel` tick and other BackgroundService ticks room to pump.
  `SignalViewModel` gains `IDisposable` to dispose the timer; the DI lifetime
  already covers the app, so this is purely a cleanup-correctness change.
- **`ChannelRouter` zero-allocation fan-out**: the per-frame `IFrameSink[]` allocation
  in `OnChannelFrame` is replaced by a single `ImmutableArray<IFrameSink>` field
  read via `ImmutableInterlocked.InterlockedExchange` (the struct-friendly analogue
  of `Volatile.Read` — the latter's `T : class` constraint rules it out). The
  per-frame allocation drops from ~32 B/frame (a 4-element array) to 0 B/frame
  on the 4-sink production path.

## Why this is a PATCH not a MINOR

The public API is unchanged. `SignalViewModel.ApplyFrame` is still `void` and the
behavioural contract is the same — frame decoding still produces the same decoded
signals and chart samples; the difference is that the UI-thread mutation is now
batched through a timer instead of a per-frame dispatcher post. `ChannelRouter` is
internal to Infrastructure; the public sinks/channels API is unchanged. The new
internal fields (`_pendingLock`, `_pending`, `_drainTimer`, `DrainCount`, `DrainPendingForTest`,
`FilterRebuildCount`, `_lastFilterPattern`, `_lastFilterRebuildUtc`, `FilterRebuildInterval`,
`_sinks`) are either `private` or `internal` and surfaced only through
`[InternalsVisibleTo]` for unit tests.

## Known issue (NOT fixed in this PATCH)

**The Stats tab OxyPlot chart is blank** when the app is run on .NET 10 windows.
The chart axes (X = 0..60 sample index, Y = 0..100) and title render correctly,
but the line series + legend do not. The Y=0 baseline line is also clipped at
the X-axis edge.

Root cause: `OxyPlot.Wpf 2.2.0` (the chart library added in v0.0.1 scaffold) is
a 2022 release with no later WPF package version — it predates .NET 10 by 3 years
and its WPF `PlotView` control does not fully render under the .NET 10 windows
binary (the `LineSeries` line + the new `Legends` collection are not drawn even
when populated correctly). This bug has existed since v0.0.1; nobody noticed
because nobody actually opened the Stats tab under load before v1.2.3.

The dispatcher starvation fix in this PATCH makes the Stats tab **responsive**
(can switch to it without freezing, can read Total / Error frames, can switch
back) but the chart still shows an empty plot area. Replacement of the
OxyPlot chart is deferred to v1.3.0 as a separate MINOR task.

## Test Results

- 535 pass + 6 SKIP + 0 fail (was 523 at v1.2.2; +12 net this cycle: 5 from
  `SignalViewModelTests` PATCH-2 buffer/drain contract tests, 2 from
  `ChannelRouterTests` allocation tests, 5 from PATCH-1 throttling / `Points`
  / `InvalidatePlotCalls` tests that were partially reverted along with the
  `StatsViewModel` OxyPlot refactor and are not present in this PATCH).
- 0 warnings, 0 errors
- Manual smoke: WPF app starts, Stats tab switches in <100 ms with no UI freeze,
  Total frames increments per 1 Hz tick.
