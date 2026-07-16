# v3.50.6 PATCH — Dynamic Decimal Precision in Watch List + Tracker

## Summary

Replaces hard-coded F2 numeric display in the Trace Viewer watch list
(Latest / Blue / Δ columns) and the OxyPlot Tracker tooltip with
precision derived from each signal's DBC `factor`. Signals with
sub-integer steps (e.g. `factor=0.001`) now display full precision
(`3.353` not `3.35`). User-authorized behavior change: `Factor=1.0`
integer-stepped signals now display as `"100"` instead of `"100.00"`.

## Changes

**Core** (PeakCan.Host.Core):
- `SignalFormatter.ResolveDecimalDigits(double factor) → int` — new public static
  - Algorithm: 10-power main path (log10 round-trip with 1e-9 tolerance) + fraction simplification (min k such that 10^k % denom == 0) + best-effort ceil fallback
- `SignalFormatter.FormatValue(double factor, double value) → string` — convenience wrapper
- `SignalFormatter.FormatValue(int digits, double value) → string` — sister overload for hot-path callers that cache digits

**App** (PeakCan.Host.App):
- `WatchedSignalRow._decimalDigits` — plain C# int field (MVVM-gen limitation, sister of v3.50.0 `Signal` and v3.50.5 `Dbc`)
- `WatchedSignalRow.Signal` setter — recomputes `_decimalDigits` on signal change
- `WatchedSignalRow.LatestText` / `BlueText` / `DeltaText` — use `SignalFormatter.FormatValue(_decimalDigits, value)`
- `EnumTrackerLineSeries.GetNearestPoint` — y-value line uses `SignalFormatter.FormatValue(_signal.Factor, yVal)`

**Tests** (+6 new, +1 modified):
- `tests/PeakCan.Host.Core.Tests/SignalFormatterTests.cs` — NEW (+6 tests)
- `tests/PeakCan.Host.App.Tests/ViewModels/WatchedSignalRowTests.cs` — +2 `WatchedSignalRowPrecisionTests`; +1 modified (`LatestText_ReturnsNumeric_WhenNotMapped` updated from `Factor=1.0` to `Factor=0.01` to preserve intent under new contract)

## Behavior change (user-authorized 2026-07-16)

`Factor=1.0` integer-stepped signals (CAN counters, raw bit counts) now display
as `"100"` instead of `"100.00"`. User explicitly accepted this trade-off:
factor-derived precision is the primary signal; hard-coded F2 is no longer the
contract.

## Test plan

- [x] `dotnet test --filter "FullyQualifiedName~SignalFormatterTests"` — 6/6 PASS
- [x] `dotnet test --filter "FullyQualifiedName~WatchedSignalRow"` — 10/10 PASS (8 existing + 2 new)
- [x] `dotnet test` (full suite) — App 824 / Core 467 / Infra 89 = 1380 PASS; 0 failures
- [x] `dotnet build src/PeakCan.Host.App/` — 0 errors, 0 new warnings

## Sister patterns

- v3.50.5 PATCH `EnumTrackerLineSeries` (immediate predecessor)
- v3.50.0 MINOR `WatchedSignalRow.Signal` field precedent (plain C# not `[ObservableProperty]`)
- v3.50.5 PATCH `Dbc` field precedent (cached reference at Signal-set time)
- v3.50.5 PATCH `TryDecodeEnumText` sister pattern (Core pure static helper)
- W19 R1 LESSON (verbatim re-extraction, no fabrication)
- W22 R1 LESSON (spec-then-implement order; spec bug fix at 78cedf6 preceded T1 implementer commit)

## Algorithmic bug fix during execution

Spec/plan originally specified `Math.Ceiling(Math.Log10(denom))` for the
fraction simplification fallback. This incorrectly returns 1 for `0.25`
(denom=4, log10(4)≈0.6, ceil=1) and 1 for `0.125` (denom=8, log10(8)≈0.9, ceil=1).
Fixed at commit `78cedf6` to use `MinTerminatingDigits` (min k such that
10^k % denom == 0): 1/4 → 2 digits, 1/8 → 3 digits. Spec + plan updated
in same commit; brief regenerated; implementer re-dispatched.

## NEW 1/3 lesson candidates (8 total)

1. `decimal-precision-must-derive-from-factor-not-fixed-f2`
2. `decimal-precision-fraction-fallback-handles-binary-factors`
3. `signal-setter-should-cache-derived-properties-to-avoid-hot-path-recomputation`
4. `enum-text-priority-must-precede-decimal-precision`
5. `precision-rounding-tolerance-must-be-1e-9-not-1e-6`
6. `cached-derived-format-metadata-requires-public-overload-contract` (FormatValue(int,double) sister overload)
7. `contract-migration-tests-must-change-fixture-not-assertion-when-derived-formatting-changes`
8. `numeric-display-contract-change-needs-explicit-user-review-for-integer-signals`

## Out of scope

See [release notes](docs/release-notes-v3.50.6.md) §Out of scope.