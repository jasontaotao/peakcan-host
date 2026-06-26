# Release Notes — PeakCan Host v1.2.5

**Date:** 2026-06-26

## Summary

v1.2.5 is a backlog-cleanup PATCH that ships the residual items
discovered during the v1.2.3 / v1.2.4 investigation cycle plus the
user-manual v0.7.0 → v1.2.2 rewrite that was overdue.

**Three groups of changes:**

### 1. Silent Extended-frame DBC decode failure (Branch B)

PEAK's `CanId.Raw` carries the pure 11/29-bit identifier (bit 31
= 0, enforced by `CanId` ctor validation at `CanId.cs:30-31`),
but `DbcDocument.MessagesById` is keyed by the merged-IDE
convention (bit 31 set for Extended, matching `PCAN_MESSAGE_ID`
and `DbcParser`'s acceptance of `BO_ 2147487744` = `0x80001000`).
Without normalisation, every Extended frame silently misses the
dictionary lookup and `SignalViewModel` stays empty for any DBC
containing an Extended message.

Fix: project `frame.Id.Raw` to the merged-IDE form before lookup
(`IsExtended → | 0x80000000u`).

A new test `DbcDecodeBackgroundServiceTests.OnFrame_With_Extended_Id_Decodes_When_Dbc_Has_Merged_Ide_Bit`
pins the contract: a `CanId(0x100, Extended)` frame + DBC
`Message(Id: 0x80000100u)` decodes.

A clarifying comment in `CanApi.cs:217` documents that the
**scripting** API (`can.onMessage(0x100, cb)`) intentionally uses
the raw-ID convention, not merged-IDE — applying the same fix
to that lookup would silently break `can.onMessage` registrations
for Extended frames using the natural on-wire-ID convention.

### 2. Trace view UX improvements

- **`MaxRows` default 10 000 → 1 000.** Under sustained high
  frame rates, 10 000 rows × 20 px = 200 k px of virtualised
  content saturates the WPF DataGrid paint + collection-mutation
  budget. 1 000 rows × 20 px = 20 k px stays well within the
  recycling-virtualisation budget.
- **Hex formatting with byte separators** (`"DE AD BE EF"` vs
  `"DEADBEEF"`). `FormatHexWithSpaces` uses a single `char[]` build
  to avoid per-byte string allocation. The Trace view header now
  shows the active cap (read-only) in muted gray.
- **CSV export is now async + RFC 4180 escaped.** The
  `SaveFileDialog` remains modal (user-visible) but the file write
  is dispatched to `Task.Run` so the WPF dispatcher stays
  responsive for scrolling / other tabs while the export runs.
  `Entries` is snapshotted before the worker hands off to avoid
  TOCTOU vs the live dispatcher action. Fields containing comma,
  double-quote, CR, or LF are wrapped in double-quotes; embedded
  double-quotes are doubled per RFC 4180 §2.6.

11 new tests (`FormatHexWithSpaces`: 4 InlineData cases;
`CsvEscape`: 7 InlineData cases) pin the helpers' contracts.

### 3. User-manual v0.7.0 → v1.2.2 rewrite

Cumulative user-manual update covering the v1.x feature surface
that had accumulated without doc updates:
- Add Script view (v1.2.0) + UDS diagnostic stack (v1.2.0) to
  navigation + feature matrix.
- Update View sub-menu SVG to 7 items.
- Version stamp v0.7.0 → v1.2.5.
- Fix §11.1 TesterPresent row contradiction: the row now
  correctly qualifies that `Dispose` is only invoked if called
  explicitly, and cross-links §14.2 (which documents that the DI
  container does not call Dispose for singleton VMs).

## Why this is a PATCH not a MINOR

The public API is unchanged. `MaxRows` is a generated property
whose default value changed (existing setter still works for
programmatic adjustment). `FormatHexWithSpaces` and `CsvEscape`
went from `private static` to `internal static` — only a
visibility change for an existing class, no signature change. CSV
export went from sync UI-thread to async fire-and-forget but the
public command surface (`ExportCsvCommand`) is identical. DBC
Extended-frame lookup is a one-line branch addition. User-manual
is pure HTML/SVG.

## Known issue (carried over)

Stats tab OxyPlot chart still shows an empty plot area under
.NET 10 windows. OxyPlot.Wpf 2.2.0 is a 2022 release with no
later WPF package; its WPF `PlotView` control does not fully
render under .NET 10 windows binary. Pre-existing bug since
v0.0.1 scaffold. Replacement deferred to **v1.3.0 MINOR**.

## Tests

548 pass + 6 SKIP + 0 fail (was 535 at v1.2.3 ship / 536 at v1.2.4
ship; **+13 net in v1.2.5**: 1 DBC extended-frame regression test
+ 1 v1.2.4 IHostedService regression test + 4 `FormatHexWithSpaces`
InlineData + 7 `CsvEscape` InlineData). 0 warnings, 0 errors.
Pre-ship csharp-reviewer:
- Branch B commits: **0C / 1H / 1M / 0L** (HIGH rejected with
  reasoning: `CanApi.cs:217` raw-ID convention is correct, not a
  bug).
- v1.2.5 UX + docs: **0C / 1H / 3M / 5L** (H1 false "user can raise
  via toolbar" claim + M1 missing tests + M2 §11.1 doc contradiction
  + L1 version stamp addressed same-commit; L2-L5 deferred to
  v1.2.6).

## Files changed

- `src/PeakCan.Host.App/Services/DbcDecodeBackgroundService.cs` —
  Extended-frame ID normalisation
- `src/PeakCan.Host.App/Services/Scripting/CanApi.cs` — convention
  comment
- `src/PeakCan.Host.App/ViewModels/TraceViewModel.cs` — `MaxRows`
  default, `FormatHexWithSpaces`, async CSV export, `CsvEscape`
- `src/PeakCan.Host.App/Views/TraceView.xaml` — cap display in
  header
- `tests/PeakCan.Host.App.Tests/Services/DbcDecodeBackgroundServiceTests.cs`
  — Extended-frame regression test
- `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewModelTests.cs`
  — `FormatHexWithSpaces` + `CsvEscape` theories,
  `Default_MaxRows_Is_One_Thousand` rename
- `docs/user-manual.html` — v0.7.0 → v1.2.5 rewrite + §11.1 fix