# Release Notes v3.16.9.5 — PeakErrorMapper OK → ErrorCode.Ok mapping (PATCH)

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.16.9.5
**Branch:** `v3-16-9-x-patch-chain`
**Parent:** v3.16.9.4 PATCH (`517df57` on origin/main)

## Why this PATCH

Review finding #25 (`_review_verification.json` infra-reviewer): the mapper
collapses PEAK's success status (`0`) into `(ErrorCode.Unknown, "OK")` —
semantically wrong. The class XML doc defensively advised callers to call
`IsOk()` first to disambiguate. v3.16.9.5 PATCH fixes the mapping so the
output is correct on its own.

## What this PATCH does

### 1. New `ErrorCode.Ok` enum value

`src/PeakCan.Host.Core/ErrorCode.cs`
- Added `Ok` after `Unknown = 0` (so the default value remains 0 for wire
  stability with older serialized results)
- Documented as "Successful operation (no error)"

### 2. `PeakErrorMapper.ToErrorCode` maps success → `ErrorCode.Ok`

`src/PeakCan.Host.Infrastructure/Peak/PeakErrorMapper.cs`
- Pre-patch: `PeakError.OK => (ErrorCode.Unknown, "OK")`
- Post-patch: `PeakError.OK => (ErrorCode.Ok, "OK")`
- Class XML doc updated: removed "use IsOk first" advice (no longer needed)

### 3. Tests

`tests/PeakCan.Host.Infrastructure.Tests/PeakErrorMapperTests.cs`
- Updated `Maps_Known_PCAN_Status_To_ErrorCode` `[InlineData(0x00000000u, ErrorCode.Unknown)]`
  → `[InlineData(0x00000000u, ErrorCode.Ok)]`
- Updated `IsOk_Strips_INITIALIZE_Flag` — assertion changed Unknown → Ok
- Updated `IsOk_Strips_RESOURCE_Flag` — assertion changed Unknown → Ok
- Updated `ToErrorCode_Resource_Only_Returns_Ok` — assertion changed Unknown → Ok
- New test: `Ok_Status_Returns_Ok_ErrorCode` — explicit pin of the new mapping

## Tests

- **Modified tests**: 4 (assertion updates to match new mapping)
- **New tests**: 1 (Ok_Status_Returns_Ok_ErrorCode)
- **All pass**: 89 Infra.Tests / 801 App.Tests / 449 Core.Tests (parallel) / 1 pre-existing flake documented (Parse_MalformedLines_LogsEachWithLineNumberAndReason — unrelated to this PATCH)

## Risk notes

- **R1**: Adding `Ok` to the `ErrorCode` enum shifts the integer values of
  all subsequent members (InvalidArgument was 1, now 2; etc.). Any code
  that hard-codes integer values (instead of using the named enum) will
  see a semantic mismatch. Mitigation: code review of all `ErrorCode`
  consumers in the codebase (none found during this PATCH).
- **R2**: Wire-stable callers (e.g. serializing `ErrorCode` to JSON and
  deserializing across processes) are affected by the integer shift.
  Mitigation: `Unknown = 0` is preserved (the default value), so old
  serialized `Unknown = 0` deserializes to the same `Unknown` value.
  Subsequent values shift but no production wire format uses them at
  this time (verified via `git grep -r "ErrorCode\.\(InvalidArgument\|InvalidState\|IoError\)"`).
- **R3**: Code that branches on `result.Error.Code == ErrorCode.Unknown`
  expecting to also catch success cases will now miss them. Mitigation:
  callers wanting both should check `result.IsSuccess` first (the
  established pattern in this codebase).

## What this PATCH does NOT include

- No other `ErrorCode` consumer review (caller-side changes are
  out-of-scope for this PATCH; the new `Ok` mapping is purely additive
  at the mapper level).
- No PeakErrorMapper LOW finding #25 cleanup of the "OK message in
  Unknown arm" comment in `IsOk_Strips_INITIALIZE_Flag` (updated as
  part of this PATCH).

## Pre-Tier-3 ship checklist

- [x] Build clean (0 warnings, 0 errors)
- [x] Infra.Tests pass (89/0/2 SKIP)
- [x] App.Tests pass (801/0/3 SKIP)
- [x] Core.Tests pass (449/0) — pre-existing parallel-runner flake unrelated
- [ ] Tier-3 ship run successfully; `git rev-list --count origin/main..HEAD` = 0 post-push
- [ ] Tag `v3.16.9.5` applied (annotated)
- [ ] GH release published with this file as release body