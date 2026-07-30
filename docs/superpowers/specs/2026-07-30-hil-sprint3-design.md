# HIL Sprint 3: End-to-End Testing Pipeline

**Date**: 2026-07-30 (merged 2026-07-29 draft)
**Status**: Draft v1 (merged, pending review)
**Depends**: [Sprint 1](2026-07-29-hil-sprint1-design.md) (complete), [Sprint 2](2026-07-29-hil-sprint2-design.md) (complete)
**Supersedes**: [2026-07-29 Sprint 3 draft](2026-07-29-hil-sprint3-design.md) (Draft v1)
**Scope**: JUnit XML output, WaitForFrame / UDS assertion executors, BLF support, WriteAsync loopback, PeakCanAssertionContext, WPF HIL panel, FramesAroundFailure

---

## Merge Decisions & Disagreements

本 spec 合并了两份 Sprint 3 draft（2026-07-29 "Physical Channel & Stimulus-Response" 与 2026-07-30 "Output Formats, UDS Assertions & Frame Arrival"）。合并过程中存在以下分歧与决策，请审核：

| # | 议题 | 07-29 方案 | 07-30 方案 | 采纳 | 理由 |
|---|---|---|---|---|---|
| M1 | Sprint 3 范围 | 真实硬件 + WPF 面板 + FramesAroundFailure | JUnit + BLF + loopback | **全部纳入，分阶段** | 两份都是 Sprint 3 有效内容；Phase A 离线可独立交付，不阻塞于硬件 |
| M2 | UDS executor 注入 | 直接注入 concrete `UdsClient` | 引入 `IUdsSession` 接口 | **IUdsSession + UdsSessionAdapter** | 现有 `UdsClient.ReadDtcInformationAsync` 返回原始 `byte[]`；adapter 封装解析后 executor 可用 mock IUdsSession 直接单测，无需 mock IsoTp 链 |
| M3 | WaitForFrame 匹配语义 | AND-mask `(data & mask) == mask` | `SequenceEqual` 精确匹配 | **AND-mask** | 汽车惯例 don't-care 字节；复用现有 `ExpectFrameStep` record |
| M4 | WaitForFrame step record | 复用现有 `ExpectFrameStep(CanId Id, byte[]? DataMask, int TimeoutMs)` | 新建 `WaitForFrameStep(uint CanId, ...)` | **复用 ExpectFrameStep** | 现有 record 已有 discriminator `"expectFrame"` + factory 映射；新建会抢同一个 `TestCaseStepKind.WaitForFrame` enum value |
| M5 | AssertResponseTime 语义 | 帧级 wall-clock（ReqId→RespId，现有 record） | UDS session `LastResponseTimeMs` | **帧级 wall-clock** | 现有 record `AssertResponseTimeStep(CanId ReqId, CanId RespId, int MaxMs)` 是总线级通用测量，适用于 trace + 硬件两种通道 |
| M6 | JUnit XML namespace | — | 捏造 `http://junit.org/junit4/extensions` | **无 namespace** | 标准 JUnit XML（Jenkins/Azure DevOps/GitLab 兼容）不使用 XML namespace；且原代码 `ns` 变量声明后从未使用 |
| M7 | BlfParser 调用 | — | `BlfParser.Parse(stream, ct)` | **`BlfParser.ParseAsync(stream, options, logger, ct)`** | 实际 API 需要 `ReplayOptions` 参数且为 async；原签名不存在 |
| M8 | WriteAsync loopback channel | — | `CreateBounded<CanFrame>(1000)` + `TryWrite`（满则静默丢帧） | **`DropOldest` bounded channel** | 与 `HILAssertionContext._frameChannel` 一致；明确语义避免测试中静默丢帧 |

---

## 1. Goal

Sprint 2 交付了离线 trace-replay 测试（只读通道，`WriteAsync` = no-op）。Sprint 3 补齐端到端能力，分三个阶段（Stage）交付：

> **术语说明**：本 spec 用 **Stage A/B/C** 指 Sprint 3 内部交付阶段；用 **Phase 3/5** 指项目级阶段（故障注入、LLM 分析等）。两者含义不同。

- **Stage A（离线能力）**：JUnit XML 输出、WaitForFrame / AssertResponseTime / AssertDtc / AssertNrc 执行器、BLF 文件支持、WriteAsync loopback — 无需硬件，CI 可跑
- **Stage B（硬件在环）**：PeakCanAssertionContext（PCAN 真实硬件）、CLI `--hw` 模式切换、ISO-TP 帧路由桥接
- **Stage C（UI + 诊断）**：WPF HIL 面板、FramesAroundFailure 环形缓冲

Out of scope: 故障注入（Phase 3）、多 ECU 矩阵（Phase 3）、LLM 辅助分析（Phase 5）、ECU 模拟器（Phase 3）。

---

## 2. Sprint Positioning

| Sprint | Channel | WriteAsync | Executors | Runner |
|---|---|---|---|---|
| Sprint 1 | Mock | Mock | 6 skeleton | Unit tests |
| Sprint 2 | TraceDrivenChannel | No-op | Same 6 | CLI headless |
| **Sprint 3** | TraceDrivenChannel / **PeakCanChannel** | **Real / Loopback** | **10 (6 + 4, 9 functional + 1 SendSequence placeholder)** | **CLI + WPF** |

---

## 3. Stage A — Key Architecture Decisions

### 3.1 JUnit XML Schema

Standard JUnit XML format (compatible with Jenkins, Azure DevOps, GitLab). **No XML namespace** — the de-facto JUnit schema is namespace-less.

```xml
<?xml version="1.0" encoding="utf-8"?>
<testsuites>
  <testsuite name="IntegrationSuite" tests="2" failures="1" time="1.500">
    <testcase name="case_1" classname="IntegrationSuite" time="0.500"/>
    <testcase name="case_2" classname="IntegrationSuite" time="1.000">
      <failure message="Step 0 failed: signal RPM out of tolerance">...</failure>
    </testcase>
  </testsuite>
</testsuites>
```

### 3.2 WaitForFrame Executor (uses existing ExpectFrameStep)

Reuse existing `ExpectFrameStep(CanId Id, byte[]? DataMask, int TimeoutMs)` record. New `ExpectFrameStepExecutor` + `AssertionPrimitives.WaitForFrameAsync` with AND-mask matching.

### 3.3 UDS Assertion Executors (IUdsSession abstraction)

Introduce `IUdsSession` interface to decouple HIL executors from the concrete `UdsClient` / IsoTpLayer dependency chain. `UdsSessionAdapter` (Infrastructure layer) wraps `UdsClient`.

### 3.4 BLF File Support

Add `LoadBlf(string path)` to `TraceDrivenChannel`. Use existing `BlfParser.ParseAsync(stream, options, logger, ct)` — requires `ReplayOptions` parameter.

### 3.5 WriteAsync Loopback

`TraceDrivenChannel.WriteAsync` writes to a `DropOldest` bounded loopback queue, then **synchronously drains it** via `ProcessLoopbackInternal` and raises `FrameReceived`. Does **not** depend on `OnTick` (loopback frames are emitted on the caller's thread). A `_loopbackLock` ensures mutual exclusion with OnTick's trace frame emission, so `FrameReceived` is only ever invoked from one thread at a time. Enables stimulus-response testing without physical ECU.

---

## 4. Step Parameters (existing — DO NOT redefine)

⚠️ 以下 record 已存在于代码库（`Core/HIL/StepParams/`），有对应的 `[JsonDerivedType]` discriminator（`StepParameters.cs`）和 `StepParametersFactory` 映射。**复用，不要重新定义。**

### 4.1 ExpectFrameStep (WaitForFrame kind)

```csharp
// Core/HIL/StepParams/ExpectFrameStep.cs (EXISTING)
public record ExpectFrameStep(CanId Id, byte[]? DataMask, int TimeoutMs)
    : StepParameters(TestCaseStepKind.WaitForFrame);
```

JSON discriminator: `"expectFrame"` · Enum: `TestCaseStepKind.WaitForFrame`

### 4.2 AssertDtcStep

```csharp
// Core/HIL/StepParams/AssertDtcStep.cs (EXISTING)
public record AssertDtcStep(ushort? DtcCode, bool ExpectPresent)
    : StepParameters(TestCaseStepKind.AssertDtc);
```

JSON discriminator: `"assertDtc"` · Semantics: check if a SPECIFIC `DtcCode` is present/absent (null = any DTC).

### 4.3 AssertNrcStep

```csharp
// Core/HIL/StepParams/AssertNrcStep.cs (EXISTING)
public record AssertNrcStep(byte ServiceId, byte ExpectedNrc)
    : StepParameters(TestCaseStepKind.AssertNrc);
```

JSON discriminator: `"assertNrc"` · Semantics: send request to `ServiceId`, expect `ExpectedNrc`.

### 4.4 AssertResponseTimeStep

```csharp
// Core/HIL/StepParams/AssertResponseTimeStep.cs (EXISTING)
public record AssertResponseTimeStep(CanId ReqId, CanId RespId, int MaxMs)
    : StepParameters(TestCaseStepKind.AssertResponseTime);
```

JSON discriminator: `"assertResponseTime"` · Semantics: send `ReqId` frame, measure wall-clock until `RespId` frame arrives (bus-level, works on any channel).

### 4.5 TestCaseStepKind enum (existing — no new values needed)

```csharp
public enum TestCaseStepKind
{
    SendFrame, SendSequence, WaitForFrame, WaitForSignal,
    AssertSignal, AssertRange, AssertDtc, AssertNrc,
    AssertResponseTime, Delay, Comment,
}
```

---

## 5. JUnit XML Writer

### 5.1 File: `PeakCan.Host.Cli/JUnitWriter.cs`

Aligned with existing `ResultWriter` pattern (§5.2), but **no XML namespace** (standard JUnit schema). Explicit `XDeclaration` for `<?xml?>` header (matches `ResultWriter` behavior).

```csharp
public static class JUnitWriter
{
    public static async Task WriteJunit(TestSuiteResult result, string path)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("testsuites",
                new XElement("testsuite",
                    new XAttribute("name", result.SuiteName),
                    new XAttribute("tests", result.TotalCases),
                    new XAttribute("failures", result.FailedCases),
                    new XAttribute("skipped", result.SkippedCases),
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

**已知限制**：空 suite（`TotalCases = 0`）时输出 `tests="0" failures="0"`。某些 CI（如 GitLab）可能将 `tests="0"` 解释为"测试发现失败"。如需避免，可在 `testsuites` 元素上添加 `errors="0"` 属性，或在 CI 配置中忽略空 suite。

### 5.2 Output Formats (updated)

| Format | Flag | Implementation |
|---|---|---|
| Console ANSI | default | `ConsoleProgress` |
| TRX | `--format trx` | `ResultWriter.WriteTrx()` |
| JUnit XML | `--format junit` | `JUnitWriter.WriteJunit()` |

---

## 6. WaitForFrame Executor

### 6.1 New method in AssertionPrimitives

File: `Core/HIL/Assertions/AssertionPrimitives.cs` (add method)

```csharp
public async Task<AssertionResult> WaitForFrameAsync(
    CanId expectedId, byte[]? dataMask, int timeoutMs, CancellationToken ct)
{
    var tcs = new TaskCompletionSource<CanFrame>();
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(timeoutMs);

    using var sub = _ctx.SubscribeDecodedFrames(frame =>
    {
        if (frame.Frame.Id.Raw == expectedId.Raw && MatchesMask(frame.Frame.Data, dataMask))
            tcs.TrySetResult(frame.Frame);
    });

    using var registration = cts.Token.Register(() => tcs.TrySetCanceled());
    try
    {
        var matched = await tcs.Task.ConfigureAwait(false);
        return AssertionResult.Pass($"frame 0x{expectedId.Raw:X} received");
    }
    catch (OperationCanceledException)
    {
        return AssertionResult.Fail($"timeout waiting for frame 0x{expectedId.Raw:X} ({timeoutMs}ms)");
    }
}

private static bool MatchesMask(ReadOnlyMemory<byte> data, byte[]? mask)
{
    if (mask is null || mask.Length == 0) return true;
    if (data.Length < mask.Length) return false;
    for (int i = 0; i < mask.Length; i++)
    {
        if ((data.Span[i] & mask[i]) != mask[i]) return false;
    }
    return true;
}
```

**Design note**: `dataMask` is AND-masked: `(data[i] & mask[i]) == mask[i]`. A `null` or empty mask matches any data. This is the automotive convention for "don't care" bytes.

### 6.2 ExpectFrameStepExecutor

File: `Core/HIL/StepExecutor/ExpectFrameStepExecutor.cs` (NEW)

```csharp
internal sealed class ExpectFrameStepExecutor : IStepExecutor
{
    private readonly Assertions.AssertionPrimitives _primitives;

    public ExpectFrameStepExecutor(Assertions.AssertionPrimitives primitives) => _primitives = primitives;
    public TestCaseStepKind Kind => TestCaseStepKind.WaitForFrame;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, Contracts.IAssertionContext ctx, CancellationToken ct)
    {
        var p = (ExpectFrameStep)step.Parameters;
        var result = await _primitives.WaitForFrameAsync(p.Id, p.DataMask, p.TimeoutMs, ct);

        return new StepResult(0, step.Kind, step.Label,
            result.Passed ? StepStatus.Passed : StepStatus.Failed,
            result.Message, result.ActualValue, result.ExpectedValue, 0);
    }
}
```

---

## 7. UDS Assertion Executors

### 7.1 New Interface: `IUdsSession`

File: `Core/HIL/Contracts/IUdsSession.cs` (NEW)

```csharp
/// <summary>
/// Decouples HIL executors from the concrete UdsClient / IsoTpLayer dependency chain.
/// Adapter (Infrastructure layer) wraps UdsClient.
/// </summary>
public interface IUdsSession
{
    Task<IReadOnlyList<DtcInfo>> ReadDtcInformation(byte statusMask, CancellationToken ct);
    Task SendRequestAsync(byte serviceId, byte[]? data, CancellationToken ct);
}
```

### 7.2 DtcInfo record

File: `Core/HIL/Contracts/DtcInfo.cs` (NEW)

```csharp
/// <summary>
/// Parsed DTC entry (ISO 14229-1 §11.3.5).
/// Code is 2-byte (Motorola high byte first from 3-byte DTC field).
/// Status byte: bit 0 = testFailed, bit 2 = confirmedDTC.
/// </summary>
public sealed record DtcInfo(ushort Code, byte Status);
```

### 7.3 Exception hierarchy (clean layering)

File: `Core/HIL/Contracts/UdsSessionException.cs` (NEW)
File: `Core/HIL/Contracts/UdsNrcException.cs` (NEW)

⚠️ **层级隔离**：executor 不能 `catch UdsException`（`Core.Uds` 命名空间），否则违反 M2/D4 的"HIL Core 不依赖 Core.Uds"目标。adapter 必须将所有 `UdsException` 转换为 HIL Contracts 层异常。

```csharp
/// <summary>
/// Base exception for all IUdsSession failures. Defined in Core/HIL/Contracts
/// so executors can catch without referencing Core.Uds.
/// </summary>
public abstract class UdsSessionException : Exception
{
    protected UdsSessionException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>
/// Thrown by IUdsSession.SendRequestAsync when ECU returns a Negative Response.
/// </summary>
public sealed class UdsNrcException : UdsSessionException
{
    public byte ServiceId { get; }
    public byte Nrc { get; }
    public UdsNrcException(byte serviceId, byte nrc)
        : base($"NRC 0x{nrc:X2} from service 0x{serviceId:X2}")
    {
        ServiceId = serviceId;
        Nrc = nrc;
    }
}

/// <summary>
/// Thrown when UDS request times out or transport fails (not an NRC).
/// </summary>
public sealed class UdsSessionTransportException : UdsSessionException
{
    public UdsSessionTransportException(string message, Exception? inner = null)
        : base(message, inner) { }
}
```

### 7.4 UdsSessionAdapter

File: `Infrastructure/Uds/UdsSessionAdapter.cs` (NEW)

```csharp
internal sealed class UdsSessionAdapter : IUdsSession
{
    private readonly UdsClient _client;

    public UdsSessionAdapter(UdsClient client) => _client = client;

    public async Task<IReadOnlyList<DtcInfo>> ReadDtcInformation(byte statusMask, CancellationToken ct)
    {
        try
        {
            // Service 0x19, sub-function 0x02 (reportDTCByStatusMask)
            var response = await _client.ReadDtcInformationAsync(0x02, statusMask, ct);
            return ParseDtcInfos(response);
        }
        catch (UdsNegativeResponseException ex)
        {
            // ECU 对 ReadDTC 返回 NRC（如 0x10 generalReject）→ 转为 NRC 异常
            throw new UdsNrcException(0x19, (byte)ex.ResponseCode);
        }
        catch (UdsException ex)
        {
            // UDS 超时 / 传输错误 → 转为 session 异常
            throw new UdsSessionTransportException($"ReadDTC failed: {ex.Message}", ex);
        }
    }

    public async Task SendRequestAsync(byte serviceId, byte[]? data, CancellationToken ct)
    {
        try
        {
            await _client.SendRequestAsync(serviceId, data, ct);
        }
        catch (UdsNegativeResponseException ex)
        {
            // Translate Core.Uds exception → HIL.Contracts exception (clean layering)
            throw new UdsNrcException(ex.ServiceId, (byte)ex.ResponseCode);
        }
        catch (UdsException ex)
        {
            // UDS timeout / transport error → convert to session exception
            // (preserves the inner exception for diagnostics)
            throw new UdsSessionTransportException($"UDS request failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Parse DTC status mask response (ISO 14229-1 §11.3.5).
    /// Response format: [availabilityMask, DTC3 DTC2 DTC1, statusOfDTC, ...]
    /// Each DTC entry is 4 bytes: 3-byte DTC (big-endian) + 1-byte status.
    /// DtcCode is 2-byte: high byte = response[i], low byte = response[i+1].
    /// </summary>
    private static IReadOnlyList<DtcInfo> ParseDtcInfos(byte[] response)
    {
        var result = new List<DtcInfo>();
        if (response.Length < 5) return result;

        for (int i = 1; i + 3 < response.Length; i += 4)
        {
            ushort code = (ushort)((response[i] << 8) | response[i + 1]);
            byte status = response[i + 3];
            result.Add(new DtcInfo(code, status));
        }
        return result;
    }
}
```

### 7.5 AssertDtcStepExecutor

File: `Core/HIL/StepExecutor/AssertDtcStepExecutor.cs` (NEW)

```csharp
internal sealed class AssertDtcStepExecutor : IStepExecutor
{
    private readonly IUdsSession _uds;

    public AssertDtcStepExecutor(IUdsSession uds) => _uds = uds;
    public TestCaseStepKind Kind => TestCaseStepKind.AssertDtc;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, Contracts.IAssertionContext ctx, CancellationToken ct)
    {
        var p = (AssertDtcStep)step.Parameters;
        try
        {
            var dtcs = await _uds.ReadDtcInformation(0xFF, ct);

            if (p.DtcCode is null)
            {
                // Any DTC present?
                bool anyActive = dtcs.Any(d => (d.Status & 0x01) != 0 || (d.Status & 0x04) != 0);
                return p.ExpectPresent
                    ? (anyActive
                        ? new StepResult(0, step.Kind, step.Label, StepStatus.Passed, "at least one DTC present", null, null, 0)
                        : new StepResult(0, step.Kind, step.Label, StepStatus.Failed, "no DTC present", "0", ">=1", 0))
                    : (anyActive
                        ? new StepResult(0, step.Kind, step.Label, StepStatus.Failed, "unexpected DTC present", ">=1", "0", 0)
                        : new StepResult(0, step.Kind, step.Label, StepStatus.Passed, "no DTC present", "0", "0", 0));
            }

            // 用 Any 而非 FirstOrDefault — 避免 default(DtcInfo).Code == 0 误匹配 DTC 0x0000
            bool isActive = dtcs.Any(d => d.Code == p.DtcCode.Value
                && ((d.Status & 0x01) != 0 || (d.Status & 0x04) != 0));

            return p.ExpectPresent
                ? (isActive
                    ? new StepResult(0, step.Kind, step.Label, StepStatus.Passed, $"DTC 0x{p.DtcCode:X4} present", null, null, 0)
                    : new StepResult(0, step.Kind, step.Label, StepStatus.Failed, $"DTC 0x{p.DtcCode:X4} not found", "absent", "present", 0))
                : (isActive
                    ? new StepResult(0, step.Kind, step.Label, StepStatus.Failed, $"DTC 0x{p.DtcCode:X4} unexpectedly present", "present", "absent", 0)
                    : new StepResult(0, step.Kind, step.Label, StepStatus.Passed, $"DTC 0x{p.DtcCode:X4} absent", null, null, 0));
        }
        catch (UdsSessionException ex)
        {
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed, $"UDS error: {ex.Message}", null, null, 0);
        }
    }
}
```

### 7.6 AssertNrcStepExecutor

File: `Core/HIL/StepExecutor/AssertNrcStepExecutor.cs` (NEW)

```csharp
internal sealed class AssertNrcStepExecutor : IStepExecutor
{
    private readonly IUdsSession _uds;

    public AssertNrcStepExecutor(IUdsSession uds) => _uds = uds;
    public TestCaseStepKind Kind => TestCaseStepKind.AssertNrc;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, Contracts.IAssertionContext ctx, CancellationToken ct)
    {
        var p = (AssertNrcStep)step.Parameters;
        try
        {
            await _uds.SendRequestAsync(p.ServiceId, null, ct);
            // Positive response (no exception) → we expected NRC → fail
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"Expected NRC 0x{p.ExpectedNrc:X2} but got positive response for service 0x{p.ServiceId:X2}",
                actual: "positive response", expected: $"NRC 0x{p.ExpectedNrc:X2}", 0);
        }
        catch (UdsNrcException ex)
        {
            bool nrcMatches = ex.Nrc == p.ExpectedNrc;
            return new StepResult(0, step.Kind, step.Label,
                nrcMatches ? StepStatus.Passed : StepStatus.Failed,
                nrcMatches ? $"NRC 0x{p.ExpectedNrc:X2} received as expected"
                           : $"NRC mismatch: got 0x{ex.Nrc:X2}, expected 0x{p.ExpectedNrc:X2}",
                actual: $"0x{ex.Nrc:X2}", expected: $"0x{p.ExpectedNrc:X2}", 0);
        }
        catch (UdsSessionException ex)
        {
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"UDS error (not NRC): {ex.Message}", null, null, 0);
        }
    }
}
```

### 7.7 AssertResponseTimeStepExecutor

File: `Core/HIL/StepExecutor/AssertResponseTimeStepExecutor.cs` (NEW)

**Note**: Uses frame-level wall-clock timing (existing `AssertResponseTimeStep.ReqId/RespId`). This is a bus-level measurement that works on both trace and hardware channels — NOT UDS-specific.

**Known limitation**: Sends an empty-payload CAN frame (DLC=0). This tests bus-level timing for ECUs that respond to the request CAN ID regardless of payload. If the ECU requires a specific payload (e.g., a UDS SID) to respond, the test will time out. Future enhancement: add optional `byte[]? Data` field to `AssertResponseTimeStep`.

```csharp
internal sealed class AssertResponseTimeStepExecutor : IStepExecutor
{
    public TestCaseStepKind Kind => TestCaseStepKind.AssertResponseTime;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, Contracts.IAssertionContext ctx, CancellationToken ct)
    {
        var p = (AssertResponseTimeStep)step.Parameters;

        // 关键：先订阅再发送，避免 ECU 快响应（<1ms）在订阅注册前到达导致丢帧
        var tcs = new TaskCompletionSource<CanFrame>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        using var sub = ctx.SubscribeDecodedFrames(frame =>
        {
            if (frame.Frame.Id.Raw == p.RespId.Raw)
                tcs.TrySetResult(frame.Frame);
        });

        // ⚠️ 计时器必须在 SendFrameAsync 之前启动，否则发送延迟不被计入
        // 同时 cts.CancelAfter 与 Stopwatch 同步启动，确保超时判断与测量一致
        var sw = System.Diagnostics.Stopwatch.StartNew();
        cts.CancelAfter(p.MaxMs);

        // Send request frame（订阅已就绪 + 计时器已启动后才发送）
        var sendResult = await ctx.SendFrameAsync(
            new CanFrame(p.ReqId, ReadOnlyMemory<byte>.Empty, FrameFlags.None, default, default), ct);
        if (!sendResult.IsSuccess)
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"Failed to send request: {sendResult.Error?.Message}", null, null, 0);

        using var registration = cts.Token.Register(() => tcs.TrySetCanceled());
        try
        {
            await tcs.Task.ConfigureAwait(false);
            sw.Stop();
            bool withinTime = sw.ElapsedMilliseconds <= p.MaxMs;
            return new StepResult(0, step.Kind, step.Label,
                withinTime ? StepStatus.Passed : StepStatus.Failed,
                withinTime ? $"Response in {sw.ElapsedMilliseconds}ms"
                           : $"Response too slow: {sw.ElapsedMilliseconds}ms > {p.MaxMs}ms",
                actual: sw.ElapsedMilliseconds.ToString(), expected: $"<= {p.MaxMs}ms", 0);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"No response from 0x{p.RespId.Raw:X} within {p.MaxMs}ms",
                null, expected: $"<= {p.MaxMs}ms", 0);
        }
    }
}
```

---

## 8. BLF File Support

### 8.1 File: `Infrastructure/Channel/TraceDrivenChannel.cs` (add method)

⚠️ 修复：`BlfParser.Parse(stream, ct)` 不存在。实际 API 为 `BlfParser.ParseAsync(stream, options, logger, ct)`（`Core/Replay/BlfParser.cs:35`），需要 `ReplayOptions` 参数。

```csharp
public void LoadBlf(string path, CancellationToken ct = default)
{
    ObjectDisposedException.ThrowIf(_state == 2, this);
    if (IsConnected)
        throw new InvalidOperationException("Cannot load trace while playing. Disconnect first.");
    if (!File.Exists(path))
        throw new FileNotFoundException("BLF trace file not found.", path);

    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    // 使用 ReplayOptions.Default（200 MB，与 AscParser 一致）
    var frames = BlfParser.ParseAsync(stream, ReplayOptions.Default, logger: null, ct)
        .GetAwaiter().GetResult();

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

### 8.2 CLI Syntax (updated)

```
peakcan-hil --dbc path.dbc --trace path.asc|path.blf --suite tests.json [--output results.trx|results.xml] [--format console|trx|junit]
```

---

## 9. WriteAsync Loopback

### 9.1 File: `Infrastructure/Channel/TraceDrivenChannel.cs` (modify WriteAsync)

⚠️ 修复 1：原方案 `CreateBounded<CanFrame>(1000)` + `TryWrite` 在 channel 满时静默丢帧。改用 `DropOldest` 与 `HILAssertionContext._frameChannel` 保持一致。

⚠️ 修复 2（死锁）：trace 播放完毕时 `OnTick` 停止定时器（`TraceDrivenChannel.cs:177-181`：`_timer?.Change(Timeout.Infinite, ...)`）。如果 `ProcessLoopback` 仅在 `OnTick` 内调用，则定时器停止后 `WriteAsync` 写入的 loopback 帧将**永久滞留**在 channel 中，导致后续 `WaitForFrame` 永久超时。

**设计选择**：loopback 通过 bounded channel **异步化**（非同步调用 `FrameReceived`），避免 `WriteAsync` 在 timer 线程上同步触发 `FrameReceived` 导致的重入问题（OnTick → ProcessLoopback → FrameReceived → subscriber → WriteAsync → 重入 OnTick 内部）。

```csharp
private readonly Channel<CanFrame> _loopbackChannel = Channel.CreateBounded<CanFrame>(
    new BoundedChannelOptions(1000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleWriter = true,
        SingleReader = true,
    });

public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
{
    // Sprint 3: loopback mode — sent frames become received frames
    _loopbackChannel.Writer.TryWrite(frame);

    // 立即同步排空 loopback 帧
    // 设计选择：loopback 帧始终在 WriteAsync 调用者线程上同步发射，不依赖 OnTick
    // 原因：
    //   (1) 避免 OnTick 停止后帧滞留（trace 播完后定时器停止，OnTick 不再触发）
    //   (2) 测试引擎单线程顺序执行步骤，WriteAsync 不会自身并发
    ProcessLoopbackInternal();

    return ValueTask.FromResult(Result<Unit>.Ok(default));
}

// ⚠️ 线程安全：FrameReceived 可能被两个线程同时 invoke：
//   (a) WriteAsync 调用者线程（测试线程）→ ProcessLoopbackInternal
//   (b) OnTick 的 ThreadPool timer 线程 → trace 帧发射（原有逻辑）
// 两者共用 _loopbackLock 确保 FrameReceived 只在单线程上触发
// （HILAssertionContext.OnFrame 的 _currentTimestamp 赋值和 FrameReceivedSubscription
//   的 delegate list 不是线程安全的）
private readonly object _loopbackLock = new();

private void ProcessLoopbackInternal()
{
    lock (_loopbackLock)
    {
        while (_loopbackChannel.Reader.TryRead(out var frame))
        {
            FrameReceived?.Invoke(frame);
        }
    }
}

// OnTick 中发射 trace 帧时也需要获取 _loopbackLock
// （在原有 OnTick 的 foreach (var frame in _emitBuffer) FrameReceived?.Invoke(frame) 外层加 lock）
```

**说明**：
- loopback 帧始终在 WriteAsync 内同步排空（不依赖 OnTick）
- trace 帧仍由 OnTick 异步发射（原有逻辑不变）
- `_loopbackLock` 确保 FrameReceived 不会被两个线程同时 invoke
  （HILAssertionContext.OnFrame 使用 Channel<T>.TryWrite 是线程安全的，但 `_currentTimestamp`
   赋值和 FrameReceivedSubscription 的 delegate list 不是线程安全的）
- OnTick 中发射 trace 帧的 `foreach (var frame in _emitBuffer) FrameReceived?.Invoke(frame)`
  也必须在 `lock (_loopbackLock)` 内执行，与 ProcessLoopbackInternal 互斥
- ⚠️ lock 持有时间 < 1ms（仅 TryRead 循环 + FrameReceived invoke）；FrameReceived 回调必须非阻塞（IFrameSink 契约），不会导致 timer tick 堆积

---

## 10. Stage B — PeakCanAssertionContext

### 10.1 File: `Infrastructure/HIL/PeakCanAssertionContext.cs` (NEW)

`PeakCanAssertionContext` reuses the **exact same thread model** as Sprint 2's `HILAssertionContext`:
- `OnFrame` → `TryWrite` to `Channel<CanFrame>` (non-blocking, DropOldest)
- Consumer thread: DBC decode → signal cache → subscriber notification
- `Dispose`: unsubscribe → drain channel (100ms) → cancel consumer → wait (2s)

The **only difference** from `HILAssertionContext`: `SendFrameAsync` delegates to `_channel.WriteAsync(frame, ct)` instead of being a no-op.

```csharp
internal sealed class PeakCanAssertionContext : IAssertionContext, IHasRecentFrames, IDisposable
{
    private readonly PeakCanChannel _channel;
    private readonly IDbcLookup _dbcLookup;
    private readonly Channel<CanFrame> _frameChannel;
    private readonly CancellationTokenSource _consumerCts = new();
    private readonly Task _consumerTask;
    private readonly ConcurrentDictionary<string, (double Value, double TimestampUs)> _signalCache = new();
    private volatile double _currentTimestamp;
    private readonly IDisposable _frameSubscription;
    private ImmutableList<Action<DecodedFrame>> _subscribers = ImmutableList<Action<DecodedFrame>>.Empty;

    public PeakCanAssertionContext(PeakCanChannel channel, IDbcLookup dbcLookup)
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

    public double CurrentTimestamp => _currentTimestamp;

    // 以下 5 个方法实现与 HILAssertionContext 完全一致（复用 Sprint 2 §4.3 线程模型）
    // 差异仅：SendFrameAsync 委托给 _channel.WriteAsync（非 no-op）
    public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame) { /* 见 HILAssertionContext.cs:44 */ }
    public double? GetSignalValue(string signalName, int maxAgeMs = 5000) { /* 见 HILAssertionContext.cs:67 */ }
    public async ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct) => await _channel.WriteAsync(frame, ct);
    public void Dispose() { /* drain: unsubscribe → 100ms drain → cancel → 2s wait；见 HILAssertionContext.cs:89 */ }
    private void OnFrame(CanFrame frame) { /* TryWrite to _frameChannel；见 HILAssertionContext.cs:72 */ }
    private async Task ConsumerLoop(CancellationToken ct) { /* decode → signal cache → notify；见 HILAssertionContext.cs:95 */ }
}
```

### 10.2 SendFrameAsync Pre-condition

`_channel.IsConnected` must be `true`. Caller (TestSuiteEngine via fixture `SetupAsync`) is responsible for calling `ConnectAsync` before test execution.

### 10.3 DbcLookupKey extraction

Both `HILAssertionContext` and `PeakCanAssertionContext` share `DbcLookupKey.ToLookupKey`:

```csharp
// Infrastructure/HIL/DbcLookupKey.cs (NEW)
internal static class DbcLookupKey
{
    internal static uint ToLookupKey(uint rawId, bool isExtended) =>
        isExtended ? rawId | 0x80000000u : rawId;
}
```

---

## 11. Stage B — CLI Hardware Mode

### 11.1 Updated CliArgs

File: `Cli/CliArgs.cs` (modify)

⚠️ C# 规则：有默认值的参数之后不能跟无默认值的参数（CS1737）。`SuitePath` 必须在 `TracePath` 之前。

```csharp
public sealed record CliArgs(
    string DbcPath,
    string SuitePath,                // 无默认值，必须在 TracePath 之前
    string? TracePath = null,        // nullable: --hw 模式下不使用 trace
    string? OutputPath = null,
    string Format = "console",
    // Stage B additions:
    string? HardwareChannel = null,  // e.g. "USB1" — if set, use real hardware
    uint UdsRequestId = 0x7DF,
    uint UdsResponseId = 0x7E8);
```

### 11.1b Updated CliArgsParser

File: `Cli/CliArgsParser.cs` (modify)

⚠️ 原有校验 `if (trace is null) throw` 在硬件模式下会误抛。改为互斥校验：

```csharp
public static CliArgs Parse(string[] args)
{
    string? dbc = null, trace = null, suite = null, output = null, format = "console";
    string? hw = null;
    uint udsReq = 0x7DF, udsResp = 0x7E8;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--dbc": dbc = args[++i]; break;
            case "--trace": trace = args[++i]; break;
            case "--suite": suite = args[++i]; break;
            case "--output": output = args[++i]; break;
            case "--format": format = args[++i]; break;
            case "--hw": hw = args[++i]; break;
            // 支持十进制和 0x 前缀十六进制（与 StepParametersFactory.StripHexPrefix 一致）
            case "--uds-req": udsReq = ParseUdsId(args[++i]); break;
            case "--uds-resp": udsResp = ParseUdsId(args[++i]); break;
            case "--help":
            case "-h":
                PrintHelp();
                Environment.Exit(0);
                break;
        }
    }

    if (dbc is null) throw new ArgumentException("Missing required --dbc argument.");
    if (suite is null) throw new ArgumentException("Missing required --suite argument.");
    if (trace is null && hw is null)
        throw new ArgumentException("Must specify --trace or --hw.");
    if (trace is not null && hw is not null)
        throw new ArgumentException("Cannot use --trace and --hw simultaneously.");

    return new CliArgs(dbc, suite, trace, output, format, hw, udsReq, udsResp);
}

/// <summary>
/// 解析 UDS CAN ID 字符串（支持十进制和 0x 前缀十六进制）。
/// </summary>
private static uint ParseUdsId(string raw)
{
    if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        return Convert.ToUInt32(raw[2..], 16);
    return Convert.ToUInt32(raw);
}
```

### 11.2 Mode selection

- `--trace path.asc` (no `--hw`): Sprint 2 trace-replay mode (TraceDrivenChannel)
- `--hw USB1` (no `--trace`): Sprint 3 hardware mode (PeakCanChannel)
- Both / Neither: `InvalidOperationException`

### 11.3 Channel Factory (HeadlessHostBuilder)

```csharp
if (cli.HardwareChannel is not null && cli.TracePath is not null)
    throw new InvalidOperationException("Cannot use --trace and --hw simultaneously");
if (cli.HardwareChannel is null && cli.TracePath is null)
    throw new InvalidOperationException("Must specify --trace or --hw");

if (cli.HardwareChannel is not null)
{
    var handle = ParseChannelHandle(cli.HardwareChannel);
    builder.Services.AddSingleton<ICanChannel>(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<PeakCanChannel>>();
        return new PeakCanChannel(new ChannelId(handle), logger);
    });
}
else
{
    // Trace-replay mode (Sprint 2 + BLF support)
    builder.Services.AddSingleton<ICanChannel>(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<TraceDrivenChannel>>();
        var ch = new TraceDrivenChannel(new ChannelId(1), logger);
        // 根据扩展名分派 ASC / BLF 解析
        if (Path.GetExtension(cli.TracePath).Equals(".blf", StringComparison.OrdinalIgnoreCase))
            ch.LoadBlf(cli.TracePath);
        else
            ch.LoadAscii(cli.TracePath);
        return ch;
    });
}
```

> **注**：`ParseChannelHandle` 是 `HeadlessHostBuilder` 的**类级别 `private static` 方法**（在 `Build` 方法之外定义），不在 if-else 内部。以下是该类的方法定义：

```csharp
/// <summary>
/// 将 "USB1".."USB16" 字符串解析为 PCAN-Basic 通道 handle（0x51..0x60）。
/// 参照 PeakChannelEnumerator.UsbHandles（0x51 = PCAN_USBBUS1）。
/// ⚠️ 仅支持 USB1..USB16；PCI/ISA/DNG 通道不在当前项目范围（PeakChannelEnumerator 仅枚举 USB）。
/// </summary>
private static ushort ParseChannelHandle(string hw)
{
    if (hw.StartsWith("USB", StringComparison.OrdinalIgnoreCase)
        && ushort.TryParse(hw[3..], out var n)
        && n is >= 1 and <= 16)
    {
        return (ushort)(0x50 + n);  // USB1 → 0x51, USB2 → 0x52, ...
    }
    throw new ArgumentException($"Invalid hardware channel: {hw}. Expected USB1..USB16.", nameof(hw));
}
```

### 11.4 AssertionContext Factory

```csharp
builder.Services.AddSingleton<IAssertionContext>(sp =>
{
    var channel = sp.GetRequiredService<ICanChannel>();
    var dbc = sp.GetRequiredService<IDbcLookup>();

    if (channel is TraceDrivenChannel)
        return new HILAssertionContext(channel, dbc);
    if (channel is PeakCanChannel peakCh)
        return new PeakCanAssertionContext(peakCh, dbc);

    throw new InvalidOperationException($"Unsupported channel type: {channel.GetType().Name}");
});
```

### 11.5 UDS Registration (Stage B)

⚠️ `CanIdConfig` 是 `sealed record` with `{ get; init; }` 属性（非位置参数 record）。属性名是 `IsExtendedFrame`（非 `IsExtended`）。

```csharp
builder.Services.AddSingleton<IsoTpLayer>(sp =>
{
    var channel = sp.GetRequiredService<ICanChannel>();
    var config = new CanIdConfig
    {
        RequestId = cli.UdsRequestId,
        ResponseId = cli.UdsResponseId,
        IsExtendedFrame = false,
    };
    // ⚠️ 构造函数签名是 Func<CanFrame, Task>，但 WriteAsync 返回 ValueTask<Result<Unit>>
    // 必须用 async lambda 避免 Task<Result<Unit>> → Task 的隐式转换编译错误
    return new IsoTpLayer(config,
        async frame => { await channel.WriteAsync(frame, default).ConfigureAwait(false); });
});
builder.Services.AddSingleton<UdsClient>(sp =>
{
    var isoTp = sp.GetRequiredService<IsoTpLayer>();
    return new UdsClient(isoTp);
});
builder.Services.AddSingleton<IUdsSession>(sp =>
{
    var client = sp.GetRequiredService<UdsClient>();
    return new UdsSessionAdapter(client);
});
```

### 11.6 ISO-TP Frame Bridge (Stage B — 关键)

⚠️ **架构约束**：`IsoTpLayer` **不会**自动订阅 `ICanChannel.FrameReceived`。它只暴露 `ProcessFrame(CanFrame)` 作为帧接收入口（`IsoTpLayer/ReceiveFlow.cs:26`）。在 App 层由 `IsoTpSinkAdapter : IFrameSink` 桥接 `ChannelRouter` → `ProcessFrame`（`App/Composition/IsoTpSinkAdapter.cs:80`）。

HIL CLI 是 headless 路径，**不使用 `ChannelRouter`**（`PeakCanAssertionContext` 直接订阅 `ICanChannel.FrameReceived`）。因此必须提供自己的桥接，否则 ECU 响应帧永远不会到达 `IsoTpLayer` → `UdsClient` 永远收不到响应 → 所有 UDS 请求超时。

**方案**：注册一个 `HilIsoTpBridge`（Infrastructure 层），订阅 `ICanChannel.FrameReceived` 并调用 `IsoTpLayer.ProcessFrame`。

```csharp
// Infrastructure/HIL/HilIsoTpBridge.cs (NEW)
internal sealed class HilIsoTpBridge : IDisposable
{
    private readonly IsoTpLayer _isoTp;
    private readonly IDisposable _subscription;

    public HilIsoTpBridge(ICanChannel channel, IsoTpLayer isoTp)
    {
        _isoTp = isoTp;
        _subscription = new FrameReceivedSubscription(channel, OnFrame);
    }

    private void OnFrame(CanFrame frame)
    {
        try { _isoTp.ProcessFrame(frame); }
        catch (ArgumentException)
        {
            // ProcessFrame 对畸形帧抛 ArgumentException — 吞掉避免破坏接收路径
            // （与 IsoTpSinkAdapter.OnFrame 一致的防御策略）
            // ⚠️ 不声明 ex 变量：TreatWarningsAsErrors=true 下 CS0168 会变成编译错误
        }
    }

    public void Dispose() => _subscription.Dispose();
}
```

注册（仅在硬件模式下）：
```csharp
if (cli.HardwareChannel is not null)
{
    builder.Services.AddSingleton<HilIsoTpBridge>(sp =>
    {
        var channel = sp.GetRequiredService<ICanChannel>();
        var isoTp = sp.GetRequiredService<IsoTpLayer>();
        return new HilIsoTpBridge(channel, isoTp);
    });
}
```

**注意**：`HilIsoTpBridge` 与 `PeakCanAssertionContext` **并行订阅**同一个 `ICanChannel.FrameReceived` — 两者都会收到每帧（DBC 解码 + ISO-TP 重组都需要原始帧）。`PeakCanAssertionContext` 内部已有 `FrameReceivedSubscription` 封装，可复用。

### 11.7 Dispose 顺序（Stage B）

⚠️ **关键**：DI 容器按注册**逆序** Dispose。必须确保 Dispose 顺序避免悬挂请求或帧丢失：

**正确顺序**（先注册后 Dispose）：
```
PeakCanAssertionContext  → 先 Dispose（停止消费 frameChannel，drain 超时 100ms）
HilIsoTpBridge           → 取消 FrameReceived 订阅
UdsClient                → _isoTp.MessageReceived -= OnMessageReceived
IsoTpLayer               → Dispose reassembly state
ICanChannel              → 最后 Dispose（Disconnect + 释放 PCAN handle）
```

**注册顺序**（在 HeadlessHostBuilder 中按以下顺序 `AddSingleton`）：
```csharp
// 1. ICanChannel（最后 Dispose → 最先注册）
// 2. IsoTpLayer
// 3. UdsClient
// 4. HilIsoTpBridge
// 5. PeakCanAssertionContext（最先 Dispose → 最后注册）
```

**理由**：
- `PeakCanAssertionContext.Dispose` 需要 `ICanChannel.FrameReceived` 仍然活跃（drain 阶段可能还有帧到达）
- `HilIsoTpBridge.Dispose` 取消订阅后，`IsoTpLayer` 不再收到新帧
- `UdsClient.Dispose` 取消 `MessageReceived` 订阅后，`IsoTpLayer` 的 `MessageReceived` 事件不再触发
- 最终 `ICanChannel.Dispose` 释放硬件时，所有消费者已停止

---

## 12. Stage C — FramesAroundFailure

### 12.0 New Interface: `IHasRecentFrames`

File: `Core/HIL/Contracts/IHasRecentFrames.cs` (NEW)

```csharp
/// <summary>
/// Implemented by assertion contexts that maintain a recent-frames ring buffer.
/// Used by TestSuiteEngine to capture FramesAroundFailure on step failure.
/// </summary>
public interface IHasRecentFrames
{
    /// <summary>Snapshot of the ring buffer (copy). Thread-safe.</summary>
    IReadOnlyList<CanFrame> GetRecentFrames();
}
```

`PeakCanAssertionContext` and `HILAssertionContext` both implement `IHasRecentFrames` by wrapping their internal `CircularBuffer<CanFrame>`.

### 12.1 CircularBuffer

File: `Infrastructure/HIL/CircularBuffer.cs` (NEW)

```csharp
internal sealed class CircularBuffer<T> where T : struct
{
    private readonly T[] _buffer;
    private int _head;
    private int _count;
    private readonly object _lock = new();

    public CircularBuffer(int capacity) => _buffer = new T[capacity];

    public void Add(T item)
    {
        lock (_lock)
        {
            _buffer[_head] = item;
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length) _count++;
        }
    }

    public IReadOnlyList<T> Snapshot()
    {
        lock (_lock)
        {
            var result = new T[_count];
            int start = (_head - _count + _buffer.Length) % _buffer.Length;
            for (int i = 0; i < _count; i++)
                result[i] = _buffer[(start + i) % _buffer.Length];
            return result;
        }
    }
}
```

### 12.2 Engine Integration

In `TestSuiteEngine.ExecuteCaseAsync`, when a step fails:
```csharp
if (!stepResults[^1].Passed && stepResults[^1].FramesAroundFailure is null)
{
    if (ctx is IHasRecentFrames hasRecent)
    {
        stepResults[^1] = stepResults[^1] with
        {
            FramesAroundFailure = hasRecent.GetRecentFrames().ToList()
        };
    }
}
```

**Note**: `FramesAroundFailure` is a field on `StepResult` (`IReadOnlyList<CanFrame>?`, `StepResult.cs:15`).

**CircularBuffer 更新位置**：在 **ConsumerLoop 中**更新（与 DBC 解码同步），而非 OnFrame 中。
- 原因：OnFrame 在 channel 帧线程（PeakCanChannel read-loop 线程）上运行，CircularBuffer.Add 用 lock 会阻塞 read-loop
- 实现：ConsumerLoop 从 `_frameChannel` 读出 CanFrame → DBC 解码 → `circularBuffer.Add(frame)` → 通知订阅者
- Buffer 存储 `CanFrame`（原始帧），与 `StepResult.FramesAroundFailure` 类型匹配

Both `HILAssertionContext` and `PeakCanAssertionContext` implement `IHasRecentFrames.GetRecentFrames()` returning `circularBuffer.Snapshot()`.

---

## 13. Stage C — WPF HIL Panel

### 13.1 File Structure

⚠️ **架构约束**：`HeadlessHostBuilder` 在 `PeakCan.Host.Cli` 项目中，而 `PeakCan.Host.App` **不引用** `Cli`（csproj 仅引用 Core + Infrastructure）。因此 `HilRunnerService` 必须放在 **Infrastructure 项目**中（App 引用 Infrastructure），App 的 DI 容器才能注册 `IHilRunnerService`。

`HilRunnerService` 依赖 `HeadlessHostBuilder`（在 Cli 中）→ 需要将 `HeadlessHostBuilder` 提取到 Infrastructure 项目（它只依赖 Core + Infrastructure 类型，可以迁移）。Cli 的 `Program.cs` 调用 `HeadlessHostBuilder.Build` 不受影响（Cli 引用 Infrastructure）。

```
PeakCan.Host.Core/HIL/Contracts/ (NEW):
  IHilRunnerService.cs                 (App → Infrastructure 解耦接口)

PeakCan.Host.Infrastructure/Services/ (NEW):
  HilRunnerService.cs                  (实现 IHilRunnerService)
  HilRunRequest.cs                     (record + ToCliArgs())

PeakCan.Host.Infrastructure/HIL/ (MOVED from Cli):
  HeadlessHostBuilder.cs               (DI 注册逻辑，仅依赖 Core + Infrastructure)

PeakCan.Host.App/
├── Views/HilView.xaml                 (NEW)
├── Views/HilView.xaml.cs              (NEW)
├── ViewModels/HilViewModel.cs         (NEW)
└── ViewModels/TestCaseResultViewModel.cs (NEW)
```

**App 注册**（在 `AppHostBuilder` 中）：
```csharp
builder.Services.AddSingleton<IHilRunnerService, HilRunnerService>();
```

### 13.2 IHilRunnerService (interface in Core)

File: `Core/HIL/Contracts/IHilRunnerService.cs` (NEW)

```csharp
/// <summary>
/// Decouples the WPF App layer from the Cli-layer HilRunnerService.
/// App project references Core but not Cli — this interface is the bridge.
/// </summary>
public interface IHilRunnerService
{
    Task<TestSuiteResult> RunAsync(
        HilRunRequest request,
        IProgress<TestProgress>? progress = null,
        CancellationToken ct = default);
}
```

### 13.3 HilRunRequest (record in Cli)

File: `Cli/Services/HilRunRequest.cs` (NEW)

```csharp
public sealed record HilRunRequest(
    string DbcPath,
    string SuitePath,
    string? TracePath = null,
    string? HardwareChannel = null,
    string Format = "console",
    uint UdsRequestId = 0x7DF,          // 硬件模式下 UDS 请求 CAN ID
    uint UdsResponseId = 0x7E8);        // 硬件模式下 UDS 响应 CAN ID

public static class HilRunRequestExtensions
{
    public static CliArgs ToCliArgs(this HilRunRequest r) => new(
        r.DbcPath,
        r.SuitePath,
        r.TracePath,
        OutputPath: null,
        r.Format,
        r.HardwareChannel,
        r.UdsRequestId,
        r.UdsResponseId);
}
```

### 13.4 HilViewModel (sketch)

```csharp
public sealed partial class HilViewModel : ObservableObject
{
    [ObservableProperty] private string _dbcPath = "";
    [ObservableProperty] private string _suitePath = "";
    [ObservableProperty] private string _tracePath = "";
    [ObservableProperty] private bool _useHardware = false;
    [ObservableProperty] private string _hardwareChannel = "USB1";
    [ObservableProperty] private bool _isRunning = false;
    [ObservableProperty] private double _progress = 0;
    [ObservableProperty] private string _statusMessage = "Ready";

    public ObservableCollection<TestCaseResultViewModel> Results { get; } = new();

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        // ⚠️ 示意代码，非完整实现（省略了 progress 报告、异常处理、结果绑定）
        var runner = App.Services.GetRequiredService<IHilRunnerService>();
        var result = await runner.RunAsync(new HilRunRequest(
            _dbcPath, _suitePath,
            _useHardware ? null : _tracePath,
            _useHardware ? _hardwareChannel : null));
        // TODO: 绑定 result.CaseResults 到 Results 集合
    }

    private bool CanRun() => !_isRunning && !string.IsNullOrEmpty(_suitePath);
    private void OnProgress(TestProgress p) => _progress = p.PercentComplete;
}
```

### 13.5 HilRunnerService (in Infrastructure)

File: `Infrastructure/Services/HilRunnerService.cs` (NEW)

```csharp
public sealed class HilRunnerService : IHilRunnerService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<HilRunnerService> _logger;

    public HilRunnerService(IServiceProvider sp, ILogger<HilRunnerService> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    public async Task<TestSuiteResult> RunAsync(
        HilRunRequest request,
        IProgress<TestProgress>? progress = null,
        CancellationToken ct = default)
    {
        var host = HeadlessHostBuilder.Build(request.ToCliArgs());
        try
        {
            var engine = host.Services.GetRequiredService<TestSuiteEngine>();
            var channel = host.Services.GetRequiredService<ICanChannel>();
            var ctx = host.Services.GetRequiredService<IAssertionContext>();

            var suiteJson = await File.ReadAllTextAsync(request.SuitePath, ct);
            var suite = JsonSerializer.Deserialize<TestSuite>(suiteJson, HILJsonOptions.Default)!;

            await channel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true, ct);
            var result = await engine.ExecuteAsync(suite, ctx, suite.Config, progress, ct);
            await channel.DisconnectAsync(ct);

            return result;
        }
        finally
        {
            if (host is IAsyncDisposable ad) await ad.DisposeAsync();
        }
    }
}
```

---

## 14. File Structure (new/modified)

```
PeakCan.Host.Cli/ (modified):
  JUnitWriter.cs                         (NEW)
  ResultWriter.cs                        (unchanged — TRX writer)
  CliArgs.cs                             (MODIFY — TracePath nullable, add HardwareChannel/UdsRequestId/UdsResponseId)
  CliArgsParser.cs                       (MODIFY — --hw/--uds-req/--uds-resp, nullable trace, ParseChannelHandle)
  Program.cs                             (MODIFY — JUnit output path)
  (HeadlessHostBuilder 移出到 Infrastructure)

PeakCan.Host.Infrastructure/ (moved from Cli):
  HeadlessHostBuilder.cs                 (MOVED — DI 注册逻辑，仅依赖 Core + Infrastructure)

PeakCan.Host.Infrastructure/Services/ (NEW):
  HilRunnerService.cs                    (NEW — 实现 IHilRunnerService，依赖 HeadlessHostBuilder)
  HilRunRequest.cs                       (NEW — record + ToCliArgs())

PeakCan.Host.Core/HIL/Contracts/ (NEW):
  IUdsSession.cs
  DtcInfo.cs
  UdsSessionException.cs              (base: UdsSessionException, UdsSessionTransportException)
  UdsNrcException.cs                  (inherits UdsSessionException)
  IHasRecentFrames.cs
  IHilRunnerService.cs

PeakCan.Host.Core/HIL/StepExecutor/ (NEW):
  ExpectFrameStepExecutor.cs
  AssertDtcStepExecutor.cs
  AssertNrcStepExecutor.cs
  AssertResponseTimeStepExecutor.cs

PeakCan.Host.Core/HIL/Assertions/ (modified):
  AssertionPrimitives.cs                 (ADD WaitForFrameAsync + MatchesMask)

PeakCan.Host.Infrastructure/Channel/ (modified):
  TraceDrivenChannel.cs                  (ADD LoadBlf + WriteAsync loopback + ProcessLoopback)

PeakCan.Host.Infrastructure/HIL/ (NEW):
  PeakCanAssertionContext.cs
  DbcLookupKey.cs
  CircularBuffer.cs
  HilIsoTpBridge.cs                   (subscribes FrameReceived → IsoTpLayer.ProcessFrame)
  Uds/
    UdsSessionAdapter.cs

PeakCan.Host.App/ (NEW):
  Views/HilView.xaml
  Views/HilView.xaml.cs
  ViewModels/HilViewModel.cs
  ViewModels/TestCaseResultViewModel.cs
  (App 通过 IHilRunnerService 接口调用，实现在 Infrastructure 中)

PeakCan.Host.App/Composition/ (modified):
  AppHostBuilder.cs                      (ADD — services.AddSingleton<IHilRunnerService, HilRunnerService>())

tests/PeakCan.Host.Cli.Tests/ (NEW):
  JUnitWriterTests.cs

tests/PeakCan.Host.Core.Tests/HIL/StepExecutor/ (NEW):
  ExpectFrameStepExecutorTests.cs
  AssertDtcStepExecutorTests.cs
  AssertNrcStepExecutorTests.cs
  AssertResponseTimeStepExecutorTests.cs

tests/PeakCan.Host.Core.Tests/HIL/Assertions/ (modified):
  AssertionPrimitivesTests.cs            (ADD WaitForFrameAsync tests)

tests/PeakCan.Host.Infrastructure.Tests/ (modified):
  TraceDrivenChannelTests.cs             (ADD BLF + loopback tests)
  PeakCanAssertionContextTests.cs        (NEW — Inc 8)
  HilIsoTpBridgeTests.cs                 (NEW — Inc 9b)

tests/PeakCan.Host.Core.Tests/HIL/ (NEW):
  CircularBufferTests.cs                 (NEW — Inc 10)

tests/PeakCan.Host.Cli.Tests/ (modified):
  CliArgsParserTests.cs                  (ADD --hw / --uds-req / --uds-resp / nullable trace)
  HilRunRequestTests.cs                  (NEW — ToCliArgs mapping)

tests/PeakCan.Host.Cli.Tests/ (NEW):
  HardwareModeIntegrationTests.cs        (NEW — Inc 13, Skip on CI without HW)

tests/PeakCan.Host.Core.Tests/Fakes/ (NEW):
  FakeIUdsSession.cs
```

---

## 15. TDD Increments

| Increment | Phase | Component | Tests |
|---|---|---|---|
| Inc 1 | A | JUnit XML Writer | Valid XML, passed/failed cases, empty suite, time formatting |
| Inc 2 | A | WaitForFrame (ExpectFrameStepExecutor + WaitForFrameAsync) | Exact ID match, mask match, mask mismatch, timeout, null mask = match all, cancelled |
| Inc 3 | A | AssertResponseTime | Fast response pass, slow response fail, no response timeout, send failure, cancellation |
| Inc 4 | A | UDS AssertDtc (mock IUdsSession) | DTC present (expect present), DTC absent (expect present), DTC absent (expect absent), DTC present (expect absent), null DtcCode (any), UDS session error |
| Inc 5 | A | UDS AssertNrc (mock IUdsSession) | Correct NRC pass, wrong NRC fail, positive response fail, UDS session transport error |
| Inc 6 | A | BLF Support | Load BLF file, playback frames, mixed ASC/BLF extension detection |
| Inc 7 | A | WriteAsync Loopback | Write frame → receive frame, stimulus-response cycle, DropOldest under overflow |
| Inc 8 | B | PeakCanAssertionContext | Construction, OnFrame→TryWrite, consumer decode, signal cache, SendFrameAsync delegates, Dispose drain |
| Inc 9 | B | CLI Hardware Mode | `--hw` switches channel, `--trace`+`--hw` error, `--uds-req`/`--uds-resp` parsing |
| Inc 9b | B | HilIsoTpBridge | FrameReceived → ProcessFrame forwarding, malformed frame tolerance, dispose unsubscribes |
| Inc 9c | B | HilRunRequest.ToCliArgs() | Trace mode mapping, hardware mode mapping, null trace in hw mode |
| Inc 9d | B | ParseChannelHandle | USB1 → 0x51, USB16 → 0x60, invalid input rejection |
| Inc 9e | B | CliArgsParser UDS args | --uds-req 0x7DF (hex), --uds-req 2013 (decimal), --uds-resp parsing |
| Inc 10 | C | CircularBuffer | Add below capacity, add overflow, snapshot order, thread-safety |
| Inc 11 | C | FramesAroundFailure | Engine captures ring buffer on failure |
| Inc 12 | C | WPF HIL Panel | Manual: load suite, run, view results |
| Inc 13 | B/C | Integration | Hardware mode end-to-end (skipped on CI without HW), trace mode with 10 executors |
| **Total** | | | **~48 tests** |

---

## 16. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| UdsClient depends on IsoTpLayer + channel | UDS executors can't be unit-tested with concrete client | Introduce `IUdsSession` interface + `UdsSessionAdapter`; inject mock in tests |
| BlfParser API mismatch | Inc 6 blocked | Use existing `BlfParser.ParseAsync(stream, options, logger, ct)` with `ReplayOptions`; pre-flight verify |
| WriteAsync loopback race | Frame emitted before subscriber ready | Use `DropOldest` bounded channel; `ProcessLoopbackInternal` in `WriteAsync` synchronously drains; `_loopbackLock` protects against OnTick concurrency |
| JUnit XML schema variation | CI system may expect specific attributes | Follow Jenkins/JUnit4 schema (no namespace); add `time`, `skipped`, `failures` attributes |
| PCAN hardware not available on CI | Hardware tests can't run in CI | Hardware tests marked `[Trait("category","integration")] Skip="Requires PCAN hardware"` |
| PeakCanChannel read-loop vs consumer thread | Thread-safety | Same proven pattern as Sprint 2: `OnFrame` non-blocking, `Channel<T>` decouples threads |
| WPF UI thread marshaling | Cross-thread collection update | `HilViewModel` uses `Dispatcher` for `ObservableCollection` updates |
| DTC parsing assumes 4-byte entries | ISO 14229-1 §11.3.5 format variance | Document assumption; DtcCode is 2-byte (high+mid) |
| IsoTpLayer 不订阅 FrameReceived | UDS 响应无法到达 UdsClient | HilIsoTpBridge 显式订阅并调用 ProcessFrame（§11.6） |
| UDS 0x78 (responsePending) 被 UdsClient 吞掉 | ECU pending 后发正响应时 AssertNrc 误判 | 已知限制：UdsClient 对 0x78 延长 P2* 超时，不抛异常 |
| LoadBlf sync-over-async 阻塞 | ParseAsync 内部 await 可能死锁 | 与既有 LoadAscii 一致的低风险模式；WPF 路径通过 async RunAsync 避免阻塞 UI |
| Loopback 定时器停止后帧滞留 | 永久超时 | WriteAsync 内同步排空（不依赖 OnTick），§9.1 |
| Loopback 与 OnTick 并发 invoke FrameReceived | HILAssertionContext.OnFrame 的 `_currentTimestamp` 竞争 + delegate list 竞争 | `_loopbackLock` 同时保护 ProcessLoopbackInternal 和 OnTick 的 trace 帧发射（§9.1） |
| ParseChannelHandle 仅支持 USB1..USB16 | PCI/ISA 用户无法使用 | 文档标注范围限制；PeakChannelEnumerator 仅枚举 USB |
| Dispose 顺序不当导致悬挂请求 | UDS 请求丢失 / 帧滞留 | 规定注册顺序 = channel → isoTp → uds → bridge → context（§11.7） |
| HilRunnerService 跨项目引用 | App 不引用 Cli | IHilRunnerService 接口在 Core，实现在 Cli（§13.1） |

---

## 17. Design Decision Record

| ID | Decision | Rationale |
|---|---|---|
| S3-D1 | JUnit XML over TRX for CI compatibility | Jenkins/Azure DevOps/GitLab all consume JUnit; TRX is VS-only |
| S3-D2 | WaitForFrame reuses existing ExpectFrameStep record | Record already has discriminator + factory mapping; no new enum value needed |
| S3-D3 | WaitForFrame uses AND-mask (automotive don't-care) | Industry convention; more flexible than exact match |
| S3-D4 | UDS executors depend on IUdsSession, not concrete UdsClient | Decouples HIL Core from IsoTp dependency chain; enables mock-based unit tests |
| S3-D5 | UdsSessionAdapter parses raw byte[] → DtcInfo in Adapter | Adapter owns wire-format parsing; executor stays pure logic |
| S3-D6 | Adapter converts ALL UdsException → UdsSessionException hierarchy | Clean layering: executors catch UdsSessionException/UdsNrcException/UdsSessionTransportException in HIL Contracts — zero dependency on Core.Uds |
| S3-D6b | HilIsoTpBridge subscribes FrameReceived → IsoTpLayer.ProcessFrame | IsoTpLayer doesn't auto-subscribe; HIL CLI (no ChannelRouter) needs explicit bridge |
| S3-D7 | AssertResponseTime uses frame-level wall-clock (ReqId→RespId) | Bus-level measurement works on both trace and hardware channels |
| S3-D8 | BLF uses existing BlfParser with ReplayOptions | No new parser; mirrors LoadAscii pattern |
| S3-D9 | WriteAsync loopback via DropOldest bounded channel | Matches HILAssertionContext._frameChannel; explicit overflow semantics |
| S3-D10 | PeakCanAssertionContext mirrors HILAssertionContext thread model | Proven pattern from Sprint 2; only SendFrameAsync differs |
| S3-D11 | DbcLookupKey extracted to shared static | Both contexts use same bit-31 conversion; DRY |
| S3-D12 | CircularBuffer with lock | Simpler than lock-free; 50-frame buffer, low contention |
| S3-D13 | FramesAroundFailure captured at engine level | Engine has step-failure context; context provides ring buffer |
| S3-D14 | CLI --hw / --trace mutual exclusion | Clear mode separation; prevents confused configurations |
| S3-D15 | WPF panel builds scoped host per run | Isolates test execution; clean dispose after each run |
| S3-D16 | UdsClient as singleton | Thread-safe via internal SemaphoreSlim; serializes requests |
| S3-D17 | JUnit XML uses no XML namespace | Standard JUnit schema is namespace-less; Jenkins/Azure DevOps/GitLab compatible |
| S3-D18 | Stage A/B/C phased delivery | Offline capability (Stage A) independently shippable without hardware |
| S3-D19 | Loopback frames processed synchronously in WriteAsync + shared lock | Avoids timer-stop deadlock; `_loopbackLock` 同时保护 loopback 帧（WriteAsync）和 trace 帧（OnTick）的 FrameReceived 发射，确保单线程 invoke |
| S3-D20 | Dispose order: context → bridge → uds → isoTp → channel | Reverse registration order ensures no dangling subscriptions or frame loss |
| S3-D21 | IHilRunnerService decouples App from Cli | App doesn't reference Cli project; interface in Core, impl in Cli |

---

## 18. Pre-Flight Verification Checklist

Before implementation, verify these existing API signatures:

| Dependency | File | Verify |
|---|---|---|
| `BlfParser.ParseAsync` | `Core/Replay/BlfParser.cs:35` | `(Stream, ReplayOptions, ILogger?, CancellationToken)` → `Task<IReadOnlyList<ReplayFrame>>` |
| `ReplayOptions` | `Core/Replay/ReplayOptions.cs:19` | `record ReplayOptions(long MaxFileSizeBytes)` |
| `UdsClient` constructor | `Core/Uds/UdsClient.cs:68` | `UdsClient(IsoTpLayer isoTp, UdsTimer? timer = null, ILogger<UdsSession>? sessionLogger = null)` |
| `UdsClient.ReadDtcInformationAsync` | `Core/Uds/UdsClient/DataIOFlow.cs:51` | `(byte subFunc, byte mask, CancellationToken)` → `Task<byte[]>` |
| `UdsClient.SendRequestAsync` | `Core/Uds/UdsClient/TransportFlow.cs:30` | `(byte serviceId, byte[]? data, CancellationToken)` → `Task<byte[]>` |
| `UdsNegativeResponseException` | `Core/Uds/UdsException.cs:15` | `.ResponseCode` (UdsNegativeResponseCode), `.ServiceId` |
| `IsoTpLayer` constructor | `Core/Uds/IsoTp/IsoTpLayer.cs` | `(CanIdConfig, Func<CanFrame, Task>, ILogger?)` |
| `CanIdConfig` | `Core/Uds/IsoTp/IsoTpLayer.cs:137` | `sealed record { RequestId, ResponseId, FunctionalId?, IsExtendedFrame }` (init-only, no positional ctor) |
| `PeakCanChannel` | `Infrastructure/Peak/PeakCanChannel.cs:97` | ctor: `(ChannelId, ILogger?, IPcanReader? = null)` — reader=null 用真实 PCAN-Basic; WriteAsync: `ValueTask<Result<Unit>>`; `event Action<CanFrame>? FrameReceived` |
| `ExpectFrameStep` record | `Core/HIL/StepParams/ExpectFrameStep.cs:6` | `record(CanId Id, byte[]? DataMask, int TimeoutMs)` |
| `AssertDtcStep` record | `Core/HIL/StepParams/AssertDtcStep.cs:6` | `record(ushort? DtcCode, bool ExpectPresent)` |
| `AssertNrcStep` record | `Core/HIL/StepParams/AssertNrcStep.cs:6` | `record(byte ServiceId, byte ExpectedNrc)` |
| `AssertResponseTimeStep` record | `Core/HIL/StepParams/AssertResponseTimeStep.cs:6` | `record(CanId ReqId, CanId RespId, int MaxMs)` |
| `JsonDerivedType` discriminators | `Core/HIL/StepParameters.cs` | `"expectFrame"`, `"assertDtc"`, `"assertNrc"`, `"assertResponseTime"` |
| `TestSuiteResult` record | `Core/HIL/TestSuiteResult.cs:6` | `(string SuiteName, int TotalCases, int PassedCases, int FailedCases, int SkippedCases, int ElapsedMs, IReadOnlyList<string> SetupFailures, IReadOnlyList<TestCaseResult> CaseResults)` |
| `IsoTpLayer` async ctor | `Core/Uds/IsoTp/IsoTpLayer/LifecycleFlow.cs:39` | `(CanIdConfig, Func<CanFrame, Task>, ILogger?)` — 必须用 async lambda |
| `PeakCanChannel` ctor | `Infrastructure/Peak/PeakCanChannel.cs:97` | `(ChannelId, ILogger?, IPcanReader? = null)` — reader=null 用真实 PCAN-Basic |
| `FrameReceivedSubscription` | `Infrastructure/HIL/FrameReceivedSubscription.cs` | `(ICanChannel, Action<CanFrame>)` — HilIsoTpBridge 复用 |
| App → Cli 引用 | `PeakCan.Host.App.csproj` | **无** — App 不引用 Cli，HilRunnerService 必须在 Infrastructure 中（见 §13.1） |
| `HilRunRequest` record | `Infrastructure/Services/HilRunRequest.cs` | `(DbcPath, SuitePath, TracePath?, HardwareChannel?, Format, UdsRequestId, UdsResponseId)` |

---

## 19. Definition of Done

### Stage A (Offline)

- [ ] Inc 1: JUnitWriter tests pass (4 tests)
- [ ] Inc 2: ExpectFrameStepExecutor tests pass (6 tests)
- [ ] Inc 3: AssertResponseTimeStepExecutor tests pass (5 tests)
- [ ] Inc 4: AssertDtcStepExecutor tests pass (6 tests, using mock IUdsSession)
- [ ] Inc 5: AssertNrcStepExecutor tests pass (4 tests, using mock IUdsSession)
- [ ] Inc 6: BLF LoadBlf tests pass (3 tests)
- [ ] Inc 7: WriteAsync loopback tests pass (3 tests)
- [ ] `dotnet build` entire solution succeeds
- [ ] `dotnet test` all Sprint 1 + 2 + 3A tests pass
- [ ] CLI `--format junit` produces valid JUnit XML
- [ ] CLI `--trace file.blf` replays BLF files
- [ ] TraceDrivenChannel loopback: WriteAsync → FrameReceived works

### Stage B (Hardware)

- [ ] Inc 8: PeakCanAssertionContext tests pass (8 tests)
- [ ] Inc 9: CLI hardware mode flag tests pass
- [ ] Inc 9b: HilIsoTpBridge tests pass
- [ ] Inc 9d: ParseChannelHandle tests pass
- [ ] Inc 9e: CliArgsParser UDS args tests pass (hex + decimal)
- [ ] Inc 13: Integration test with real PCAN hardware (skipped on CI)
- [ ] CLI `--hw USB1` runs against real hardware
- [ ] CLI `--uds-req` / `--uds-resp` configure UDS CAN IDs
- [ ] HilIsoTpBridge: ECU response frames reach UdsClient end-to-end (integration)

### Stage C (UI + Diagnostics)

- [ ] Inc 10: CircularBuffer tests pass (4 tests)
- [ ] Inc 11: FramesAroundFailure capture test passes
- [ ] Inc 12: WPF HIL panel manual test (load suite, run, view results)
- [ ] WPF panel build succeeds
