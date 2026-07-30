# HIL Phase 4 TDD Plan

> Date: 2026-07-30
> Based on: `docs/superpowers/specs/2026-07-30-hil-phase4-design.md` (v2, 6 review rounds, 0 CRITICAL)
> Total: ~45 tests across 11 increments

---

## Sprint 7: Stateful ECU Simulation (~28 tests)

### Inc 0: EcuContextStore (3 tests)

| # | Test | Description |
|---|------|-------------|
| 0.1 | `Get_ReturnsDefault_WhenKeyNotExists` | Get<T> on empty store returns default(T) |
| 0.2 | `SetAndGet_RoundTrips_Value` | Set<byte[]> then Get<byte[]> returns same value; Set<int> then Get<int> returns same value |
| 0.3 | `Clear_RemovesAll_Keys` | Set 2 keys, Clear, both HasKey return false |

**Pre-check**: `EcuContextStore` is `internal sealed class` in `Core/HIL/Contracts/EcuStateMachine.cs`. Test project needs `InternalsVisibleTo`.

### Inc 1: EcuStateMachine - static transitions (5 tests)

| # | Test | Description |
|---|------|-------------|
| 1.1 | `ProcessRequest_ReturnsStaticResponse_WhenSidAndSubFuncMatch` | Transition with ServiceId=0x22, SubFunction=0x01; request [0x22, 0x01] -> response from StaticResponse |
| 1.2 | `ProcessRequest_ReturnsStaticResponse_WhenDataMaskMatches` | Transition with DataMask=[0xFF,0xFF], DataPattern=[0xF1,0x90]; request [0x2E, 0x00, 0xF1, 0x90] matches |
| 1.3 | `ProcessRequest_TransitionsToNewState_WhenToStateSet` | Transition with ToState="unlocked"; after ProcessRequest, CurrentState == "unlocked" |
| 1.4 | `ProcessRequest_MatchesWildcardTransition_WhenFromStateIsNull` | Transition with FromState=null matches from any state (locked, seedSent, etc.) |
| 1.5 | `ProcessRequest_ReturnsNrc11_WhenNoTransitionMatches` | Request with unmatched SID -> [0x7F, sid, 0x11] |

**Pre-check**: `EcuStateMachine` in `Core/HIL/Contracts/EcuStateMachine.cs`. `ProcessRequest` returns `(byte[] Response, int DelayMs)`.

### Inc 2: EcuStateMachine - dynamic transitions (4 tests)

| # | Test | Description |
|---|------|-------------|
| 2.1 | `ProcessRequest_InvokesGenerator_WhenDynamicResponse` | DynamicResponse with GeneratorName="TestGen"; mock IEcuResponseGenerator returns [0x62]; ProcessRequest returns [0x62] |
| 2.2 | `ProcessRequest_ReturnsNrc72_WhenGeneratorNameNotFound` | DynamicResponse with GeneratorName="Unknown"; no generator registered -> [0x7F, sid, 0x72] |
| 2.3 | `Generator_ReceivesCurrentState_AndContext` | Generator's Generate() receives current state name and context; verify context.Set/Get works |
| 2.4 | `Reset_ClearsState_AndContext` | After state transition + context.Set, Reset() restores CurrentState="default" and clears context |

**Pre-check**: Need a fake `IEcuResponseGenerator` in test project.

### Inc 3: StatefulVirtualEcu (5 tests)

| # | Test | Description |
|---|------|-------------|
| 3.1 | `SingleFrameRequest_TriggersStateTransition_AndResponse` | Send 0x27 subFunc=1 to ECU in "locked" state -> transition to "seedSent", response received |
| 3.2 | `SecurityAccess_FullFlow_SeedKeyUnlockWrite` | Seed request -> key verification -> write data. Full state machine flow: locked -> seedSent -> unlocked |
| 3.3 | `ClearDtc_Generator_ClearsContext` | ClearDtc generator sets DtcList to empty in context |
| 3.4 | `StatelessRules_BackwardCompatible` | EcuStateMachine.FromRules with Phase 3 UdsResponseRule list -> wildcard transitions work |
| 3.5 | `Dispose_UnsubscribesFromChannel` | After Dispose, channel.FrameReceived -= OnCanFrameReceived; verify no more callbacks |

**Pre-check**: `StatefulVirtualEcu` in `Infrastructure/HIL/StatefulVirtualEcu.cs`. Uses `VirtualChannel` + `IsoTpLayer`. `SendCanId => _ecuCanIds.ResponseId`.

### Inc 4: EcuScriptLoader (4 tests)

| # | Test | Description |
|---|------|-------------|
| 4.1 | `ParseEcuScript_StatefulJson_ParsesStatesAndTransitions` | JSON with "states" array -> EcuScript with EcuStateMachine, transitions parsed correctly |
| 4.2 | `ParseEcuScript_StatelessJson_ConvertsViaFromRules` | JSON with "rules" array -> EcuScript with EcuStateMachine (wildcard transitions) |
| 4.3 | `ParseEcuScript_SwapsCanIds_ToEcuPerspective` | JSON canIds requestId=0x7E0, responseId=0x7E8 -> EcuScript.CanIds RequestId=0x7E8, ResponseId=0x7E0 |
| 4.4 | `ParseEcuScript_Throws_WhenBothStatesAndRulesPresent` | JSON with both "states" and "rules" -> JsonException |

**Pre-check**: `EcuScriptLoader` in `Infrastructure/HIL/EcuScriptLoader.cs`. `ParseEcuScript` accepts optional `generators` param. `ParseCanIds` swaps IDs.

### Inc 5: Built-in generators (4 tests)

| # | Test | Description |
|---|------|-------------|
| 5.1 | `SecurityAccessSeed_GeneratesAndCachesSeed` | First call generates 4-byte seed; second call returns same seed |
| 5.2 | `SecurityAccessVerifyKey_ReturnsPositive_WhenKeyCorrect` | Seed = [0xAA,0xBB,0xCC,0xDD]; key = seed XOR 0xAA; -> [0x67, 0x02] |
| 5.3 | `SecurityAccessVerifyKey_ReturnsNrc35_WhenKeyIncorrect` | Wrong key -> [0x7F, 0x27, 0x35] |
| 5.4 | `ClearDtc_SetsEmptyDtcList_AndReturnsPositive` | -> [0x54]; context has DtcList = empty list |

**Pre-check**: Generators in `Infrastructure/HIL/Generators/`. Key algorithm: `seed[i] ^ 0xAA`.

### Inc 6: CLI + EcuMatrix integration (3 tests)

| # | Test | Description |
|---|------|-------------|
| 6.1 | `EcuMatrix_AddEcu_Stateful_RespondsToRequest` | Matrix with 1 stateful ECU -> request -> response |
| 6.2 | `EcuMatrix_AddEcu_DetectsCanIdConflict` | Two ECUs with same send CAN ID -> InvalidOperationException |
| 6.3 | `HeadlessHostBuilder_EcuMode_Stateful_EndToEnd` | --ecu with stateful JSON script -> DI builds -> ECU responds to UDS request |

**Pre-check**: `EcuMatrix` uses `StatefulVirtualEcu` (not `VirtualEcu`). `HeadlessHostBuilder.RegisterVirtualEcuMode` creates `StatefulVirtualEcu`.

---

## Sprint 8: Receive-Path Fault Injection + ODX Import (~17 tests)

### Inc 7: ReceivePathFaultInjector (7 tests)

| # | Test | Description |
|---|------|-------------|
| 7.1 | `FrameReceived_PassesThrough_WhenNoFaults` | No faults added -> subscriber receives frame unchanged |
| 7.2 | `FrameReceived_DropsFrame_WhenDropFaultMatches` | Drop fault with TargetCanId matching -> subscriber receives nothing |
| 7.3 | `FrameReceived_CorruptsFrame_WhenCorruptFaultMatches` | Corrupt fault with byte index 0, XOR 0xFF -> subscriber receives modified frame |
| 7.4 | `FrameReceived_DuplicatesFrame_WhenDuplicateFaultMatches` | Duplicate fault -> subscriber receives 2 frames |
| 7.5 | `FrameReceived_DelaysFrame_WhenDelayFaultMatches` | Delay fault 100ms -> subscriber receives frame after ~100ms (assert > 90ms) |
| 7.6 | `FrameReceived_IsolatesSubscriberExceptions` | Subscriber A throws -> Subscriber B still receives frame |
| 7.7 | `FrameReceived_DoubleRemove_DoesNotBreakSubscription` | Add handler A, remove A twice, add handler B -> B still receives frames |

**Pre-check**: `ReceivePathFaultInjector` in `Infrastructure/Channel/ReceivePathFaultInjector.cs`. Implements `ICanChannel`. Uses `FaultRule.Apply()` for non-Delay faults. Delay uses `Task.Run` + `_pendingDelayTasks` tracking.

### Inc 8: InjectFaultStep Direction + Executor (4 tests)

| # | Test | Description |
|---|------|-------------|
| 8.1 | `Executor_SendDirection_CallsAddFault` | Direction=Send -> faultCtx.AddFault called (verify via mock) |
| 8.2 | `Executor_ReceiveDirection_CallsAddReceiveFault` | Direction=Receive -> faultCtx.AddReceiveFault called |
| 8.3 | `Executor_BothDirection_CallsBoth` | Direction=Both -> both AddFault and AddReceiveFault called |
| 8.4 | `Executor_SendDirection_BackwardCompatible_WhenDirectionOmitted` | InjectFaultStep JSON without "direction" field -> defaults to Send |

**Pre-check**: `InjectFaultStep` has `Direction = FaultDirection.Send` default. `FaultDirection` enum in `Core/HIL`. `CompositeHandle` wraps both handles.

### Inc 9: OdxEcuScriptImporter (4 tests)

| # | Test | Description |
|---|------|-------------|
| 9.1 | `ImportToJson_ExtractsServices_FromValidOdx` | ODX with DIAG-COMM elements -> JSON with rules array |
| 9.2 | `ImportToJson_GeneratesCorrectResponseData` | ODX with POS-RESPONSE/CODED-VALUE -> responseData bytes in JSON |
| 9.3 | `ImportToJson_GeneratesCorrectCanIds` | requestId=0x7E0, responseId=0x7E8 -> JSON canIds "0x7E0"/"0x7E8" |
| 9.4 | `ImportToJson_Throws_WhenNoServicesFound` | Empty/invalid ODX -> InvalidOperationException |

**Pre-check**: `OdxEcuScriptImporter` in `Infrastructure/HIL/Odx/`. Uses `XDocument.Load` + `using System.Xml.Linq`. Uses `HILJsonOptions.Default` for serialization.

### Inc 10: CLI --import-odx (2 tests)

| # | Test | Description |
|---|------|-------------|
| 10.1 | `Cli_ImportOdx_WritesJsonFile` | `--import-odx bms.odx --ecu-name BMS --output bms_sim.json` -> file written, valid JSON |
| 10.2 | `Cli_ImportOdx_OutputUsableWithEcuMode` | Generated JSON from Inc 10.1 -> `--ecu bms_sim.json` -> ECU responds |

**Pre-check**: `CliArgs` has `ImportOdxPath`, `ImportOdxEcuName`, `ImportOdxRequestId`, `ImportOdxResponseId`. `CliArgsParser` has `--import-odx`, `--ecu-name`, `--import-uds-req`, `--import-uds-resp` cases.

### Inc 11: E2E fault injection integration (1 test)

| # | Test | Description |
|---|------|-------------|
| 11.1 | `FaultInjection_ReceiveDirection_DropsEcuResponse` | --ecu + --enable-faults; InjectFault Receive Drop on ECU response CAN ID -> HILAssertionContext does not receive response frame |

**Pre-check**: `HeadlessHostBuilder.RegisterVirtualEcuMode` creates `ReceivePathFaultInjector(FaultInjector(channel))`. DI registers `effectiveChannel`. `HILAssertionContext` receives `txFault` + `rxFault` via constructor.

---

## Pre-implementation validation checklist

Before starting Inc 0, verify these existing types match spec assumptions:

| Type | File | Verify |
|---|---|---|
| `ICanChannel` | `Core/ICanChannel.cs` | Inherits `IAsyncDisposable` (not `IDisposable`); has `FrameReceived` event |
| `IsoTpLayer` ctor | `Core/Uds/IsoTp/IsoTpLayer/LifecycleFlow.cs:42` | `(CanIdConfig, Func<CanFrame,Task>, ILogger<IsoTpLayer>?, uint?)` |
| `IsoTpLayer.SendMessageAsync` | `Core/Uds/IsoTp/IsoTpLayer/SendFlow.cs:23` | `Task SendMessageAsync(byte[], CancellationToken)` |
| `IsoTpLayer.ProcessFrame` | `Core/Uds/IsoTp/IsoTpLayer/ReceiveFlow.cs:26` | `void ProcessFrame(CanFrame)` |
| `CanFrame` | `Core/CanFrame.cs:15` | `readonly record struct(CanId, ReadOnlyMemory<byte>, FrameFlags, ChannelId, Timestamp)` |
| `CanIdConfig` | `Core/Uds/IsoTp/IsoTpLayer.cs:144` | `sealed record` with `RequestId`, `ResponseId`, `IsExtendedFrame` |
| `FaultRule` | `Core/HIL/Contracts/FaultRule.cs:6` | `sealed record` with init-only properties, no positional ctor |
| `FaultInjector` | `Infrastructure/Channel/FaultInjector.cs:10` | `AddFault(FaultRule)` returns `FaultHandle` |
| `HILJsonOptions` | `Core/HIL/Serialization/HILJsonOptions.cs:6` | `CamelCase`, `ByteArrayJsonConverter`, `JsonStringEnumConverter` |
| `VirtualChannel` | `Infrastructure/CanChannels/VirtualChannel.cs:10` | `SingleReader = true`, `DropOldest` |
| `StepParameters` | `Core/HIL/StepParams/StepParameters.cs:24` | `abstract record(Kind)`, `[JsonPolymorphic($kind)]` |
| `InjectFaultStep` | `Core/HIL/StepParams/InjectFaultStep.cs:6` | Positional record, last param will be `Direction` |
