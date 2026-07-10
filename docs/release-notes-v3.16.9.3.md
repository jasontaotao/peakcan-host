# Release Notes v3.16.9.3 — RebuildSignalsAsync tests migrated to v3.15.0 WatchedSignals contract (PATCH)

**Released:** 2026-07-10
**Parent:** v3.16.9.2 (`88e44f6` — X-axis wall-clock + LineSeries markers)
**Tag:** v3.16.9.3
**Branch:** `feature/v3-12-0-minor`

## Highlights

Migrates 5 pre-existing failing tests in
`tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs`
from the **obsolete v3.14.3 contract** (assert `sut.Signals` is
populated by `RebuildSignalsAsync`) to the **current v3.15.0
MINOR contract** (assert `sut.WatchedSignals` is populated by
`AddToWatch` + updated by `RebuildSignalsAsync`).

### Why these tests were failing on HEAD

`TraceViewerViewModel.Signals` is the legacy v3.14.3 "DBC 全列"
collection (every signal in the loaded DBC). v3.15.0 MINOR changed
the design to user opt-in: signals are added explicitly via
`AddToWatch(canId, signalName, sourceId)` and stored in
`WatchedSignals`. The legacy `Signals` collection is intentionally
left in place for back-compat but no longer populated.

5 tests were left over from the v3.14.3 era asserting
`sut.Signals.Should().HaveCount(N)`. Since v3.15.0, `Signals` is
always empty, so 3 of 5 failed and 2 of 5 passed vacuously
(they asserted empty). These failures were attributed in the
v3.16.9.2 plan §6.1 to commit `ea51d2f` ("switch to header-aware
parser + expose LastParseResult"), but the actual root cause is the
v3.15.0 MINOR migration gap. HEAD `ea51d2f` merely re-surfaced the
failing tests because it changed `TraceViewerService` init flow.

### What the migrated tests now verify

Each test now drives `AddToWatch` (the v3.15.0+ entry point) before
`RebuildSignalsAsync`, then asserts on `WatchedSignals` filtered by
`!IsPlaceholder` (the placeholder row is re-added by
`EnsurePlaceholderRow` on every `RebuildSignalsCore` call — verified
empirically).

| Test | Asserts |
|---|---|
| `RebuildSignalsAsync_NoDbc_LeavesSignalsEmpty` | `Signals.Empty` AND `WatchedSignals.Empty` (no DBC + no AddToWatch) |
| `RebuildSignalsAsync_DbcLoaded_PopulatesOneRowPerSignal` | `Signals.Empty`, `WatchedSignals.Where(!IsPlaceholder).Count == 1`, row 0 has correct CanIdHex/SignalName/Unit/LatestValue=322.0 |
| `RebuildSignalsAsync_MultipleSignalsSameId_PopulatesAll` | AddToWatch twice (RPM + TEMP for 0x100) → 2 rows with correct LatestValue (16.0 + 32.0) |
| `RebuildSignalsAsync_NoMatchingFrames_LeavesSignalsEmpty` | `Signals.Empty` AND `WatchedSignals.Empty` (DBC defines 0x100, only 0x555 frames loaded) |
| `RebuildSignalsAsync_LatestValueIsLastDecoded` | 3 frames → LatestValue=5.0 (the LAST decoded, not the first or max) |

One assertion changed: `row.IsPlotted` is now `BeTrue` (not
`BeFalse`) because `AddToWatch` auto-plots the just-added row
(`PlotSignalFromTableRow` at `TraceViewerViewModel.cs:1075`).
Documented inline.

### Why this is a tests-only PATCH

No production code touched. The 5 tests were stale (asserting the
v3.14.3 contract that was explicitly deprecated in v3.15.0). The
fix is to update the tests to assert the current contract, with
inline comments referencing the v3.15.0 MINOR design decision.

This closes the v3.16.9.2 plan §6.1 "RebuildSignalsAsync_*"
regression noted as a follow-up PATCH.

## Files in this PATCH

```
tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs    (+39 / -22)
  - 5 test methods rewritten to drive AddToWatch first
  - Inline comments referencing v3.15.0 MINOR design intent
  - Signals.Should().BeEmpty() guard added to all 5 tests
  - LatestValueIsLastDecoded: IsPlotted assertion changed BeFalse → BeTrue

docs/release-notes-v3.16.9.3.md (this file, NEW)
```

## Tests

- **Modified**: 5 tests (`RebuildSignalsAsync_*`)
- **All pass**: 5/5 (previously 2/5)
- **Full App.Tests**: 799 pass / 0 fail / 3 skip (was 793 / 3 / 3)
- **Full Core.Tests**: 448 pass / 0 fail (single-run mode); 447/1 in parallel mode — the 1 failure is a **pre-existing parallel-runner flake** (`Parse_MalformedLines_LogsEachWithLineNumberAndReason`), not caused by this PATCH. Verified via `--blame` + single-suite re-run that the flake is environmental (parallel test isolation), matching the `RecordServiceChannelTests.Writer_Flushes_Every_One_Second` pattern documented in 2026-07-04 devlog.

## Lessons (1 CONFIRMED)

1. **`test-rewrite-vs-skip`** — confirmed pattern. When a production
   contract changes (here v3.15.0 MINOR moved from auto-populate to
   opt-in), tests asserting the old contract have 3 options:
   - **Skip with annotation** (lose coverage documentation)
   - **Rewrite to new contract** (preserves coverage AND documents the change)
   - **Delete** (loses coverage)

   Rewrite is preferred when the test's INTENT (DBC-driven watch list
   correctness) survives the contract change — only the path differs.
   Skip is preferred when the test asserts a behavior that no longer
   exists in any form. Delete is last resort.

   The 5 tests in this PATCH were all "rewrite" candidates because
   their intent (validate DBC-driven signal rows after a rebuild) is
   still meaningful — only the path is `AddToWatch → WatchedSignals`
   instead of `RebuildSignalsAsync → Signals`.

## Risk notes

- **R1**: `EnsurePlaceholderRow` adds a placeholder row on every
  `RebuildSignalsCore` call; tests filter with
  `.Where(w => !w.IsPlaceholder)`. If `EnsurePlaceholderRow` is
  ever removed, the tests will still pass (the placeholder filter
  becomes a no-op). Low risk.
- **R2**: Tests assume `AddToWatch` succeeds silently when given a
  valid `(canId, signalName, sourceId)` triple. If the v3.16.x
  picker contract is later changed to throw on error, tests will
  need to be updated. Low risk — same pattern as v3.16.9.2 PATCH tests.