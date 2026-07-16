# Release Notes v3.50.6 — Dynamic Decimal Precision in Watch List + Tracker

**Released**: pending ship
**Tag**: v3.50.6
**Branch**: `feature/v3-50-6-patch-decimal-precision`
**Parent**: v3.50.5 PATCH (`e9bd205` on `main`)

## Why this PATCH

User feedback 2026-07-16 (screenshot of `B2V_Ucel1_N` signal):
- DBC factor = `0.001` (each integer step = 1 mV).
- Actual decoded value 3.353 V was displayed as **"3.35"** because `LatestText` used hard-coded `F2`.
- 3.353 vs 3.355 looked identical — **information loss**.
- "横展此类问题" direction generalizes: every signal with `factor < 1` loses precision at F2.

v3.50.5 PATCH fixed enum text display but left numeric formatting at F2. v3.50.6 addresses the parallel precision issue.

## What this PATCH does

### 1. `SignalFormatter` (new Core type)

Pure static class in `PeakCan.Host.Core.Dbc` with two public methods:

- `ResolveDecimalDigits(double factor) → int` — minimum decimal digits needed to express the smallest step implied by the factor. Algorithm: 10-power main path (log10 round-trip check) + fraction-simplification fallback for sub-1 factors (min k such that 10^k % denom == 0) + best-effort ceil fallback.
- `FormatValue(double factor, double value) → string` — convenience wrapper, uses `InvariantCulture`.
- `FormatValue(int digits, double value) → string` — sister overload accepting pre-resolved digits, used by hot-path callers to avoid per-frame `ResolveDecimalDigits` calls.

### 2. Watch list uses factor-derived precision

`WatchedSignalRow` adds `_decimalDigits` int field (plain C#, MVVM-gen limitation sister of v3.50.0 `Signal` field and v3.50.5 `Dbc` field). The `Signal` setter recomputes `_decimalDigits` at signal-change time using `SignalFormatter.ResolveDecimalDigits(value.Factor)`. The `LatestText` / `BlueText` / `DeltaText` getters then use `SignalFormatter.FormatValue(_decimalDigits, value)` instead of `value.ToString("F2", ...)`.

### 3. Tracker tooltip uses factor-derived precision

`EnumTrackerLineSeries.GetNearestPoint` switches the y-value line from `yVal.ToString("F2", ...)` to `SignalFormatter.FormatValue(_signal.Factor, yVal)`. Watch list and Tracker now show the same precision.

### 4. Behavior change (user-authorized 2026-07-16)

`Factor=1.0` (integer-stepped signals — CAN counters, raw bit counts) now display as `"100"` instead of `"100.00"`. This is a **deliberate trade-off**: factor-derived precision is the primary signal; hard-coded F2 is no longer the contract. User explicitly accepted this trade-off (review of T2 flagged the change; user direction: "接受变化 ship v3.50.6").

## Files changed

### Core (1 src + 1 test)
- `src/PeakCan.Host.Core/Dbc/SignalFormatter.cs` — NEW
- `tests/PeakCan.Host.Core.Tests/SignalFormatterTests.cs` — NEW (+6 tests)
- `docs/superpowers/specs/2026-07-16-v3-50-6-decimal-precision-design.md` — spec
- `docs/superpowers/plans/2026-07-16-v3-50-6-decimal-precision.md` — plan

### App (2 src + 1 test)
- `src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs` — + `_decimalDigits` field + Signal setter + 3 getters
- `src/PeakCan.Host.App/ViewModels/EnumTrackerLineSeries.cs` — y-value line uses `FormatValue`
- `tests/PeakCan.Host.App.Tests/ViewModels/WatchedSignalRowTests.cs` — `+ 2 tests`, `+ 1 modified` (v3.50.5 test updated for new contract)

## LoC delta (rough)

- Core: +52 LoC (1 helper) + 60 LoC (6 tests) = +112 LoC
- App: +15 LoC (WatchedSignalRow) + 5 LoC (Tracker) + 53 LoC (2 new + 1 modified test) = +73 LoC
- Total: +185 LoC across 6 files

## Test outcomes

- **App.Tests**: 824 PASS / 3 SKIP / 0 FAIL (baseline 822 + 2 new; v3.50.5 test modified for new contract)
- **Core.Tests**: 467 PASS / 0 SKIP / 0 FAIL (baseline 461 + 6 new)
- **Infrastructure.Tests**: 89 PASS / 2 SKIP / 0 FAIL (unchanged)
- **Build**: 0 errors, 0 new warnings (3 pre-existing warnings in unrelated files)

## Lesson candidates (NEW 1/3 each — await 2nd observation for promotion)

| Lesson | Source observation |
|---|---|
| `decimal-precision-must-derive-from-factor-not-fixed-f2` | T1: factor → digit mapping is the right primary signal; F2 hard-coding loses information for sub-1 factors |
| `decimal-precision-fraction-fallback-handles-binary-factors` | T1: 0.5/0.25/0.125 path via `MinTerminatingDigits` (min k such that 10^k % denom == 0); covers engineering factors not expressible as 10-powers |
| `signal-setter-should-cache-derived-properties-to-avoid-hot-path-recomputation` | T2: digit caching at Signal setter, not getter; sister of v3.50.5 Dbc field which similarly caches lookup inputs |
| `enum-text-priority-must-precede-decimal-precision` | T2: TryDecodeEnumText success → return enum text; else FormatValue; preserves v3.50.5 user-visible behavior for enum signals |
| `precision-rounding-tolerance-must-be-1e-9-not-1e-6` | T1: tight tolerance for floating-point round-trip; loose tolerance would falsely match non-10-power factors |
| `cached-derived-format-metadata-requires-public-overload-contract` | T2: When high-frequency hot path caches derived format metadata (digits), the public formatter must expose an overload accepting pre-resolved metadata, with overload contract documented in spec |
| `contract-migration-tests-must-change-fixture-not-assertion-when-derived-formatting-changes` | T2: When format contract changes (F2 → factor-derived), update test fixture to match new contract intent (e.g. Factor=0.01 instead of Factor=1.0), don't preserve old assertion |
| `numeric-display-contract-change-needs-explicit-user-review-for-integer-signals` | T2: factor-derived precision for `Factor=1.0` signals renders `"100"` not `"100.00"` — user-visible behavior change that needs explicit user direction, not auto-fix |

## Out of scope (YAGNI)

- No Unit-based precision lookup (mV → 3, etc.)
- No per-user precision override (Settings dialog)
- No Sampling Table changes
- No watch-list column width auto-resize based on digit count
- No BigInteger exact-fraction library
- No public API change beyond `SignalFormatter` static methods

## Next (post-v3.50.6 ship)

- **v3.50.7 vault-only PATCH** — promote NEW 1/3 lessons to 2/3
- **W36 god-class refactor** — StatsViewModel 263 / DbcTokenizer 239 / DbcSendViewModel 238 / AscLocator 225 / RecordService 215
- **v3.50.2 fixture exposure concern** — still deferred
- **Open follow-ups from v3.50.5** — placeholder row ✕ button (user "不改了"), VM playback controls cleanup (kept for tests)