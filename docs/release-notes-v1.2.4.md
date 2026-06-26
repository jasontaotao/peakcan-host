# Release Notes — PeakCan Host v1.2.4

**Date:** 2026-06-26

## Summary

v1.2.4 is a 2-issue PATCH release that restores data flow to the
Trace / Stats / Recording views. v1.2.3 fixed the WPF UI dispatcher
starvation but introduced (or rather, left unfixed) a separate DI
wiring gap that prevented any `BackgroundService` from ever starting.

In v1.2.3 main, the SDK read loop was delivering frames and
`ChannelRouter` was fanning them out, but only the `CanApi` self-attach
was active — `TraceService`, `StatisticsService`, `SinkWiringService`,
and `DbcDecodeBackgroundService` never ran their `ExecuteAsync`
loops. Result: `TraceViewModel.Entries` stayed empty, `StatsViewModel`
never got any samples, and the recording view had no data even with
the bus busy.

Three production changes:

- **`App.OnStartup` now calls `_host.StartAsync()` synchronously**
  before resolving the shell. Without this call, no `BackgroundService`
  ever starts. The sync-over-async pattern is safe in WPF `OnStartup`
  because `Microsoft.Extensions.Hosting`'s `BackgroundService.StartAsync`
  awaits `ExecuteAsync` on the threadpool without capturing the WPF
  Dispatcher `SynchronizationContext` — no deadlock risk on the STA
  UI thread.

- **`AppHostBuilder` registers `TraceService` and `StatisticsService`
  as hosted services too**, via the singleton-instance factory pattern
  `AddHostedService(sp => sp.GetRequiredService<TraceService>())`.
  Without this, the existing singletons are never started by
  `IHost.StartAsync` because the hosted-service resolution list is
  empty for those two. `SinkWiringService` and
  `DbcDecodeBackgroundService` were already wired correctly in v1.2.3
  (lines 100, 199) and needed no change.

- **`TraceService.PeriodicTimer` raised from 50 ms to 200 ms**, and
  the per-tick buffer capacity raised from 256 to 1024. At sustained
  high frame rates (≥1 000 fps) the 50 ms tick fired the WPF
  dispatcher 20 times/sec with batches of ~50 frames each, saturating
  the UI thread with `ObservableCollection` mutations and DataGrid
  item-container creation. The 200 ms tick caps dispatcher
  invocations at 5/sec with batches of ~200 frames; absolute work is
  unchanged but layout passes collapse, leaving the UI thread free
  for paint + scroll between batches. End-to-end latency from bus
  arrival to Trace row visibility rises from ~50 ms to ~200 ms p50 —
  still well under the human-perceptible threshold for live trace
  monitoring.

A new regression test pins the load-bearing wiring invariant:

- **`AppHostBuilderTests.Build_Registers_TraceService_And_StatisticsService_As_Both_Singleton_And_HostedService_Same_Instance`**
  asserts that the hosted-service registration for `TraceService` and
  `StatisticsService` resolves to the **same instance** as the
  singleton registration. Without this test, a future refactor from
  `AddHostedService(sp => sp.GetRequiredService<T>())` to
  `AddHostedService<T>()` (which would resolve a second instance)
  would silently reintroduce the v1.2.3 bug, with all 535+ existing
  tests staying green.

## Why this is a PATCH not a MINOR

The public API is unchanged. `TraceService` and `StatisticsService`
implementations are unchanged. The DI container shape is unchanged.
The `App.OnStartup` contract gains one call; the
`AppHostBuilder.AddHostedService` list gains two registrations. The
`TraceService.PeriodicTimer` interval and buffer are configuration
constants, not API. No `Version` change required for dependents.

## Why this was not in v1.2.3

The DI wiring gap was discovered during the v1.2.3 dispatcher
investigation (via DIAG logging: `sinks=1, dispatches=18000` — only
the `CanApi` self-attach was active). v1.2.3 shipped the dispatcher
fix because that was the regression-blocking item; the IHostedService
fix was split into the v1.2.4 candidate branch to keep v1.2.3 scoped
to one concern.

## Known issue (carried over from v1.2.3)

The Stats tab OxyPlot chart still shows an empty plot area under
.NET 10 windows. OxyPlot.Wpf 2.2.0 is a 2022 release with no later
WPF package; its WPF `PlotView` control does not fully render under
.NET 10 windows binary. Pre-existing bug since v0.0.1 scaffold,
invisible until v1.2.3 made the Stats tab reachable under load.
Replacement deferred to v1.3.0 MINOR.

## Tests

536 pass + 6 SKIP + 0 fail (was 535; +1 net — the new
`AppHostBuilderTests` regression test). 0 warnings, 0 errors.
Pre-ship csharp-reviewer: **0C / 0H / 1M / 2L — APPROVE** with the
MEDIUM (regression test for singleton/hosted-service contract) and
LOW (inline deadlock-safety comment) addressed same-commit.

## Files changed

- `src/PeakCan.Host.App/App.xaml.cs` — `_host.StartAsync()` in
  `OnStartup` + deadlock-safety inline comment
- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` —
  `AddHostedService` for `TraceService` and `StatisticsService`
- `src/PeakCan.Host.App/Services/TraceService.cs` — `PeriodicTimer`
  50 ms → 200 ms, buffer 256 → 1024
- `tests/PeakCan.Host.App.Tests/Composition/AppHostBuilderTests.cs`
  — new regression test