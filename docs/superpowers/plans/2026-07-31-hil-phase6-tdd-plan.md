# HIL Phase 6 TDD Plan

> Spec: `docs/superpowers/specs/2026-07-31-hil-phase6-spec.md` (v5, 0 CRITICAL)
> Created: 2026-07-31
> Sprints: 15-19 | Increments: 0-24 | Tests: 34

---

## Pre-checks (verify before coding)

| # | Check | Command | Expected |
|---|-------|---------|----------|
| 0 | Build passes | `dotnet build` | 0 errors |
| 1 | HIL tests green | `dotnet test --filter "FullyQualifiedName~HIL"` | 0 failed |
| 2 | `CliArgs` has no `ExportFramesDir` | grep `ExportFramesDir` in `CliArgs.cs` | 0 matches |
| 3 | `Program.cs` only handles trx/junit | grep `cli.Format` in `Program.cs` | only `trx`/`junit` |
| 4 | `HilViewModel.AnalyzeAsync` is stub | grep `stub` in `HilViewModel.cs` | line ~112 |
| 5 | `IHilAnalysisService` not in DI | grep `IHilAnalysisService` in `HeadlessHostBuilder.cs` | 0 matches |
| 6 | `EcuStateMachine` initial state hardcoded | grep `_currentState = "default"` in `EcuStateMachine.cs` | line 13, 102 |
| 7 | `EcuScript` has no `InitialState` | grep `InitialState` in `EcuScript.cs` | 0 matches |
| 8 | `OdxToEcuScriptAdapter.Load` has no out param | grep `out string` in `OdxToEcuScriptAdapter.cs` | 0 matches |
| 9 | `RequestBasedMappers.ReadServiceId` is private | grep `private static.*ReadServiceId` in `RequestBasedMappers.cs` | line 441 |
| 10 | Infrastructure.csproj has no Polly | grep `Polly` in Infrastructure.csproj | 0 matches |
| 11 | `HilAnalysisService` ctor is `(ICredentialStore, HttpClient?)` | grep `public HilAnalysisService` in `HilAnalysisService.cs` | line 23 |
| 12 | Demo_Cdd.odx-d has 15 STATE-TRANSITIONs | grep -c `STATE-TRANSITION ID=` Demo_Cdd.odx-d | 15 |
| 13 | `HilViewModel` ctor has 3 params | grep `public HilViewModel` in `HilViewModel.cs` | runner, logger, fileDialog |
| 14 | 9 `new HilViewModel(` calls in tests | grep -rn `new HilViewModel(` tests/ | 9 matches |
| 15 | 9 `adapter.Load(` calls in tests | grep -rn `adapter\.Load(` tests/ | 9 matches |

---

## Sprint 15: CLI 报告格式接线 (6 tests)

### Inc 0: CliArgs ExportFramesDir + format html

**Files**: `Infrastructure/Cli/CliArgs.cs`, `Infrastructure.Tests/Cli/Reporting/CliReportIntegrationTests.cs`

| Test | Description |
|------|-------------|
| `CliArgsParser_ExportFramesDir_ParsesFlag` | `--export-frames /tmp/out` sets `ExportFramesDir` |
| `CliArgsParser_FormatHtml_ParsesFormat` | `--format html` sets `Format = "html"` |
| `CliArgsParser_FormatHtmlJunit_ParsesFormat` | `--format html+junit` sets `Format = "html+junit"` |

**Implementation**:
- Add `string? ExportFramesDir = null` to `CliArgs` record (append after `Simulate`)
- Add `case "--export-frames": exportFramesDir = args[++i]; break;` to parser
- Update `PrintHelp` to list `html`, `html+junit`, `--export-frames`
- Update all existing `new CliArgs(...)` calls to include `ExportFramesDir: null` (or rely on default)

**Key constraint**: `CliArgs` is a positional record -- new field must be appended at the end to avoid breaking existing constructor calls. Check `CliArgsParser.Parse` return statements (3 return paths: import-odx, simulate, normal).

### Inc 1: Program.cs report switch + frame export

**Files**: `PeakCan.Host.Cli/Program.cs`, `Infrastructure.Tests/Cli/Reporting/CliReportIntegrationTests.cs` (continued)

| Test | Description |
|------|-------------|
| `Program_ConsoleFormat_OutputsSummary` | `--format console` produces `ConsoleSummaryFormatter.Format(result)` output |
| `Program_HtmlFormat_WritesHtmlFile` | `--format html --output report.html` creates file containing `<html` |
| `Program_ExportFrames_CreatesDirectory` | `--export-frames /tmp/frames` creates directory and writes .asc files |

**Implementation**:
- Replace `if (cli.OutputPath is not null) { ... }` block (Program.cs:80-86) with switch on `cli.Format`
- Add `case "html":` / `case "html+junit":` / `case "junit":` / `case "trx":` / `case "console": default:`
- Move `return result.AllPassed ? 0 : 1;` after the switch + frame export block
- Console mode: `Console.WriteLine(ConsoleSummaryFormatter.Format(result))`
- HTML mode: `TrendTracker.Load` + `HtmlReportGenerator.GenerateHtml` + `File.WriteAllTextAsync` + `TrendTracker.Record`
- Frame export: `if (cli.ExportFramesDir is not null) { await FrameCaptureExporter.ExportAsync(...); }`

**Key constraint**: switch replaces the existing `if` block inside `try { ... } finally { channel2.DisconnectAsync(); }`. The `return` must be after switch but still inside `try`.

---

## Sprint 16: LLM 分析接线 (6 tests)

> **Dependency**: Sprint 19 must be done first (IHilAnalysisService DI registration)

### Inc 2: HilViewModel ctor + AnalyzeAsync wiring

**Files**: `App/ViewModels/HilViewModel.cs`, `App.Tests/ViewModels/HilViewModelAnalysisTests.cs`

| Test | Description |
|------|-------------|
| `AnalyzeAsync_LastResultNull_ReturnsEarly` | No prior run -> AnalyzeAsync does nothing, AnalysisResult stays empty |
| `AnalyzeAsync_AllPassed_ReturnsEarly` | All passed -> AnalyzeAsync does nothing |
| `AnalyzeAsync_ServiceReturnsContent_UpdatesAnalysisResult` | Mock IHilAnalysisService returns AnalysisResult.Success -> AnalysisResult == content |
| `AnalyzeAsync_ServiceUnavailable_ShowsReason` | Mock returns AnalysisResult.Unavailable -> AnalysisResult contains reason |
| `CanAnalyze_NotRunningWithFailedResult_ReturnsTrue` | After failed run, CanAnalyze is true |
| `CanAnalyze_IsRunning_ReturnsFalse` | During analysis, CanAnalyze is false |

**Implementation**:
- Add `private readonly IHilAnalysisService _analysisService;`
- Add `private TestSuiteResult? _lastResult;`
- Add `[ObservableProperty] private string _analysisResult = "";`
- Add `[ObservableProperty] private bool _isAnalyzing = false;`
- Add `[ObservableProperty] private bool _enableAnalyze = false;`
- Ctor: add `IHilAnalysisService analysisService` as 4th param
- Replace stub `AnalyzeAsync` with real implementation
- In `RunAsync`: after `_runner.RunAsync`, set `_lastResult = result; AnalyzeCommand.NotifyCanExecuteChanged();`
- Change `EnableAnalyze: false` to `EnableAnalyze: EnableAnalyze`

**Mock strategy**: Use `NSubstitute.Substitute.For<IHilAnalysisService>()`, configure `AnalyzeAsync` to return `AnalysisResult.Success("...")` or `AnalysisResult.Unavailable("...")`.

### Inc 2b: Update 9 existing test call sites

**Files** (4 files, 9 call sites):
- `App.Tests/ViewModels/HilViewModelTests.cs` (1)
- `App.Tests/ViewModels/AppShellViewModelTests.cs` (6)
- `App.Tests/ViewModels/AppShellViewModelMessageBoxPromptTests.cs` (1)
- `App.Tests/Windows/UdsWindowTests.cs` (1)

Each `new HilViewModel(runner, logger, fileDialog)` -> `new HilViewModel(runner, logger, fileDialog, Substitute.For<IHilAnalysisService>())`.

### Inc 2c: HilView.xaml UI binding

**Files**: `App/Views/HilView.xaml`

No test (XAML change, verified by build + manual check). Add:
- `CheckBox Content="Analyze" IsChecked="{Binding EnableAnalyze}"` next to Faults CheckBox
- `TextBox Text="{Binding AnalysisResult}" IsReadOnly="True" AcceptsReturn="True"` below Results Grid

---

## Sprint 17: Credential Store 统一 (5 tests)

### Inc 3: ChainedCredentialStore

**Files**: `Infrastructure/HIL/Analysis/ChainedCredentialStore.cs` (NEW), `Infrastructure.Tests/HIL/Analysis/ChainedCredentialStoreTests.cs` (NEW)

| Test | Description |
|------|-------------|
| `GetAsync_PrimaryHasValue_ReturnsPrimary` | stores[0] returns "key1" -> returns "key1" |
| `GetAsync_PrimaryNull_FallsBackToSecondary` | stores[0] returns null, stores[1] returns "key2" -> returns "key2" |
| `SetAsync_WritesToFirstStoreOnly` | SetAsync -> stores[0].GetAsync returns value, stores[1].GetAsync returns null |
| `DeleteAsync_DeletesFromAllStores` | DeleteAsync -> both stores' DeleteAsync called |
| `DeleteAsync_StoreThrowsCredentialStoreException_Continues` | stores[0].DeleteAsync throws -> stores[1].DeleteAsync still called |

**Implementation**:
- `ChainedCredentialStore : ICredentialStore`
- `params ICredentialStore[] stores` constructor
- `GetAsync`: iterate stores, return first non-empty
- `SetAsync`: write to `_stores[0]` only
- `DeleteAsync`: iterate all, catch `CredentialStoreException` per store

**Mock strategy**: Use simple in-memory `FakeCredentialStore : ICredentialStore` (Dictionary-based, no NSubstitute needed).

---

## Sprint 18: ODX STATE-CHART + Routine POS-RESPONSE (13 tests)

### Inc 4: OdxStateChartInfo + OdxStateChartExtractor

**Files**: `Core/Uds/Odx/OdxStateChartInfo.cs` (NEW), `Core/Uds/Odx/OdxStateChartExtractor.cs` (NEW), `Core.Tests/Uds/Odx/OdxStateChartExtractorTests.cs` (NEW)

| Test | Description |
|------|-------------|
| `TryExtract_DemoCddSecurityChart_ReturnsLockedStartState` | Parse Demo_Cdd.odx-d with semantic="SECURITY" -> StartState == "Locked" |
| `TryExtract_DemoCddDefaultChart_ReturnsFirstChart` | No semantic -> returns first chart (Session, StartState="Default") |
| `TryExtract_NoStateChart_ReturnsNull` | Parse complete.odx -> null |
| `TryExtract_DemoCddSecurityChart_Has9Transitions` | SecurityAccess chart -> 9 StateChartTransition entries |
| `BuildDiagServiceTransitionMap_DemoCdd_ReturnsTransitionRefs` | DIAG-SERVICE _637 -> contains _639, _640, _641 |

**Implementation**:
- `OdxStateChartInfo` record: `(ChartName, StartState, StateNames, Transitions)`
- `StateChartTransition` record: `(TransitionId, SourceState, TargetState)`
- `OdxStateChartExtractor.TryExtract`: find STATE-CHART by SEMANTIC, extract START-STATE-SNREF, STATES, STATE-TRANSITIONS (SOURCE-SNREF / TARGET-SNREF)
- `OdxStateChartExtractor.BuildDiagServiceTransitionMap`: DIAG-SERVICE -> STATE-TRANSITION-REFS -> ID-REF list

**Test fixtures**: Use existing `tests/PeakCan.Host.Core.Tests/Fixtures/Odx/Demo_Cdd.odx-d` and `complete.odx`. Load via `XDocument.Load(fixturePath)`.

### Inc 5: RequestBasedMappers -- ExtractRoutineResponses

**Files**: `Core/Uds/Odx/RequestBasedMappers.cs` (MODIFY), `Core.Tests/Uds/Odx/RequestBasedMappersRoutineResponseTests.cs` (NEW)

| Test | Description |
|------|-------------|
| `ExtractRoutineResponses_DemoCdd_ReturnsRoutineResponseBytes` | Demo_Cdd has 0x31 routines -> dictionary non-empty, values start with 0x71 |
| `ExtractRoutineResponses_NoRoutines_ReturnsEmpty` | complete.odx (no 0x31) -> empty dictionary |
| `ExtractRoutineResponses_ResponseStartsWith_0x71_AndSubFunc` | Each value: [0] == 0x71, [1] == subFunc from REQUEST |
| `ExtractResponseBytes_NoDataParams_ReturnsEmpty` | POS-RESPONSE with no SEMANTIC="DATA" PARAMs -> empty byte[] |

**Implementation**:
- Change `ReadServiceId`, `ReadSubfunctionParam`, `ParseByte` from `private` to `internal`
- Add `ExtractRoutineResponses(XDocument, XNamespace) -> IReadOnlyDictionary<ushort, byte[]>`
  - Walk REQUEST (0x31) -> DIAG-SERVICE -> POS-RESPONSE-REF -> POS-RESPONSE -> PARAM SEMANTIC="DATA" -> CODED-VALUE
  - Build `[0x71, subFunc, ...dataBytes]`
- Add `ExtractResponseBytes(XElement pos, XNamespace ns) -> byte[]` (private helper)
  - Read `CODED-VALUE` with `NumberStyles.Integer` (decimal, NOT hex)
  - Fallback to `PHYSICAL-VALUE`

### Inc 6: RequestBasedMappers visibility change + EcuStateMachine/EcuScript/EcuScriptLoader

**Files**: `Core/Uds/Odx/RequestBasedMappers.cs` (MODIFY), `Core/HIL/Contracts/EcuStateMachine.cs` (MODIFY), `Infrastructure/HIL/EcuScript.cs` (MODIFY), `Infrastructure/HIL/EcuScriptLoader.cs` (MODIFY)

| Test | Description |
|------|-------------|
| `EcuStateMachine_CustomInitialState_ResetRestoresToInitial` | Construct with initialState="Locked" -> Reset() -> CurrentState == "Locked" |
| `EcuStateMachine_DefaultInitialState_ResetRestoresToDefault` | Construct without initialState -> Reset() -> CurrentState == "default" |
| `EcuScriptLoader_LoadsInitialState_FromJson` | JSON with `"initialState": "Locked"` -> EcuScript.InitialState == "Locked" |
| `EcuScriptLoader_MissingInitialState_DefaultsToDefault` | JSON without initialState -> EcuScript.InitialState == "default" |

**Implementation**:
- `EcuStateMachine`: add `private readonly string _initialState;`, constructor param `string initialState = "default"`, `Reset()` uses `_initialState`
- `EcuScript`: add `public string InitialState { get; init; } = "default";`
- `EcuScriptLoader.ParseStateMachine`: add `string initialState` param, pass to `new EcuStateMachine(allTransitions, generators, initialState)`
- `EcuScriptLoader.ParseEcuScript`: `element.TryGetProperty("initialState", out var isEl) ? isEl.GetString() ?? "default" : "default"`, pass to `ParseStateMachine`, set `EcuScript.InitialState`

### Inc 7: OdxToEcuScriptAdapter STATE-CHART integration + routine response

**Files**: `Infrastructure/HIL/Odx/OdxToEcuScriptAdapter.cs` (MODIFY), `Infrastructure/HIL/Odx/OdxEcuScriptImporter.cs` (MODIFY), `Infrastructure.Tests/HIL/Odx/OdxToEcuScriptAdapterStateChartTests.cs` (NEW)

| Test | Description |
|------|-------------|
| `Load_DemoCdd_ReturnsInitialStateLocked` | Load Demo_Cdd -> `out initialState` == "Locked" |
| `Load_CompleteOdx_ReturnsInitialStateDefault` | Load complete.odx -> `out initialState` == "default" |
| `Load_DemoCdd_SecurityTransitionHasFromStateLocked` | 0x27 0x02 transition -> FromState == "Locked", ToState == "UnlockedL1" |
| `Load_DemoCdd_RoutineResponseNotHardcoded` | 0x31 routine transition -> response bytes from ODX (not just [0x71, 0x01]) |

**Implementation**:
- `Load(string odxPath, out string initialState)`:
  - `initialState = "default"`
  - `OdxStateChartExtractor.TryExtract(doc, ns, "SECURITY")` -> update transitions' FromState/ToState
  - `RequestBasedMappers.ExtractRoutineResponses(doc, ns)` -> replace hardcoded `[0x71, subFunc]`
  - Build `ServiceRequest` struct, `BuildDiagServiceToRequestMap` (private, calls `RequestBasedMappers.ReadServiceId/ReadSubfunctionParam`)
- `OdxEcuScriptImporter.ImportToJson`: `adapter.Load(odxPath, out var initialState)`, add `initialState` to JSON output
- Update 9 existing test call sites: `adapter.Load(tempPath)` -> `adapter.Load(tempPath, out _)`

**Key constraint**: `t.SubFunction is { } sub` pattern -- only transitions with non-null SubFunction are matched (DID Read 0x22 without SubFunction stays wildcard).

---

## Sprint 19: HttpClient 工厂化 + Polly Retry (4 tests)

### Inc 8: HilAnalysisService ctor change + Polly registration

**Files**: `Infrastructure/PeakCan.Host.Infrastructure.csproj` (MODIFY), `Infrastructure/HIL/Analysis/HilAnalysisService.cs` (MODIFY), `Infrastructure/HIL/HeadlessHostBuilder.cs` (MODIFY), `App/Composition/AppHostBuilder/AppServicesFlow.cs` (MODIFY), `Infrastructure.Tests/HIL/Analysis/HilAnalysisServiceRetryTests.cs` (NEW), `Infrastructure.Tests/HIL/Analysis/Sprint14Tests.cs` (MODIFY)

| Test | Description |
|------|-------------|
| `AnalyzeService_RetryOn500_EventuallySucceeds` | MockHandler returns 500 once, then 200 -> AnalyzeAsync returns content |
| `AnalyzeService_Retry3Times_ThenFails` | MockHandler always returns 500 -> AnalyzeAsync returns Unavailable (3 retries) |
| `AnalyzeService_OperationCancelled_DoesNotRetry` | CancellationToken cancelled -> no retry, throws/returns unavailable |
| `AnalyzeService_Success_NoRetry` | MockHandler returns 200 immediately -> single call |

**Implementation**:
- `Infrastructure.csproj`: add `<PackageReference Include="Microsoft.Extensions.Http.Polly" />`
- `HilAnalysisService` ctor: `(HttpClient httpClient, ICredentialStore credentialStore)`, keep `_ownsHttpClient = false`, Dispose empty
- `HeadlessHostBuilder`: `AddHttpClient<IHilAnalysisService, HilAnalysisService>(...)` + `.AddPolicyHandler(GetRetryPolicy())` + register `ICredentialStore` as `SimpleCredentialStore`
- `AppServicesFlow`: same `AddHttpClient` + `GetRetryPolicy()`
- `GetRetryPolicy`: `HandleTransientHttpError().OrResult(429).WaitAndRetryAsync(3, n => TimeSpan.FromSeconds(2^n))`
- `Sprint14Tests.cs`: update 3 calls:
  - `:96` `new HilAnalysisService(credentialStore, httpClient)` -> `new HilAnalysisService(httpClient, credentialStore)`
  - `:108` `new HilAnalysisService(credentialStore)` -> `new HilAnalysisService(new HttpClient(), credentialStore)` (or `new HttpClient(new MockHttpMessageHandler(...))`)
  - `:123` same as `:96`

**Mock strategy**: `MockHttpMessageHandler` (existing in Sprint14Tests.cs) that returns 500 N times then 200. Count invocations to verify retry count.

**Key constraint**: Polly retry tests need `AddHttpClient` + DI container, OR direct policy application. For unit tests, use `Policy` directly on `HttpClient` handler instead of full DI. Alternative: test the policy in isolation, trust `AddHttpClient` wiring.

---

## Test Count Summary

| Sprint | Inc | Tests | Running Total |
|--------|-----|-------|---------------|
| 15 | 0 | 3 | 3 |
| 15 | 1 | 3 | 6 |
| 16 | 2 | 6 | 12 |
| 17 | 3 | 5 | 17 |
| 18 | 4 | 5 | 22 |
| 18 | 5 | 4 | 26 |
| 18 | 6 | 4 | 30 |
| 18 | 7 | 4 | 34 |
| 19 | 8 | 4 | 38 |
| **Total** | | **38** | |

> Spec says 34 tests; TDD plan adds 4 extra (EcuStateMachine Reset tests in Inc 6). These verify the L1-R4 critical fix.

---

## Execution Order

```
Sprint 15 (Inc 0, 1) ──────────────────> independent
Sprint 17 (Inc 3) ─────────────────────> independent
Sprint 18 (Inc 4, 5, 6, 7) ────────────> independent (Inc 4->5->6->7 sequential)
Sprint 19 (Inc 8) ─────────────────────> independent
Sprint 16 (Inc 2, 2b, 2c) ─────────────> depends on Sprint 19
```

**Recommended**: 15 -> 17 -> 18 -> 19 -> 16

---

## R5 Boundary Case Coverage

| R5 Finding | Test | Inc |
|------------|------|-----|
| L1-R4: Reset() must use _initialState | `EcuStateMachine_CustomInitialState_ResetRestoresToInitial` | Inc 6 |
| L2-R4: Load signature breaks 9 test calls | 9 call sites updated to `out _` | Inc 7 |
| L3-R4: HilAnalysisService ctor param order | Sprint14Tests 3 calls updated with explicit code | Inc 8 |
| L4-R4: AnalyzeCommand.CanExecute not notified | `AnalyzeCommand.NotifyCanExecuteChanged()` in RunAsync | Inc 2 |
| T2-R1: CODED-VALUE decimal not hex | `ExtractResponseBytes` uses `NumberStyles.Integer` | Inc 5 |
