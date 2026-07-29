# HIL Sprint 2: TraceDrivenChannel & CLI Runner

**Date**: 2026-07-29
**Status**: Draft v2 (incorporates 14 review findings)
**Depends**: [Sprint 1 design](2026-07-29-hil-sprint1-design.md) (complete)
**Scope**: Virtual CAN channel + headless test execution

---

## 1. Goal

Enable **offline HIL testing** without real PCAN hardware:

1. **TraceDrivenChannel** — Replays ASC/BLF trace files as a virtual CAN channel, implementing `ICanChannel`
2. **CLI Runner** — Headless console mode that loads DBC + test suite JSON, executes via `TestSuiteEngine`, outputs results

---

## 2. Key Architecture Decisions

### 2.1 No ChannelRouter for Virtual Channel

**Decision**: `TraceDrivenChannel` raises `FrameReceived` directly. Does NOT route through `ChannelRouter`.

**Rationale (fixes L1, L2, L5)**:
- `ChannelRouter.OnChannelFrame` is `private` — no public frame injection API exists (L2)
- Routing sent frames back through the router would pollute assertion reads with test's own stimulus (L1)
- Sprint 1's `IAssertionContext` already uses a `Channel<T>` + consumer thread model — we reuse that pattern directly (L5)

### 2.2 HILAssertionContext Subscribes to FrameReceived

**Decision**: `HILAssertionContext` subscribes to `TraceDrivenChannel.FrameReceived` (like any other consumer), NOT via `ChannelRouter`.

**Rationale**: Avoids the private `OnChannelFrame` issue entirely. The channel → consumer event pattern is the existing single-source model documented in `IFrameSource.cs` comments.

### 2.3 WriteAsync Is a No-Op (Sprint 2)

**Decision**: `TraceDrivenChannel.WriteAsync` returns success immediately. Stimulus-response testing deferred to Phase 3.

**Rationale (fixes L1, T1)**: Without a separate physical bus, sent frames have no destination. Echo suppression / direction markers would add complexity without a real use case in Sprint 2 (trace replay is read-only).

### 2.4 ReplayTimeline Reimplementation

**Decision**: `TraceDrivenChannel` contains its own timer-based replay logic. Does NOT reference `ReplayTimeline` (which is `internal` to Core).

**Rationale (fixes L3)**: Avoids cross-layer internal access. The timer algorithm is simple (~30 lines) and specific to the channel's event-emission model.

### 2.5 Signal Cache with Timestamps

**Decision**: `SignalCache` entries include both value and timestamp. `GetSignalValue` accepts an optional `maxAgeMs` parameter.

**Rationale (fixes B1)**: Prevents assertions from using stale signal data.

---

## 3. TraceDrivenChannel

### 3.1 File: `Infrastructure/Channel/TraceDrivenChannel.cs`

```csharp
public sealed class TraceDrivenChannel : ICanChannel
{
    private readonly ChannelId _id;
    private readonly ILogger<TraceDrivenChannel>? _logger;
    private readonly List<ReplayFrame> _frames = new();
    private System.Threading.Timer? _timer;
    private DateTime _playStartWallClock;
    private double _playStartTimestamp;
    private double _speed = 1.0;

    public ChannelId Id => _id;
    public bool IsConnected { get; private set; }

    public event Action<CanFrame>? FrameReceived;
    public event Action<ReadLoopError>? ReadLoopError;

    // Load trace file (call before ConnectAsync)
    public async Task LoadAsync(string path, CancellationToken ct = default);

    // ICanChannel implementation
    public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default);
    public Task DisconnectAsync(CancellationToken ct = default);
    public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default);
    public ValueTask DisposeAsync();
}
```

### 3.2 Frame Emission Model (fixes L4, B2)

```
TraceDrivenChannel
  │
  ├─ LoadAsync(path)
  │    └─ AscParser.ParseAsync(file) → frames stored in _frames
  │
  ├─ ConnectAsync() → start replay
  │    ├─ BaudRate / fd parameters are IGNORED (virtual channel)
  │    ├─ _playStartWallClock = DateTime.UtcNow
  │    ├─ _playStartTimestamp = _frames[0].Timestamp
  │    └─ _timer = new Timer(OnTick, null, 0, 1)
  │
  ├─ OnTick(state)  [ThreadPool timer callback, ~1ms period]
  │    ├─ elapsed_wall = (DateTime.UtcNow - _playStartWallClock).TotalSeconds * _speed
  │    ├─ target_ts = _playStartTimestamp + elapsed_wall
  │    ├─ emitted = 0
  │    ├─ while (_nextFrameIndex < _frames.Count
  │    │     && _frames[_nextFrameIndex].Timestamp <= target_ts
  │    │     && emitted < MaxFramesPerTick):    ← BATCH LIMIT (fixes B2)
  │    │     FrameReceived?.Invoke(ToCanFrame(_frames[_nextFrameIndex]))
  │    │     _nextFrameIndex++
  │    │     emitted++
  │    └─ if (_nextFrameIndex >= _frames.Count):
  │         _timer.Dispose()  ← end of trace
  │
  └─ WriteAsync(frame) → return Result<Unit>.Ok(default)
       (no-op, Sprint 2: no stimulus-response)
```

**BATCH LIMIT** (`MaxFramesPerTick = 100`): Prevents timer callback from monopolizing the ThreadPool when many frames are due.

### 3.3 Frame Conversion (fixes D5)

```csharp
private static CanFrame ToCanFrame(ReplayFrame frame, ChannelId channelId) => new(
    new CanId(frame.Id, (frame.Flags & FrameFlags.Extended) != 0 ? FrameFormat.Extended : FrameFormat.Standard),
    frame.Data,
    frame.Flags & ~FrameFlags.Extended,  // Extended flag is encoded in CanId.Format, not Flags
    channelId,
    frame.Timestamp * 1_000_000);  // seconds → microseconds
```

### 3.4 State Machine (fixes B3)

```
States: Unloaded → Loaded → Playing → [Ended|Stopped]
        Loaded → Disposed
        Playing → Stopped → Loaded (restart)
        Any → Disposed (final)

Transitions:
  LoadAsync:   Unloaded → Loaded,  Loaded → Loaded (reload, resets)
  ConnectAsync: Loaded → Playing
  DisconnectAsync: Playing → Stopped, Stopped → Stopped (idempotent)
  DisposeAsync: Any → Disposed (final)

Illegal:
  ConnectAsync on Unloaded → InvalidOperationException
  ConnectAsync on Disposed → ObjectDisposedException
  LoadAsync on Playing → InvalidOperationException
```

### 3.5 Dispose Order (fixes B4)

```
DisposeAsync():
  1. Stop timer (prevents new FrameReceived events)
  2. Wait 100ms for in-flight callbacks to complete
  3. Clear _frames list
  4. Set IsConnected = false
```

### 3.6 Configuration (fixes T2)

| Parameter | Source | Default |
|---|---|---|
| `MaxFramesPerTick` | Constructor arg | 100 |
| `Speed` | Property (set after LoadAsync) | 1.0 |

CanIdFilter is NOT applied at the channel level. Filtering happens at the sink layer (HILAssertionContext subscribes to specific IDs via `SubscribeDecodedFrames`).

---

## 4. HILAssertionContext

### 4.1 File: `Infrastructure/HIL/HILAssertionContext.cs`

```csharp
internal sealed class HILAssertionContext : IAssertionContext, IDisposable
{
    private readonly ICanChannel _channel;
    private readonly IDbcLookup _dbcLookup;
    private readonly Channel<DecodedFrame> _channel;
    private readonly CancellationTokenSource _consumerCts = new();
    private readonly Task _consumerTask;
    private readonly ConcurrentDictionary<string, (double Value, double TimestampUs)> _signalCache = new();
    private double _currentTimestamp;

    public HILAssertionContext(ICanChannel channel, IDbcLookup dbcLookup);

    public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame);
    public double? GetSignalValue(string signalName, int maxAgeMs = 1000);
    public double CurrentTimestamp => _currentTimestamp;
    public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct);
    public void Dispose();
}
```

### 4.2 Thread Model (fixes L5)

```
TraceDrivenChannel.FrameReceived
  ↓ (ThreadPool timer thread)
HILAssertionContext.OnFrame(frame)  [IFrameSink-style callback, MUST NOT block]
  ├─ DBC decode: lookup message → decode signals (fast, <1μs per signal)
  ├─ Update _signalCache[name] = (value, timestamp)
  ├─ Write DecodedFrame to Channel<DecodedFrame> (Wait mode, capacity=10000)
  └─ Return immediately

Consumer Thread (_consumerTask):
  ↓ (dedicated ThreadPool thread)
  await foreach (decoded in _channel.Reader.ReadAllAsync(ct))
    → Invoke all subscriber callbacks synchronously
```

**Key**: Signal decode happens on the fast path (timer thread) because `SignalDecoder.Decode` is O(signals-per-message) and typically <1μs. The `Channel<T>` only carries the final `DecodedFrame` to subscribers, preserving the Sprint 1 non-blocking contract.

### 4.3 Signal Name Format (fixes B5)

Format: `"MessageName.SignalName"` where:
- `MessageName` does NOT contain `.` (DBC message names use `_` or CamelCase)
- `SignalName` may contain `.` — the **first** `.` is the separator

```csharp
// Parsing: "BMS_Status.EngineRPM" → Message="BMS_Status", Signal="EngineRPM"
//          "ECU.Temp.Sensor1"  → Message="ECU", Signal="Temp.Sensor1"
public static string GetSignalValue(string signalName, int maxAgeMs = 1000)
{
    var firstDot = signalName.IndexOf('.');
    if (firstDot < 0) return null; // No separator → not found
    // ... lookup using full signalName as cache key
}
```

Cache key is the full `signalName` string (not split). The split is only needed for DBC lookup. This avoids ambiguity.

### 4.4 Timestamp Validation (fixes B1)

```csharp
public double? GetSignalValue(string signalName, int maxAgeMs = 1000)
{
    if (!_signalCache.TryGetValue(signalName, out var entry)) return null;
    if (maxAgeMs > 0)
    {
        var ageUs = _currentTimestamp - entry.TimestampUs;
        if (ageUs > maxAgeMs * 1000) return null; // Stale
    }
    return entry.Value;
}
```

---

## 5. CLI Runner

### 5.1 Entry Point: `PeakCan.Host.Cli/Program.cs` (fixes D6)

```csharp
// CLI syntax:
//   peakcan-hil --dbc path.dbc --suite tests.json [--output results.trx]

var args = ParseArgs(args);
await using var host = HeadlessHostBuilder.Build(args);
var engine = host.Services.GetRequiredService<TestSuiteEngine>();
var suite = await LoadTestSuiteAsync(args.Suite);
var context = host.Services.GetRequiredService<IAssertionContext>();
var channel = host.Services.GetRequiredService<ICanChannel>();

await ((TraceDrivenChannel)channel).LoadAsync(args.Trace);
await channel.ConnectAsync(BaudRate.Baud500K, fd: true);

var result = await engine.ExecuteAsync(suite, context,
    new TestSuiteConfig(), new ConsoleProgress(), default);

await WriteResultsAsync(result, args.Output);
```

### 5.2 HeadlessHostBuilder (fixes D1, D2, D3, D4)

```csharp
public static class HeadlessHostBuilder
{
    public static IHost Build(CliArgs args)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                // Channel + DBC (fixes D1: register concrete type)
                services.AddSingleton<ChannelRouter>();
                services.AddSingleton<IFrameSource>(sp => sp.GetRequiredService<ChannelRouter>());
                services.AddSingleton<ICanChannel, TraceDrivenChannel>();

                // DBC lookup (fixes D3: use DbcParser directly)
                services.AddSingleton<IDbcLookup>(sp =>
                {
                    var logger = sp.GetRequiredService<ILogger<HeadlessDbcLookup>>();
                    return HeadlessDbcLookup.Load(args.DbcPath, logger);
                });

                // HIL context
                services.AddSingleton<IAssertionContext, HILAssertionContext>();

                // Step executors (fixes D4: register all Sprint 1 executors)
                var primitives = new Assertions.AssertionPrimitives(null!); // ctx injected at runtime
                services.AddSingleton<IStepExecutor>(sp =>
                    new WaitForSignalStepExecutor(
                        new Assertions.AssertionPrimitives(sp.GetRequiredService<IAssertionContext>())));
                services.AddSingleton<IStepExecutor>(sp =>
                    new AssertSignalStepExecutor(
                        new Assertions.AssertionPrimitives(sp.GetRequiredService<IAssertionContext>())));
                services.AddSingleton<IStepExecutor>(sp =>
                    new AssertRangeStepExecutor(
                        new Assertions.AssertionPrimitives(sp.GetRequiredService<IAssertionContext>())));
                services.AddSingleton<IStepExecutor, SendFrameStepExecutor>();
                services.AddSingleton<IStepExecutor, DelayStepExecutor>();

                // Engine
                services.AddSingleton<TestSuiteEngine>();
            })
            .UseSerilog((ctx, cfg) => cfg.WriteTo.Console())
            .Build();
    }
}
```

### 5.3 HeadlessDbcLookup (fixes D3)

```csharp
internal sealed class HeadlessDbcLookup : IDbcLookup
{
    private readonly Dictionary<uint, DbcMessage> _messages = new();

    public static HeadlessDbcLookup Load(string path, ILogger? logger = default)
    {
        var text = File.ReadAllText(path);
        var doc = DbcParser.Parse(text, maxCount: 0, ct: default);
        var lookup = new HeadlessDbcLookup();
        foreach (var msg in doc.Messages)
            lookup._messages[msg.Id] = msg;
        return lookup;
    }

    public DbcMessage? FindMessage(uint canId) =>
        _messages.TryGetValue(canId, out var msg) ? msg : null;
}
```

### 5.4 Output Formats (fixes D7)

| Format | Implementation | Increment |
|---|---|---|
| Console ANSI | Direct `Console.WriteLine` with color | Inc 3 |
| TRX | Hand-write XML (MSTest `.trx` schema) | Inc 3 |
| JUnit XML | Use `JUnitXml.TestLogger` NuGet package | Sprint 3 |

---

## 6. File Structure

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

tests/PeakCan.Host.Infrastructure.Tests/
  TraceDrivenChannelTests.cs              ← NEW
  HILAssertionContextTests.cs             ← NEW
```

---

## 7. TDD Increments

| Increment | Component | Tests |
|---|---|---|
| Inc 1 | TraceDrivenChannel | Load ASC, Connect fires frames, FrameReceived timing, State machine, Dispose |
| Inc 2 | HILAssertionContext | Subscribe receives decoded frames, GetSignalValue with timestamp, MaxAge staleness |
| Inc 3 | CLI Runner | Load suite JSON, Execute produces TestSuiteResult, Console output, TRX output |
| Inc 4 | Integration | Load DBC + trace + suite → full execution → pass/fail result |

---

## 8. Risks

| Risk | Mitigation |
|---|---|
| Timer drift on slow machines | Use `Stopwall.GetTimestamp()` based on first frame |
| ASC files with >1M frames | Stream-based parsing, bounded frame cache |
| DBC not loaded at startup | Fail fast with clear error |
| ThreadPool starvation | MaxFramesPerTick = 100 |

---

## 9. Relationship to Phased Gap Analysis

| Gap ID | Covered In |
|---|---|
| 1.1 CLI Runner | Section 5 |
| 1.7b TraceDrivenChannel | Section 3 |
| 2.x AssertionPrimitives async tests | Fixed by HILAssertionContext (Section 4) — new tests in Inc 2 |

---

## 10. Design Decision Record

| ID | Decision | Rationale | Rejects |
|---|---|---|---|
| D1 | No ChannelRouter for virtual channel | OnChannelFrame is private; avoids loopback pollution | Routing through ChannelRouter |
| D2 | Direct FrameReceived subscription | Existing single-source model; no API break | IFrameSink on ChannelRouter |
| D3 | WriteAsync is no-op | No physical bus in Sprint 2; stimulus-response is Phase 3 | Echo suppression, direction markers |
| D4 | Own timer logic (not ReplayTimeline) | ReplayTimeline is internal to Core | Making ReplayTimeline public |
| D5 | Signal cache with timestamp | Prevents stale reads | Plain string→double cache |
| D6 | First-dot separator for signal names | DBC message names don't contain dots | Escoping, fixed-position |
| D7 | MaxFramesPerTick = 100 | Prevents ThreadPool starvation | Unlimited emission |
