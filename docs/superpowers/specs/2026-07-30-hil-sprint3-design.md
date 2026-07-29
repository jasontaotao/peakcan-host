# HIL Sprint 3: Output Formats, UDS Assertions & Frame Arrival

**Date**: 2026-07-30
**Status**: Draft v0 (initial draft, pending review)
**Depends**: [Sprint 2 design](2026-07-29-hil-sprint2-design.md) (complete)
**Scope**: JUnit XML output, WaitForFrame/AssertDtc/AssertNrc/AssertResponseTime executors, BLF support, WriteAsync loopback

---

## 1. Goal

Complete the HIL testing pipeline with:

1. **JUnit XML output** — CI/CD friendly test reporting
2. **UDS assertion executors** — AssertDtc, AssertNrc, AssertResponseTime for diagnostic testing
3. **WaitForFrame executor** — detect frame arrival/departure on the bus
4. **BLF file support** — replay Binary Vector CAN Log files alongside ASC
5. **WriteAsync loopback** — stimulus-response testing without physical ECU (Sprint 2 WriteAsync was no-op)

---

## 2. Key Architecture Decisions

### 2.1 JUnit XML Schema

Use standard JUnit XML format (compatible with Jenkins, Azure DevOps, GitLab):

```xml
<testsuites>
  <testsuite name="IntegrationSuite" tests="2" failures="1" time="1.500">
    <testcase name="case_1" classname="IntegrationSuite" time="0.500"/>
    <testcase name="case_2" classname="IntegrationSuite" time="1.000">
      <failure message="Step 0 failed: signal RPM out of tolerance">...</failure>
    </testcase>
  </testsuite>
</testsuites>
```

### 2.2 UDS Assertion Executors

Reuse existing `UdsClient` / `IsoTpLayer` from the App layer. Executors call UDS services via a new `IUdsSession` interface (decouples from App-layer DI).

| Executor | Kind | UDS Service | Behavior |
|---|---|---|---|
| `AssertDtcStepExecutor` | `AssertDtc` | ReadDTCInformation (0x19) | Assert DTC count/status matches expected |
| `AssertNrcStepExecutor` | `AssertNrc` | Any (capture NRC) | Assert last NRC matches expected |
| `AssertResponseTimeStepExecutor` | `AssertResponseTime` | Any | Assert response time < timeout |

### 2.3 WaitForFrame Executor

Subscribe to `HILAssertionContext.SubscribeDecodedFrames`, wait for a frame matching specified CAN ID (and optional data pattern) within timeout.

```csharp
public record WaitForFrameStep(uint CanId, byte[]? DataPattern, int TimeoutMs)
    : StepParameters(TestCaseStepKind.WaitForFrame);
```

### 2.4 BLF File Support

Add `LoadBlf(string path)` to `TraceDrivenChannel`. Use `BlfParser` (existing in Core) to parse BLF files into `ReplayFrame` list, then same playback pipeline as ASC.

### 2.5 WriteAsync Loopback

For stimulus-response testing without physical ECU:
- `TraceDrivenChannel.WriteAsync` writes to a loopback queue
- `HILAssertionContext` reads from the loopback queue and raises `FrameReceived`
- This simulates ECU response to stimulus frames

---

## 3. New Step Parameters

### 3.1 WaitForFrameStep

```csharp
public record WaitForFrameStep(
    uint CanId,
    byte[]? DataPattern,
    int TimeoutMs)
    : StepParameters(TestCaseStepKind.WaitForFrame);
```

### 3.2 AssertDtcStep

```csharp
public record AssertDtcStep(
    int ExpectedDtcCount,
    byte[]? StatusMask = null)
    : StepParameters(TestCaseStepKind.AssertDtc);
```

### 3.3 AssertNrcStep

```csharp
public record AssertNrcStep(
    byte ExpectedNrc)
    : StepParameters(TestCaseStepKind.AssertNrc);
```

### 3.4 AssertResponseTimeStep

```csharp
public record AssertResponseTimeStep(
    int MaxResponseTimeMs)
    : StepParameters(TestCaseStepKind.AssertResponseTime);
```

---

## 4. JUnit XML Writer

### 4.1 File: `PeakCan.Host.Cli/JUnitWriter.cs`

```csharp
public static class JUnitWriter
{
    public static async Task WriteJunit(TestSuiteResult result, string path)
    {
        var ns = XNamespace.Get("http://junit.org/junit4/extensions");
        var doc = new XDocument(
            new XElement("testsuites",
                new XElement("testsuite",
                    new XAttribute("name", result.SuiteName),
                    new XAttribute("tests", result.TotalCases),
                    new XAttribute("failures", result.FailedCases),
                    new XAttribute("time", $"{result.ElapsedMs / 1000.0:F3}"),
                    result.CaseResults.Select(cr =>
                        new XElement("testcase",
                            new XAttribute("name", cr.TestCaseName),
                            new XAttribute("classname", result.SuiteName),
                            new XAttribute("time", $"{cr.ElapsedMs / 1000.0:F3}"),
                            cr.Passed ? null : new XElement("failure",
                                new XAttribute("message", cr.FailureReason),
                                string.Join("\n", cr.StepResults
                                    .Where(r => r.Status == StepStatus.Failed)
                                    .Select(r => $"Step {r.StepIndex}: {r.Message}")))))))));

        await using var stream = File.Create(path);
        await doc.SaveAsync(stream, SaveOptions.None, CancellationToken.None);
    }
}
```

### 4.2 Output Formats (updated)

| Format | Flag | Implementation |
|---|---|---|
| Console ANSI | default | ConsoleProgress |
| TRX | `--format trx` | ResultWriter.WriteTrx() |
| JUnit XML | `--format junit` | JUnitWriter.WriteJunit() |

---

## 5. WaitForFrame Executor

### 5.1 File: `Core/HIL/StepExecutor/WaitForFrameStepExecutor.cs`

```csharp
internal sealed class WaitForFrameStepExecutor : IStepExecutor
{
    public TestCaseStepKind Kind => TestCaseStepKind.WaitForFrame;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, Contracts.IAssertionContext ctx, CancellationToken ct)
    {
        var p = (WaitForFrameStep)step.Parameters;
        var tcs = new TaskCompletionSource<bool>();

        using var sub = ctx.SubscribeDecodedFrames(frame =>
        {
            if (frame.Frame.Id.Raw == p.CanId &&
                (p.DataPattern is null || frame.Frame.Data.Span.SequenceEqual(p.DataPattern)))
            {
                tcs.TrySetResult(true);
            }
        });

        var delayTask = Task.Delay(p.TimeoutMs, ct);
        var winner = await Task.WhenAny(tcs.Task, delayTask).ConfigureAwait(false);

        return winner == tcs.Task
            ? new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
                $"frame 0x{p.CanId:X} received", null, null, 0)
            : new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"timeout waiting for frame 0x{p.CanId:X}", null, null, 0);
    }
}
```

---

## 6. UDS Assertion Executors

### 6.1 New Interface: `IUdsSession`

```csharp
public interface IUdsSession
{
    Task<Result<IReadOnlyList<DtcInfo>>> ReadDtcInformation(byte statusMask, CancellationToken ct);
    Task<Result<Response>> SendRequestAsync(byte serviceId, byte[] data, CancellationToken ct);
    int LastResponseTimeMs { get; }
    byte? LastNrc { get; }
}
```

### 6.2 AssertDtcStepExecutor

```csharp
internal sealed class AssertDtcStepExecutor : IStepExecutor
{
    private readonly IUdsSession _uds;

    public AssertDtcStepExecutor(IUdsSession uds) => _uds = uds;
    public TestCaseStepKind Kind => TestCaseStepKind.AssertDtc;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, Contracts.IAssertionContext ctx, CancellationToken ct)
    {
        var p = (AssertDtcStep)step.Parameters;
        var result = await _uds.ReadDtcInformation(p.StatusMask ?? 0xFF, ct);

        if (!result.IsSuccess)
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"ReadDTC failed: {result.Error?.Message}", null, null, 0);

        var count = result.Value!.Count;
        return count == p.ExpectedDtcCount
            ? new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
                $"DTC count = {count}", count.ToString(), p.ExpectedDtcCount.ToString(), 0)
            : new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"DTC count {count} != expected {p.ExpectedDtcCount}",
                count.ToString(), p.ExpectedDtcCount.ToString(), 0);
    }
}
```

### 6.3 AssertNrcStepExecutor

```csharp
internal sealed class AssertNrcStepExecutor : IStepExecutor
{
    private readonly IUdsSession _uds;

    public AssertNrcStepExecutor(IUdsSession uds) => _uds = uds;
    public TestCaseStepKind Kind => TestCaseStepKind.AssertNrc;

    public Task<StepResult> ExecuteAsync(TestCaseStep step, Contracts.IAssertionContext ctx, CancellationToken ct)
    {
        var p = (AssertNrcStep)step.Parameters;
        var lastNrc = _uds.LastNrc;

        return Task.FromResult(lastNrc == p.ExpectedNrc
            ? new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
                $"NRC = 0x{lastNrc:X2}", null, null, 0)
            : new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"NRC 0x{lastNrc:X2} != expected 0x{p.ExpectedNrc:X2}",
                lastNrc?.ToString("X2"), p.ExpectedNrc.ToString("X2"), 0));
    }
}
```

### 6.4 AssertResponseTimeStepExecutor

```csharp
internal sealed class AssertResponseTimeStepExecutor : IStepExecutor
{
    private readonly IUdsSession _uds;

    public AssertResponseTimeStepExecutor(IUdsSession uds) => _uds = uds;
    public TestCaseStepKind Kind => TestCaseStepKind.AssertResponseTime;

    public Task<StepResult> ExecuteAsync(TestCaseStep step, Contracts.IAssertionContext ctx, CancellationToken ct)
    {
        var p = (AssertResponseTimeStep)step.Parameters;
        var elapsed = _uds.LastResponseTimeMs;

        return Task.FromResult(elapsed <= p.MaxResponseTimeMs
            ? new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
                $"response time {elapsed}ms <= {p.MaxResponseTimeMs}ms",
                elapsed.ToString(), p.MaxResponseTimeMs.ToString(), 0)
            : new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"response time {elapsed}ms > {p.MaxResponseTimeMs}ms",
                elapsed.ToString(), p.MaxResponseTimeMs.ToString(), 0));
    }
}
```

---

## 7. BLF File Support

### 7.1 File: `Infrastructure/Channel/TraceDrivenChannel.cs` (add method)

```csharp
public void LoadBlf(string path, CancellationToken ct = default)
{
    ObjectDisposedException.ThrowIf(_state == 2, this);
    if (IsConnected)
        throw new InvalidOperationException("Cannot load trace while playing.");

    if (!File.Exists(path))
        throw new FileNotFoundException("BLF trace file not found.", path);

    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    var frames = BlfParser.Parse(stream, ct).GetAwaiter().GetResult();

    if (frames.Count > _maxTraceFrames)
        throw new InvalidOperationException(
            $"Trace file has {frames.Count} frames, exceeds MaxTraceFrames={_maxTraceFrames}.");

    lock (_framesLock)
    {
        _frames.Clear();
        _frames.AddRange(frames);
        _nextFrameIndex = 0;
        _playStartTimestamp = frames.Count > 0 ? frames[0].Timestamp : -1;
    }
}
```

### 7.2 CLI Syntax (updated)

```
peakcan-hil --dbc path.dbc --trace path.asc|--trace path.blf --suite tests.json [--output results.trx|results.xml]
```

---

## 8. WriteAsync Loopback

### 8.1 File: `Infrastructure/Channel/TraceDrivenChannel.cs` (modify WriteAsync)

```csharp
private readonly Channel<CanFrame> _loopbackChannel = Channel.CreateBounded<CanFrame>(1000);

public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
{
    // Sprint 3: loopback mode — sent frames become received frames
    _loopbackChannel.Writer.TryWrite(frame);
    return ValueTask.FromResult(Result<Unit>.Ok(default));
}

// In OnTick, also process loopback frames:
private void ProcessLoopback()
{
    while (_loopbackChannel.Reader.TryRead(out var frame))
    {
        FrameReceived?.Invoke(frame);
    }
}
```

---

## 9. File Structure (new/modified)

```
PeakCan.Host.Cli/ (modified):
  JUnitWriter.cs (NEW)
  ResultWriter.cs (unchanged)
  Program.cs (add --format junit)

PeakCan.Host.Core/HIL/StepParams/ (NEW):
  WaitForFrameStep.cs
  AssertDtcStep.cs
  AssertNrcStep.cs
  AssertResponseTimeStep.cs

PeakCan.Host.Core/HIL/StepExecutor/ (NEW):
  WaitForFrameStepExecutor.cs
  AssertDtcStepExecutor.cs
  AssertNrcStepExecutor.cs
  AssertResponseTimeStepExecutor.cs

PeakCan.Host.Core/HIL/Contracts/ (NEW):
  IUdsSession.cs
  DtcInfo.cs

PeakCan.Host.Infrastructure/Channel/ (modified):
  TraceDrivenChannel.cs (LoadBlf + WriteAsync loopback)

tests/PeakCan.Host.Cli.Tests/ (modified):
  JUnitWriterTests.cs (NEW)

tests/PeakCan.Host.Core.Tests/HIL/StepExecutor/ (NEW):
  WaitForFrameStepExecutorTests.cs
  AssertDtcStepExecutorTests.cs
  AssertNrcStepExecutorTests.cs
  AssertResponseTimeStepExecutorTests.cs

tests/PeakCan.Host.Infrastructure.Tests/ (modified):
  TraceDrivenChannelTests.cs (add BLF + loopback tests)
```

---

## 10. TDD Increments

| Increment | Component | Tests |
|---|---|---|
| Inc 1 | JUnit XML Writer | Valid XML, passed/failed cases, empty suite |
| Inc 2 | WaitForFrame Executor | Frame received, timeout, data pattern match |
| Inc 3 | UDS Assertion Executors | AssertDtc (count match/fail), AssertNrc (match/fail), AssertResponseTime (within/exceed) |
| Inc 4 | BLF Support | Load BLF file, playback frames, mixed ASC/BLF |
| Inc 5 | WriteAsync Loopback | Write frame → receive frame, stimulus-response cycle |
| Inc 6 | Integration | Full pipeline with UDS mock session |

---

## 11. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| UdsClient dependency on App-layer DI | UDS executors can't use App-layer singletons | Introduce `IUdsSession` interface, inject mock in tests |
| BLF parser API mismatch | Inc 4 blocked | Pre-flight verification against existing BlfParser |
| WriteAsync loopback race | Frame emitted before subscriber ready | Use bounded channel + ProcessLoopback in OnTick |
| JUnit XML schema variation | CI system may expect specific attributes | Follow Jenkins/JUnit4 schema (most compatible) |

---

## 12. Design Decision Record

D1: JUnit XML over TRX for CI compatibility
D2: WaitForFrame uses existing SubscribeDecodedFrames (no new subscription)
D3: UDS executors depend on IUdsSession (not concrete UdsClient)
D4: BLF uses existing BlfParser (no new parser)
D5: WriteAsync loopback via bounded channel (thread-safe)
D6: UDS session lifecycle managed by HeadlessHostBuilder (not executors)

---

## 13. Definition of Done

- [ ] Inc 1: JUnitWriter tests pass (4 tests)
- [ ] Inc 2: WaitForFrameStepExecutor tests pass (3 tests)
- [ ] Inc 3: UDS assertion executor tests pass (6 tests, using mock IUdsSession)
- [ ] Inc 4: BLF LoadBlf tests pass (3 tests)
- [ ] Inc 5: WriteAsync loopback tests pass (2 tests)
- [ ] Inc 6: Integration test with mock UDS session (2 tests)
- [ ] `dotnet build` entire solution succeeds
- [ ] `dotnet test` all tests pass (Sprint 1 + Sprint 2 + Sprint 3)
- [ ] CLI `--format junit` produces valid JUnit XML
- [ ] CLI `--trace file.blf` replays BLF files
