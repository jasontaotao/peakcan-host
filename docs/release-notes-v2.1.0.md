# v2.1.0 MINOR — Multi-frame send window (2026-07-02)

## Summary

New feature: a **non-modal Multi-frame send window** for composing and dispatching a list of CAN frames in either **concurrent** (all at once via `Task.WhenAll`) or **sequential** (one after another with optional inter-frame delay) mode, with configurable iteration count. Opened from the Send view's "Multi-frame send…" button.

```
Send view ─── "Multi-frame send…" button ──→ Multi-frame send window
                                              │
                                              ├─ DataGrid of frames (ID / Data / Ext / FD / RTR / BRS / ESI)
                                              ├─ Add / Remove / Duplicate / Move Up / Move Down / Clear
                                              ├─ Mode: Concurrent | Sequential @ Nms
                                              ├─ Iterations: 1+
                                              ├─ ▶ Send / ⏹ Stop / Close
                                              └─ Status: "Sent N / total …" or "Done with errors …"
```

## Use cases

1. **Stress test**: 100 frames × 10 iterations in concurrent mode → 1000 sends dispatched together via `Task.WhenAll` (rate-limited by the existing `SendService` token-bucket)
2. **Sequence replay**: ordered list of frames with 50ms inter-frame delay in sequential mode → preserves order, simulates a workflow
3. **Bulk edit + send**: load several frame templates into the window, tweak IDs/data inline, hit Send

## Architecture

```
App/ViewModels/MultiFrameSendViewModel.cs       ─┐
   ObservableCollection<MultiFrameSequenceRow>  │
   IsConcurrent / DelayMs / Iterations / Status  │  UI binding
   Commands: Add/Remove/Dup/MoveUp/MoveDown/    │
             Clear/Send/Stop                     │
                                                  ├── DI singleton (1 per app)
App/Models/MultiFrameSequenceRow.cs              │
   Id / DataHex / Extended / Fd / Rtr / BRS / ESI│
   Build() → CanFrame                            │
                                                  │
App/Services/MultiFrame/SequenceSendService.cs   │
   SendAsync(frames, Mode, DelayMs, Iters,       │
             IProgress<int>, CT) → Result        │
   Concurrent: Task.WhenAll                     │
   Sequential: foreach + Task.Delay             │
                                                  │
App/Windows/MultiFrameSendWindow.xaml(.cs)      ─┘
   Non-modal Window. DataGrid + toolbar + mode
   row. Lazy-created by SendViewModel.OpenMultiFrameSend
   (WPF Window ctor needs STA + Application —
   cannot be DI-resolved on test threads).
```

### Why the window is NOT DI-registered

WPF `Window` ctor throws `InvalidOperationException` ("The calling thread must be STA") when invoked on a non-STA thread. DI resolution can run on any thread (test pools, hosted services, etc.). Pattern: keep the VM in DI (UI-thread-safe), lazy-create the Window on first open from the VM.

## Decisions made

| # | Choice | Rationale |
|---|--------|-----------|
| 1 | **Non-modal Window** (vs modal dialog or inline dock) | Lets the operator monitor Trace / DBC / SendView while dispatching; the "monitor while you send" workflow is the primary use case for a sequence runner |
| 2 | **True concurrent** (`Task.WhenAll`) (vs sequential-with-zero-delay) | Matches the user's explicit "并发" requirement; observable behavior is "all sent in this iteration before the next begins" |
| 3 | **No persistence** (vs JSON Sequence Library) | MVP first cut; Frame Library already exists for single frames; sequence persistence is a future PATCH |
| 4 | **DBC support deferred** to v2.1.1 PATCH | Smaller scope, faster ship cadence; raw-frame sequence is the highest-ROI first cut |
| 5 | **Window lazy-created** by SendViewModel | WPF Window needs STA; DI resolution runs on test threads and throws; the VM is DI-safe |

## Test counts

| Suite | v2.0.7 | v2.1.0 | Δ |
|-------|--------|--------|---|
| Core  | 388    | 388    | 0 |
| App   | 456    | 477    | +21 (9 `SequenceSendServiceTests` + 12 `MultiFrameSendViewModelTests`) |
| Infra | 84     | 84     | 0 |
| **Total** | **928 + 6 SKIP** | **949 + 6 SKIP** | +21 |

### New tests

| File | Test | Coverage |
|------|------|----------|
| `SequenceSendServiceTests.cs` | `SendAsync_Concurrent_FiresAllFrames` | mode dispatch |
|  | `SendAsync_Sequential_FiresInOrder` | mode dispatch |
|  | `SendAsync_Iterations_RepeatsSequenceNTimes` | iteration loop |
|  | `SendAsync_Sequential_DelayRespectedBetweenFrames` | delay semantics (stopwatch check) |
|  | `SendAsync_ChannelFails_AllCountedAsFailures` | failure tally |
|  | `SendAsync_EmptyFrames_ReturnsZeroResult` | empty list |
|  | `SendAsync_ProgressReporter_ReceivesIncrementalUpdates` | IProgress<int> |
|  | `SendAsync_Cancellation_PropagatesBetweenIterations` | CT propagation |
|  | `SendAsync_RejectsInvalidArgs` | ArgumentOutOfRangeException for iterations<1 and delayMs<0 |
| `MultiFrameSendViewModelTests.cs` | `Ctor_SeedsWithOneRow` | seed |
|  | `AddRowCommand_AppendsNewRow_AndSelectsIt` | add |
|  | `RemoveRowCommand_RemovesSelectedRow` | remove |
|  | `DuplicateRowCommand_CopiesAllFields` | duplicate |
|  | `MoveUp_DownCommand_ReordersRows` | move |
|  | `ClearRowsCommand_EmptiesRows` | clear |
|  | `SendCommand_Concurrent_DispatchesAllFrames` | integration with service |
|  | `SendCommand_Sequential_FiresInOrder` | integration with service |
|  | `SendCommand_NoRows_SkipsSend_WithStatusMessage` | empty guard |
|  | `SendCommand_InvalidHexData_AbortsBeforeAnySend` | validation pre-send |
|  | `SendCommand_CanExecute_ReflectsIsRunning` | state machine |
|  | `ProgressMax_TracksRowsCountTimesIterations` | progress calc |

## Files added (5)

### Production (4)
- `src/PeakCan.Host.App/Models/MultiFrameSequenceRow.cs` (~70 lines)
- `src/PeakCan.Host.App/Services/MultiFrame/SequenceSendService.cs` (~125 lines)
- `src/PeakCan.Host.App/ViewModels/MultiFrameSendViewModel.cs` (~230 lines)
- `src/PeakCan.Host.App/Windows/MultiFrameSendWindow.xaml` (~90 lines)
- `src/PeakCan.Host.App/Windows/MultiFrameSendWindow.xaml.cs` (~20 lines)

### Tests (2)
- `tests/PeakCan.Host.App.Tests/Services/MultiFrame/SequenceSendServiceTests.cs` (~190 lines, 9 tests)
- `tests/PeakCan.Host.App.Tests/ViewModels/MultiFrameSendViewModelTests.cs` (~200 lines, 12 tests)

## Files modified (3)

- `src/PeakCan.Host.App/Views/SendView.xaml` (+1 button)
- `src/PeakCan.Host.App/ViewModels/SendViewModel.cs` (+1 ctor param + 1 command)
- `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` (+2 DI registrations)
- `docs/release-notes-v2.1.0.md` (this file)

## Process lessons (NEW — from this MINOR)

1. **WPF Window cannot be DI-resolved on non-STA threads** — `Window..ctor` calls into WPF's `InputManager` which throws `InvalidOperationException` ("calling thread must be STA") when invoked outside the WPF dispatcher. DI resolution runs on whatever thread the resolution call originates from (test pool, hosted service, etc.), so the pattern "VM in DI + Window lazy-created by the VM" is the only safe shape for Windows. The earlier v1.x SendViewModel ctor was OK only because SendView is a UserControl, not a Window — UserControls don't have the STA requirement (their visual tree is only realized when added to a parent).

2. **Use `ThrowIfLessThan` / `ThrowIfNegative` for ArgumentOutOfRangeException** — CA1512 analyzer (enabled by default in .NET 10) requires `ArgumentOutOfRangeException.ThrowIfLessThan(value, 1)` instead of `if (value < 1) throw new ArgumentOutOfRangeException(...)`. Saves a few lines and produces the same exception type. Pattern is symmetric with the older `ThrowIfNull` etc.

3. **CA2016 forces CancellationToken forwarding** — Analyzer warns when an async method accepts a `CancellationToken` but doesn't forward it to inner awaits. The temptation to "fire-and-forget" the inner call (omit ct) so cancellation doesn't slow the loop is wrong: CA2016 catches it at build time, and the right fix is always to forward the token. Performance impact is negligible for typical sequence sizes.

4. **Use `SendService.ActiveChannel` property, not a ctor-injected channel** — The pre-v2.1.0 SendService ctor takes only ILogger; the channel is set later via the `ActiveChannel` property (volatile + Interlocked). Tests can swap in a recording channel without re-constructing the service. SequenceSendService follows the same pattern: take SendService, not ICanChannel directly.

## Pre-ship review

- 0C / 0H / 0M / 0L (self-review)
- Verified: no DI registration of `MultiFrameSendWindow` (the STA trap); VM lives in DI as singleton; window lazy-instantiated from VM
- Verified: `SendAsync` builds all `CanFrame`s BEFORE any send — partial-send surprise eliminated
- Verified: concurrent mode uses `Task.WhenAll` (true concurrent); sequential mode uses `foreach + await + Task.Delay` (order preserved)
- Verified: IProgress<int> drives `ProgressValue` on UI dispatcher (caller uses `Progress<T>` which captures SynchronizationContext)
- Verified: cancellation propagates to both `SendAsync` and the underlying `SendService.SendAsync(frame, ct)` — CA2016 satisfied

## Ship method

延续 Tier 3 fallback（github.com:443 仍不通）；预计 11-call pipeline。

## Open follow-ups

- **v2.1.1 PATCH candidate**: DBC message Sequence mode (per-message signal values, multiple messages in one sequence)
- **v2.1.2 PATCH candidate**: Sequence persistence (Save / Load Sequence JSON, mirror Frame Library)
- **v2.2.0 MINOR candidate**: Replay-from-file (load ASC/CSV, dispatch as sequence) — bigger scope, separate ship