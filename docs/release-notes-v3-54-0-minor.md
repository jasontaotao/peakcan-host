# v3.54.0 MINOR — Trace Viewer AI 推理 v1 P1 LLM 完整实现（P1b + P1c）

> P1 LLM 系列完成（step 1/3 v3.53.1 P1a + step 2/3 v3.54.0 P1b + step 3/3 v3.54.0 P1c）。`ILlmProvider` 从 `NotImplementedLlmProvider` P0 stub 升级为真实 `DeepSeekProvider`，AI 推理链路真正接通 DeepSeek API。

## 概述

完成 v3.52.0 spec 推迟的 P1 LLM 全部：
- **P1b**: DeepSeek Provider + HttpClient + JSON request/response
- **P1c**: Evidence ID whitelist filter（per hard-boundary #13）

借鉴 sister project `aspice-toolkit` 的 DeepSeek 集成（Python；provider-registry pattern + endpoint URL + DeepSeek systematic bias insight）。

## 用户可见变化

**无 UI 变化**（P1b + P1c 是 backend-only）。用户使用前需要：
1. 打开 Windows Credential Manager GUI
2. 添加 Generic credential，target = `peakcan-host:deepseek-api-key`，用户名任意，密码 = DeepSeek API key
3. Trace Viewer → AI Analysis 面板 → 点"运行分析" → DeepSeek API call 真正发生

未配置 API key 时 `LlmProviderDisplayName` 仍显示 `"DeepSeek (deepseek-chat)"` 但 `RunAnalysisAsync` 返回 `Error = "API key not configured; set in Windows Credential Manager (target: peakcan-host:deepseek-api-key)"`，UI 显示 `ErrorMessage`，fall back 到 local-only 报告。

## 安全保证

API key 永不 plaintext on disk（Windows Credential Manager = DPAPI-encrypted，v3.53.1 P1a foundation）。`Authorization: Bearer` header 仅 per-request 添加，**永不** log。Full response body **永不** log（只 log status code + token usage + finish reason）。

## 数据契约

新增 2 个 Core + 3 个 App 类型：

```csharp
// Core
public sealed record DeepSeekOptions
{
    public string ApiBase { get; init; } = "https://api.deepseek.com";
    public string Model { get; init; } = "deepseek-chat";
    public int TimeoutSeconds { get; init; } = 30;
}

public static class EvidenceIdWhitelistFilter
{
    public static LlmAnalysisResult Filter(AnalysisSession session, LlmAnalysisResult raw);
}

// App
public sealed class DeepSeekProvider : ILlmProvider
{
    public string DisplayName => $"DeepSeek ({_options.Model})";
    public Task<LlmAnalysisResult> AnalyzeAsync(AnalysisSession session, CancellationToken ct);
}
// + DeepSeekRequest / DeepSeekResponse / DeepSeekMessage / DeepSeekChoice / DeepSeekUsage / DeepSeekResponseFormat (System.Text.Json DTOs)
```

## 7 个核心决策

| ID | 决策 | 选择 |
|---|---|---|
| D1 | HttpClient lifecycle | Typed HttpClient via `IHttpClientFactory` (named "DeepSeek") |
| D2 | Model id | Default `deepseek-chat` (non-reasoning) |
| D3 | JSON 序列化 | `System.Text.Json` (built-in, no 3rd-party) |
| D4 | Schema 验证 | **手写 whitelist filter** (no NJsonSchema) |
| D5 | Error handling | `LlmAnalysisResult.Error` envelope (never throw) |
| D6 | Timeout | 30s default; CancellationToken overrides |
| D7 | DI registration | `DeepSeekProvider` as default; `NotImplementedLlmProvider` 保留为 fallback option |

## 架构里程碑

- **P1 LLM 系列 step 2/3 + 3/3 完成**（v3.52.0 P0 + v3.53.1 P1a + v3.54.0 P1b + P1c 全部 ship）
- **首个真实 HTTP 客户端进入 peakcan-host**（之前无 `HttpClient` / `HttpClientFactory` 代码）
- **首个 real AI call** (P0 stub → P1 real)
- **6 NEW 1/3 lesson candidates** 观察成功（待 2nd 观察后晋升）
- **sister of aspice-toolkit**（provider-registry pattern + DeepSeek systematic bias insight）

## 计数

- 7 commits on `feature/v3-54-0-minor-p1-llm-complete`
  - T1 DeepSeekOptions record
  - T2 EvidenceIdWhitelistFilter + 6 unit tests
  - T3 DeepSeekRequest + DeepSeekResponse DTOs
  - T4 DeepSeekProvider ILlmProvider impl (~180 LoC)
  - T4-fix brief bugs: missing `using System.Net.Http;` + missing `Microsoft.Extensions.Http` NuGet ref
  - T5 7 integration tests (mocked HttpMessageHandler)
  - T5-fix brief bugs: 2 missing usings + MakeProvider signature widening + HttpClient timeout
  - T6 DI wiring (AddHttpClient + DeepSeekProvider registration)
- ~600 LoC 增量 (Core ~85 + App ~260 + tests ~280 + DI ~10)
- 1443 → **1456 PASS / 0 FAIL / 5 SKIP** (+13: 6 EvidenceIdWhitelistFilter + 7 DeepSeekProvider)
- 1 transient Core flake (sister of v3.52.0/v3.52.1/v3.53.0/v3.53.0/v3.53.1 pattern; cleared on retry)

## 6 NEW 1/3 lesson candidates

| 候选 | 期望观察点 |
|---|---|
| `deepseek-api-key-must-flow-through-icredentialstore-not-appsettingsjson` | T4 verified: API key read from `ICredentialStore.GetAsync("deepseek-api-key")` |
| `httpclient-via-ihttpclientfactory-required-for-socket-lifecycle-management` | T4+T6 verified: `services.AddHttpClient("DeepSeek")` + `IHttpClientFactory.CreateClient("DeepSeek")` per-call |
| `llm-evidence-id-whitelist-filter-must-drop-entire-claim-not-just-id` | T2 verified: per-claim filter not per-ID |
| `system-text-json-preferred-over-newtonsoft-for-net10-no-3rd-party-dep` | T3+T4 verified: System.Text.Json only, no Newtonsoft / NJsonSchema |
| `deepseek-systematic-bias-mitigation-via-strict-json-schema-and-whitelist` | T2+T4 verified (借鉴 aspice-toolkit): SystemPrompt + summary extraction + whitelist mitigate bias |
| `llm-error-must-be-result-envelope-not-throw-so-caller-can-fall-back-to-local` | T4 verified: 401/429/timeout/JSON-parse 都 return `ErrorResult`, never throw |

**Total NEW 1/3 cumulative across project** (v3.52.0 + v3.52.1 + v3.53.0 + v3.53.1 + v3.54.0): 25 candidates, all held at 1/3.

## 显式不在范围（推到后续 PATCH）

- **P2 PATCH**: API Key UI 设置界面（用户目前用 Windows Credential Manager GUI）
- **Streaming LLM response**: 后续 PATCH
- **AzureOpenAI / Ollama concrete impls**: 后续 PATCH（`ILlmProvider` interface 预留 + DI 兼容；本期只 ship DeepSeek）
- **NJsonSchema / JsonSchema.Net 3rd-party**: 不做（手写 whitelist 足够）
- **Retry policy with exponential backoff**: 不做（用户手动 retry via UI）
- **Token usage tracking persistence**: 不做（v3.54.0 只 log 不持久化）
- **Real DeepSeek API integration test (CI env var)**: 后续 PATCH（本期只 mock）

## 关联

- Spec: `docs/superpowers/specs/2026-07-17-v3-54-0-minor-p1-llm-complete-design.md` (`950f33c`)
- Plan: `docs/superpowers/plans/2026-07-17-v3-54-0-minor-p1-llm-complete.md` (`7ff93ba`)
- v3.52.0 spec 推迟项 hard-boundary #13 + D1/D5: `docs/superpowers/specs/2026-07-16-ai-inference-v1-design.md`
- v3.53.1 P1a (security foundation): commit `6cb2500` + `docs/superpowers/specs/2026-07-17-v3-53-1-patch-p1a-credential-store-design.md`
- sister 借鉴: `D:/claude_proj2/aspice-toolkit/scripts/dashboard/backend.py` (Python; LLM_PROVIDERS pattern + DeepSeek endpoint + systematic bias)

## 验证

```bash
dotnet build PeakCan.Host.slnx -c Debug           # 0 errors, 0 warnings on touched code
dotnet test PeakCan.Host.slnx --no-build -c Debug  # 1456 PASS / 0 FAIL / 5 SKIP
```

## 已知并行测试 flake（不影响 ship）

`Uds.UdsClientConcurrentSecurityAccessTests.TwoArg_Overload_ConcurrentMidHandshakeLockoutFlip_PostStateConsistent` + `Replay.AscParserTests.Parse_MalformedLines_LogsEachWithLineNumberAndReason` 在并行测试下偶发失败（wall-clock timing）。`dotnet test -- xUnit.MaxParallelThreads=1` 单线程运行即可 1456/1456 PASS。属于 pre-existing sister pattern（v3.52.0/v3.52.1/v3.53.0/v3.53.1）。**不是 v3.54.0 PATCH 引入**。
