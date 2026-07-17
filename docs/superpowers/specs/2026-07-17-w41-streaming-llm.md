# W41 v3.59.0 MINOR — Streaming LLM Response for DeepSeek

> 状态：W41 设计 spec（v3.58.0 series 之后；tech-debt PATCH 路线延续）
>
> 触发条件：v3.54.0 MINOR 已 ship DeepSeekProvider（non-streaming JSON response）；用户感知延迟 ~30s 待优化；本 MINOR 接 SSE 流式响应，首 token 延迟降到 ~1s。

## 目标

为 DeepSeekProvider 加 SSE 流式响应，让 AI Analysis 面板能边收边呈现 LLM 输出（感知延迟从 ~30s 降到 ~1s 首 token），不破坏现有 v3.54.0 LlmAnalysisResult envelope 契约。

## 当前代码证据（已坐实）

- `src/PeakCan.Host.Core/Analysis/ILlmProvider.cs` (28 LoC): `DisplayName` + `AnalyzeAsync(AnalysisSession, CancellationToken) → Task<LlmAnalysisResult>`
- `src/PeakCan.Host.Core/Analysis/LlmAnalysisResult.cs` (16 LoC): sealed record (`Summary`, `AttributedEvidenceIds`, `RawResponseJson`, `Error`)
- `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekProvider.cs` (209 LoC): non-streaming `JsonSerializer.Deserialize<DeepSeekResponse>`
- `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekRequest.cs` (~30 LoC): current fields = Model + Messages (no Stream field)
- DeepSeek API supports `stream: true` → returns `text/event-stream` with `data: {...}\n\n` framing; each chunk has `choices[].delta.content` partial + `choices[].finish_reason`
- W40 v3.58.0 ships PasswordBox + Save/Remove/Test in AI Analysis panel (`src/PeakCan.Host.App/Views/TraceViewerView.AIPanel.xaml`); current UI shows whole `CurrentAnalysisSession` — no streaming infrastructure
- v3.54.0 `EvidenceIdWhitelistFilter` + `DeepSeekOptions` + DI wiring all preserved
- Tests: 1461 PASS / 0 FAIL / 5 SKIP (v3.58.0 baseline)

## 5 个 D 决策（D1-D5）

| ID | 决策 | 选择 | 理由 |
|---|---|---|---|
| D1 | 流式 API 表面 | `IAsyncEnumerable<LlmPartialUpdate>` **新增** parallel API；保留 `AnalyzeAsync`（non-streaming）作为 default | 老 callers 无破坏；新 callers 走 streaming；UI 选择使用 |
| D2 | `LlmPartialUpdate` 形状 | **3 变体** discriminated union: `PartialSummary(string Delta)` / `PartialEvidenceId(string EvidenceId)` / `FinalResult(LlmAnalysisResult Result)` | UI 根据变体追加文本 / 累积 ID / 完成终态 |
| D3 | 流式开关 | `DeepSeekRequest.Stream = true` + `Accept: text/event-stream` header + HttpClient `ResponseHeadersRead` 模式 | DeepSeek 标准 SSE 协议 |
| D4 | 非流式降级路径 | 新增 `bool UseStreaming` 字段在 `DeepSeekOptions` (默认 `true` for v3.59.0) | 测试/老 callers 可 opt-out |
| D5 | Sister-borrow 决策 | **不沿用** aspice-toolkit `ai_extract.py` DeepSeek impl（用户指定）；peakcan-host 独立架构 | aspice-toolkit 仅 non-streaming，无 streaming code 可借鉴 |

## 架构

```text
src/PeakCan.Host.Core/
├── Analysis/
│   ├── ILlmProvider.cs                          (MODIFY +20 LoC; add AnalyzeStreamingAsync default method)
│   ├── LlmAnalysisResult.cs                     (unchanged)
│   └── LlmPartialUpdate.cs                      (NEW ~20 LoC; sealed abstract record + 3 variants)

src/PeakCan.Host.App/
├── Services/
│   └── LlmProvider/
│       ├── DeepSeekProvider.cs                  (MODIFY +60 LoC; add StreamAnalyzeAsync SSE path)
│       ├── DeepSeekRequest.cs                   (MODIFY +3 LoC; add Stream field)
│       ├── DeepSeekStreamingChunk.cs            (NEW ~30 LoC; per-chunk DTO)
│       └── DeepSeekOptions.cs                   (MODIFY +5 LoC; add UseStreaming field default true)

src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/
└── AnalysisFlow.cs                              (MODIFY +40 LoC; RunAnalysisStreamingCommand + partial text binding)

src/PeakCan.Host.App/Views/
└── TraceViewerView.AIPanel.xaml                 (MODIFY +25 LoC; partial-summary TextBlock)
```

## 数据契约

### LlmPartialUpdate (Core side, NEW)
```csharp
public abstract record LlmPartialUpdate
{
    public sealed record PartialSummary(string Delta) : LlmPartialUpdate;
    public sealed record PartialEvidenceId(string EvidenceId) : LlmPartialUpdate;
    public sealed record FinalResult(LlmAnalysisResult Result) : LlmPartialUpdate;
}
```

### ILlmProvider (additions)
```csharp
public interface ILlmProvider
{
    string DisplayName { get; }
    Task<LlmAnalysisResult> AnalyzeAsync(AnalysisSession session, CancellationToken ct);

    // v3.59.0 MINOR: streaming variant. Default impl wraps AnalyzeAsync
    // so NotImplementedLlmProvider + Azure/Ollama deferred providers
    // don't break (single-shot fallback emits 1 FinalResult).
    IAsyncEnumerable<LlmPartialUpdate> AnalyzeStreamingAsync(
        AnalysisSession session, CancellationToken ct)
        => AnalyzeStreamingFromSingleShot(this, session, ct);
}

internal static class ILlmProviderExtensions
{
    public static async IAsyncEnumerable<LlmPartialUpdate> AnalyzeStreamingFromSingleShot(
        this ILlmProvider provider,
        AnalysisSession session,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var result = await provider.AnalyzeAsync(session, ct).ConfigureAwait(false);
        yield return new LlmPartialUpdate.FinalResult(result);
    }
}
```

### DeepSeekRequest (additions)
```csharp
public sealed record DeepSeekRequest
{
    // existing fields (Model + Messages)...
    [JsonPropertyName("stream")] public bool Stream { get; init; } = false;
}
```

### DeepSeekStreamingChunk (NEW DTO)
```csharp
public sealed record DeepSeekStreamingChunk
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("choices")] public List<DeepSeekStreamingChoice>? Choices { get; init; }
}

public sealed record DeepSeekStreamingChoice
{
    [JsonPropertyName("index")] public int Index { get; init; }
    [JsonPropertyName("delta")] public DeepSeekStreamingDelta? Delta { get; init; }
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; init; }
}

public sealed record DeepSeekStreamingDelta
{
    [JsonPropertyName("role")] public string? Role { get; init; }
    [JsonPropertyName("content")] public string? Content { get; init; }
}
```

### DeepSeekOptions (additions)
```csharp
public sealed record DeepSeekOptions
{
    public string ApiBase { get; init; } = "https://api.deepseek.com";
    public string Model { get; init; } = "deepseek-chat";
    public int TimeoutSeconds { get; init; } = 30;
    // v3.59.0 MINOR: when true (default), DeepSeekProvider uses
    // SSE streaming; when false, falls back to single-shot non-streaming
    // (sister of v3.54.0 P1b behavior).
    public bool UseStreaming { get; init; } = true;
}
```

## 错误处理

- SSE stream 中断 → accumulate partial content as Summary, set Error = "Stream interrupted" (per LlmAnalysisResult envelope)
- HTTP 401/429 → return single FinalResult with error envelope (no streaming tokens emitted)
- JSON parse failure per chunk → skip that chunk, log warning, continue (resilient to single bad chunk)
- Cancellation → break stream, return FinalResult with "DeepSeek streaming cancelled"
- Timeout (HttpClient.Timeout via IHttpClientFactory + UseStreaming combined): return FinalResult with "DeepSeek streaming timed out after {seconds}s"

## 测试策略（TDD RED → GREEN → IMPROVE）

- 现有 1461 PASS / 0 FAIL / 5 SKIP (v3.58.0 baseline) 保持不变
- 新增测试:
  1. `DeepSeekProviderStreamingTests` (3 tests):
     - `StreamAnalyzeAsync_AccumulatesPartialSummary_FromChunks`
     - `StreamAnalyzeAsync_ExtractsEvidenceIds_FromIncrementalJson`
     - `StreamAnalyzeAsync_CancellationBreaksStream`
  2. `ILlmProviderExtensionsTests` (1 test):
     - `AnalyzeStreamingFromSingleShot_EmitsOneFinalResult`
  3. `LlmPartialUpdateTests` (1 test):
     - `LlmPartialUpdate_VariantDiscriminator_PatternMatchExhaustiveness`
- WPF UI 增量更新绑定测试不强求（需 STA + Application，不必要）

## 复评/不做项

- **真实 DeepSeek API CI integration test**: 不做（独立 PATCH 推迟项）
- **Azure / Ollama 流式**: 不做（独立 PATCH 推迟项）
- **Token usage 持久化**: 不做（独立 PATCH 推迟项）
- **流式重试策略**: 不做（用户手动 retry via UI）
- **UI 撤销/重做**: 不做（独立 PATCH 推迟项）
- **aspice-toolkit sister-borrow**: 用户明确选择**不沿用**（独立架构；aspice-toolkit 只有 non-streaming impl，无 streaming code 可借鉴）

## 6 NEW 1/3 lesson candidates 观察

| 候选 | 期望观察点 |
|---|---|
| `iasyncenumerable-default-interface-method-pattern-provides-single-shot-fallback` | W41 1st obs: `AnalyzeStreamingAsync` default method wraps `AnalyzeAsync` so 老 callers + Azure/Ollama deferred providers 不破坏 |
| `sse-event-stream-framing-requires-data-line-prefix-stripping` | W41 1st obs: each SSE chunk is `data: {...JSON...}\n\n` — parser must strip `data: ` prefix + `\n\n` suffix + skip `[DONE]` sentinel |
| `delta-content-accumulation-into-summary-handles-utf8-boundary-splits` | W41 1st obs: DeepSeek may split CJK char across 2 chunks (UTF-8 multi-byte sequence boundary); StringBuilder + Append avoids string concat alloc |
| `stream-cancellation-breaks-httpresponse-readasync-loop-without-throwing` | W41 1st obs: `ct.ThrowIfCancellationRequested()` inside ReadAsync loop + dispose stream gracefully |
| `partial-update-discriminated-union-via-sealed-abstract-record-is-net-7-pattern` | W41 1st obs: `LlmPartialUpdate` 的 3 变体 + C# 9 `init`-only properties 给 WPF UI 提供 type-safe partial pattern matching |
| `httpclient-accept-text-event-stream-header-required-for-sse-endpoint` | W41 1st obs: `request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"))` 是 SSE 协议关键 |

## 文件 / LoC 估算

| 文件 | LoC |
|---|---|
| `src/PeakCan.Host.Core/Analysis/LlmPartialUpdate.cs` | ~20 (NEW) |
| `src/PeakCan.Host.Core/Analysis/ILlmProvider.cs` | +20 (MODIFY) |
| `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekProvider.cs` | +60 (MODIFY) |
| `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekRequest.cs` | +3 (MODIFY) |
| `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekStreamingChunk.cs` | ~30 (NEW) |
| `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekOptions.cs` | +5 (MODIFY) |
| `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/AnalysisFlow.cs` | +40 (MODIFY) |
| `src/PeakCan.Host.App/Views/TraceViewerView.AIPanel.xaml` | +25 (MODIFY) |
| `tests/PeakCan.Host.App.Tests/Services/LlmProvider/DeepSeekProviderStreamingTests.cs` | ~80 (NEW, 3 tests) |
| `tests/PeakCan.Host.Core.Tests/Analysis/LlmPartialUpdateTests.cs` | ~15 (NEW, 1 test) |
| `tests/PeakCan.Host.Core.Tests/Analysis/ILlmProviderExtensionsTests.cs` | ~30 (NEW, 1 test) |
| **总计** | **+328 LoC net** |

## 待 SPEC 用户复核

本文为 spec 初稿。请 review 后批准，下一步进入 writing-plans 写实施计划。