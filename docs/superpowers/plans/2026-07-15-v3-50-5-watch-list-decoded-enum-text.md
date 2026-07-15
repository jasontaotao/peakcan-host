# v3.50.5 PATCH — Watch List Decoded Enum Text + Tracker 4-line Tooltip

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Render DBC `VAL_` table enum text (e.g. "高压上电") in the Trace Viewer's watch list Latest/Δ/Blue columns and in the OxyPlot Tracker tooltip, falling back to existing F2 numeric display when no VAL_ table is present.

**Architecture:** Extend `SignalDecoder` (Core) with one static lookup method `TryDecodeEnumText`. Add three string computed properties (`LatestText` / `BlueText` / `DeltaText`) plus a `Dbc` field on `WatchedSignalRow` (App). XAML bindings for the three columns switch from the `double` source properties (via `DoubleNanToStr` converter) to the new `string` properties. Chart per-series Tracker is configured via OxyPlot's `LineSeries.TrackerFormatString` + a new `EnumTextTrackerConverter` to emit the 4-line tooltip.

**Tech Stack:** C# / .NET 10 / WPF / OxyPlot 2.2.0 / CommunityToolkit.Mvvm / FluentAssertions + xUnit.

## Global Constraints

- **Frequent commits**: each task ends with a `git commit` (sister of W19 R1 LESSON).
- **TDD**: each functional step writes the failing test FIRST, then implements.
- **WatchedSignalRow MVVM-gen limitation**: CommunityToolkit.Mvvm source-gen emits partial `.g.cs` into the XAML temp csproj which cannot pull `PeakCan.Host.Core.dll`. Therefore the new `Dbc` field/property MUST be plain C# (no `[ObservableProperty]` source-gen) — sister of v3.50.0 MINOR's `Signal` field precedent in the same class.
- **No caching** of enum-text lookups (user direction 2026-07-15; Dictionary O(1) ≈ 50ns × 100 rows × 1kHz = ~5ms/s is in noise floor).
- **Sanitized fixture** (spec §7.3): `tests/PeakCan.Host.Core.Tests/Dbc/Fixtures/sample-with-val.dbc` ≤ 50 lines, generic names (SigA/MsgA), no real CAN IDs / nodes / OEM strings.
- **Public API**: 1 new public static method (`SignalDecoder.TryDecodeEnumText`); no breaking changes to existing methods.
- **No enum/raw toggle** (N1 in spec). No Y-axis color change (N2). No watch-list column restructure (N3). No tracker box restyle (N4).
- **Plan goes through 5 tasks (T0-T4)**: T0 branch + baseline tests; T1 Core enum-text lookup; T2 App row Text properties + xaml bindings; T3 Tracker 4-line tooltip; T4 release notes + ship.

---

### Task T0: Branch + baseline test run

**Files:**
- Create: `tests/PeakCan.Host.Core.Tests/Dbc/Fixtures/sample-with-val.dbc`

**Interfaces:**
- Consumes: nothing (greenfield).
- Produces: branch `feature/v3-50-5-patch-watch-list-decoded-enum` off `main` (currently at `5cf9f0f`).

- [ ] **Step 1: Create branch**

```bash
git checkout -b feature/v3-50-5-patch-watch-list-decoded-enum
```

- [ ] **Step 2: Create sanitized DBC fixture**

Create `tests/PeakCan.Host.Core.Tests/Dbc/Fixtures/sample-with-val.dbc`:

```dbc
VERSION "sanitized-fixture"

NS_ :

BS_:

BO_ 256 MsgA: 8 Vector__XL
 SG_ SigA : 0|2@1+ (1,0) [0|3] "bit" Vector__XL

VAL_ 256 SigA 0 "Zero" 1 "One" 2 "Two" 3 "Three" ;
```

(Strip `BU_` lines per spec §7.3 — keep only the minimum needed for `TryDecodeEnumText` to find a signal + table.)

- [ ] **Step 3: Verify DBC parses (smoke test)**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --nologo --filter "FullyQualifiedName~DbcParser" -c Debug --logger "console;verbosity=minimal"
```
Expected: PASS (existing DbcParser tests). The fixture is unused until T1, but we want to confirm the file content is valid DBC.

- [ ] **Step 4: Capture baseline test count**

```bash
dotnet test --nologo -c Debug --logger "console;verbosity=minimal" | tee /tmp/baseline-tests.log
grep -E "Passed:|Failed:" /tmp/baseline-tests.log | tail -10
```
Expected: 819 + 457 + 89 = 1365 PASS (sister of v3.50.4 ship baseline). Record the exact numbers for later comparison.

- [ ] **Step 5: Commit fixture**

```bash
git add tests/PeakCan.Host.Core.Tests/Dbc/Fixtures/sample-with-val.dbc
git commit -m "v3.50.5 T0: add sanitized DBC fixture (SigA+VAL_ for TryDecodeEnumText tests)"
```

---

### Task T1: Core — `SignalDecoder.TryDecodeEnumText` (RED → GREEN)

**Files:**
- Modify: `src/PeakCan.Host.Core/Dbc/SignalDecoder.cs`
- Modify: `tests/PeakCan.Host.Core.Tests/SignalDecoderTests.cs` (append 4 tests)

**Interfaces:**
- Consumes: sanitized fixture (`sample-with-val.dbc`) loaded via `DbcParser.Parse`.
- Produces: `public static string? SignalDecoder.TryDecodeEnumText(Signal signal, double decodedValue, DbcDocument document)`.

- [ ] **Step 1: Append 4 failing tests to SignalDecoderTests.cs**

Append to `tests/PeakCan.Host.Core.Tests/SignalDecoderTests.cs` (after the closing brace of the existing test class):

```csharp
using System.IO;
using PeakCan.Host.Core.Dbc; // DbcParser lives in Core.Dbc namespace

public class TryDecodeEnumTextTests
{
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "PeakCan.Host.Core.Tests", "Dbc", "Fixtures",
        "sample-with-val.dbc");

    private static DbcDocument LoadFixture()
    {
        var absPath = Path.GetFullPath(FixturePath);
        var text = File.ReadAllText(absPath);
        return DbcParser.Parse(text).GetValueOrThrow();
    }

    [Fact]
    public void TryDecodeEnumText_ReturnsMappedString_WhenValueInTable()
    {
        var doc = LoadFixture();
        var sig = doc.MessagesById[256].Signals[0]; // SigA
        sig.ValueTableName.Should().NotBeNull();
        var text = SignalDecoder.TryDecodeEnumText(sig, 1.0, doc);
        text.Should().Be("One");
    }

    [Fact]
    public void TryDecodeEnumText_ReturnsNull_WhenNoValueTable()
    {
        var doc = LoadFixture();
        // Build a Signal without ValueTableName (the default Signal record param).
        var sigBare = new Signal(
            Name: "BareSig", StartBit: 0, Length: 8,
            Order: ByteOrder.LittleEndian, ValueType: DbcValueType.Unsigned,
            Factor: 1.0, Offset: 0.0, Min: 0, Max: 255, Unit: "",
            Receivers: Array.Empty<string>(), ValueTableName: null);
        SignalDecoder.TryDecodeEnumText(sigBare, 5.0, doc).Should().BeNull();
    }

    [Fact]
    public void TryDecodeEnumText_ReturnsNull_WhenValueNotInTable()
    {
        var doc = LoadFixture();
        var sig = doc.MessagesById[256].Signals[0];
        // SigA range is [0..3]; 99 is out of table.
        SignalDecoder.TryDecodeEnumText(sig, 99.0, doc).Should().BeNull();
    }

    [Fact]
    public void TryDecodeEnumText_ReturnsNull_WhenTableMissingFromDocument()
    {
        var doc = LoadFixture();
        var sig = doc.MessagesById[256].Signals[0];
        // Build a document with empty ValueTables but keep the signal pointing at "SigA".
        var emptyDoc = doc with { ValueTables = new Dictionary<string, ValueTable>() };
        SignalDecoder.TryDecodeEnumText(sig, 1.0, emptyDoc).Should().BeNull();
    }
}
```

Notes:
- `DbcParser.Parse(text).GetValueOrThrow()` — `DbcParser.Parse` returns `Result<DbcDocument>` per existing convention (see `DbcParser.ParseDocumentFlow`). If the API surface differs, follow whatever the existing DbcParserTests use (search `tests/PeakCan.Host.Core.Tests/` for `DbcParser.Parse` call sites).
- Adjust the import line to match the file's existing `using` block style; the snippet above shows the minimum.

- [ ] **Step 2: Run tests to verify they FAIL**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --nologo --filter "FullyQualifiedName~TryDecodeEnumText" -c Debug --logger "console;verbosity=minimal"
```
Expected: 4/4 FAIL with "TryDecodeEnumText does not exist" / CS0117.

- [ ] **Step 3: Implement TryDecodeEnumText in SignalDecoder.cs**

Append to `src/PeakCan.Host.Core/Dbc/SignalDecoder.cs` (before the closing class brace, after `Decode`):

```csharp
/// <summary>
/// Look up the DBC VAL_ table text for a decoded signal value. Returns
/// null when the signal has no ValueTableName, the table is missing
/// from the document, or the value is not in the table — callers
/// should fall back to numeric formatting.
/// </summary>
public static string? TryDecodeEnumText(
    Signal signal,
    double decodedValue,
    DbcDocument document)
{
    ArgumentNullException.ThrowIfNull(signal);
    ArgumentNullException.ThrowIfNull(document);
    if (signal.ValueTableName is null) return null;
    if (!document.ValueTables.TryGetValue(signal.ValueTableName, out var table))
        return null;
    return table.Entries.TryGetValue((long)decodedValue, out var text) ? text : null;
}
```

- [ ] **Step 4: Run tests to verify they PASS**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --nologo --filter "FullyQualifiedName~TryDecodeEnumText" -c Debug --logger "console;verbosity=minimal"
```
Expected: 4/4 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Core/Dbc/SignalDecoder.cs tests/PeakCan.Host.Core.Tests/SignalDecoderTests.cs
git commit -m "v3.50.5 T1: SignalDecoder.TryDecodeEnumText — DBC VAL_ table lookup with null fallback"
```

---

### Task T2: App — `WatchedSignalRow.LatestText/BlueText/DeltaText` + xaml binding switch

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs` (add Dbc field + 3 computed properties)
- Modify: `src/PeakCan.Host.App/Views/TraceViewerView.xaml` (Latest/Δ/Blue column bindings switch to .Text, drop DoubleNanToStr)
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/WatchedSignalsFlow.cs` (or wherever CollectionChanged sets Signal — set Dbc too)
- Modify: `tests/PeakCan.Host.App.Tests/ViewModels/WatchedSignalRowTests.cs` (append 2 tests)

**Interfaces:**
- Consumes: `SignalDecoder.TryDecodeEnumText` from T1.
- Produces: `WatchedSignalRow.LatestText` / `BlueText` / `DeltaText` (string, computed). `WatchedSignalRow.Dbc` property setter triggers PropertyChanged for the three `.Text` properties.

- [ ] **Step 1: Append 2 failing tests to WatchedSignalRowTests.cs**

Append to `tests/PeakCan.Host.App.Tests/ViewModels/WatchedSignalRowTests.cs`:

```csharp
using PeakCan.Host.Core.Dbc;
using DbcValueType = PeakCan.Host.Core.Dbc.ValueType;

public class WatchedSignalRowTextTests
{
    private static readonly string FixturePath = System.IO.Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "PeakCan.Host.Core.Tests", "Dbc", "Fixtures",
        "sample-with-val.dbc");

    private static DbcDocument LoadFixture()
    {
        var absPath = System.IO.Path.GetFullPath(FixturePath);
        var text = System.IO.File.ReadAllText(absPath);
        return DbcParser.Parse(text).GetValueOrThrow();
    }

    [Fact]
    public void LatestText_ReturnsEnumText_WhenMapped()
    {
        var doc = LoadFixture();
        var sig = doc.MessagesById[256].Signals[0]; // SigA
        var row = new WatchedSignalRow("0x100", "MsgA", "SigA", "bit");
        row.Signal = sig;
        row.Dbc = doc;
        row.LatestValue = 2.0; // SigA[2] -> "Two"
        row.LatestText.Should().Be("Two");
    }

    [Fact]
    public void LatestText_ReturnsNumeric_WhenNotMapped()
    {
        // Build a Signal without ValueTableName.
        var sigBare = new Signal(
            Name: "BareSig", StartBit: 0, Length: 8,
            Order: ByteOrder.LittleEndian, ValueType: DbcValueType.Unsigned,
            Factor: 1.0, Offset: 0.0, Min: 0, Max: 255, Unit: "",
            Receivers: Array.Empty<string>(), ValueTableName: null);
        var row = new WatchedSignalRow("0x100", "MsgA", "BareSig", "bit");
        row.Signal = sigBare;
        row.LatestValue = 1.23;
        row.LatestText.Should().Be("1.23");
    }
}
```

- [ ] **Step 2: Run tests to verify they FAIL**

```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --nologo --filter "FullyQualifiedName~WatchedSignalRowTextTests" -c Debug --logger "console;verbosity=minimal"
```
Expected: 2/2 FAIL — `Dbc` property not on `WatchedSignalRow`, `LatestText` not on `WatchedSignalRow`.

- [ ] **Step 3: Add Dbc + LatestText/BlueText/DeltaText to WatchedSignalRow.cs**

Modify `src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs`:

1. Add `using System.Globalization;` at the top (for `CultureInfo.InvariantCulture`).
2. Add `using PeakCan.Host.App.Composition.Converters;` (for `DoubleNanToStringConverter.Placeholder`).
3. After the existing `Signal` setter (around line 57-61), update the setter to also raise PropertyChanged for the three text properties:
   ```csharp
   public PeakCan.Host.Core.Dbc.Signal? Signal
   {
       get => _signal;
       set
       {
           if (SetProperty(ref _signal, value))
           {
               OnPropertyChanged(nameof(LatestText));
               OnPropertyChanged(nameof(BlueText));
               OnPropertyChanged(nameof(DeltaText));
           }
       }
   }
   ```
4. After the `Signal` property, add the `Dbc` field + property:
   ```csharp
   private PeakCan.Host.Core.Dbc.DbcDocument? _dbc;
   public PeakCan.Host.Core.Dbc.DbcDocument? Dbc
   {
       get => _dbc;
       set
       {
           if (SetProperty(ref _dbc, value))
           {
               OnPropertyChanged(nameof(LatestText));
               OnPropertyChanged(nameof(BlueText));
               OnPropertyChanged(nameof(DeltaText));
           }
       }
   }
   ```
5. Update the `LatestValue` setter (around line 78) to also raise PropertyChanged for `LatestText`:
   ```csharp
   public double LatestValue
   {
       get => _latestValue;
       set
       {
           if (SetProperty(ref _latestValue, value))
           {
               OnPropertyChanged(nameof(DeltaValue));
               OnPropertyChanged(nameof(LatestText));
           }
       }
   }
   ```
6. Update the `BlueLatestValue` setter (around line 94) to also raise PropertyChanged for `BlueText` + `DeltaText`:
   ```csharp
   public double BlueLatestValue
   {
       get => _blueLatestValue;
       set
       {
           if (SetProperty(ref _blueLatestValue, value))
           {
               OnPropertyChanged(nameof(DeltaValue));
               OnPropertyChanged(nameof(BlueText));
               OnPropertyChanged(nameof(DeltaText));
           }
       }
   }
   ```
7. Add three computed properties at the bottom of the class (before the closing brace):
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

   public string BlueText
   {
       get
       {
           if (double.IsNaN(_blueLatestValue)) return DoubleNanToStringConverter.Placeholder;
           if (_signal is not null && _dbc is not null)
           {
               var text = SignalDecoder.TryDecodeEnumText(_signal, _blueLatestValue, _dbc);
               if (text is not null) return text;
           }
           return _blueLatestValue.ToString("F2", CultureInfo.InvariantCulture);
       }
   }

   public string DeltaText
   {
       get
       {
           if (double.IsNaN(_latestValue) || double.IsNaN(_blueLatestValue)) return DoubleNanToStringConverter.Placeholder;
           // G4: enum signals have no subtractable semantics between text labels.
           if (_signal?.ValueTableName is not null) return DoubleNanToStringConverter.Placeholder;
           return (_blueLatestValue - _latestValue).ToString("F2", CultureInfo.InvariantCulture);
       }
   }
   ```

- [ ] **Step 4: Run tests to verify they PASS**

```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --nologo --filter "FullyQualifiedName~WatchedSignalRow" -c Debug --logger "console;verbosity=minimal"
```
Expected: ALL WatchedSignalRow tests PASS (existing + 2 new = 6+).

- [ ] **Step 5: Update TraceViewerViewModel to set Dbc on new rows**

Find the file that handles `WatchedSignals.CollectionChanged` (sister of `_signalByKey` lookup; search the codebase for `WatchedSignals.CollectionChanged` or `_signalByKey` to locate the partial). In that handler, where new rows get their `Signal` assigned, also assign `Dbc` from `_dbcService.Current`.

Example pattern (adapt to actual code):
```csharp
// In WatchedSignals CollectionChanged handler
foreach (var row in e.NewItems.OfType<WatchedSignalRow>())
{
    row.Signal = _signalByKey.GetValueOrDefault(row.SignalKey);
    row.Dbc = _dbcService.Current;  // NEW
}
```

If a dedicated partial `TraceViewerViewModel/DbcSyncFlow.cs` is preferred (per spec §4 architecture), create it. Otherwise edit the existing handler in place.

Also: add a handler for `_dbcService.CurrentChanged` (or equivalent; check the actual API) that iterates all `WatchedSignals` and re-assigns `Dbc` to fan out INPC. Sister of the existing "DBC reload" handling for `_signalByKey` rebuild.

- [ ] **Step 6: Update TraceViewerView.xaml — switch bindings**

In `src/PeakCan.Host.App/Views/TraceViewerView.xaml`, find the Latest/Δ/Blue columns (around lines 286-298 per current HEAD):

Before:
```xml
<DataGridTextColumn Header="Latest"
                    Binding="{Binding LatestValue, Converter={StaticResource DoubleNanToStr}}"
                    Width="60" />
<DataGridTextColumn Header="Δ"
                    Binding="{Binding DeltaValue, Converter={StaticResource DoubleNanToStr}}"
                    Width="60" />
<DataGridTextColumn Header="Blue"
                    Binding="{Binding BlueLatestValue, Converter={StaticResource DoubleNanToStr}}"
                    Width="60" />
```

After:
```xml
<!-- v3.50.5 PATCH: bind to string properties LatestText/BlueText/DeltaText
     (which prefer DBC VAL_ table text and fall back to F2 numeric). Drop
     the DoubleNanToStr converter — the .Text properties handle NaN/—
     internally. -->
<DataGridTextColumn Header="Latest" Binding="{Binding LatestText}" Width="80" />
<DataGridTextColumn Header="Δ"      Binding="{Binding DeltaText}"  Width="50" />
<DataGridTextColumn Header="Blue"   Binding="{Binding BlueText}"   Width="80" />
```

Update the comment block on the Δ column (around line 289-292) to reflect the new behavior:
```xml
<!-- v3.50.5 PATCH: enum signals show "—" in Δ (no subtractable semantics
     between text labels); numeric signals keep the F2 diff. The .Text
     computed property handles both cases. -->
```

- [ ] **Step 7: Run full App.Tests + build**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --nologo -c Debug --logger "console;verbosity=minimal"
```
Expected: 0 build errors, 0 build warnings. App.Tests PASS at count = baseline (819) + 2 new = 821.

- [ ] **Step 8: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs \
        src/PeakCan.Host.App/Views/TraceViewerView.xaml \
        src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/ \
        tests/PeakCan.Host.App.Tests/ViewModels/WatchedSignalRowTests.cs
git commit -m "v3.50.5 T2: WatchedSignalRow LatestText/BlueText/DeltaText + XAML bindings + DbcSync"
```

---

### Task T3: Tracker — `EnumTextTrackerConverter` + `LineSeries.TrackerFormatString`

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/EnumTextTrackerConverter.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/ChartSeriesFlow.cs`

**Interfaces:**
- Consumes: `SignalDecoder.TryDecodeEnumText` (T1), `_dbcService.Current` (T2 pattern), `OxyPlot.TrackerHitResult`.
- Produces: `LineSeries.TrackerFormatString` and `LineSeries.TrackerConverter` set on each chart series built in `BuildOneChartSeriesForSource`. The converter returns `object[] { sigName, decodedText, y, x }` for OxyPlot to format.

- [ ] **Step 1: Create EnumTextTrackerConverter.cs**

Create `src/PeakCan.Host.App/ViewModels/EnumTextTrackerConverter.cs`:

```csharp
using System.Globalization;
using OxyPlot;
using OxyPlot.Annotations;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// v3.50.5 PATCH: <see cref="OxyPlot.Series.LineSeries.TrackerConverter"/>
/// that emits the four-line tooltip matching the CANoe screenshot:
/// <code>
///   SignalName
///   decodedText
///   yValue
///   t = X.XXX s
/// </code>
/// Decoded text comes from <see cref="SignalDecoder.TryDecodeEnumText"/>
/// when the signal has a DBC VAL_ table; otherwise the yValue is
/// formatted as F2 (numeric fallback).
/// </summary>
public sealed class EnumTextTrackerConverter : ITrackerConverter
{
    private readonly Signal _signal;
    private readonly Func<DbcDocument?> _dbcProvider;

    public EnumTextTrackerConverter(Signal signal, Func<DbcDocument?> dbcProvider)
    {
        _signal = signal;
        _dbcProvider = dbcProvider;
    }

    public object? Convert(TrackerHitResult hit)
    {
        var yVal = hit.DataPoint.Y;
        var dbc = _dbcProvider();
        string decoded = dbc is not null
            ? (SignalDecoder.TryDecodeEnumText(_signal, yVal, dbc)
               ?? yVal.ToString("F2", CultureInfo.InvariantCulture))
            : yVal.ToString("F2", CultureInfo.InvariantCulture);
        return new object[] { _signal.Name, decoded, yVal, hit.DataPoint.X };
    }
}
```

- [ ] **Step 2: Verify build**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```
Expected: 0 errors. If CS0246 "ITrackerConverter" not found, add `using OxyPlot;` (the interface lives in `OxyPlot` core, not `OxyPlot.Wpf` — sister of W38 v3.50.4 `PlotController` namespace lesson).

- [ ] **Step 3: Wire TrackerFormatString + TrackerConverter in ChartSeriesFlow.cs**

In `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/ChartSeriesFlow.cs`, find the `LineSeries` initializer inside `BuildOneChartSeriesForSource` (around line 113-120 per current HEAD). After the existing properties, add:

```csharp
// v3.50.5 PATCH: 4-line tracker tooltip matching the CANoe screenshot:
// SignalName / decoded text / yValue / t = X.XXX s
TrackerFormatString = "{0}\n{1}\n{2}\nt = {3:0.000}s",
TrackerConverter = new EnumTextTrackerConverter(sig, () => _dbcService.Current),
```

Also update the file's xmldoc / comment near the `LineAnnotation` block to mention the new Tracker setup.

- [ ] **Step 4: Build + run full test suite**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test --nologo -c Debug --logger "console;verbosity=minimal"
```
Expected: 0 build errors. Test counts: Core.Tests 457+4 = 461, App.Tests 821, Infrastructure.Tests 89. Sister of v3.50.4 ship totals.

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/EnumTextTrackerConverter.cs \
        src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/ChartSeriesFlow.cs
git commit -m "v3.50.5 T3: OxyPlot Tracker 4-line tooltip (signal name + decoded text + y + t)"
```

---

### Task T4: Release notes + version bump + Tier-3 ship

**Files:**
- Modify: `src/Directory.Build.props` (version 3.50.4 → 3.50.5)
- Create: `docs/release-notes-v3.50.5.md`

**Interfaces:**
- Consumes: nothing (administrative).
- Produces: PR + tag `v3.50.5` + GitHub release.

- [ ] **Step 1: Bump version**

In `src/Directory.Build.props`, change:
- `<Version>3.50.4</Version>` → `<Version>3.50.5</Version>`
- `<AssemblyVersion>3.50.4.0</AssemblyVersion>` → `<AssemblyVersion>3.50.5.0</AssemblyVersion>`
- `<FileVersion>3.50.4.0</FileVersion>` → `<FileVersion>3.50.5.0</FileVersion>`
- `<InformationalVersion>3.50.4</InformationalVersion>` → `<InformationalVersion>3.50.5</InformationalVersion>`

- [ ] **Step 2: Write release notes**

Create `docs/release-notes-v3.50.5.md` mirroring the v3.50.4 template. Include:
- Background: user screenshot showing canoe-style enum text in watch list + tracker.
- Why: 6 user-flagged UX improvements... actually 2 (watch list enum text + tracker 4-line).
- What:
  - `SignalDecoder.TryDecodeEnumText` (Core) — new API.
  - `WatchedSignalRow.LatestText/BlueText/DeltaText` — string computed properties.
  - XAML: Latest/Δ/Blue column bindings switched to `.Text` properties (drop converter).
  - Tracker: 4-line tooltip via `LineSeries.TrackerFormatString` + `EnumTextTrackerConverter`.
- LoC delta (rough): +1 method (~10 LoC) + 3 properties (~30 LoC) + 1 converter (~25 LoC) + xaml bindings + 6 new tests + 1 fixture = ~+130 LoC.
- Test outcomes: Core.Tests 461, App.Tests 821, Infrastructure.Tests 89.
- Lesson candidates (NEW 1/3 each): `decoder-extension-must-fall-back-silently`, `watch-list-text-formatting-layer-must-be-string-not-double`, `oxyplot-trackerformatstring-and-trackerconverter-compose`.
- Out of scope: see spec §3 non-goals.
- Next: v3.50.6 vault-only PATCH (lesson promotion candidates).

- [ ] **Step 3: Commit version + release notes**

```bash
git add src/Directory.Build.props docs/release-notes-v3.50.5.md
git commit -m "v3.50.5: version bump 3.50.4 → 3.50.5 + release notes"
```

- [ ] **Step 4: Run full test suite (final verification)**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test --nologo -c Debug --logger "console;verbosity=minimal"
```
Expected: 0 errors. Counts match T3 + 0 new tests in T4. Sister of v3.50.4 totals (461 / 821 / 89).

- [ ] **Step 5: Create PR body file**

Create `docs/pr-body-v3.50.5.md` (kept out of repo, or staged):

```markdown
# v3.50.5 PATCH — Watch List Decoded Enum Text + Tracker 4-line Tooltip

## Summary

Renders DBC `VAL_` table enum text (e.g. "高压上电") in the Trace Viewer
watch list Latest / Δ / Blue columns and in the OxyPlot Tracker tooltip.

## Changes

- **Core**: `SignalDecoder.TryDecodeEnumText(Signal, double, DbcDocument) → string?`
- **App**: `WatchedSignalRow.LatestText/BlueText/DeltaText` computed properties
- **App**: `WatchedSignalRow.Dbc` field + INPC fan-out
- **App**: XAML bindings for Latest / Δ / Blue columns switched to string properties
- **App**: `EnumTextTrackerConverter` + `LineSeries.TrackerFormatString` for 4-line tooltip
- **Tests**: +4 Core tests + +2 App tests + 1 sanitized DBC fixture

## Test plan

- [x] `dotnet test --filter "FullyQualifiedName~TryDecodeEnumText"` — 4/4 PASS
- [x] `dotnet test --filter "FullyQualifiedName~WatchedSignalRow"` — all PASS
- [x] `dotnet test` (full suite) — no new failures, no regressions

## Sister patterns

- W38 v3.50.4 PATCH (immediate predecessor) — same 6-step PR + squash + tag flow
- v3.50.0 MINOR `WatchedSignalRow.Signal` field precedent (plain C# not [ObservableProperty])
- v3.50.4 PATCH `PlotController` namespace lesson (OxyPlot core, not Wpf) — applies to ITrackerConverter

## Out of scope

See [release notes](docs/release-notes-v3.50.5.md) §Out of scope.
```

- [ ] **Step 6: Create PR (awaits explicit user auth per auto-mode classifier)**

```bash
gh pr create --title "v3.50.5 PATCH: watch list decoded enum text + Tracker 4-line tooltip" \
             --body-file docs/pr-body-v3.50.5.md \
             --base main \
             --head feature/v3-50-5-patch-watch-list-decoded-enum
```

Expected: PR created. URL printed. **DO NOT MERGE** — wait for user explicit auth.

- [ ] **Step 7: STOP — wait for user authorization to merge + tag + release**

Per auto-mode classifier, irreversible ops (PR merge, tag push, GitHub release publish) each require explicit user instruction. Do not proceed.

---

## Sister-lesson candidates to monitor

| Lesson | This plan's observation count |
|---|---|
| `decoder-extension-must-fall-back-silently-on-miss-not-throw` | NEW 1/3 (T1: TryDecodeEnumText returns null on 3 miss paths) |
| `watch-list-text-formatting-layer-must-be-string-not-double` | NEW 1/3 (T2: switching binding from double+converter to string computed property) |
| `oxyplot-trackerformatstring-and-trackerconverter-compose` | NEW 1/3 (T3: both required for multi-line tooltip) |
| `mvvm-source-gen-cannot-resolve-cross-assembly-types-must-use-plain-csharp` | NEW 1/3 (T2: Dbc field must be plain C#, sister of v3.50.0 Signal field precedent) |
| `sanitized-fixture-must-strip-bu-nodes-and-use-generic-identifiers` | NEW 1/3 (T0: sample-with-val.dbc fixture sanitization pattern) |

## Verification

- `dotnet build src/`: 0 errors, 0 warnings
- `dotnet test --filter "FullyQualifiedName~TryDecodeEnumText"`: 4/4 PASS
- `dotnet test --filter "FullyQualifiedName~WatchedSignalRow"`: 6+/0 PASS
- `dotnet test` (full solution): 461/821/89 = 1371 PASS (sister of v3.50.4 1365 + 6 new)
- 1 sanitized .dbc fixture (≤ 50 lines, no real CAN IDs)
- PR + tag + GH release published
- Branch deleted post-merge

## Out of scope (YAGNI)

- No enum/raw toggle
- No Y-axis label color change
- No watch-list column restructure
- No tracker box restyling
- No caching layer
- No VM-side playback controls cleanup (already removed v3.50.4)
- No sampling table changes
- No Sampling Table binding changes
- No new public service layer (EnumDecoder)