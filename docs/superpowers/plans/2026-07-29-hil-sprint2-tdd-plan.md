# HIL Sprint 2 TDD Implementation Plan

**Date**: 2026-07-29
**Spec**: [Sprint 2 design](../specs/2026-07-29-hil-sprint2-design.md) (v14, reviewed 13 rounds)
**Depends**: [Sprint 1 TDD plan](2026-07-29-hil-sprint1-tdd-plan.md) (complete)
**Goal**: TraceDrivenChannel + HILAssertionContext + CLI Runner, 4 increments

---

## Pre-flight: Verify Existing API Signatures

Before any implementation, confirm these signatures match the spec's assumptions.

### Core types

| Type | File | Verify |
|---|---|---|
| `ICanChannel` | `Core/ICanChannel.cs` | `ConnectAsync(BaudRate, bool, CancellationToken)`, `FrameReceived` event, `WriteAsync`, `DisposeAsync` |
| `CanFrame` | `Core/CanFrame.cs` | `record struct CanFrame(CanId, ReadOnlyMemory<byte>, FrameFlags, ChannelId, Timestamp)` |
| `CanId` | `Core/CanId.cs` | `CanId(uint raw, FrameFormat format)`, `.Raw`, `.IsExtended` |
| `Timestamp` | `Core/Timestamp.cs` | `Timestamp(ulong TotalMicroseconds)`, `.TotalMicroseconds` |
| `FrameFlags` | `Core/FrameFlags.cs` | No `Extended` member (inferred from ID > 0x7FF) |
| `ChannelId` | `Core/ChannelId.cs` | `ChannelId(ushort Handle)` |
| `BaudRate` | `Core/ICanChannel.cs` | `Can500kbps` static field |

### Replay types

| Type | File | Verify |
|---|---|---|
| `ReplayFrame` | `Core/Replay/ReplayFrame.cs` | `record(double Timestamp, uint Id, byte Dlc, byte[] Data, FrameFlags Flags)` |
| `AscParser` | `Core/Replay/AscParser.cs` | `ParseAsync(string path, ...)` returns frames |

### DBC types

| Type | File | Verify |
|---|---|---|
| `DbcParser` | `Core/Dbc/DbcParser.cs` | `Parse(string text, int maxMessageCount, CancellationToken)` returns `Result<DbcDocument>` |
| `DbcDocument` | `Core/Dbc/DbcDocument.cs` | `.Messages` property |
| `Message` | `Core/Dbc/Message.cs` | `record(uint Id, string Name, byte Dlc, string Sender, IReadOnlyList<Signal> Signals, ...)` |
| `SignalDecoder` | `Core/Dbc/SignalDecoder.cs` | `Decode(ReadOnlySpan<byte> data, Signal signal)` returns `double` |

### HIL types (Sprint 1)

| Type | File | Verify |
|---|---|---|
| `IAssertionContext` | `Core/HIL/Contracts/IAssertionContext.cs` | Current: `GetSignalValue(string)` -> will add `maxAgeMs` |
| `IDbcLookup` | `Core/HIL/Contracts/IDbcLookup.cs` | `FindMessage(uint canId)` returns `Message?` |
| `DecodedFrame` | `Core/HIL/Contracts/DecodedFrame.cs` | `record(CanFrame Frame, IReadOnlyDictionary<string, double> Signals)` |
| `TestSuiteEngine` | `Core/HIL/TestSuiteEngine.cs` | `ctor(IFixtureResolver, IEnumerable<IStepExecutor>)`, `ExecuteAsync(suite, ctx, config, progress, ct)` |
| `TestSuite` | `Core/HIL/TestSuite.cs` | `record(string Name, IReadOnlyList<TestCase> Cases, ...)` |
| `TestSuiteResult` | `Core/HIL/TestSuiteResult.cs` | `.AllPassed` computed property |
| `TestSuiteConfig` | `Core/HIL/TestSuiteConfig.cs` | Default constructor |
| `TestProgress` | `Core/HIL/Progress/TestProgress.cs` | `record(int CompletedCases, int TotalCases, string? CurrentCaseName, string? Message)`, `.PercentComplete` |
| `IFixtureResolver` | `Core/HIL/Setup/IFixtureResolver.cs` | `Resolve(string key)` returns `ITestFixture` |
| `ITestFixture` | `Core/HIL/Setup/ITestFixture.cs` | `SetupAsync`, `TeardownAsync` |
| `AssertionPrimitives` | `Core/HIL/Assertions/AssertionPrimitives.cs` | `ctor(IAssertionContext)`, calls `GetSignalValue(name)` |
| `IStepExecutor` | `Core/HIL/StepExecutor/IStepExecutor.cs` | `.Kind` property, `ExecuteAsync(step, ctx, ct)` |
| `HILJsonOptions` | `Core/HIL/Serialization/HILJsonOptions.cs` | `.Default` static field (camelCase) |
| `TestCaseStep` | `Core/HIL/TestCaseStep.cs` | `[JsonConverter(typeof(TestCaseStepJsonConverter))]` |

### Existing executor classes (all `internal sealed` in Core)

| Executor | Kind |
|---|---|
| `WaitForSignalStepExecutor` | `WaitForSignal` |
| `AssertSignalStepExecutor` | `AssertSignal` |
| `AssertRangeStepExecutor` | `AssertRange` |
| `SendFrameStepExecutor` | `SendFrame` |
| `SendSequenceStepExecutor` | `SendSequence` |
| `DelayStepExecutor` | `Delay` |

### Assembly visibility

| Assembly | Current IVT | Sprint 2 addition |
|---|---|---|
| `PeakCan.Host.Core` | `PeakCan.Host`, `.Core.Tests`, `.App.Tests` | `+ PeakCan.Host.Infrastructure`, `+ PeakCan.Host.Cli` |
| `PeakCan.Host.Infrastructure` | `.Infrastructure.Tests` | `+ PeakCan.Host.Cli` |

---

## Inc 0: Sprint 1 Interface Update (direct implementation, no TDD)

**Rationale**: Trivial signature change with default parameter. Sprint 1 tests unchanged (default param). Verify with existing Sprint 1 test suite.

### Changes

1. **`Core/HIL/Contracts/IAssertionContext.cs`**:
   - `double? GetSignalValue(string signalName)` -> `double? GetSignalValue(string signalName, int maxAgeMs = 5000)`
   - Update xmldoc: "Returns null if signal not found, never decoded, or age exceeds maxAgeMs."

2. **`Tests/.../Fakes/FakeAssertionContext.cs`**: Add `maxAgeMs` parameter to implementation (ignore or use for staleness check).

3. **`Core/HIL/Assertions/AssertionPrimitives.cs`**: `GetSignalValue(name)` -> `GetSignalValue(name, maxAgeMs: 5000)` (explicit, future-proof).

### Verification

- Run Sprint 1 test suite: `dotnet test` -- all tests must pass (default parameter preserves existing calls).
- Specifically verify `AssertionPrimitivesTests.cs` and `TestSuiteEngineTests.cs` pass unchanged.

---

## Inc 1: TraceDrivenChannel (TDD)

**File**: `Infrastructure/Channel/TraceDrivenChannel.cs`
**Test file**: `tests/PeakCan.Host.Infrastructure.Tests/TraceDrivenChannelTests.cs`

### RED: Test list

```
1. LoadAscii_valid Asc file_populates frames
   - Create TraceDrivenChannel, call LoadAscii(small Asc file)
   - Assert: no exception, IsConnected == false

2. LoadAscii_empty file_sets _playStartTimestamp to -1
   - LoadAscii(empty file)
   - Assert: ConnectAsync throws InvalidOperationException

3. LoadAscii_nonexistent file_throws FileNotFoundException
   - LoadAscii("nonexistent.asc")
   - Assert: FileNotFoundException (or clear error)

4. LoadAscii_file exceeds MaxTraceFrames_throws
   - LoadAscii with MaxTraceFrames=10, file has 11 frames
   - Assert: InvalidOperationException

5. LoadAscii_on Playing_throws InvalidOperationException
   - LoadAscii, ConnectAsync, then LoadAscii again
   - Assert: InvalidOperationException

6. ConnectAsync_on Unloaded_throws InvalidOperationException
   - Don't call LoadAscii, call ConnectAsync
   - Assert: InvalidOperationException

7. ConnectAsync_starts frame emission
   - LoadAscii(small Asc with 3 frames), ConnectAsync
   - Subscribe to FrameReceived, collect frames
   - Assert: receives 3 CanFrames within timeout

8. ConnectAsync_ignores baud and fd parameters
   - ConnectAsync with different baud/fd values
   - Assert: no exception, same behavior

9. FrameReceived_correct CanFrame conversion
   - Load Asc with known frame (ID=0x123, data=[0x01,0x02])
   - ConnectAsync, capture frame
   - Assert: frame.Id.Raw == 0x123, frame.Data.Span [0]==0x01, [1]==0x02

10. FrameReceived_extended frame ID > 0x7FF_sets FrameFormat.Extended
    - Load Asc with extended frame (ID=0x18FEF100)
    - ConnectAsync, capture frame
    - Assert: frame.Id.IsExtended == true, frame.Id.Raw == 0x18FEF100

11. FrameReceived_timestamp converted seconds to microseconds
    - Load Asc with frame at t=1.5 seconds
    - Capture frame
    - Assert: frame.Timestamp.TotalMicroseconds == 1_500_000

12. OnTick_respects MaxFramesPerTick batch limit
    - Load Asc with 200 frames at same timestamp
    - ConnectAsync with MaxFramesPerTick=50
    - Assert: first tick emits <= 50 frames; remaining emitted in subsequent ticks

13. OnTick_stops timer when all frames emitted
    - Load Asc with 3 frames, ConnectAsync
    - Wait for completion
    - Assert: no more FrameReceived events after all 3 frames

14. DisposeAsync_stops timer and prevents new callbacks
    - LoadAscii, ConnectAsync, then DisposeAsync
    - Assert: no FrameReceived events after Dispose

15. DisposeAsync_idempotent
    - DisposeAsync twice
    - Assert: no exception on second call

16. State machine_DisconnectAsync_after Connect_stops playback
    - LoadAscii, ConnectAsync, DisconnectAsync
    - Assert: no new FrameReceived events

17. NTP clock jump backward_clamps elapsed to zero
    - (Unit test: mock DateTime.UtcNow is not feasible; test the logic indirectly)
    - Alternative: test that elapsed_wall < 0 path resets _playStartWallClock
    - Use InternalsVisibleTo to test the reset logic directly if needed
```

### GREEN: Implementation points

1. Constructor stores `ChannelId`, `ILogger`, `MaxFramesPerTick`, `MaxTraceFrames`.
2. `LoadAscii`: call `AscParser.ParseAsync(path)`, guard `Count <= MaxTraceFrames`, populate `_frames` under `_framesLock`, set `_playStartTimestamp`.
3. `ConnectAsync`: guard state (Loaded, `_playStartTimestamp >= 0`), set `_playStartWallClock = DateTime.UtcNow`, create `Timer(OnTick, null, 0, 1)`.
4. `OnTick`: CAS `_state` 0->1. Compute `elapsed_wall`. If negative, reset `_playStartWallClock` + clamp. `lock(_framesLock)` collect frames into `_emitBuffer` (up to `MaxFramesPerTick`). Copy buffer. Emit outside lock via `FrameReceived?.Invoke`. CAS `_state` 1->0.
5. `WriteAsync`: return `Result<Unit>.Ok(default)` (no-op).
6. `DisconnectAsync`: `_timer?.Change(Infinite, Infinite)`, set `IsConnected = false`.
7. `DisposeAsync`: CAS/SpinUntil/Exchange state machine per spec 3.2.
8. `ToCanFrame`: `frame.Id > 0x7FF -> Extended`, `frame.Timestamp * 1_000_000 -> Timestamp(ulong)`.

### IMPROVE

- Use `Stopwatch.GetTimestamp()` instead of `DateTime.UtcNow` for monotonic timing (spec Section 9 says "acceptable" but Stopwatch is better practice).
- Add `_logger` diagnostics: log frame count on load, log on state transitions.
- Consider `ConfiguredConfigureAwait(false)` if any await in the path (none expected in OnTick).

---

## Inc 2: HILAssertionContext (TDD)

**File**: `Infrastructure/HIL/HILAssertionContext.cs`
**Test file**: `tests/PeakCan.Host.Infrastructure.Tests/HILAssertionContextTests.cs`

### Shared test infrastructure

```csharp
// Fake ICanChannel for testing HILAssertionContext without TraceDrivenChannel
internal sealed class FakeCanChannel : ICanChannel
{
    public ChannelId Id => new(1);
    public bool IsConnected { get; private set; }
    public event Action<CanFrame>? FrameReceived;
    public event Action<ReadLoopError>? ReadLoopError;

    public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
    { IsConnected = true; return Task.FromResult(Result<Unit>.Ok(default)); }

    public Task DisconnectAsync(CancellationToken ct = default)
    { IsConnected = false; return Task.CompletedTask; }

    public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
    => ValueTask.FromResult(Result<Unit>.Ok(default));

    public ValueTask DisposeAsync() { IsConnected = false; return ValueTask.CompletedTask; }

    // Test helper: simulate receiving a frame
    public void SimulateFrame(CanFrame frame) => FrameReceived?.Invoke(frame);
}

// Fake IDbcLookup with configurable messages
internal sealed class FakeDbcLookup : IDbcLookup
{
    private readonly Dictionary<uint, Message> _messages = new();
    public void AddMessage(Message msg) => _messages[msg.Id] = msg;
    public Message? FindMessage(uint canId) =>
        _messages.TryGetValue(canId, out var msg) ? msg : null;
}
```

### RED: Test list

```
1. Constructor_subscribes to channel FrameReceived
   - Create HILAssertionContext with FakeCanChannel
   - SimulateFrame on FakeCanChannel
   - Assert: OnFrame is invoked (verify via SubscribeDecodedFrames callback)

2. OnFrame_writes CanFrame to channel without blocking
   - Create context, SimulateFrame
   - Assert: ConsumerLoop receives the frame (SubscribeDecodedFrames callback fires)

3. ConsumerLoop_decodes signals and populates signalCache
   - Setup FakeDbcLookup with a message (ID=0x100, 1 signal "RPM" at startBit=0, length=8)
   - SimulateFrame with matching data
   - Assert: GetSignalValue("MsgName.RPM") returns expected physical value

4. ConsumerLoop_extended frame DBC lookup uses bit 31 key
   - Setup FakeDbcLookup with extended message (Id=0x98FEF100)
   - SimulateFrame with CanFrame(Id=0x18FEF100, IsExtended=true)
   - Assert: GetSignalValue returns decoded value (ToDbcLookupKey converts 0x18FEF100 -> 0x98FEF100)

5. ConsumerLoop_frame not in DBC_emits DecodedFrame with empty signals
   - SimulateFrame with unknown CAN ID
   - Assert: SubscribeDecodedFrames callback receives DecodedFrame with empty Signals dict

6. ConsumerLoop_subscriber callback throws_isolated
   - Register subscriber that throws
   - Register second subscriber that records
   - SimulateFrame
   - Assert: second subscriber still receives callback

7. SubscribeDecodedFrames_returns IDisposable that unsubscribes
   - Subscribe, then Dispose the subscription
   - SimulateFrame
   - Assert: callback NOT invoked after Dispose

8. SubscribeDecodedFrames_multiple subscribers all notified
   - Subscribe 3 callbacks
   - SimulateFrame
   - Assert: all 3 invoked

9. GetSignalValue_signal not found_returns null
   - GetSignalValue("Nonexistent.Signal")
   - Assert: null

10. GetSignalValue_fresh signal_returns value
    - Decode a frame, immediately query
    - Assert: returns decoded value

11. GetSignalValue_stale signal_returns null
    - Decode frame at t=0, advance _currentTimestamp to t=6s
    - GetSignalValue(name, maxAgeMs=5000)
    - Assert: null (6s > 5s)

12. GetSignalValue_maxAgeMs=0_disables staleness check
    - Decode frame at t=0, advance to t=100s
    - GetSignalValue(name, maxAgeMs=0)
    - Assert: returns value (staleness disabled)

13. CurrentTimestamp_updates on each frame
    - SimulateFrame with Timestamp=1000000 (1s)
    - Assert: CurrentTimestamp == 1000000
    - SimulateFrame with Timestamp=2000000 (2s)
    - Assert: CurrentTimestamp == 2000000

14. SendFrameAsync_returns success (no-op in Sprint 2)
    - SendFrameAsync(any frame)
    - Assert: result.IsSuccess == true

15. Dispose_unsubscribes from channel
    - Create context, Dispose
    - SimulateFrame on FakeCanChannel
    - Assert: no exception, no callback invoked

16. Dispose_drains channel and cancels consumer
    - Create context, SimulateFrame (queue a frame)
    - Dispose immediately
    - Assert: Dispose completes within 3s (100ms drain + 2s consumer wait)

17. ImmutableList subscribers_thread safe during enumeration
    - Start a long-running subscriber
    - During enumeration, add a new subscriber (from another thread)
    - Assert: no InvalidOperationException
```

### GREEN: Implementation points

1. Constructor: store `_channel`, `_dbcLookup`. Create `_frameChannel` (BoundedChannel DropOldest 10000). Create `FrameReceivedSubscription(channel, OnFrame)`. Start `_consumerTask = Task.Run(ConsumerLoop)`.
2. `OnFrame`: `_currentTimestamp = frame.Timestamp.TotalMicroseconds`. `_frameChannel.Writer.TryWrite(frame)`. Return.
3. `ConsumerLoop`: `await foreach (frame in _frameChannel.Reader.ReadAllAsync(ct))`. Compute `ToDbcLookupKey(frame.Id.Raw, frame.Id.IsExtended)`. `FindMessage(key)`. If found, decode all signals, populate `_signalCache` + `signals` dict. Construct `DecodedFrame`. `Volatile.Read(ref _subscribers)` snapshot. Foreach subscriber: try-catch isolate.
4. `SubscribeDecodedFrames`: `ImmutableList.Interlocked.Add`. Return `SubscriberSubscription` (Dispose -> `ImmutableList.Interlocked.Remove`).
5. `GetSignalValue`: TryGetValue. If `maxAgeMs > 0`: check `ageUs > maxAgeMs * 1000`. Return value or null.
6. `SendFrameAsync`: `return ValueTask.FromResult(Result<Unit>.Ok(default))`.
7. `Dispose`: `_frameSubscription.Dispose()`. `SpinUntil(Count==0, 100ms)`. `_consumerCts.Cancel()`. `try await _consumerTask.WaitAsync(2s) catch OCE`. `_frameChannel.Writer.Complete()`.

### IMPROVE

- Log dropped frames when `TryWrite` returns false (DropOldest mode always returns true, but log for diagnostics).
- Log subscriber callback exceptions at Warning level.
- Consider `ConfigureAwait(false)` in ConsumerLoop's `await foreach`.

---

## Inc 3: CLI Runner (direct implementation + partial TDD)

**Files**:
- `PeakCan.Host.Cli/Program.cs`
- `PeakCan.Host.Cli/CliArgs.cs`
- `PeakCan.Host.Cli/ConsoleProgress.cs`
- `PeakCan.Host.Cli/ResultWriter.cs`
- `PeakCan.Host.Cli/HeadlessHostBuilder.cs`
- `PeakCan.Host.Infrastructure/HIL/HeadlessDbcLookup.cs`
- `PeakCan.Host.Infrastructure/HIL/HeadlessFixtureResolver.cs`
- `PeakCan.Host.Infrastructure/HIL/FrameReceivedSubscription.cs` (if not created in Inc 2)

### Direct implementation (no TDD -- glue/config code)

1. **CliArgs.cs**: Parse `--dbc`, `--trace`, `--suite`, `--output`, `--format` from `string[]`.
2. **HeadlessDbcLookup.cs**: `HeadlessDbcLookup(DbcDocument doc)` -> `_messages[msg.Id] = msg`. `FindMessage(uint canId) -> GetValueOrDefault`.
3. **HeadlessFixtureResolver.cs**: Returns `NoOpTestFixture` for any key.
4. **HeadlessHostBuilder.cs**: `Host.CreateApplicationBuilder()`. Register DI per spec 6.1. Return `builder.Build()`.
5. **ConsoleProgress.cs**: `IProgress<TestProgress>` with colored output per spec 6.5.
6. **Program.cs**: Per spec 6.7. Parse args, build host, load suite JSON with `HILJsonOptions.Default`, connect channel, execute, write results.

### TDD: ResultWriter (TRX output)

**Test file**: `tests/PeakCan.Host.Cli.Tests/ResultWriterTests.cs` (or inline in Inc 4)

```
1. WriteTrx_valid result_produces valid XML
   - Create TestSuiteResult with 1 passed case, 1 failed case
   - WriteTrx(result, "output.trx")
   - Assert: file exists, XML parses, contains <UnitTestResult> elements

2. WriteTrx_passed case_outcome="Passed"
   - Result with all passed cases
   - Assert: XML contains outcome="Passed"

3. WriteTrx_failed case_outcome="Failed"
   - Result with 1 failed case
   - Assert: XML contains outcome="Failed"

4. WriteTrx_empty suite_produces valid XML with no results
   - Result with 0 cases
   - Assert: XML valid, <Results> empty
```

### Verification

- `dotnet build` on CLI project succeeds.
- `dotnet test` on CLI tests passes.
- Manual: `peakcan-hil --dbc test.dbc --trace test.asc --suite test.json --format console` produces console output.
- Manual: `--format trx --output result.trx` produces TRX file.

---

## Inc 4: Integration Test (TDD)

**Test file**: `tests/PeakCan.Host.Infrastructure.Tests/HILIntegrationTests.cs`

### RED: Test list

```
1. End_to_end_DBC + trace + suite_executes and produces result
   - Load real DBC file (test fixture)
   - Load real ASC trace file (test fixture)
   - Create minimal TestSuite JSON with 1 WaitForSignal step
   - Build HeadlessHostBuilder, execute
   - Assert: TestSuiteResult not null, TotalCases == 1

2. End_to_end_extended frame signal_decoded correctly
   - Use DBC with extended frame message (J1939-style)
   - Use ASC trace with matching extended frame
   - Suite with AssertSignal step on the extended frame's signal
   - Assert: result.AllPassed == true

3. End_to_end_console output_contains progress
   - Execute with ConsoleProgress
   - Capture console output
   - Assert: output contains "[1/N]" progress markers

4. End_to_end_TRX output_valid XML
   - Execute, write TRX
   - Parse TRX XML
   - Assert: <TestRun> root, <UnitTestResult> entries match case count

5. End_to_end_Dispose_cleans up without hanging
   - Execute, then Dispose host
   - Assert: Dispose completes within 5s
```

### Test fixtures

- Use existing test data from `peakcan-host-test-fixtures` memory ([Logging.asc + DBC]).
- Create minimal TestSuite JSON inline or as embedded resource.

### GREEN

- Fix any wiring issues found during integration.
- Verify InternalsVisibleTo configurations work.
- Verify DI resolution order (ICanChannel -> HILAssertionContext -> AssertionPrimitives -> executors -> TestSuiteEngine).

---

## Risk Register

| Risk | Impact | Mitigation |
|---|---|---|
| AscParser API mismatch | Inc 1 blocked | Pre-flight verification |
| InternalsVisibleTo not configured | Inc 3 compile failure | Add IVT entries in Inc 0 |
| Serilog package version conflict | Inc 3 build failure | Verify package versions against existing App project |
| Timer precision on CI (Windows vs Linux) | Inc 1 timing tests flaky | Use >= 50ms tolerances in timing assertions |
| HILAssertionContext consumer thread leaks in tests | Test runner hangs | Verify Dispose in test cleanup |
| DBC test fixture missing extended frame | Inc 2/4 gap | Create synthetic DBC with extended message |

---

## Definition of Done

- [ ] Inc 0: IAssertionContext updated, Sprint 1 tests pass
- [ ] Inc 1: TraceDrivenChannel tests pass (17 tests)
- [ ] Inc 2: HILAssertionContext tests pass (17 tests)
- [ ] Inc 3: CLI project builds, ResultWriter tests pass (4 tests)
- [ ] Inc 4: Integration tests pass (5 tests)
- [ ] `dotnet build` entire solution succeeds
- [ ] `dotnet test` all tests pass (Sprint 1 + Sprint 2)
- [ ] InternalsVisibleTo added to Core + Infrastructure
- [ ] No `Console.WriteLine` in production code (only ConsoleProgress)
- [ ] Spec v14 matches implementation
