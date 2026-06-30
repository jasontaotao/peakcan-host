# v1.6.8 PATCH — `DbcApi.Load` Failure Surface (Design)

**Status**: Draft 2026-06-30
**Target ship**: v1.6.8 PATCH
**Closes**: v1.6.7 pre-ship code review MEDIUM #1
**Branch**: TBD (off remote main @ `d7c5c027`)
**Size**: Tidy PATCH (~3 production-file edits + 1 new test file)

## Context

v1.6.7 PATCH pre-ship code review flagged: `DbcApi.Load` does not
subscribe to `DbcService.LoadFailed`. When `DbcService.LoadAsync` fails
(IO error, parse failure, size cap exceeded, message-count cap exceeded),
the failure is fired via the `LoadFailed` event but `DbcApi.Load` reads
`_currentDocument == null` and returns a synthetic
`"Load completed but no document available"` — silent-swallow.
ClearScript V8 scripts invoking `dbc.load(path)` get no error information
(no `ErrorCode`, no message).

## Goal

Surface `DbcService.LoadFailed` payload
(`record Error(ErrorCode, string)`) through `DbcApi.Load` return value, so
scripts can distinguish categorical errors (`IoError`, `ParseFailure`,
`DbcFileTooLarge`) and react accordingly.

## Approach

Field-based last-error capture with volatile semantics:
- `DbcApi` ctor subscribes to `LoadFailed`; stores last failure into a
  `volatile Error? _lastLoadError` field.
- `OnDbcLoaded` handler clears `_lastLoadError = null` on successful load.
- `Load` method, after `await LoadAsync`: if `_currentDocument == null`,
  read `_lastLoadError`; return its Code + Message (or `"Cancelled"` if
  null, since cancellation is silent per `DbcService.LoadFailed` doc).

## Decisions

### D1 — Field-based capture (Approach A)

Chose over `TaskCompletionSource` (Approach B) and V8 exception throw
(Approach C). Rationale:
- Minimal diff — single field + 2 handlers + Load method body change
- No per-call subscribe/unsubscribe overhead
- Matches existing event-driven architecture (`DbcViewModel` +
  `DbcSendViewModel` subscribe the same way)
- Non-breaking for scripts (Approach C would break `result.success` checks)

### D2 — Extended return object with `errorCode` field

Current return: `new { success, messageCount, error }`.
New return: `new { success, messageCount, errorCode, error }`.

`errorCode` is `ErrorCode.ToString()` (e.g. `"IoError"`, `"ParseFailure"`,
`"DbcFileTooLarge"`, `"Cancelled"`). Non-breaking: ClearScript V8 dynamic
dispatch handles extra fields transparently; existing scripts checking
`result.success` or `result.error` continue to work.

### D3 — Cancellation distinguished as `errorCode = "Cancelled"`

Per `DbcService.LoadFailed` doc: "Cancellation is silent (no `LoadFailed`)".
When `LoadAsync` returns without a document and no `LoadFailed` fired,
treat as cancelled. Scripts can distinguish "I cancelled this" from
"this failed with reason X".

### D4 — Successful load clears stale error

`OnDbcLoaded` handler sets `_lastLoadError = null`. Prevents a previous
failed-load's error from leaking into the next successful load's return
(verified by Test 5).

### D5 — Scope discipline: silent-swallow fix only

Do NOT also fix:
- `_currentDocument` race condition (gap report #5; v1.6.7 concurrent test
  already characterizes last-write-wins; passes in practice)
- `DbcApi.Dispose` idempotency on unsubscribe (not load-bearing)
- Refactor `DbcService.LoadAsync` to throw exceptions instead of firing
  events (would break `DbcViewModel` + `DbcSendViewModel` event-based
  contracts — separate architectural decision)

Rationale: Tidy PATCH discipline; defer speculative scope.

## Files affected

| File | Type | Change |
|---|---|---|
| `src/PeakCan.Host.App/Services/Scripting/DbcApi.cs` | edit | ctor +1 line (subscribe), field +1, 2 handlers (`OnLoadFailed` new, `OnDbcLoaded` extend), `Load` method body change, `Dispose` +1 line (unsubscribe) |
| `tests/PeakCan.Host.App.Tests/Services/Scripting/DbcApiTests.cs` | NEW | 5 tests covering happy / IO / size-cap / cancel / state-after-failure |
| `docs/release-notes-v1.6.8.md` | NEW | per established PATCH template |

## Tests

`DbcApiTests.cs` (NEW — no existing test file for `DbcApi`):

| # | Test | Verifies |
|---|------|----------|
| 1 | `Load_Valid_Dbc_Returns_Success_True_With_MessageCount` | happy path (no regression) |
| 2 | `Load_Nonexistent_Path_Returns_Success_False_With_IoError_Code` | IO failure → `errorCode=IoError` |
| 3 | `Load_File_Exceeding_MaxFileSize_Returns_Success_False_With_DbcFileTooLarge_Code` | size cap → `errorCode=DbcFileTooLarge` (regression for v1.6.7 wiring) |
| 4 | `Load_With_Cancelled_CT_Returns_Success_False_With_Cancelled_Code` | cancellation → `errorCode=Cancelled` |
| 5 | `Load_After_Failure_Clears_Stale_Error_On_Next_Success` | D4 validation |

Test scaffolding:
- Use real `DbcService` instance + temp DBC fixture (same pattern as
  `DbcServiceTests` `LoadAsync_Valid_Dbc_Temp_File_Fires_DbcLoaded_With_NonNull_Document`)
- Constructor: `new DbcApi(NullLogger<DbcApi>.Instance, realDbcService)`
- For size cap test (Test 3): construct `DbcService` with low
  `MaxFileSizeBytes` via `DbcOptions` (v1.6.6 PATCH seam)

## Out of scope

- `_currentDocument` race (deferred; characterized in v1.6.7 concurrent test)
- `DbcApi.Dispose` idempotency
- Refactor `LoadAsync` to throw
- IronPython integration (design explicitly rejected in
  `2026-06-22-scripting-engine-design.md`)
- 6 untracked design/plan docs from v1.6.5 / v1.6.6 / v1.6.7 (housekeeping
  cycle, separate PATCH)

## Open questions

None.

## Cross-references

- `peakcan-host-v1-6-7-shipped` (memory) — pre-ship review flagged this
  MEDIUM #1
- `peakcan-host-v1-6-6-shipped` (memory) — `DbcOptions` + size cap /
  message-count cap that this PATCH surfaces to scripts
- `peakcan-host-v1-6-7-shipped` (memory) — `ErrorCode.DbcFileTooLarge`
  wiring (v1.6.7 PATCH Item 1) that Test 3 verifies reaches the script
- `2026-06-22-scripting-engine-design.md` — V8-only scripting (IronPython
  explicitly rejected)