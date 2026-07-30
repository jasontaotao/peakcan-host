# HIL Phase 3 TDD Implementation Plan

**Spec**: [2026-07-30-hil-phase3-design.md](../specs/2026-07-30-hil-phase3-design.md)
**Sprints**: 4 (ECU Simulator) + 5 (Fault Injection) + 6 (Multi-ECU Matrix)
**Total tests**: ~53

---

## Pre-flight Verification

| # | Check | Command |
|---|---|---|
| P1 | `ICanChannel` inherits `IAsyncDisposable`, has `Id` + `ReadLoopError` | `grep "interface ICanChannel" src/PeakCan.Host.Core/ICanChannel.cs` |
| P2 | `CanFrame` is `readonly record struct` with `ReadOnlyMemory<byte> Data` | `grep "record struct CanFrame" src/PeakCan.Host.Core/CanFrame.cs` |
| P3 | `CanId` ctor requires `(uint raw, FrameFormat format)` | `grep "public CanId(" src/PeakCan.Host.Core/CanId.cs` |
| P4 | `IsoTpLayer.ProcessFrame` filters by `_config.ResponseId` | `grep "ResponseId" src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer/ReceiveFlow.cs` |
| P5 | `IsoTpLayer.SendMessageAsync` sends with `_config.RequestId` | `grep "RequestId" src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer/SendFlow.cs` |
| P6 | `IsoTpLayer` ctor: `(CanIdConfig, Func<CanFrame,Task>, ILogger<IsoTpLayer>?)` | `grep "public IsoTpLayer" src/PeakCan.Host.Core/Uds/IsoTp/IsoTpLayer/LifecycleFlow.cs` |
| P7 | `StepParameters` is positional record `(TestCaseStepKind Kind)` | `grep "record StepParameters" src/PeakCan.Host.Core/HIL/StepParams/StepParameters.cs` |
| P8 | `HILJsonOptions` has `JsonStringEnumConverter` | `grep "JsonStringEnumConverter" src/PeakCan.Host.Core/HIL/Serialization/HILJsonOptions.cs` |
| P9 | `Result<T>.Fail` requires `(ErrorCode, string)` | `grep "static.*Fail" src/PeakCan.Host.Core/Result.cs` |
| P10 | `BlfParser.ParseAsync` returns `Task<IReadOnlyList<ReplayFrame>>` | `grep "ParseAsync" src/PeakCan.Host.Core/Replay/BlfParser.cs` |
| P11 | `AscParser.ParseAsync` returns `Task<IReadOnlyList<ReplayFrame>>` | `grep "ParseAsync" src/PeakCan.Host.Core/Replay/AscParser.cs` |
| P12 | `ChannelId.None` exists | `grep "None" src/PeakCan.Host.Core/ChannelId.cs` |
| P13 | `Timestamp(ulong TotalMicroseconds)` | `grep "record struct Timestamp" src/PeakCan.Host.Core/Timestamp.cs` |
| P14 | `HeadlessHostBuilder` is `static class`, `Build(CliArgs)` | `grep "static class HeadlessHostBuilder" src/PeakCan.Host.Infrastructure/HIL/HeadlessHostBuilder.cs` |

---

## Sprint 4: ECU Simulator

### Inc 0: VirtualChannel

**Files**:
- `src/PeakCan.Host.Infrastructure/Channel/VirtualChannel.cs` (new)
- `tests/PeakCan.Host.Infrastructure.Tests/Channel/VirtualChannelTests.cs` (new)

**RED** (6 tests):

1. `ConnectAsync_sets_IsConnected_true` — Connect 后 `IsConnected == true`
2. `WriteAsync_loops_back_to_FrameReceived` — WriteAsync 一帧, FrameReceived 收到同一帧
3. `FrameReceived_supports_multiple_subscribers` — 两个订阅者都收到帧
4. `DropOldest_keeps_latest_when_full` — 容量 2, 写入 3 帧, 消费者收到最新 2 帧
5. `DisposeAsync_is_idempotent` — 两次 DisposeAsync 不抛
6. `Implements_ICanChannel_all_members` — `Id` 返回 `ChannelId.None`, `ReadLoopError` add/remove 不抛

**GREEN**: 按 spec §3.1 实现 `VirtualChannel`。内部 `_consumerCts` + `_consumerTask`。

**IMPROVE**: 确认 `DisposeAsync` 中 `await _consumerTask` 有超时保护（避免消费者线程死锁时永久阻塞）。

---

### Inc 1: UdsResponseRule

**Files**:
- `src/PeakCan.Host.Core/HIL/Contracts/UdsResponseRule.cs` (new)
- `tests/PeakCan.Host.Core.Tests/HIL/Contracts/UdsResponseRuleTests.cs` (new)

**RED** (4 tests):

1. `TryMatch_returns_true_when_SID_matches` — request `[0x22, 0xF1, 0x90]`, rule SID=0x22 -> true
2. `TryMatch_checks_subFunction` — rule SID=0x19 subFunc=0x02, request `[0x19, 0x02]` -> true; request `[0x19, 0x0A]` -> false
3. `TryMatch_checks_DataMask` — rule DataMask=[0xFF,0xFF] DataPattern=[0xF1,0x90], request `[0x22, ?, 0xF1, 0x90]` -> true; DataPattern=[0xF1,0x91] -> false
4. `TryMatch_returns_false_when_SID_mismatch` — request `[0x10]`, rule SID=0x22 -> false

**GREEN**: 按 spec §3.3 实现 `UdsResponseRule` record + `TryMatch`。

---

### Inc 2: EcuScriptLoader

**Files**:
- `src/PeakCan.Host.Infrastructure/HIL/EcuScriptLoader.cs` (new)
- `src/PeakCan.Host.Infrastructure/HIL/EcuScript.cs` (new)
- `tests/PeakCan.Host.Infrastructure.Tests/HIL/EcuScriptLoaderTests.cs` (new)

**RED** (4 tests):

1. `Parse_loads_name_and_rules` — JSON 含 name + 3 rules, 返回 EcuScript with 3 rules
2. `ParseCanId_supports_0x_prefix` — `"requestId": "0x7E0"` 解析为 0x7E0
3. `ParseCanId_supports_decimal` — `"requestId": "2016"` 解析为 0x7E0
4. `Parse_swaps_RequestId_ResponseId` — JSON requestId=0x7E0 responseId=0x7E8, 返回 EcuScript.CanIds.RequestId=0x7E8 ResponseId=0x7E0

**GREEN**: 按 spec §3.5 实现 `EcuScriptLoader` + `EcuScript` record。注意 `ParseCanId` 使用 `? :` 三元运算符。

---

### Inc 3: VirtualEcu

**Files**:
- `src/PeakCan.Host.Infrastructure/HIL/VirtualEcu.cs` (new)
- `tests/PeakCan.Host.Infrastructure.Tests/HIL/VirtualEcuTests.cs` (new)

**RED** (6 tests):

1. `Responds_to_single_frame_UDS_request` — VirtualChannel + VirtualEcu(SID=0x3E rule), WriteAsync 请求帧, FrameReceived 收到响应帧 `[0x7E]`
2. `Responds_to_multi_frame_ISO_TP_request` — 发送 FF + CF 序列, VirtualEcu 重组后匹配规则并响应
3. `Returns_NRC_0x11_when_no_rule_matches` — 发送 SID=0x10 (无匹配规则), 收到 `[0x7F, 0x10, 0x11]`
4. `ResponseDelayMs_delays_response` — rule delayMs=100, 响应帧在 >=100ms 后到达
5. `Dispose_unsubscribes_FrameReceived` — Dispose 后 WriteAsync 请求帧, FrameReceived 不再收到响应
6. `First_matching_rule_wins` — 两个规则都匹配, 第一个规则的 ResponseData 被使用

**GREEN**: 按 spec §3.2 实现 `VirtualEcu`。注意：
- IsoTpLayer logger 传 `null`（`ILogger<VirtualEcu>` ≠ `ILogger<IsoTpLayer>`）
- `SendUdsResponseAsync(byte[] data, int delayMs)` 接收 delay 参数
- CanIdConfig 由 EcuScriptLoader 已交换

**IMPROVE**: 测试 2 需要构造 ISO-TP 多帧序列。可复用 `IsoTpFrame.Encode()` 构造 FF/CF 帧。

---

### Inc 4: CliArgs 扩展 + CLI --ecu 模式

**Files**:
- `src/PeakCan.Host.Infrastructure/Cli/CliArgs.cs` (extend)
- `src/PeakCan.Host.Infrastructure/Cli/CliArgsParser.cs` (extend)
- `src/PeakCan.Host.Infrastructure/HIL/HeadlessHostBuilder.cs` (extend)
- `tests/PeakCan.Host.Cli.Tests/CliArgsParserTests.cs` (extend)

**RED** (4 tests):

1. `Parser_accepts_ecu_flag` — `--ecu bms_sim.json` 解析为 `EcuScriptPath = "bms_sim.json"`
2. `Ecu_and_hw_are_mutually_exclusive` — 同时 `--ecu` + `--hw` 抛 `ArgumentException`
3. `EcuScriptPath_defaults_null` — 不传 `--ecu` 时 `EcuScriptPath == null`
4. `RegisterVirtualEcuMode_registers_UDS_services` — DI 容器包含 `IsoTpLayer`, `IUdsSession`, `AssertDtcStepExecutor`, `AssertNrcStepExecutor`

**GREEN**: 扩展 `CliArgs` record 新增 `EcuScriptPath`。扩展 `CliArgsParser` 新增 `--ecu` case。按 spec §3.8 在 `HeadlessHostBuilder.Build` 中分派到 `RegisterVirtualEcuMode`。

---

### Inc 5: WPF HIL Panel

**Files**:
- `src/PeakCan.Host.App/Views/HilView.xaml` (extend)
- `src/PeakCan.Host.App/ViewModels/HilViewModel.cs` (extend)

**Manual verification**: ECU 脚本路径选择 + 互斥校验（与 CLI 一致）。

---

### Inc 6: Stage A 集成验证

**Manual / E2E**:
1. 创建 `bms_sim.json` ECU 脚本（3 条规则）
2. 创建 `test_suite.json` 含 sendFrame + waitForFrame + assertDtc 步骤
3. 运行 `peakcan-hil --suite test_suite.json --dbc bms.dbc --ecu bms_sim.json --output results.xml`
4. 验证 JUnit XML 输出 all tests passed

---

## Sprint 5: Fault Injection

### Inc 7: FaultRule

**Files**:
- `src/PeakCan.Host.Core/HIL/Contracts/FaultRule.cs` (new)
- `tests/PeakCan.Host.Core.Tests/HIL/Contracts/FaultRuleTests.cs` (new)

**RED** (5 tests):

1. `Drop_with_probability_1_always_drops` — Probability=1.0, Apply 返回空列表
2. `Drop_with_probability_0_never_drops` — Probability=0.0, Apply 返回原帧
3. `Corrupt_flips_specified_bytes` — CorruptByteIndices=[0], CorruptXorMask=0xFF, byte[0] 被翻转
4. `Duplicate_returns_two_frames` — Apply 返回 2 个相同帧
5. `Matches_filters_by_TargetCanId` — TargetCanId=0x123, frame.Id.Raw=0x123 -> true; frame.Id.Raw=0x456 -> false; TargetCanId=null -> always true

**GREEN**: 按 spec §4.2 实现 `FaultRule` + `FaultType` 枚举。注意 `Apply` 不含 Delay（Delay 在 FaultInjector.WriteAsync 中处理）。

---

### Inc 8: FaultInjector

**Files**:
- `src/PeakCan.Host.Infrastructure/Channel/FaultInjector.cs` (new)
- `tests/PeakCan.Host.Infrastructure.Tests/Channel/FaultInjectorTests.cs` (new)

**RED** (7 tests):

1. `WriteAsync_passes_through_when_no_faults` — 无故障, 帧直接到达 inner channel
2. `Drop_fault_drops_frame` — 添加 Drop 故障, WriteAsync 后 inner channel 无帧
3. `Corrupt_fault_modifies_data` — 添加 Corrupt 故障, inner channel 收到篡改后的帧
4. `Duplicate_fault_sends_two_frames` — 添加 Duplicate 故障, inner channel 收到 2 帧
5. `Delay_fault_adds_latency` — 添加 Delay 100ms, WriteAsync 耗时 >= 90ms
6. `Multiple_faults_compose` — Drop(0.5) + Corrupt 同时存在, 帧经过两次变换
7. `Id_and_ReadLoopError_transparent` — FaultInjector.Id == inner.Id, ReadLoopError add/remove 不抛

**GREEN**: 按 spec §4.1 实现 `FaultInjector : ICanChannel`。注意：
- 只实现 `DisposeAsync`（`ICanChannel` 不继承 `IDisposable`）
- `lock(_faultsLock)` 保护 `_activeFaults`
- Delay 取最大值而非累加

**IMPROVE**: 测试 1-4 需要一个 fake `ICanChannel` 捕获 WriteAsync 的帧。可复用 `VirtualChannel`（Inc 0）作为 inner channel，订阅 FrameReceived 验证。

---

### Inc 9: InjectFaultStep + ClearFaultStep + Executors

**Files**:
- `src/PeakCan.Host.Core/HIL/StepParams/InjectFaultStep.cs` (new)
- `src/PeakCan.Host.Core/HIL/StepParams/ClearFaultStep.cs` (new)
- `src/PeakCan.Host.Core/HIL/StepParams/StepParameters.cs` (extend: add [JsonDerivedType])
- `src/PeakCan.Host.Core/HIL/StepExecutor/InjectFaultStepExecutor.cs` (new)
- `src/PeakCan.Host.Core/HIL/StepExecutor/ClearFaultStepExecutor.cs` (new)
- `src/PeakCan.Host.Core/HIL/Contracts/IFaultInjectionContext.cs` (new)
- `src/PeakCan.Host.Core/HIL/TestCaseStepKind.cs` (extend)
- `src/PeakCan.Host.Core/HIL/TestCaseStepJsonConverter.cs` (extend)
- `src/PeakCan.Host.Core/HIL/StepParams/StepParametersFactory.cs` (extend)
- `tests/PeakCan.Host.Core.Tests/HIL/StepExecutor/InjectFaultStepExecutorTests.cs` (new)
- `tests/PeakCan.Host.Core.Tests/HIL/StepExecutor/ClearFaultStepExecutorTests.cs` (new)

**RED** (6 tests):

1. `InjectFault_adds_rule_to_context` — FakeIFaultInjectionContext, executor 添加规则, context.AddFault 被调用
2. `InjectFault_fails_when_context_not_IFaultInjectionContext` — ctx is IAssertionContext only, 返回 StepStatus.Failed
3. `InjectFault_tags_FaultId` — FaultId="fault1", context.TagFault 被调用
4. `ClearFault_clears_by_FaultId` — ClearFaultStep(FaultId="fault1"), context.ClearFaults("fault1") 被调用
5. `ClearFault_clears_all_when_FaultId_null` — ClearFaultStep(null), context.ClearFaults(null) 被调用
6. `InjectFaultStep_serializes_roundtrip` — JSON serialize -> deserialize -> equal

**GREEN**: 按 spec §4.3-4.4 实现。注意：
- `InjectFaultStep` 继承 `StepParameters(TestCaseStepKind.InjectFault)`
- `ClearFaultStep` 继承 `StepParameters(TestCaseStepKind.ClearFault)`
- `InjectFaultStep.CanId` 是 `CanId` 类型（非 `uint`），executor 中 `p.CanId.Raw == 0` 判断 "全部"

---

### Inc 10: HILAssertionContext 扩展 + CLI --enable-faults

**Files**:
- `src/PeakCan.Host.Infrastructure/HIL/HILAssertionContext.cs` (extend)
- `src/PeakCan.Host.Infrastructure/Cli/CliArgs.cs` (extend)
- `src/PeakCan.Host.Infrastructure/Cli/CliArgsParser.cs` (extend)
- `src/PeakCan.Host.Infrastructure/HIL/HeadlessHostBuilder.cs` (extend)
- `tests/PeakCan.Host.Infrastructure.Tests/HIL/HILAssertionContextFaultInjectionTests.cs` (new)

**RED** (2 tests):

1. `SendFrameAsync_goes_through_FaultInjector_when_enabled` — enableFaultInjection=true, 添加 Drop 故障, SendFrameAsync 后 channel 无帧
2. `SendFrameAsync_bypasses_FaultInjector_when_disabled` — enableFaultInjection=false (default), 帧直接到达 channel

**GREEN**: 按 spec §4.5 扩展 `HILAssertionContext` 实现 `IFaultInjectionContext`。扩展 `CliArgs` 新增 `EnableFaultInjection`。扩展 `CliArgsParser` 新增 `--enable-faults` flag。

---

## Sprint 6: Multi-ECU Matrix

### Inc 11: EcuMatrix

**Files**:
- `src/PeakCan.Host.Infrastructure/HIL/EcuMatrix.cs` (new)
- `tests/PeakCan.Host.Infrastructure.Tests/HIL/EcuMatrixTests.cs` (new)

**RED** (4 tests):

1. `AddEcu_creates_VirtualEcu_on_shared_channel` — 添加 2 个 ECU, Channel 属性返回共享 VirtualChannel
2. `AddEcu_throws_on_CAN_ID_conflict` — 两个 ECU 的 CanIds.RequestId 相同, 抛 InvalidOperationException
3. `Channel_exposed_for_external_use` — EcuMatrix.Channel 是 ICanChannel, 可订阅 FrameReceived
4. `Dispose_disposes_all_ECUs_and_channel` — Dispose 后 WriteAsync 返回 Fail

**GREEN**: 按 spec §5.1 实现 `EcuMatrix`。注意 `AddEcu` 的 `logger` 参数是 `ILogger<VirtualEcu>?`。

---

### Inc 12: MatrixConfigLoader + CLI --matrix

**Files**:
- `src/PeakCan.Host.Infrastructure/HIL/MatrixConfigLoader.cs` (new)
- `src/PeakCan.Host.Infrastructure/Cli/CliArgs.cs` (extend)
- `src/PeakCan.Host.Infrastructure/Cli/CliArgsParser.cs` (extend)
- `src/PeakCan.Host.Infrastructure/HIL/HeadlessHostBuilder.cs` (extend)
- `tests/PeakCan.Host.Infrastructure.Tests/HIL/MatrixConfigLoaderTests.cs` (new)
- `tests/PeakCan.Host.Cli.Tests/CliArgsParserTests.cs` (extend)

**RED** (5 tests):

1. `Load_external_refs_loads_each_script` — 矩阵 JSON 引用 3 个脚本文件, 返回 3 个 EcuScript
2. `Load_inline_ecus_parses_directly` — 内联矩阵 JSON, 返回 2 个 EcuScript
3. `Parser_accepts_matrix_flag` — `--matrix powertrain.json` 解析为 `MatrixPath`
4. `Matrix_and_ecu_are_mutually_exclusive` — 同时 `--matrix` + `--ecu` 抛 ArgumentException
5. `Multi_ECU_end_to_end` — 2 ECU 矩阵, 发送请求到 ECU1, 收到 ECU1 响应; 发送到 ECU2, 收到 ECU2 响应

**GREEN**: 按 spec §5.2-5.3 实现 `MatrixConfigLoader`。扩展 `CliArgs` + `CliArgsParser` + `HeadlessHostBuilder`。

---

### Inc 13: Final Integration

**Manual / E2E**:
1. 创建 `powertrain.json` 矩阵（BMS + MCU 两个 ECU）
2. 创建 `test_suite.json` 含跨 ECU 交互步骤
3. 运行 `peakcan-hil --suite test_suite.json --matrix powertrain.json --output results.xml`
4. 验证两个 ECU 各自响应正确
5. 可选：`--enable-faults` + `injectFault` 步骤验证故障注入 + 多 ECU 组合

---

## Shared Test Infrastructure

### FakeIFaultInjectionContext

```csharp
// tests/PeakCan.Host.Core.Tests/Fakes/FakeIFaultInjectionContext.cs
public sealed class FakeIFaultInjectionContext : IFaultInjectionContext
{
    public List<FaultRule> AddedFaults { get; } = new();
    public Dictionary<string, IDisposable> TaggedFaults { get; } = new();
    public int ClearAllCallCount { get; private set; }
    public List<string> ClearedFaultIds { get; } = new();

    public IDisposable AddFault(FaultRule fault)
    {
        AddedFaults.Add(fault);
        return new FaultHandle(() => AddedFaults.Remove(fault));
    }

    public void TagFault(string faultId, IDisposable handle)
        => TaggedFaults[faultId] = handle;

    public void ClearFaults(string? faultId = null)
    {
        if (faultId is null) ClearAllCallCount++;
        else ClearedFaultIds.Add(faultId);
    }
}
```

### Test ECU Script (bms_sim.json)

```json
{
  "name": "BMS_Simulator",
  "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
  "rules": [
    { "serviceId": "0x3E", "subFunction": 0, "responseData": [126] },
    { "serviceId": "0x22", "dataMask": [255,255], "dataPattern": [241,144],
      "responseData": [98, 241, 144, 87, 65, 85, 84, 90, 90, 90, 57, 67, 49, 50, 51, 52, 53, 54, 55, 56] },
    { "serviceId": "0x19", "subFunction": 2, "responseData": [89, 2, 8, 0, 0, 0, 9] }
  ]
}
```

---

## Risk Register (Implementation)

| Risk | Mitigation |
|---|---|
| Inc 3 多帧 ISO-TP 测试复杂 | 复用 `IsoTpFrame.Encode()` 构造测试帧; 或用 `IsoTpLayer.SendMessageAsync` 在发送端编码 |
| Inc 8 FaultInjector 测试需要 fake ICanChannel | 复用 VirtualChannel（Inc 0）作为 inner channel, 订阅 FrameReceived 验证 |
| Inc 9 StepParameters JSON 多态序列化 | 确认 `HILJsonOptions` 的 `[JsonDerivedType]` 列表包含 InjectFaultStep/ClearFaultStep |
| Inc 10 HILAssertionContext 构造函数变更 | `enableFaultInjection` 有默认值 false, 现有调用点不受影响 |
| Inc 12 矩阵配置内联 vs 外部引用 | 两种格式都测试; 外部引用用相对路径 |

---

## Definition of Done

- [ ] 所有 ~53 个测试通过
- [ ] `dotnet build` 无 error 无 warning
- [ ] Spec 中 P1-P14 预飞行验证全部通过
- [ ] Inc 6 + Inc 13 集成验证通过（手动/E2E）
- [ ] `CliArgs` 新增字段（EcuScriptPath, EnableFaultInjection, MatrixPath）有解析测试
- [ ] `StepParameters` 多态 JSON 序列化覆盖 InjectFaultStep/ClearFaultStep
- [ ] `HILAssertionContext` 保持 `internal sealed`，通过 `IFaultInjectionContext` 接口暴露故障注入能力
