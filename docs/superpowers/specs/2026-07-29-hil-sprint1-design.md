# HIL Sprint 1: Domain Model & Orchestration Skeleton

**Date**: 2026-07-29
**Status**: Draft
**Scope**: Phase 1 Sprint 1 (Pure domain, zero hardware dependency)

---

## 1. Goal

Define the HIL testing domain model and execution orchestration skeleton that:

1. Can be fully unit-tested without hardware or WPF (mock `IAssertionContext`)
2. Establishes all interface contracts that Infrastructure/App layers will implement
3. Provides the foundation for Sprint 2 (TraceDrivenChannel + CLI Runner + end-to-end execution)

**Out of scope for Sprint 1**: Real channel implementation, CLI runner, fault injection, ECU simulator, reporting.

---

## 2. Sprint 1 Positioning

| Sprint | TestSuiteEngine State |
|---|---|
| Sprint 1 | Orchestration skeleton: iterate cases, call setup/teardown, collect results. Assertion execution via injected `IAssertionContext` mock. Unit tests verify orchestration logic (case skip, teardown exception handling, result aggregation). |
| Sprint 2 | End-to-end executable: Infrastructure-layer `AssertionContext` implementation + CLI Runner. `TestSuiteEngine` connects to real (or trace-driven) channel. |

---

## 3. File Structure

```
PeakCan.Host.Core/HIL/
├── TestCase.cs                    // Pure data model
├── TestCaseStep.cs                // Pure data with validated factory + custom JSON converter
├── TestCaseResult.cs              // Execution result per case
├── TestSuite.cs                   // Suite data model
├── TestSuiteResult.cs             // Execution result per suite
├── TestSuiteConfig.cs             // Configuration (FailurePolicy, etc.)
├── TestSuiteEngine.cs             // Orchestration engine
├── StepStatus.cs                  // Step execution status enum
├── StepParameters/                 // Strong-typed parameter records
│   ├── StepParameters.cs          // Abstract base (polymorphic JSON)
│   ├── SendFrameStep.cs
│   ├── WaitForSignalStep.cs
│   ├── AssertSignalStep.cs
│   ├── AssertRangeStep.cs
│   ├── ExpectFrameStep.cs
│   ├── AssertResponseTimeStep.cs
│   ├── AssertDtcStep.cs
│   ├── AssertNrcStep.cs
│   ├── DelayStep.cs
│   └── CommentStep.cs
├── StepExecutor/
│   ├── IStepExecutor.cs           // Strategy interface
│   └── StepParametersFactory.cs   // Dictionary -> strongly-typed
├── Setup/
│   ├── ITestFixture.cs            // Single fixture interface (Suite + Case level)
│   └── IFixtureResolver.cs        // Fixture key -> instance resolution
├── Assertions/
│   ├── AssertionResult.cs
│   └── AssertionPrimitives.cs     // Instance class, ctx-injected
├── Diff/
│   ├── DiffResult.cs
│   ├── DiffEntry.cs
│   ├── DiffConfig.cs              // Three-layer orthogonal config (with validation)
│   ├── DiffGranularity.cs
│   ├── AlignStrategy.cs
│   ├── ToleranceSpec.cs
│   └── IDiffEngine.cs
├── Parameterization/
│   ├── TestCaseTemplate.cs
│   ├── TemplateStep.cs
│   ├── ParameterSet.cs
│   └── TestCaseGenerator.cs
├── Progress/
│   └── TestProgress.cs
├── TypeResolver.cs                // Type name -> Type resolution
└── Contracts/
    ├── IAssertionContext.cs       // Frame stream + signal query + send
    ├── DecodedFrame.cs            // Extracted from interface (standalone)
    ├── ISignalObserver.cs         // Real-time signal observation (push)
    ├── ISignalHistory.cs          // Offline signal history (pull)
    └── IDbcLookup.cs             // DBC message lookup (App implements)
```

**Layering rules**: `Core/HIL/` has zero references to `Infrastructure` or `App`. All cross-layer communication via interfaces in `Contracts/`.

---

## 4. Data Models

### 4.1 TestCase

```csharp
public sealed record TestCase(
    string Id,
    string Name,
    string Description,
    string? PreConditions,
    IReadOnlyList<TestCaseStep> Steps,
    string? PostConditions,
    IReadOnlyList<string> Tags,
    int TimeoutMs = 0,
    IReadOnlyList<string>? CaseFixtureKeys = null);
```

### 4.2 TestSuite

```csharp
public sealed record TestSuite(
    string Name,
    IReadOnlyList<TestCase> Cases,
    IReadOnlyList<string> GlobalCaseFixtureKeys,    // DI keys (not type names)
    IReadOnlyList<string> SuiteFixtureKeys,          // DI keys (not type names)
    TestSuiteConfig Config,
    int TimeoutMs = 0);
```

### 4.3 TestSuiteConfig

```csharp
public sealed record TestSuiteConfig(
    FailurePolicy FailurePolicy = FailurePolicy.ContinueAll,
    bool ContinueAfterSetupFailure = true);

public enum FailurePolicy
{
    ContinueAll,           // Continue all steps and cases regardless of failures
    StopCaseOnFailure,     // Skip remaining steps in case, but continue suite
    StopSuiteOnFailure     // Stop entire suite on first case failure
}
```

### 4.4 TestCaseStep

```csharp
[JsonConverter(typeof(TestCaseStepJsonConverter))]
public sealed record TestCaseStep
{
    public TestCaseStepKind Kind { get; }
    public string? Label { get; }
    public StepParameters Parameters { get; }

    private TestCaseStep(TestCaseStepKind kind, string? label, StepParameters parameters)
    {
        Kind = kind;
        Label = label;
        Parameters = parameters;
    }

    public static TestCaseStep Create(StepParameters parameters, string? label = null)
        => new(parameters.Kind, label, parameters);
}
```

**Design note**: Private constructor + factory method guarantees `Kind == Parameters.Kind` (crash point F fix).

### 4.5 StepParameters (strongly-typed hierarchy with polymorphic JSON)

```csharp
[JsonDerivedType(typeof(WaitForSignalStep), "waitForSignal")]
[JsonDerivedType(typeof(SendFrameStep), "sendFrame")]
[JsonDerivedType(typeof(AssertSignalStep), "assertSignal")]
[JsonDerivedType(typeof(AssertRangeStep), "assertRange")]
[JsonDerivedType(typeof(ExpectFrameStep), "expectFrame")]
[JsonDerivedType(typeof(AssertResponseTimeStep), "assertResponseTime")]
[JsonDerivedType(typeof(AssertDtcStep), "assertDtc")]
[JsonDerivedType(typeof(AssertNrcStep), "assertNrc")]
[JsonDerivedType(typeof(DelayStep), "delay")]
[JsonDerivedType(typeof(CommentStep), "comment")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
public abstract record StepParameters(TestCaseStepKind Kind);

public record SendFrameStep(CanId Id, byte[] Data, bool Fd, bool Extended)
    : StepParameters(TestCaseStepKind.SendFrame);

public record WaitForSignalStep(string SignalName, double Expected, double Tolerance, int TimeoutMs)
    : StepParameters(TestCaseStepKind.WaitForSignal);

public record AssertSignalStep(string SignalName, double Expected, double Tolerance)
    : StepParameters(TestCaseStepKind.AssertSignal);

public record AssertRangeStep(string SignalName, double Min, double Max)
    : StepParameters(TestCaseStepKind.AssertRange);

public record ExpectFrameStep(CanId Id, byte[]? DataMask, int TimeoutMs)
    : StepParameters(TestCaseStepKind.WaitForFrame);

public record AssertResponseTimeStep(CanId ReqId, CanId RespId, int MaxMs)
    : StepParameters(TestCaseStepKind.AssertResponseTime);

public record AssertDtcStep(ushort? DtcCode, bool ExpectPresent)
    : StepParameters(TestCaseStepKind.AssertDtc);

public record AssertNrcStep(byte ServiceId, byte ExpectedNrc)
    : StepParameters(TestCaseStepKind.AssertNrc);

public record DelayStep(int Milliseconds) : StepParameters(TestCaseStepKind.Delay);

public record CommentStep(string Text) : StepParameters(TestCaseStepKind.Comment);

public enum TestCaseStepKind
{
    SendFrame, SendSequence, WaitForFrame, WaitForSignal,
    AssertSignal, AssertRange, AssertDtc, AssertNrc, AssertResponseTime,
    Delay, Comment
}
```

### 4.6 StepResult / TestCaseResult / TestSuiteResult

```csharp
public enum StepStatus { Passed, Failed, Skipped, Comment }

public sealed record StepResult(
    int StepIndex,
    TestCaseStepKind Kind,
    string? Label,
    StepStatus Status,
    string? Message,
    string? ActualValue,
    string? ExpectedValue,
    int ElapsedMs,
    IReadOnlyList<CanFrame>? FramesAroundFailure = null)
{
    public bool Passed => Status == StepStatus.Passed;
}

public sealed record TestCaseResult(
    string TestCaseId,
    string TestCaseName,
    bool Passed,
    string? FailureReason,
    int ElapsedMs,
    int TotalSteps,
    int PassedSteps,
    int FailedSteps,
    int SkippedSteps,
    IReadOnlyList<StepResult> StepResults);

public sealed record TestSuiteResult(
    string SuiteName,
    int TotalCases,
    int PassedCases,
    int FailedCases,
    int SkippedCases,
    int ElapsedMs,
    IReadOnlyList<string> SetupFailures,
    IReadOnlyList<TestCaseResult> CaseResults)
{
    public double PassRate => TotalCases > 0 ? (double)PassedCases / TotalCases : 0.0;
    public bool AllPassed => TotalCases > 0 && FailedCases == 0 && SkippedCases == 0;
}

public sealed record TestProgress(
    int CompletedCases,
    int TotalCases,
    string? CurrentCaseName = null,
    string? Message = null)
{
    public double PercentComplete => TotalCases > 0 ? (double)CompletedCases / TotalCases * 100 : 0;
}
```

---

## 5. Interface Contracts

### 5.1 IAssertionContext

```csharp
public interface IAssertionContext
{
    /// <summary>
    /// Subscribe to decoded frame stream. Callback fires when frame is decoded
    /// (implementation guarantees frame and signals snapshot are consistent).
    /// Callback invoked on a dedicated consumer thread (NOT the sink thread).
    /// Returns IDisposable; Dispose cancels subscription.
    /// </summary>
    IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame);

    /// <summary>
    /// Get last-decoded value of a signal (global cache across all frames).
    /// Format: "MessageName.SignalName" (e.g. "BMS_Status.EngineRPM").
    /// Returns null if signal not found or never decoded.
    /// </summary>
    double? GetSignalValue(string signalName);

    double CurrentTimestamp { get; }

    ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct);
}
```

**IDisposable contract**:
1. Idempotent (multiple Dispose calls harmless).
2. After Dispose returns, callback will NOT be invoked again.
3. Dispose does not block a callback currently executing (uses volatile flag).
4. Remaining queued frames are drained before consumer thread exits (5s timeout then forced cancel).

### 5.2 DecodedFrame (standalone, not nested)

```csharp
namespace PeakCan.Host.Core.HIL.Contracts;

/// <summary>
/// Decoded frame with signal snapshot.
    /// Signals dict contains ONLY signals from the current frame's matched message.
    /// Key format: "MessageName.SignalName".
    /// If frame matches no DBC message, Signals is empty.
    /// </summary>
    public sealed record DecodedFrame(
        CanFrame Frame,
        IReadOnlyDictionary<string, double> Signals);
```

### 5.3 ISignalObserver

```csharp
public interface ISignalObserver
{
    IDisposable ObserveSignal(string name, Action<double> onValueChanged);
    double? GetCurrentValue(string name);
}
```

### 5.4 ISignalHistory

```csharp
public interface ISignalHistory
{
    IReadOnlyList<(double Timestamp, double Value)> GetSignalSamples(
        string name, double startTime, double endTime);
    IReadOnlyList<string> KnownSignals { get; }
}
```

### 5.5 IDbcLookup

```csharp
public interface IDbcLookup
{
    Core.Dbc.Message? FindMessage(uint canId);
}
```

**Implementation chain**:
```
Core/HIL/Contracts/IDbcLookup.cs        ← interface
Infrastructure/Channel/AssertionContext.cs ← injects IDbcLookup, uses for decode
App/Services/DbcLookupAdapter.cs        ← implements IDbcLookup, wraps DbcService.Current
App/Composition/AppHostBuilder.cs       ← services.AddSingleton<IDbcLookup, DbcLookupAdapter>()
```

### 5.6 ITestFixture

```csharp
public interface ITestFixture
{
    Task SetupAsync(IAssertionContext ctx, CancellationToken ct);
    Task TeardownAsync(IAssertionContext ctx, CancellationToken ct);
}
```

### 5.7 IFixtureResolver

```csharp
public interface IFixtureResolver
{
    ITestFixture Resolve(string key);
}
```

**Rationale**: Decouples Engine from `IServiceProvider` keyed DI, enabling simple test fakes without NSubstitute limitations on extension methods.

**Production implementation**:
```csharp
internal sealed class ServiceProviderFixtureResolver : IFixtureResolver
{
    private readonly IServiceProvider _sp;
    public ServiceProviderFixtureResolver(IServiceProvider sp) => _sp = sp;
    public ITestFixture Resolve(string key) =>
        _sp.GetKeyedService<ITestFixture>(key)
        ?? throw new InvalidOperationException($"Fixture '{key}' not registered");
}
```

**Test implementation**:
```csharp
internal sealed class FakeFixtureResolver : IFixtureResolver
{
    private readonly Dictionary<string, ITestFixture> _fixtures = new();
    public void Register(string key, ITestFixture fixture) => _fixtures[key] = fixture;
    public ITestFixture Resolve(string key) =>
        _fixtures.TryGetValue(key, out var f) ? f
        : throw new KeyNotFoundException($"Fixture '{key}' not in fake");
}
```

---

## 6. AssertionPrimitives

```csharp
public sealed class AssertionPrimitives
{
    private readonly IAssertionContext _ctx;

    public AssertionPrimitives(IAssertionContext ctx) => _ctx = ctx;

    public async Task<AssertionResult> WaitForSignalAsync(
        string name, double expected, double tolerance, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>();
        using var sub = _ctx.SubscribeDecodedFrames(frame =>
        {
            var val = _ctx.GetSignalValue(name);
            if (val is { } v && Math.Abs(v - expected) <= tolerance)
                tcs.TrySetResult(true);
        });

        var delayTask = Task.Delay(Timeout.Infinite, ct);
        var winner = await Task.WhenAny(tcs.Task, delayTask).ConfigureAwait(false);

        return winner == tcs.Task
            ? AssertionResult.Pass($"signal {name} = {expected} ±{tolerance}")
            : AssertionResult.Fail($"timeout waiting for {name} = {expected} ±{tolerance}",
                actual: _ctx.GetSignalValue(name)?.ToString());
    }

    public AssertionResult AssertSignal(string name, double expected, double tolerance)
    {
        var val = _ctx.GetSignalValue(name);
        return val is null
            ? AssertionResult.Fail($"signal {name} not found")
            : Math.Abs(val.Value - expected) <= tolerance
                ? AssertionResult.Pass()
                : AssertionResult.Fail($"signal {name} out of tolerance",
                    actual: val.Value.ToString(), expected: expected.ToString());
    }

    public AssertionResult AssertRange(string name, double min, double max)
    {
        var val = _ctx.GetSignalValue(name);
        return val is null
            ? AssertionResult.Fail($"signal {name} not found")
            : val >= min && val <= max
                ? AssertionResult.Pass()
                : AssertionResult.Fail($"signal {name} = {val} outside [{min}, {max}]");
    }
}

public sealed record AssertionResult(
    bool Passed,
    string? Message,
    string? ActualValue,
    string? ExpectedValue)
{
    public static AssertionResult Pass(string? msg = null) => new(true, msg, null, null);
    public static AssertionResult Fail(string msg, string? actual = null, string? expected = null)
        => new(false, msg, actual, expected);
}
```

**Async contract**: All assertion methods return `Task<AssertionResult>` (never throw). Timeout controlled by caller via `CancellationTokenSource.CancelAfter(timeout)`.

---

## 7. Diff Engine

```csharp
public enum DiffGranularity { Frame, Signal, Event }
public enum AlignStrategy { Timestamp, NearestNeighbor, Index }

public sealed record ToleranceSpec(
    double AbsoluteTolerance = 0.0,
    double RelativeTolerance = 0.0)
{
    public bool IsWithin(double expected, double actual)
    {
        var diff = Math.Abs(expected - actual);
        return diff <= AbsoluteTolerance || diff <= Math.Abs(expected) * RelativeTolerance;
    }
    public static ToleranceSpec Exact => new(0.0, 0.0);
}

public sealed record DiffConfig(
    DiffGranularity Granularity = DiffGranularity.Frame,
    AlignStrategy Alignment = AlignStrategy.Timestamp,
    ToleranceSpec Tolerance = default,
    int NeighborWindowMs = 100)
{
    public DiffConfig(
        DiffGranularity granularity,
        AlignStrategy alignment,
        ToleranceSpec tolerance,
        int neighborWindowMs)
    {
        if (alignment == AlignStrategy.NearestNeighbor && neighborWindowMs <= 0)
            throw new ArgumentException(
                "NeighborWindowMs must be > 0 for NearestNeighbor alignment",
                nameof(neighborWindowMs));
        if (tolerance.AbsoluteTolerance < 0 || tolerance.RelativeTolerance < 0)
            throw new ArgumentException("Tolerance cannot be negative", nameof(tolerance));

        Granularity = granularity;
        Alignment = alignment;
        Tolerance = tolerance;
        NeighborWindowMs = neighborWindowMs;
    }
}

public interface IDiffEngine
{
    /// <summary>
    /// Compare two frame sequences.
    /// Memory constraint: inputs are fully loaded IReadOnlyList.
    /// Current implementation assumes trace <= 1M frames (~128MB double-buffer).
    /// Future: IAsyncEnumerable for streaming diff.
    /// </summary>
    DiffResult Diff(IReadOnlyList<CanFrame> golden, IReadOnlyList<CanFrame> actual, DiffConfig config);
}

/// <summary>
/// Diff engine implementation. Two constructors:
/// - DiffEngine(): frame-level diff (no DBC needed)
/// - DiffEngine(IDbcLookup): signal-level diff (DBC required for decode)
/// </summary>
internal sealed class DiffEngine : IDiffEngine
{
    private readonly IDbcLookup? _dbcLookup;

    public DiffEngine() { }
    public DiffEngine(IDbcLookup dbcLookup) => _dbcLookup = dbcLookup;

    public DiffResult Diff(
        IReadOnlyList<CanFrame> golden,
        IReadOnlyList<CanFrame> actual,
        DiffConfig config)
    {
        if (config.Granularity == DiffGranularity.Signal && _dbcLookup is null)
            throw new InvalidOperationException(
                "Signal-level diff requires IDbcLookup. Use DiffEngine(IDbcLookup) constructor.");
        // ... implementation
    }
}

public sealed record DiffResult(
    int TotalGolden,
    int TotalActual,
    int Matched,
    int Added,
    int Removed,
    int Modified,
    IReadOnlyList<DiffEntry> Entries)
{
    public bool IsMatch => Added == 0 && Removed == 0 && Modified == 0;
    public double MatchRate => TotalGolden > 0 ? (double)Matched / TotalGolden : 0.0;
}

public sealed record DiffEntry(
    DiffEntryType Type,
    int? GoldenIndex,
    int? ActualIndex,
    string? Reason,
    CanFrame? GoldenFrame,
    CanFrame? ActualFrame);

public enum DiffEntryType { Added, Removed, Modified, Matched }
```

**Dependency direction**: Frame-level diff depends only on `Core/CanFrame` + `Core/Replay/ReplayFrame`. Signal-level diff optionally uses `IDbcLookup` via constructor injection. No mandatory dependency on `Contracts/`.

---

## 8. Parameterization

```csharp
public sealed record ParameterSet(
    IReadOnlyDictionary<string, object> Values,
    string Id);  // Caller-provided unique identifier, e.g. "rpm=3000_temp=85"

public sealed record TestCaseTemplate(
    string BaseId,
    string NameTemplate,
    string DescriptionTemplate,
    IReadOnlyList<TemplateStep> Steps,
    IReadOnlyList<string> Tags);

public sealed record TemplateStep(
    string Kind,                    // "WaitForSignal" - string, not enum
    string? Label,
    IReadOnlyDictionary<string, string> Parameters);  // All values are strings

public static class TestCaseGenerator
{
    public static TestCase Generate(TestCaseTemplate template, ParameterSet parameters)
    {
        var resolvedSteps = template.Steps.Select(step =>
        {
            var resolvedParams = step.Parameters.ToDictionary(
                kv => kv.Key,
                kv => (object)Resolve(kv.Value, parameters));
            var kind = Enum.Parse<TestCaseStepKind>(step.Kind);
            return TestCaseStep.Create(StepParametersFactory.Create(kind, resolvedParams));
        }).ToList();

        return new TestCase(
            Id: $"{template.BaseId}_{parameters.Id}",
            Name: Resolve(template.NameTemplate, parameters),
            Description: Resolve(template.DescriptionTemplate, parameters),
            PreConditions: null,
            Steps: resolvedSteps,
            PostConditions: null,
            Tags: template.Tags,
            TimeoutMs: 0);
    }

    private static string Resolve(string template, ParameterSet parameters)
        => Regex.Replace(template, @"\{\{(\w+)\}\}", m =>
            parameters.Values.TryGetValue(m.Groups[1].Value, out var v)
                ? v?.ToString() ?? m.Value
                : m.Value);
}

public static class StepParametersFactory
{
    public static StepParameters Create(TestCaseStepKind kind, IReadOnlyDictionary<string, object> p) => kind switch
    {
        TestCaseStepKind.WaitForSignal => new WaitForSignalStep(
            (string)p["SignalName"],
            Convert.ToDouble(p["Expected"]),
            Convert.ToDouble(p["Tolerance"]),
            Convert.ToInt32(p["TimeoutMs"])),
        TestCaseStepKind.SendFrame => new SendFrameStep(
            new CanId(Convert.ToUInt32(p["Id"], 16)),
            Convert.FromHexString((string)p["Data"]),
            Convert.ToBoolean(p["Fd"]),
            Convert.ToBoolean(p["Extended"])),
        TestCaseStepKind.Delay => new DelayStep(Convert.ToInt32(p["Milliseconds"])),
        TestCaseStepKind.Comment => new CommentStep((string)p["Text"]),
        // ... other kinds
        _ => throw new ArgumentException($"Unknown step kind: {kind}")
    };
}
```

**Implementation verification required**: CanId constructor signature (uint raw vs uint + FrameFormat) and CanFrame constructor signature must be verified against actual Core types before implementation.

---

## 9. Serialization

### 9.1 Polymorphic StepParameters

Uses `System.Text.Json` polymorphic serialization attributes (Section 4.5). Each subclass registered with a string discriminator.

### 9.2 TestCaseStep Custom Converter

```csharp
internal sealed class TestCaseStepJsonConverter : JsonConverter<TestCaseStep>
{
    public override TestCaseStep Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var kind = Enum.Parse<TestCaseStepKind>(root.GetProperty("$kind").GetString()!);
        var parameters = JsonSerializer.Deserialize<StepParameters>(root.GetProperty("parameters").GetRawText(), options)!;
        var label = root.TryGetProperty("label", out var l) ? l.GetString() : null;
        return TestCaseStep.Create(parameters, label);
    }

    public override void Write(Utf8JsonWriter writer, TestCaseStep value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("$kind", value.Kind.ToString().ToLowerInvariant());
        if (value.Label is not null)
            writer.WriteString("label", value.Label);
        writer.WritePropertyName("parameters");
        JsonSerializer.Serialize(writer, value.Parameters, value.Parameters.GetType(), options);
        writer.WriteEndObject();
    }
}
```

### 9.3 JSON Schema Example

```json
{
  "name": "BMS_SleepCurrent_Test",
  "cases": [{
    "id": "bms_sleep_001",
    "name": "BMS Sleep Current < 10mA",
    "steps": [
      {
        "$kind": "sendFrame",
        "parameters": { "id": "0x7DF", "data": "0210030000000000", "fd": false, "extended": false }
      },
      {
        "$kind": "waitForSignal",
        "parameters": { "signalName": "BMS_Status.SleepCurrent", "expected": 0.01, "tolerance": 0.005, "timeoutMs": 5000 }
      }
    ]
  }],
  "globalCaseFixtureKeys": ["CanChannelFixture"],
  "suiteFixtureKeys": ["DbcLoadFixture"],
  "config": { "failurePolicy": "continueAll", "continueAfterSetupFailure": true },
  "timeoutMs": 60000
}
```

---

## 10. TestSuiteEngine

### 10.1 Class Definition

```csharp
public sealed class TestSuiteEngine
{
    private readonly IFixtureResolver _fixtureResolver;
    private readonly IReadOnlyDictionary<TestCaseStepKind, IStepExecutor> _executors;

    public TestSuiteEngine(IFixtureResolver fixtureResolver, IEnumerable<IStepExecutor> executors)
    {
        _fixtureResolver = fixtureResolver;
        _executors = executors.ToDictionary(e => e.Kind);
    }

    public async Task<TestSuiteResult> ExecuteAsync(
        TestSuite suite,
        IAssertionContext ctx,
        TestSuiteConfig config,
        IProgress<TestProgress>? progress = null,
        CancellationToken ct = default);
}
```

### 10.2 Execution Sequence

```
1. Create linked CTS (suite timeout)
2. Resolve suite fixture keys -> instantiate via IFixtureResolver
3. Suite Setup (on exception: record; if !ContinueAfterSetupFailure -> skip all cases)
4. For each TestCase (try-finally for suite teardown):
   a. Resolve case fixture keys (global + case-specific merged) -> instantiate
   b. Case Setup (on exception: record SetupFailure, skip steps, still teardown)
   c. For each Step (StopCaseOnFailure respected):
      - Find IStepExecutor
      - Execute via defensive try-catch
      - Record StepResult
   d. Case Teardown (finally: reverse order, independent 10s CTS)
   e. Aggregate -> TestCaseResult
   f. Report progress
   g. FailurePolicy.StopSuiteOnFailure check
5. Suite Teardown (finally: reverse order, independent 10s CTS)
6. Aggregate -> TestSuiteResult
```

### 10.3 Teardown Protection

```csharp
// Suite-level
try
{
    if (!suiteSetupFailed)
    {
        int caseIndex = 0;
        foreach (var caseModel in suite.Cases)
        {
            linkedCt.ThrowIfCancellationRequested();
            var caseResult = await ExecuteCaseAsync(...);
            caseResults.Add(caseResult);
            if (!caseResult.Passed && config.FailurePolicy == FailurePolicy.StopSuiteOnFailure)
                break;
            caseIndex++;
        }
    }
}
finally
{
    foreach (var fixture in suiteFixtures.Reverse())
    {
        try
        {
            using var teardownCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await fixture.TeardownAsync(ctx, teardownCts.Token);
        }
        catch (Exception ex) => LogTeardownFailure(fixture.GetType(), ex);
    }
}

// Case-level (inside ExecuteCaseAsync)
try
{
    // steps execution
}
finally
{
    foreach (var fixture in caseFixtures.Reverse())
    {
        try
        {
            using var teardownCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await fixture.TeardownAsync(ctx, teardownCts.Token);
        }
        catch (Exception ex)
            => failureReason = (failureReason ?? "") + $"; Teardown failed: {ex.Message}";
    }
}
```

### 10.4 Executor Defensive Catch

```csharp
StepResult result;
try
{
    result = await executor.ExecuteAsync(step, ctx, ct);
}
catch (OperationCanceledException) { throw; }
catch (Exception ex)
{
    result = new StepResult(i, step.Kind, step.Label, StepStatus.Failed,
        $"Executor threw unhandled: {ex.GetType().Name}: {ex.Message}",
        null, null, 0);
}
```

### 10.5 Fixture Resolution

```csharp
private IReadOnlyList<ITestFixture> ResolveFixtures(IEnumerable<string> keys)
{
    return keys.Select(key => _fixtureResolver.Resolve(key)).ToList();
}

// Merge global + case-specific
var globalFixtures = ResolveFixtures(suite.GlobalCaseFixtureKeys);
var caseFixtures = ResolveFixtures(testCase.CaseFixtureKeys ?? Array.Empty<string>());
var allCaseFixtures = globalFixtures.Concat(caseFixtures).ToList();
```

### 10.6 Empty Suite Handling

```csharp
if (suite.Cases.Count == 0)
{
    return new TestSuiteResult(
        SuiteName: suite.Name,
        TotalCases: 0, PassedCases: 0, FailedCases: 0, SkippedCases: 0,
        ElapsedMs: 0,
        SetupFailures: Array.Empty<string>(),
        CaseResults: Array.Empty<TestCaseResult>());
    // Caller checks TotalCases == 0 to detect empty suite
}
```

---

## 11. IStepExecutor Strategy Interface

```csharp
public interface IStepExecutor
{
    TestCaseStepKind Kind { get; }
    Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct);
}
```

**Registration**: Each `TestCaseStepKind` maps to one executor. New step kind = new executor class + DI registration. Engine zero modification (Open/Closed principle).

**Sprint 1 executors** (skeleton only, full implementation in Sprint 2):
- `SendFrameStepExecutor`
- `WaitForSignalStepExecutor`
- `AssertSignalStepExecutor`
- `AssertRangeStepExecutor`
- `ExpectFrameStepExecutor`
- `AssertResponseTimeStepExecutor`
- `AssertDtcStepExecutor`
- `AssertNrcStepExecutor`
- `DelayStepExecutor`

---

## 12. Type Resolver

```csharp
internal static class TypeResolver
{
    public static Type ResolveType(string typeName)
    {
        var type = Type.GetType(typeName, throwOnError: false);
        if (type is not null) return type;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = asm.GetType(typeName, throwOnError: false);
            if (type is not null) return type;
        }
        throw new TypeLoadException($"Cannot resolve type: {typeName}");
    }
}
```

---

## 13. Architecture Tests

New test file: `tests/PeakCan.Host.Core.Tests/Architecture/HILLayeringTests.cs`

```csharp
[Fact]
public void HIL_assembly_does_not_reference_Infrastructure_or_App()
{
    var assembly = typeof(TestCase).Assembly;
    var refs = assembly.GetReferencedAssemblies().Select(a => a.Name).ToHashSet();

    Assert.DoesNotContain(refs, n => n == "PeakCan.Host.Infrastructure");
    Assert.DoesNotContain(refs, n => n == "PeakCan.Host.App");
}
```

**Note**: Compiler already prevents type-level dependencies. This test catches future project reference additions. NetArchTest not required for Sprint 1.

---

## 14. Key Design Decisions Summary

| Decision | Rationale |
|---|---|
| TestCase = pure data, TestSuiteEngine = orchestration | Separation of concerns; testable without execution |
| StepParameters strong-typed hierarchy + Kind enum | Compile-time safety vs dictionary |
| IStepExecutor strategy pattern | Open/close principle; no engine modification for new step kinds |
| IAssertionContext IDisposable subscription | Leak-free callback management |
| DecodedFrame signals = current frame only, GetSignalValue = global cache | Clear split between incremental and state access |
| IDbcLookup interface in Core | Breaks DBC resolution chain across layers |
| ITestFixture single interface + IFixtureResolver | Avoids 4 separate interfaces; testable without keyed DI |
| FailurePolicy enum (3 levels) | Covers all HIL failure propagation needs |
| DiffConfig three-layer orthogonal + validation | Frame/Signal/Event × Timestamp/NearestNeighbor/Index × tolerance |
| TemplateStep (strings) + StepParametersFactory | Enables parameterized testing with type-safe output |
| Channel + single consumer thread with DropOldest | Prevents backpressure deadlock on sink thread |
| Teardown in try-finally with independent 10s CTS | Resource cleanup guaranteed even on cancellation |
| Dispose: Complete -> Wait(5s) -> Cancel (timeout only) | Honors drain contract; cancel is fallback |
| Polymorphic JSON + custom TestCaseStep converter | Full serialization round-trip for all step types |
| IFixtureResolver decouples from IServiceProvider | Simple test fakes; no NSubstitute keyed DI limitations |

---

## 15. Open Issues (Deferred)

| Issue | Target Sprint |
|---|---|
| FramesAroundFailure ring buffer mechanism | Sprint 2 |
| Multi-ECU matrix simulation | Phase 3 |
| Signal-level diff (requires IDbcLookup injection) | Sprint 2 |
| IAsyncEnumerable diff for large traces | Future |
| LLM-assisted failure analysis (opt-in) | Phase 5 |
| TraceDrivenChannel implementation | Sprint 2 |
| CanId/CanFrame constructor signature verification | Sprint 1 implementation |

---

## 16. Sprint 1 Test Plan

1. **Unit tests**: `TestSuiteEngine` with mock `IAssertionContext` and mock `IStepExecutor`
   - Empty suite -> TotalCases = 0, AllPassed = false
   - Single case, single step -> Passed
   - Step failure + StopCaseOnFailure -> remaining steps skipped
   - Step failure + ContinueAll -> all steps executed
   - Case setup failure -> case skipped, teardown still called
   - Suite setup failure + ContinueAfterSetupFailure=false -> all cases skipped
   - CancellationToken cancellation -> teardown still called, OperationCanceledException propagated
   - Setup + Teardown both fail -> failureReason contains both messages

2. **Unit tests**: `AssertionPrimitives` with mock `IAssertionContext`
   - WaitForSignal: signal appears -> Pass
   - WaitForSignal: timeout -> Fail with actual value
   - AssertSignal: in tolerance -> Pass
   - AssertSignal: out of tolerance -> Fail with actual + expected
   - AssertRange: in range -> Pass
   - AssertRange: out of range -> Fail

3. **Unit tests**: `TestCaseGenerator`
   - Template expansion -> correct StepParameters types
   - Type conversion failure -> ArgumentException
   - Parameter set with special characters -> correct Id

4. **Unit tests**: `Diff` engine
   - Frame-level exact match -> IsMatch
   - Frame-level one modified -> Modified = 1
   - Signal-level tolerance match -> IsMatch
   - Signal-level out of tolerance -> Modified = 1
   - Signal-level without IDbcLookup -> InvalidOperationException
   - DiffConfig negative window -> ArgumentException

5. **Unit tests**: Serialization round-trip
   - TestCase serialize -> deserialize -> equal
   - Polymorphic StepParameters preserves all fields
   - TestCaseStep with private constructor round-trips correctly

6. **Architecture test**: HIL namespace assembly references

---

## 17. Relationship to Phased Gap Analysis

This spec covers Phase 1 Sprint 1 (items 1.2, 1.3, 1.4, 1.5a, 1.6, 1.7b-frame, 1.8a-frame):

| Gap ID | Covered In |
|---|---|
| 1.2 TestCase data model | Section 4 |
| 1.3 Setup/Teardown | Section 5.6 + 10.3 |
| 1.4 Parameterization | Section 8 |
| 1.5a Suite sequential execution | Section 10 |
| 1.6 Interface contracts (IAssertionContext, ISignalObserver, IFixtureResolver) | Section 5 |
| 1.7b Frame-level diff | Section 7 |
| 1.8a Baseline comparison (frame-level) | Section 7 |
