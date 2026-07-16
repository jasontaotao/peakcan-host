# v3.50.5 PATCH — Watch List Decoded Enum Text + Tracker 4-line Tooltip

## Summary

Renders DBC `VAL_` table enum text (e.g. "高压上电") in the Trace Viewer
watch list Latest / Δ / Blue columns and in the OxyPlot Tracker tooltip.
Mirrors the CANoe layout the user shared as a screenshot.

## Changes

**Core** (PeakCan.Host.Core):
- `SignalDecoder.TryDecodeEnumText(Signal, double, DbcDocument) → string?` — new API. Returns null on any miss path (no ValueTableName / table missing / value out of table); callers fall back to numeric formatting.

**App** (PeakCan.Host.App):
- `WatchedSignalRow.Dbc` field + property (plain C#, sister of v3.50.0 `Signal` field precedent; CommunityToolkit.Mvvm source-gen limitation)
- `WatchedSignalRow.LatestText` / `BlueText` / `DeltaText` — string computed properties preferring DBC VAL_ text, falling back to F2 numeric. NaN → "—"; enum Δ → "—"
- XAML bindings for Latest / Δ / Blue columns switched to `.Text` properties (drop `DoubleNanToStr` converter on those 3 columns)
- `EnumTrackerLineSeries` — `OxyPlot.Series.LineSeries` subclass overriding `GetNearestPoint` to rewrite the tracker text into the 4-line CANoe layout
- `BuildOneChartSeriesForSource` — switch `LineSeries` → `EnumTrackerLineSeries`
- `OnDbcLoaded` — fan-out `Dbc` to all existing watch rows so DBC reload triggers INPC for text columns

**Tests** (+6 new tests, 1 sanitized fixture):
- `tests/PeakCan.Host.Core.Tests/SignalDecoderTests.cs` — +4 `TryDecodeEnumTextTests`
- `tests/PeakCan.Host.App.Tests/ViewModels/WatchedSignalRowTests.cs` — +2 `WatchedSignalRowTextTests`
- `tests/PeakCan.Host.Core.Tests/Dbc/Fixtures/sample-with-val.dbc` — NEW, sanitized (no real CAN IDs / nodes / OEM strings)

## Test plan

- [x] `dotnet test --filter "FullyQualifiedName~TryDecodeEnumText"` — 4/4 PASS
- [x] `dotnet test --filter "FullyQualifiedName~WatchedSignalRow"` — all 8 PASS (4 existing + 2 new + 2 from text class)
- [x] `dotnet test` (full suite) — App 822 / Core 460 / Infra 89 PASS; 1 transient flake in Core (`IReplayServiceTests.SetSpeed_PreservesCurrentTimestamp`) is W34-W50 sister pattern, isolated run passes, NOT introduced by v3.50.5
- [x] `dotnet build src/PeakCan.Host.App/` — 0 errors, 0 new warnings

## Sister patterns

- W38 v3.50.4 PATCH (immediate predecessor) — same 5-task TDD plan + squash + tag flow
- v3.50.0 MINOR `WatchedSignalRow.Signal` field precedent (plain C#, not `[ObservableProperty]` due to XAML temp csproj cannot pull `PeakCan.Host.Core.dll`)
- v3.50.4 PATCH `PlotController` namespace lesson (OxyPlot core, not OxyPlot.Wpf) — applied to `EnumTrackerLineSeries` parent class lookup
- W19 R1 LESSON (verbatim re-extraction, no fabrication) — applied throughout T1-T3

## v3.50.5 NEW LESSON

**OxyPlot 2.2.0 tracker extension is `override GetNearestPoint`, NOT `ITrackerConverter`.**
The spec assumed `ITrackerConverter` interface exists; verified absent via
ilspycmd decompile of `OxyPlot.Series.LineSeries`. The internal pipeline is:
`base.GetNearestPoint` builds `TrackerHitResult` with `.Text` set via
`StringHelper.Format(TrackerFormatString, item, Title, XAxis.Title,
XAxis.GetValue(X), YAxis.Title, YAxis.GetValue(Y))` — fixed 6 placeholders,
no enum-text lookup injection point. Real extension: subclass `LineSeries`,
override `GetNearestPoint(ScreenPoint, bool)`, mutate `hit.Text` after
`base.GetNearestPoint` returns.

## Out of scope

See [release notes](docs/release-notes-v3.50.5.md) §Out of scope.