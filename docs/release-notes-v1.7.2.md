# v1.7.2 PATCH — Release Notes (2026-07-01)

## Summary

3-item Tidy: `PathOptions.cs` EOF newline + `DbcApi.Load(CancellationToken)`
+ Option B housekeeping (commit v1.7.1 cycle's untracked design/plan docs).

## What's changed

### Item 1 — `PathOptions.cs` newline-at-EOF (cosmetic)

`src/PeakCan.Host.Core/Path/PathOptions.cs` now ends with a trailing newline
(closes v1.7.0 LOW finding). The previously-cited `appsettings.json` EOF newline
was already present (verified via `od -c`), so it was dropped from scope.
No behavior change.

### Item 2 — `DbcApi.Load(CancellationToken)` (API additive)

`IScriptDbcApi.Load` and `DbcApi.Load` now accept an optional
`CancellationToken ct = default` parameter, propagated to the existing
`DbcService.LoadAsync(path, ct)` overload. Closes the v1.6.8 PATCH carry-over —
`DbcService.LoadAsync` already accepted CT; the gap was at the `DbcApi`
script-facing boundary.

**For ClearScript V8 scripts**: `dbc.load(path)` is unchanged. The V8 binder
ignores default-value parameters, so existing script calls work bit-identically.

**For C# callers** (tests, future non-script consumers): pass a
`CancellationToken` to cancel an in-flight DBC load. Cancellation surfaces
as `{ success: false, messageCount: 0, errorCode: "Cancelled", error:
"Load was cancelled" }` — the existing silent-cancel branch already handles
this case (no new code path needed; the CT just enables reaching it).

### Item 3 — Housekeeping

Committed 2 design/plan docs from the v1.7.1 PATCH cycle (held back per
the Option B convention that has now been followed for 6 consecutive PATCHes
since v1.6.6). v1.7.2 cycle's own design + plan docs are similarly held back
for v1.7.3 PATCH Item 3.

## Test counts

| Suite | v1.7.1 | v1.7.2 | Delta |
|-------|--------|--------|-------|
| Core  | 353    | 353    | 0     |
| App   | 436    | 437    | +1 (cancellation test) |
| Infra | 84     | 84     | 0     |
| **Total** | **873 + 6 SKIP** | **874 + 6 SKIP** | **+1 net** |

The +1 test (`Load_With_CancelledToken_Returns_Cancelled_Code`) verifies that
`DbcApi.Load` with a pre-cancelled token returns the expected
`errorCode="Cancelled"` envelope via the production silent-cancel branch
(`DbcService.cs:162-165` + `DbcApi.cs:115-121`).

## Compatibility

- **API**: additive change. Default-value parameter; existing callers
  (V8 scripts, C# tests) work bit-identically.
- **Binary**: `IScriptDbcApi.Load` interface signature changed (parameter
  added with default value). Any consumer compiled against the prior signature
  continues to work because the new parameter has a default value.
- **Wire format**: `DbcApi.Load` returns an anonymous object with the same
  4 fields (`success`, `messageCount`, `errorCode`, `error`) — no change.

## Migration

None required. Existing script calls continue to work.

## Known follow-ups

- **v1.7.3 PATCH** (next): close the 2 MEDIUMs deferred from v1.7.1 PATCH
  review (`ScriptErrorType.ResourceLimit` enum + `when` filter on
  `ScriptEngineException.Message`; spec update for `CanFrame` struct/ROM
  types). Plus Option B housekeeping for v1.7.2 cycle's design + plan docs.
- **v1.7.1 MINOR** (future): OEM `IKeyDerivationAlgorithm` concrete
  implementation — the last v1.6.0 MINOR item. Needs crypto review first.
- **v1.7.0 LOW cosmetic**: newline-at-EOF on `appsettings.json` was confirmed
  already present in v1.7.2 PATCH pre-flight; closed.

## Ship metadata

- PR: `feature/v1-7-2-patch` → `main` (squash)
- Tag: `v1.7.2`
- Branch base: `origin/main` @ `bd7555c` (v1.7.1 squash)
- Local `main` revert to `5c522ca` (v1.7.0) preserved per v1.7.1 PATCH ship precedent.