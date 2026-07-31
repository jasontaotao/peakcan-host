# HIL Phase 5 TDD Plan

> **Spec**: `docs/superpowers/specs/2026-07-31-hil-phase5-spec.md` (v5)
> **Created**: 2026-07-31
> **Total**: 6 Sprints, 63 tests

---

## Pre-check: verify spec assumptions against existing code

| # | Assumption | File:Line | Verify |
|---|-----------|-----------|--------|
| 1 | `EcuStateMachine.MatchesRequest` skips data check when `DataMask=null` | `Core/HIL/Contracts/EcuStateMachine.cs:85` | `if (t.DataMask is not null && t.DataMask.Length > 0)` |
| 2 | `IEcuResponseGenerator.Generate` returns `byte[]` (not throws) | `Core/HIL/Contracts/IEcuResponseGenerator.cs:20` | `byte[] Generate(...)` |
| 3 | Existing generators return NRC as byte[] | `Infrastructure/HIL/Generators/SecurityAccessVerifyKeyGenerator.cs:17` | `return new byte[] { 0x7F, 0x27, 0x22 }` |
| 4 | `RoutineDefinition` has no `Queryable` field | `Core/Uds/Database/RoutineDefinition.cs:13-18` | `(ushort Id, string Name, string Description, bool Startable, bool Stoppable)` |
| 5 | `RequestBasedMappers.ExtractDidFields` only matches 0x22/0x2E | `Core/Uds/Odx/RequestBasedMappers.cs:226-227` | hardcoded 0x22/0x2E filter |
| 6 | `SecurityAccessExtractor.Extract` returns `SecurityAccessConfig?` | `Core/Uds/Odx/SecurityAccessExtractor.cs:22` | `public static SecurityAccessConfig? Extract(XDocument, XNamespace)` |
| 7 | `OdxParser.OdxNamespace` is public const | `Core/Uds/Odx/OdxParser.cs:16` | `public const string OdxNamespace` |
| 8 | `DeepSeekOptions.Model` default is `"deepseek-v4-flash"` | `Core/Analysis/DeepSeekOptions.cs:9` | confirmed |
| 9 | `PeakCanChannel` ctor takes `ChannelId` (not `ChannelHandle`) | `Infrastructure/Peak/PeakCanChannel.cs:97` | `public PeakCanChannel(ChannelId id, ...)` |
| 10 | `HeadlessHostBuilder.ParseChannelHandle` returns `ushort` | `Infrastructure/HIL/HeadlessHostBuilder.cs:193` | `public static ushort ParseChannelHandle(string)` |
| 11 | `EcuStateMachine._transitions` is private (no public state list) | `Core/HIL/Contracts/EcuStateMachine.cs:10` | `private readonly List<EcuStateTransition>` |
| 12 | `StatefulVirtualEcu` implements `IDisposable` | `Infrastructure/HIL/StatefulVirtualEcu.cs:71` | `public void Dispose()` |
| 13 | `HILAssertionContext._faultHandles` is `Dictionary` | `Infrastructure/HIL/HILAssertionContext.cs:31` | `private readonly Dictionary<string, IDisposable>` |
| 14 | `ReceivePathFaultInjector` has no `CancellationTokenSource` | `Infrastructure/Channel/ReceivePathFaultInjector.cs` | no `_cts`/`_delayCts` field |
| 15 | `CliArgs` in `Infrastructure/Cli/` | `Infrastructure/Cli/CliArgs.cs:1` | `namespace PeakCan.Host.Infrastructure.Cli` |
| 16 | `ICredentialStore` impl only in App layer | `App/Services/CredentialStore/WindowsCredentialManagerStore.cs` | grep Infrastructure: 0 matches |

---

## Sprint 9: ODX -> Stateful EcuScript (10 tests)

### Inc 0: OdxToEcuScriptAdapter namespace resolution (3 tests)

| Test | Description |
|------|-------------|
| `NamespaceResolution_OdxNamespace_ReturnsCorrectNs` | ODX doc with `xmlns="http://www.asam.net/xml/odx"` -> Adapter resolves ns, passes to SecurityAccessExtractor.Extract |
| `NamespaceResolution_EmptyNamespace_ReturnsEmptyNs` | ODX-D doc (no xmlns) -> Adapter resolves empty namespace, passes to RequestBasedMappers.ExtractDids |
| `NamespaceResolution_InvalidNamespace_ThrowsOdxParseException` | ODX doc with wrong namespace -> Adapter throws (or logs warning + returns empty transitions) |

### Inc 1: SecurityAccess transition generation (3 tests)

| Test | Description |
|------|-------------|
| `SecurityAccess_SeedLength4_GeneratesTwoTransitions` | SecurityAccessConfig(Level=0x01, SeedLength=4) -> seed transition (0x27,0x01,DataMask=null,toState=seedSent) + key verify (0x27,0x02,DataMask=null,toState=unlocked) |
| `SecurityAccess_SeedLengthNull_SkipsAndWarns` | SecurityAccessConfig(SeedLength=null) -> skips, records warning |
| `SecurityAccess_DataMaskNull_ProcessRequestSucceeds` | Generated transitions with DataMask=null fed to EcuStateMachine.ProcessRequest -> matches by ServiceId+SubFunction, no NRE |

### Inc 2: Routine transition generation (2 tests)

| Test | Description |
|------|-------------|
| `Routine_GeneratesStartStopResults_Transitions` | RoutineDefinition(Id=0xFF00, Startable=true, Stoppable=true) -> 3 transitions: subFunc 0x01/0x02/0x03, dataMask=[0xFF,0xFF], dataPattern=[0xFF,0x00], response=[0x71,subFunc] |
| `Routine_StoppableFalse_OmitsStopTransition` | RoutineDefinition(Stoppable=false) -> Start + RequestResults only, no Stop |

### Inc 3: DID Read + end-to-end ODX import (2 tests)

| Test | Description |
|------|-------------|
| `DidRead_ExtractDids_GeneratesDynamicDidReadoutTransition` | ExtractDids returns {0xF190: false} -> transition: serviceId=0x22, dataMask=[0xFF,0xFF], dataPattern=[0xF1,0x90], response=dynamic "DidReadout" |
| `OdxEcuScriptImporter_EndToEnd_GeneratesStatesJson` | Minimal ODX string -> ImportToJson -> output JSON contains "states" array, parseable by EcuScriptLoader.Parse |

---

## Sprint 10: Generator Extensibility + DID Read/Write (11 tests)

### Inc 4: GeneratorPluginLoader (3 tests)

| Test | Description |
|------|-------------|
| `PluginLoader_ValidDll_LoadsGenerators` | DLL with one IEcuResponseGenerator impl (parameterless ctor) -> loader returns 1 item |
| `PluginLoader_InvalidDll_SkipsAndContinues` | Non-DLL or DLL without IEcuResponseGenerator -> empty list, no exception |
| `PluginLoader_MixedDlls_LoadsOnlyValid` | Dir with 1 valid + 1 invalid DLL -> only valid generator returned |

### Inc 5: External-first override (2 tests)

| Test | Description |
|------|-------------|
| `MergeGenerators_ExternalOverridesBuiltIn_SameName` | Built-in SecurityAccessSeed + external SecurityAccessSeed -> merged has external instance |
| `MergeGenerators_DisjointNames_KeepsBoth` | Built-in SecurityAccessSeed + external CustomGen -> 2 entries |

### Inc 6: didValues injection (2 tests)

| Test | Description |
|------|-------------|
| `EcuScriptLoader_DidValues_InjectsIntoContext` | JSON with didValues -> stateMachine.Context.Get<Dictionary<ushort,byte[]>>("DidValues") returns values |
| `EcuMatrix_AddEcu_DidValues_InjectsIfMissing` | Manually constructed EcuScript with DidValues -> EcuMatrix.AddEcu -> Context.HasKey("DidValues") true |

### Inc 7: DidReadoutGenerator (2 tests)

| Test | Description |
|------|-------------|
| `DidReadoutGenerator_DidFound_ReturnsPositiveResponse` | Context has DidValues[{0xF190: [0x41,0x42,0x43]}] -> request [0x22,0xF1,0x90] -> response [0x62,0xF1,0x90,0x41,0x42,0x43] |
| `DidReadoutGenerator_DidNotFound_ReturnsNrcByteArray` | Context has DidValues without 0xF190 -> request [0x22,0xF1,0x90] -> response [0x7F,0x22,0x31] (**R5 L1-R5 boundary case: NRC as byte[], not exception**) |

### Inc 8: DidWriteGenerator (2 tests)

| Test | Description |
|------|-------------|
| `DidWriteGenerator_WritesValue_ReturnsPositiveResponse` | Context has DidValues -> request [0x2E,0xF1,0x90,0x01,0x02] -> response [0x6E,0xF1,0x90], DidValues[0xF190] updated |
| `DidWriteGenerator_DidNotWritable_ReturnsNrc` | DID not in writable set -> response [0x7F,0x2E,0x31] |

---

## Sprint 11: Reporting + M2/M3 Hardening (15 tests)

### Inc 9: HtmlReportGenerator (3 tests)

| Test | Description |
|------|-------------|
| `HtmlReport_AllPassed_GeneratesSummaryWithPassRate` | 3 passed / 0 failed -> HTML contains "100%" and "3/3" |
| `HtmlReport_WithFailure_IncludesFramesHexDump` | 1 failed step + 3 FramesAroundFailure -> HTML contains hex dump table |
| `HtmlReport_FramesCappedAt50_DoesNotExceedLimit` | 60 frames -> HTML renders only first 50 |

### Inc 10: TrendTracker (4 tests)

| Test | Description |
|------|-------------|
| `TrendTracker_FirstRun_CreatesFileWithOneEntry` | hil-trends.json does NOT exist -> Record(entry) -> file created with 1 entry (**R5 B1-R5 boundary case: FileNotFoundException handled**) |
| `TrendTracker_ExistingFile_AppendsEntry` | File with 5 entries -> Record -> 6 entries |
| `TrendTracker_Over100_RollsOldest` | 100 entries -> Record -> still 100, oldest removed |
| `TrendTracker_CorruptedJson_BackupsAndRebuilds` | Truncated JSON -> Record -> .corrupt-* backup + new file with 1 entry |

### Inc 11: ConsoleSummaryFormatter (2 tests)

| Test | Description |
|------|-------------|
| `ConsoleSummary_MixedResults_PrintsPassAndFail` | 2 passed + 1 failed -> output contains green check and red cross |
| `ConsoleSummary_DoesNotConflictWithConsoleProgress` | ConsoleProgress runs during, ConsoleSummaryFormatter after -> no interference |

### Inc 12: FrameCaptureExporter (2 tests)

| Test | Description |
|------|-------------|
| `FrameExporter_WritesAscFormat` | 3 CanFrame -> .asc file with PEAK ASCII header + 3 frame lines |
| `FrameExporter_FramesCappedAt50` | 60 frames -> exports only first 50 |

### Inc 13: M2 ConcurrentDictionary fix (2 tests)

| Test | Description |
|------|-------------|
| `ClearFaults_ConcurrentAddAndClear_NoLeak` | Thread A AddFault loop + Thread B ClearFaults -> no exception, no undisposed handles |
| `ClearFaults_TargetedClear_RemovesOnlyMatchingId` | 2 faults diff IDs -> ClearFaults("fault1") -> only fault1 disposed |

### Inc 14: M3 OperationCanceledException fix (2 tests)

| Test | Description |
|------|-------------|
| `DelayFault_DisposeCancelsPending_NoExceptionInWaitForPending` | Delay fault + frame triggers delay -> DisposeAsync -> WaitForPendingDelaysAsync completes without TaskCanceledException |
| `DelayFault_ApplyDispatchThrows_StoresException` | Fault causes ApplyAndDispatch throw -> GetLastDelayFaultException returns exception |

---

## Sprint 12: WPF HIL Panel (10 tests)

### Inc 15: HilMode selection (3 tests)

| Test | Description |
|------|-------------|
| `ModeSwitch_ToVirtualEcu_SetsEcuScriptPathActive` | Select VirtualEcu -> TracePath disabled, EcuScriptPath enabled |
| `ModeSwitch_ToHardware_SetsHardwareChannelActive` | Select Hardware -> HardwareChannel enabled, others disabled |
| `HilRunRequest_ModeField_ToCliArgs_MapsCorrectly` | Mode=VirtualEcu, EcuScriptPath="x.json" -> CliArgs.EcuScriptPath="x.json", TracePath=null |

### Inc 16: File browse (2 tests)

| Test | Description |
|------|-------------|
| `BrowseCommand_DbcFilter_CallsFileDialogService` | Browse DBC -> ShowOpenDialog called, DbcPath updated |
| `BrowseCommand_UserCancels_NoChange` | ShowOpenDialog returns null -> DbcPath unchanged |

### Inc 17: Progress + result tree (3 tests)

| Test | Description |
|------|-------------|
| `Progress_UpdatesPercentComplete` | Report((1,3,"case1")) -> Progress = 33.3 |
| `ResultTree_FailedCase_HasStepAndFrameNodes` | 1 failed step + frames -> TestCaseNode -> StepNode -> FrameNode |
| `ResultTree_AllPassed_NoFrameNodes` | All passed -> StepNodes exist, no FrameNodes |

### Inc 18: ECU editor Save & Run (2 tests)

| Test | Description |
|------|-------------|
| `EcuEditor_SaveAndRun_WritesTempFile_SetsEcuScriptPath` | TextBox JSON -> Save & Run -> temp .json created, EcuScriptPath set |
| `EcuEditor_EmptyJson_RunButtonDisabled` | TextBox empty -> CanExecute false |

---

## Sprint 13: Standalone Simulator (8 tests)

### Inc 19: EcuSimulatorHost lifecycle (4 tests)

| Test | Description |
|------|-------------|
| `Simulator_ConnectAndRun_StatefulVirtualEcuCreated` | Construct with FakeCanChannel -> RunAsync -> IsConnected true, InstanceCount incremented |
| `Simulator_Cancellation_DisconnectsChannel` | RunAsync running -> cancel ct -> IsConnected false |
| `Simulator_Dispose_ReleasesStatefulVirtualEcu` | After RunAsync -> InstanceCount decremented (EcuSimulatorHost disposes _ecu) |
| `Simulator_CanIdConflict_PrintsWarning` | Two ECUs same response CAN ID -> console contains "CAN ID conflict" |

### Inc 20: CLI simulate args (2 tests)

| Test | Description |
|------|-------------|
| `CliArgs_SimulateFlag_ParsedCorrectly` | --simulate --ecu script.json --hw USB1 -> Simulate=true, EcuScriptPath set |
| `CliArgs_SimulateWithoutEcu_ThrowsArgumentException` | --simulate --hw USB1 (no --ecu) -> ArgumentException |

### Inc 21: E2E with FakeCanChannel (2 tests)

| Test | Description |
|------|-------------|
| `Simulator_E2E_FakeChannelReceivesUdsRequest_EcuResponds` | FakeCanChannel.SimulateFrame(UDS request) -> response frame received via WriteAsync spy |
| `Simulator_E2E_SecurityAccess_FullFlow` | 0x27 seed -> receive seed -> 0x27 key (XOR 0xAA) -> positive response |

---

## Sprint 14: LLM Analysis (9 tests)

### Inc 22: HilPromptBuilder (4 tests)

| Test | Description |
|------|-------------|
| `PromptBuilder_ExcludesPassedCases_OnlyFailedInPrompt` | 3 cases (2 passed, 1 failed) -> prompt contains only failed case name |
| `PromptBuilder_FramesTruncated_At20Frames` | 30 FramesAroundFailure -> prompt contains at most 20 frame hex lines |
| `PromptBuilder_WithEcuScript_IncludesStateNames` | EcuScript with states -> prompt contains "States:" + names |
| `PromptBuilder_WithoutEcuScript_OmitsStatesSection` | ecuScript=null -> no "## ECU States" section |

### Inc 23: HilAnalysisService (3 tests)

| Test | Description |
|------|-------------|
| `AnalysisService_MockHttpClient_ReturnsContent` | Mock handler returns {"choices":[{"message":{"content":"Root cause:..."}}]} -> AnalyzeAsync returns content |
| `AnalysisService_MissingApiKey_ReturnsUnavailable` | SimpleCredentialStore empty -> AnalyzeAsync returns Unavailable("API key not configured") |
| `AnalysisService_HttpError_ReturnsError` | Mock handler returns 500 -> AnalyzeAsync returns error result (not exception) |

### Inc 24: SimpleCredentialStore (2 tests)

| Test | Description |
|------|-------------|
| `CredentialStore_SetThenGet_ReturnsValue` | SetAsync("key","val") -> GetAsync("key") returns "val" (**R5 B1-R4 regression guard**) |
| `CredentialStore_GetFromEnvVar_ReturnsValue` | Env var HIL_DEEPSEEK_API_KEY set -> GetAsync returns value |

---

## Test count summary

| Sprint | Increments | Tests |
|--------|-----------|-------|
| 9 (ODX) | Inc 0-3 | 10 |
| 10 (Generators) | Inc 4-8 | 11 |
| 11 (Reporting + M2/M3) | Inc 9-14 | 15 |
| 12 (WPF Panel) | Inc 15-18 | 10 |
| 13 (Simulator) | Inc 19-21 | 8 |
| 14 (LLM) | Inc 22-24 | 9 |
| **Total** | **25 increments** | **63** |

---

## R5 boundary case coverage

| R5 Finding | Test(s) covering it |
|-----------|-------------------|
| **L1-R5**: DidReadoutGenerator must return NRC byte[] (not throw) | `DidReadoutGenerator_DidNotFound_ReturnsNrcByteArray` (Inc 7) |
| **B1-R5**: ReadSafely must handle FileNotFoundException on first run | `TrendTracker_FirstRun_CreatesFileWithOneEntry` (Inc 10) |
