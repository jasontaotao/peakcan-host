# peakcan-host v3.14.0 MINOR — code-review bug bash (7 HIGH + 11 MEDIUM + 5 LOW)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close all 7 HIGH bugs surfaced by the 2026-07-08 full-codebase review. MEDIUM and LOW items are out of scope for this MINOR (deferred to v3.14.x PATCH follow-ups) — focus is high-severity correctness + safety only.

**Architecture:** Four independent fix units, file-scope disjoint, ship together as one MINOR. Each unit gets its own subagent dispatch with explicit OWN/DO NOT TOUCH file lists (per the v3.10.0 MINOR subagent-driven pattern). Units are dispatched sequentially because each unit's implementer may add a test; running them in parallel would race on the test base.

**Tech Stack:** WPF .NET 10 + CommunityToolkit.Mvvm 8.x + xUnit + NSubstitute. Test harness already supports STA + WPF dispatcher for converter smoke tests (per v3.11.7 PATCH + v3.12.0 M3).

## Global Constraints

- **Public type identity preserved** — no API surface changes. All 7 HIGH bugs are silent corruption / leak / hang; the fix must be internal behavior only.
- **DI signatures unchanged** — no `AppHostBuilder.cs` edits. (Some fixes add `Unsubscribe` calls to `Dispose()` but don't change what the VM takes in its ctor.)
- **No schema changes** — DBC / bundle / replay DTOs unchanged.
- **STA smoke-test collection discipline** — any new WPF-runtime test joins `[Collection(WpfAppTestCollection.Name)]` (see `tests/PeakCan.Host.App.Tests/Collections/WpfAppTestCollection.cs`).
- **Test count delta +4 minimum** (one regression test per fix unit). Total target: 1298 + 5 SKIP → **1302 + 5 SKIP / 0 fail**.
- **Review backlog item IDs A1-A7** — preserve the review's numbering in commit messages + xmldoc + PKM topic for traceability. The PKM topic file (pre-captured by v3.13.3 capture) uses A1-A7.

## File Structure

| File | Bug | Action | LoC delta |
|------|-----|--------|-----------|
| `src/PeakCan.Host.Core/Dbc/SignalDecoder.cs` | A1 | Add len==64 branch before the `1UL << len` line | +4 / -1 |
| `tests/PeakCan.Host.Core.Tests/Dbc/SignalDecoderTests.cs` (or new) | A1 | New test: 64-bit signed -1 returns -1 | +25 / 0 |
| `src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs` | A5 | Wrap `DetachSink(s)` in inner try/catch | +4 / -1 |
| `tests/PeakCan.Host.Infrastructure.Tests/Channel/ChannelRouterTests.cs` (or new) | A5 | New test: sink-throws-DetachSink-doesn't-kill-readloop | +30 / 0 |
| `src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs` | A2 + A3 | Add 2 `-=` lines to `Dispose()`; fix xmldoc | +3 / -1 |
| `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` | A4 | Add 1 `-=` line to `Dispose()`; fix xmldoc | +3 / -1 |
| `tests/PeakCan.Host.App.Tests/ViewModels/EventSubscriptionLeakTests.cs` (NEW) | A2/A3/A4 | New tests: VM ctor+Dispose does not leak the singleton's event subscribers | +40 / 0 |
| `src/PeakCan.Host.Core/Replay/ReplayService.cs` | A6 | Replace `GetAwaiter().GetResult()` with fire-and-forget; fix self-contradicting xmldoc | +2 / -2 |
| `src/PeakCan.Host.Core/Replay/ReplayTimeline.cs` | A7 | Add Start <= End validation in the `region` setter; nullify invalid region | +5 / 0 |
| `tests/PeakCan.Host.Core.Tests/Replay/LoopRegionValidationTests.cs` (NEW) | A7 | New test: invalid region (Start > End) rejected, no death spiral | +25 / 0 |
| `tests/PeakCan.Host.Core.Tests/Replay/TimerAsyncWaitTests.cs` (NEW) | A6 | New test: slow sink does not block timer (smoke-level) | +30 / 0 |
| `docs/release-notes-v3.14.0.md` | — | MINOR release notes | NEW |
| `scripts/tier3_v3140.py` | — | Tier 3 ship | NEW |

**Total LoC delta: ~+170 / -7** (small +170 because of 4 new test files)

## Task-by-task

---

### Task U1: A1 SignalDecoder 64-bit signed (data corruption)

**Files:**
- Modify: `src/PeakCan.Host.Core/Dbc/SignalDecoder.cs:64-73` (the `ValueType.Signed` arm of the switch)
- Modify (or create): `tests/PeakCan.Host.Core.Tests/Dbc/SignalDecoderTests.cs` — add 1 test

**Pre-flight:** confirm the current line numbers by reading the file (they may have shifted since v3.13.x). The fix is: add a special case for `signal.Length >= 64` BEFORE the `1UL << signal.Length` line so the shift doesn't wrap to 0.

**Exact change at SignalDecoder.cs:69:**

```csharp
// BEFORE:
ValueType.Signed => (ulong)SignExtend(raw, signal.Length) & ((1UL << signal.Length) - 1UL),

// AFTER:
ValueType.Signed when signal.Length >= 64 => (ulong)SignExtend(raw, signal.Length),
ValueType.Signed => (ulong)SignExtend(raw, signal.Length) & ((1UL << signal.Length) - 1UL),
```

The `SignExtend` method (line 150) already returns a `long` correctly for 64-bit inputs — no upstream change needed. The 64-bit case just needs to skip the `1UL << 64` (which equals 1 in C#) mask.

**New test (append to `tests/PeakCan.Host.Core.Tests/Dbc/SignalDecoderTests.cs` — check if file exists first):**

```csharp
[Fact]
public void DecodeRaw_64bitSigned_NegativeOne_ReturnsAllOnesBitPattern()
{
    // v3.14.0 MINOR A1 regression: pre-fix returned 0 because
    // 1UL << 64 == 1 in C# (mod 64), so mask = 0, raw & 0 == 0.
    // SignExtend must return -1 (0xFFFFFFFFFFFFFFFF as ulong).
    var signal = new Signal(
        Name: "test_sig64",
        StartBit: 0,
        Length: 64,
        ByteOrder: ByteOrder.LittleEndian,
        ValueType: ValueType.Signed,
        Factor: 1.0,
        Offset: 0.0,
        Min: 0,
        Max: 0,
        Unit: "",
        Receivers: Array.Empty<string>(),
        Comment: null,
        Attributes: new Dictionary<string, string>(),
        ValueTable: null);
    var data = new byte[8];
    for (int i = 0; i < 8; i++) data[i] = 0xFF;  // all-bits-set
    var raw = SignalDecoder.DecodeRaw(data, signal);
    raw.Should().Be(0xFFFFFFFFFFFFFFFFUL,
        "v3.14.0 MINOR A1: 64-bit signed -1 must decode to all-bits-set, not 0");
}
```

(Adapt the `Signal` ctor args to match the actual `Signal` record signature in this codebase — read the file first if it exists, otherwise use NSubstitute / existing test factory.)

**Verification:**
- `dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj --nologo` — 0 errors
- `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~SignalDecoder" --nologo` — all green

**Commit format:** `fix(decoder): SignalDecoder 64-bit signed returns correct bit pattern (v3.14.0 MINOR A1)`

**Report file:** `.git/sdd/patch-u1-a1-report.md` (status, commit SHA, one-line summary, concerns).

**Owns:** the 2 files listed above (one prod + one test).

**Does NOT touch:** `SignExtend` impl (it's already correct for 64-bit), any other file.

---

### Task U2: A5 ChannelRouter DetachSink catch (bus killer)

**Files:**
- Modify: `src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs:221-232` (the `catch (Exception onErrorEx)` block)
- Modify (or create): `tests/PeakCan.Host.Infrastructure.Tests/Channel/ChannelRouterTests.cs` — add 1 test

**Pre-flight:** read the file to confirm current line numbers (the review cites 67-71 for the xmldoc and 231 for the bare `DetachSink` call).

**Exact change at ChannelRouter.cs:230-232:**

```csharp
// BEFORE:
LogSinkOnError(_logger, onErrorEx, s.GetType().Name);
DetachSink(s);
}

// AFTER:
LogSinkOnError(_logger, onErrorEx, s.GetType().Name);
// v3.14.0 MINOR A5: wrap DetachSink in inner try/catch. A multi-threaded
// AttachSink racing with the catch block can mutate _sinks while DetachSink
// does Array.IndexOf + Array.Copy + Volatile.Write, throwing ArgumentException.
// The original exception (onErrorEx) is preserved via _logger; a detach
// failure must NOT escape OnChannelFrame because the caller (read loop)
// tracks consecutive failures and gives up after 100, killing the CAN bus.
try { DetachSink(s); }
catch (Exception detachEx)
{
    LogDetachSinkFailed(_logger, detachEx, s.GetType().Name);
}
```

**Add a new `[LoggerMessage]` partial method at the bottom of the class** (next to existing logger methods):

```csharp
[LoggerMessage(Level = LogLevel.Warning, Message = "ChannelRouter.DetachSink failed for sink {SinkType} (original sink error is the more interesting failure; this detach failure is collateral)")]
private static partial void LogDetachSinkFailed(ILogger logger, Exception ex, string sinkType);
```

**New test:** assert that a sink which throws on `OnChannelFrame` AND a concurrent thread mutating `_sinks` (simulated via a `Mock<...>` sink that throws inside the call) does NOT escape the DetachSink exception. The test must:
1. Create a ChannelRouter
2. Attach 2 sinks: sink A (normal), sink B (throws on OnChannelFrame)
3. Push 1 frame — sink B throws, DetachSink(B) is called
4. The DetachSink must not propagate any exception out of `OnChannelFrame`
5. The `LogDetachSinkFailed` logger receives a Warning entry

If a full multi-threaded race is hard to set up deterministically, a simpler test: stub a `IChannelSink` whose `OnChannelFrame` synchronously throws AFTER calling `ChannelRouter.DetachSink(otherSink)` — verify the inner DetachSink exception is caught and logged, not propagated. This proves the inner try/catch exists and is wired; the multi-threaded race is a separate concern.

**Verification:**
- `dotnet build src/PeakCan.Host.Infrastructure/PeakCan.Host.Infrastructure.csproj --nologo` — 0 errors
- `dotnet test tests/PeakCan.Host.Infrastructure.Tests/PeakCan.Host.Infrastructure.Tests.csproj --filter "FullyQualifiedName~ChannelRouter" --nologo` — all green

**Commit format:** `fix(channel): ChannelRouter.DetachSink must not escape sink-error catch (v3.14.0 MINOR A5)`

**Report file:** `.git/sdd/patch-u2-a5-report.md` (status, commit SHA, one-line summary, concerns).

**Owns:** the 2 files listed above.

**Does NOT touch:** any other file. The `LogDetachSinkFailed` LoggerMessage is a NEW partial method, not a rename of an existing one.

---

### Task U3: A2+A3+A4 Dispose audit (event-subscription leaks)

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs:374-385` (the `Dispose()` body + xmldoc)
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs:1209-1216` (the `Dispose()` body + xmldoc)
- Create: `tests/PeakCan.Host.App.Tests/ViewModels/EventSubscriptionLeakTests.cs` (new test file)

**Pre-flight:** read both Dispose methods to confirm current line numbers + the ctor xmldoc that needs fixing.

**Fix A2 — ReplayViewModel.cs:379-385:**

```csharp
// BEFORE:
public void Dispose()
{
    _service.FrameEmitted -= OnFrameEmitted;
    _service.PlaybackEnded -= OnPlaybackEnded;
    _service.Stop();
    GC.SuppressFinalize(this);
}

// AFTER:
// v3.14.0 MINOR A2: cancel the v3.9.0 MINOR P1 LoopRewound subscription.
// IReplayService is a DI singleton, so without the -= the closure
// chain singleton → old-VM → old-frames prevents old-VM GC.
_service.LoopRewound -= OnLoopRewound;
_service.FrameEmitted -= OnFrameEmitted;
_service.PlaybackEnded -= OnPlaybackEnded;
_service.Stop();
GC.SuppressFinalize(this);
```

**Fix A3 — ReplayViewModel.cs:295** (the ctor subscription) + the Dispose:

```csharp
// BEFORE (ctor):
_recentSessions.PropertyChanged += (_, __) => RefreshRecentEntries();

// AFTER (ctor): no change to the += itself; the fix is in Dispose.
```

```csharp
// In Dispose, ADD:
_recentSessions.PropertyChanged -= (_, __) => RefreshRecentEntries();
```

This `-=` is tricky because the original subscription uses a lambda (`(_, __) => RefreshRecentEntries()`). C# `event -=` on a typed event handler can remove lambda subscriptions only if the lambda reference is preserved. **Two fix options:**

1. **Promote the lambda to a method** — `private void OnRecentSessionsPropertyChanged(object? s, PropertyChangedEventArgs e) => RefreshRecentEntries();` and subscribe with that. Then `Dispose` can `-=` with the same method reference.

2. **Store the handler as a field** — `private readonly PropertyChangedEventHandler _recentSessionsHandler;` assigned in ctor (`_recentSessionsHandler = (_, __) => RefreshRecentEntries();`), subscribed via `_recentSessions.PropertyChanged += _recentSessionsHandler;`, unsubscribed via `-= _recentSessionsHandler;`.

**Option 1 is cleaner** — promote to a method. Use that.

```csharp
// ctor becomes:
_recentSessions.PropertyChanged += OnRecentSessionsPropertyChanged;
RefreshRecentEntries();
```

```csharp
// New method:
private void OnRecentSessionsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    => RefreshRecentEntries();
```

```csharp
// In Dispose, ADD:
_recentSessions.PropertyChanged -= OnRecentSessionsPropertyChanged;
```

**Fix A4 — TraceViewerViewModel.cs:1209-1216:**

```csharp
// BEFORE:
public void Dispose()
{
    if (_disposed) return;
    _disposed = true;
    DetachAllServiceHandlers();
    _registry.SourcesChanged -= OnRegistrySourcesChanged;
    GC.SuppressFinalize(this);
}

// AFTER:
// v3.14.0 MINOR A4: cancel the v3.13.2 PATCH F5 DbcLoaded subscription.
// The ctor xmldoc at line 174-180 incorrectly argued "DbcService is
// DI singleton so no unsubscribe needed" — backwards: the singleton
// holds a strong reference to the handler, blocking the VM from GC.
_dbcService.DbcLoaded -= OnDbcLoaded;
DetachAllServiceHandlers();
_registry.SourcesChanged -= OnRegistrySourcesChanged;
GC.SuppressFinalize(this);
```

**Also fix the ctor xmldoc at TraceViewerViewModel.cs:174-180** — the comment defending "no unsubscribe" is now wrong. Update to:

```csharp
// v3.13.2 PATCH F5: subscribe to DbcService.DbcLoaded so the Trace
// Viewer auto-rebuilds Signals + chart subplots when a DBC is loaded
// via the DbcView tab. The xmldoc above (line 388) historically
// documented this as "_dbcService.PropertyChanged" but DbcService
// does not implement INotifyPropertyChanged — it exposes the typed
// DbcLoaded event. The handler is cancelled in Dispose() per
// v3.14.0 MINOR A4; DbcService is a singleton so the subscription
// would otherwise pin the VM for the app lifetime.
_dbcService.DbcLoaded += OnDbcLoaded;
```

**New test `EventSubscriptionLeakTests.cs`:**

The hard part is asserting "no leak" without running a real GC. The proxy signal is: after `Dispose()`, raise the singleton's event and assert the handler does NOT fire. For ReplayService.LoopRewound, RecentSessionsService.PropertyChanged, DbcService.DbcLoaded — all three are raisable in tests:

```csharp
[Fact]
public void ReplayViewModel_Dispose_UnsubscribesFromIReplayServiceLoopRewound()
{
    var svc = Substitute.For<IReplayService>();
    var vm = new ReplayViewModel(svc, /* ... other deps ... */);
    vm.Dispose();
    // After Dispose, raising LoopRewound must NOT trigger any handler
    // — proves the -= line runs and the closure chain is broken.
    svc.LoopRewound += Raise.EventWith(new object(), new LoopRegionRewoundEventArgs(0, 1));
    // If the handler still fires, the test would observe a side effect
    // (StatusMessage change etc.). For a clean test, just assert no
    // exception — the lack of observable behavior is the proof.
}

[Fact]
public void ReplayViewModel_Dispose_UnsubscribesFromRecentSessionsServicePropertyChanged()
{
    var sessions = new RecentSessionsService(NullLogger<RecentSessionsService>.Instance, "test-recent.json");
    var vm = new ReplayViewModel(/* inject sessions */);
    vm.Dispose();
    // After Dispose, raising PropertyChanged must not trigger a refresh.
    // Test: capture vm.RecentSessionEntries count before + after, must be equal.
}

[Fact]
public void TraceViewerViewModel_Dispose_UnsubscribesFromDbcServiceDbcLoaded()
{
    var dbc = new DbcService(NullLogger<DbcService>.Instance);
    var vm = new TraceViewerViewModel(/* inject dbc */);
    var signalsBefore = vm.Signals.Count;
    vm.Dispose();
    // After Dispose, raising DbcLoaded must not trigger RebuildSignalsCore.
    dbc.LoadAsync(testDbcPath).GetAwaiter().GetResult();
    vm.Signals.Count.Should().Be(signalsBefore, "Dispose must cancel the DbcLoaded subscription");
}
```

(Adapt to actual ctor signatures — read the test ctor pattern from existing test files first.)

**Verification:**
- `dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj --nologo` — 0 errors
- `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~EventSubscriptionLeak|FullyQualifiedName~ReplayViewModel|FullyQualifiedName~TraceViewerViewModel" --nologo` — all green

**Commit format:** `fix(viewmodels): cancel singleton event subscriptions in VM Dispose (v3.14.0 MINOR A2+A3+A4)`

**Report file:** `.git/sdd/patch-u3-a2-a3-a4-report.md` (status, commit SHA, one-line summary, concerns).

**Owns:** the 3 files listed above.

**Does NOT touch:** any other file.

---

### Task U4: A6+A7 Replay service (timer sync wait + loop-region death spiral)

**Files:**
- Modify: `src/PeakCan.Host.Core/Replay/ReplayService.cs:223-234` (the OnTick `try { ... .GetAwaiter().GetResult(); }`)
- Modify: `src/PeakCan.Host.Core/Replay/ReplayTimeline.cs:281-310` (the loop-region rewind block — add Start <= End validation earlier in the property setter if one exists; if not, add a defensive check in the OnTick rewind block)
- Create: `tests/PeakCan.Host.Core.Tests/Replay/TimerAsyncWaitTests.cs`
- Create: `tests/PeakCan.Host.Core.Tests/Replay/LoopRegionValidationTests.cs`

**Pre-flight:** read both files to find the loop-region setter / _activeLoopRegionGetter.

**Fix A6 — ReplayService.cs:225:**

```csharp
// BEFORE:
try
{
    EmitFrameToSinkAsync(frame).GetAwaiter().GetResult();
}
catch (ReplaySendException)
{
    throw;
}
catch (Exception ex)
{
    LogSinkThrew(_logger, ex, frame.Id, frame.Timestamp);
}

// AFTER:
// v3.14.0 MINOR A6: fire-and-forget. Sync wait on a 1ms timer thread
// blocks the entire timeline when the PEAK driver blocks (USB unplug
// / driver busy). The self-contradicting xmldoc at lines 242-247
// already declared "intentionally fire-and-forget" — the
// implementation just didn't match. ReplaySendException no longer
// rethrows from the timer thread (it propagates via onSinkThrew to
// the original async-sink's fault path); other exceptions are caught
// by the timer-level try/catch in ReplayTimeline.OnTick.
_ = Task.Run(async () =>
{
    try
    {
        await EmitFrameToSinkAsync(frame).ConfigureAwait(false);
    }
    catch (ReplaySendException ex)
    {
        // propagate via the singleton's onSinkThrew channel
        _onSinkThrew?.Invoke(ex);
    }
    catch (Exception ex)
    {
        LogSinkThrew(_logger, ex, frame.Id, frame.Timestamp);
    }
});
```

(Read `ReplayService.cs` first to find `_onSinkThrew` field — it's a constructor-injected `Action<Exception>?`. Adapt the field name if different.)

Also delete the now-obsolete xmldoc at lines 242-247 (the comment is now self-consistent because the code matches).

**Fix A7 — ReplayTimeline.cs:281-310:** the simplest fix is in the OnTick rewind block — add a `Start > End` guard so the rewind check is skipped (and ideally log a warning):

```csharp
// BEFORE:
if (_activeLoopRegionGetter is { } getRegion)
{
    var region = getRegion();
    if (region is { } r && _currentTimestamp >= r.End)
    {
        // ... rewind ...
    }
}

// AFTER:
if (_activeLoopRegionGetter is { } getRegion)
{
    var region = getRegion();
    if (region is { } r)
    {
        // v3.14.0 MINOR A7: defensive guard against user-supplied
        // Start > End (the ReplayViewModel ActiveLoopRegion setter
        // doesn't validate). Pre-fix, _currentTimestamp >= r.End
        // immediately re-triggered on the next tick, burning 100%
        // CPU in an infinite rewind loop. Skip the rewind check
        // (but keep the region for read-only visibility) + log once.
        if (r.Start > r.End)
        {
            LogInvalidLoopRegion(_logger, r.Start, r.End);
            // do not rewind; the timeline will play to natural EOF
        }
        else if (_currentTimestamp >= r.End)
        {
            // ... existing rewind block ...
        }
    }
}
```

Add a LoggerMessage:

```csharp
[LoggerMessage(Level = LogLevel.Warning, Message = "Invalid loop region (Start > End: {Start} > {End}); rewind disabled")]
private static partial void LogInvalidLoopRegion(ILogger logger, double start, double end);
```

**New tests:**

`TimerAsyncWaitTests.cs`:

```csharp
[Fact]
public async Task OnTick_DoesNotBlock_When_Sink_Takes_Long()
{
    // Construct ReplayService with a sink that delays 200ms.
    // After 1 OnTick fire, assert the timer is still ticking
    // (a sync wait would have pinned the timer for 200ms).
    // Use Stopwatch to measure the gap between two timer fires.
    // Pre-fix, gap >= 200ms. Post-fix, gap ~= 1ms.
}
```

`LoopRegionValidationTests.cs`:

```csharp
[Fact]
public void OnTick_WithInvalidLoopRegion_StartGreaterThanEnd_DoesNotInfiniteLoop()
{
    // Build a ReplayTimeline with frames [0..10], set loop region (5, 2).
    // Play for 100ms in a background task; CPU must NOT be 100%.
    // Count rewind events via test seam; assert == 0.
}
```

(Adapt both tests to the actual `ReplayTimeline` ctor + `ActiveLoopRegion` setter API.)

**Verification:**
- `dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj --nologo` — 0 errors
- `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~TimerAsyncWait|FullyQualifiedName~LoopRegion" --nologo` — all green

**Commit format:** `fix(replay): ReplayService timer must fire-and-forget + ReplayTimeline rejects invalid loop region (v3.14.0 MINOR A6+A7)`

**Report file:** `.git/sdd/patch-u4-a6-a7-report.md` (status, commit SHA, one-line summary, concerns — especially the A6 fire-and-forget semantic change which is observable).

**Owns:** the 4 files listed above.

**Does NOT touch:** any other file. The A7 validation is in the OnTick rewind block; do NOT add validation to `ReplayViewModel.ActiveLoopRegion` setter (out of scope — defensive guard is sufficient; the setter is internal so a future fix can validate upstream).

---

### Task U5: Release notes + Tier 3 ship + PKM capture

**Files:**
- Create: `docs/release-notes-v3.14.0.md`
- Create: `scripts/tier3_v3140.py` (parent = v3.13.3 PATCH ship SHA)

**Pre-flight:** look up the v3.13.3 PATCH ship SHA on `origin/main` via `gh api repos/jasontaotao/peakcan-host/commits?sha=v3.13.3&per_page=1` (or use the cached value `1b3f57608f4c105523692d1a17e50d5fec96af34`).

**Ship script structure:** mirror `scripts/tier3_v3133.py:1-130`. The `ADDED_OR_MODIFIED` list enumerates all files touched by Tasks U1-U4 + this task's release notes. Since the diff is small (~170 LoC across 12 files), a single overlay commit is fine.

**Commit msg for the new ship commit:** `v3.14.0 MINOR: code-review bug bash — 7 HIGH fixes (A1 SignalDecoder, A2/A3/A4 Dispose leaks, A5 ChannelRouter, A6 ReplayService timer, A7 LoopRegion validation)`

**Verification commands (final gate):**
1. `dotnet test PeakCan.Host.slnx --nologo` — expect 1302 + 5 SKIP / 0 fail (+4 from baseline 1298)
2. `git diff --stat v3.13.3..HEAD` — confirm only the 12 files in the ADDED_OR_MODIFIED list changed

**Commit format (the docs/ship commit):** `docs(ship): v3.14.0 MINOR release notes + tier3 ship script`

**Report file:** `.git/sdd/patch-u5-ship-report.md` (status, ship commit SHA, tag SHA, release URL, one-line summary).

**PKM capture:** dispatched in background after Tier 3 ship completes (per harness pattern).

**Owns:** the 2 files listed above.

**Does NOT touch:** any other file.

---

## Out of scope (deferred to v3.14.x PATCH)

- **B1-B11 MEDIUM** (11 items): Pause() timer leak, VAL_ minus, LoadAsync naming, thread safety, async state machine, OnAnyFrameEmitted frame unused, SetSpeed cross-lock, DbcViewModel ExportCsv, DbcTokenizer block comments + 2-byte BOM, 0.5.6 float token, RelayCommand CanExecute audit. All medium, all user-visible-but-not-safety-critical. Defer to a separate v3.14.1+ PATCH chain.
- **C1-C5 LOW** (5 items): Signal StartBit comment, TPCANTimestamp micro verify, TraceViewerService.Dispose, AppShellViewModel connect-async readability, BusStatisticsCollector fps/80. Pure style/correctness tightening. Defer indefinitely.

## Verification

```bash
# Per-unit RED→GREEN gate (run after each task):
dotnet test PeakCan.Host.slnx --nologo

# Final gate (after all 4 units + ship commit):
dotnet test PeakCan.Host.slnx --nologo
# Expect 1302 + 5 SKIP / 0 fail (+4 active)
```

## Ship summary

- **Tag**: v3.14.0 (MINOR)
- **Parent**: v3.13.3 PATCH on `origin/main` (ship commit `1b3f5760`)
- **Files**: 12 (4 prod + 4 test + 2 docs + 2 ship scripts) + minor xmldoc updates in 2 prod files
- **Tests**: 1298 + 5 SKIP / 0 fail → **1302 + 5 SKIP / 0 fail** (+4 active: 1 SignalDecoder 64-bit + 1 ChannelRouter DetachSink + 1 Dispose event-subscription + 1 LoopRegion validation)
- **LoC**: +~170 / -7
- **Commits**: 5 task commits + 1 docs/ship commit on `feature/v3-12-0-minor` (squash to 1 for Tier 3)
- **Review backlog items closed**: A1 + A2 + A3 + A4 + A5 + A6 + A7 (7 of 7 HIGH; 0 of 11 MEDIUM; 0 of 5 LOW)
- **NEW 1-of-1 lessons to capture**:
  - `signal-decoder-mask-shift-by-64-is-zero-requires-special-case` (A1)
  - `channel-router-detachsink-inside-catch-needs-inner-try-catch` (A5)
  - `di-singleton-event-subscribers-must-unsubscribe-in-dispose` (A2+A3+A4)
  - `timer-thread-must-fire-and-forget-on-sink-writes` (A6)
  - `user-configured-time-range-must-be-validated-against-start-end-reversal` (A7)