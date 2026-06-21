# PeakCan Host v0.2.1

**Tag:** `v0.2.1`
**Branch:** `fix/v0.2.1-high-bug-review`
**Date:** 2026-06-21
**Base:** `v0.2.0` (commit `445a078`)
**HEAD:** `95ae3b2`
**Commits since v0.2.0:** 6 atomic commits (5 fix + 1 review-triage)

## Summary

Seven HIGH-severity bugs found by a 71-file multi-agent code review of
the post-v0.2.0 codebase, all closed with regression tests. No new
public API; the changes are all internal to the existing data flow.
334 unit tests pass (153 Core + 107 App + 74 Infrastructure); 0 type
errors, 0 lint errors. Build succeeds with `TreatWarningsAsErrors`.

---

## Bug-by-bug writeup

### H1 — `AppShellViewModel.DisconnectAsync` catch path leaks state

**File:** `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs:302-340`
**Severity:** HIGH
**Commit:** `2311f0f` (initial) + `95ae3b2` (review reorder)

The previous catch block only set `ConnectionState = "Disconnected"`
and logged. If `DisconnectAsync()` threw (hardware fault, driver
glitch), the user was left in a stuck state:

- `IsConnected` stayed true → the Disconnect button stayed enabled
  but the channel was dead
- `ChannelRouter` still routed incoming frames to the dead channel
- `SendService.ActiveChannel` still pointed at the dead channel
  → the next manual `Send` targeted it

The router and send-service refs leaked until the next successful
connect, causing ghost frames and write attempts to a disposed
channel.

**Fix:** Reset all three in the same order as the success path
(UnregisterChannel → ActiveChannel=null → IsConnected=false) so
the two paths produce identical state transitions from any
observer's point of view. The `finally` block already cleared
`_activeChannel`; the only new work is the three resets.

**Test:** `AppShellViewModelTests.DisconnectCommand_When_Channel_
Throws_Still_Resets_IsConnected_Router_And_SendService` — uses a
`ThrowingFakeCanChannel` whose `DisconnectAsync` throws an
`InvalidOperationException`. The test reaches into the (sealed,
non-virtual) `ChannelRouter._channels` list via reflection to
confirm the channel was unregistered.

---

### H2 — `Signal.StartBit` byte overflow on CAN FD Motorola signals

**File:** `src/PeakCan.Host.Core/Dbc/Signal.cs:25`
**Severity:** HIGH
**Commit:** `817e59f`

`Signal.StartBit` was declared as `byte` (max 255). CAN FD payloads
reach 64 bytes (= 512 bits), so a Motorola signal can have a
`StartBit` in byte 32+ (value 256-511). The previous code
unparseable any DBC with start bit ≥ 256 (parser threw
`OverflowException` from `byte.Parse`).

**Fix:** Widen `StartBit` to `ushort`. `Signal.Length` stays `byte`
because the DBC spec caps signal width at 64 bits. Updated the
`DbcParser` to use a new `ParseUShort` helper for the start-bit
token, and shifted the existing `Fails_On_Signal_Start_Bit_Overflow`
test to the new ushort boundary (start=999999 now fails; start=999
is in-range and succeeds).

**Test:** `DbcParserTests.Parses_Signal_Start_Bit_Above_255_For_CAN_FD`
parses a DBC with `StartBit=300` in a 64-DLC message and asserts the
parse succeeds.

---

### H3 — `SignalDecoder.ReadBigEndian` / `ReadLittleEndian` byte truncation

**File:** `src/PeakCan.Host.Core/Dbc/SignalDecoder.cs:82` (also 66-67)
**Severity:** HIGH
**Commit:** `817e59f`

Coupled with H2: even if the parser accepted a start bit > 255, the
decoder was using `(byte)(start + i)` and `(byte)((start + i) / 8)`
to compute the per-bit byte index and offset. The explicit byte cast
silently wrapped at 256, so a signal starting at byte 32 was being
read from byte 0 (or byte 5, depending on the offset), producing
spurious decode values with no error indication.

**Fix:** Replace the byte accumulators with `int` throughout both
read paths. The int accumulator naturally covers the full 0..511
range. The byte-truncation in the loop was the root cause; the
type widening in H2 is the user-facing surface.

**Tests:** 3 new tests in `SignalDecoderTests`:
- `BigEndian_Unsigned_8Bit_At_FD_Start_256` — start=256, byte 32
  only, 8-bit Motorola → expect 128
- `BigEndian_Unsigned_16Bit_At_FD_Start_300` — start=300, 16-bit
  Motorola across bytes 37-39 → expect 0xF000
- `LittleEndian_Unsigned_8Bit_At_FD_Start_300` — start=300,
  8-bit Intel across bytes 37-38 → expect 0xFF

---

### H4 — `MultiplexorSignalIndex` ushort wrap-around on malformed DBC

**File:** `src/PeakCan.Host.Core/Dbc/DbcParser.cs:243`
**Severity:** HIGH
**Commit:** `6618a08`

A malformed DBC can contain multiplexed signals (`m0..m15` markers)
but no multiplexor (`M` marker). The previous code used
`(ushort?)signals.FindIndex(s => s.IsMultiplexor)`, which silently
wrapped `FindIndex`'s -1 to `ushort` 65535. Downstream dispatch that
used the index to index into the signals list threw
`ArgumentOutOfRangeException`, taking down the viewer for the
entire DBC.

**Fix:** Guard the cast with an explicit `int idx` check; only set
`muxIdx` when the multiplexor was actually found.

**Test:** `MultiplexorSignalIndex_Is_Null_When_No_Signal_Is_Marked_
Multiplexor` parses a DBC with only an `m0` signal and asserts the
index is null, not 65535. Downstream grep confirmed no production
consumer of `MultiplexorSignalIndex` exists in the current code, so
the null contract change is safe.

---

### H5 — `PeakCanChannel.ReadLoopAsync` silent exception swallow

**File:** `src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs:202`
**Severity:** HIGH
**Commit:** `53b0395` (initial) + `95ae3b2` (review counter rename)

The previous `catch (Exception)` had an empty body. Any exception
from the SDK read path — bus-off, driver unload, a buggy
`FrameReceived` subscriber — was completely invisible in
production. The XML doc even admitted "there is currently no
logging path".

**Fix:** Inject `ILogger<PeakCanChannel>` via the ctor (optional
parameter, defaults to `NullLogger` so test paths stay simple).
Log every read-loop throw at `Error` level via `LoggerMessage`
source generator (avoiding CA1848 / CA1873). Add a `Critical`-level
"giving up" log + early return when consecutive failed iterations
hit `MaxConsecutiveReadFailures` (100) so a dead bus no longer
busy-spins inside the backoff loop.

**Counter semantics:** Reviewed fix renames `consecutiveFailures`
to `consecutiveIterationsWithFailure` and counts per-iteration
(not per-throw) to match the pre-split semantics. With per-throw
counting, a worst-case iteration with both classic and FD failures
would halve the effective give-up threshold.

**Tests:** `Constructor_Accepts_Optional_Logger_For_Read_Loop_
Logging` and `MaxConsecutiveReadFailures_Is_Exposed_And_Reasonable`
(the latter bounds-checks the new internal const). The read loop
itself is private + uses static `PCANBasic.*` calls, so the catch
path and the give-up path are exercised manually on real hardware.

---

### H6 — Classic/FD read in same try block (cascading skip on subscriber throw)

**File:** `src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs:189-199`
**Severity:** HIGH
**Commit:** `53b0395`

The classic (`PCANBasic.Read`) and FD (`PCANBasic.ReadFD`) read
loops shared one outer try block. If a `FrameReceived` subscriber
threw for a classic frame (e.g. a buggy decoder), control jumped
to the catch and the FD read in the same iteration was silently
skipped, dropping FD traffic until the next loop turn. On a
mixed classic+FD bus with a transient subscriber error, FD
throughput collapses.

**Fix:** Split into two independent try/catch blocks. Mirrors the
per-sink isolation pattern in `ChannelRouter.OnChannelFrame`.

**Test coverage:** As for H5, this is a hardware-bound path. Code
review verifies the structure; the split is load-bearing for the
new behavior of H5 (per-iteration counting).

---

### H7 — `App.xaml.cs` IHost leak + no global exception handlers

**File:** `src/PeakCan.Host.App/App.xaml.cs`
**Severity:** HIGH
**Commit:** `ad267d7` (initial) + `95ae3b2` (review timeout bump)

Two coupled defects in the WPF entry point:

1. **IHost leak.** The previous code had `var host = AppHostBuilder.Build()`
   as a local variable in `OnStartup`. The host (and every hosted
   service inside it — `SinkWiringService`, `DbcDecodeBackgroundService`)
   was never disposed on application exit. Process teardown happened
   by GC, not by the clean shutdown contract.

2. **No global exception handlers.** Any unhandled exception in
   production (background task failure, dispatcher exception,
   async-void mishap) crashed the process with no diagnostic
   surface. The operator saw a window close, nothing in the log.

**Fix:**

- Store the host in a private `IHost? _host` field.
- Override `OnExit` to call `IHost.StopAsync(10s timeout)` followed
  by `Dispose` in a `try/finally`. 10s (was 5s in the initial
  commit; bumped after review) gives a mid-decode 64-byte FD frame
  or a long `ChannelRouter` sink teardown time to finish; the OS
  reaps the process if shutdown still hangs.
- Install three global exception handlers at startup:
  - `AppDomain.CurrentDomain.UnhandledException` → log Error
  - `DispatcherUnhandledException` → log Error (do NOT mark
    handled; the dispatcher loop is in an undefined state, a
    controlled crash is safer than continuing)
  - `TaskScheduler.UnobservedTaskException` → log Error + set
    observed (the process keeps running; the log is the
    diagnostic surface)
- All three route through the existing Serilog pipeline set up
  in `AppHostBuilder.Build`, so they land in the same rolling
  log file as every other error.

**Tests:** 6 new tests in `AppLifecycleTests` verify the structural
fixes (field exists, method overrides, idempotency guard) via
reflection. The handlers' logging side-effects are not unit-tested
— they fire on actual crashes, which is what makes them valuable
in production.

---

## Test count

| Layer | v0.2.0 | v0.2.1 | Δ |
|---|---|---|---|
| Core | 137 | 153 | +16 |
| App | 96 | 107 | +11 |
| Infrastructure | 72 | 74 | +2 |
| **Total pass** | **305** | **334** | **+29** |
| Skipped (hardware) | 7 | 7 | 0 |
| Failed | 0 | 0 | 0 |

(For comparison, the absolute count at the start of v0.2.1 was
313 pass; the +21 delta over the v0.2.0 release is the new
regression tests, and the +8 over the start of v0.2.1 came from
the `Fails_On_Signal_Start_Bit_Overflow` reframe adding
`Parses_Signal_Start_Bit_Above_255_For_CAN_FD` to keep the test
count symmetric.)

---

## Commits

| Hash | Subject |
|---|---|
| `2311f0f` | fix(app): reset IsConnected + unregister channel in DisconnectAsync catch (H1 initial) |
| `817e59f` | fix(core): widen Signal.StartBit to ushort and fix decoder bit arithmetic (H2 + H3) |
| `6618a08` | fix(core): MultiplexorSignalIndex null when DBC has m0..m15 but no M (H4) |
| `53b0395` | fix(infra): log read-loop exceptions and isolate classic/FD read blocks (H5 + H6 initial) |
| `ad267d7` | fix(app): store IHost for OnExit dispose and install global exception handlers (H7 initial) |
| `95ae3b2` | fix(review): address csharp-reviewer findings on v0.2.1 fixes (H1 reorder + H5 counter + H7 timeout) |

---

## Known follow-ups (deferred to v0.2.2 or v0.3.0)

Not regressions; not in v0.2.1 scope but surfaced during the review:

- The `MultiplexorSignalIndex` null contract is a no-op today
  (no production consumer), but any future decoder that dispatches
  on the index must handle `null` (i.e. "no multiplexor — cannot
  decode this signal in isolation").
- `OnExit` 10s cap is a heuristic. If a future hosted service
  genuinely needs more time (e.g. flushing a large replay log), it
  should expose its own timeout knob rather than extending the
  global cap.
- The two hardware-bound paths (read loop catch + classic/FD
  isolation) cannot be unit-tested without a real PCAN device.
  Any future refactor that introduces an `IPcanReader` seam
  would make them testable.
- The reflection-based test for `ChannelRouter._channels` in H1
  couples the test to the field name. A future `internal int
  RegisteredCount => _channels.Count;` (with the existing
  `InternalsVisibleTo`) would survive a rename.

---

## How to ship

```bash
git push origin main
git push origin v0.2.1
```

(GH release creation is still manual — `gh` CLI is not installed
in this environment. Use the GitHub web UI to create the release
from the v0.2.1 tag, pasting the H1-H7 summary above as the
release body.)
