# PeakCan Host v0.2.0

**Released:** 2026-06-20
**Tag:** `v0.2.0`
**Commits since v0.1.0:** 10 (4 task commits + 2 review-fix commits + 1 reviewer-driven cleanup + 1 whole-branch fix batch + 1 docs)
**Tests:** 305 pass + 7 SKIP (137 Core + 96 App + 72 Infrastructure)
**NetArchTest:** 5/5 rules pass

---

## Six design defects closed

### C1 — Disconnect button (CRITICAL)

Connected users can now release the PEAK hardware at any time via the toolbar **Disconnect** button. Previously the only way to disconnect was to close the application.

- Toolbar `IsConnected` cross-triggers both Connect and Disconnect `CanExecute` states via `[NotifyCanExecuteChangedFor]`.
- `DisconnectAsync` is idempotent — repeated clicks are safe no-ops.
- Status bar shows the disconnect progress without leaking the prior `ConnectionState` literal into the verbose message.

### H1 — CTS race (HIGH)

`PeakCanChannel.ConnectAsync` no longer assigns the read-loop `Task` outside the connect lock. The previous code allowed a concurrent `DisconnectAsync` to dispose the `CancellationTokenSource` before the loop observed it, leading to a permanent deadlock.

- New `internal sealed class ChannelConnectGate` owns the lock + CTS + read-loop task reference as a single atomic state machine.
- Every `Result.Fail` after `TryEnter` calls `MarkFailed` so the gate is re-enterable.
- 12 dedicated unit tests cover the contract surface, including a 64-thread concurrent `TryEnter` race that asserts exactly one winner.

### H5 — `DisposeAsync` double-dispose (HIGH)

The previous `DisconnectAsync` disposed its CTS unconditionally; a second call (WPF teardown + DI container dispose both call `DisposeAsync`) threw `ObjectDisposedException`.

- `ChannelConnectGate.CaptureForDisconnect` returns `(CancellationToken.None, null)` when not connected — the second call early-returns before touching any state.
- `Dispose` snapshots `_cts` under the lock, nulls the field, then disposes outside the lock — second `Dispose` is a guaranteed no-op.

### H4 — `IChannelFactory` seam (HIGH-architecture)

`AppShellViewModel` no longer `new`s `PeakCanChannel` directly. The new `IChannelFactory` interface (Core) + `PeakCanChannelFactory` implementation (Infrastructure.Peak) + `FakeChannelFactory` test double let the VM's connect/disconnect state machine be driven end-to-end in unit tests for the first time.

- End-to-end coverage: `ConnectCommand_Through_Fake_Factory_Sets_IsConnected_True` and `DisconnectCommand_Through_Fake_Factory_Resets_IsConnected` both assert that the underlying `ICanChannel`'s `IsConnected` flips, proving the channel's `ConnectAsync`/`DisconnectAsync` were actually invoked.
- Architectural cleanup: the planned `Core → Infrastructure` reference cycle was caught at implementation time; resolved by relocating `ICanChannel` and `Unit` from `Infrastructure.Channel` to `Core`. NetArchTest rule 2 (Core must not depend on `Peak.Can.Basic`) is preserved via `PeakCanChannel.ResolveClassicCode` (Name → `TPCANBaudrate` switch) replacing the deleted `BaudRate.TPCANBaudrate?` field.

### H8 — Hardcoded baud / handle constants (MEDIUM)

`PcanUsbFdFirstHandle`, `DefaultBaudRate` (BaudRate.CanFd1Mbps), and `DefaultFd` (true) are now named class-level constants on `AppShellViewModel`. `ConnectAsync` reads from these instead of inlining the values.

### M11 — DBC decode offload (MEDIUM-performance)

The SDK read thread no longer performs dictionary lookups or signal-decoding work. New `DbcDecodeBackgroundService` (implements both `BackgroundService` and `IFrameSink`):

- `OnFrame` is now a single `TryWrite` enqueue into a bounded (10 000, `DropOldest`) channel — pure forwarder.
- DBC lookup + `SignalViewModel.ApplyFrame` runs on the service's `ExecuteAsync` worker thread.
- DI registers the service as both a singleton and a hosted service via `sp => sp.GetRequiredService<DbcDecodeBackgroundService>()` so the router's attached sink and the host's worker reference the same instance.
- 3 stale Task-16 tests that asserted the now-deleted inline DBC decode fan-out were correctly inverted into M11-contract tests asserting the pure-forwarder invariant.

---

## Test progression

```
v0.1.0 baseline:  287 pass + 7 SKIP (5 hardware + 1 STA-blocked + 1 parallel flake)
v0.2.0 final:     305 pass + 7 SKIP    (+18 tests)
                   (137 Core + 96 App + 72 Infrastructure)
```

5/5 NetArchTest rules pass.

---

## What was NOT done in this release (deferred to future sprints)

From the original review report, the following items remain open and are scheduled for later sprints:

- **H6** — Trace view has no search/filter (10 k-row FIFO flushes in 1.25 s at 8 kfps)
- **H7** — Send view has no input validation feedback
- **H9** — No cyclic transmit
- **H2** — `DisconnectAsync` lock — superseded by H1 fix; no separate work needed
- **H3** — `CancellationToken` silently ignored in `ConnectAsync` — no callers actually pass a token; defer to v0.3.x refactor
- **M1–M13** — Various UX polish (error-frame highlighting, view keyboard shortcuts, DBC drill-down, signal Min/Max bounds, channel-router per-sink filtering, etc.)

See the Sprint 17 plan at `docs/superpowers/plans/2026-06-19-sprint-17-v0-2-0.md` for the full triage.

---

## Upgrade notes

- **No data migration** — config / DBC / project state from v0.1.0 is forward-compatible.
- **No breaking public API changes for end users** — the `ICanChannel` / `Unit` type relocation is an internal-assembly concern; the v0.2.0 binary is drop-in compatible with v0.1.0 user data.
- **No new third-party dependencies** — all changes use the existing stack (WPF .NET 10, CommunityToolkit.Mvvm, Microsoft.Extensions.Hosting, xUnit + FluentAssertions, PEAK PCAN-Basic).

---

## Full changelog

| SHA | Subject |
|---|---|
| `54225cb` | feat(app): add Disconnect button (C1) + promote baud/fd to const (H8) |
| `32d0a91` | fix(channel): extract connect gate, fix CTS race + double-dispose (H1+H5) |
| `1b1ec76` | style(channel): add trailing newline to new gate files |
| `ea12909` | refactor(app): introduce IChannelFactory, decouple VM from PeakCanChannel (H4) |
| `a725246` | refactor(channel): mark FromDescriptor constraint + strip task IDs from public XML doc |
| `0a57fa9` | perf(app): offload DBC decode off SDK read thread (M11) |
| `cb60ac8` | fix(app): disconnect status string — show baud label, not literal Disconnecting... |
| `fc6a7c0` | style: add trailing newline to T3+T4 new files |
| `451c036` | docs: v0.2.0 changelog + user-manual Disconnect note |