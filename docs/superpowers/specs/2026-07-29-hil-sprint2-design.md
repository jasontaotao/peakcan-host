# HIL Sprint 2: TraceDrivenChannel & CLI Runner

**Date**: 2026-07-29
**Status**: Draft v5 (incorporates 4th round review)
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

Rationale: `ChannelRouter.OnChannelFrame` is private. Routing sent frames back would pollute assertions.

### 2.2 HILAssertionContext Subscribes to FrameReceived Event

`HILAssertionContext` subscribes to `ICanChannel.FrameReceived` event via an adapter that returns `IDisposable`. Subscribe in constructor, unsubscribe in Dispose.

### 2.3 WriteAsync Is No-Op (Sprint 2)

Without a physical bus, sent frames have no destination. Stimulus-response deferred to Phase 3.

### 2.4 Own Timer Logic

`TraceDrivenChannel` contains its own timer (ReplayTimeline is internal to Core).

### 2.5 Signal Cache with Timestamps + DropOldest Channel

Reuse Sprint 1's `Channel<T>` + consumer thread model (DropOldest, bounded capacity).

### 2.6 Keep Sprint 1 TestSuiteEngine Constructor

`TestSuiteEngine` keeps Sprint 1 constructor: `TestSuiteEngine(IFixtureResolver, IEnumerable<IStepExecutor>)`.

---

## 3. TraceDrivenChannel

### 3.1 File: `Infrastructure/Channel/TraceDrivenChannel.cs`

```csharp
public sealed class TraceDrivenChannel : ICanChannel
{
    private readonly ChannelId _id;
    private readonly ILogger<TraceDrivenChannel>? _logger;
    private readonly List<ReplayFrame> _frames = new();
    private readonly object _framesLock = new();
    private int _nextFrameIndex;
    private System.Threading.Timer? _timer;
    private long _state;  // Interlocked: 0=Idle, 1=CallbackInProgress, 2=Disposing
    private DateTime _playStartWallClock;
    private double _playStartTimestamp = -1;
    private double _speed = 1.0;
    private int _endOfTraceReached;

    // Reusable buffer for frame emission (avoids per-tick allocation)
    private readonly List<CanFrame> _emitBuffer = new(capacity: 128);

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
  │    ├─ Guard: parsed.Count <= MaxTraceFrames (throw if exceeded)
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
  │    ├─ Atomic state check: if current state == Disposing → return immediately
  │    ├─ Interlocked.Exchange(ref _state, CallbackInProgress)
  │    ├─ elapsed_wall = (DateTime.UtcNow - _playStartWallClock).TotalSeconds * _speed
  │    ├─ target_ts = _playStartTimestamp + elapsed_wall
  │    ├─ emitted = 0
  │    ├─ lock(_framesLock):
  │    │   _emitBuffer.Clear()
  │    │   while (_nextFrameIndex < _frames.Count
  │    │         && _frames[_nextFrameIndex].Timestamp <= target_ts
  │    │         && emitted < MaxFramesPerTick):
  │    │     _emitBuffer.Add(ToCanFrame(_frames[_nextFrameIndex]))
  │    │     _nextFrameIndex++
  │    │     emitted++
  │    ├─ bufferCopy = _emitBuffer.ToList()  [shallow copy for emission]
  │    ├─ Interlocked.Exchange(ref _state, Idle)
  │    ├─ foreach (frame in bufferCopy):  [outside lock + state=Idle]
  │    │     FrameReceived?.Invoke(frame)
  │    ├─ if (_nextFrameIndex >= _frames.Count):
  │    │     _timer.Change(Timeout.Infinite, Timeout.Infinite)  ← pause timer
  │
  └─ DisposeAsync()
       ├─ Interlocked.Exchange(ref _state, Disposing)
       ├─ _timer?.Dispose()
       ├─ SpinWait.Interlocked.CompareExchange(ref _state, Idle, Disposing) retry loop (200ms)
       │   [wait until any in-flight OnTick exits its Idle state transition]
       └─ State → Disposed
```

**State machine** (Interlocked):
- `0 = Idle`: No callback running
- `1 = CallbackInProgress`: OnTick executing (inside lock or emitting)
- `2 = Disposing`: DisposeAsync running, no new callbacks allowed

**TOCTOU fix**: OnTick atomically transitions Idle→CallbackInProgress. If state is Disposing, return immediately. DisposeAsync transitions to Disposing BEFORE disposing timer, then waits for any in-flight callback to finish its Idle transition.

### 3.3 Frame Conversion

```csharp
private static CanFrame ToCanFrame(ReplayFrame frame, ChannelId channelId)
{
    var format = (frame.Id > 0x7FFu) ? FrameFormat.Extended : FrameFormat.Standard;
    var totalUs = (ulong)(frame.Timestamp * 1_000_000.0);
    return new CanFrame(
        new CanId(frame.Id, format),
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

Illegal:
  ConnectAsync on Unloaded → InvalidOperationException
  ConnectAsync on Loaded with _playStartTimestamp < 0 → InvalidOperationException
  ConnectAsync on Disposed → ObjectDisposedException
  LoadAscii on Playing → InvalidOperationException
```

### 3.5 Configuration

| Parameter | Source | Default |
|---|---|---|
| `MaxFramesPerTick` | Constructor arg | 100 |
| `MaxTraceFrames` | Constructor arg | 2_000_000 |
| `Speed` | Property | 1.0 |

---

## 4. HILAssertionContext

### 4.1 File: `Infrastructure/HIL/HILAssertionContext.cs`

```csharp
internal sealed class HILAssertionContext : IAssertionContext, IDisposable
{
    private readonly ICanChannel _channel;
    private readonly IDbcLookup _dbcLookup;
    private readonly Channel<CanFrame> _frameChannel;  // CanFrame (not DecodedFrame)
    private readonly CancellationTokenSource _consumerCts = new();
    private readonly Task _consumerTask;
    private readonly ConcurrentDictionary<string, (double Value, double TimestampUs)> _signalCache = new();
    private volatile double _currentTimestamp;
    private readonly IDisposable _frameSubscription;

    public HILAssertionContext(ICanChannel channel, IDbcLookup dbcLookup);

    public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame);
    public double? GetSignalValue(string signalName, int maxAgeMs = 5000);
    public double CurrentTimestamp => _currentTimestamp;
    public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct);
    public void Dispose();
}
```

### 4.2 Event Subscription Adapter (fixes L1)

```csharp
/// <summary>
/// Wraps ICanChannel.FrameReceived event subscription as IDisposable.
/// </summary>
internal sealed class FrameReceivedSubscription : IDisposable
{
    private ICanChannel? _channel;
    private readonly Action<CanFrame> _handler;

    public FrameReceivedSubscription(ICanChannel channel, Action<CanFrame> handler)
    {
        _channel = channel;
        _handler = handler;
        channel.FrameReceived += handler;
    }

    public void Dispose()
    {
        var ch = Interlocked.Exchange(ref _channel, null);
        if (ch is not null) ch.FrameReceived -= _handler;
    }
}
```

### 4.3 Thread Model (fixes L2, B1)

```
Constructor:
  ├─ _frameSubscription = new FrameReceivedSubscription(_channel, OnFrame)
  └─ _consumerTask = Task.Run(() => ConsumerLoop(_consumerCts.Token))

OnFrame(frame)  [channel's frame thread]
  ├─ _currentTimestamp = frame.Timestamp.TotalMicroseconds
  ├─ _frameChannel.Writer.TryWrite(frame)  [DropOldest, capacity=10000]
  └─ Return immediately  [NO decode here]

ConsumerLoop:
  await foreach (frame in _frameChannel.Reader.ReadAllAsync(ct))
    ├─ DBC decode: lookup message → decode signals
    ├─ _signalCache[name] = (value, timestamp)
    └─ foreach subscriber: subscriber.OnFrame(new DecodedFrame(frame, signals))

Dispose:
  ├─ _frameSubscription.Dispose()  ← unsubscribe (no in-flight wait)
  ├─ Drain remaining frames in _frameChannel (best-effort, 100ms)
  ├─ _consumerCts.Cancel()
  ├─ await _consumerTask (timeout 2s)
  └─ _frameChannel.Writer.Complete()
```

**Key**: Channel carries `CanFrame` (raw frames). Consumer thread does DBC decode + subscriber notification. `DecodedFrame` is constructed on the consumer thread.

### 4.4 Signal Name Format

Format: `"MessageName.SignalName"`. The **last** dot is the separator.

**Known limitation**: If a signal name itself contains a dot, the last-dot rule may produce incorrect split. Documented as a limitation.

### 4.5 Timestamp + Staleness

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
| `Contracts/IAssertionContext.cs` | Add `maxAgeMs` parameter + update xmldoc |
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

                // DBC lookup
                services.AddSingleton<IDbcLookup>(sp =>
                {
                    var logger = sp.GetRequiredService<ILogger<HeadlessDbcLookup>>();
                    return HeadlessDbcLookup.Load(args.DbcPath, logger);
                });

                // HIL context
                services.AddSingleton<IAssertionContext, HILAssertionContext>();

                // Fixture resolver (headless: no-op)
                services.AddSingleton<IFixtureResolver, HeadlessFixtureResolver>();

                // AssertionPrimitives (shared singleton)
                services.AddSingleton<AssertionPrimitives>(sp =>
                    new(sp.GetRequiredService<IAssertionContext>()));

                // Step executors (Sprint 1 pattern: register as IStepExecutor)
                services.AddSingleton<IStepExecutor>(sp =>
                    new WaitForSignalStepExecutor(sp.GetRequiredService<AssertionPrimitives>()));
                services.AddSingleton<IStepExecutor>(sp =>
                    new AssertSignalStepExecutor(sp.GetRequiredService<AssertionPrimitives>()));
                services.AddSingleton<IStepExecutor>(sp =>
                    new AssertRangeStepExecutor(sp.GetRequiredService<AssertionPrimitives>()));
                services.AddSingleton<IStepExecutor, SendFrameStepExecutor>();
                services.AddSingleton<IStepExecutor, DelayStepExecutor>();

                // Engine (Sprint 1 constructor, unchanged)
                services.AddSingleton<TestSuiteEngine>();
            })
            .UseSerilog((ctx, cfg) => cfg.WriteTo.Console().WriteTo.File("hil.log"))
            .Build();
    }
}
```

### 6.3 HeadlessFixtureResolver

```csharp
internal sealed class HeadlessFixtureResolver : IFixtureResolver
{
    private static readonly ITestFixture NoOp = new NoOpTestFixture();
    public ITestFixture Resolve(string key) => NoOp;

    private sealed class NoOpTestFixture : ITestFixture
    {
        public Task SetupAsync(IAssertionContext ctx, CancellationToken ct) => Task.CompletedTask;
        public Task TeardownAsync(IAssertionContext ctx, CancellationToken ct) => Task.CompletedTask;
    }
}
```

### 6.4 HeadlessDbcLookup

```csharp
internal sealed class HeadlessDbcLookup : IDbcLookup
{
    private readonly Dictionary<uint, Message> _messages = new();

    public static HeadlessDbcLookup Load(string path, ILogger? logger = default)
    {
        // DbcParser.Parse takes string input, so we read all text first.
        // Future: implement streaming parser for very large DBC files.
        var text = File.ReadAllText(path, Encoding.UTF8);
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
    HeadlessFixtureResolver.cs            ← NEW

Sprint 1 modifications:
  Core/HIL/Contracts/IAssertionContext.cs  ← Add maxAgeMs + update xmldoc
  Tests/.../Fakes/FakeAssertionContext.cs  ← Implement new signature
  Core/HIL/Assertions/AssertionPrimitives.cs ← Update call

tests/PeakCan.Host.Infrastructure.Tests/
  TraceDrivenChannelTests.cs              ← NEW
  HILAssertionContextTests.cs             ← NEW
```

---

## 8. TDD Increments

| Increment | Component | Tests |
|---|---|---|
| Inc 1 | TraceDrivenChannel | Load ASCII, Connect fires frames, State machine, Empty trace guard, Frame conversion (std + ext ID) |
| Inc 2 | HILAssertionContext + IAssertionContext update | Subscribe receives decoded frames, GetSignalValue staleness (5s), Sprint 1 backward compat |
| Inc 3 | CLI Runner + HeadlessHostBuilder | Load suite JSON, Execute produces result, Console output, TRX output, --trace arg |
| Inc 4 | Integration | Load DBC + trace + suite → full execution → pass/fail |

---

## 9. Risks

| Risk | Mitigation |
|---|---|
| Timer drift on slow machines | Acceptable for offline testing; real-time HIL needs hardware |
| ASC files with >2M frames | `MaxTraceFrames` guard rejects oversized files at load time |
| DBC not loaded at startup | Fail fast with clear error |
| ThreadPool starvation | MaxFramesPerTick = 100, DropOldest channel |
| FrameReceived during Dispose | Interlocked state machine prevents callback during disposal |

---

## 10. Design Decision Record

| ID | Decision | Rationale |
|---|---|---|
| D1 | No ChannelRouter for virtual channel | OnChannelFrame is private; avoids loopback |
| D2 | Direct FrameReceived event subscription | Existing single-source model |
| D3 | WriteAsync is no-op | No physical bus; stimulus-response is Phase 3 |
| D4 | Own timer logic | ReplayTimeline is internal to Core |
| D5 | Signal cache + DropOldest | Prevents stale reads + ThreadPool blocking |
| D6 | Last-dot separator (known limitation) | DBC names may contain dots |
| D7 | Default maxAgeMs = 5000ms | Accommodates low-frequency (0.5Hz) signals |
| D8 | Keep Sprint 1 TestSuiteEngine constructor | Interface-based DI, open for extension |
| D9 | IAssertionContext backward-compatible | Default parameter preserves existing calls |
| D10 | Bounded frame cache | MaxTraceFrames guard at load time |
| D11 | Explicit --trace CLI arg | Required for trace-driven testing |
| D12 | Infer extended frame from ID > 0x7FF | FrameFlags.Extended doesn't exist |
| D13 | DBC decode on consumer thread | Keeps timer callback fast |
| D14 | Interlocked state machine for Dispose safety | Prevents TOCTOU race |
| D15 | Reusable emit buffer | Avoids per-tick List allocation |
| D16 | Shared AssertionPrimitives singleton | One instance for all executors |
