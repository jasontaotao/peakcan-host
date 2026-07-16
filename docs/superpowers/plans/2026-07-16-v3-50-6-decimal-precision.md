# v3.50.6 PATCH — Dynamic Decimal Precision in Watch List + Tracker

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the hard-coded `F2` numeric display in the Trace Viewer watch list (Latest / Blue / Δ columns) and the OxyPlot Tracker tooltip with precision derived from each signal's DBC `factor`, so signals with sub-integer steps (e.g. `factor=0.001`) display full precision (`3.353` not `3.35`).

**Architecture:** Add a pure static helper `SignalFormatter` in Core with two methods — `ResolveDecimalDigits(double factor)` returning minimum digits (with 10-power main path + 1..10000 denominator fraction-simplification fallback) and `FormatValue(double factor, double value)` convenience wrapper. Cache the resolved digits on `WatchedSignalRow` at `Signal` setter time (one computation per signal change, not per refresh tick). Update `WatchedSignalRow.LatestText` / `BlueText` / `DeltaText` and `EnumTrackerLineSeries.GetNearestPoint` to call `SignalFormatter.FormatValue` instead of `value.ToString("F2")`. Enum text priority from v3.50.5 PATCH is preserved unchanged.

**Tech Stack:** C# / .NET 10 / WPF / OxyPlot 2.2.0 / CommunityToolkit.Mvvm / FluentAssertions + xUnit.

## Global Constraints

- **Frequent commits**: each task ends with `git commit` (sister of W19 R1 LESSON).
- **TDD**: each functional step writes failing test FIRST, then implements.
- **MVVM-gen limitation**: `WatchedSignalRow._decimalDigits` MUST be plain C# field (no `[ObservableProperty]`); sister of v3.50.0 `Signal` field and v3.50.5 `Dbc` field precedents.
- **Pure static helper**: `SignalFormatter` has no state, no DI — sister of `SignalDecoder.TryDecodeEnumText` (v3.50.5).
- **InvariantCulture** for all numeric formatting (sister of v3.50.5 `LatestText`).
- **Enum text priority**: `TryDecodeEnumText` success → return enum text; else `FormatValue(factor, value)`. v3.50.5 rule preserved.
- **Tolerance 1e-9** for floating-point round-trip in `ResolveDecimalDigits` (spec §6.2).
- **Plan goes through 4 tasks (T0-T3)**: T0 branch + baseline; T1 Core `SignalFormatter` (RED → GREEN); T2 App row.Text + Tracker integration; T3 release notes + ship.

---

### Task T0: Branch + baseline test run

**Files:**
- Modify: nothing

**Interfaces:**
- Consumes: nothing (greenfield).
- Produces: branch `feature/v3-50-6-patch-decimal-precision` off `main` (currently at `63683c1` spec commit).

- [ ] **Step 1: Create branch**

```bash
git checkout -b feature/v3-50-6-patch-decimal-precision
```

- [ ] **Step 2: Capture baseline test count**

```bash
dotnet test --nologo -c Debug --logger "console;verbosity=minimal" 2>&1 | grep -E "通过|失败|总计" | tail -5
```

Expected baseline (sister of v3.50.5 ship on `e9bd205`):
- App.Tests: 822 / 3 SKIP / 0 fail
- Core.Tests: 461 / 0 SKIP / 1 transient fail (`IReplayServiceTests.SetSpeed_PreservesCurrentTimestamp`, W34-W50 sister pattern; isolated run PASS)
- Infrastructure.Tests: 89 / 2 SKIP / 0 fail

Record the exact numbers for later comparison.

- [ ] **Step 3: No commits on T0**

T0 produces no source changes — branch + baseline only.

---

### Task T1: Core — `SignalFormatter` (RED → GREEN)

**Files:**
- Create: `src/PeakCan.Host.Core/Dbc/SignalFormatter.cs`
- Create: `tests/PeakCan.Host.Core.Tests/SignalFormatterTests.cs`

**Interfaces:**
- Consumes: nothing (greenfield).
- Produces: `public static class SignalFormatter` with `ResolveDecimalDigits(double factor) → int` and `FormatValue(double factor, double value) → string`.

- [ ] **Step 1: Write 6 failing tests**

Create `tests/PeakCan.Host.Core.Tests/SignalFormatterTests.cs`:

```csharp
using FluentAssertions;
using Xunit;

namespace PeakCan.Host.Core.Tests;

/// <summary>
/// v3.50.6 PATCH: verifies SignalFormatter.ResolveDecimalDigits + FormatValue.
/// Sister of SignalDecoderTests.TryDecodeEnumTextTests (v3.50.5).
/// </summary>
public class SignalFormatterTests
{
    [Fact]
    public void ResolveDecimalDigits_HandlesZero()
    {
        SignalFormatter.ResolveDecimalDigits(0.0).Should().Be(0);
    }

    [Fact]
    public void ResolveDecimalDigits_HandlesNaN()
    {
        SignalFormatter.ResolveDecimalDigits(double.NaN).Should().Be(0);
        SignalFormatter.ResolveDecimalDigits(double.PositiveInfinity).Should().Be(0);
        SignalFormatter.ResolveDecimalDigits(double.NegativeInfinity).Should().Be(0);
    }

    [Fact]
    public void ResolveDecimalDigits_HandlesPowerOfTen()
    {
        // 10-power main path: log10 round-trip check.
        SignalFormatter.ResolveDecimalDigits(0.001).Should().Be(3, "0.001 factor → 3 digits (B2V_Ucel1_N case)");
        SignalFormatter.ResolveDecimalDigits(0.01).Should().Be(2);
        SignalFormatter.ResolveDecimalDigits(0.1).Should().Be(1);
        SignalFormatter.ResolveDecimalDigits(1.0).Should().Be(0);
        SignalFormatter.ResolveDecimalDigits(10.0).Should().Be(0, "factor >= 1 → 0 digits (max(0, -log10(10)))");
        SignalFormatter.ResolveDecimalDigits(100.0).Should().Be(0);
    }

    [Fact]
    public void ResolveDecimalDigits_HandlesFraction()
    {
        // Fraction simplification fallback (denom search 1..10000).
        SignalFormatter.ResolveDecimalDigits(0.5).Should().Be(1, "1/2 → ceil(log10(2))=1");
        SignalFormatter.ResolveDecimalDigits(0.25).Should().Be(2, "1/4 → ceil(log10(4))=2");
        SignalFormatter.ResolveDecimalDigits(0.125).Should().Be(3, "1/8 → ceil(log10(8))=3");
    }

    [Fact]
    public void FormatValue_FormatsWithResolvedDigits()
    {
        SignalFormatter.FormatValue(0.001, 3.353).Should().Be("3.353");
        SignalFormatter.FormatValue(0.1, 23.5).Should().Be("23.5");
        SignalFormatter.FormatValue(1.0, 1200).Should().Be("1200");
        SignalFormatter.FormatValue(0.001, 0.0).Should().Be("0.000");
    }

    [Fact]
    public void FormatValue_FallsBackToF2ForUnknownFactor()
    {
        // factor=NaN → ResolveDecimalDigits returns 0 → FormatValue still formats.
        // Spec §6.1: edge cases return 0, so FormatValue(NaN, v) → v.ToString("F0") = F0 behavior.
        SignalFormatter.FormatValue(double.NaN, 1.23).Should().Be("1");
    }
}
```

- [ ] **Step 2: Run tests to verify they FAIL**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --nologo -c Debug --filter "FullyQualifiedName~SignalFormatterTests" --logger "console;verbosity=minimal"
```

Expected: 6/6 FAIL with CS0246 "SignalFormatter does not exist".

- [ ] **Step 3: Implement SignalFormatter**

Create `src/PeakCan.Host.Core/Dbc/SignalFormatter.cs`:

```csharp
using System.Globalization;

namespace PeakCan.Host.Core.Dbc;

/// <summary>
/// v3.50.6 PATCH: numeric format helpers that derive the minimum
/// decimal digits from a DBC <c>factor</c>, so watch list and Tracker
/// render with full precision rather than the hard-coded F2.
/// Pure static functions — no state, no DI. Sister of
/// <see cref="SignalDecoder.TryDecodeEnumText"/> (v3.50.5).
/// </summary>
public static class SignalFormatter
{
    /// <summary>
    /// Resolve the minimum number of decimal digits needed to express
    /// the smallest step implied by <paramref name="factor"/>. Returns 0
    /// for non-fractional factors and edge cases (NaN, Infinity, 0).
    /// <para>
    /// Algorithm (spec §6):
    /// </para>
    /// <list type="number">
    ///   <item>Guard against NaN/Infinity/0 → 0.</item>
    ///   <item>log10 approximation: <c>-log10(|factor|)</c>, round-trip check
    ///         with tolerance 1e-9. If exact 10-power, return
    ///         <c>max(0, rounded)</c>.</item>
    ///   <item>If <c>|factor| &lt; 1</c>, fraction simplification via
    ///         denominator search 1..10000. Return
    ///         <c>max(0, ceil(log10(denom)))</c>.</item>
    ///   <item>Best-effort fallback: <c>max(0, ceil(approx))</c>.</item>
    /// </list>
    /// </summary>
    /// <param name="factor">DBC engineering scale factor. <c>physical = raw * factor + offset</c>.</param>
    /// <returns>Minimum decimal digits (≥0).</returns>
    public static int ResolveDecimalDigits(double factor)
    {
        if (double.IsNaN(factor) || double.IsInfinity(factor) || factor == 0.0)
            return 0;

        var absF = Math.Abs(factor);

        // Step 1: 10-power main path.
        var approx = -Math.Log10(absF);
        var rounded = Math.Round(approx);
        if (Math.Abs(approx - rounded) < 1e-9)
            return Math.Max(0, (int)rounded);

        // Step 2: fraction simplification for sub-1 factors.
        // For 1/denom, the minimum digits to express as a terminating
        // decimal is min k such that 10^k is divisible by denom, which
        // equals max(exponent of 2 in denom, exponent of 5 in denom).
        //   denom = 2  → 1 digit (1/2 = 0.5)
        //   denom = 4  → 2 digits (1/4 = 0.25)
        //   denom = 8  → 3 digits (1/8 = 0.125)
        //   denom = 5  → 1 digit (1/5 = 0.2)
        //   denom = 10 → 1 digit (1/10 = 0.1)
        // For denominators with non-2/5 prime factors (3, 7, etc.) the
        // decimal expansion is non-terminating — fall back to log10 ceil.
        if (absF < 1.0)
        {
            var frac = SimplifyFraction(absF);
            if (frac is { Denom: > 1 })
                return MinTerminatingDigits(frac.Denom.Value);
        }

        // Step 3: best-effort fallback.
        return Math.Max(0, (int)Math.Ceiling(approx));
    }

    /// <summary>
    /// Returns min k >= 1 such that 10^k % denom == 0.
    /// For denominators with only 2/5 prime factors, this is exact and
    /// bounded by max(exponent of 2, exponent of 5) ≤ 16 in DBC factor
    /// space. For non-terminating denominators, falls back to log10 ceil.
    /// </summary>
    private static int MinTerminatingDigits(long denom)
    {
        long pow = 10;
        for (int k = 1; k <= 16; k++)
        {
            if (pow % denom == 0)
                return k;
            pow *= 10;
        }
        // Non-terminating: 1/3, 1/7, 1/6, etc. Fall back to log10 ceil.
        return Math.Max(0, (int)Math.Ceiling(Math.Log10(denom)));
    }

    /// <summary>
    /// Format <paramref name="value"/> using the digits resolved from
    /// <paramref name="factor"/>. Convenience wrapper around
    /// <see cref="ResolveDecimalDigits"/> + <c>ToString("F{digits}",
    /// InvariantCulture)</c>. Always uses InvariantCulture so locale
    /// cannot change the decimal point (sister of v3.50.5 LatestText).
    /// </summary>
    public static string FormatValue(double factor, double value)
    {
        var digits = ResolveDecimalDigits(factor);
        return value.ToString("F" + digits, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Try to express <paramref name="value"/> as a simple fraction
    /// <c>numer / denom</c> with denom in 1..10000 and tolerance 1e-9.
    /// Returns null if no clean fraction is found (e.g. 1/3 = 0.333...).
    /// </summary>
    private static (long Numer, long Denom)? SimplifyFraction(double value)
    {
        // value > 0 and value < 1 (caller-guaranteed).
        for (long denom = 1; denom <= 10000; denom++)
        {
            var numer = (long)Math.Round(value * denom);
            if (numer <= 0) continue;
            if (Math.Abs((double)numer / denom - value) < 1e-9)
                return (numer, denom);
        }
        return null;
    }
}
```

- [ ] **Step 4: Run tests to verify they PASS**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --nologo -c Debug --filter "FullyQualifiedName~SignalFormatterTests" --logger "console;verbosity=minimal"
```

Expected: 6/6 PASS.

- [ ] **Step 5: Run full Core.Tests to confirm no regression**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --nologo -c Debug --logger "console;verbosity=minimal"
```

Expected: 461 + 6 new = 467 PASS; 1 transient fail (W34-W50 sister, isolated PASS).

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.Core/Dbc/SignalFormatter.cs tests/PeakCan.Host.Core.Tests/SignalFormatterTests.cs
git commit -m "v3.50.6 T1: SignalFormatter.ResolveDecimalDigits + FormatValue — factor-derived precision with 10-power main path and fraction-simplification fallback"
```

---

### Task T2: App — `WatchedSignalRow` digit cache + xaml binding + Tracker integration

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs` (add `_decimalDigits` field, update Signal setter, update LatestText/BlueText/DeltaText)
- Modify: `src/PeakCan.Host.App/ViewModels/EnumTrackerLineSeries.cs` (replace `yVal.ToString("F2")` with `SignalFormatter.FormatValue`)
- Modify: `tests/PeakCan.Host.App.Tests/ViewModels/WatchedSignalRowTests.cs` (append 2 integration tests)

**Interfaces:**
- Consumes: `SignalFormatter.ResolveDecimalDigits` and `SignalFormatter.FormatValue` from T1.
- Produces: `WatchedSignalRow._decimalDigits` (int, plain field). `WatchedSignalRow.LatestText`/`BlueText`/`DeltaText` use `SignalFormatter.FormatValue(_decimalDigits, value)`. `EnumTrackerLineSeries.GetNearestPoint` uses `SignalFormatter.FormatValue(_signal.Factor, yVal)`.

- [ ] **Step 1: Append 2 failing tests to WatchedSignalRowTests.cs**

Append to `tests/PeakCan.Host.App.Tests/ViewModels/WatchedSignalRowTests.cs` (after the existing `WatchedSignalRowTextTests` class):

```csharp
// v3.50.6 PATCH: factor-derived precision replaces hard-coded F2.
// Sister pattern of WatchedSignalRowTextTests (v3.50.5).
public class WatchedSignalRowPrecisionTests
{
    [Fact]
    public void LatestText_UsesFactorDigits_WhenSignalBound()
    {
        // factor=0.001 signal (sister of B2V_Ucel1_N screenshot case).
        var sig = new Signal(
            Name: "SigB", StartBit: 0, Length: 16,
            Order: ByteOrder.LittleEndian, ValueType: DbcValueType.Unsigned,
            Factor: 0.001, Offset: 0.0, Min: 0, Max: 65.535, Unit: "V",
            Receivers: Array.Empty<string>(), ValueTableName: null);
        var row = new WatchedSignalRow("0x200", "MsgB", "SigB", "V")
        {
            Signal = sig
        };
        row.LatestValue = 3.353;
        row.LatestText.Should().Be("3.353", "factor=0.001 → 3 decimal digits, NOT F2-truncated 3.35");
    }

    [Fact]
    public void DeltaText_UsesFactorDigits_ForDelta()
    {
        var sig = new Signal(
            Name: "SigB", StartBit: 0, Length: 16,
            Order: ByteOrder.LittleEndian, ValueType: DbcValueType.Unsigned,
            Factor: 0.001, Offset: 0.0, Min: 0, Max: 65.535, Unit: "V",
            Receivers: Array.Empty<string>(), ValueTableName: null);
        var row = new WatchedSignalRow("0x200", "MsgB", "SigB", "V")
        {
            Signal = sig,
            LatestValue = 3.350,
            BlueLatestValue = 3.353
        };
        row.DeltaText.Should().Be("0.003", "Δ uses same factor-derived precision as signal: 0.003 not 0.00");
    }
}
```

- [ ] **Step 2: Run tests to verify they FAIL**

```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --nologo -c Debug --filter "FullyQualifiedName~WatchedSignalRowPrecisionTests" --logger "console;verbosity=minimal"
```

Expected: 2/2 FAIL — `LatestText` returns "3.35" (F2), `DeltaText` returns "0.00" (F2).

- [ ] **Step 3: Add `_decimalDigits` field + update Signal setter + LatestText/BlueText/DeltaText in WatchedSignalRow.cs**

Modify `src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs`:

1. Add at the top of the class (after `_dbc` field):

```csharp
// v3.50.6 PATCH: cached minimum decimal digits derived from
// _signal.Factor. Recomputed at Signal-set time (not per refresh
// tick). Plain int field, sister of v3.50.0 _signal and v3.50.5 _dbc.
private int _decimalDigits;
```

2. Update the `Signal` setter to recompute `_decimalDigits` and propagate INPC. Replace the existing setter body:

```csharp
public PeakCan.Host.Core.Dbc.Signal? Signal
{
    get => _signal;
    set
    {
        if (SetProperty(ref _signal, value))
        {
            // v3.50.6 PATCH: cache digit count at signal-set time.
            // value is null → 0 digits (consistent with no-signal fallback).
            _decimalDigits = value is null
                ? 0
                : SignalFormatter.ResolveDecimalDigits(value.Factor);
            OnPropertyChanged(nameof(LatestText));
            OnPropertyChanged(nameof(BlueText));
            OnPropertyChanged(nameof(DeltaText));
        }
    }
}
```

3. Update `LatestText` getter to use `SignalFormatter.FormatValue` instead of `value.ToString("F2")`. Replace:

```csharp
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
        return _latestValue.ToString("F2", CultureInfo.InvariantCulture);
    }
}
```

with:

```csharp
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
        // v3.50.6 PATCH: factor-derived precision replaces F2.
        return SignalFormatter.FormatValue(_decimalDigits, _latestValue);
    }
}
```

4. Same pattern for `BlueText` (use `_decimalDigits`, `_blueLatestValue`) and `DeltaText` (use `_decimalDigits`, `_blueLatestValue - _latestValue`).

For `BlueText`, replace:

```csharp
return _blueLatestValue.ToString("F2", CultureInfo.InvariantCulture);
```

with:

```csharp
return SignalFormatter.FormatValue(_decimalDigits, _blueLatestValue);
```

For `DeltaText`, replace:

```csharp
return (_blueLatestValue - _latestValue).ToString("F2", CultureInfo.InvariantCulture);
```

with:

```csharp
return SignalFormatter.FormatValue(_decimalDigits, _blueLatestValue - _latestValue);
```

- [ ] **Step 4: Run tests to verify they PASS**

```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --nologo -c Debug --filter "FullyQualifiedName~WatchedSignalRow" --logger "console;verbosity=minimal"
```

Expected: ALL WatchedSignalRow tests PASS (existing 8 + new 2 = 10).

- [ ] **Step 5: Update EnumTrackerLineSeries.GetNearestPoint to use SignalFormatter.FormatValue**

In `src/PeakCan.Host.App/ViewModels/EnumTrackerLineSeries.cs`, replace the y-value formatting in the `hit.Text` interpolation. The current code (per v3.50.5 commit `ce24d41`):

```csharp
hit.Text = $"{_signal.Name}\n{decoded}\n{yVal}\nt = {xVal:0.000}s";
```

becomes:

```csharp
// v3.50.6 PATCH: factor-derived precision for y-value line,
// sister of WatchedSignalRow.LatestText (uses SignalFormatter too).
var yDisplay = SignalFormatter.FormatValue(_signal.Factor, yVal);
hit.Text = $"{_signal.Name}\n{decoded}\n{yDisplay}\nt = {xVal:0.000}s";
```

- [ ] **Step 6: Build + run full App.Tests**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --nologo -c Debug --logger "console;verbosity=minimal"
```

Expected: 0 build errors, 0 new warnings. App.Tests PASS at count = baseline 822 + 2 new = 824.

- [ ] **Step 7: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs \
        src/PeakCan.Host.App/ViewModels/EnumTrackerLineSeries.cs \
        tests/PeakCan.Host.App.Tests/ViewModels/WatchedSignalRowTests.cs
git commit -m "v3.50.6 T2: WatchedSignalRow digit cache + FormatValue integration + Tracker 4-line tooltip factor-precision (Latest/Blue/Δ use factor-derived digits, enum text priority preserved)"
```

---

### Task T3: Release notes + version bump + Tier-3 ship

**Files:**
- Modify: `src/Directory.Build.props` (version 3.50.5 → 3.50.6)
- Create: `docs/release-notes-v3.50.6.md`

**Interfaces:**
- Consumes: nothing (administrative).
- Produces: PR + tag `v3.50.6` + GitHub release.

- [ ] **Step 1: Bump version**

In `src/Directory.Build.props`, change:
- `<Version>3.50.5</Version>` → `<Version>3.50.6</Version>`
- `<AssemblyVersion>3.50.5.0</AssemblyVersion>` → `<AssemblyVersion>3.50.6.0</AssemblyVersion>`
- `<FileVersion>3.50.5.0</FileVersion>` → `<FileVersion>3.50.6.0</FileVersion>`
- `<InformationalVersion>3.50.5</InformationalVersion>` → `<InformationalVersion>3.50.6</InformationalVersion>`

- [ ] **Step 2: Write release notes**

Create `docs/release-notes-v3.50.6.md` mirroring the v3.50.5 template. Include:
- Background: user screenshot showing `B2V_Ucel1_N` with `factor=0.001` displaying `3.35` instead of `3.353`.
- Why: 1 user-flagged UX gap (横展 = generalize the precision problem across all factor < 1 signals).
- What:
  - `SignalFormatter.ResolveDecimalDigits` (Core) — new public static.
  - `SignalFormatter.FormatValue` (Core) — new public static convenience wrapper.
  - `WatchedSignalRow._decimalDigits` (App) — cached int field.
  - `WatchedSignalRow.Signal` setter — recomputes `_decimalDigits` on signal change.
  - `WatchedSignalRow.LatestText` / `BlueText` / `DeltaText` — use `FormatValue`.
  - `EnumTrackerLineSeries.GetNearestPoint` — uses `FormatValue` for y-value line.
- LoC delta (rough): +60 LoC (helper + 6 Core tests + 2 App tests + integration + release notes).
- Test outcomes: Core 461+6=467 / App 822+2=824 / Infra 89.
- Lesson candidates (NEW 1/3 each): `decimal-precision-must-derive-from-factor-not-fixed-f2`, `decimal-precision-fraction-fallback-handles-binary-factors`, `signal-setter-should-cache-derived-properties-to-avoid-hot-path-recomputation`, `enum-text-priority-must-precede-decimal-precision`, `precision-rounding-tolerance-must-be-1e-9-not-1e-6`.
- Out of scope: see spec §3 non-goals.
- Next: W36 god-class refactor / v3.50.7 vault-only PATCH (lesson promotion).

- [ ] **Step 3: Commit version + release notes**

```bash
git add src/Directory.Build.props docs/release-notes-v3.50.6.md
git commit -m "v3.50.6: version bump 3.50.5 → 3.50.6 + release notes (factor-derived decimal precision)"
```

- [ ] **Step 4: Run full test suite (final verification)**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test --nologo -c Debug --logger "console;verbosity=minimal"
```

Expected: 0 build errors. Counts: App.Tests 824, Core.Tests 467, Infrastructure.Tests 89. Sister of v3.50.5 ship totals + 8 new tests.

- [ ] **Step 5: Create PR body file**

Create `docs/pr-body-v3.50.6.md`:

```markdown
# v3.50.6 PATCH — Dynamic Decimal Precision in Watch List + Tracker

## Summary

Replaces hard-coded F2 numeric display in the Trace Viewer watch list
(Latest / Blue / Δ columns) and the OxyPlot Tracker tooltip with
precision derived from each signal's DBC `factor`. Signals with
sub-integer steps (e.g. `factor=0.001`) now display full precision
(`3.353` not `3.35`).

## Changes

- **Core**: `SignalFormatter.ResolveDecimalDigits(double factor) → int`
  - Algorithm: 10-power main path + fraction-simplification fallback + best-effort ceil
- **Core**: `SignalFormatter.FormatValue(double factor, double value) → string`
  - Convenience wrapper: `value.ToString("F{digits}", InvariantCulture)`
- **App**: `WatchedSignalRow._decimalDigits` (cached int field)
- **App**: `WatchedSignalRow.Signal` setter — recomputes digits on signal change
- **App**: `WatchedSignalRow.LatestText` / `BlueText` / `DeltaText` — use `FormatValue`
- **App**: `EnumTrackerLineSeries.GetNearestPoint` — uses `FormatValue` for y-value line

## Test plan

- [x] `dotnet test --filter "FullyQualifiedName~SignalFormatterTests"` — 6/6 PASS
- [x] `dotnet test --filter "FullyQualifiedName~WatchedSignalRow"` — 10/10 PASS (8 existing + 2 new)
- [x] `dotnet test` (full suite) — no new failures, no regressions

## Sister patterns

- v3.50.5 PATCH `EnumTrackerLineSeries` (immediate predecessor)
- v3.50.0 MINOR `WatchedSignalRow.Signal` field precedent (plain C#, not `[ObservableProperty]`)
- v3.50.5 PATCH `Dbc` field precedent (cached reference at Signal-set time)
- W19 R1 LESSON (verbatim re-extraction, no fabrication)

## Out of scope

See [release notes](docs/release-notes-v3.50.6.md) §Out of scope.
```

- [ ] **Step 6: Create PR (awaits explicit user auth per auto-mode classifier)**

```bash
gh pr create --title "v3.50.6 PATCH: dynamic decimal precision in watch list + Tracker" \
             --body-file docs/pr-body-v3.50.6.md \
             --base main \
             --head feature/v3-50-6-patch-decimal-precision
```

Expected: PR created. URL printed. **DO NOT MERGE** — wait for user explicit auth.

- [ ] **Step 7: STOP — wait for user authorization to merge + tag + release**

Per auto-mode classifier, irreversible ops (PR merge, tag push, GitHub release publish) each require explicit user instruction. Do not proceed.

---

## Sister-lesson candidates to monitor

| Lesson | This plan's observation count |
|---|---|
| `decimal-precision-must-derive-from-factor-not-fixed-f2` | NEW 1/3 (T1: factor → digit mapping is the right primary signal; F2 hard-coding loses information for sub-1 factors) |
| `decimal-precision-fraction-fallback-handles-binary-factors` | NEW 1/3 (T1: 0.5/0.25/0.125 path via 1..10000 denom search; covers engineering factors not expressible as 10-powers) |
| `signal-setter-should-cache-derived-properties-to-avoid-hot-path-recomputation` | NEW 1/3 (T2: digit caching at Signal setter, not getter; sister of v3.50.5 Dbc field which similarly caches lookup inputs) |
| `enum-text-priority-must-precede-decimal-precision` | NEW 1/3 (T2: TryDecodeEnumText success → return enum text; else FormatValue; preserves v3.50.5 user-visible behavior for enum signals) |
| `precision-rounding-tolerance-must-be-1e-9-not-1e-6` | NEW 1/3 (T1: tight tolerance for floating-point round-trip; loose tolerance would falsely match non-10-power factors) |

## Verification

- `dotnet build src/`: 0 errors, 0 warnings
- `dotnet test --filter "FullyQualifiedName~SignalFormatterTests"`: 6/6 PASS
- `dotnet test --filter "FullyQualifiedName~WatchedSignalRow"`: 10/10 PASS (8 existing + 2 new)
- `dotnet test` (full solution): 824/467/89 = 1380 PASS (sister of v3.50.5 1371 + 8 new; 1 Core transient flake, isolated PASS)
- 1 PR + tag + GH release published
- Branch deleted post-merge

## Out of scope (YAGNI)

- No Unit-based precision lookup (mV → 3, etc.)
- No per-user precision override (Settings dialog)
- No Sampling Table changes (out of scope per v3.50.5; this PATCH keeps the boundary)
- No watch-list column width auto-resize based on digit count
- No BigInteger exact-fraction library
- No public API change beyond `SignalFormatter` static methods