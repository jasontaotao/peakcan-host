# HIL Sprint 3 TDD Implementation Plan

**Date**: 2026-07-30
**Spec**: [2026-07-30-hil-sprint3-design.md](../specs/2026-07-30-hil-sprint3-design.md)
**Depends**: Sprint 1 + Sprint 2 complete

---

## Pre-Flight Verification

实现前必须验证以下 API 签名（spec §18 已列出，此处标注验证方式）：

| # | 依赖 | 验证命令 / 文件 |
|---|---|---|
| P1 | `BlfParser.ParseAsync` 签名 | `Read Core/Replay/BlfParser.cs:35` |
| P2 | `ReplayOptions` 有无参 ctor + `.Default` | `Read Core/Replay/ReplayOptions.cs` |
| P3 | `UdsClient` ctor 接受 `IsoTpLayer` | `Read Core/Uds/UdsClient.cs:68` |
| P4 | `UdsClient.ReadDtcInformationAsync(byte, byte, CancellationToken)` | `Read Core/Uds/UdsClient/DataIOFlow.cs:51` |
| P5 | `UdsClient.SendRequestAsync(byte, byte[]?, CancellationToken)` | `Read Core/Uds/UdsClient/TransportFlow.cs:30` |
| P6 | `UdsNegativeResponseException.ServiceId + ResponseCode` | `Read Core/Uds/UdsException.cs:15` |
| P7 | `IsoTpLayer` async ctor `(CanIdConfig, Func<CanFrame,Task>, ILogger?)` | `Read Core/Uds/IsoTp/IsoTpLayer/LifecycleFlow.cs:39` |
| P8 | `CanIdConfig` init-only props (RequestId, ResponseId, IsExtendedFrame) | `Read Core/Uds/IsoTp/IsoTpLayer.cs:137` |
| P9 | `PeakCanChannel(ChannelId, ILogger?, IPcanReader?)` ctor | `Read Infrastructure/Peak/PeakCanChannel.cs:97` |
| P10 | `PeakCanChannel.WriteAsync` returns `ValueTask<Result<Unit>>` | same file |
| P11 | `FrameReceivedSubscription(ICanChannel, Action<CanFrame>)` | `Read Infrastructure/HIL/FrameReceivedSubscription.cs` |
| P12 | `ExpectFrameStep` / `AssertDtcStep` / `AssertNrcStep` / `AssertResponseTimeStep` records | `Read Core/HIL/StepParams/*.cs` |
| P13 | App.csproj 不引用 Cli | `Read src/PeakCan.Host.App/PeakCan.Host.App.csproj` |
| P14 | Infrastructure.csproj 不引用 `Microsoft.Extensions.Hosting`（迁移前） | `Read src/PeakCan.Host.Infrastructure/PeakCan.Host.Infrastructure.csproj` |

---

## Stage A - Offline Capability (CI-runnable)

### Inc 0: Infrastructure 项目准备（直接实现）

**前置条件**：无

**文件**：
- `MODIFY: src/PeakCan.Host.Infrastructure/PeakCan.Host.Infrastructure.csproj` — 添加 `Microsoft.Extensions.Hosting` + `Serilog.Extensions.Hosting` + `Serilog.Sinks.Console` PackageReference（为后续 HeadlessHostBuilder 迁移做准备）
- `MOVE: Cli/CliArgs.cs` -> `Infrastructure/Cli/CliArgs.cs`
- `MOVE: Cli/CliArgsParser.cs` -> `Infrastructure/Cli/CliArgsParser.cs`
- `MOVE: Cli/HeadlessHostBuilder.cs` -> `Infrastructure/HIL/HeadlessHostBuilder.cs`
- `MODIFY: Cli/Program.cs` — 更新 using 命名空间
- `MODIFY: src/PeakCan.Host.Infrastructure/AssemblyInfo.cs` 或 csproj — 添加 `<InternalsVisibleTo Include="PeakCan.Host.Cli" />`（Cli 需访问 Infrastructure internal 类型）

**验证**：`dotnet build` 成功，`dotnet test` Sprint 1+2 测试全绿

**无测试**：纯项目结构调整

---

### Inc 1: JUnit XML Writer (TDD, 4 tests)

**前置条件**：Inc 0

**文件**：
- `NEW: tests/PeakCan.Host.Cli.Tests/JUnitWriterTests.cs`
- `NEW: src/PeakCan.Host.Cli/JUnitWriter.cs`

**测试用例**：

| # | 测试名 | Arrange | Act | Assert |
|---|---|---|---|---|
| 1 | `WriteJunit_ValidSuite_ProducesValidXml` | 2 cases (1 pass, 1 fail) | WriteJunit -> parse XML | `testsuites/testsuite` 存在；`tests=2 failures=1`；pass case 无 `<failure>`；fail case 有 `<failure>` |
| 2 | `WriteJunit_EmptySuite_OutputsZeroTests` | 0 cases | WriteJunit -> parse XML | `tests="0" failures="0" skipped="0"` |
| 3 | `WriteJunit_TimeFormattedAsSeconds` | ElapsedMs=1500 | WriteJunit -> parse XML | `time="1.500"` |
| 4 | `WriteJunit_FailureMessageContainsStepDetails` | 1 fail case with 2 failed steps | WriteJunit -> parse XML | `<failure>` text contains "Step 0:" and "Step 1:" |

**TDD 流程**：
1. RED: 写 4 个测试，运行 -> `JUnitWriter` 不存在，编译失败
2. GREEN: 实现 `JUnitWriter.WriteJunit`（spec §5.1）
3. IMPROVE: 验证 XML 声明 `<?xml version="1.0" encoding="utf-8"?>`

---

### Inc 2: WaitForFrame Executor (TDD, 6 tests)

**前置条件**：Inc 0

**文件**：
- `NEW: tests/PeakCan.Host.Core.Tests/HIL/Assertions/WaitForFrameAsyncTests.cs`
- `MODIFY: src/PeakCan.Host.Core/HIL/Assertions/AssertionPrimitives.cs` — 添加 `WaitForFrameAsync` + `MatchesMask`
- `NEW: tests/PeakCan.Host.Core.Tests/HIL/StepExecutor/ExpectFrameStepExecutorTests.cs`
- `NEW: src/PeakCan.Host.Core/HIL/StepExecutor/ExpectFrameStepExecutor.cs`

**共享测试基础设施**：使用 Sprint 2 的 `FakeAssertionContext`（已有 `SubscribeDecodedFrames` + `SendFrameAsync`）

**测试用例**：

| # | 测试名 | Arrange | Act | Assert |
|---|---|---|---|---|
| 1 | `WaitForFrame_ExactIdMatch_Passes` | ctx, sub fires frame(Id=0x123) | WaitForFrameAsync(0x123, null, 1000ms) | Pass, message contains "0x123" |
| 2 | `WaitForFrame_MaskMatch_Passes` | ctx, sub fires frame(Id=0x123, Data=[0xFF,0x0F]) | WaitForFrameAsync(0x123, [0xFF], 1000ms) | Pass (mask [0xFF] AND data[0]=0xFF -> match) |
| 3 | `WaitForFrame_MaskMismatch_Fails` | ctx, sub fires frame(Id=0x123, Data=[0x0F,0xFF]) | WaitForFrameAsync(0x123, [0xFF], 1000ms) | Fail timeout (data[0] & 0xFF != 0xFF) |
| 4 | `WaitForFrame_Timeout_Fails` | ctx, no frame fired | WaitForFrameAsync(0x123, null, 50ms) | Fail, message contains "timeout" |
| 5 | `WaitForFrame_NullMask_MatchesAnyData` | ctx, sub fires frame(Id=0x123, Data=[]) | WaitForFrameAsync(0x123, null, 1000ms) | Pass |
| 6 | `WaitForFrame_Cancelled_ThrowsOrFails` | ctx, external ct cancelled | WaitForFrameAsync(0x123, null, 10000ms, ct) | Fail or OperationCanceledException |

**TDD 流程**：
1. RED: 写 `WaitForFrameAsyncTests`，运行 -> 方法不存在
2. GREEN: 实现 `WaitForFrameAsync` + `MatchesMask`（spec §6.1）
3. RED: 写 `ExpectFrameStepExecutorTests`（用 mock `IAssertionContext`），运行 -> executor 不存在
4. GREEN: 实现 `ExpectFrameStepExecutor`（spec §6.2）

---

### Inc 3: AssertResponseTime Executor (TDD, 5 tests)

**前置条件**：Inc 0

**文件**：
- `NEW: tests/PeakCan.Host.Core.Tests/HIL/StepExecutor/AssertResponseTimeStepExecutorTests.cs`
- `NEW: src/PeakCan.Host.Core/HIL/StepExecutor/AssertResponseTimeStepExecutor.cs`

**测试用例**：

| # | 测试名 | Arrange | Act | Assert |
|---|---|---|---|---|
| 1 | `ResponseTime_FastResponse_Passes` | ctx, SendFrameAsync succeeds, sub fires RespId after 5ms | ExecuteAsync(MaxMs=100) | Passed, message contains "Response in" |
| 2 | `ResponseTime_SlowResponse_Fails` | ctx, SendFrameAsync succeeds, sub fires RespId after 200ms | ExecuteAsync(MaxMs=50) | Failed, message contains "No response" or "too slow" |
| 3 | `ResponseTime_NoResponse_Timeout` | ctx, SendFrameAsync succeeds, no sub fire | ExecuteAsync(MaxMs=50) | Failed, message contains "No response" |
| 4 | `ResponseTime_SendFails_Fails` | ctx, SendFrameAsync returns Fail | ExecuteAsync(MaxMs=100) | Failed, message contains "Failed to send" |
| 5 | `ResponseTime_ExternalCancel_Fails` | ctx, external ct cancelled after 10ms | ExecuteAsync(MaxMs=5000, ct) | Failed or OperationCanceledException |

**TDD 流程**：
1. RED: 写测试，运行 -> executor 不存在
2. GREEN: 实现 `AssertResponseTimeStepExecutor`（spec §7.7，注意先订阅再发送、Stopwatch 在 SendFrameAsync 前启动）

---

### Inc 4: UDS AssertDtc Executor (TDD, 6 tests)

**前置条件**：Inc 0

**文件**：
- `NEW: src/PeakCan.Host.Core/HIL/Contracts/IUdsSession.cs`
- `NEW: src/PeakCan.Host.Core/HIL/Contracts/DtcInfo.cs`
- `NEW: src/PeakCan.Host.Core/HIL/Contracts/UdsSessionException.cs`（含 UdsSessionException + UdsNrcException + UdsSessionTransportException）
- `NEW: tests/PeakCan.Host.Core.Tests/Fakes/FakeIUdsSession.cs`
- `NEW: tests/PeakCan.Host.Core.Tests/HIL/StepExecutor/AssertDtcStepExecutorTests.cs`
- `NEW: src/PeakCan.Host.Core/HIL/StepExecutor/AssertDtcStepExecutor.cs`

**FakeIUdsSession 设计**：
```csharp
internal sealed class FakeIUdsSession : IUdsSession
{
    private readonly IReadOnlyList<DtcInfo> _dtcs;
    private readonly Exception? _exception;
    public bool SendRequestCalled { get; private set; }
    public byte? LastServiceId { get; private set; }

    public FakeIUdsSession(IReadOnlyList<DtcInfo>? dtcs = null, Exception? exception = null) { ... }
    public Task<IReadOnlyList<DtcInfo>> ReadDtcInformation(byte statusMask, CancellationToken ct) { ... }
    public Task SendRequestAsync(byte serviceId, byte[]? data, CancellationToken ct) { ... }
}
```

**测试用例**：

| # | 测试名 | Arrange | Act | Assert |
|---|---|---|---|---|
| 1 | `AssertDtc_DtcPresent_ExpectPresent_Passes` | FakeIUdsSession with DtcInfo(0x1234, status=0x01) | ExecuteAsync(DtcCode=0x1234, ExpectPresent=true) | Passed |
| 2 | `AssertDtc_DtcAbsent_ExpectPresent_Fails` | FakeIUdsSession with empty list | ExecuteAsync(DtcCode=0x1234, ExpectPresent=true) | Failed, "not found" |
| 3 | `AssertDtc_DtcAbsent_ExpectAbsent_Passes` | FakeIUdsSession with empty list | ExecuteAsync(DtcCode=0x1234, ExpectPresent=false) | Passed |
| 4 | `AssertDtc_DtcPresent_ExpectAbsent_Fails` | FakeIUdsSession with DtcInfo(0x1234, status=0x04) | ExecuteAsync(DtcCode=0x1234, ExpectPresent=false) | Failed, "unexpectedly present" |
| 5 | `AssertDtc_NullDtcCode_AnyDtc` | FakeIUdsSession with 1 active DTC | ExecuteAsync(DtcCode=null, ExpectPresent=true) | Passed |
| 6 | `AssertDtc_UdsError_Fails` | FakeIUdsSession throws UdsSessionTransportException | ExecuteAsync(DtcCode=0x1234, ExpectPresent=true) | Failed, "UDS error" |

**TDD 流程**：
1. RED: 写接口 + 异常 + Fake + 测试，运行 -> executor 不存在
2. GREEN: 实现 `AssertDtcStepExecutor`（spec §7.5）

---

### Inc 5: UDS AssertNrc Executor (TDD, 4 tests)

**前置条件**：Inc 4（共享 IUdsSession + 异常层级 + Fake）

**文件**：
- `NEW: tests/PeakCan.Host.Core.Tests/HIL/StepExecutor/AssertNrcStepExecutorTests.cs`
- `NEW: src/PeakCan.Host.Core/HIL/StepExecutor/AssertNrcStepExecutor.cs`

**测试用例**：

| # | 测试名 | Arrange | Act | Assert |
|---|---|---|---|---|
| 1 | `AssertNrc_CorrectNrc_Passes` | FakeIUdsSession throws UdsNrcException(SID=0x22, NRC=0x13) | ExecuteAsync(ServiceId=0x22, ExpectedNrc=0x13) | Passed |
| 2 | `AssertNrc_WrongNrc_Fails` | FakeIUdsSession throws UdsNrcException(SID=0x22, NRC=0x31) | ExecuteAsync(ServiceId=0x22, ExpectedNrc=0x13) | Failed, "NRC mismatch" |
| 3 | `AssertNrc_PositiveResponse_Fails` | FakeIUdsSession.SendRequestAsync returns normally | ExecuteAsync(ServiceId=0x22, ExpectedNrc=0x13) | Failed, "got positive response" |
| 4 | `AssertNrc_TransportError_Fails` | FakeIUdsSession throws UdsSessionTransportException | ExecuteAsync(ServiceId=0x22, ExpectedNrc=0x13) | Failed, "UDS error" |

**TDD 流程**：
1. RED: 写测试，运行 -> executor 不存在
2. GREEN: 实现 `AssertNrcStepExecutor`（spec §7.6）

---

### Inc 6: BLF File Support (TDD, 3 tests)

**前置条件**：Inc 0

**文件**：
- `NEW: tests/PeakCan.Host.Infrastructure.Tests/TraceDrivenChannelBlfTests.cs`
- `MODIFY: src/PeakCan.Host.Infrastructure/Channel/TraceDrivenChannel.cs` — 添加 `LoadBlf`

**测试用例**：

| # | 测试名 | Arrange | Act | Assert |
|---|---|---|---|---|
| 1 | `LoadBlf_ValidFile_LoadsFrames` | 小 BLF fixture 文件 | LoadBlf -> ConnectAsync -> 等待 FrameReceived | 帧数量 > 0；帧 ID 正确 |
| 2 | `LoadBlf_ExceedsMaxTraceFrames_Throws` | 构造 TraceDrivenChannel(maxTraceFrames=1) | LoadBlf(fixture with 5 frames) | throws InvalidOperationException |
| 3 | `LoadBlf_FileNotFound_Throws` | 不存在的路径 | LoadBlf("nonexistent.blf") | throws FileNotFoundException |

**TDD 流程**：
1. RED: 写测试，运行 -> `LoadBlf` 不存在
2. GREEN: 实现 `LoadBlf`（spec §8.1，使用 `BlfParser.ParseAsync` + `ReplayOptions.Default`）

**BLF fixture**：使用项目已有的测试 BLF 文件（如 `tests/fixtures/test.blf`），或创建最小 BLF fixture

---

### Inc 7: WriteAsync Loopback (TDD, 3 tests)

**前置条件**：Inc 0

**文件**：
- `NEW: tests/PeakCan.Host.Infrastructure.Tests/TraceDrivenChannelLoopbackTests.cs`
- `MODIFY: src/PeakCan.Host.Infrastructure/Channel/TraceDrivenChannel.cs` — 修改 `WriteAsync` + 添加 `_loopbackChannel` + `_loopbackLock` + `ProcessLoopbackInternal`

**测试用例**：

| # | 测试名 | Arrange | Act | Assert |
|---|---|---|---|---|
| 1 | `WriteAsync_FrameReceived_Raised` | TraceDrivenChannel, subscribe FrameReceived | WriteAsync(frame) | FrameReceived callback fired with same frame |
| 2 | `WriteAsync_StimulusResponse_TraceNotLoaded` | TraceDrivenChannel (no trace loaded), subscribe | WriteAsync(reqFrame) -> WriteAsync(respFrame) | Both frames received via FrameReceived |
| 3 | `WriteAsync_DropOldest_OverflowDoesNotBlock` | TraceDrivenChannel, write 1001 frames to channel(cap=1000) | Write 1001 frames | Does not block; last frame received; first frame dropped |

**TDD 流程**：
1. RED: 写测试，运行 -> `WriteAsync` 仍为 no-op
2. GREEN: 实现 loopback（spec §9.1，注意 `_loopbackLock` 保护 + OnTick trace 帧发射也加锁）

---

### Inc 8: Stage A 集成验证

**前置条件**：Inc 1-7

**操作**：
- `dotnet build` 全解
- `dotnet test` 全测试套件（Sprint 1 + 2 + 3A）
- CLI 手动验证：
  - `peakcan-hil --dbc test.dbc --trace test.blf --suite suite.json --format junit --output results.xml`
  - `peakcan-hil --dbc test.dbc --trace test.asc --suite suite.json --format trx --output results.trx`
  - 验证 loopback：suite 中 SendFrame 步骤后跟 WaitForFrame 步骤，trace 不加载

**无新测试**：验证现有测试 + 手动 CLI

---

## Stage B - Hardware Integration

### Inc 9: PeakCanAssertionContext (TDD, 6 tests)

**前置条件**：Inc 0

**文件**：
- `NEW: src/PeakCan.Host.Infrastructure/HIL/DbcLookupKey.cs` — 提取共享方法
- `MODIFY: src/PeakCan.Host.Infrastructure/HIL/HILAssertionContext.cs` — 使用 `DbcLookupKey.ToLookupKey`（重构）
- `NEW: src/PeakCan.Host.Infrastructure/HIL/PeakCanAssertionContext.cs`
- `NEW: tests/PeakCan.Host.Infrastructure.Tests/PeakCanAssertionContextTests.cs`

**测试用例**（硬件无关，使用 FakeCanChannel）：

| # | 测试名 | Arrange | Act | Assert |
|---|---|---|---|---|
| 1 | `Constructor_SubscribesToFrameReceived` | FakeCanChannel + FakeDbcLookup | new PeakCanAssertionContext | channel.FrameReceived has subscriber |
| 2 | `OnFrame_WritesToFrameChannel` | ctx, fire FrameReceived on channel | check _frameChannel.Reader count | Frame enqueued |
| 3 | `SendFrameAsync_DelegatesToChannel` | ctx, channel.WriteAsync mock | ctx.SendFrameAsync(frame) | channel.WriteAsync called |
| 4 | `GetSignalValue_AfterDecode_ReturnsValue` | ctx with DBC, fire frame with signal data | GetSignalValue("Msg.Sig") | Returns decoded value |
| 5 | `Dispose_UnsubscribesAndDrains` | ctx, fire frames, then Dispose | check no more callbacks after Dispose | Clean shutdown |
| 6 | `GetRecentFrames_ReturnsBuffer` | ctx, fire 3 frames, call GetRecentFrames() | Returns 3 frames in order | IHasRecentFrames implemented |

**TDD 流程**：
1. RED: 写测试，运行 -> `PeakCanAssertionContext` 不存在
2. GREEN: 实现（spec §10.1，注意 `IHasRecentFrames` 接口）

---

### Inc 10: HilIsoTpBridge (TDD, 3 tests)

**前置条件**：Inc 0

**文件**：
- `NEW: src/PeakCan.Host.Infrastructure/HIL/HilIsoTpBridge.cs`
- `NEW: tests/PeakCan.Host.Infrastructure.Tests/HilIsoTpBridgeTests.cs`

**测试用例**：

| # | 测试名 | Arrange | Act | Assert |
|---|---|---|---|---|
| 1 | `OnFrame_ForwardsToProcessFrame` | FakeCanChannel + mock IsoTpLayer | fire FrameReceived | isoTp.ProcessFrame called with frame |
| 2 | `OnFrame_MalformedFrame_SwallowsArgumentException` | FakeCanChannel, isoTp.ProcessFrame throws ArgumentException | fire FrameReceived | No exception propagates |
| 3 | `Dispose_UnsubscribesFromChannel` | bridge, then Dispose | fire FrameReceived | isoTp.ProcessFrame NOT called |

**TDD 流程**：
1. RED: 写测试，运行 -> `HilIsoTpBridge` 不存在
2. GREEN: 实现（spec §11.6，注意 `catch (ArgumentException)` 不声明变量）

---

### Inc 11: UdsSessionAdapter (TDD, 4 tests)

**前置条件**：Inc 4（IUdsSession + 异常层级）

**文件**：
- `NEW: src/PeakCan.Host.Infrastructure/Uds/UdsSessionAdapter.cs`
- `NEW: tests/PeakCan.Host.Infrastructure.Tests/Uds/UdsSessionAdapterTests.cs`

**测试用例**：

| # | 测试名 | Arrange | Act | Assert |
|---|---|---|---|---|
| 1 | `ReadDtcInformation_ReturnsParsedDtcInfos` | mock UdsClient returns byte[] [0xFF, 0x12,0x34,0x00, 0x01] | adapter.ReadDtcInformation(0xFF) | 1 DtcInfo(Code=0x1234, Status=0x01) |
| 2 | `ReadDtcInformation_NrcResponse_ThrowsUdsNrcException` | mock UdsClient throws UdsNegativeResponseException | adapter.ReadDtcInformation(0xFF) | throws UdsNrcException(SID=0x19) |
| 3 | `SendRequestAsync_NrcResponse_ThrowsUdsNrcException` | mock UdsClient throws UdsNegativeResponseException(SID=0x22, NRC=0x13) | adapter.SendRequestAsync(0x22, null) | throws UdsNrcException(Nrc=0x13) |
| 4 | `SendRequestAsync_Timeout_ThrowsTransportException` | mock UdsClient throws UdsException("timeout") | adapter.SendRequestAsync(0x22, null) | throws UdsSessionTransportException |

**TDD 流程**：
1. RED: 写测试，运行 -> `UdsSessionAdapter` 不存在
2. GREEN: 实现（spec §7.4）

---

### Inc 12: CLI Hardware Mode (直接实现 + TDD, 8 tests)

**前置条件**：Inc 9, 10, 11

**文件**：
- `MODIFY: src/PeakCan.Host.Infrastructure/Cli/CliArgs.cs` — TracePath nullable, 加 HardwareChannel/UdsRequestId/UdsResponseId
- `MODIFY: src/PeakCan.Host.Infrastructure/Cli/CliArgsParser.cs` — 加 --hw/--uds-req/--uds-resp, ParseUdsId, ParseChannelHandle
- `MODIFY: src/PeakCan.Host.Infrastructure/HIL/HeadlessHostBuilder.cs` — 硬件模式 channel factory + UDS 注册 + HilIsoTpBridge + Dispose 顺序
- `MODIFY: src/PeakCan.Host.Cli/Program.cs` — JUnit output
- `NEW: tests/PeakCan.Host.Cli.Tests/CliArgsParserTests.cs`
- `NEW: tests/PeakCan.Host.Cli.Tests/ParseChannelHandleTests.cs`

**测试用例**：

| # | 测试名 | 测试内容 |
|---|---|---|
| 1 | `Parse_HwOnly_NoTrace_Succeeds` | `--hw USB1 --dbc x --suite y` -> HardwareChannel="USB1", TracePath=null |
| 2 | `Parse_TraceOnly_NoHw_Succeeds` | `--trace x.asc --dbc x --suite y` -> TracePath="x.asc", HardwareChannel=null |
| 3 | `Parse_BothHwAndTrace_Throws` | `--hw USB1 --trace x.asc --dbc x --suite y` -> throws ArgumentException |
| 4 | `Parse_NeitherHwNorTrace_Throws` | `--dbc x --suite y` -> throws ArgumentException |
| 5 | `ParseUdsId_Hex_0xPrefix` | "0x7DF" -> 0x7DF |
| 6 | `ParseUdsId_Decimal` | "2013" -> 0x7DF |
| 7 | `ParseChannelHandle_USB1_Returns0x51` | "USB1" -> 0x51 |
| 8 | `ParseChannelHandle_Invalid_Throws` | "PCI1" -> throws ArgumentException |

**TDD 流程**：
1. RED: 写 CliArgsParser 测试 + ParseChannelHandle 测试
2. GREEN: 实现修改

---

### Inc 13: HilRunRequest + HeadlessHostBuilder 迁移 (直接实现 + TDD, 3 tests)

**前置条件**：Inc 12

**文件**：
- `NEW: src/PeakCan.Host.Core/HIL/Contracts/IHilRunnerService.cs`
- `NEW: src/PeakCan.Host.Infrastructure/Services/HilRunnerService.cs`
- `NEW: src/PeakCan.Host.Infrastructure/Services/HilRunRequest.cs`
- `NEW: tests/PeakCan.Host.Infrastructure.Tests/HilRunRequestTests.cs`

**测试用例**：

| # | 测试名 | 测试内容 |
|---|---|---|
| 1 | `ToCliArgs_TraceMode_MapsCorrectly` | HilRunRequest(trace="x.asc") -> CliArgs.TracePath="x.asc", HardwareChannel=null |
| 2 | `ToCliArgs_HardwareMode_MapsCorrectly` | HilRunRequest(hw="USB1") -> CliArgs.HardwareChannel="USB1", TracePath=null |
| 3 | `ToCliArgs_UdsIds_Preserved` | HilRunRequest(udsReq=0x714, udsResp=0x760) -> CliArgs.UdsRequestId=0x714 |

**验证**：
- `dotnet build` 全解
- App 的 `AppHostBuilder` 注册 `IHilRunnerService`

---

### Inc 14: Stage B 集成验证

**前置条件**：Inc 9-13

**操作**：
- `dotnet build` 全解
- `dotnet test` 全测试套件
- 硬件集成测试（手动，标记 Skip on CI）：
  - `peakcan-hil --hw USB1 --dbc test.dbc --suite suite.json --uds-req 0x7DF --uds-resp 0x7E8`

---

## Stage C - UI + Diagnostics

### Inc 15: CircularBuffer (TDD, 4 tests)

**前置条件**：无

**文件**：
- `NEW: src/PeakCan.Host.Infrastructure/HIL/CircularBuffer.cs`
- `NEW: tests/PeakCan.Host.Core.Tests/HIL/CircularBufferTests.cs`

> 注意：`CircularBuffer` 在 Infrastructure 项目，但测试在 Core.Tests 项目。需要 Core.Tests 引用 Infrastructure（已有），或测试放 Infrastructure.Tests。放 Infrastructure.Tests。

**修正**：
- `NEW: tests/PeakCan.Host.Infrastructure.Tests/CircularBufferTests.cs`

**测试用例**：

| # | 测试名 | Arrange | Act | Assert |
|---|---|---|---|---|
| 1 | `Add_BelowCapacity_ReturnsAllInOrder` | buffer(3), add A,B,C | Snapshot() | [A,B,C] |
| 2 | `Add_Overflow_DropsOldest` | buffer(3), add A,B,C,D | Snapshot() | [B,C,D] |
| 3 | `Snapshot_Empty_ReturnsEmptyList` | buffer(3) | Snapshot() | Count=0 |
| 4 | `Add_Concurrent_ThreadSafe` | buffer(1000), 2 threads x 500 adds each | Snapshot() after both complete | Count=1000, no exception |

**TDD 流程**：
1. RED: 写测试，运行 -> `CircularBuffer` 不存在
2. GREEN: 实现（spec §12.1）

---

### Inc 16: FramesAroundFailure (TDD, 2 tests)

**前置条件**：Inc 9（PeakCanAssertionContext with IHasRecentFrames）, Inc 15

**文件**：
- `NEW: src/PeakCan.Host.Core/HIL/Contracts/IHasRecentFrames.cs`
- `MODIFY: src/PeakCan.Host.Core/HIL/TestSuiteEngine.cs` — 添加 FramesAroundFailure 捕获逻辑
- `MODIFY: src/PeakCan.Host.Infrastructure/HIL/PeakCanAssertionContext.cs` — 实现 IHasRecentFrames + CircularBuffer 在 ConsumerLoop 中更新
- `MODIFY: src/PeakCan.Host.Infrastructure/HIL/HILAssertionContext.cs` — 同上
- `NEW: tests/PeakCan.Host.Core.Tests/HIL/FramesAroundFailureTests.cs`

**测试用例**：

| # | 测试名 | Arrange | Act | Assert |
|---|---|---|---|---|
| 1 | `StepFailure_CapturesRecentFrames` | FakeAssertionContext implementing IHasRecentFrames with 3 frames in buffer; step fails | Engine.ExecuteCaseAsync | StepResult.FramesAroundFailure has 3 frames |
| 2 | `StepPassed_NoFramesCaptured` | Same context, step passes | Engine.ExecuteCaseAsync | StepResult.FramesAroundFailure is null |

**TDD 流程**：
1. RED: 写测试（需要 FakeAssertionContext 实现 IHasRecentFrames），运行 -> 接口不存在
2. GREEN: 定义 `IHasRecentFrames`，修改 `TestSuiteEngine`，修改 contexts

---

### Inc 17: WPF HIL Panel (直接实现, 手动验证)

**前置条件**：Inc 13, 16

**文件**：
- `NEW: src/PeakCan.Host.App/Views/HilView.xaml`
- `NEW: src/PeakCan.Host.App/Views/HilView.xaml.cs`
- `NEW: src/PeakCan.Host.App/ViewModels/HilViewModel.cs`
- `NEW: src/PeakCan.Host.App/ViewModels/TestCaseResultViewModel.cs`
- `MODIFY: src/PeakCan.Host.App/Composition/AppHostBuilder.cs` — 注册 `IHilRunnerService`

**验证**：
- `dotnet build` WPF 项目成功
- 手动：启动 App -> HIL 面板可见 -> 选择 DBC + suite -> 点击 Run -> 进度更新 -> 结果显示

---

### Inc 18: Final Integration (手动)

**前置条件**：Inc 1-17

**操作**：
- `dotnet build` 全解
- `dotnet test` 全测试套件（Sprint 1 + 2 + 3）
- 硬件端到端（如硬件可用）：CLI `--hw USB1` + UDS assertions
- Trace 端到端：CLI `--trace test.asc` + 10 executors + loopback

---

## 风险登记

| 风险 | 影响 | 缓解 |
|---|---|---|
| HeadlessHostBuilder 迁移到 Infrastructure 需要添加 Hosting 包 | Infrastructure.csproj 需新增 PackageReference | Inc 0 中处理 |
| CliArgs/CliArgsParser 迁移导致 Cli 项目 using 变更 | Program.cs 编译失败 | Inc 0 中更新 using |
| BLF fixture 文件不存在 | Inc 6 无法测试 | 创建最小 BLF 或复用现有测试 fixture |
| FakeCanChannel 不存在 | Inc 9 无法测试 | Sprint 2 TDD plan 中已有 FakeCanChannel，验证是否存在 |
| HilRunnerService 在 Infrastructure 但引用 Serilog | Infrastructure 需 Serilog 包 | Inc 0 中添加 |
| OnTick _loopbackLock 持有时间 | timer 精度下降 | 确保 FrameReceived 回调非阻塞（Channel<T>.TryWrite） |

---

## 完成定义

### Stage A
- [ ] Inc 0: 项目迁移，`dotnet build` 成功
- [ ] Inc 1: JUnitWriter 4 tests pass
- [ ] Inc 2: ExpectFrameStepExecutor 6 tests pass
- [ ] Inc 3: AssertResponseTimeStepExecutor 5 tests pass
- [ ] Inc 4: AssertDtcStepExecutor 6 tests pass
- [ ] Inc 5: AssertNrcStepExecutor 4 tests pass
- [ ] Inc 6: BLF 3 tests pass
- [ ] Inc 7: Loopback 3 tests pass
- [ ] Inc 8: Stage A 集成验证通过
- [ ] CLI `--format junit` 产出有效 XML
- [ ] CLI `--trace file.blf` 回放 BLF
- [ ] TraceDrivenChannel loopback: WriteAsync -> FrameReceived

### Stage B
- [ ] Inc 9: PeakCanAssertionContext 6 tests pass
- [ ] Inc 10: HilIsoTpBridge 3 tests pass
- [ ] Inc 11: UdsSessionAdapter 4 tests pass
- [ ] Inc 12: CliArgsParser 8 tests pass
- [ ] Inc 13: HilRunRequest 3 tests pass
- [ ] Inc 14: Stage B 集成验证通过
- [ ] CLI `--hw USB1` 对真实硬件运行
- [ ] CLI `--uds-req` / `--uds-resp` 配置 UDS CAN ID

### Stage C
- [ ] Inc 15: CircularBuffer 4 tests pass
- [ ] Inc 16: FramesAroundFailure 2 tests pass
- [ ] Inc 17: WPF HIL Panel build + 手动测试
- [ ] Inc 18: Final integration 验证

### 全局
- [ ] `dotnet build` 全解成功
- [ ] `dotnet test` Sprint 1+2+3 全部通过
- [ ] 无 CRITICAL / HIGH 代码审查问题
