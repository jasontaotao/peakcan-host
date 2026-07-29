# HIL Sprint 2: TraceDrivenChannel & CLI Runner

**Date**: 2026-07-29
**Status**: Draft v3 (incorporates 2nd round review)
**Depends**: [Sprint 1 design](2026-07-29-hil-sprint1-design.md) (complete)
**Scope**: Virtual CAN channel + headless test execution

---

## 1. Goal

Enable **offline HIL testing** without real PCAN hardware:

1. **TraceDrivenChannel** — Replays ASC/BLF trace files as a virtual CAN channel
2. **CLI Runner** — Headless console mode: load DBC + test suite JSON, execute, output results

---

## 2. Key Architecture Decisions

### 2.1 No ChannelRouter for Virtual Channel

`TraceDrivenChannel` raises `FrameReceived` directly. Does NOT route through ChannelRouter.

Rationale: `ChannelRouter.OnChannelFrame` is private (no public injection API). Routing sent frames back would pollute assertions.

### 2.2 HILAssertionContext Subscribes to FrameReceived

`HILAssertionContext` subscribes to `TraceDrivenChannel.FrameReceived` event directly.

### 2.3 WriteAsync Is No-Op (Sprint 2)

Without a physical bus, sent frames have no destination. Stimulus-response deferred to Phase 3.

### 2.4 Own Timer Logic

`TraceDrivenChannel` contains its own timer (ReplayTimeline is internal to Core).

### 2.5 Signal Cache with Timestamps + DropOldest Channel

Reuse Sprint 1's `Channel<T>` + consumer thread model (DropOldest, bounded capacity).

---

## 3. TraceDrivenChannel

### 3.1 File: `Infrastructure/Channel/TraceDrivenChannel.cs`

```csharp
public sealed class TraceDrivenChannel : ICanChannel
{
    private readonly ChannelId _id;
    private readonly ILogger<TraceDrivenChannel>? _logger;
    private readonly List<ReplayFrame> _frames = new();
    private readonly object _framesLock = new();  // guards _frames + _nextFrameIndex
    private int _nextFrameIndex;
    private System.Threading.Timer? _timer;
    private DateTime _playStartWallClock;
    private double _playStartTimestamp = -1;  // -1 = not loaded
    private double _speed = 1.0;

    public ChannelId Id => _id;
    public bool IsConnected { get; private set; }

    public event Action<CanFrame>? FrameReceived;
    public event Action<ReadLoopError>? ReadLoopError;

    public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default);
    public Task DisconnectAsync(CancellationToken ct = default);
    public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default);
    public ValueTask DisposeAsync();

    public void LoadAscii(string path, CancellationToken ct = default);
}
```

### 3.2 Frame Emission Model

```
TraceDrivenChannel
  │
  ├─ LoadAscii(path)
  │    ├─ Read file → AscParser.ParseAsync → frames
  │    ├─ lock(_framesLock) { _frames.Clear(); _frames.AddRange(parsed); _nextFrameIndex = 0; }
  │    ├─ _playStartTimestamp = _frames.Count > 0 ? _frames[0].Timestamp : -1
  │    └─ State: → Loaded
  │
  ├─ ConnectAsync(baud, fd)  [baud/fd IGNORED for virtual channel]
  │    ├─ Guard: State == Loaded, _playStartTimestamp >= 0
  │    ├─ _playStartWallClock = DateTime.UtcNow
  │    └─ _timer = new Timer(OnTick, null, 0, 1)
  │
  ├─ OnTick(state)  [ThreadPool timer callback, ~1ms period]
  │    ├─ elapsed_wall = (DateTime.UtcNow - _playStartWallClock).TotalSeconds * _speed
  │    ├─ target_ts = _playStartTimestamp + elapsed_wall
  │    ├─ emitted = 0
  │    ├─ lock(_framesLock):
  │    │   while (_nextFrameIndex < _frames.Count
  │    │         && _frames[_nextFrameIndex].Timestamp <= target_ts
  │    │         && emitted < MaxFramesPerTick):
  │    │     var frame = ToCanFrame(_frames[_nextFrameIndex])
  │    │     _nextFrameIndex++
  │    │     emitted++
  │    │   toEmit = collected frames
  │    ├─ foreach (frame in toEmit):  [outside lock, to avoid blocking]
  │    │     FrameReceived?.Invoke(frame)
  │    ├─ if (_nextFrameIndex >= _frames.Count):
  │    │     _timer.Change(Timeout.Infinite, Timeout.Infinite)  ← stop timer
  │
  └─ DisposeAsync()
       ├─ _timer?.Dispose()
       ├─ SpinWait.SpinUntil(() => !timerCallbackInProgress, 200ms)
       └─ State → Disposed
```

**BATCH LIMIT**: `MaxFramesPerTick = 100`. If backlog > 100 frames, remaining frames deferred to next tick. If backlog grows unbounded, frames are **dropped** — matching Sprint 1 DropOldest semantics.

### 3.3 Frame Conversion

```csharp
private static CanFrame ToCanFrame(ReplayFrame frame, ChannelId channelId)
{
    var totalUs = (ulong)(frame.Timestamp * 1_000_000.0);
    return new CanFrame(
        new CanId(frame.Id, (frame.Flags & FrameFlags.Extended) != 0 ? FrameFormat.Extended : FrameFormat.Standard),
        frame.Data,
        frame.Flags,
        channelId,
        new Timestamp(totalUs));
}
```

### 3.4 State Machine

```
States: Unloaded → Loaded → Playing → Ended
                  ↑                  ↓
                  └─── Restart ─── Stopped
        Any → Disposed

Transitions:
  LoadAscii:   Unloaded → Loaded,  Loaded → Loaded (reload, resets frame pointer),
               Stopped → Loaded (reload, resets frame pointer)
  ConnectAsync: Loaded → Playing (requires _playStartTimestamp >= 0)
  Timer end:   Playing → Ended (auto when last frame emitted)
  DisconnectAsync: Playing → Stopped,  Stopped → Stopped (idempotent)
  DisposeAsync: Any → Disposed (final)

Illegal:
  ConnectAsync on Unloaded → InvalidOperationException
  ConnectAsync on Loaded with _playStartTimestamp < 0 → InvalidOperationException (empty trace)
  ConnectAsync on Disposed → ObjectDisposedException
  LoadAscii on Playing → InvalidOperationException
```

### 3.5 Configuration

| Parameter | Source | Default |
|---|---|---|
| `MaxFramesPerTick` | Constructor arg | 100 |
| `Speed` | Property (set after LoadAscii) | 1.0 |
| `MaxTraceFrames` | Constructor arg | 2_000_000 |

---

## 4. HILAssertionContext

### 4.1 File: `Infrastructure/HIL/HILAssertionContext.cs`

```csharp
internal sealed class HILAssertionContext : IAssertionContext, IDisposable
{
    private readonly ICanChannel _channel;
    private readonly IDbcLookup _dbcLookup;
    private readonly Channel<DecodedFrame> _decodeChannel;
    private readonly CancellationTokenSource _consumerCts = new();
    private readonly Task _consumerTask;
    private readonly ConcurrentDictionary<string, (double Value, double TimestampUs)> _signalCache = new();
    private volatile double _currentTimestamp;

    public HILAssertionContext(ICanChannel channel, IDbcLookup dbcLookup);

    public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame);
    public double? GetSignalValue(string signalName, int maxAgeMs = 5000);
    public double CurrentTimestamp => _currentTimestamp;
    public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct);
    public void Dispose();
}
```

### 4.2 Thread Model

```
TraceDrivenChannel.FrameReceived
  ↓ (ThreadPool timer thread)
HILAssertionContext.OnFrame(frame)  [MUST NOT block]
  ├─ _currentTimestamp = frame.Timestamp.TotalMicroseconds
  ├─ DBC decode: lookup message → decode signals
  ├─ _signalCache[name] = (value, timestamp)
  ├─ Write DecodedFrame to _decodeChannel (DropOldest, capacity=10000)
  └─ Return immediately

Consumer Thread:
  await foreach (decoded in _decodeChannel.Reader.ReadAllAsync(ct))
    → Invoke all subscriber callbacks synchronously
```

### 4.3 Signal Name Format

Format: `"MessageName.SignalName"`. No restriction on dots — the **last** dot is the separator.

```csharp
// "BMS_Status.EngineRPM" → Message="BMS_Status", Signal="EngineRPM"
// "ECU.Temp.Sensor1"  → Message="ECU.Temp", Signal="Sensor1"
private static int FindSeparator(string signalName) => signalName.LastIndexOf('.');
```

### 4.4 Timestamp + Staleness

```csharp
public double? GetSignalValue(string signalName, int maxAgeMs = 5000)
{
    if (!_signalCache.TryGetValue(signalName, out var entry)) return null;
    if (maxAgeMs > 0)
    {
        var ageUs = _currentTimestamp - entry.TimestampUs;
        if (ageUs > maxAgeMs * 1000.0) return null;  // Stale
    }
    return entry.Value;
}
```

---

## 5. Sprint 1 Interface Update

### 5.1 IAssertionContext Change

```csharp
// Sprint 1 (original):
double? GetSignalValue(string signalName);

// Sprint 2 (updated):
double? GetSignalValue(string signalName, int maxAgeMs = 5000);
```

### 5.2 Sprint 1 Files Affected

| File | Change |
|---|---|
| `Contracts/IAssertionContext.cs` | Add `maxAgeMs` parameter |
| `Tests/.../Fakes/FakeAssertionContext.cs` | Implement new signature |
| `Tests/.../Assertions/AssertionPrimitivesTests.cs` | Unchanged (default param) |
| `Assertions/AssertionPrimitives.cs` | Update call to pass `maxAgeMs: 5000` |

---

## 6. CLI Runner

### 6.1 Entry Point: `PeakCan.Host.Cli/Program.cs`

```
CLI syntax:
  peakcan-hil --dbc path.dbc --trace path.asc --suite tests.json [--output results.trx]

Args:
  --dbc <path>     DBC file (required)
  --trace <path>   ASC/BLF trace file (required)
  --suite <path>   Test suite JSON (required)
  --output <path>  Output file (default: stdout)
  --format <type>  Output format: console|trx (default: console)
```

### 6.2 HeadlessHostBuilder

```csharp
public static class HeadlessHostBuilder
{
    public static IHost Build(CliArgs args)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                // Channel
                services.AddSingleton<ICanChannel, TraceDrivenChannel>();

                // DBC lookup (uses DbcParser directly)
                services.AddSingleton<IDbcLookup>(sp =>
                {
                    var logger = sp.GetRequiredService<ILogger<HeadlessDbcLookup>>();
                    return HeadlessDbcLookup.Load(args.DbcPath, logger);
                });

                // HIL context
                services.AddSingleton<IAssertionContext, HILAssertionContext>();

                // Step executors (registered individually by concrete type)
                services.AddSingleton<WaitForSignalStepExecutor>();
                services.AddSingleton<AssertSignalStepExecutor>();
                services.AddSingleton<AssertRangeStepExecutor>();
                services.AddSingleton<SendFrameStepExecutor>();
                services.AddSingleton<DelayStepExecutor>();

                // Engine
                services.AddSingleton<TestSuiteEngine>();
            })
            .UseSerilog((ctx, cfg) => cfg.WriteTo.Console().WriteTo.File("hil.log"))
            .Build();
    }
}
```

### 6.3 TestSuiteEngine Constructor Update

```csharp
public TestSuiteEngine(
    IEnumerable<WaitForSignalStepExecutor> waitForSignal,
    IEnumerable<AssertSignalStepExecutor> assertSignal,
    IEnumerable<AssertRangeStepExecutor> assertRange,
    IEnumerable<SendFrameStepExecutor> sendFrame,
    IEnumerable<DelayStepExecutor> delay)
{
    _executors = new Dictionary<TestCaseStepKind, IStepExecutor>
    {
        [TestCaseStepKind.WaitForSignal] = waitForSignal.Single(),
        [TestCaseStepKind.AssertSignal] = assertSignal.Single(),
        [TestCaseStepKind.AssertRange] = assertRange.Single(),
        [TestCaseStepKind.SendFrame] = sendFrame.Single(),
        [TestCaseStepKind.Delay] = delay.Single(),
    };
}
```

### 6.4 HeadlessDbcLookup

```csharp
internal sealed class HeadlessDbcLookup : IDbcLookup
{
    private readonly Dictionary<uint, Message> _messages = new();

    public static HeadlessDbcLookup Load(string path, ILogger? logger = default)
    {
        var text = File.ReadAllText(path);
        var result = DbcParser.Parse(text, maxMessageCount: 0, ct: default);
        if (!result.IsSuccess)
            throw new InvalidOperationException($"DBC parse failed: {result.Error?.Message}");

        var lookup = new HeadlessDbcLookup();
        foreach (var msg in result.Value.Messages)
            lookup._messages[msg.Id] = msg;
        logger?.LogInformation("DBC loaded: {Count} messages from {Path}", lookup._messages.Count, path);
        return lookup;
    }

    public Message? FindMessage(uint canId) =>
        _messages.TryGetValue(canId, out var msg) ? msg : null;
}
```

### 6.5 Output Formats

| Format | Implementation | When |
|---|---|---|
| Console ANSI | `Console.WriteLine` with color codes | Default |
| TRX | Hand-write XML (MSTest .trx schema v2) | `--format trx` |
| JUnit XML | JUnitXml.TestLogger NuGet | Sprint 3 |

---

## 7. File Structure

```
PeakCan.Host.Cli/                         ← NEW project (Exe, net10.0)
  Program.cs
  CliArgs.cs
  ConsoleProgress.cs
  ResultWriter.cs

PeakCan.Host.Infrastructure/
  Channel/
    TraceDrivenChannel.cs                 ← NEW
  HIL/
    HILAssertionContext.cs                ← NEW
    HeadlessDbcLookup.cs                  ← NEW

Sprint 1 modifications (interface change):
  Core/HIL/Contracts/IAssertionContext.cs  ← Update signature
  Tests/.../Fakes/FakeAssertionContext.cs  ← Update signature
  Core/HIL/Assertions/AssertionPrimitives.cs ← Update call
  Core/HIL/TestSuiteEngine.cs             ← Update constructor

tests/PeakCan.Host.Infrastructure.Tests/
  TraceDrivenChannelTests.cs              ← NEW
  HILAssertionContextTests.cs             ← NEW
```

---

## 8. TDD Increments

| Increment | Component | Tests |
|---|---|---|
| Inc 1 | TraceDrivenChannel | Load ASCII, Connect fires frames, State machine, Empty trace guard, Frame conversion |
| Inc 2 | HILAssertionContext + IAssertionContext update | Subscribe receives decoded frames, GetSignalValue staleness (5s), Sprint 1 backward compat |
| Inc 3 | CLI Runner + HeadlessHostBuilder | Load suite JSON, Execute produces result, Console output, TRX output, --trace arg |
| Inc 4 | Integration | Load DBC + trace + suite → full execution → pass/fail |

---

## 9. Risks

| Risk | Mitigation |
|---|---|
| Timer drift on slow machines | Use `Stopwatch` for monotonic wall-clock |
| ASC files with >2M frames | Bounded frame cache (`MaxTraceFrames`) |
| DBC not loaded at startup | Fail fast with clear error |
| ThreadPool starvation | MaxFramesPerTick = 100, DropOldest channel |
| FrameReceived during Dispose | SpinWait until callback exits (200ms timeout) |

---

## 10. Design Decision Record

| ID | Decision | Rationale |
|---|---|---|
| D1 | No ChannelRouter for virtual channel | OnChannelFrame is private; avoids loopback |
| D2 | Direct FrameReceived subscription | Existing single-source model |
| D3 | WriteAsync is no-op | No physical bus; stimulus-response is Phase 3 |
| D4 | Own timer logic | ReplayTimeline is internal to Core |
| D5 | Signal cache + DropOldest | Prevents stale reads + ThreadPool blocking |
| D6 | Last-dot separator for signal names | Accommodates dots in message names |
| D7 | Default maxAgeMs = 5000ms | Accommodates low-frequency (0.5Hz) signals |
| D8 | Engine takes concrete executor types | DI resolves single instances by type |
| D9 | IAssertionContext backward-compatible | Default parameter preserves existing calls |
| D10 | Bounded frame cache | Prevents OOM on large trace files |
| D11 | Explicit --trace CLI arg | Required for trace-driven testing |
