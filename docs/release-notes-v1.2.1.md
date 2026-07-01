# Release Notes — PeakCan Host v1.2.1

**Date:** 2026-06-25

## Summary

v1.2.1 is a PATCH release that bundles eight cosmetic / robustness fixes
discovered after v1.2.0 ship: three UDS cosmetic cleanups (brush freeze,
test rename, session VM dispose wiring), trailing newlines on 8 UDS files,
two test-isolation flakes (root-caused to leaked `Application.Current` in
xUnit WPF tests), one NRE in `DidDatabase`'s 2-arg ctor, and one
collection-fixture hoist. No behavioral changes for end users; this
release is purely code-health.

## Bug Fixes

- **`UdsView.xaml.cs` SolidColorBrush freeze** — three brushes
  (`BgBrush`, `WarnBrush`, `ErrorBrush`) are now `Freeze()`'d at construction
  so they can be shared across `Freezable` boundaries on the dispatcher
  without warnings or per-access allocation.
- **`UdsViewModelTests` over-promising test renamed** — a test method
  named after a behavior the production code does not implement has been
  renamed to accurately reflect what it asserts.
- **`UdsView.xaml.cs` session VM dispose wiring** — the
  `SessionPanelViewModel` is now disposed in the view's `Unloaded`
  handler (via a new `DisposeSessionVm()` helper) so its 2s
  `TesterPresent` background loop stops when the user closes the tab.
- **Trailing newlines on 8 UDS files** — adds a final newline to each of
  the eight UDS source files that were missing one, eliminating POSIX
  tool noise (e.g. `cat -A`, `wc -l`).
- **`DidDatabase` 2-arg ctor NRE** — the 2-arg constructor
  `DidDatabase(string, ILogger?)` no longer throws NullReferenceException
  when a `null` logger is passed. Adds null-safe logger fallback matching
  the 1-arg ctor's existing behavior.
- **`SignalViewModelTests` leaked `Application.Current`** — the test
  class was leaking a WPF `Application` instance between tests, which
  caused intermittent `STA`/`MTA` dispatcher failures in unrelated tests
  in the same xUnit collection. Fixture now tears down the dispatcher
  reliably.
- **`DbcDecodeBackgroundServiceTests` leaked `Application.Current`** —
  same root cause as above: a background `Application` was not disposed
  in test cleanup, contaminating subsequent tests. Same fix pattern.

## Test Infrastructure

- **`RoutineDatabaseFixture` xUnit collection fixture** — the
  `NewPopulatedDb()` helper used by `RoutinePanelViewModelTests` is
  hoisted into a shared `IAssemblyFixture`-style collection fixture
  (parallel-safe via `[CollectionDefinition]` with `DisableParallelization
  = false` on the owning collection), eliminating per-test rebuild cost
  and matching the pattern used by the other UDS test classes.

## Test Results

- Baseline v1.2.0: 514 pass + 6 SKIP + 0 fail (Core 207 + App 233 + Infrastructure 74).
- v1.2.1: **519 pass + 6 SKIP + 0 fail** (Core 207 + App 238 + Infrastructure 74).
  Delta **+5 pass** (App +5 from Tasks 4/5/6 new failing-tests-now-passing;
  Core and Infrastructure unchanged). 6 SKIP unchanged (4 hardware-dependent
  App + 2 hardware-dependent Infrastructure).
- All v1.2.1 fixes land with **RED → GREEN** discipline: a failing test
  preceded each production code change (Tasks 4/5/6). Tasks 1/2/3 are
  cosmetic / refactor and have no test changes.
- The two RED repro tests added during Tasks 5 and 6 root-cause
  investigation were removed in commit `00cf863` after their fixes landed
  (they were diagnostic scaffolding, not regression coverage).

## Commits Since v1.2.0

```
00cf863 chore(tests): remove 2 RED repro tests from Tasks 5+6
f4bdcc2 docs(spec): update §9.1 with final Task 6 commit SHA
d3909a8 fix(tests): DbcDecodeBackgroundService flake from leaked Application.Current
34e4bcc docs(spec): update §9.1 with final Task 5 commit SHA
23e7d7c fix(tests): SignalViewModel flake from leaked Application.Current
1b1c24f fix(uds): DidDatabase 2-arg ctor accepts null ILogger without NRE
f4935c8 test(uds): hoist NewPopulatedDb into xUnit collection fixture
c36c5bb chore: add trailing newline to 8 UDS files
39069ef fix(uds): freeze brushes, rename misleading test, wire SessionPanelVM.Dispose
```

## Known Limitations / v2.0 Backlog

- J1939 / CANopen (v2.0).
- Linux + SocketCAN cross-platform (v2.0).
- OEM-specific key algorithms remain out of scope; OEMs wire their
  `IKeyDerivationAlgorithm` at deploy time via DI.