# HIL Sprint 2: TraceDrivenChannel & CLI Runner

**Date**: 2026-07-29
**Status**: Draft
**Depends**: [Sprint 1 design](2026-07-29-hil-sprint1-design.md) (complete)
**Scope**: Virtual CAN channel + headless test execution

---

## 1. Goal

Enable **offline HIL testing** without real PCAN hardware:

1. **TraceDrivenChannel** — Replays ASC/BLF trace files as a virtual CAN channel, implementing `ICanChannel`
2. **CLI Runner** — Headless console mode that loads DBC + test suite JSON, executes via `TestSuiteEngine`, outputs TRX/JUnit results

---

## 2. Architecture

```
┌─────────────────────────────────────────────────────────────┐
│  CLI Program (PeakCan.Host.Cli)                             │
│  ┌─────────────┐  ┌──────────────┐  ┌───────────────────┐  │
│  │ HeadlessHost│→ │TestSuiteEngine│→ │ConsoleProgress    │  │
│  │ Builder     │  │ (Sprint 1)   │  │ (IProgress<>)     │  │
│  └──────┬──────┘  └──────┬───────┘  └───────────────────┘  │
│         │                │                                   │
│  ┌──────┴────────────────┴───────────────────────────────┐  │
│  │ IAssertionContext (HILAssertionContext)               │  │
│  │  - SubscribeDecodedFrames → ChannelRouter → DBC decode │  │
│  │  - GetSignalValue → signal cache                      │  │
│  │  - SendFrameAsync → ICanChannel.WriteAsync            │  │
│  └──────────────────────┬───────────────────────────────┘  │
│                         │                                   │
│  ┌──────────────────────┴───────────────────────────────┐  │
│  │ ICanChannel (TraceDrivenChannel)                     │  │
│  │  - Replays ASC/BLF via Timer-based frame emission    │  │
│  │  - FrameReceived event fires on timer tick           │  │
│  │  - WriteAsync → optional stimulus callback           │  │
│  └─────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

---

## 3. TraceDrivenChannel

### 3.1 File: `Infrastructure/Channel/TraceDrivenChannel.cs`

```csharp
public sealed class TraceDrivenChannel : ICanChannel
{
    private readonly ChannelId _id;
    private readonly ILogger<TraceDrivenChannel>? _logger;
    private readonly ReplayTimeline _timeline;  // Existing Core replay engine
    private readonly ChannelRouter _router;     // Fans frames to sinks

    public ChannelId Id => _id;
    public bool IsConnected { get; private set; }

    // Events
    public event Action<CanFrame>? FrameReceived;
    public event Action<ReadLoopError>? ReadLoopError;

    // Load trace file
    public async Task LoadAsync(string path, CancellationToken ct = default);

    // ICanChannel implementation
    public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default);
    public Task DisconnectAsync(CancellationToken ct = default);
    public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default);
    ValueTask.DisposeAsync();
}
```

### 3.2 Frame Emission Model

```
TraceDrivenChannel
  │
  ├─ LoadAsync(path)
  │    └─ AscParser.ParseAsync(file) → IReadOnlyList<ReplayFrame>
  │    └─ _timeline.SetFrames(frames)
  │
  ├─ ConnectAsync() → _timeline.Play()
  │    └─ Internal Timer (1ms period):
  │         OnTick():
  │           elapsed_wall = (DateTime.UtcNow - _playStart).TotalSeconds * _speed
  │           target_ts = _playStartTimestamp + elapsed_wall
  │           while (frames[i].Timestamp <= target_ts):
  │             var cf = ToCanFrame(frames[i])
  │             FrameReceived?.Invoke(cf)  ← fires event
  │             i++
  │
  └─ WriteAsync(frame) → _router.OnChannelFrame(frame)
       (optional: store for stimulus-response correlation)
```

### 3.3 Key Design Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Reuse ReplayTimeline | ✅ Yes | Existing, tested timing engine |
| WriteAsync behavior | Route to ChannelRouter | Enables stimulus-response HIL loops |
| Speed control | 1.0x default, configurable | Match real-time for deterministic tests |
| Loop mode | Off by default | Tests run once; loop is manual |
| CanIdFilter | Optional | Focus test on specific ECU |

### 3.4 Frame Conversion

```csharp
private static CanFrame ToCanFrame(ReplayFrame frame, ChannelId channelId) => new(
    new CanId(frame.Id, frame.Flags.HasFlag(FrameFlags.Extended) ? FrameFormat.Extended : FrameFormat.Standard),
    frame.Data,
    frame.Flags,
    channelId,
    frame.Timestamp * 1_000_000); // seconds → microseconds
```

---

## 4. HILAssertionContext

### 4.1 File: `Infrastructure/HIL/HILAssertionContext.cs`

```csharp
internal sealed class HILAssertionContext : IAssertionContext, IDisposable
{
    private readonly ICanChannel _channel;
    private readonly IDbcLookup _dbcLookup;
    private readonly ChannelRouter _router;
    private readonly ConcurrentDictionary<string, double> _signalCache = new();

    public HILAssertionContext(ICanChannel channel, IDbcLookup dbcLookup, ChannelRouter router);

    public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame);
    public double? GetSignalValue(string signalName);
    public double CurrentTimestamp { get; }
    public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct);
}
```

### 4.2 Signal Decoding

```
FrameReceived event
  → ChannelRouter.OnChannelFrame(frame)
    → IFrameSink.OnFrame(frame)  [HILAssertionContext is a sink]
      → SignalDecoder.Decode(frame.Data, message.Signals)
        → _signalCache["MessageName.SignalName"] = physicalValue
        → onFrame(new DecodedFrame(frame, signals))
```

### 4.3 Thread Safety

- `SubscribeDecodedFrames` returns `IDisposable` (unsubscribe on Dispose)
- `_signalCache` is `ConcurrentDictionary<string, double>`
- Callback fires on the channel's frame thread (ThreadPool)

---

## 5. CLI Runner

### 5.1 Entry Point: `PeakCan.Host.Cli/Program.cs`

```csharp
// CLI syntax:
//   peakcan-hil --dbc path.dbc --suite tests.json [--trace path.asc] [--output results.trx]

var args = ParseArgs(args);
var host = HeadlessHostBuilder.Build(args);
var engine = host.Services.GetRequiredService<TestSuiteEngine>();
var suite = await LoadTestSuiteAsync(args.Suite);
var channel = host.Services.GetRequiredService<ICanChannel>();

if (args.Trace is { } tracePath)
    await ((TraceDrivenChannel)channel).LoadAsync(tracePath);

await channel.ConnectAsync(BaudRate.Baud500K, fd: true);

var result = await engine.ExecuteAsync(suite, host.Services.GetRequiredionContext(),
    new TestSuiteConfig(), new ConsoleProgress(), default);

await WriteResultsAsync(result, args.Output);
```

### 5.2 HeadlessHostBuilder

```csharp
public static class HeadlessHostBuilder
{
    public static IHost Build(CliArgs args)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                // Core
                services.AddSingleton<IChannelFactory, TraceChannelFactory>();
                services.AddSingleton<ICanChannel, TraceDrivenChannel>();
                services.AddSingleton<IFrameSource, ChannelRouter>();
                services.AddSingleton<IDbcLookup>(sp => new HeadlessDbcLookup(args.DbcPath));

                // HIL
                services.AddSingleton<IAssertionContext, HILAssertionContext>();

                // Engine
                services.AddSingleton<IStepExecutor>(sp => new WaitForSignalStepExecutor(...));
                // ... all Sprint 1 executors
                services.AddSingleton<TestSuiteEngine>();
            })
            .UseSerilog((ctx, cfg) => cfg.WriteTo.Console())
            .Build();
    }
}
```

### 5.3 Output Formats

| Format | Use Case |
|---|---|
| TRX | Azure DevOps native |
| JUnit XML | Jenkins/GitLab |
| Console (ANSI) | Human-readable |

---

## 6. File Structure

```
PeakCan.Host.Cli/                    ← New project (Exe)
  Program.cs
  CliArgs.cs
  ConsoleProgress.cs
  ResultWriter.cs

PeakCan.Host.Infrastructure/
  Channel/
    TraceDrivenChannel.cs            ← New
  HIL/
    HILAssertionContext.cs           ← New
    HeadlessDbcLookup.cs             ← New
    HeadlessHostBuilder.cs           ← New

tests/PeakCan.Host.Infrastructure.Tests/
  TraceDrivenChannelTests.cs         ← New
  HILAssertionContextTests.cs        ← New
```

---

## 7. TDD Increments

| Increment | Component | Tests |
|---|---|---|
| Inc 1 | TraceDrivenChannel (load + connect + frame emission) | Load ASC, Connect fires frames, FrameReceived timing |
| Inc 2 | HILAssertionContext (subscribe + decode + signal cache) | Subscribe receives decoded frames, GetSignalValue returns physical value |
| Inc 3 | CLI Runner (headless host + JSON load + execution) | Load suite JSON, Execute produces TestSuiteResult, TRX output |
| Inc 4 | Stimulus-response (WriteAsync → frame injection) | WriteAsync routes to router, Round-trip: send → receive → assert |

---

## 8. Risks

| Risk | Mitigation |
|---|---|
| Timer drift on slow machines | Use `Stopwatch` for monotonic timing, not `DateTime.UtcNow` |
| ASC files with >1M frames | Stream-based parsing (don't load all into memory) |
| DBC not loaded for signal names | Fail fast at startup with clear error message |
| Thread affinity (CLI has no SynchronizationContext) | Use `ConfigureAwait(false)` everywhere |

---

## 9. Relationship to Phased Gap Analysis

| Gap ID | Covered In |
|---|---|
| 1.1 CLI Runner | Section 5 |
| 1.7a TraceDrivenChannel | Section 3 |
| 2.x AssertionPrimitives async tests | Fixed by HILAssertionContext (Section 4) |
