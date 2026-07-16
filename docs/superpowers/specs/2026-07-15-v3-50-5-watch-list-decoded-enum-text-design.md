# v3.50.5 Watch List Decoded Enum Text + Tracker 4-line Tooltip

> **Status**: Design — pending user approval.

**Goal**: Watch list Latest / Δ / Blue columns and OxyPlot Tracker tooltip render DBC
`VAL_` enum text (e.g. "高压上电") instead of raw numeric values for enumerated signals.
Numeric (non-`VAL_`) signals keep displaying the existing F2-scaled engineering value.

**Architecture**: Extend `SignalDecoder` with one static `TryDecodeEnumText` method that
looks up a `Signal.ValueTableName` reference inside `DbcDocument.ValueTables`. App layer
adds three string computed properties (`LatestText` / `BlueText` / `DeltaText`) to
`WatchedSignalRow` that prefer the enum text and fall back to the numeric string. XAML
bindings for the three watch-list columns switch from `LatestValue` / `BlueLatestValue` /
`DeltaValue` (double) to the new string properties (no `DoubleNanToStr` converter on
those columns). Chart per-series Tracker is configured via OxyPlot's
`LineSeries.TrackerFormatString` + `TrackerConverter` to render the 4-line tooltip that
matches the CANoe screenshot.

**Tech Stack**: C# / .NET 10 / WPF / OxyPlot (existing) / CommunityToolkit.Mvvm (existing).

## 1. Background & motivation

User feedback 2026-07-15 (screenshot from CANoe for reference):
- Watch list currently shows `LatestValue` as a raw `double` (F2-formatted), even when
  the underlying signal is an enumerated CAN state (e.g. `V2B_HVOnCmd` value `1` should
  read "高压上电", not `1.00`).
- Tracker tooltip on hover shows `(X:..., Y:...)` — useful for X/Y but does not label
  the signal name nor show the decoded enum text.

The Trace Viewer's purpose is "inspect a moment in the trace, see the decoded state of
every signal at that moment". For enumerated signals the *state name* (Chinese text
from the DBC `VAL_` table) is the primary information — the raw integer is debug data,
not what a user wants to read in a busy chart.

`DbcDocument.ValueTables` and `Signal.ValueTableName` already exist (parsed by
`DbcParser.ValueTableFlow` since v1.2.9 PATCH); the only missing piece is the lookup
function and the UI bindings to surface it.

## 2. Goals (in scope)

- **G1**: When a watched signal has a DBC `VAL_` table and its decoded integer is in the
  table, the `Latest` / `Blue` columns show the table's text label (e.g. "高压上电")
  instead of the F2 number.
- **G2**: When the signal has no `VAL_` table, or the decoded value is not in the
  table, the columns fall back to the existing F2-scaled numeric display.
- **G3**: When the value is NaN (no frame at the anchor), columns show "—" (existing
  `DoubleNanToStringConverter.Placeholder` behavior).
- **G4**: The `Δ` column for an enumerated signal shows "—" (no subtractable semantics
  between text labels). For numeric signals it keeps the current numeric diff.
- **G5**: OxyPlot Tracker tooltip shows four lines per the CANoe screenshot:
  `SignalName` / decoded text / decoded value / `t = X.XXX s`.
- **G6**: The decode path is hot (called on every drag tick for every watched row);
  the lookup must not introduce a measurable frame-rate regression. No special caching
  layer added — dictionary O(1) is sufficient.

## 3. Non-goals (YAGNI)

- **N1**: No enum/raw toggle. Latest/Blue always prefer enum text when available.
- **N2**: No change to Y-axis label color (kept black per user direction 2026-07-15).
- **N3**: No watch-list column restructure. CAN ID / Signal / Plot / N / Latest /
  Δ / Blue / ✕ columns unchanged in count and order.
- **N4**: No tracker box restyling — keep OxyPlot default Tracker visual.
- **N5**: No `Sampling Table` changes — this PATCH is scoped to watch list + per-chart
  Tracker only.
- **N6**: No new public API beyond `SignalDecoder.TryDecodeEnumText` and the App-layer
  computed properties; no new service layer.

## 4. Architecture

```
Core layer (no UI dep):
  SignalDecoder (src/PeakCan.Host.Core/Dbc/SignalDecoder.cs)
    + public static string? TryDecodeEnumText(Signal sig, double decodedValue, DbcDocument doc)
        1. if sig.ValueTableName is null → return null
        2. if doc.ValueTables[sig.ValueTableName] is not { } tbl → return null
        3. return tbl.Entries.TryGetValue((long)decodedValue, out var s) ? s : null
      Returns null on miss; callers fall back to numeric formatting.

App layer:
  WatchedSignalRow (src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs)
    + private PeakCan.Host.Core.Dbc.DbcDocument? _dbc  // plain field, mvvm-gen limitation
    + public DbcDocument? Dbc { get => _dbc; set { if (SetProperty(...)) { OnPropertyChanged(nameof(LatestText)); OnPropertyChanged(nameof(BlueText)); OnPropertyChanged(nameof(DeltaText)); } } }
    + public Signal? Signal { set } // existing setter, add OnPropertyChanged(nameof(LatestText/BlueText/DeltaText))
    + public string LatestText { get; } // computed
    + public string BlueText { get; }   // computed
    + public string DeltaText { get; }  // computed
    
    LatestText getter:
      if IsPlaceholder or double.IsNaN(LatestValue) → return Placeholder
      if Signal != null && Dbc != null:
        var s = SignalDecoder.TryDecodeEnumText(Signal, LatestValue, Dbc)
        return s ?? LatestValue.ToString("F2", InvariantCulture)
      return LatestValue.ToString("F2", InvariantCulture)
    
    DeltaText getter:
      if double.IsNaN(LatestValue) || double.IsNaN(BlueLatestValue) → return Placeholder
      if Signal?.ValueTableName is not null → return Placeholder  // G4 enum rule
      return (BlueLatestValue - LatestValue).ToString("F2", InvariantCulture)

  TraceViewerViewModel (src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/*.cs)
    + in WatchedSignals CollectionChanged handler: assign Dbc from _dbcService.Current
    + new partial: TraceViewerViewModel/DbcSyncFlow.cs — handles DBC reload (re-assign
      Dbc on every existing row → INPC fan-out)

  TraceViewerView.xaml
    Latest column:    Binding="{Binding LatestText}"   Width="80"
    Δ column:         Binding="{Binding DeltaText}"    Width="50"
    Blue column:      Binding="{Binding BlueText}"     Width="80"
    Remove DoubleNanToStr converter on these three columns.

  ChartSeriesFlow.BuildOneChartSeriesForSource
    LineSeries.TrackerFormatString = "{0}\n{1}\n{2}\nt = {3:0.000}s"
    LineSeries.TrackerConverter = new EnumTextTrackerConverter(sig, dbcRef)
    // TrackerConverter is a small ITrackerConverter impl in App/ViewModels that
    // returns object[] { sigName, decodedText ?? y.ToString(...), y, x }

  EnumTextTrackerConverter (src/PeakCan.Host.App/ViewModels/EnumTextTrackerConverter.cs)
    - ctor: (Signal sig, Func<DbcDocument?> dbcProvider)
    - Convert(TrackerHitResult h) → object[] { sig.Name, decodedText, y, x }
```

### 4.1 Tracker Converter injection

`BuildOneChartSeriesForSource` runs once per (source, signal) opt-in plot, captures the
`Signal` reference and a `Func<DbcDocument?>` lambda that returns `_dbcService.Current`
at hover time (so a DBC reload between chart-build and hover uses the new DBC).

## 5. Data flow

### 5.1 Watch-list refresh on green-line drag

```
User drags green line to X = 615 s
  → PlotView.PreviewMouseMove → TraceViewerView.OnPlotViewMouseMove
  → vm.RefreshAtAnchor(615)
  → TraceViewerViewModel.RefreshAtAnchor:
      foreach row in WatchedSignals:
        frame = _registry.GetFramesAt(X, row.CanId)
        row.LatestValue = SignalDecoder.Decode(frame.Data, row.Signal)
        // setter triggers OnPropertyChanged(nameof(LatestText))
  → WPF DataGrid re-evaluates Binding → LatestText getter runs
  → LatestText → SignalDecoder.TryDecodeEnumText(Signal, v, Dbc)
                ?? v.ToString("F2", InvariantCulture)
  → cell renders "高压上电" or "1.00" or "—"
```

### 5.2 Tracker hover

```
User hovers over a chart line
  → OxyPlot PlotController.Tracker shows on each hit
  → TrackerFormatter evaluates TrackerFormatString + TrackerConverter.Convert(hit)
  → EnumTextTrackerConverter.Convert:
      sigName = sig.Name                              // "V2B_HVOnCmd"
      decodedText = TryDecodeEnumText(sig, hit.Y, dbc()) ?? hit.Y.ToString("0.##")
      rawInt = hit.Y                                   // decoded int per user direction
      xTime = hit.X
      return object[] { sigName, decodedText, rawInt, xTime }
  → formatted output:
      V2B_HVOnCmd
      高压上电
      1
      t = 155615.585s
```

## 6. API contracts

### 6.1 `SignalDecoder.TryDecodeEnumText`

```csharp
// src/PeakCan.Host.Core/Dbc/SignalDecoder.cs
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

Notes:
- `(long)decodedValue` — VAL_ keys are `long`. The DBC engineering value is `double`
  (after `factor`/`offset`); for enum signals factor is typically 1 and offset 0 so the
  cast is exact. For pathological cases (factor != 1) the integer in the table is the
  raw physical value, not the bit pattern; this matches DBC semantics.
- `IReadOnlyDictionary.TryGetValue` is already used by `DbcParser` patterns; no new
  exception types.

### 6.2 `WatchedSignalRow` extensions

```csharp
// src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs

private DbcDocument? _dbc;
public DbcDocument? Dbc
{
    get => _dbc;
    set { if (SetProperty(ref _dbc, value)) { OnLatestTextChanged(); OnBlueTextChanged(); OnDeltaTextChanged(); } }
}

public Signal? Signal
{
    get => _signal;
    set { if (SetProperty(ref _signal, value)) { OnLatestTextChanged(); OnBlueTextChanged(); OnDeltaTextChanged(); } }
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
        return _latestValue.ToString("F2", CultureInfo.InvariantCulture);
    }
}
// BlueText and DeltaText follow the same pattern.
```

### 6.3 `EnumTextTrackerConverter`

```csharp
// src/PeakCan.Host.App/ViewModels/EnumTextTrackerConverter.cs
public sealed class EnumTextTrackerConverter : ITrackerConverter
{
    private readonly Signal _signal;
    private readonly Func<DbcDocument?> _dbcProvider;

    public EnumTextTrackerConverter(Signal signal, Func<DbcDocument?> dbcProvider)
    { _signal = signal; _dbcProvider = dbcProvider; }

    public object? Convert(TrackerHitResult hit)
    {
        var dbc = _dbcProvider();
        var yVal = hit.DataPoint.Y;
        var decoded = dbc is not null
            ? SignalDecoder.TryDecodeEnumText(_signal, yVal, dbc) ?? yVal.ToString("0.##", CultureInfo.InvariantCulture)
            : yVal.ToString("0.##", CultureInfo.InvariantCulture);
        return new object[] { _signal.Name, decoded, yVal, hit.DataPoint.X };
    }
}
```

## 7. Test plan

### 7.1 Unit tests (Core.Tests — 4 new)

| Test | Validates |
|---|---|
| `SignalDecoder.TryDecodeEnumText_ReturnsMappedString_WhenValueInTable` | G1 happy path |
| `SignalDecoder.TryDecodeEnumText_ReturnsNull_WhenNoValueTable` | signal.ValueTableName is null |
| `SignalDecoder.TryDecodeEnumText_ReturnsNull_WhenValueNotInTable` | raw not in Entries |
| `SignalDecoder.TryDecodeEnumText_ReturnsNull_WhenTableMissingFromDocument` | orphaned table name |

### 7.2 Unit tests (App.Tests — 2 new)

| Test | Validates |
|---|---|
| `WatchedSignalRow.LatestText_ReturnsEnumText_WhenMapped` | G1 + G2 row-level integration |
| `WatchedSignalRow.LatestText_ReturnsNumeric_WhenNotMapped` | G2 fallback |

### 7.3 Sanitized DBC fixture (Core.Tests — 1 new file)

`tests/PeakCan.Host.Core.Tests/Dbc/Fixtures/sample-with-val.dbc`

Sanitization rules:
- Strip all `BU_` lines (remove node names).
- Strip all `BO_` entries except a single `BO_ 256 MsgA: 8 SG_ SigA ...`.
- Strip `CM_`, `BA_DEF_`, `BA_`, `VAL_TABLE_`, `SIG_GROUP_`, etc. — keep only `BO_` +
  `SG_` + `VAL_`.
- Rename signal/message to generic identifiers (`SigA`, `MsgA`).
- Inline the `VAL_` mapping so no separate `VAL_TABLE_` is needed:
  ```
  BO_ 256 MsgA: 8 Vector__XL
   SG_ SigA : 0|2@1+ (1,0) [0|3] "bit" Vector__XL
  VAL_ 256 SigA 0 "Zero" 1 "One" 2 "Two" 3 "Three" ;
  ```
- File size ≤ 50 lines. No real CAN IDs, no real vehicle signals, no OEM/proprietary
  strings.

### 7.4 Regression checks

- Run `dotnet test --filter "FullyQualifiedName~WatchedSignalRow|TraceViewer"` — must
  remain green.
- Run `dotnet test --filter "FullyQualifiedName~SignalDecoder|DbcParser"` — must
  remain green.
- Full solution `dotnet test` — no new failures.

## 8. Risks & mitigations

| Risk | Mitigation |
|---|---|
| TrackerFormatString conflicts with existing v3.50.4 PlotController | Both are sub-properties of the same controller. OxyPlot merges them. Verified in OxyPlot 2.2.0 docs. |
| RefreshAtAnchor hot path adds dict lookup overhead | Dictionary O(1) ≈ 50ns × 100 rows × 1kHz = ~5ms/s. No caching. |
| DBC reload leaves stale `DbcDocument?` on existing rows | New `DbcSyncFlow` partial re-assigns Dbc on every existing row in `WatchedSignals` when `_dbcService.Current` changes. |
| TrackerConverter captures stale dbcProvider lambda | Lambda captures `_dbcService` reference (not value); reads `.Current` at hover time. DBC reload transparent. |
| Sanitized fixture diverges from real DBC edge cases (extended ID, Motorola, float) | New tests only cover 2-bit unsigned little-endian. Real-world edge cases covered by existing tests with the original fixture (not modified by this PATCH). |

## 9. Out of scope

- VM-side playback controls (already removed v3.50.4) — no further cleanup.
- Watch-list placeholder row ✕ button visibility — deferred per W38 ship.
- v3.50.2 fixture exposure concern — unaffected (no new public fixtures; sanitized
  fixture added under `tests/`).

## 10. Sister patterns to monitor

| Lesson candidate | This PATCH observation |
|---|---|
| `decoder-extension-must-fall-back-silently-on-miss-not-throw` | NEW 1/3: SignalDecoder.TryDecodeEnumText returns null on miss; UI fallback to F2 numeric. |
| `watch-list-text-formatting-layer-must-be-string-not-double` | NEW 1/3: Switching XAML binding from `double LatestValue` (via converter) to `string LatestText` (computed) shifts formatting responsibility from converter to property. |
| `oxyplot-trackerformatstring-and-trackerconverter-compose` | NEW 1/3: `TrackerFormatString` is the formatter, `TrackerConverter` is the value-source; both required to emit multi-line text. |