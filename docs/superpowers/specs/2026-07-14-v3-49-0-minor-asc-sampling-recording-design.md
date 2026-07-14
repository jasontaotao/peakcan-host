# v3.49.0 MINOR SPEC — ASC single-source-of-truth + Trace Viewer sampling table + RecordView consolidation

**Date**: 2026-07-14
**Target version**: v3.49.0 MINOR (3 user-facing changes, no compatibility break — minor)
**Parent**: v3.48.2 PATCH (W35 + v3.48.2 PATCH SHIPPED = `fe40d63` + `e1c8c25` capture-decisions on `main`)

## Context

Three user-reported pain points consolidated into one MINOR:

1. **Q1 — Trace Viewer has no synchronized multi-signal sampling view.** When an operator loads 2+ traces + a DBC and selects 10+ signals to "watch", there is no place that shows "at scrubber time T, what is the decoded value of each watched signal?" Each watch entry has a `LatestValue` column but it reflects the latest *received* frame, not the value at the current playback position. Currently the operator has to eyeball each subplot's cursor intersection visually — painful for 10+ signals, error-prone for cross-signal comparison at a specific instant.

2. **Q2 — The Recording tab (`RecordView` in AppShell's tab strip) duplicates UI real estate that should live inside the Trace Viewer window.** Recording is conceptually a Trace Viewer adjunct (you record trace + watch it in the same context). The standalone tab was a v3.0 carry-over when the Trace Viewer was modal; v3.16.6 PATCH moved the Trace Viewer to a non-modal Window. Recording never followed. Operator has to context-switch between two windows when scrubbing a recorded trace.

3. **Q3 — ASC writer (`src/PeakCan.Host.App/Services/RecordService/Format.partial.cs`) and ASC parser (`src/PeakCan.Host.Core/Replay/AscParser/`) duplicate format knowledge in two places.** A round-trip test (write → parse → assert frame-equal) would lock down the format contract but there is no such test today. Vector ASC v1.3 conventions (`d N` / `l N` / Rx / Tx / `Length = N BitCount = N ID = N`) only exist in the parser's `DataLineParserFlow.cs`; the writer does not emit them so a writer→parser round-trip will silently lose Vector conventions on re-import.

## v3.49.0 D1-D7

- **D1**: 3 logical work streams (Q1 + Q2 + Q3) ship in one MINOR. Sister precedent: W25 + W35 each shipped one large refactor + supplementary cleanups in one MINOR/PATCH. Sister to the broader pattern: 3 independent features batched when related + small.
- **D2**: Already-partial classes (`PeakCanChannel` W35 sister) need no `<partial>` modifier edit. New `AscFormat` class is a new file (not a partial of anything). `RecordService` is already `public sealed partial class`. `TraceViewerViewModel` is already `public sealed partial class`. No D2 entries needed.
- **D3**: Composition boundaries are decided:
  - `AscFormat` lives in `src/PeakCan.Host.Core/Replay/AscFormat.cs` (sibling to `AscParser.cs`) — keeps the public surface co-located with the existing parser, no new `Common/` folder needed
  - `RecordView` partial stays as a UserControl (not migrated into TraceViewerView.xaml cs) — easier to test; the Trace Viewer binds to the RecordViewModel directly via DI
  - `SamplingTableFlow` becomes a 9th partial of `TraceViewerViewModel` (sister of `WatchFlow` + `ChartSeriesFlow`); 336 LoC main + +120 LoC = ~456 LoC after — well below the 800 LoC Round-1 ceiling, can absorb in T4
- **D4**: N/A — no `[LoggerMessage]` partials added (existing `LogSkippedLine` partial stays on `AscParser.cs` per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34+W35 sister precedent). New `LogSamplingTableUpdate` partial goes on `TraceViewerViewModel.cs` main (CS8795 mitigation).
- **D5**: N/A — no LARGEST method ≥ 60 LoC introduced; `SamplingTableFlow.RefreshSamplingTable` is estimated ~25 LoC; `RecordViewModel` does not change in this MINOR.
- **D6**: Branch name `feature/v3-49-0-minor-asc-sampling-recording`.
- **D7**: Tasks ordered **Q3 (AscFormat extraction)** first because Q1 and Q2 lean on it (Q3 sets the new format contract; Q1 reads via AscFormat; Q2 reads via AscFormat for cross-validation). Q2 before Q1 because Q2 removes the AppShell RecordView tab — operators then land directly in the TraceViewer (where the new Sampling Table will also live), so they get one coherent UI update rather than two successive UX changes.

## Architecture

### Stream 1 — Q3: `AscFormat` single-source-of-truth

**New file**: `src/PeakCan.Host.Core/Replay/AscFormat.cs` (~150 LoC)

```
public static class AscFormat
{
    public static readonly char[] WhitespaceSeparators = ...;
    public const string HeaderDateFormat = "ddd MMM dd HH:mm:ss yyyy";
    public const string HeaderBaseAbsolute = "base hex  timestamps absolute";
    public const string HeaderNoInternalEvents = "no internal events logged";

    // Writer side
    public static void WriteHeader(StreamWriter writer, DateTime origin);    // 3 lines
    public static void WriteFooter(StreamWriter writer, TimeSpan elapsed);  // 1 comment line + blank
    public static void WriteDataLine(StreamWriter writer,
                                     ChannelId channel, CanFrame frame);    // 1 line per frame

    // Parser side
    public static bool TryParseDataLine(string line,
                                        out ReplayFrame frame,
                                        out string reason);                    // extracted from AscParser
    public static DateTime? TryParseDateHeader(string line);
    public static bool LineIsSectionDelimiter(string line);
    public static string SectionDelimiterPattern { get; } = "begin triggerblock|end triggerblock|...";

    // Round-trip helpers
    public static string FormatFlagsCompact(FrameFlags flags);               // "fd brs esi error"
    public static FrameFlags ParseFlagsCompact(string token);                 // inverse

    // Format-versioning
    public enum FormatVersion { V1_3 }                                        // future-proofs additional version constants
}
```

**Refactor target #1**: `RecordService/Format.partial.cs` (67 LoC, current `WriteHeader` + `WriteFooter` + `WriteFrame` + `FormatFlags`)
- `WriteHeader` → `AscFormat.WriteHeader(_writer, _startTime)` (preserves current ASC header shape EXACTLY — same 3 lines)
- `WriteFooter` → `AscFormat.WriteFooter(_writer, elapsed)` (1 blank line + 1 `// {elapsed}s` comment)
- `WriteFrame` → `AscFormat.WriteDataLine(_writer, frame.Channel, frame)` (same `{ts:F6} {ch:X2}  {id:X}  {dlc}  {hex}{flags}` format)
- `FormatFlags` (CSV variant) keeps its local impl since CSV uses `|` separators, not space-separated
- Result: Format.partial.cs shrinks from 67 LoC to ~30 LoC (Csv format kept; Asc format delegated)

**Refactor target #2**: `AscParser/DataLineParserFlow.cs` (171 LoC current TryParseDataLine)
- `TryParseDataLine` calls `AscFormat.TryParseDataLine(...)` instead of duplicating the format decoder
- Result: DataLineParserFlow.cs shrinks from 171 LoC to ~50 LoC

**Refactor target #3**: `AscParser/ParseLinesFlow.cs` (113 LoC current TryParseDateHeader + section-delimiter scan + main loop)
- `TryParseDateHeader` extracts to `AscFormat.TryParseDateHeader(...)`
- Section-delimiter scan extracts to `AscFormat.LineIsSectionDelimiter(...)`
- Main loop stays in `ParseLinesFlow.cs` (it owns the partial-line skip + 50%-malformed invariant)
- Result: ParseLinesFlow.cs shrinks from 113 LoC to ~70 LoC

**Net**: AscFormat.cs new ~150 LoC; Format.partial.cs -37 LoC; DataLineParserFlow.cs -120 LoC; ParseLinesFlow.cs -43 LoC. Net: **+150 - 200 = -50 LoC**, broken up across 4 files (each file ~30-150 LoC; well under 800 LoC ceiling).

### Stream 2 — Q3: round-trip test

**New file**: `tests/PeakCan.Host.Core.Tests/Replay/AscFormatRoundTripTests.cs` (~120 LoC, ≥6 tests)

```
[Fact] WriteDataLine_ClassicFrame_ParseBackRoundTripEqual
[Fact] WriteDataLine_FdFrame_ParseBackRoundTripEqual
[Fact] WriteDataLine_BrsAndEsiFlags_PreserveFrameFlags
[Fact] WriteDataLine_ErrorFlag_PreserveErrorFrameBit
[Theory] WriteHeader_ThreeLines_ParseDateAndBaseIsAbsolute
[Theory] WriteDataLine_MultipleFrames_TimestampMonotonic
```

Each test invokes `AscFormat.WriteDataLine` → captures the stream string → invokes `AscParser.Parse` (the single-public entry point) → compares frame-by-frame equality against the input.

This locks down the format spec for future changes. If a future contributor breaks the writer or parser, the round-trip test catches it.

### Stream 3 — Q2: RecordView → TraceViewer consolidation

**Step 1**: Confirm `RecordView.xaml` UserControl can be reused as-is. It already has the 3 sections (Output + Browse / Format / Start+Stop+Status+Frames) — no UI changes needed.

**Step 2**: Add `<RecordView>` host to `TraceViewerView.xaml` at the bottom DockPanel (under the chart area), with `<Expander>` wrapper so operators can collapse it:
```xml
<Expander DockPanel.Dock="Bottom"
          Header="Recording"
          IsExpanded="False">
    <rec:RecordView DataContext="{Binding RecordingViewModel}" />
</Expander>
```

**Step 3**: New partial on `TraceViewerViewModel`: `Recording.partial.cs` (~60 LoC)
- `RecordingViewModel` property exposes the existing DI-injected `RecordViewModel`
- Loaded-by-DI in `AppHostBuilder.cs` (already registered)
- TraceViewerView.xaml.cs DataContext wires RecordingViewModel via `AppHostBuilder.GetRequiredService<RecordViewModel>()`

**Step 4**: Remove `RecordView` from `AppShell/ViewSwitchFlow.cs:141` (the `_recordViewModel` tab factory entry). Operator loses the standalone tab; the Recording Expander in Trace Viewer takes over.

**Step 5**: Update DI smoke-test (`AppHostBuilderTests`): the test that asserts all singleton VMs register must drop `RecordViewModel` from the assertion (still registered as singleton, but no longer added to AppShell's tab strip).

**Net LoC**:
- New: Recording.partial.cs ~60 LoC, TraceViewerView.xaml +12 LoC, RecordView.xaml.cs binding adapter ~10 LoC
- Removed: AppShell/ViewSwitchFlow.cs `RecordView` factory ~7 LoC, Recording.partial.cs's RecordingViewModel property deletable (replaced by the public-property approach)
- Net: **+75 LoC** but the operator sees **1 tab → 1 collapsible panel**, gaining physical-screen real estate

### Stream 4 — Q1: Trace Viewer sampling table

**New partial**: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SamplingTableFlow.cs` (~140 LoC)

```
public sealed partial class TraceViewerViewModel
{
    private const int SampleRefreshDebounceMs = 50;  // smooth scrubber drag
    
    [ObservableProperty]
    private ObservableCollection<SamplingTableRow> samplingRows = new();
    
    public SamplingTableRow BuildRow(WatchedSignalRow watchRow);   // synchronous per-signal value lookup
    
    private void RefreshSamplingTable();                            // called on ScrubberValue changes (debounced)
    private void ScheduleSamplingRefresh();
    private void CancelSamplingRefresh();
}

public sealed record SamplingTableRow(
    string CanIdHex,
    string MessageName,
    string SignalName,
    string Unit,
    string Value,         // formatted per the signal's bit-layout encoding
    OxyColor Color         // matches the subplot color of the same signal
);
```

Refresh logic: when `ScrubberValue` changes (debounced 50 ms), for each `WatchedSignalRow` row in `WatchedSignals`, find the *latest frame in the master source's `frames` array at-or-before `ScrubberValue`* and decode the signal value using the watch row's `DbcSignal` + `DbcMessage` + `CanFrame` decoded payload. If no such frame exists, show `—`.

Frame lookup uses the existing `TraceViewerService.GetFrames(string sourceId)` API — already iterates `ITraceSessionRegistry.GetFrames(sourceId)` and is fully tested (`TraceViewerServiceTests.cs`).

**XAML addition**: `TraceViewerView.xaml` adds a new DockPanel section:
```xml
<Border DockPanel.Dock="Right" Width="280" BorderBrush="#DDD" BorderThickness="1,0,0,0"
        Visibility="{Binding HasWatchedSignals, Converter={StaticResource BoolToVis}}">
    <DockPanel>
        <TextBlock DockPanel.Dock="Top" Text="Sampling Table" FontWeight="SemiBold" Margin="6,4" />
        <DataGrid ItemsSource="{Binding SamplingRows}"
                  AutoGenerateColumns="False" IsReadOnly="True"
                  EnableRowVirtualization="False" RowHeight="22">
            <DataGrid.Columns>
                <DataGridTextColumn Header="Signal" Binding="{Binding SignalName}" Width="*" />
                <DataGridTextColumn Header="Unit" Binding="{Binding Unit}" Width="50" />
                <DataGridTextColumn Header="Value" Binding="{Binding Value}" Width="70" />
            </DataGrid.Columns>
        </DataGrid>
    </DockPanel>
</Border>
```

**New test**: `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModel/SamplingTableFlowTests.cs`
```
[Fact] SamplingRows_StartEmpty
[Fact] SamplingRows_PopulatedAfterScrubberMove_OneRowPerWatchedSignal
[Fact] SamplingRows_NoMasterSource_NoCrash_RowsEmpty
[Fact] SamplingRows_Debounce100ScrubberMoves_RendersOnce
[Fact] SamplingRows_FormatFlagsCompact_RoundTripWithParser
```

## LoC trajectory summary

| Stream | Files changed | Net LoC |
|---|---|---|
| Q3 AscFormat spec | 1 new + 3 refactored | -50 |
| Q3 round-trip test | 1 new tests file | +120 |
| Q2 RecordView → TraceViewer | 1 new partial + XAML edits + 1 ViewSwitchFlow line removed | +65 |
| Q1 SamplingTable | 1 new partial + XAML edit + 1 test file | +210 |

**Total net**: -50 + 120 + 65 + 210 = **+345 LoC net** (mostly XAML + tests + new partials).

## Risks + mitigations

| Risk | Mitigation |
|---|---|
| `AscFormat.WriteDataLine` doesn't exactly match the parser's expectations (Vector-convention drift) | Round-trip test locks down the contract in T2 |
| 9th partial on `TraceViewerViewModel` blows past 800 LoC ceiling on the main file | TraceViewerViewModel main is 336 LoC today; SamplingTableFlow ~140 LoC; main absorbs ~140 LoC = 476 LoC, under 600 LoC; safe |
| Removing the RecordView tab breaks the operator's muscle memory | Release notes mention the move; operator can right-click the new Expander's title bar to dock/undock |
| `RefreshSamplingTable` debounce + per-signal decode could stutter on large trace files | 50 ms debounce + sample-refresh scoped to master source only; perf test on 100 MB trace to confirm <16 ms per sample |
| Q3 AscFormat extraction breaks existing `AscParserTests` | Sister: round-trip tests + retained AscParser public API = test suite unaffected (the parse public surface is unchanged; only internals move) |

## Sister-lesson candidates to monitor

| Lesson | What v3.49.0 observes |
|---|---|
| `add-partial-keyword-to-monolithic-class-before-extraction` | AscFormat is a new static class (not a partial); TraceViewerViewModel gets a 9th partial (sister of W3-W20 expansions) |
| `partial-class-with-private-static-logger-message-cross-partial-compiles-clean` | `TraceViewerViewModel/LogSamplingTableUpdate` partial added to main partial declaration (sister of W18+W34+W35) — confirms 15th cross-partial logger call site |
| `cross-format-spec-extracted-into-shared-library` (NEW) | AscFormat extraction = ASC writer (App/Services) and parser (Core/Replay) now share Format/Parse logic. If v3.49.0 ships cleanly, **NEW 1/3 lesson candidate** documenting 3-layer drift-prevention pattern. |
| `recording-controls-moved-within-trace-viewer` (NEW) | If v3.49.0 ships cleanly, **NEW 1/3 lesson candidate** documenting user-flow consolidation when an adjunct tab moves into its conceptual owner window. |
| `sampling-table-panel-shared-cursor-across-multiple-signals` (NEW) | If v3.49.0 ships cleanly, **NEW 1/3 lesson candidate** documenting master-source-driven per-signal value lookup with debounced refresh. |

## Verification

- `dotnet build PeakCan.Host.slnx`: 0 errors, 0 new warnings (1 pre-existing CS8602 in `DbcService/LoadLifecycle.partial.cs` retained)
- `dotnet test PeakCan.Host.slnx`: existing **1339/1339 PASS, 5 SKIP** + ≥10 new tests pass = **≥1349/≥1349 PASS**
- `dotnet test --filter "FullyQualifiedName~AscFormat"`: ≥6 round-trip tests pass
- `dotnet test --filter "FullyQualifiedName~SamplingTable"`: ≥5 tests pass
- `dotnet test --filter "FullyQualifiedName~AppHostBuilder"`: existing tests still pass after RecordView removed from tab strip
- Manual smoke: load 2 traces, scrub the slider, see "Sampling Table" panel populate with sync values; click Start in the Recording Expander, see "Frames: 12" count up; click Stop → file written to disk → re-import via Trace Viewer's Add trace → file parses
- `wc -l src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` ≤ 500 LoC after T4

## Out of scope (YAGNI)

- **CSV export of Sampling Table rows at the current scrubber position** — easy PATCH follow-up but not v3.49.0 scope
- **Per-row signal-pair correlation matrix** ("show V2B_CMD vs V2B_Speed at every 100ms") — much larger MINOR
- **Auto-record on Connect + Auto-snapshot into existing recording** — out of scope
- **Replay cursor in the Sampling Table grid** (sub-row-level cursor highlighting) — pure CSS works for now
- **Cross-source sampling comparison** (compare 2 sources' values at the same scrubber position) — single-master only in v3.49.0
- **Recording to formats other than ASC** — CSV was scope-of-v1.3.0 per `RecordView.xaml:18` comment "CSV support deferred to v1.3.0"
- **ASC writer emits Vector 'd N' / 'l N' / Rx / Tx / Length convention tokens** — round-trip test verifies current writer's output; Vector convention tokens added only if a future user-imported Vector ASC file proves to lose round-trip symmetry, which we should not preemptively add
