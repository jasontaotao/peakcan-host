# Release Notes — PeakCan Host v1.2.2

**Date:** 2026-06-26

## Summary

v1.2.2 is a 2-item PATCH release that closes two latent bugs filed in v1.2.1:

- **RoutineDatabase null-ILogger NRE**: Same `logger!` hazard as DidDatabase had (fixed in v1.2.1 Task 4). All 4 `logger!` call sites in `LoadUserFile` now guarded with `if (_logger is { } l)` pattern. Latent because all v1.2.x callers pass `NullLogger<RoutineDatabase>.Instance`. 3 new regression tests added.
- **SessionPanelViewModel.Dispose resets TesterPresentActive**: `Dispose()` now sets the public flag back to `false` before disposing the CTS. 1 new regression test added.

## Test Results

- 523 pass + 6 SKIP + 0 fail (was 519 pre-v1.2.1; +4 net this cycle: +3 RoutineDatabase + 1 Dispose)
- 0 warnings, 0 errors