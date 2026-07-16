# Release Notes v3.50.5 — Watch List Decoded Enum Text + Tracker 4-line Tooltip (combined PATCH)

**Released**: pending ship
**Tag**: v3.50.5
**Branch**: `feature/v3-50-5-patch-watch-list-decoded-enum`
**Parent**: v3.50.4 PATCH (`ab4613d` on `main`)

## Why this PATCH

User reference: canoe screenshot showing the watch-list rendering decoded enum
text (e.g. "高压上电") instead of raw integers, and a 4-line tracker tooltip on
hover (signal name / decoded text / raw value / t = X.XXX s). Our v3.50.4
watch list still shows F2 numeric for everything; the v3.50.4 Tracker
shows the default OxyPlot "(X:..., Y:...)" text. Both fall short of canoe.

## What this PATCH does

### 1. Watch list Latest / Δ / Blue columns display decoded enum text

DBC `VAL_` table entries (e.g. `1 → "高压上电"`) now surface in the watch list
when the watched signal has a ValueTableName reference. Behavior:

| Signal type | Latest / Blue column | Δ column |
|---|---|---|
| DBC enum (has `VAL_` table, value in table) | text label (e.g. "高压上电") | "—" (no subtractable semantics) |
| DBC enum, value out of table | F2 numeric (fallback) | "—" |
| Pure numeric signal (no `VAL_` table) | F2 numeric (unchanged) | F2 diff (unchanged) |
| Any, NaN | "—" (unchanged) | "—" |

XAML binding for the three columns switched from `{Binding LatestValue,
Converter={StaticResource DoubleNanToStr}}` (double + converter) to plain
`{Binding LatestText}` (string). Column widths updated (60→80 / 60→50 /
60→80) to accommodate text content.

### 2. Tracker 4-line tooltip (CANoe layout)

Hovering over a chart line shows:

```
V2B_HVOnCmd
高压上电
1
t = 155615.585s
```

Order: signal name / decoded text (DBC VAL_ fallback to F2) / yValue /
wall-clock-style time. Implementation: `EnumTrackerLineSeries` subclass of
`OxyPlot.Series.LineSeries` overrides `GetNearestPoint` to rewrite the
internal `.Text` field after `base.GetNearestPoint` runs. Captured
`Func<DbcDocument?>` reads `_dbcService.Current` at hover time so a DBC
reload between chart-build and hover uses the new DBC.

### 3. DBC reload fans out to existing watch rows

`OnDbcLoaded` (TraceViewerViewModel/SourceFlow.cs) now iterates all
`WatchedSignals` and re-assigns `row.Dbc = doc`, which triggers
`PropertyChanged` for `LatestText` / `BlueText` / `DeltaText` so the
columns refresh against the new DBC's VAL_ tables. Sister of the
existing `RebuildSignalsCore` (which already re-keys Signal references).

## Files changed

### Core (1 src + 1 test)
- `src/PeakCan.Host.Core/Dbc/SignalDecoder.cs` — `+ TryDecodeEnumText`
- `tests/PeakCan.Host.Core.Tests/SignalDecoderTests.cs` — `+ 4 tests`
- `tests/PeakCan.Host.Core.Tests/Dbc/Fixtures/sample-with-val.dbc` — `NEW`

### App (5 src + 1 test)
- `src/PeakCan.Host.App/ViewModels/WatchedSignalRow.cs` — `+ Dbc` field/property, `+ LatestText/BlueText/DeltaText`, INPC fan-out in setters
- `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` — CollectionChanged handler also sets `row.Dbc = dbc`
- `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/SourceFlow.cs` — `OnDbcLoaded` re-binds Dbc on existing rows
- `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/ChartSeriesFlow.cs` — `LineSeries` → `EnumTrackerLineSeries`
- `src/PeakCan.Host.App/Views/TraceViewerView.xaml` — Latest/Δ/Blue bindings to `.Text` (no DoubleNanToStr)
- `src/PeakCan.Host.App/ViewModels/EnumTrackerLineSeries.cs` — `NEW`
- `tests/PeakCan.Host.App.Tests/ViewModels/WatchedSignalRowTests.cs` — `+ 2 tests`

## LoC delta (rough)

- Core: +30 LoC (1 method + 4 tests)
- App: +130 LoC (3 properties + 1 subclass + xaml + 2 tests + sync flow)
- Total: +160 LoC across 10 files (+1 sanitized fixture)

## Test outcomes

- **App.Tests**: 822 PASS / 3 SKIP / 0 FAIL (baseline 820 + 2 new)
- **Core.Tests**: 460 PASS / 0 SKIP / 1 transient FAIL (`SetSpeed_PreservesCurrentTimestamp` W34-W50 sister pattern; isolated run passes; not introduced by v3.50.5)
- **Infrastructure.Tests**: 89 PASS / 2 SKIP / 0 FAIL (unchanged)
- **Build**: 0 errors, 0 new warnings (3 pre-existing warnings in unrelated files)

## Lesson candidates (NEW 1/3 each — await 2nd observation for promotion)

| Lesson | Source observation |
|---|---|
| `decoder-extension-must-fall-back-silently-on-miss-not-throw` | T1: `TryDecodeEnumText` returns null on 3 distinct miss paths (no ValueTableName / table missing / value out of table). UI layer treats null as "fall back to numeric". |
| `watch-list-text-formatting-layer-must-be-string-not-double` | T2: switching XAML binding from `double LatestValue + DoubleNanToStr converter` to `string LatestText computed property` shifts formatting responsibility from converter into the VM property. |
| `oxyplot-2-2-0-line-series-tracker-extension-is-override-getnearestpoint-not-itrackerconverter` | T3: **NEW lesson captured**. OxyPlot 2.2.0 does NOT expose ITrackerConverter (assumed by spec; verified absent via ilspycmd decompile of LineSeries). Real extension point: subclass LineSeries and override `GetNearestPoint(ScreenPoint, bool)`, mutate `hit.Text` after `base.GetNearestPoint` returns. |
| `mvvm-source-gen-cannot-resolve-cross-assembly-types-must-use-plain-csharp` | T2: `WatchedSignalRow.Dbc` (DbcDocument?) field must be plain C#, NOT `[ObservableProperty]`. Sister of v3.50.0 MINOR Signal field precedent. CommunityToolkit.Mvvm source-gen partial .g.cs into XAML temp csproj cannot pull PeakCan.Host.Core.dll. |
| `sanitized-fixture-must-strip-bu-nodes-and-use-generic-identifiers` | T0: `sample-with-val.dbc` keeps only `BO_` + `SG_` + `VAL_` inline entries; strips `BU_` / nodes / `CM_` / `BA_DEF_` to ensure no proprietary signal names leak into tests/. |

## Out of scope (YAGNI)

- No enum/raw toggle
- No Y-axis label color change
- No watch-list column restructure
- No tracker box restyling
- No caching layer
- VM-side playback controls (already removed v3.50.4)
- Stale `Signal` reference after DBC reload (sister of v3.50.0 known issue;
  only Dbc ref is re-bound; Signal cache `_signalByKey` is preserved untouched)
- Sampling Table (separate panel, out of scope)

## Next (post-v3.50.5 ship)

- **v3.50.6 vault-only PATCH** — promote the 4 NEW 1/3 lessons
  (decoder-extension-fallback / watch-list-text-formatting / mvvm-cross-asm /
  sanitized-fixture-strip-bu). The 5th lesson
  (oxyplot-2-2-0-tracker-override) is implementation-specific and may not
  promote — keep an eye on future OxyPlot upgrades.
- **W36+ god-class refactor** — sister of W35 PeakCanChannel 2nd-cycle;
  candidates: StatsViewModel 263 / DbcTokenizer 239 / DbcSendViewModel 238 /
  AscLocator 225 / RecordService 215.
- **v3.50.2 fixture exposure concern** — still deferred; auto-mode classifier
  flagged the .asc + .dbc fixtures as personal/proprietary data shipped in
  main history. v3.50.5 adds 1 NEW sanitized fixture under tests/ which is
  the correct pattern (≤ 50 lines, generic names).