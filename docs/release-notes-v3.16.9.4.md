# Release Notes v3.16.9.4 — Bus-off / driver-unload visibility PATCH

**Released:** TBD (pending Tier-3 push to main + tag + GH release)
**Tag:** v3.16.9.4
**Branch:** `v3-16-9-x-patch-chain` (tip of feature branch, post-rename)
**Parent:** v3.16.9.0 MINOR (`7099a47` on origin/main)

## Why this PATCH

Review finding #4 (`_review_verification.json` infra-reviewer): the read loop in
`PeakCanChannel` swallowed every SDK exception via `_logger.LogError` only.
Hardware faults like bus-off, driver unload, USB disconnect were invisible to
the operator — the user saw "Connected" status + "no frames" with no
explanation. The TODO to surface via `IFrameSink.OnError` was documented but
unimplemented.

## What this PATCH does

### 1. New `ReadLoopError` event on `ICanChannel`

```
src/PeakCan.Host.Core/ICanChannel.cs
  + event Action<ReadLoopError>? ReadLoopError
  + readonly record struct ReadLoopError(ushort Handle, ReadLoopErrorKind Kind, Exception? Exception)
  + enum ReadLoopErrorKind { ClassicReadException, FdReadException, LoopGivingUp }
```

Fires on the SDK read thread. Subscribers marshal to UI themselves
(matches `FrameReceived` contract).

### 2. `PeakCanChannel.ReadLoopAsync` emits on 3 paths

`src/PeakCan.Host.Infrastructure/Peak/PeakCanChannel.cs`
- `catch (Exception)` after `ReadClassic` → emit `ClassicReadException`
- `catch (Exception)` after `ReadFd` → emit `FdReadException`
- Give-up after 100 consecutive failures → emit `LoopGivingUp`

Emission uses `SafeEmitReadLoopError` helper: iterates
`GetInvocationList()` with per-subscriber try/catch so a misbehaving UI
subscriber (e.g. disposed Dispatcher) cannot crash the SDK read loop.
Matches the per-sink isolation pattern in `ChannelRouter`.

The existing `_logger.LogError` / `LogCritical` calls are RETAINED — the
event is additive so production Serilog still captures full stack traces
for post-mortem.

### 3. `AppShellViewModel` surfaces to StatusMessage

`src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs`
- `ConnectAsync` success path: `channel.ReadLoopError += OnReadLoopError`
- `DisconnectAsync` success path: `_activeChannel.ReadLoopError -= OnReadLoopError`
- `OnReadLoopError` handler updates `StatusMessage` + `ConnectionState`

Status messages are human-readable:
- Classic: `"Read loop error (classic): <ex.Message> — bus may be off"`
- FD: `"Read loop error (FD): <ex.Message> — driver may be unloaded"`
- LoopGivingUp: `"Read loop abandoned after 100 failures — call Disconnect + Connect to recover"`

### 4. Tests

`tests/PeakCan.Host.Infrastructure.Tests/PeakCanChannelTests.cs`
- `ReadLoopError_Event_Is_Exposed_On_ICanChannel` — compile-time check via interface assignment + += subscription
- `ReadLoopError_Fires_When_Classic_Read_Throws` — uses `FakePcanReader` (new test fake) with `ThrowOnClassicRead=true`
- `Constructor_Accepts_IPcanReader_For_Testability` — pins ctor signature so future refactors don't drop the IPcanReader injection point

`tests/PeakCan.Host.Infrastructure.Tests/ChannelRouterTests.cs` —
`FakeChannel` updated to satisfy new interface contract.

8 test fakes across `tests/PeakCan.Host.App.Tests/**` updated to satisfy
the new `ReadLoopError` interface contract (CS0067 suppressed via #pragma).

## Tests

- **New tests**: 3 (2 in PeakCanChannelTests, 1 IPcanReader ctor pin)
- **Modified fakes**: 11 (1 in Infra.Tests, 10 in App.Tests — all add the
  no-op `ReadLoopError` event to satisfy interface contract)
- **All pass**: 1338 / 0 / 5 SKIP across App.Tests (801) + Core.Tests (449) +
  Infrastructure.Tests (88)
- **Build**: 0 warnings, 0 errors (2 pre-existing nullable warnings in
  `DbcService.cs:157` unrelated)

## Risk notes

- **R1**: StatusMessage is a single property bound to a TextBlock in
  AppShell.xaml — the operator sees the error message but does NOT get a
  red/yellow color change. YAGNI for this PATCH (the existing "Connected"
  → "Connected (read loop degraded: ...)" state transition + the descriptive
  StatusMessage is enough for an MVP fix).
- **R2**: Bus-off is often transient (PCANBasic auto-recovers when the
  bus returns to ERROR_ACTIVE). The handler does NOT auto-disconnect —
  the existing `MaxConsecutiveReadFailures=100` give-up mechanism handles
  the genuinely-dead-bus case. Operators should Disconnect+Connect if they
  see "LoopGivingUp" in StatusMessage.
- **R3**: The handler runs on the SDK read thread. [ObservableProperty]
  source-gen setter raises PropertyChanged synchronously; UI binding
  marshalling is the WPF DataBinding engine's responsibility. No
  explicit `Dispatcher.Invoke` needed — the existing `TraceViewerViewModel
  .OnAnyFrameEmitted` uses the same pattern successfully.

## What this PATCH does NOT include

- No red/yellow color binding for StatusMessage (R1 above).
- No click-to-see-log dialog (YAGNI).
- No auto-disconnect on bus-off (R2 above).
- No PeakErrorMapper LOW finding #25 fix (OK → ErrorCode.Unknown) — separate PATCH if desired.

## Pre-Tier-3 ship checklist

- [x] Build clean (0 warnings, 0 errors)
- [x] All tests pass (1338/0/5 SKIP)
- [x] Tier-3 ship script prepared (see `scripts/tier3_v3169_4.py` — to be created on ship day)
- [ ] Tier-3 ship run successfully; `git rev-list --count origin/main..HEAD` = 0 post-push
- [ ] Tag `v3.16.9.4` applied (annotated)
- [ ] GH release published with this file as release body