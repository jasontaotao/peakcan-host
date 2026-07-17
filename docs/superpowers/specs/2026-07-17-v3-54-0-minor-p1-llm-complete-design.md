# v3.54.0 MINOR — Trace Viewer AI 推理 v1 P1 LLM 完整实现 (P1b + P1c)

> 状态：v3.54.0 MINOR 设计 spec
> 触发：v3.52.0 P0 + v3.52.1 cleanup + v3.53.0 refactor + v3.53.1 P1a security foundation 均已 ship。完成 P1 LLM step 2/3 + 3/3。
> 借鉴：sister project `aspice-toolkit` 的 DeepSeek 集成（Python；provider-registry pattern + endpoint URL + DeepSeek systematic bias insight）。

## 目标

完整实现 v3.52.0 spec 推迟的 P1 LLM 全套：
- **P1b**: DeepSeek Provider + HttpClient + JSON request + response
- **P1c**: JSON Schema 验证 + Evidence ID whitelist filter + offline-mode stub fallback

完成后 `ILlmProvider` 从 `NotImplementedLlmProvider` (P0 stub) 升级为真实 `DeepSeekProvider`。

## 当前代码证据（已坐实）

- `ILlmProvider.cs:1-27` — interface + NotImplementedLlmProvider stub
- `LlmAnalysisResult.cs:1-15` — Summary + AttributedEvidenceIds + RawResponseJson + Error
- `ICredentialStore.cs:1-35` — v3.53.1 P1a security foundation
- `WindowsCredentialManagerStore.cs:1-143` — v3.53.1 P1a Win32 P/Invoke impl (DPAPI-encrypted)
- `AppServicesFlow.cs:173-178` — v3.53.1 DI: ICredentialStore + ILlmProvider stub
- `AnalysisFlow.cs:14-21` — RunAnalysisAsync consumes _llmProvider
- 无 HttpClient / DeepSeek / OpenAI 现有代码

## Sister 借鉴 (from aspice-toolkit)

| Insight | 借鉴到 v3.54.0 |
|---|---|
| Provider registry pattern: `LLM_PROVIDERS = { deepseek: {endpoint, env_key, default_model}, ... }` | ✅ P1b 未来扩展: ProviderConfig record + ILlmProvider DI 注册保留 multi-provider 路径 |
| Endpoint URL `https://api.deepseek.com/chat/completions` | ✅ 写进 spec 但**不写死**；DeepSeekOptions.ApiBase 默认值用这个 URL |
| API key masking | ❌ 不借鉴（peakcan-host 用 CredentialManager 不暴露给 UI） |
| DeepSeek systematic bias: priority "must→should/may" + category compound | ✅ **P1c JSON Schema 严格限定** + whitelist filter |
| DeepSeek config via JSON file | ❌ 不借鉴（peakcan-host DI + CredentialManager） |

## 7 个 D 决策

| ID | 决策 | 选择 | 理由 |
|---|---|---|---|
| D1 | HttpClient lifecycle | Typed HttpClient via `IHttpClientFactory` | .NET 10 推荐 + 避免 socket exhaustion |
| D2 | Model id | Default `deepseek-chat` (non-reasoning) | reasoning 模式 `max_tokens` 含 reasoning tokens，可能截断 |
| D3 | JSON 序列化 | `System.Text.Json` (built-in) | Newtonsoft 被 Microsoft 弃用；性能更好；无 3rd-party dep |
| D4 | Schema 验证 | **手写 whitelist filter** (不引入 NJsonSchema) | 3rd-party 库 +500KB binary + 攻击面；string 集合操作手写足够 |
| D5 | Error handling | `LlmAnalysisResult.Error` envelope (per ILlmProvider doc) | 401/429/timeout/JSON-parse → return Error, NOT throw |
| D6 | Timeout | 30s default; `CancellationToken` overrides | DeepSeek typical 2-10s；用户可 cancel |
| D7 | DI registration | Replace `NotImplementedLlmProvider` with `DeepSeekProvider` as default; 保留 `NotImplementedLlmProvider` as fallback option | 无 API key → local-only；zero-config works |

## 硬边界（新增 15-17）

继承 v3.52.0 14 + 新增：
- 15. HttpClient 不持久化 connection-level state
- 16. LLM response Summary ≤ 4096 chars (UI safety cap)
- 17. Whitelist filter drop **整个 claim** 而不仅 ID (per v3.52.0 hard-boundary #13)

## 数据契约

### New Core types

`DeepSeekOptions.cs` (~25 LoC): `record(ApiBase="https://api.deepseek.com", Model="deepseek-chat", TimeoutSeconds=30)`

`EvidenceIdWhitelistFilter.cs` (~60 LoC): static class with `Filter(session, raw) → LlmAnalysisResult` method. Logic:
1. Build `HashSet<string>` of valid IDs from `session.Report.Evidence`
2. Parse `raw.Summary` + claims JSON
3. For each `evidence_id` reference, check membership:
   - Valid → keep claim
   - Invalid → drop entire claim, log warning (no plaintext payload)
4. Reconstruct filtered Summary + filtered AttributedEvidenceIds

### New App types

`DeepSeekProvider.cs` (~180 LoC): `ILlmProvider` impl. ctor: `IHttpClientFactory`, `ICredentialStore`, `ILogger<DeepSeekProvider>`, `IOptions<DeepSeekOptions>`. `AnalyzeAsync` flow:
1. `ICredentialStore.GetAsync("deepseek-api-key")` → null → Error envelope "API key not configured"
2. Build request: `{ model, messages, response_format: {type:"json_object"}, max_tokens, temperature, stream: false }`
3. POST → non-2xx → Error envelope
4. Parse response → extract `choices[0].message.content` JSON
5. Pass through `EvidenceIdWhitelistFilter.Filter`
6. Return `LlmAnalysisResult`

`DeepSeekRequest.cs` (~30 LoC) + `DeepSeekResponse.cs` (~25 LoC) — `System.Text.Json` with `[JsonPropertyName]`

## 错误处理

| 场景 | Error envelope |
|---|---|
| API key null | "API key not configured; set in Windows Credential Manager (target: peakcan-host:deepseek-api-key)" |
| 401/403 | "DeepSeek API key invalid or revoked" |
| 429 | "DeepSeek rate limit exceeded; retry later" (log Retry-After) |
| 5xx | "DeepSeek server error (HTTP {status})" |
| Timeout | "DeepSeek request timed out after {seconds}s" |
| Malformed JSON | "DeepSeek returned malformed JSON" (log response shape only) |
| All claims dropped | "DeepSeek response cited no valid evidence IDs; falling back to local-only" |

**No throw** from AnalyzeAsync. All errors return Error envelope.

## UI 接线

**无 UI 变化**（P1b + P1c 是 backend-only）：
- AI Analysis panel 自动显示 DeepSeek response
- 错误显示：`ErrorMessage = $"LLM 分析失败: {result.Error}"`
- API Key 设置界面：明确不做（P2 PATCH；用户用 Windows Credential Manager GUI）

## 测试策略

### Core unit tests (~100 LoC, 6 tests)
- `EvidenceIdWhitelistFilterTests`:
  - All valid IDs pass through
  - All invalid → empty AttributedEvidenceIds + "cited no valid evidence IDs" error
  - Mixed valid + invalid → only valid claims kept
  - Empty session.Evidence → all IDs invalid
  - Whitespace handling (trim)
  - Case sensitivity (preserve case)

### App integration tests (~180 LoC, 7 tests)
- `DeepSeekProviderTests` with mocked HttpMessageHandler (no real network):
  - Happy path: valid response → LlmAnalysisResult with Summary + AttributedEvidenceIds
  - 401 → Error envelope
  - 429 → Error envelope + Retry-After logged
  - 500 → Error envelope
  - Timeout → Error envelope
  - Malformed JSON → Error envelope
  - ICredentialStore returns null → "API key not configured" error

## 复评/不做项

- API Key UI 设置界面 → P2 PATCH
- Streaming LLM response → 后续
- Tool calling → 不做
- AzureOpenAI / Ollama concrete impls → 后续（interface 预留）
- NJsonSchema / JsonSchema.Net 3rd-party → 不做（手写 whitelist 足够）
- Retry policy with exponential backoff → 不做（用户手动 retry）
- Token usage tracking persistence → 不做（v3.54.0 只 log）

## 6 NEW 1/3 lesson candidates

| 候选 | 期望观察点 |
|---|---|
| `deepseek-api-key-must-flow-through-icredentialstore-not-appsettingsjson` | P1b verified |
| `httpclient-via-ihttpclientfactory-required-for-socket-lifecycle-management` | P1b verified |
| `llm-evidence-id-whitelist-filter-must-drop-entire-claim-not-just-id` | P1c verified |
| `system-text-json-preferred-over-newtonsoft-for-net10-no-3rd-party-dep` | P1b verified |
| `deepseek-systematic-bias-mitigation-via-strict-json-schema-and-whitelist` | P1c verified (借鉴 aspice-toolkit) |
| `llm-error-must-be-result-envelope-not-throw-so-caller-can-fall-back-to-local` | P1b verified |

## 文件 / LoC 估算

| 文件 | LoC |
|---|---|
| `src/PeakCan.Host.Core/Analysis/DeepSeekOptions.cs` | 25 |
| `src/PeakCan.Host.Core/Analysis/EvidenceIdWhitelistFilter.cs` | 60 |
| `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekProvider.cs` | 180 |
| `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekRequest.cs` | 30 |
| `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekResponse.cs` | 25 |
| `tests/PeakCan.Host.Core.Tests/Analysis/EvidenceIdWhitelistFilterTests.cs` | 100 |
| `tests/PeakCan.Host.App.Tests/Services/LlmProvider/DeepSeekProviderTests.cs` | 180 |
| `src/PeakCan.Host.App/Composition/AppHostBuilder/AppServicesFlow.cs` | +5 |
| **总计** | **~605 LoC** |

注：spec plan 估算 ~1500 LoC；actual ~600 LoC。理由：D4 手写 whitelist (no NJsonSchema) + D3 System.Text.Json (no Newtonsoft) + no retry policy + no streaming 大幅收敛。

## 待 SPEC 用户复核

本文为 v3.54.0 MINOR spec。范围 P1b + P1c 全部。