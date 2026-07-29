# HIL Sprint 2: TraceDrivenChannel & CLI Runner

**Date**: 2026-07-29
**Status**: Draft v10 (incorporates 9th round review)
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

### 2.2 HILAssertionContext Subscribes to FrameReceived Event

Subscribe in constructor, unsubscribe in Dispose.

### 2.3 WriteAsync Is No-Op (Sprint 2)

Without a physical bus, sent frames have no destination. Stimulus-response deferred to Phase 3.

### 2.4 Own Timer Logic

`TraceDrivenChannel` contains its own timer (ReplayTimeline is internal to Core).

### 2.5 Signal Cache with Timestamps + DropOldest Channel

Reuse Sprint 1's `Channel<T>` + consumer thread model.

### 2.6 Keep Sprint 1 TestSuiteEngine Constructor

`TestSuiteEngine` keeps Sprint 1 constructor.

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
    private long _state;  // 0=Idle, 1=CallbackInProgress, 2=Disposing
    private DateTime _playStartWallClock;
    private double _playStartTimestamp = -1;
    private double _speed = 1.0;
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

### 3.2 Frame Emission Model (fixes L1, L2)

```
OnTick(state)  [ThreadPool timer callback]
  if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;
  [CAS: only enter if state was Idle(0), atomically set to CallbackInProgress(1)]
  [if state was Disposing(2), CAS fails -> return immediately]

  elapsed_wall = (DateTime.UtcNow - _playStartWallClock).TotalSeconds * _speed
  if (elapsed_wall < 0):  [NTP clock jump backward detected]
      _playStartWallClock = DateTime.UtcNow - TimeSpan.FromSeconds(_playStartTimestamp / _speed)
      elapsed_wall = 0
  target_ts = _playStartTimestamp + elapsed_wall

  lock(_framesLock):
      _emitBuffer.Clear()
      emitted = 0
      while (_nextFrameIndex < _frames.Count
            && _frames[_nextFrameIndex].Timestamp <= target_ts
            && emitted < MaxFramesPerTick):
          _emitBuffer.Add(ToCanFrame(_frames[_nextFrameIndex]))
          _nextFrameIndex++
          emitted++

  bufferCopy = _emitBuffer.ToList()  [copy under lock, shallow]

  foreach (frame in bufferCopy):  [outside lock, state still CallbackInProgress]
      FrameReceived?.Invoke(frame)

  if (_nextFrameIndex >= _frames.Count):
      _timer.Change(Timeout.Infinite, Timeout.Infinite)

  Interlocked.CompareExchange(ref _state, 0, 1)
  [CAS: only set Idle(0) if state is still CallbackInProgress(1)]

DisposeAsync()
  SpinWait.SpinUntil(() => Interlocked.Read(ref _state) != 1, 200ms)
  [wait until OnTick exits CallbackInProgress]
  Interlocked.CompareExchange(ref _state, 2, 0)  [only set Disposing if Idle]
  _timer?.Dispose()
  SpinWait.SpinUntil(() => Interlocked.Read(ref _state) != 1, 200ms)
  [final wait for any in-flight OnTick]
  Interlocked.Exchange(ref _state, 2)  [force Disposing even if OnTick set Idle(0) during wait — L1 fix]
  State -> Disposed
```

State machine (CAS preferred; Exchange only as timeout fallback):
- 0 = Idle: No callback running
- 1 = CallbackInProgress: OnTick executing (including FrameReceived invokes)
- 2 = Disposing: DisposeAsync running

TOCTOU fix: OnTick atomically CAS Idle->CallbackInProgress. If state is Disposing, CAS fails and OnTick returns. FrameReceived invokes happen while state=CallbackInProgress. DisposeAsync waits for state != CallbackInProgress, then force-sets Disposing via Exchange as timeout fallback (handles the race where OnTick completes and resets to Idle between the CAS and the final wait).

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

### 3.4 DBC Lookup Key Conversion (fixes L3)

```csharp
// DBC Message.Id stores extended IDs with bit 31 set (e.g., 0x98FEF100)
// CanFrame.Id.Raw stores the raw ID without bit 31 (e.g., 0x18FEF100)
static uint ToDbcLookupKey(uint rawId, bool isExtended) =>
    isExtended ? rawId | 0x80000000u : rawId;
```

### 3.5 State Machine

```
States: Unloaded -> Loaded -> Playing -> Ended
                  ^                  |
                  +--- Restart ----+
                  Any -> Disposed

Illegal:
  ConnectAsync on Unloaded -> InvalidOperationException
  ConnectAsync on Loaded with _playStartTimestamp < 0 -> InvalidOperationException
  ConnectAsync on Disposed -> ObjectDisposedException
  LoadAscii on Playing -> InvalidOperationException
```

### 3.6 Configuration

| Parameter | Source | Default |
|---|---|---|
| MaxFramesPerTick | Constructor arg | 100 |
| MaxTraceFrames | Constructor arg | 2_000_000 |
| Speed | Property | 1.0 |

---

## 4. HILAssertionContext

### 4.1 File: `Infrastructure/HIL/HILAssertionContext.cs`

```csharp
internal sealed class HILAssertionContext : IAssertionContext, IDisposable
{
    private readonly ICanChannel _channel;
    private readonly IDbcLookup _dbcLookup;
    private readonly Channel<CanFrame> _frameChannel;
    private readonly CancellationTokenSource _consumerCts = new();
    private readonly Task _consumerTask;
    private readonly ConcurrentDictionary<string, (double Value, double TimestampUs)> _signalCache = new();
    private volatile double _currentTimestamp;
    private readonly IDisposable _frameSubscription;
    private ImmutableList<Action<DecodedFrame>> _subscribers = ImmutableList<Action<DecodedFrame>>.Empty;

    public HILAssertionContext(ICanChannel channel, IDbcLookup dbcLookup)
    {
        _channel = channel;
        _dbcLookup = dbcLookup;
        _frameChannel = Channel.CreateBounded<CanFrame>(
            new BoundedChannelOptions(10000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = true,
                SingleReader = true,
            });
        _frameSubscription = new FrameReceivedSubscription(channel, OnFrame);
        _consumerTask = Task.Run(() => ConsumerLoop(_consumerCts.Token));
    }

    public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame)
    {
        ImmutableList.Interlocked.Add(ref _subscribers, onFrame);
        return new SubscriberSubscription(() => ImmutableList.Interlocked.Remove(ref _subscribers, onFrame));
    }

    public double? GetSignalValue(string signalName, int maxAgeMs = 5000);
    public double CurrentTimestamp => _currentTimestamp;
    public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct);
    public void Dispose();
}

internal sealed class SubscriberSubscription : IDisposable
{
    private Action? _dispose;
    public SubscriberSubscription(Action dispose) => _dispose = dispose;
    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}
```

### 4.2 Event Subscription Adapter

```csharp
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

### 4.3 Thread Model (fixes L2, B1, B2)

```
OnFrame(frame)  [channel frame thread]
  _currentTimestamp = frame.Timestamp.TotalMicroseconds
  _frameChannel.Writer.TryWrite(frame)
  [DropOldest: if full, removes oldest, writes new, always returns true]
  Return immediately

ConsumerLoop:
  await foreach (frame in _frameChannel.Reader.ReadAllAsync(ct))
    key = ToDbcLookupKey(frame.Id.Raw, frame.Id.IsExtended)
    message = _dbcLookup.FindMessage(key)
    if (message is not null):
        var signals = new Dictionary<string, double>();
        foreach (signal in message.Signals):
            var signalName = $"{message.Name}.{signal.Name}";
            var value = SignalDecoder.Decode(frame.Data.Span, signal);
            signals[signalName] = value;
            _signalCache[signalName] = (value, _currentTimestamp);
        var decoded = new DecodedFrame(frame, signals);
    else:
        var decoded = new DecodedFrame(frame, new Dictionary<string, double>());
    ImmutableList<Action<DecodedFrame>> subscribers = Volatile.Read(ref _subscribers);
    foreach (subscriber in subscribers):
        try { subscriber(decoded); }
        catch (Exception ex) { log, isolate per subscriber }

Dispose:
  _frameSubscription.Dispose()
  SpinWait.SpinUntil(() => _frameChannel.Reader.Count == 0, 100ms)
  _consumerCts.Cancel()
  try { await _consumerTask.WaitAsync(TimeSpan.FromSeconds(2)); }
  catch (OperationCanceledException) { expected on Cancel }
  _frameChannel.Writer.Complete()
```

### 4.4 Signal Name Format (fixes T1)

Format: "MessageName.SignalName". The **last** dot is the separator.

Known limitation: Signal names containing dots may be incorrectly split, producing wrong physical values (silent data corruption, not null).

Mitigation: Use DBC files where signal names don't contain dots (automotive convention). For DBCs with dots in signal names, the user must manually verify decoded values against expected ranges.

### 4.5 Timestamp + Staleness (fixes T2)

```csharp
public double? GetSignalValue(string signalName, int maxAgeMs = 5000)
{
    if (!_signalCache.TryGetValue(signalName, out var entry)) return null;
    if (maxAgeMs > 0)
    {
        var ageUs = _currentTimestamp - entry.TimestampUs;
        if (ageUs > maxAgeMs * 1000.0) return null;
    }
    return entry.Value;
}
```

Interaction with WaitForSignal: Effective wait window = min(test timeout, time until signal age exceeds 5s). For signals with period > 5s, increase maxAgeMs or use a shorter signal period.

---

## 5. Sprint 1 Interface Update

Sprint 1 original: double? GetSignalValue(string signalName);
Sprint 2 updated: double? GetSignalValue(string signalName, int maxAgeMs = 5000);

| File | Change |
|---|---|
| Contracts/IAssertionContext.cs | Add maxAgeMs + update xmldoc |
| Tests/.../Fakes/FakeAssertionContext.cs | Implement new signature |
| Tests/.../Assertions/AssertionPrimitivesTests.cs | Unchanged |
| Assertions/AssertionPrimitives.cs | Update call |

---

## 6. CLI Runner

CLI syntax: `peakcan-hil --dbc path.dbc --trace path.asc --suite tests.json [--output results.trx] [--format trx]`

### 6.1 HeadlessHostBuilder (DI registration)

**File**: `PeakCan.Host.Cli/HeadlessHostBuilder.cs` (T1 fix — explicit project location)

**L2 fix — InternalsVisibleTo**: Core's `AssemblyInfo.cs` currently exposes internals only to `PeakCan.Host`, `PeakCan.Host.Core.Tests`, `PeakCan.Host.App.Tests`. Must add:

```csharp
[assembly: InternalsVisibleTo("PeakCan.Host.Infrastructure")]
[assembly: InternalsVisibleTo("PeakCan.Host.Cli")]
```

This enables Cli to reference the 6 `internal` executor classes in `PeakCan.Host.Core.HIL.StepExecutor.*`.

**D1 fix — return type**: Use `Host.CreateApplicationBuilder()` (returns `IHost`), not bare `ServiceCollection.BuildServiceProvider()` (returns `ServiceProvider`).

**L1 fix — no CommentStepExecutor**: `TestSuiteEngine` handles `Comment` kind inline (never looks up an executor). Do NOT register a `CommentStepExecutor` — the class doesn't exist.

**B2 fix — register all 6 existing executors**: Sprint 2 supports the step kinds that have executors. `WaitForFrame`, `AssertDtc`, `AssertNrc`, `AssertResponseTime` have no executor yet — TestSuiteEngine marks them Failed with "No executor for kind".

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.StepExecutor;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.HIL;

public static class HeadlessHostBuilder
{
    public static IHost Build(CliArgs args)
    {
        var builder = Host.CreateApplicationBuilder(args: [args.DbcPath, args.TracePath, args.SuitePath]);

        // Channel (TraceDrivenChannel loads ASC via LoadAscii)
        builder.Services.AddSingleton<ICanChannel>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<TraceDrivenChannel>>();
            var ch = new TraceDrivenChannel(new ChannelId(1), logger);
            ch.LoadAscii(args.TracePath);
            return ch;
        });

        // DBC lookup
        builder.Services.AddSingleton<IDbcLookup>(sp =>
        {
            var text = File.ReadAllText(args.DbcPath);
            var doc = DbcParser.Parse(text);
            if (!doc.IsSuccess) throw new InvalidOperationException($"DBC parse failed: {doc.Error?.Message}");
            return new HeadlessDbcLookup(doc.Value!);
        });

        // Assertion context
        builder.Services.AddSingleton<IAssertionContext>(sp =>
        {
            var channel = sp.GetRequiredService<ICanChannel>();
            var dbc = sp.GetRequiredService<IDbcLookup>();
            return new HILAssertionContext(channel, dbc);
        });

        // Fixture resolver (no-op for headless)
        builder.Services.AddSingleton<IFixtureResolver, HeadlessFixtureResolver>();

        // Assertion primitives (shared singleton)
        builder.Services.AddSingleton<AssertionPrimitives>();

        // Step executors (6 existing internal classes — L1/L2 fix)
        builder.Services.AddSingleton<IStepExecutor, SendFrameStepExecutor>();
        builder.Services.AddSingleton<IStepExecutor, SendSequenceStepExecutor>;
        builder.Services.AddSingleton<IStepExecutor, AssertSignalStepExecutor>();
        builder.Services.AddSingleton<IStepExecutor, AssertRangeStepExecutor>();
        builder.Services.AddSingleton<IStepExecutor, WaitForSignalStepExecutor>();
        builder.Services.AddSingleton<IStepExecutor, DelayStepExecutor>();

        // Engine
        builder.Services.AddSingleton<TestSuiteEngine>();

        // Logging
        builder.Logging.AddSerilog(new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File("hil.log")
            .CreateLogger());

        return builder.Build();
    }
}
```

### 6.2 HeadlessFixtureResolver

```csharp
internal sealed class HeadlessFixtureResolver : IFixtureResolver
{
    private static readonly ITestFixture NoOp = new NoOpTestFixture();
    public ITestFixture Resolve(string key) => NoOp;
}

internal sealed class NoOpTestFixture : ITestFixture
{
    public Task SetupAsync(IAssertionContext ctx, CancellationToken ct) => Task.CompletedTask;
    public Task TeardownAsync(IAssertionContext ctx, CancellationToken ct) => Task.CompletedTask;
}
```

### 6.3 HeadlessDbcLookup

```csharp
internal sealed class HeadlessDbcLookup : IDbcLookup
{
    private readonly Dictionary<uint, Message> _messages;

    public HeadlessDbcLookup(DbcDocument doc)
    {
        _messages = new Dictionary<uint, Message>();
        foreach (var msg in doc.Messages)
        {
            // B1 fix: msg.Id already carries bit 31 for extended frames (D17).
            // No ToDbcLookupKey needed at build time — direct key is correct.
            // ToDbcLookupKey is only used at lookup time (ConsumerLoop) to
            // convert CanFrame.Id.Raw (no bit 31) → DBC key format.
            _messages[msg.Id] = msg;
        }
    }

    public Message? FindMessage(uint canId)
        => _messages.GetValueOrDefault(canId);
}
```

### 6.4 ConsoleProgress

TestProgress properties: `CompletedCases`, `TotalCases`, `CurrentCaseName`, `Message` (constructor args), `PercentComplete` (computed: `CompletedCases / TotalCases * 100`).

```csharp
internal sealed class ConsoleProgress : IProgress<TestProgress>
{
    public void Report(TestProgress value)
    {
        var color = value.PercentComplete switch
        {
            >= 100 => ConsoleColor.Green,
            > 0 => ConsoleColor.Yellow,
            _ => ConsoleColor.Gray,
        };
        Console.ForegroundColor = color;
        Console.Write($"[{value.CompletedCases}/{value.TotalCases}] ");
        Console.ResetColor();
        Console.WriteLine(value.CurrentCaseName ?? value.Message ?? "running");
    }
}
```

### 6.5 Output Formats

| Format | Flag | Implementation |
|---|---|---|
| Console ANSI | default | ConsoleProgress |
| TRX | `--format trx` | ResultWriter.WriteTrx() |
| JUnit XML | Sprint 3 | — |

TRX schema (minimal):
```xml
<TestRun>
  <Results>
    <UnitTestResult testName="case_1" outcome="Passed|Failed" duration="00:00:01" />
  </Results>
  <TestDefinitions>
    <UnitTest name="case_1" />
  </TestDefinitions>
</TestRun>
```

---

## 7. File Structure

PeakCan.Host.Cli/ (NEW project, Exe, net10.0): Program.cs, CliArgs.cs, ConsoleProgress.cs, ResultWriter.cs, HeadlessHostBuilder.cs
PeakCan.Host.Infrastructure/Channel/: TraceDrivenChannel.cs (NEW)
PeakCan.Host.Infrastructure/HIL/: HILAssertionContext.cs (NEW), FrameReceivedSubscription.cs (NEW),
  HeadlessDbcLookup.cs (NEW), HeadlessFixtureResolver.cs (NEW)
Sprint 1 modifications: IAssertionContext.cs, FakeAssertionContext.cs, AssertionPrimitives.cs
tests/PeakCan.Host.Infrastructure.Tests/: TraceDrivenChannelTests.cs (NEW), HILAssertionContextTests.cs (NEW)

---

## 8. TDD Increments

| Increment | Component | Tests |
|---|---|---|
| Inc 1 | TraceDrivenChannel | Load ASCII, Connect fires frames, State machine, Empty trace, Frame conversion |
| Inc 2 | HILAssertionContext + IAssertionContext update | Subscribe receives decoded frames, Staleness (5s), Extended frame DBC lookup, Backward compat |
| Inc 3 | CLI Runner + HeadlessHostBuilder | Load suite JSON, Execute (6 step kinds), Console output, TRX output |
| Inc 4 | Integration | Load DBC + trace + suite -> full execution |

---

## 9. Risks

NTP clock jump backward: Reset _playStartWallClock (playback pauses until catch-up)
ASC files with >2M frames: MaxTraceFrames guard
DBC not loaded: Fail fast
ThreadPool starvation: MaxFramesPerTick = 100, DropOldest
FrameReceived during Dispose: Interlocked CAS state machine

---

## 10. Design Decision Record

D1: No ChannelRouter (OnChannelFrame private)
D2: Direct FrameReceived subscription (single-source model)
D3: WriteAsync no-op (no physical bus)
D4: Own timer logic (ReplayTimeline internal)
D5: Signal cache + DropOldest (prevents stale reads)
D6: Last-dot separator (documented limitation)
D7: maxAgeMs = 5000ms (low-frequency signals)
D8: Keep Sprint 1 Engine constructor (interface-based DI)
D9: Backward-compatible interface (default parameter)
D10: Bounded frame cache (MaxTraceFrames guard)
D11: Explicit --trace arg (required for testing)
D12: Infer extended from ID > 0x7FF (no FrameFlags.Extended)
D13: DBC decode on consumer thread (fast timer callback)
D14: Interlocked CAS preferred; Exchange only as Dispose timeout fallback (L1 fix)
D15: Reusable emit buffer (no per-tick allocation)
D16: Shared AssertionPrimitives (one instance)
D17: ToDbcLookupKey conversion (bit 31 mismatch)
D18: DropOldest + TryWrite (always succeeds)
D19: NTP reset _playStartWallClock (pauses until catch-up)
D20: ImmutableList for subscribers (thread-safe enumeration)
D21: InternalsVisibleTo("PeakCan.Host.Infrastructure" | "PeakCan.Host.Cli") for executor access
D22: Host.CreateApplicationBuilder() for CLI (returns IHost, not ServiceProvider)
D23: Direct msg.Id key in HeadlessDbcLookup (bit 31 already set)
D24: Sprint 2 supports 6 step kinds (WaitForFrame/AssertDtc/AssertNrc/AssertResponseTime deferred)
