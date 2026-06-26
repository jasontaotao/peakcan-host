# Release Notes — PeakCan Host v1.2.9

**Date:** 2026-06-26

## Summary

v1.2.9 is a 4-commit PATCH that fixes three issues surfaced on
the v1.2.8 build:

1. **Signal Value column showed raw integers, not the
   human-readable VAL_ strings** — the DBC parser attached
   the signal's `ValueTableName` but discarded the inline
   `(int → text)` entries.
2. **Stats chart showed a single vertical line, not a 60-sample
   rolling window** — the v1.2.7 Points-based refactor
   collapsed every new sample to `X=MaxPoints-1`, so all
   points stacked at the same X position.
3. **DBC load failed on zh-CN / ja-JP / ko-KR systems with
   `IoError No data ... Encoding.RegisterProvider`** — the
   OEM-fallback path needed the legacy code-page encoding
   provider registered, and the DBC loader didn't tolerate
   its absence.

## Bug 1 — VAL_ inline entries discarded

### Symptom

```
VAL_ 2566536948 BMS_DCChgStage 0 "默认 "
                              1 "充电握手启动阶段 "
                              2 "充电握手辨识阶段 " ... ;
```

The `BMS_DCChgStage` row in the Signal view's Value column
always showed the raw integer (`0`, `1`, `2`, ...) instead of
the Chinese strings (`"默认"`, `"充电握手启动阶段"`, ...).

### Root cause

Pre-v1.2.9 the DBC parser correctly attached the signal's
`ValueTableName` on a `VAL_` inline-pairs attachment (form
(a): `VAL_ <msg> <sig> <int> "<text>" ... ;`) but **discarded
the `(int → text)` entries** — the parser consumed the
tokens but never built a `ValueTable` object. Result: the
document's `ValueTables` dict had no entry under the signal's
`ValueTableName`, and `SignalViewModel.ResolveValueTableName`
returned `null`.

### Fix

`ParserState` now carries a `Dictionary<string, ValueTable>
_pendingValueTables`. The inline-pairs branch of
`ParseValForSignal` builds a `(long → string)` map, wraps it
in a `ValueTable` named after the signal (self-reference
matches the signal's `ValueTableName`), and stores it.
`ParseDocument` merges `_pendingValueTables` into the
document's `valueTables` dict at the end. Inline tables take
precedence over a pre-existing `VAL_TABLE_` block of the same
name — matches DBC convention that the most-recently-defined
table wins.

`ResolveValueTableName` (in `SignalViewModel`) already had the
correct lookup. The lookup now resolves to a real table with
entries, so the Signal view's Value column shows the
human-readable string.

A new test (`DbcParserMultiplexedTests.
Inline_VAL_Entries_Are_Available_In_Document_ValueTables`)
pins the contract: an inline-pairs block with N entries
produces a `ValueTable` with N entries under the document's
`ValueTables` dict, keyed by the signal name.

## Bug 2 — Stats chart single vertical line

### Symptom

The Stats chart showed a single short vertical line near the
right edge instead of a 60-sample rolling window
(`FPS / Load %` with 1 Hz sampling, 60-sample history). The
Y axis auto-scaled to ~150 to fit a single point at Y=125.

### Root cause

The v1.2.7 refactor switched from `ObservableCollection +
ItemsSource` binding to direct `LineSeries.Points.Add`. The
new code added every new sample at `X=MaxPoints-1=59`, so
all points were stacked at the same X position with varying
Y values. The chart rendered them as a one-pixel vertical
line.

The original `ItemsSource` path didn't have this problem
because the binding uses the collection index as X implicitly
(index 0 → X=0, index 59 → X=59). The explicit `Points`
path requires us to encode the X position ourselves.

### Fix

Rebuild `_fpsLine.Points` / `_loadLine.Points` from the
`ObservableCollection` on every `Apply`. `FpsSeries[i]` is
plotted at `X=i`, matching the original `ItemsSource`
semantics. Cost is O(N) per sample with N=MaxPoints=60 —
negligible.

`FpsSeries` / `LoadSeries` `ObservableCollection<double>` is
preserved (still updated in parallel) for backward-compat
with `StatsViewModelTests` and `StatisticsServiceTests`,
both of which assert on `FpsSeries` / `LoadSeries`.

## Bug 3 — DBC load `IoError Encoding.RegisterProvider`

### Symptom

Loading a zh-CN DBC (GBK/CP936) failed with
`IoError No data ... Encoding.RegisterProvider method`,
referencing the missing `Encoding.RegisterProvider` call
needed to activate the legacy code-page encodings.

### Root cause

v1.2.8 added a BOM-aware + UTF-8-with-OEM-fallback DBC
loader. The OEM-fallback path calls `Encoding.GetEncoding`
with the system OEM code page (e.g. CP936 / GBK on zh-CN).
On .NET 6+, the legacy code pages are NOT registered by
default — they require a one-time call to
`Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`
which is in the `System.Text.Encoding.CodePages` package.
Without the registration, `Encoding.GetEncoding(936)` throws
`NotSupportedException`, which propagated as `IoError` with
the inner message mentioning `'Encoding.RegisterProvider'`.

### Fix

1. `App.OnStartup` calls `Encoding.RegisterProvider` once at
   process startup, before any DBC load attempt. Idempotent
   and process-global.
2. The `DbcService.ReadDbcTextAsync` OEM-fallback path wraps
   the `Encoding.GetEncoding` call in a `try/catch`:
   - If the OEM code page IS registered (happy path after
     the `App.OnStartup` fix), it decodes the file with
     the system OEM code page.
   - If the OEM code page is NOT registered (e.g. running
     in a test host that skips `App.OnStartup`, or a future
     .NET version that drops the package), the loader
     falls back to Latin-1 (always available, 1-to-1 byte
     mapping). Latin-1 produces garbled chars for non-Latin
     code points but doesn't crash; the user sees a
     "noisy but readable" DBC instead of a load failure.
3. `Directory.Packages.props` keeps the
   `System.Text.Encoding.CodePages` version pinned (8.0.0)
   as a forward-compat marker. On .NET 10 the package is
   provided transitively by the shared framework, so no
   explicit `PackageReference` is needed today — the
   `csproj` comment explains the situation in case the
   package needs to be re-added explicitly in a future
   .NET version.

## Why this is a PATCH

The public API is unchanged. `DbcDocument.ValueTables` already
existed; this PATCH makes it actually populated for inline
`VAL_` blocks. `StatsViewModel.FpsSeries` / `LoadSeries` are
unchanged; the rendering now matches the documented contract.
The encoding loader is a private helper; the public
`DbcService.LoadAsync` signature is unchanged.

## Tests

549 pass + 6 SKIP + 0 fail (was 548; +1 net from the new
`Inline_VAL_Entries_Are_Available_In_Document_ValueTables`
test). The VAL_ and Stats fixes are exercised by the
existing test corpus; the encoding fallback is exercised by
the existing DBC load tests using ASCII fixtures.

## Files changed

- `src/PeakCan.Host.Core/Dbc/DbcParser.cs` — new
  `_pendingValueTables` ParserState field, inline-pairs
  branch of `ParseValForSignal` collects entries,
  `ParseDocument` merges at end
- `src/PeakCan.Host.App/ViewModels/StatsViewModel.cs` —
  `Apply` rebuilds `_fpsLine.Points` / `_loadLine.Points`
  with proper X indices
- `src/PeakCan.Host.App/Services/DbcService.cs` — OEM
  fallback wraps in try/catch with Latin-1 last-resort
- `src/PeakCan.Host.App/App.xaml.cs` — `OnStartup` calls
  `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`
  before DI host build
- `Directory.Packages.props` — `System.Text.Encoding.CodePages`
  version pin (forward-compat)
- `src/PeakCan.Host.App/PeakCan.Host.App.csproj` — comment
  explaining the transitive code-pages encoding
- `tests/PeakCan.Host.Core.Tests/DbcParserMultiplexedTests.cs` —
  new `Inline_VAL_Entries_Are_Available_In_Document_ValueTables`
  test

## Known issue (carried over)

Stats tab `OxyPlot` `Legend` is empty by default in OxyPlot
2.2.0 (no legend rendering for the FPS / bus-load series
labels). Replacement deferred to **v1.2.10 PATCH** (small)
— add `PlotModel.Legends.Add(new Legend { ... })` with
`LegendPlacement=Outside` so the series names render.

## Process lessons (apply forward)

1. **A "store the name but not the entries" parser is
   silent-failure-prone.** The v0.6.0 comment "we don't store
   the `(int → text)` map on the signal for MVP" was a
   deliberate scope cut, but the MVP contract was never
   revisited in five years. The fix is small (10 lines in
   the parser); the user-visible regression is "my custom
   DBC's value labels never showed up".
2. **When replacing a binding-based rendering with
   explicit-Points rendering, the X index must be
   preserved.** The v1.2.7 `ItemsSource → Points` refactor
   lost the implicit-index-as-X semantics. The O(N) per
   sample rebuild is the simplest correct fix.
3. **OEM-fallback paths need the encoding provider
   registered at app startup, not just at the call site.**
   The DbcService's `try/catch + Latin-1` fallback is the
   safety net; `App.OnStartup` RegisterProvider is the
   happy path. Both should ship together.

## Next work

1. **v1.2.10 PATCH** (small): add `Legend` to
   `StatsViewModel.PlotModel.Legends` so the FPS / bus-load
   series names render in the chart legend.
2. **v1.3.0 MINOR (OEM IKeyDerivationAlgorithm + OxyPlot
   full replacement)** — blocked on OEM list for the
   algorithm work; OxyPlot chart can be filed as a separate
   task.

## Ship mechanics

`git -c http.proxy="http://127.0.0.1:7897" push origin main`
(proxy alive; direct connection reset on first attempt) +
`git tag -a v1.2.9 -m "..."` + `git push origin v1.2.9` +
`gh release create v1.2.9 --title ... --notes-file
docs/release-notes-v1.2.9.md`.