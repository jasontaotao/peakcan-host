# Release Notes v3.14.1 — DBC extended-frame ID lookup fix (PATCH)

**Released:** 2026-07-08
**Parent:** v3.14.0 MINOR (`ada4162`)
**Tag:** v3.14.1
**Branch:** `feature/v3-12-0-minor`

## Highlights

This PATCH fixes a critical regression: when the user loaded a DBC + .asc file pair, **the Trace Viewer's Signals + chart subplots were empty even though both files loaded successfully**. v3.14.0 MINOR closed 7 HIGH bugs but missed the ID-format mismatch because the code review focused on async/threading issues, not on the contract between DBC and ASC.

The bug was a **CAN ID format mismatch**:
- DBC stores extended-frame IDs with the IDE bit set in bit 31: `<29-bit-raw> | 0x80000000` (e.g. `0x1802F3D0 | 0x80000000 = 0x9802F3D0 = 2550330320 dec`)
- Vector `.asc` extended-frame IDs are just the 29-bit raw with a trailing `x` marker that the parser strips: `0x1802F3D0` (no IDE bit)

The Trace Viewer's lookup used the DBC's IDE-merged ID (`0x9802F3D0`) to query the ASC frame bucket (keyed by `0x1802F3D0`) — **never matched**.

| Commit | Fix | Behavior change |
|--------|-----|------|
| (local) | (1) DbcDocument.MessagesById is now keyed by `m.Id & 0x7FFFFFFF` (stripped IDE bit). | Runtime lookup against incoming .asc frame ids works. |
| (local) | (2) `BuildSignalRows` + `BuildChartSeries` use `msg.Id & 0x7FFFFFFF` to query the frame bucket. | The DBC-message-side of the lookup also strips the IDE bit. |
| (local) | (3) `OnRegistrySourcesChanged` now calls `RebuildSignalsCore` at the end. | Loading a new .asc via `AddTraceAsync` re-decodes the (already-loaded) DBC messages against the new source's frames. Pre-fix, the user had to reload the DBC to refresh. |
| (local) | (4) `Directory.Build.props` version bumped to 3.14.1. | Window title shows the real version. |
| (local) | (5) Updated legacy test `Parses_Extended_Id_With_Ide_Bit` + `E51PtBmsSpecificValuesTests` to use the masked lookup key. | Tests assert the new contract. |

**Test delta:** 1302 + 5 SKIP / 0 fail → **1302 + 5 SKIP / 0 fail** (test count unchanged; the new behavior is verified by 1 modified DbcParser test + the legacy test update)
**Code stats:** 5 files changed, +~50 / -10 LoC net

## Root cause

The bug was a **3-part chain**:

1. **`DbcParser` line 223**: `var byId = _pendingMessages.ToDictionary(m => m.Id);` — kept the IDE bit in the key. The DbcDocument docstring says `MessagesById` is keyed by "32-bit ID with the IDE bit merged" — but runtime lookup against incoming .asc frame ids (which never have the IDE bit) never matched.
2. **`TraceViewerViewModel.BuildSignalRows` line 1089**: `if (!byId.TryGetValue(msg.Id, out var matching) ...)` — used the DBC's `msg.Id` (with IDE bit) to query a bucket keyed by raw ASC frame ids (no IDE bit). Mismatch.
3. **`TraceViewerViewModel.OnRegistrySourcesChanged`**: did not call `RebuildSignalsCore`. Loading a new .asc updated the service dictionary + master, but the Signals/Series were not rebuilt against the new frames. The user had to reload the DBC to refresh.

The legacy test `Parses_Extended_Id_With_Ide_Bit` codified the OLD (buggy) contract: it asserted that `MessagesById` was keyed by the IDE-merged value, not the masked 29-bit value. v3.14.1 PATCH reverses that.

## User-visible impact

Before: loading a DBC + .asc pair produced an empty Trace Viewer — Sources list showed the file, but Signals + chart subplots were blank. The user assumed the bug was elsewhere (DBC format mismatch, parse failure, etc.). Actually the bug was a 1-line ID-format mismatch in 3 places.

After: 316 signals decoded from the user's test files (`C:\Users\13777\Desktop\纯电动汽车CAN 通信协议V4.6(吉高编写).dbc` + `C:\Users\13777\Desktop\Logging.asc`), with real values like `BMU_BMSSplChgSt = 7.00 bit`, `BMU_BattCur = 22.20 A`, `BMU_FulEmpReq = 0.00 bit`. 316 chart subplots populate.

## Lessons (1-of-1, captured in PKM topic)

`dbc-extended-frame-id-stores-29bit-raw-with-ide-bit-merged-runtime-lookup-must-mask` — DBC stores extended-frame IDs as `<29-bit-raw> | 0x80000000` (IDE bit merged into bit 31). Vector `.asc` extended-frame IDs are just the 29-bit raw with a trailing `x` marker (no IDE bit). The two formats differ ONLY in bit 31. Runtime lookup against incoming frame ids must mask `& 0x7FFFFFFF` on at least one side. Pattern: when two sources of CAN IDs (DBC parser + ASC parser) have different IDE-bit conventions, normalize at the boundary closest to the consumer (here: `DbcDocument.MessagesById` key) so consumers don't repeat the mask.

`onregistrysourceschanged-must-rebuildsignalscore-not-just-update-servicedict` — `OnRegistrySourcesChanged` is a "configuration changed" hook: it updates the service dictionary, master, totals, etc. But the Signals + Series collections are derived from (sources × DBC.messages) — adding a new source changes the derivation even if the DBC didn't change. The fix is to always call `RebuildSignalsCore` at the end of `OnRegistrySourcesChanged`. The pre-fix behavior (only rebuild on DBC change) silently broke "load DBC then load .asc" workflows.

`legacy-test-codifying-buggy-contract-must-be-reversed-with-the-fix-not-just-ignored` — the legacy test `Parses_Extended_Id_With_Ide_Bit` codified the OLD contract (`MessagesById` keyed by IDE-merged). A fix that changes the contract must also update the legacy test, not just add a new one. Otherwise the legacy test is a tripwire for future "why doesn't this work?" investigations.

## NEXT

- v3.14.2 PATCH: B1-B11 MEDIUM review items (per the code review backlog). None are safety-critical; the user can now actually USE the Trace Viewer.
- v3.15.0 MINOR: scope TBD.
- Re-run the full code review on the v3.14.1 state to ensure the regression-fix tests + new contract are captured.
