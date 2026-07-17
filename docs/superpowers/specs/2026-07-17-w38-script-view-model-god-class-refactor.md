# W38 god-class refactor — ScriptViewModel (23rd overall)

> 状态：W38 设计 spec（v3.55.0 series 之后；god-class refactor 节奏延续）
>
> 触发条件：W35 (PeakCanChannel 20th) + W36 (StatsViewModel 21st) + W37 (AscLocator 22nd) 全部 SHIPPED。继续 W3-W37 god-class refactor 节奏。ScriptViewModel 是 App-layer ViewModel 子目录最大且唯一无 partial 的 god-class（225 LoC，4 职责清晰）。

## 目标

`src/PeakCan.Host.App/ViewModels/ScriptViewModel.cs` 225 LoC → 拆分为 `ScriptViewModel/` 子目录 + 3 NEW partials。Public API + tests + DI 全部不变。

## 当前代码证据（已坐实）

- `src/PeakCan.Host.App/ViewModels/ScriptViewModel.cs:225` — `public sealed partial class ScriptViewModel : ObservableObject` (line 21)
- 2 readonly fields: `_logger` (L23) + `_engine` (L24)
- 2 buffer fields: `_outputBuffer` (L27) + `_bufferLock` (L28)
- 1 timer field: `_flushTimer` (L31)
- 1 const `MaxOutputLines` = 1000 (L34)
- 5 `[ObservableProperty]` backing fields: `_scriptText` + `_isRunning` + `_statusText` + `_isEditorReady` + `_editorError` (L36-53)
- 1 `ObservableCollection<string> OutputLines { get; }` (L56)
- 1 public ctor (L58-78, 21 LoC)
- 1 `Dispose()` (L190-194)
- 1 `[RelayCommand] RunAsync` (~34 LoC, LARGEST, L82-114)
- 1 `CanRun()` (L116)
- 1 `[RelayCommand] Stop` (L120-126)
- 1 `CanStop()` (L128)
- 1 `[RelayCommand] ClearOutput` (L132-139)
- 1 `OnOutputReceived(ScriptOutputLine)` (L145-151)
- 1 `FlushOutputBuffer()` (L157-185, ~29 LoC, 2nd LARGEST)
- 1 public `OnWebView2InitFailed(Exception, string)` (L219-224)
- 5 `[LoggerMessage]` partials: `LogScriptCompleted` + `LogScriptFailed` + `LogScriptException` + `LogScriptStopped` + `LogWebView2InitFailed` (L196-209)

4 distinct responsibilities: script execution + output buffering + UI state + logging.

## 5 个 D 决策（D1-D5）

| ID | 决策 | 选择 | 理由 |
|---|---|---|---|
| D1 | NEW partials 数量 | **3 NEW** (`LoggingFlow.partial.cs` + `OutputFlow.partial.cs` + `ExecutionFlow.partial.cs`) | 4 职责清晰拆分；sister of W22 RecentSessionsService + W34 DbcSendViewModel + W37 AscLocator |
| D2 | Partial keyword | 已有（L21 `public sealed partial class ScriptViewModel : ObservableObject`）—— 不改 | W19 R1 sister，无需加 keyword |
| D3 | 留在 main 的成员 | `ScriptViewModel` class declaration + 2 readonly fields (`_logger` + `_engine`) + 1 timer field (`_flushTimer`) + 5 `[ObservableProperty]` backing fields + `OutputLines` ObservableCollection + public ctor + `Dispose()` | 与 W19 + W23 + W34 + W36 + W37 sister：bindable properties + state stays in main |
| D4 | LARGEST method `RunAsync` (~34 LoC) | 移到 `ExecutionFlow.partial.cs` | 这是真 god-class refactor 的核心；与 W37 D4 sister (long-method-as-orchestrator) |
| D5 | `OnWebView2InitFailed` public API + `MaxOutputLines` const + `CanRun` + `CanStop` | 分别移到对应 partial：OnWebView2InitFailed + 5 [LoggerMessage] → LoggingFlow.partial.cs；MaxOutputLines + CanRun + CanStop → 同各自 caller partial | 与 caller 同 partial（sister of W37 D5 cache helpers） |
| D6 | 分支 | `feature/w38-script-view-model-god-class` | sister of W11-W37 |
| D7 | 顺序 | T1 LoggingFlow (5 [LoggerMessage] + OnWebView2InitFailed, 5 lines) → T2 OutputFlow (OnOutputReceived + FlushOutputBuffer + ClearOutput + buffer fields + MaxOutputLines) → T3 ExecutionFlow (RunAsync + Stop + CanRun + CanStop) | T1 验证 partial extract 基础；T2 验证 buffer 拆分；T3 main 完整 |

## LoC trajectory（公式 W8.5 D7 32-locked）

| Task | Flow | Range (TBD per Phase 1 exact grep) | LoC deleted | Marker | LoC main after |
|---|---|---|---|---|---|
| T1 | Logging | L196-224 (5 [LoggerMessage] + OnWebView2InitFailed) | ~30 | 1 | ~195 |
| T2 | Output | L131-185 (ClearOutput + OnOutputReceived + FlushOutputBuffer + MaxOutputLines const + 2 buffer fields + _bufferLock) | ~60 | 1 | ~135 |
| T3 | Execution | L80-129 (RunAsync + Stop + CanRun + CanStop + 2 [RelayCommand] attributes) | ~40 | 1 | ~95 |
| T4 | release + ship | (no source) | 0 | 0 | ~95 |

**目标**：main 225 → ~95 LoC（-130 LoC, -58%）。Subdirectory 累计 3 NEW partials + main = ~225 LoC distributed across 4 files。

## 架构

```text
src/PeakCan.Host.App/ViewModels/
├── ScriptViewModel.cs                          (NEW: ~95 LoC, was 225 LoC)
└── ScriptViewModel/                            (NEW subdirectory, sister of W34 DbcSendViewModel/)
    ├── LoggingFlow.partial.cs                  (NEW: ~30 LoC, 5 [LoggerMessage] + OnWebView2InitFailed)
    ├── OutputFlow.partial.cs                   (NEW: ~60 LoC, buffer + flush + clear)
    └── ExecutionFlow.partial.cs                (NEW: ~40 LoC, RunAsync + Stop)
```

## W20 + W23 LESSON APPLIED

Per W20 T2 R1 fabrication incident + W23 T2 3-fix-cycle struct-constructor fabrication:

1. **Re-grep boundaries BEFORE running each deletion script** (re-verify before each T1/T2/T3 script run).
2. **Re-extract original code from HEAD via `git show HEAD:src/...cs | sed -n '<range>p'`** for each partial's verbatim content.
3. **Verify `ScriptEngine.RunAsync` signature** (sister Core/Service file `src/PeakCan.Host.App/Services/Scripting/ScriptEngine.cs` or similar) — confirm cancellation + error semantics.
4. **Verify `ScriptOutputLine` + `ScriptOutputLevel` records** in `src/PeakCan.Host.Core/Replay/` or sister — confirm properties used in OutputFlow.
5. **Verify `IDispatcherTimer.Tick` event signature** — `[EventHandler]` 2-arg, sister of `W11+W22+W34+W37`.
6. **Verify no `[LoggerMessage]` partial duplication risk** — W18 R1 mitigation (5 distinct messages → safe).
7. **Build + run filter tests after each task** to catch any extraction errors immediately.

## 组件拆分（part 文件位置）

**Main stays** (~95 LoC):
- using block (1-6) + namespace + class xmldoc (9-20) + outer class declaration (21) — already partial
- 2 readonly fields (`_logger` + `_engine`) + 1 timer field (`_flushTimer`)
- 5 `[ObservableProperty]` backing fields (`_scriptText` + `_isRunning` + `_statusText` + `_isEditorReady` + `_editorError`)
- 1 `ObservableCollection<string> OutputLines { get; }`
- public ctor (~21 LoC, includes 2 buffer field initializers)
- `Dispose()` (L190-194)

**LoggingFlow.partial.cs** (~30 LoC):
- 5 `[LoggerMessage]` partial methods (L196-209)
- `OnWebView2InitFailed` public API (L211-224)

**OutputFlow.partial.cs** (~60 LoC):
- `MaxOutputLines` const (L33-34)
- `_outputBuffer` Queue + `_bufferLock` object (L26-28)
- `ClearOutput` `[RelayCommand]` method (L130-139)
- `OnOutputReceived` private method (L141-151)
- `FlushOutputBuffer` private method (L153-185)

**ExecutionFlow.partial.cs** (~40 LoC):
- `RunAsync` `[RelayCommand]` method (L80-114)
- `Stop` `[RelayCommand]` method (L118-126)
- `CanRun()` + `CanStop()` private methods (L116, L128)

## 数据契约

不引入新 record / interface。Public API 100% 保留：
- `ScriptViewModel` class + `Dispose()` + `OnWebView2InitFailed(Exception, string)`
- `ScriptText` + `IsRunning` + `StatusText` + `IsEditorReady` + `EditorError` properties
- `OutputLines` ObservableCollection
- `RunCommand` + `StopCommand` + `ClearOutputCommand` (source-gen properties from [RelayCommand])
- `MaxOutputLines` const (moved to OutputFlow but stays `public`)

## 错误处理

不引入新错误处理路径。所有现有 try/catch + `[LoggerMessage]` partial 模式 verbatim 保留。

## 测试策略（TDD RED → GREEN → IMPROVE）

- 现有 `ScriptViewModelTests` 测试集必须全部保留 + PASS。
- 全套 1456 PASS / 0 FAIL / 5 SKIP（v3.55.0 ship baseline）不变。
- 不改 tests 文件。
- 不引入新 tests（refactor PATCH 通常零 test change）。

## 复评/不做项

- **ScriptViewModel 子目录 vs 同目录**：明确用子目录（sister of W34 DbcSendViewModel/）。
- **ScriptEngine DI 注入 vs ctor 内构造**：明确不 DI（sister of W36 D4）。
- **分更多 partials**（如 BufferingFlow / FlushStateFlow / RunStateFlow）：明确不做（YAGNI；3 partials 足够）。
- **W39+ 后续 refactor**（TraceSessionBundle / TraceViewerService / DbcViewModel / TraceSessionAutoSaver）：明确推到后续。
- **`ScriptEngine` / `ScriptEnvironment` / `ScriptVariableFlow` refactor**：明确不做（这些是 ScriptViewModel 的依赖；非 god-class）。

## 6 NEW 1/3 lesson candidates 观察

| 候选 | 期望观察点 |
|---|---|
| `output-buffer-with-dispatcher-timer-must-stay-coupled-in-same-partial` | W38 1st observation: `_outputBuffer` + `_bufferLock` + `OnOutputReceived` + `FlushOutputBuffer` + `ClearOutput` 同 partial 不可分（buffer state + flush + clear 相互调用） |
| `largest-method-can-move-when-flow-is-orchestrator-with-async-await-chain` | W38 1st observation: RunAsync (~34 LoC) 整段移到 ExecutionFlow（含 await + try/catch + finally；sister of W37 WalkAsync long-method variant） |
| `canexecute-guards-can-move-to-same-partial-as-their-relaycommand-methods` | W38 1st observation: CanRun + CanStop 同 partial 移到 ExecutionFlow（sister of W34 D5） |
| `webview2-init-failure-handler-must-stay-coupled-with-logger-message-partials-in-same-partial` | W38 1st observation: OnWebView2InitFailed + 5 [LoggerMessage] 同 partial（logging 职责统一） |
| `const-without-modifier-can-move-to-flow-partial-when-class-has-2-plus-flows-using-it` | W38 1st observation: `MaxOutputLines` const 移到 OutputFlow（const 与 buffer 同 partial） |
| `3-partial-subdirectory-pattern-empirical-w34-w37-w38` | NEW at W38: W34 DbcSendViewModel 3 partials + W37 AscLocator 3 partials + W38 ScriptViewModel 3 partials |

## 文件 / LoC 估算

| 文件 | LoC |
|---|---|
| `src/PeakCan.Host.App/ViewModels/ScriptViewModel.cs` | ~95 (was 225) |
| `src/PeakCan.Host.App/ViewModels/ScriptViewModel/LoggingFlow.partial.cs` | ~30 (NEW) |
| `src/PeakCan.Host.App/ViewModels/ScriptViewModel/OutputFlow.partial.cs` | ~60 (NEW) |
| `src/PeakCan.Host.App/ViewModels/ScriptViewModel/ExecutionFlow.partial.cs` | ~40 (NEW) |
| **总计** | **~225** (no net change; redistributed) |

## 待 SPEC 用户复核

本文为 spec 初稿。请 review 后批准，下一步进入 writing-plans 写实施计划。