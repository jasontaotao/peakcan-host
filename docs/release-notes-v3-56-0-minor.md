# v3.56.0 MINOR — ScriptViewModel god-class refactor

> Status: W38 SHIP — 23rd god-class overall, 1st 3-partial App/ViewModels god-class of W38
>
> Date: 2026-07-17
>
> Sister of W22 RecentSessionsService + W34 DbcSendViewModel + W37 AscLocator.

## Headline

**ScriptViewModel.cs** 225 → 83 LoC (**-142 LoC, -63.1%**), exceeding plan's -58% target. Extracted 3 NEW partials (`LoggingFlow.partial.cs` 44 + `OutputFlow.partial.cs` 81 + `ExecutionFlow.partial.cs` 68) into new `ScriptViewModel/` subdirectory.

## Architecture milestones

- **23rd god-class SHIPPED** (W3-W38 series)
- **14th App/ViewModels** god-class refactor (sister of W5 SignalViewModel + W7 MultiFrameSendViewModel + W11 SendViewModel + W16 ReplayViewModel + W21 ReplayViewModel 2nd-cycle + W24 DbcSendViewModel + W30 DbcViewModel + W31 AppShellViewModel + W33 StatsViewModel + W36 StatsViewModel 2nd-cycle)
- **27th subdirectory-pattern deployment** (1st of W38)
- **2nd 3-partial subdirectory deployment** (W37 AscLocator was 1st)
- **3rd cycle** where W19 R1 LESSON ENHANCED caught a non-contiguous-block extraction scenario (T2)
- **3 NEW 1/3 lesson candidates** confirmed (RunAsync as orchestrator-with-async-await + CanExecute-guard co-location + 2nd 3-partial subdirectory deployment)

## LoC formula EXACT (W8.5 D7 32-locked)

| Task | Flow | Range | LoC deleted | Main after | Marker |
|---|---|---|---|---|---|
| T1 | Logging | L196-225 | 30 | 195 | 1 |
| T2 | Output (non-contiguous 3-block) | L26-28 + L33-34 + L130-185 | 61 | 132 | 1 |
| T3 | Execution | L73-121 | 49 | 83 | 1 |
| **Total** | **3 partials** | **non-contiguous** | **140** | **83** | **3** |

**Per-file final LoC**:
- `ScriptViewModel.cs` main = **83 LoC** (plan target ~95, achieved 83 = -12 better)
- `LoggingFlow.partial.cs` = **44 LoC**
- `OutputFlow.partial.cs` = **81 LoC**
- `ExecutionFlow.partial.cs` = **68 LoC**
- **Total = 276 LoC** (+51 over plan's ~225 estimate; +51 = 3 × 17-LoC cross-flow header block which plan estimates omitted)

## Verification

- `dotnet build src/`: 0 errors, 0 warnings
- `dotnet test --filter "FullyQualifiedName~ScriptViewModel"`: 12/12 PASS
- `dotnet test` (full suite, single-threaded retry): **1456 PASS / 0 FAIL / 5 SKIP** (matches v3.55.0 baseline; transient Core.Tests parallel-test flake cleared on retry — sister of W34/W35 pattern)
- LoC main `ScriptViewModel.cs` ≤ 100 (achieved 83)
- 3 NEW partial files in `ScriptViewModel/` subdirectory
- 5 `[ObservableProperty]` backing fields remain in main (W19+W22+W23+W34+W37 sister)
- `[RelayCommand]` attribute traveled with methods (sister of W3-W37 + W35 T2 pattern)
- DI registration unchanged (`AddSingleton<ScriptViewModel>()` factory in AppServicesFlow.cs)
- XAML bindings unchanged (`RunCommand` + `StopCommand` + `ClearOutputCommand` all regenerate correctly via CommunityToolkit.Mvvm source-gen)

## What stays the same

- Public API surface (`ScriptViewModelTests` pass without modification)
- `ScriptText` + `IsRunning` + `StatusText` + `IsEditorReady` + `EditorError` properties
- `RunCommand` + `StopCommand` + `ClearOutputCommand` (source-gen from [RelayCommand])
- `MaxOutputLines` const (moved to OutputFlow but stays `public`)
- `OnWebView2InitFailed` public API (moved to LoggingFlow)
- `Dispose()` method

## Sister-lesson candidates confirmed at W38 SHIP

- `partial-extraction-must-use-original-code-from-head-not-fabricated-api` (3/3 LOCKED W21): W38 5th god-class application (T1+T2+T3) — 24th application total; verbatim 0-byte diff in T3 reviewer
- `add-partial-keyword-to-monolithic-class-before-extraction` (3/3 LOCKED W21): held (W38 class already partial)
- `subdirectory-partials-pattern-empirical-26-precedents` (3/3 LOCKED W20): W38 27th deployment, sister-of-W37
- **NEW 1/3** `output-buffer-with-dispatcher-timer-must-stay-coupled-in-same-partial` (W38 1st observation): `_outputBuffer` + `_bufferLock` + `OnOutputReceived` + `FlushOutputBuffer` + `ClearOutput` 同 partial 不可分
- **NEW 1/3** `largest-method-can-move-when-flow-is-orchestrator-with-async-await-chain` (W38 1st observation): RunAsync (~34 LoC) async-await chain moved
- **NEW 1/3** `canexecute-guards-can-move-to-same-partial-as-their-relaycommand-methods` (W38 1st observation): CanRun + CanStop 同 partial with Run + Stop
- **NEW 1/3** `webview2-init-failure-handler-must-stay-coupled-with-logger-message-partials-in-same-partial` (W38 1st observation): OnWebView2InitFailed + 5 [LoggerMessage] 同 partial
- **NEW 1/3** `const-without-modifier-can-move-to-flow-partial-when-class-has-2-plus-flows-using-it` (W38 1st observation): MaxOutputLines const moved
- **NEW 1/3** `3-partial-subdirectory-pattern-empirical-w34-w37-w38` (W38 1st observation): 2nd 3-partial subdirectory deployment
- **NEW 1/3** `plan-per-file-loc-estimate-omits-cross-flow-header-block` (W38 1st observation from T3 reviewer): per-file estimates in plan should include ~17 LoC for using-imports + namespace + class-decl + comment block
- **NEW 1/3** `non-contiguous-block-deletion-requires-reverse-order-execution` (W38 1st observation from T2): 3-block deletion script must execute in reverse order so earlier line numbers stay valid

## W19 R1 LESSON ENHANCED — 3rd + 4th + 5th 0-failure applications

- **T1**: re-grep post-T0 boundaries → exact range (196..225) → first-attempt PASS with delta=30 EXACT
- **T2**: re-grep post-T1 boundaries → caught plan's wrong OUTPUT_START=26 (which assumed contiguity but content was non-contiguous across L26-28 + L33-34 + L130-185) → 3-block deletion per W23 LESSON, first-attempt PASS
- **T3**: re-grep post-T2 boundaries → exact range (73..121) → first-attempt PASS with delta=49 EXACT

All 3 tasks = 0 W19 R1 failures. Pattern: **re-grep BEFORE every deletion script** continues to prevent fabrication incidents. **5th 0-failure application** of W19 R1 LESSON ENHANCED (after W33 T1+T2+T3 and W34 T2 + W35 T1+T2 and W37 T1+T2+T3 successes).

## Cumulative LoC reduction (W3-W38)

| Cycle | God-class | LoC reduction |
|---|---|---|
| W3-W34 (30 cycles) | various | -3,671 LoC |
| W35 | PeakCanChannel 2nd-cycle | -116 LoC |
| W36 | StatsViewModel | ~-150 LoC (W36 MINOR) |
| W37 | AscLocator | -131 LoC (-58.2%) |
| **W38** | **ScriptViewModel** | **-142 LoC (-63.1%)** |
| **Total** | **32 god-classes** | **~ -4,210 LoC** |

## What does NOT change

- No new test cases added (refactor PATCH with zero test delta)
- No public/internal API change
- No facade pattern (W3-W37 CONFIRMED direct partial-class visibility)
- No `[LoggerMessage]` partial duplication risk (5 distinct EventId values; safe)
- No `ScriptEngine` / `ScriptEnvironment` refactor (separate concern)

## Next steps

- **W39+ candidates**: TraceSessionBundle (247 LoC, no partials), TraceViewerService (217 LoC, no partials), TraceSessionAutoSaver (212 LoC, no partials), DbcViewModel (208 LoC, no partials)
- 10 NEW 1/3 lesson candidates need 2nd observation each before promotion to STANDALONE
- Real feature work -- user direction needed