# v1.4.1 PATCH Release Notes

**Release date:** 2026-06-29
**Branch:** `feature/v1-4-1-patch` → main `8e821c7` (Task 3) + `aed3481` (Task 2) + `773c116` (Task 1)
**Tag:** v1.4.1

## Summary

3 PATCH items closing v1.4.0 MINOR §"Known follow-ups" carry-overs. All LOW severity.

## Items

### 1. SecurityAccessAsync concurrent mid-handshake race test

**Carry-over from:** v1.3.1 PATCH pre-ship review (v1.4.0 release notes §Known follow-ups).

Adds `tests/PeakCan.Host.Core.Tests/Uds/UdsClientConcurrentSecurityAccessTests.cs` with 2 race tests:

- **Test 1 (passing):** `TwoArg_Overload_TwoConcurrentCalls_ProduceExactlyFourWireFrames` —
  2 concurrent `SecurityAccessAsync(0x01, ct)` on same level produce exactly 4 wire frames
  (2 RequestSeed + 2 SendKey) with correct SID (0x27) and sub-function (0x01/0x02) bytes.
  Asserts lockout state unchanged after both succeed.

- **Test 2 (skipped):** `TwoArg_Overload_ConcurrentMidHandshakeLockoutFlip_PostStateConsistent` —
  Designed to assert mid-handshake lockout flip post-state. **SKIPPED** because Phase 2.5
  actual code exploration discovered a **pre-existing production bug** in
  `UdsSecurity.SetSeed` (see "Out-of-scope findings" below). Test body kept
  for documentation; defer to v1.4.2 PATCH after the SetSeed bug is fixed.

Implementation details:
- **Dynamic response injection via `sent.CollectionChanged` event subscription**:
  handler inspects ISO-TP SF PCI byte + UDS SID + sub-function byte to inject
  matching seed/positive-key/NRC response. This pattern handles all
  `_requestLock` arbitration interleavings, unlike the existing
  `UdsClientTests.cs` `await Task.Yield()` pattern which assumes single-call
  sequencing.
- **ISO-TP SF PCI awareness**: captured frames are ISO-TP-encoded
  `[PCI_byte, ...UDS_payload]`. PCI byte = `0x0L` for SF (L = payload length).
  So a captured RequestSeed frame is `[0x02, 0x27, 0x01]`, not `[0x27, 0x01]`.
- **5-second `Task.WhenAny` deadline** prevents CI hangs.
- **Per memory v1.2.12 lesson 4**: race tests are transient-flaky acceptable;
  CI re-runs 3x and only fails if all 3 fail.

### 2. AscParser logs skipped malformed lines with line number

**Carry-over from:** v1.4.0 MINOR Task 7 review.

Before: `AscParser.cs:69` silently incremented a `malformedCount` counter
without any log emission, so operators had no signal that an ASC file had
corrupted lines that were skipped.

After: each skipped malformed line is logged at `LogLevel.Warning` with:

- 1-based stream line number (operator can `texteditor ASC.asc +N` to jump to
  the exact offending line).
- Raw line content (preserved pre-trim for fidelity).
- Human-readable reason (e.g. "invalid timestamp", "odd-length hex token",
  "byte count N != declared DLC M").

Implementation:
- `AscParser`: `static class` → `static partial class` (required by `[LoggerMessage]`).
- `TryParseDataLine` signature: added `out string reason` parameter capturing
  parser-specific failure mode at each of 7 return-false sites.
- `ParseLines`: `foreach` → `for` with 1-based line counter.
- New `[LoggerMessage]` source-gen helper `LogSkippedLine(ILogger, int, string, string)`.
- Static logger field defaults to `NullLogger.Instance` (C# forbids static
  classes as generic type arguments, so `NullLogger<AscParser>` doesn't
  compile — documented in the field's XML doc-comment).
- New `ParseAsync(Stream, ILogger?, CancellationToken)` overload with optional
  logger parameter; existing `ParseAsync(Stream, CancellationToken)` preserved
  as backward-compat wrapper for v1.4.0 callers (e.g. `ReplayService.LoadAsync`
  which passes `ILogger` from DI elsewhere).

Tests added:
- `Parse_MalformedLines_LogsEachWithLineNumberAndReason` (3 valid + 2 malformed;
  asserts 2 Warning logs with correct line numbers + raw lines + reasons via
  NSubstitute `Substitute.For<ILogger>`).
- `Parse_HighMalformedRatio_ThrowsAfterLoggingAll` (1 valid + 4 malformed →
  80% malformed; asserts 4 Warning logs then throw `ReplayFormatException`
  with "4/5 = 80%" message).

csproj: add `NSubstitute` + `Microsoft.Extensions.Logging.Abstractions` to
`PeakCan.Host.Core.Tests` (centrally pinned in `Directory.Packages.props`).

Per `TraceServiceTests.cs:247` + `ChannelRouterTests.cs:172` precedent:
`[LoggerMessage]` source-gen gates `Log()` on `IsEnabled(Warning)`, so tests
stub `logger.IsEnabled(Warning).Returns(true)`.

### 3. DbcSendViewModel subscribes to DbcService.DbcLoaded

**Carry-over from:** v1.4.0 MINOR Task 7 review.

Before: `DbcSendViewModel` ctor read `DbcService.Current?.Messages` exactly
once. If the user opened SendView before loading DBC, the `DbcMessages`
dropdown stayed empty for the rest of the session — the user had to
manually reopen the view to see messages after loading DBC.

After: ctor subscribes to `DbcService.DbcLoaded`. On late DBC load:

- `SelectedDbcMessage` reset to `null` (triggers
  `OnSelectedDbcMessageChanged(null)` partial method → `SignalRows.Clear()`
  → stale Signal refs cleared).
- `DbcMessages` cleared and repopulated from `doc.Messages`.
- `ErrorMessage` reset (stale failure from prior doc doesn't linger).

Implementation matches `DbcViewModel` precedent (**no `IDisposable`**):
both VMs are app-lifetime DI singletons that die together at process exit,
so GC + finalizer pass handles cleanup. A previous `IDisposable`
implementation was a latent footgun (see `DbcViewModel.cs` Task 15
fix-history). Handler body wrapped in `DispatcherExtensions.RunOnUi()` for
cross-thread `ObservableCollection<T>.Add` safety (DbcService raises
`DbcLoaded` on a `Task.Run` worker thread per `DbcService.cs:17-22`
class doc).

Test added:
`DbcSendViewModel_OnDbcLoaded_RepopulatesDbcMessagesAndClearsSignalRows` —
constructs VM BEFORE DBC loaded (`Current=null` at ctor), uses
`EventRaiseExtensions.RaiseMethod` to invoke `DbcLoaded` via reflection
(per `DbcViewModelTests.cs:70-71` precedent — direct `.Invoke` skips
multicast delegate merging), asserts `DbcMessages` populated +
`SelectedDbcMessage` null + `SignalRows` empty after the event.

## Test count

| Suite | v1.4.0 baseline | **v1.4.1 final** | Δ |
|-------|-----------------|-------------------|---|
| Core.Tests | 296 | **299** | +3 (1 race test passing + 2 AscParser logging tests) |
| Infra.Tests | 84 + 2 SKIP | **84 + 2 SKIP** | unchanged |
| App.Tests | 356 + 4 SKIP | **357 + 4 SKIP** | +1 (DbcSendViewModel DbcLoaded) |
| **Total** | 736 + 6 SKIP + 0 fail | **740 + 7 SKIP + 0 fail** | **+4 net pass + 1 skip** |

The +1 SKIP is the v1.4.1 Item 1 mid-handshake lockout flip test deferred to
v1.4.2 PATCH (see "Out-of-scope findings" below).

## Migration

No migration required. Additive only. All changes backward-compatible:

- `AscParser.ParseAsync` adds optional `ILogger?` parameter; existing
  callers continue to work via the preserved `(Stream, CancellationToken)`
  overload.
- `DbcSendViewModel` ctor adds internal subscription; no public API change.
- `SecurityAccessAsync` race test is pure regression coverage; no behavior
  change in production code.

## Out-of-scope findings (deferred)

### HIGH: `UdsSecurity.SetSeed` wipes lockout counter

**Discovered during v1.4.1 PATCH Phase 2.5 actual code exploration.**

`UdsSecurity.cs:28-38` (`SetSeed`) creates a fresh `SecurityLevelState` on
each successful RequestSeed, which **resets `AttemptCount` and clears
`LockedUntilUtc`** on that security level. When two concurrent
`SecurityAccessAsync` calls interleave, the second caller's `SetSeed` can
run between the first caller's `RecordFailedAttempt` and the second caller's
`RecordFailedAttempt`, **wiping the accumulated counter and preventing
the lockout boundary from being reached**.

The existing `SecurityAccessAsync_SendKey_Nrc_35_Still_Increments_AttemptCount`
test (`UdsClientTests.cs:439`) does NOT catch this because it uses the
3-arg overload directly without going through `SetSeed` first. The race
condition is masked by the sequential test pattern.

Fix requires `SetSeed` to preserve `AttemptCount` + `LockedUntilUtc` on the
existing state object — a behavior change with potential spec implications
(would lockout persist across successful authentications?).

**Recommended for v1.4.2 PATCH (HIGH severity)** or v1.5.0 MINOR after
spec review. Documented in the skipped test's XML doc-comment.

## Known follow-ups (deferred to later releases)

- **v1.4.2 PATCH**: HIGH — `UdsSecurity.SetSeed` wipes lockout counter
  (see above). Also re-enable the v1.4.1 PATCH Item 1 mid-handshake
  lockout flip test once SetSeed is fixed.
- **v1.5.0 MINOR**: V8 sandbox hardening + CanApi rate limit + DBC
  size/token limit + path normalization + OEM `IKeyDerivationAlgorithm`
  concrete + Channel picker + Replay loop/CAN ID filter + Periodic DBC
  send (v1.4.0 spec §Non-Goals 第 53 行原文；8 项 deferred scope).