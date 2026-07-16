# v3.50.6 PATCH — Decimal Precision in Watch List + Tracker

> **Status**: Design — pending user approval.

**Goal:** Replace the hard-coded `F2` numeric display in the Trace Viewer watch list (Latest / Blue / Δ columns) and the OxyPlot Tracker tooltip with dynamic precision derived from each signal's DBC `factor`, so signals with sub-integer steps (e.g. `factor=0.001` → 3.353 V) display with full precision instead of being rounded to two decimals.

**Architecture:** Add a pure static helper `SignalFormatter` in the Core layer with two methods: `ResolveDecimalDigits(double factor)` returning the minimum digits needed to express the factor's smallest step, and `FormatValue(double factor, double value)` formatting a decoded value with that precision. Cache the resolved digits on `WatchedSignalRow` at `Signal` setter time (one computation per signal change, not per frame). Update `WatchedSignalRow.LatestText` / `BlueText` / `DeltaText` and `EnumTrackerLineSeries.GetNearestPoint` to call `SignalFormatter.FormatValue` instead of `value.ToString("F2")`. Enum text priority from v3.50.5 is preserved unchanged.

**Tech Stack:** C# / .NET 10 / WPF / OxyPlot 2.2.0 / CommunityToolkit.Mvvm / FluentAssertions + xUnit.

## 1. Background & motivation

User feedback 2026-07-16 (screenshot of `B2V_Ucel1_N` signal):
- DBC factor = `0.001`, meaning each integer step on the wire corresponds to 1 mV.
- Actual decoded value 3.353 V is displayed as **"3.35"** because `LatestText` uses `_latestValue.ToString("F2", InvariantCulture)`.
- 3.353 vs 3.355 are visually indistinguishable ("3.35" both ways).
- The "横展此类问题" direction generalizes this: every signal with `factor < 1` loses precision at F2.

The v3.50.5 PATCH fixed display of enum text but left numeric formatting at F2. The natural next PATCH addresses the parallel precision issue.

## 2. Goals (in scope)

- **G1**: A signal with `factor = 0.001` displays its decoded value with 3 decimal digits (e.g. `3.353`).
- **G2**: A signal with `factor = 0.1` displays with 1 digit (e.g. `23.5`).
- **G3**: A signal with `factor = 1` (integer step) displays with 0 digits (e.g. `1200`, not `1200.00`).
- **G4**: A signal with `factor = 0.5` or `0.25` (binary fraction) displays with the correct digits to express the smallest step (1 digit for 0.5, 2 digits for 0.25).
- **G5**: The `Δ` column uses the same precision as the signal — so `3.353 - 3.350` displays as `0.003` (3 digits), not `0.00`.
- **G6**: The OxyPlot Tracker tooltip displays the `yValue` line with the same precision as the watch list.
- **G7**: Enum text display from v3.50.5 remains unchanged in priority order: DBC VAL_ table text > factor-derived numeric > fallback F2 (no Signal bound).
- **G8**: Performance: the digit computation runs once per signal change (cached on the row), not per refresh tick.

## 3. Non-goals (YAGNI)

- **N1**: No per-user precision override (Settings dialog). User accepts auto-derive from factor.
- **N2**: No Unit-based precision lookup table (e.g. `mV` → 3). The factor alone is sufficient.
- **N3**: No Sampling Table changes. (v3.50.5 already excluded sampling panel; v3.50.6 keeps it.)
- **N4**: No watch-list column width auto-resize based on digit count.
- **N5**: No `BigInteger` exact-fraction library; tolerance-based algorithm is sufficient for DBC factor space.
- **N6**: No public API change beyond `SignalFormatter` static methods (one new type, no breaking changes).
- **N7**: No precision override at the per-row level.

## 4. Architecture

```
Core layer (no UI dep):
  SignalFormatter (src/PeakCan.Host.Core/Dbc/SignalFormatter.cs) [NEW]
    + public static int ResolveDecimalDigits(double factor)
        // Algorithm (see §6):
        //   1. Guard against NaN/Infinity/0 → return 0.
        //   2. log10 approximation: -log10(|factor|), round-trip check (tolerance 1e-9).
        //      If exact 10-power: return max(0, rounded).
        //   3. |factor| < 1: fraction simplification via 1..10000 denom search.
        //      Return max(0, ceil(log10(denom))).
        //   4. Fallback: return max(0, ceil(approx)).
    + public static string FormatValue(double factor, double value)
        // Convenience wrapper: ResolveDecimalDigits(factor) → "F{digits}".
        // Uses InvariantCulture (sister of v3.50.5 LatestText).
    + private static (long Numer, long Denom)? SimplifyFraction(double value)
        // Try denominators 1..10000, return first (numer, denom) with |numer/denom - value| < 1e-9.
        // Returns null if no simple fraction.

App layer:
  WatchedSignalRow (src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs)
    + private int _decimalDigits = 0  // plain field (mvvm-gen limitation, sister of Dbc field)
    + Signal setter (existing): after SetProperty success
        → if value != null: _decimalDigits = SignalFormatter.ResolveDecimalDigits(value.Factor)
        → else: _decimalDigits = 0
        + OnPropertyChanged(nameof(LatestText)) / BlueText / DeltaText (already in v3.50.5)
    + LatestText getter (modified): after enum text lookup misses
        → return SignalFormatter.FormatValue(_decimalDigits, _latestValue)
        // Note: caller (SignalDecoder.TryDecodeEnumText) still uses the
        // raw decoded value, NOT _decimalDigits. Enum path unchanged.
    + BlueText / DeltaText: same pattern.

  EnumTrackerLineSeries (src/PeakCan.Host.App/ViewModels/EnumTrackerLineSeries.cs)
    + GetNearestPoint override (existing from v3.50.5): y-value line
        → replaces: yVal.ToString("F2", InvariantCulture)
        → with:     SignalFormatter.FormatValue(_signal.Factor, yVal)
```

### 4.1 Caching strategy

The `Signal` setter on `WatchedSignalRow` already triggers `INPC` for `LatestText` / `BlueText` / `DeltaText` (v3.50.5 PATCH). v3.50.6 piggy-backs the digit recomputation on that same setter call. The drag-hot-path reads `_decimalDigits` (an `int` field) — zero allocation, no `Math.Log10` in the getter.

## 5. Data flow

### 5.1 Watch-list refresh on green-line drag (numeric signal)

```
1. User drags green line to X = 615 s.
2. TraceViewerViewModel.RefreshAtAnchor:
     foreach row in WatchedSignals:
       row.LatestValue = SignalDecoder.Decode(frame.Data, row.Signal)
3. WatchedSignalRow.LatestValue setter:
     → SetProperty → OnPropertyChanged(nameof(LatestText))
4. WatchedSignalRow.LatestText getter:
     → IsPlaceholder || NaN? → "—"
     → Signal + Dbc?
       → text = TryDecodeEnumText(sig, v, dbc)
       → if text != null → return text (enum path, v3.50.5 priority)
     → return SignalFormatter.FormatValue(_decimalDigits, _latestValue)
5. DataGrid renders "3.353" (factor=0.001, 3 digits).
```

### 5.2 Tracker hover (numeric signal)

```
1. User hovers over a chart line.
2. OxyPlot PlotController → LineSeries.GetNearestPoint.
3. EnumTrackerLineSeries.GetNearestPoint override:
     hit = base.GetNearestPoint(point, interpolate)
     decoded = TryDecodeEnumText(sig, hit.Y, dbc) ?? hit.Y.ToString("F2")
     hit.Text = $"{name}\n{decoded}\n{SignalFormatter.FormatValue(_signal.Factor, hit.Y)}\nt = {x:0.000}s"
4. TrackerControl displays:
     B2V_Ucel1_N
     —
     3.353
     t = 155615.585s
```

### 5.3 Δ column (numeric signal)

```
WatchedSignalRow.DeltaText getter:
  NaN? → "—"
  Signal.ValueTableName != null? → "—"  // enum Δ rule (v3.50.5 spec §G4)
  return SignalFormatter.FormatValue(_decimalDigits, _blueLatestValue - _latestValue)
```

For `factor=0.001`, `_decimalDigits=3`, diff=0.003 → renders "0.003".

## 6. Algorithm: ResolveDecimalDigits

```
ResolveDecimalDigits(double factor):
  if double.IsNaN(factor) || double.IsInfinity(factor) || factor == 0:
    return 0
  var absF = Math.Abs(factor)

  // Step 1: 10-power main path.
  var approx = -Math.Log10(absF)
  var rounded = Math.Round(approx)
  if Math.Abs(approx - rounded) < 1e-9:
    return Math.Max(0, (int)rounded)

  // Step 2: fraction simplification for non-10-power small factors.
  // For 1/denom, the minimum digits to express as a terminating decimal
  // is min k such that 10^k is divisible by denom, which equals
  // max(exponent of 2 in denom, exponent of 5 in denom).
  //   denom = 2  → 1 digit (1/2 = 0.5)
  //   denom = 4  → 2 digits (1/4 = 0.25)
  //   denom = 8  → 3 digits (1/8 = 0.125)
  //   denom = 5  → 1 digit (1/5 = 0.2)
  //   denom = 10 → 1 digit (1/10 = 0.1)
  // For denominators with non-2/5 prime factors (e.g. 3, 7) the decimal
  // expansion is non-terminating — fall back to log10 ceil.
  if absF < 1:
    var frac = SimplifyFraction(absF)
    if frac is { Denom: > 1 }:
      return MinTerminatingDigits(frac.Denom.Value)

  // Step 3: best-effort fallback.
  return Math.Max(0, (int)Math.Ceiling(approx))

// Returns min k >= 1 such that 10^k % denom == 0.
// For non-terminating denominators (containing 3, 7, etc.) returns ceil(log10(denom)).
MinTerminatingDigits(long denom):
  for k = 1 to 16:
    if ModPow(10, k, denom) == 0:
      return k
  return Math.Max(0, (int)Math.Ceiling(Math.Log10(denom)))

SimplifyFraction(double value):
  // value > 0, value < 1.
  for denom = 1 to 10000:
    var numer = (long)Math.Round(value * denom)
    if numer <= 0: continue
    if Math.Abs((double)numer / denom - value) < 1e-9:
      return (numer, denom)
  return null
```

### 6.1 Expected behavior table

| `factor` | expected digits | algorithm path |
|---|---|---|
| `1.0` | 0 | Step 1: log10(1)=0, round-trip exact → 0 |
| `0.1` | 1 | Step 1: log10(0.1)=-1, negate → 1, round-trip exact → 1 |
| `0.01` | 2 | Step 1: log10(0.01)=-2, → 2, round-trip exact → 2 |
| `0.001` | 3 | Step 1: log10(0.001)=-3, → 3, round-trip exact → 3 (B2V_Ucel1_N case) |
| `10.0` | 0 | Step 1: log10(10)=1, → -1, → 0 (max(0,-1)) |
| `0.5` | 1 | Step 2: denom=2 → max(2¹,5⁰)=1 |
| `0.25` | 2 | Step 2: denom=4 → max(2²,5⁰)=2 |
| `0.125` | 3 | Step 2: denom=8 → max(2³,5⁰)=3 |
| `0.0` | 0 | Step 0 guard |
| `NaN` | 0 | Step 0 guard |
| `1/3 ≈ 0.333...` | 1 | Step 3 fallback: ceil(log10(0.333))=1 |

### 6.2 Tolerance rationale

`1e-9` matches DBC parser output precision (DBC factor/offset are written as decimal literals, parsed by `double.TryParse` with default rounding). A factor written as `0.0010000001` (1e-10 drift) would still round-trip as 3 digits. Factors with truly pathological floating-point representation (e.g. `0.1 + 0.2 = 0.30000000000000004`) would be caught by the fraction-simplification fallback (denom=10000, numer=3000000000, ratio=0.3, exact match within 1e-9).

## 7. API contracts

### 7.1 `SignalFormatter` (new)

```csharp
// src/PeakCan.Host.Core/Dbc/SignalFormatter.cs
namespace PeakCan.Host.Core.Dbc;

/// <summary>
/// v3.50.6 PATCH: numeric format helpers that derive the minimum
/// decimal digits from a DBC <c>factor</c>, so watch list and
/// Tracker render with full precision rather than the hard-coded F2.
/// Pure static functions — no state, no DI.
/// </summary>
public static class SignalFormatter
{
    /// <summary>
    /// Resolve the minimum number of decimal digits needed to express
    /// the smallest step implied by <paramref name="factor"/>. Returns 0
    /// for non-fractional factors and edge cases (NaN, Infinity, 0).
    /// </summary>
    public static int ResolveDecimalDigits(double factor);

    /// <summary>
    /// Format <paramref name="value"/> using the digits resolved from
    /// <paramref name="factor"/>. Convenience wrapper around
    /// <see cref="ResolveDecimalDigits"/> + <c>ToString("F{digits}",
    /// InvariantCulture</c>.
    /// </summary>
    public static string FormatValue(double factor, double value);

    /// <summary>
    /// v3.50.6 PATCH: sister overload that accepts pre-resolved
    /// <paramref name="digits"/> directly. Sister callers (e.g.
    /// <c>WatchedSignalRow.LatestText</c>) cache the digit count at
    /// <c>Signal</c> setter time and pass the cached value here to
    /// avoid per-frame <see cref="ResolveDecimalDigits"/> recomputation
    /// in the drag hot path. Behaviorally equivalent to
    /// <c>FormatValue(factor, value)</c> when
    /// <c>digits == ResolveDecimalDigits(factor)</c>; uses
    /// <c>InvariantCulture</c> like the factor-based overload.
    /// Negative <paramref name="digits"/> are treated as 0.
    /// </summary>
    public static string FormatValue(int digits, double value);
}
```

### 7.2 `WatchedSignalRow` modifications

```csharp
// src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs

private int _decimalDigits = 0;

public Signal? Signal
{
    get => _signal;
    set
    {
        if (SetProperty(ref _signal, value))
        {
            // v3.50.6 PATCH: cache digit count at signal-set time.
            _decimalDigits = value is null
                ? 0
                : SignalFormatter.ResolveDecimalDigits(value.Factor);
            OnPropertyChanged(nameof(LatestText));
            OnPropertyChanged(nameof(BlueText));
            OnPropertyChanged(nameof(DeltaText));
        }
    }
}

public string LatestText
{
    get
    {
        if (IsPlaceholder || double.IsNaN(_latestValue)) return DoubleNanToStringConverter.Placeholder;
        if (_signal is not null && _dbc is not null)
        {
            var text = SignalDecoder.TryDecodeEnumText(_signal, _latestValue, _dbc);
            if (text is not null) return text;
        }
        // v3.50.6 PATCH: factor-derived precision replaces hard-coded F2.
        // Uses pre-resolved digits (cached at Signal setter) to avoid
        // per-frame ResolveDecimalDigits calls in the drag hot path.
        return SignalFormatter.FormatValue(_decimalDigits, _latestValue);
    }
}
// BlueText: same pattern, _blueLatestValue.
// DeltaText: same pattern; preserves v3.50.5 enum "—" rule.
```

### 7.3 `EnumTrackerLineSeries` modification

```csharp
// src/PeakCan.Host.App/ViewModels/EnumTrackerLineSeries.cs
public override TrackerHitResult GetNearestPoint(ScreenPoint point, bool interpolate)
{
    var hit = base.GetNearestPoint(point, interpolate);
    if (hit is null) return hit;

    var yVal = hit.DataPoint.Y;
    var xVal = hit.DataPoint.X;
    var dbc = _dbcProvider();
    var decoded = dbc is not null
        ? (SignalDecoder.TryDecodeEnumText(_signal, yVal, dbc)
            ?? yVal.ToString("F2", CultureInfo.InvariantCulture))
        : yVal.ToString("F2", CultureInfo.InvariantCulture);

    // v3.50.6 PATCH: factor-derived precision for y-value line.
    var yDisplay = SignalFormatter.FormatValue(_signal.Factor, yVal);
    hit.Text = $"{_signal.Name}\n{decoded}\n{yDisplay}\nt = {xVal:0.000}s";
    return hit;
}
```

## 8. Test plan

### 8.1 Core.Tests — 6 new unit tests

| Test | Validates |
|---|---|
| `SignalFormatter.ResolveDecimalDigits_HandlesZero` | factor=0 → 0 (guard) |
| `SignalFormatter.ResolveDecimalDigits_HandlesNaN` | factor=NaN → 0 (guard) |
| `SignalFormatter.ResolveDecimalDigits_HandlesPowerOfTen` | 0.001→3, 0.01→2, 0.1→1, 1→0, 10→0 (Step 1) |
| `SignalFormatter.ResolveDecimalDigits_HandlesFraction` | 0.5→1, 0.25→2, 0.125→3 (Step 2) |
| `SignalFormatter.FormatValue_FormatsWithResolvedDigits` | factor=0.001, v=3.353 → "3.353" |
| `SignalFormatter.FormatValue_FallsBackToF2ForUnknownFactor` | factor=NaN → FormatValue(NaN, v) → v.ToString("F2") |

### 8.2 App.Tests — 2 new integration tests

| Test | Validates |
|---|---|
| `WatchedSignalRow.LatestText_UsesFactorDigits_WhenSignalBound` | row.Signal = factor=0.001 sig; LatestValue=3.353 → "3.353" (sister of v3.50.5 enum test) |
| `WatchedSignalRow.DeltaText_UsesFactorDigits_ForDelta` | factor=0.001 signal; LatestValue=3.350, BlueLatestValue=3.353 → "0.003" |

### 8.3 Regression checks

- `dotnet test --filter "FullyQualifiedName~TryDecodeEnumText"` → 4/4 PASS (v3.50.5 unchanged).
- `dotnet test --filter "FullyQualifiedName~WatchedSignalRow"` → all PASS (8 existing + 2 new = 10).
- Full solution `dotnet test` → no new failures.

### 8.4 No new fixtures

Reuse `tests/PeakCan.Host.Core.Tests/Dbc/Fixtures/sample-with-val.dbc` (v3.50.5) for enum-path regression. New tests construct Signal records directly with the desired `Factor` — no DBC fixture expansion needed.

## 9. Risks & mitigations

| Risk | Mitigation |
|---|---|
| Floating-point tolerance (1e-9) misses edge factor representations | DBC parser emits decimal literals; 1e-9 is well below typical parser precision. Step 2 fallback catches truly non-10-power cases. |
| `0.333...` (1/3) fallback to ceil(log10)=1 is slightly imprecise | Acceptable — the alternative (BigInteger exact fractions) is over-engineering for an unlikely DBC factor. |
| Existing `WatchedSignalRow.LatestText_ReturnsNumeric_WhenNotMapped` test (v3.50.5) sets `LatestValue=1.23` with bare signal (no Dbc, no Factor). | v3.50.6 behavior unchanged in this case: `_signal is null or _dbc is null` branch returns `value.ToString("F2")`. Test still passes. |
| `Signal setter` now does extra work (digit computation) | Computation is O(1) for 10-power path; O(denom) for fraction path with denom bounded at 10000. Runs once per signal change, not per refresh tick. |
| Tracker format change may surprise users in v3.50.5 already shipped | Documented in release notes; sister-of-v3.50.5 release note format. |
| `Math.Log10` precision on edge cases (e.g. `0.1 + 0.2`) | Round-trip check + fraction fallback handle all known floating-point edge cases. |

## 10. Out of scope

- Unit-based precision lookup (e.g. `mV` → 3).
- Per-user precision override.
- Sampling Table precision changes (out of scope per v3.50.5; this PATCH keeps the boundary).
- Watch-list column width auto-resize.
- BigInteger exact-fraction library.
- Public API change beyond `SignalFormatter`.

## 11. Sister patterns to monitor

| Lesson candidate | This PATCH observation |
|---|---|
| `decimal-precision-must-derive-from-factor-not-fixed-f2` | NEW 1/3: factor → digit mapping is the right primary signal; F2 hard-coding loses information for sub-1 factors |
| `decimal-precision-fraction-fallback-handles-binary-factors` | NEW 1/3: 0.5/0.25/0.125 path via fraction simplification; covers common engineering factors not expressible as 10-powers |
| `signal-setter-should-cache-derived-properties-to-avoid-hot-path-recomputation` | NEW 1/3: digit caching at Signal setter, not getter; sister of v3.50.5 `Dbc` field which similarly caches lookup inputs |
| `enum-text-priority-must-precede-decimal-precision` | NEW 1/3: DBC VAL_ text > factor-derived numeric > F2 fallback; preserves v3.50.5 user-visible behavior for enum signals |
| `precision-rounding-tolerance-must-be-1e-9-not-1e-6` | NEW 1/3: tight tolerance for floating-point round-trip; loose tolerance would falsely match non-10-power factors |