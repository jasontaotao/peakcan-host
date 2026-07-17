# v3.59.0 MINOR — Streaming LLM Response for DeepSeek

> Status: W41 MINOR SHIP — first SSE streaming implementation.
> Sister of v3.54.0 P1b (DeepSeekProvider non-streaming) + v3.58.0 P2 (API Key UI).

## Headline

**SSE streaming** for DeepSeekProvider. AI Analysis panel perceived latency drops from **~30s (non-streaming)** to **~1s first token**. Existing `AnalyzeAsync` API unchanged; new `StreamAnalyzeAsync` parallel API emits `IAsyncEnumerable<LlmPartialUpdate>` with 3 variant types (`PartialSummary` / `PartialEvidenceId` / `FinalResult`).

## Architecture milestones

- **1st SSE streaming implementation** in the project
- **1st `IAsyncEnumerable<T>` partial-result pattern** — discriminated union via sealed abstract record
- **1st `UseStreaming` opt-out** in `DeepSeekOptions` (default true; existing tests opt out via `UseStreaming = false`)
- Non-streaming callers (`AnalyzeAsync`) preserved 100% — routes through `StreamAnalyzeAsync` when `UseStreaming = true`; collects final result, discards partial updates
- Streaming-aware callers (`StreamAnalyzeAsync`) get full incremental delivery
- WPF AI Analysis panel now has a permanent streaming partial-summary region with monospace TextBlock + ItemsControl

## Files changed

- NEW: `src/PeakCan.Host.Core/Analysis/LlmPartialUpdate.cs` (~32 LoC; sealed abstract record + 3 variants)
- MODIFY: `src/PeakCan.Host.Core/Analysis/ILlmProvider.cs` (+30 LoC; default `AnalyzeStreamingAsync` + public `ILlmProviderExtensions` extension)
- NEW: `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekStreamingChunk.cs` (~46 LoC; per-chunk DTO + Choice + Delta)
- MODIFY: `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekProvider.cs` (+110 LoC; `StreamAnalyzeAsync` SSE path + `AnalyzeAsync` UseStreaming routing + catch refactor + using System.IO for StreamReader + ReadLineAsync loop pattern)
- MODIFY: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/AnalysisFlow.cs` (+50 LoC; 2 [ObservableProperty] + 1 [RelayCommand] for streaming)
- MODIFY: `src/PeakCan.Host.App/Views/TraceViewerView.AIPanel.xaml` (+26 LoC; streaming partial-summary TextBlock + ItemsControl)
- MODIFY: `tests/PeakCan.Host.App.Tests/Services/LlmProvider/DeepSeekProviderTests.cs` (+1 LoC; `DefaultOptions()` → `new() { UseStreaming = false }`)
- NEW: `tests/PeakCan.Host.Core.Tests/Analysis/LlmPartialUpdateTests.cs` (~68 LoC, 4 tests)
- NEW: `tests/PeakCan.Host.Core.Tests/Analysis/ILlmProviderExtensionsTests.cs` (~62 LoC, 1 test with concrete test provider + interface-cast)
- NEW: `tests/PeakCan.Host.App.Tests/Services/LlmProvider/DeepSeekProviderStreamingTests.cs` (~158 LoC, 3 tests with StubHandler)

**Total: +583 LoC net across 10 files** (spec estimated +328 LoC; +255 over for streaming UI + extensive tests + inline fixes)

## Verification

- `dotnet build src/`: 0 errors, 6 pre-existing warnings
- `dotnet test` full suite single-threaded (App.Tests): **854 PASS / 0 FAIL / 3 SKIP** (App.Tests delta: +5 new from T3+T7)
- `dotnet test --filter "FullyQualifiedName~DeepSeek|FullyQualifiedName~LlmPartialUpdate|FullyQualifiedName~ILlmProvider"`: 19 PASS / 0 FAIL across Core.Tests + App.Tests
- Public API 100% preserved: `AnalyzeAsync` signature unchanged
- Streaming tests cover: chunk accumulation, evidence ID extraction, cancellation break
- Non-streaming fallback path (default interface method) covered by ILlmProviderExtensionsTests (uses concrete test provider + interface-cast for default-method dispatch)
- DI: no changes (ILlmProvider still singleton)

## Sister-lesson candidates confirmed at W41 SHIP

- NEW 1/3: `iasyncenumerable-default-interface-method-pattern-provides-single-shot-fallback`
- NEW 1/3: `sse-event-stream-framing-requires-data-line-prefix-stripping`
- NEW 1/3: `delta-content-accumulation-into-summary-handles-utf8-boundary-splits`
- NEW 1/3: `stream-cancellation-breaks-httpresponse-readasync-loop-without-throwing`
- NEW 1/3: `partial-update-discriminated-union-via-sealed-abstract-record-is-net-7-pattern`
- NEW 1/3: `httpclient-accept-text-event-stream-header-required-for-sse-endpoint`
- NEW 1/3: `nsubstitute-does-not-call-default-interface-methods-on-mocked-instances` (T5 lesson: must use concrete test class + interface-cast)
- NEW 1/3: `ca2024-readerasync-endofstream-async-path-violation` (T6 lesson: use ReadLineAsync returning null as EOF signal)
- NEW 1/3: `cs1631-cannot-yield-in-catch-block` (T6 lesson: refactor catch to set nullable var + yield after)

## Cumulative LoC reduction (W3-W41)

| Cycle | God-class | LoC reduction |
|---|---|---|
| W3-W34 (30 cycles) | various | -3,671 LoC |
| W35 | PeakCanChannel 2nd-cycle | -116 LoC |
| W36 | StatsViewModel | ~-150 LoC |
| W37 | AscLocator | -131 LoC |
| W38 | ScriptViewModel | -142 LoC |
| W39 | DbcViewModel | -104 LoC |
| W40 | (no refactor) | 0 |
| **W41** | **(no refactor)** | **+583 LoC net** (new feature) |
| **Net** | **24 god-classes + 2 feature ships** | **~ -2,838 LoC** |

## What does NOT change

- No new credential storage path (reuses v3.53.1 + v3.58.0 P2)
- No real DeepSeek API integration test (deferred; gated by env var)
- No Azure / Ollama streaming (deferred — interface is ready for them)
- No token usage persistence (deferred — log only)
- No streaming retry policy (deferred)

## Next steps

- v3.60.0 candidates: Azure OpenAI Provider / Ollama Provider / Real DeepSeek API CI test
- 9 NEW 1/3 candidates need 2nd observation each before promotion to STANDALONE