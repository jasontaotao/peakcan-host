# v2.1.1 PATCH — Multi-frame window DBC message support (2026-07-02)

## Summary

Closes the user's ORIGINAL gap from the multi-frame request: v2.1.0 only handled **raw** frames; the Send-view DBC mode still couldn't send multiple messages in one shot. v2.1.1 PATCH extends the multi-frame window to support **DBC message rows** alongside raw rows, both in the same sequence.

| Row kind | Source of payload | Dispatch |
|----------|--------------------|----------|
| `Raw` (existing v2.1.0) | Manually entered ID/DataHex/Ext/FD/RTR/BRS/ESI | `row.Build()` → `CanFrame` |
| `Dbc` (NEW v2.1.1) | DBC message name + per-signal engineering values | `DbcEncodeService.Encode` → `CanFrame` with `message.Id` + encoded payload |

Mix raw + DBC rows in one sequence, dispatch in concurrent or sequential mode.

## Architecture changes

```
MultiFrameSequenceRow  (extended v2.1.1)
   + Kind enum (Raw / Dbc)
   + DbcMessageName (string)
   + DbcSignalValues (ObservableCollection<DbcSignalValue>)
   DbcSignalValue = (Name, Value?) tuple

SequenceSendService  (extended v2.1.1)
   SendAsync(IReadOnlyList<MultiFrameSequenceRow> rows, ...)
     for each row:
       if Raw  → row.Build()
       if Dbc  → DbcEncodeService.Encode(message, values), then build frame
       any build error → row-level failure, sequence continues

MultiFrameSendViewModel  (extended v2.1.1)
   + AvailableDbcMessages (bound to DbcService.Current?.Messages)
   + OnDbcLoaded (subscribes to DbcService.DbcLoaded, marshals to UI thread)

MultiFrameSendWindow.xaml  (extended v2.1.1)
   + "Type" column (Raw/Dbc dropdown)
   + RowDetails template: DBC message picker + per-signal value editor
     (visible only when RowKind == Dbc)

AppHostBuilder.cs  (DI wiring)
   SequenceSendService now takes DbcEncodeService + DbcService
   MultiFrameSendViewModel now takes DbcService

Composition/Converters/KindEqualsConverter.cs  (new)
   enum-value-to-Visibility converter (WPF DataTrigger on enum)

DbcEncodeService  (Core change)
   Unsealed + made Encode() virtual (was: sealed class, non-virtual method).
   Required for unit-test subclassing. No behavior change.
```

## Behavior change: per-row failure isolation

**Pre-v2.1.1** (v2.1.0): VM did upfront pre-validation. Any invalid row aborted the whole sequence with "Row N has invalid data" status.

**v2.1.1**: Service does per-row `TryBuildRow`. A row with bad hex (Raw) or unknown DBC message (Dbc) counts as a **failure of that row** and the sequence continues with the remaining rows. Final `Result` carries both `SentCount` and `FailureCount`.

This is a deliberate behavior change — upfront abort doesn't compose with mixed Raw+DBC sequences (a DBC row has no hex at all, and any "raw validation" on it is meaningless). The new test `SendCommand_InvalidHexData_CountsAsRowFailure_DoesNotAbortSequence` pins this.

## Test counts

| Suite | v2.1.0 | v2.1.1 | Δ |
|-------|--------|--------|---|
| Core  | 388    | 388    | 0 |
| App   | 477    | 484    | +7 (`SequenceSendServiceDbcTests` 7 tests) |
| Infra | 84     | 84     | 0 |
| **Total** | **949 + 6 SKIP** | **956 + 6 SKIP** | +7 |

Race-test flake counter preserved (26/26+).

### New tests (7)

| File | Test | Coverage |
|------|------|----------|
| `SequenceSendServiceDbcTests.cs` | `SendAsync_DbcRow_EncodesViaDbcEncodeService` | DBC kind → encoder dispatch |
|  | `SendAsync_DbcRow_ExtendedId_PreservesIdeBit` | Extended 29-bit ID + IDE bit strip |
|  | `SendAsync_MixedRawAndDbcRows_AllBuiltAndDispatched` | mixed sequence ordering |
|  | `SendAsync_DbcRow_NoDbcLoaded_CountsAsRowFailure` | no DBC loaded → row fails |
|  | `SendAsync_DbcRow_UnknownMessage_CountsAsRowFailure` | unknown msg name → row fails |
|  | `SendAsync_DbcRow_EncoderThrows_CountsAsRowFailure` | encoder exception → row fails |
|  | `SendAsync_DbcRow_EmptySignalValues_EncodesWithEmptyDict` | empty dict → encoder called with no values |

### Modified tests

| File | Test | Change |
|------|------|--------|
| `SequenceSendServiceTests.cs` | (all 9 tests) | `CanFrame[]` → `MultiFrameSequenceRow[]` via new `MakeRow(id)` helper (Raw kind with DataHex="DEAD") |
| `MultiFrameSendViewModelTests.cs` | `SendCommand_InvalidHexData_*` | renamed + rewritten to assert per-row failure isolation, not upfront abort |

## Files changed (8)

### Production (5)
- `src/PeakCan.Host.App/Models/MultiFrameSequenceRow.cs` (M: +Kind enum, +Dbc fields, +DbcSignalValue class)
- `src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs` (M: dispatch on row kind, +TryBuildRow, +DbcEncodeService/DbcService deps)
- `src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs` (M: +AvailableDbcMessages, +OnDbcLoaded, +2nd-arg ctor)
- `src/PeakCan.Host.App/Windows/MultiFrameSendWindow.xaml` (M: Type column + RowDetails template + KindEquals resource)
- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` (M: factory for SequenceSendService + MultiFrameSendViewModel with DBC deps)
- `src/PeakCan.Host.Core/Dbc/DbcEncodeService.cs` (M: unsealed + Encode virtual — testability)

### Added (2)
- `src/PeakCan.Host.App/Composition/Converters/KindEqualsConverter.cs` (new)
- `tests/PeakCan.Host.App.Tests/Services/MultiFrame/SequenceSendServiceDbcTests.cs` (new, ~250 lines, 7 tests)

### Docs (1)
- `docs/release-notes-v2.1.1.md` (this file)

## Process lessons (NEW — from this PATCH)

1. **Source-gen property name conflicts with nested enum** — `[ObservableProperty] private Kind _kind` generates a `Kind` property; nested `public enum Kind { ... }` declares `Kind.Kind.Raw`. Compiler error CS0102. Rename the field to `RowKind` (or use a non-conflicting property name) and update all usages. The source-gen won't warn you — it just emits code that conflicts at compile time.

2. **WPF DataTrigger on enum requires a converter** — `Style.Triggers` can't match enum values directly without an `IValueConverter` (the binding system can compare enum-to-string only via a converter). Pattern: enum property in the VM + a single-method converter that takes the enum value + the ConverterParameter string + returns Visibility. Cost: one tiny converter class per enum comparison.

3. **`System.ValueType` vs custom `ValueType` enum** — In the `PeakCan.Host.Core.Dbc` namespace, `ValueType` is a custom enum. When the test file imports both `PeakCan.Host.Core.Dbc` and `System` (implicit), `ValueType` becomes ambiguous — `using System;` shadows nothing but `using PeakCan.Host.Core.Dbc;` introduces a `ValueType` that conflicts with `System.ValueType`. Fix: fully-qualify `PeakCan.Host.Core.Dbc.ValueType` at the call site (or `using ValueType = PeakCan.Host.Core.Dbc.ValueType;` alias). CS0103 in unrelated places is a hint.

4. **Per-row failure isolation vs upfront validation tradeoff** — Upfront validation gives a clean error message ("row 3 invalid") but doesn't compose with mixed-kind sequences (Raw validation rules don't apply to DBC rows). Per-row isolation enables mixed sequences but loses the "show me the first error" UX. v2.1.1 chose isolation because mixed-kind was the new requirement; the VM status now reads "Sent N, failed M, iterations K" instead of "Row N has invalid data". Document the tradeoff in the test name so a future change doesn't accidentally revert it.

## Pre-ship review

- 0C / 0H / 0M / 0L
- Self-review:
  - DbcEncodeService unseal + virtual Encode — backward-compatible (sealed was the only thing preventing subclassing; no virtual-call cost in production)
  - DbcDocument lookup is linear scan — fine for typical DBC sizes (≤ few hundred messages); avoiding Core-layer API addition for a PATCH
  - Per-row failure isolation behavior change documented in test name + release notes
  - DI factories for SequenceSendService + MultiFrameSendViewModel now pass DBC deps; back-compat 1-arg ctor still exists for the SequenceSendService (matches v2.1.0 pattern)
- Race-test flake counter preserved (26/26+)

## Ship method

Tier 3 fallback (github.com:443 仍不通). 预计 13-call pipeline.

## Open follow-ups

- v2.1.2 PATCH candidate: Sequence persistence (Save / Load Sequence JSON, mirror Frame Library)
- v2.2.0 MINOR candidate: Replay-from-file (load ASC/CSV, dispatch as sequence) — bigger scope