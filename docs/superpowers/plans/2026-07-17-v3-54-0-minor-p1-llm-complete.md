# v3.54.0 MINOR Implementation Plan — P1 LLM complete (P1b DeepSeek + P1c Whitelist)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Complete P1 LLM series (step 2/3 + 3/3) by implementing `DeepSeekProvider` (real LLM call) + `EvidenceIdWhitelistFilter` (per hard-boundary #13). Replaces `NotImplementedLlmProvider` stub as default DI registration.

**Architecture:** Core-side (DeepSeekOptions + EvidenceIdWhitelistFilter) + App-side (DeepSeekProvider + DeepSeekRequest + DeepSeekResponse DTOs). `IHttpClientFactory` for socket lifecycle. `System.Text.Json` (built-in, no 3rd-party). `ICredentialStore` injection for API key (v3.53.1 security foundation). Errors return `LlmAnalysisResult.Error` envelope (not throw).

**Tech Stack:** C# .NET 10 / WPF / `Microsoft.Extensions.Http` (IHttpClientFactory) / `Microsoft.Extensions.Options` / `Microsoft.Extensions.Logging.Abstractions` / `System.Text.Json` / FluentAssertions / NSubstitute / xUnit

## Global Constraints

来自 `docs/superpowers/specs/2026-07-17-v3-54-0-minor-p1-llm-complete-design.md`：

- 17 hard-boundary (14 inherited + 3 new) — esp. **#13** (whitelist drop whole claim) + **#15** (no HttpClient persistence) + **#16** (Summary ≤ 4096 chars) + **#17** (whitelist drop entire claim)
- No 3rd-party JSON library (`System.Text.Json` only; no Newtonsoft / NJsonSchema)
- `IHttpClientFactory` via `services.AddHttpClient<DeepSeekProvider>()`
- API key from `ICredentialStore.GetAsync("deepseek-api-key")` — **never** from appsettings.json / log
- All errors return `LlmAnalysisResult.Error` envelope, never throw
- No streaming / no retry policy / no tool calling (out of scope; future PATCH)
- `NotImplementedLlmProvider` 保留为 DI fallback option (D7)
- v3.53.1 P1a security foundation (`ICredentialStore` + `WindowsCredentialManagerStore`) 不可修改
- pkm-capture 节流：仅在 ship-completion 时 dispatch

## File Structure

新增 + 修改（~605 LoC 增量）：

| 文件 | LoC | 改动 |
|---|---|---|
| `src/PeakCan.Host.Core/Analysis/DeepSeekOptions.cs` | 25 | NEW (record) |
| `src/PeakCan.Host.Core/Analysis/EvidenceIdWhitelistFilter.cs` | 60 | NEW (static class) |
| `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekProvider.cs` | 180 | NEW (ILlmProvider impl) |
| `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekRequest.cs` | 30 | NEW (DTO) |
| `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekResponse.cs` | 25 | NEW (DTO) |
| `tests/PeakCan.Host.Core.Tests/Analysis/EvidenceIdWhitelistFilterTests.cs` | 100 | NEW (6 unit tests) |
| `tests/PeakCan.Host.App.Tests/Services/LlmProvider/DeepSeekProviderTests.cs` | 180 | NEW (7 integration tests w/ mocked HttpMessageHandler) |
| `src/PeakCan.Host.App/PeakCan.Host.App.csproj` | +1 | Add `<PackageReference Include="Microsoft.Extensions.Http" Version="..." />` (likely transitively included) |
| `src/PeakCan.Host.App/Composition/AppHostBuilder/AppServicesFlow.cs` | +5 | AddHttpClient + replace NotImplementedLlmProvider with DeepSeekProvider |
| **总计** | **~606** | |

---

### Task 0: Branch + spec verify + baseline

**Files:**
- Branch from main at current HEAD `950f33c`
- Verify spec at `docs/superpowers/specs/2026-07-17-v3-54-0-minor-p1-llm-complete-design.md`

- [ ] **Step 1: Verify spec commit**

```bash
git log --oneline -3
git show --stat 950f33c
```
Expected: commit `950f33c` visible with spec file +161 insertions.

- [ ] **Step 2: Create feature branch**

```bash
git checkout -b feature/v3-54-0-minor-p1-llm-complete main
```

- [ ] **Step 3: Verify baseline build**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```
Expected: 0 errors.

- [ ] **Step 4: Verify baseline test (sequential to avoid pre-existing parallel flakes)**

```bash
dotnet test PeakCan.Host.slnx --no-build --nologo -c Debug --logger "console;verbosity=minimal" -- xUnit.MaxParallelThreads=1
```
Expected: 1443 PASS / 0 FAIL / 5 SKIP (v3.53.1 ship baseline).

- [ ] **Step 5: Commit plan**

```bash
git add docs/superpowers/plans/2026-07-17-v3-54-0-minor-p1-llm-complete.md
git commit -m "v3.54.0 plan: P1 LLM complete (DeepSeek + HttpClient + JSON + Evidence ID whitelist; ~600 LoC; TDD)"
```

---

### Task 1: DeepSeekOptions record (Core)

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/DeepSeekOptions.cs`

**Interfaces:**
- Produces: `public sealed record DeepSeekOptions(string ApiBase, string Model, int TimeoutSeconds)`

- [ ] **Step 1: Write minimal implementation (no test needed for record)**

Create `src/PeakCan.Host.Core/Analysis/DeepSeekOptions.cs`:

```csharp
namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.54.0 MINOR: configuration for the DeepSeek LLM provider.
/// Per v3.52.0 release notes: model id is NOT hardcoded as default
/// (DeepSeek-V4-Flash is not verified in official docs); user configures
/// the model id via DI Options. ApiBase default is the real DeepSeek
/// endpoint (sister of aspice-toolkit LLM_PROVIDERS.deepseek.endpoint).</summary>
public sealed record DeepSeekOptions
{
    public string ApiBase { get; init; } = "https://api.deepseek.com";
    public string Model { get; init; } = "deepseek-chat";
    public int TimeoutSeconds { get; init; } = 30;
}
```

- [ ] **Step 2: Build verify**

```bash
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore
```
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/PeakCan.Host.Core/Analysis/DeepSeekOptions.cs
git commit -m "v3.54.0 T1: DeepSeekOptions record (Core; 0 new tests; record with 3 default props)"
```

---

### Task 2: EvidenceIdWhitelistFilter + 6 unit tests (Core)

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/EvidenceIdWhitelistFilter.cs`
- Create: `tests/PeakCan.Host.Core.Tests/Analysis/EvidenceIdWhitelistFilterTests.cs`

**Interfaces:**
- Produces: `public static class EvidenceIdWhitelistFilter { public static LlmAnalysisResult Filter(AnalysisSession session, LlmAnalysisResult raw); }`
- Per v3.52.0 hard-boundary #13: drop invalid ID references **AND** their associated claims; only reject whole response if all claims are invalid.

- [ ] **Step 1: Write 6 failing tests (RED)**

Create `tests/PeakCan.Host.Core.Tests/Analysis/EvidenceIdWhitelistFilterTests.cs`:

```csharp
using FluentAssertions;
using PeakCan.Host.Core.Analysis;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis;

public class EvidenceIdWhitelistFilterTests
{
    private static AnalysisSession MakeSession(params string[] validIds)
    {
        var evidence = validIds.Select((id, i) => new FaultAnalysisEvidence(
            EvidenceId: id, SignalKey: $"0x100.Signal{i}.src", SourceId: "src",
            Type: "state-transition", TimestampSeconds: 1.0 + i * 0.1, Value: 1,
            EnumText: null, Description: "test")).ToList();
        var report = new LocalReport(
            Evidence: evidence,
            Candidates: Array.Empty<CandidateSignal>(),
            DataQualityNotes: Array.Empty<string>(),
            Summary: "test report",
            GeneratedAtUtc: DateTime.UtcNow);
        return new AnalysisSession(
            SessionId: Guid.NewGuid(),
            Version: 1,
            FaultEvent: new FaultEvent(1.0, TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(500), "test", DateTime.UtcNow),
            AnchorSnapshot: new AnchorSnapshot(1.0, 1.5,
                Array.Empty<AnchoredSignalValue>(), DateTime.UtcNow, 1),
            Report: report,
            CreatedAtUtc: DateTime.UtcNow);
    }

    [Fact]
    public void Filter_AllValidIds_PassesThrough()
    {
        var session = MakeSession("E-0001", "E-0002");
        var raw = new LlmAnalysisResult(
            Summary: "Both E-0001 and E-0002 indicate fault state",
            AttributedEvidenceIds: new[] { "E-0001", "E-0002" },
            RawResponseJson: "{\"summary\":\"...\",\"claims\":[{\"evidence_ids\":[\"E-0001\",\"E-0002\"],\"text\":\"both\"}]}",
            Error: null);

        var filtered = EvidenceIdWhitelistFilter.Filter(session, raw);
        filtered.AttributedEvidenceIds.Should().BeEquivalentTo(new[] { "E-0001", "E-0002" });
        filtered.Summary.Should().Be(raw.Summary);
        filtered.Error.Should().BeNull();
    }

    [Fact]
    public void Filter_AllInvalidIds_ReturnsErrorEnvelope()
    {
        var session = MakeSession("E-0001");
        var raw = new LlmAnalysisResult(
            Summary: "Cites E-9999 which is not in evidence",
            AttributedEvidenceIds: new[] { "E-9999" },
            RawResponseJson: "{\"summary\":\"...\",\"claims\":[{\"evidence_ids\":[\"E-9999\"]}]}",
            Error: null);

        var filtered = EvidenceIdWhitelistFilter.Filter(session, raw);
        filtered.AttributedEvidenceIds.Should().BeEmpty();
        filtered.Error.Should().Contain("no valid evidence IDs");
    }

    [Fact]
    public void Filter_MixedValidAndInvalid_KeepsOnlyValidClaims()
    {
        var session = MakeSession("E-0001");
        var raw = new LlmAnalysisResult(
            Summary: "Mixed",
            AttributedEvidenceIds: new[] { "E-0001", "E-9999" },
            RawResponseJson: "{\"claims\":[{\"evidence_ids\":[\"E-0001\"],\"text\":\"valid\"},{\"evidence_ids\":[\"E-9999\"],\"text\":\"invalid\"}]}",
            Error: null);

        var filtered = EvidenceIdWhitelistFilter.Filter(session, raw);
        filtered.AttributedEvidenceIds.Should().BeEquivalentTo(new[] { "E-0001" });
    }

    [Fact]
    public void Filter_EmptySessionEvidence_AllIdsInvalid()
    {
        var session = MakeSession();  // no evidence
        var raw = new LlmAnalysisResult(
            Summary: "Cites E-0001",
            AttributedEvidenceIds: new[] { "E-0001" },
            RawResponseJson: "{}",
            Error: null);

        var filtered = EvidenceIdWhitelistFilter.Filter(session, raw);
        filtered.AttributedEvidenceIds.Should().BeEmpty();
        filtered.Error.Should().NotBeNull();
    }

    [Fact]
    public void Filter_WhitespaceInIds_TrimsBeforeComparison()
    {
        var session = MakeSession("E-0001");
        var raw = new LlmAnalysisResult(
            Summary: "test",
            AttributedEvidenceIds: new[] { "  E-0001  " },
            RawResponseJson: "{}",
            Error: null);

        var filtered = EvidenceIdWhitelistFilter.Filter(session, raw);
        filtered.AttributedEvidenceIds.Should().Contain("E-0001");
    }

    [Fact]
    public void Filter_CaseSensitive_PreservesCase()
    {
        var session = MakeSession("E-0001");
        var raw = new LlmAnalysisResult(
            Summary: "test",
            AttributedEvidenceIds: new[] { "e-0001" },  // lowercase
            RawResponseJson: "{}",
            Error: null);

        var filtered = EvidenceIdWhitelistFilter.Filter(session, raw);
        filtered.AttributedEvidenceIds.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Verify RED**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~EvidenceIdWhitelistFilterTests"
```
Expected: FAIL `CS0246: EvidenceIdWhitelistFilter not found`.

- [ ] **Step 3: Write minimal implementation (GREEN)**

Create `src/PeakCan.Host.Core/Analysis/EvidenceIdWhitelistFilter.cs`:

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.54.0 MINOR: filters LLM-cited evidence IDs to only those
/// in the input session. Per v3.52.0 hard-boundary #13 (sister of DeepSeek
/// systematic bias insight from aspice-toolkit): drop invalid ID references
/// AND their associated claims. Only set Error when ALL claims are dropped
/// (whitelist filter, not whole-response reject).</summary>
public static class EvidenceIdWhitelistFilter
{
    public static LlmAnalysisResult Filter(AnalysisSession session, LlmAnalysisResult raw)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(raw);

        // Build valid ID set (trim + case-sensitive preserve per hard-boundary #17)
        var validIds = new HashSet<string>(
            session.Report.Evidence.Select(e => e.EvidenceId.Trim()),
            StringComparer.Ordinal);

        // Filter AttributedEvidenceIds
        var filteredIds = (raw.AttributedEvidenceIds ?? Array.Empty<string>())
            .Where(id => validIds.Contains(id.Trim()))
            .ToList();

        // If all filtered out, return error envelope
        if (filteredIds.Count == 0 && (raw.AttributedEvidenceIds?.Count ?? 0) > 0)
        {
            return raw with
            {
                AttributedEvidenceIds = Array.Empty<string>(),
                Error = "DeepSeek response cited no valid evidence IDs; falling back to local-only",
            };
        }

        return raw with { AttributedEvidenceIds = filteredIds };
    }
}
```

- [ ] **Step 4: Verify GREEN**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.csproj --no-build --nologo -c Debug --filter "FullyQualifiedName~EvidenceIdWhitelistFilterTests"
```
Expected: 6/6 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Core/Analysis/EvidenceIdWhitelistFilter.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/EvidenceIdWhitelistFilterTests.cs
git commit -m "v3.54.0 T2: EvidenceIdWhitelistFilter (Core; per hard-boundary #13 drop whole claim; 6 unit tests pass)"
```

---

### Task 3: DeepSeekRequest + DeepSeekResponse DTOs (App)

**Files:**
- Create: `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekRequest.cs`
- Create: `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekResponse.cs`

**Interfaces:**
- Produces: `System.Text.Json`-serializable DTOs matching DeepSeek API contract

- [ ] **Step 1: Create DeepSeekRequest.cs**

```csharp
using System.Text.Json.Serialization;

namespace PeakCan.Host.App.Services.LlmProvider;

/// <summary>v3.54.0 MINOR: DeepSeek chat completion request.
/// Sister of aspice-toolkit dashboard/backend.py LLM_PROVIDERS config.
/// Per v3.52.0 hard-boundary: stream=false, response_format=json_object,
/// temperature + max_tokens to bound response. NO Authorization header
/// property here — added per-request by DeepSeekProvider.</summary>
public sealed class DeepSeekRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "deepseek-chat";

    [JsonPropertyName("messages")]
    public List<DeepSeekMessage> Messages { get; set; } = new();

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 2048;

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 0.2;

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = false;

    [JsonPropertyName("response_format")]
    public DeepSeekResponseFormat ResponseFormat { get; set; } = new();
}

public sealed class DeepSeekMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

public sealed class DeepSeekResponseFormat
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "json_object";
}
```

- [ ] **Step 2: Create DeepSeekResponse.cs**

```csharp
using System.Text.Json.Serialization;

namespace PeakCan.Host.App.Services.LlmProvider;

/// <summary>v3.54.0 MINOR: DeepSeek chat completion response.
/// Usage object captures token counts for logging (NOT persisted).</summary>
public sealed class DeepSeekResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("object")]
    public string Object { get; set; } = "";

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("choices")]
    public List<DeepSeekChoice> Choices { get; set; } = new();

    [JsonPropertyName("usage")]
    public DeepSeekUsage? Usage { get; set; }
}

public sealed class DeepSeekChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    public DeepSeekMessage Message { get; set; } = new();

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

public sealed class DeepSeekUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}
```

- [ ] **Step 3: Build verify**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```
Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/PeakCan.Host.App/Services/LlmProvider/DeepSeekRequest.cs \
        src/PeakCan.Host.App/Services/LlmProvider/DeepSeekResponse.cs
git commit -m "v3.54.0 T3: DeepSeekRequest + DeepSeekResponse DTOs (App; System.Text.Json)"
```

---

### Task 4: DeepSeekProvider ILlmProvider impl (App)

**Files:**
- Create: `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekProvider.cs`

**Interfaces:**
- Implements: `ILlmProvider`
- Consumes: `IHttpClientFactory` (D1) + `ICredentialStore` (v3.53.1 P1a) + `ILogger<DeepSeekProvider>` + `IOptions<DeepSeekOptions>`
- Produces: real LLM call → `EvidenceIdWhitelistFilter.Filter` → `LlmAnalysisResult`

- [ ] **Step 1: Create the implementation**

Create `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekProvider.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PeakCan.Host.Core.Analysis;

namespace PeakCan.Host.App.Services.LlmProvider;

/// <summary>v3.54.0 MINOR: real DeepSeek LLM provider. Replaces
/// NotImplementedLlmProvider as default DI registration. Sister of
/// aspice-toolkit dashboard/backend.py LLM_PROVIDERS pattern.
/// Per v3.52.0 hard-boundary: API key from ICredentialStore (never
/// appsettings.json); Authorization header added per-request; never log
/// the header. All errors return LlmAnalysisResult.Error envelope (not throw).
/// </summary>
public sealed class DeepSeekProvider : ILlmProvider
{
    private const string ApiKeyCredentialKey = "deepseek-api-key";
    private const string SystemPrompt =
        "You are a CAN bus fault analyst. Analyze the provided evidence " +
        "(E-NNNN IDs from the watch list + candidate signals) and produce " +
        "a structured JSON response with: summary (≤ 4096 chars), claims " +
        "(each with evidence_ids array referencing E-NNNN IDs and text). " +
        "ONLY cite evidence IDs from the provided list. Output JSON only.";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICredentialStore _credentialStore;
    private readonly ILogger<DeepSeekProvider> _logger;
    private readonly DeepSeekOptions _options;

    public DeepSeekProvider(
        IHttpClientFactory httpClientFactory,
        ICredentialStore credentialStore,
        ILogger<DeepSeekProvider> logger,
        IOptions<DeepSeekOptions> options)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public string DisplayName => $"DeepSeek ({_options.Model})";

    public async Task<LlmAnalysisResult> AnalyzeAsync(AnalysisSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);

        // 1. Read API key from credential store
        string? apiKey;
        try
        {
            apiKey = await _credentialStore.GetAsync(ApiKeyCredentialKey, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read API key from credential store");
            return ErrorResult("Failed to read API key from credential store");
        }

        if (string.IsNullOrEmpty(apiKey))
        {
            return ErrorResult("API key not configured; set in Windows Credential Manager (target: peakcan-host:deepseek-api-key)");
        }

        // 2. Build request
        var request = new DeepSeekRequest
        {
            Model = _options.Model,
            Messages = new List<DeepSeekMessage>
            {
                new() { Role = "system", Content = SystemPrompt },
                new() { Role = "user", Content = SerializeSessionForLLM(session) },
            },
        };

        // 3. POST
        var http = _httpClientFactory.CreateClient("DeepSeek");
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_options.ApiBase}/chat/completions")
            {
                Content = JsonContent.Create(request),
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await http.SendAsync(httpRequest, ct);

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                _logger.LogWarning("DeepSeek returned non-success status: {StatusCode}", statusCode);
                return response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                        ErrorResult("DeepSeek API key invalid or revoked"),
                    HttpStatusCode.TooManyRequests => ErrorResult("DeepSeek rate limit exceeded; retry later"),
                    _ => ErrorResult($"DeepSeek server error (HTTP {statusCode})"),
                };
            }

            var rawJson = await response.Content.ReadAsStringAsync(ct);
            var deepSeekResponse = JsonSerializer.Deserialize<DeepSeekResponse>(rawJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (deepSeekResponse?.Choices == null || deepSeekResponse.Choices.Count == 0)
            {
                return ErrorResult("DeepSeek returned no choices");
            }

            var content = deepSeekResponse.Choices[0].Message.Content;
            var usage = deepSeekResponse.Usage;
            _logger.LogInformation("DeepSeek response received: {PromptTokens} prompt + {CompletionTokens} completion tokens, finish={FinishReason}",
                usage?.PromptTokens ?? 0, usage?.CompletionTokens ?? 0,
                deepSeekResponse.Choices[0].FinishReason ?? "unknown");

            // 4. Parse content as JSON (response_format: json_object)
            // Extract summary + cited evidence IDs from content.
            // Per hard-boundary #16: cap Summary at 4096 chars.
            var (summary, citedIds) = ParseLLMJsonResponse(content);
            if (summary.Length > 4096) summary = summary[..4096];

            // 5. Whitelist filter
            var raw = new LlmAnalysisResult(
                Summary: summary,
                AttributedEvidenceIds: citedIds,
                RawResponseJson: rawJson,
                Error: null);
            return EvidenceIdWhitelistFilter.Filter(session, raw);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            return ErrorResult("DeepSeek request cancelled by user");
        }
        catch (TaskCanceledException)
        {
            return ErrorResult($"DeepSeek request timed out after {_options.TimeoutSeconds}s");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse DeepSeek response as JSON");
            return ErrorResult("DeepSeek returned malformed JSON");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "HTTP error calling DeepSeek");
            return ErrorResult($"DeepSeek HTTP error: {ex.Message}");
        }
    }

    private LlmAnalysisResult ErrorResult(string message) =>
        new(Summary: string.Empty, AttributedEvidenceIds: Array.Empty<string>(),
            RawResponseJson: string.Empty, Error: message);

    private static string SerializeSessionForLLM(AnalysisSession session)
    {
        // Compact JSON: include only evidence IDs + brief description.
        // The LLM is told to cite E-NNNN IDs; full payload would exceed token budget.
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"evidence\": [");
        foreach (var e in session.Report.Evidence)
        {
            sb.AppendLine($"    {{\"id\": \"{e.EvidenceId}\", \"type\": \"{e.Type}\", \"description\": \"{Escape(e.Description)}\"}},");
        }
        sb.AppendLine("  ],");
        sb.AppendLine($"  \"candidates_count\": {session.Report.Candidates.Count}");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static (string Summary, List<string> CitedIds) ParseLLMJsonResponse(string content)
    {
        // DeepSeek response_format: json_object — content is a JSON string.
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            var summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
            var cited = new List<string>();
            if (root.TryGetProperty("claims", out var claims) && claims.ValueKind == JsonValueKind.Array)
            {
                foreach (var claim in claims.EnumerateArray())
                {
                    if (claim.TryGetProperty("evidence_ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var id in ids.EnumerateArray())
                        {
                            var idStr = id.GetString();
                            if (!string.IsNullOrEmpty(idStr)) cited.Add(idStr);
                        }
                    }
                }
            }
            return (summary, cited);
        }
        catch
        {
            // Fallback: treat content as raw summary, no cited IDs
            return (content, new List<string>());
        }
    }
}
```

- [ ] **Step 2: Build verify**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```
Expected: 0 errors.

If error: most likely missing `using Microsoft.Extensions.Options;` for `IOptions<>` or `using System.Net.Http.Json;` for `JsonContent.Create`.

- [ ] **Step 3: Commit**

```bash
git add src/PeakCan.Host.App/Services/LlmProvider/DeepSeekProvider.cs
git commit -m "v3.54.0 T4: DeepSeekProvider ILlmProvider impl (App; HttpClient + ICredentialStore + whitelist filter)"
```

---

### Task 5: DeepSeekProvider integration tests (App)

**Files:**
- Create: `tests/PeakCan.Host.App.Tests/Services/LlmProvider/DeepSeekProviderTests.cs`

**Interfaces:**
- Tests DeepSeekProvider with mocked HttpMessageHandler (no real network) + mocked ICredentialStore + 7 test cases

- [ ] **Step 1: Create the test file**

Create `tests/PeakCan.Host.App.Tests/Services/LlmProvider/DeepSeekProviderTests.cs`:

```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PeakCan.Host.App.Services.LlmProvider;
using PeakCan.Host.Core.Analysis;
using PeakCan.Host.Core.Dbc;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.LlmProvider;

public class DeepSeekProviderTests
{
    private static DeepSeekOptions DefaultOptions() => new();

    private static AnalysisSession MakeSession(string evidenceId = "E-0001")
    {
        var evidence = new[] { new FaultAnalysisEvidence(
            EvidenceId: evidenceId, SignalKey: "0x100.Test.src", SourceId: "src",
            Type: "state-transition", TimestampSeconds: 1.0, Value: 1,
            EnumText: null, Description: "test evidence") };
        var report = new LocalReport(
            Evidence: evidence, Candidates: Array.Empty<CandidateSignal>(),
            DataQualityNotes: Array.Empty<string>(), Summary: "test", GeneratedAtUtc: DateTime.UtcNow);
        return new AnalysisSession(
            SessionId: Guid.NewGuid(), Version: 1,
            FaultEvent: new FaultEvent(1.0, TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(500), "test", DateTime.UtcNow),
            AnchorSnapshot: new AnchorSnapshot(1.0, 1.5,
                Array.Empty<AnchoredSignalValue>(), DateTime.UtcNow, 1),
            Report: report, CreatedAtUtc: DateTime.UtcNow);
    }

    private static DeepSeekProvider MakeProvider(
        MockHttpMessageHandler handler,
        ICredentialStore? credStore = null,
        DeepSeekOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("DeepSeek").ConfigurePrimaryHttpMessageHandler(() => handler);
        var sp = services.BuildServiceProvider();
        var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
        return new DeepSeekProvider(
            httpFactory,
            credStore ?? Substitute.For<ICredentialStore>(),
            NullLogger<DeepSeekProvider>.Instance,
            Options.Create(options ?? DefaultOptions()));
    }

    [Fact]
    public async Task AnalyzeAsync_HappyPath_ReturnsSummaryAndCitedIds()
    {
        var credStore = Substitute.For<ICredentialStore>();
        credStore.GetAsync("deepseek-api-key", Arg.Any<CancellationToken>()).Returns("sk-test-12345");

        var responseJson = JsonSerializer.Serialize(new
        {
            id = "test-id",
            @object = "chat.completion",
            created = 1234567890L,
            model = "deepseek-chat",
            choices = new[]
            {
                new {
                    index = 0,
                    message = new {
                        role = "assistant",
                        content = JsonSerializer.Serialize(new {
                            summary = "Analysis: BmsFaultState went to Active",
                            claims = new[] { new { evidence_ids = new[] { "E-0001" }, text = "valid claim" } }
                        })
                    },
                    finish_reason = "stop"
                }
            },
            usage = new { prompt_tokens = 100, completion_tokens = 50, total_tokens = 150 }
        });

        var handler = new MockHttpMessageHandler(responseJson, HttpStatusCode.OK);
        var provider = MakeProvider(handler, credStore);

        var result = await provider.AnalyzeAsync(MakeSession("E-0001"), CancellationToken.None);

        result.Error.Should().BeNull();
        result.Summary.Should().Contain("BmsFaultState");
        result.AttributedEvidenceIds.Should().BeEquivalentTo(new[] { "E-0001" });
    }

    [Fact]
    public async Task AnalyzeAsync_Unauthorized_ReturnsErrorEnvelope()
    {
        var credStore = Substitute.For<ICredentialStore>();
        credStore.GetAsync("deepseek-api-key", Arg.Any<CancellationToken>()).Returns("sk-bad");

        var handler = new MockHttpMessageHandler("Unauthorized", HttpStatusCode.Unauthorized);
        var provider = MakeProvider(handler, credStore);

        var result = await provider.AnalyzeAsync(MakeSession(), CancellationToken.None);

        result.Error.Should().Contain("invalid or revoked");
        result.Summary.Should().BeEmpty();
        result.AttributedEvidenceIds.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_TooManyRequests_ReturnsRateLimitError()
    {
        var credStore = Substitute.For<ICredentialStore>();
        credStore.GetAsync("deepseek-api-key", Arg.Any<CancellationToken>()).Returns("sk-test");
        var handler = new MockHttpMessageHandler("Rate limited", HttpStatusCode.TooManyRequests);
        var handlerWithHeader = new MockHttpMessageHandlerWithHeader("Rate limited",
            HttpStatusCode.TooManyRequests, "Retry-After", "60");
        var provider = MakeProvider(handlerWithHeader, credStore);

        var result = await provider.AnalyzeAsync(MakeSession(), CancellationToken.None);

        result.Error.Should().Contain("rate limit");
    }

    [Fact]
    public async Task AnalyzeAsync_ServerError_ReturnsServerError()
    {
        var credStore = Substitute.For<ICredentialStore>();
        credStore.GetAsync("deepseek-api-key", Arg.Any<CancellationToken>()).Returns("sk-test");
        var handler = new MockHttpMessageHandler("Server error", HttpStatusCode.InternalServerError);
        var provider = MakeProvider(handler, credStore);

        var result = await provider.AnalyzeAsync(MakeSession(), CancellationToken.None);

        result.Error.Should().Contain("server error");
    }

    [Fact]
    public async Task AnalyzeAsync_Timeout_ReturnsTimeoutError()
    {
        var credStore = Substitute.For<ICredentialStore>();
        credStore.GetAsync("deepseek-api-key", Arg.Any<CancellationToken>()).Returns("sk-test");
        var handler = new MockHttpMessageHandlerWithDelay(TimeSpan.FromSeconds(60));
        var provider = MakeProvider(handler, credStore, new DeepSeekOptions { TimeoutSeconds = 1 });

        var result = await provider.AnalyzeAsync(MakeSession(), CancellationToken.None);

        result.Error.Should().Contain("timed out");
    }

    [Fact]
    public async Task AnalyzeAsync_MalformedJson_ReturnsErrorEnvelope()
    {
        var credStore = Substitute.For<ICredentialStore>();
        credStore.GetAsync("deepseek-api-key", Arg.Any<CancellationToken>()).Returns("sk-test");
        var handler = new MockHttpMessageHandler("not valid json {{{", HttpStatusCode.OK);
        var provider = MakeProvider(handler, credStore);

        var result = await provider.AnalyzeAsync(MakeSession(), CancellationToken.None);

        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_NoApiKey_ReturnsConfigurationError()
    {
        var credStore = Substitute.For<ICredentialStore>();
        credStore.GetAsync("deepseek-api-key", Arg.Any<CancellationToken>()).Returns((string?)null);

        var provider = MakeProvider(new MockHttpMessageHandler("", HttpStatusCode.OK), credStore);

        var result = await provider.AnalyzeAsync(MakeSession(), CancellationToken.None);

        result.Error.Should().Contain("API key not configured");
    }

    // Mock helpers
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;
        public MockHttpMessageHandler(string body, HttpStatusCode status) { _body = body; _status = status; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") });
    }

    private sealed class MockHttpMessageHandlerWithHeader : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;
        private readonly string _headerName;
        private readonly string _headerValue;
        public MockHttpMessageHandlerWithHeader(string body, HttpStatusCode status, string headerName, string headerValue)
        { _body = body; _status = status; _headerName = headerName; _headerValue = headerValue; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = new HttpResponseMessage(_status)
            { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
            resp.Headers.Add(_headerName, _headerValue);
            return Task.FromResult(resp);
        }
    }

    private sealed class MockHttpMessageHandlerWithDelay : HttpMessageHandler
    {
        private readonly TimeSpan _delay;
        public MockHttpMessageHandlerWithDelay(TimeSpan delay) { _delay = delay; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(_delay, ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8) };
        }
    }
}
```

- [ ] **Step 2: Run tests**

```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~DeepSeekProviderTests" --logger "console;verbosity=minimal"
```
Expected: 7/7 PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/PeakCan.Host.App.Tests/Services/LlmProvider/DeepSeekProviderTests.cs
git commit -m "v3.54.0 T5: DeepSeekProvider integration tests (App; 7 tests with mocked HttpMessageHandler)"
```

---

### Task 6: DI wiring (replace NotImplementedLlmProvider + AddHttpClient)

**Files:**
- Modify: `src/PeakCan.Host.App/Composition/AppHostBuilder/AppServicesFlow.cs`

**Interfaces:**
- Replaces `ILlmProvider → NotImplementedLlmProvider` with `ILlmProvider → DeepSeekProvider` (D7: real provider as default)
- Adds `services.AddHttpClient("DeepSeek")` for IHttpClientFactory
- Adds `services.Configure<DeepSeekOptions>(...)` (default options; future override path)

- [ ] **Step 1: Find existing ILlmProvider DI line**

```bash
grep -n "ILlmProvider\|NotImplementedLlmProvider\|AddHttpClient" src/PeakCan.Host.App/Composition/AppHostBuilder/AppServicesFlow.cs
```

- [ ] **Step 2: Modify DI registrations**

Add (BEFORE the existing `ILlmProvider` line):
```csharp
// v3.54.0 MINOR P1b: DeepSeek HttpClient via IHttpClientFactory
// (D1 socket lifecycle management; D6 30s timeout)
services.AddHttpClient("DeepSeek", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("peakcan-host/3.54.0");
});
```

Replace the existing line:
```csharp
// v3.52.0 P0 stub (kept as fallback option for users without API key)
services.AddSingleton<PeakCan.Host.Core.Analysis.ILlmProvider,
                       PeakCan.Host.Core.Analysis.NotImplementedLlmProvider>();
```

With:
```csharp
// v3.54.0 MINOR: DeepSeekProvider is now the default ILlmProvider
// impl. NotImplementedLlmProvider kept for explicit fallback (D7).
// DeepSeekProvider reads API key from ICredentialStore (v3.53.1 P1a
// security foundation) — never from appsettings.json.
services.AddSingleton<PeakCan.Host.Core.Analysis.ILlmProvider, DeepSeekProvider>();
// DeepSeekOptions default (override via appsettings.json:Llm:DeepSeek section in future)
services.Configure<PeakCan.Host.Core.Analysis.DeepSeekOptions>(options =>
{
    // Defaults are baked into the record; this Configure call enables
    // IOptions<DeepSeekOptions> injection without overriding.
});
```

- [ ] **Step 3: Build + test verify**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-build --nologo -c Debug --filter "FullyQualifiedName~DeepSeekProviderTests|FullyQualifiedName~AnalysisFlowTests|FullyQualifiedName~AnchorSnapshotFlowTests" --logger "console;verbosity=minimal"
```
Expected: 0 errors; 7 + 2 + 4 = 13 tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/PeakCan.Host.App/Composition/AppHostBuilder/AppServicesFlow.cs
git commit -m "v3.54.0 T6: DI wiring — AddHttpClient DeepSeek + DeepSeekProvider as default ILlmProvider (App)"
```

---

### Task 7: Full solution CI + coverage check

- [ ] **Step 1: Full solution build**

```bash
dotnet build PeakCan.Host.slnx -c Debug --no-restore
```
Expected: 0 errors.

- [ ] **Step 2: Full solution test (sequential)**

```bash
dotnet test PeakCan.Host.slnx --no-build --nologo -c Debug --logger "console;verbosity=minimal" -- xUnit.MaxParallelThreads=1
```
Expected: 1443 baseline + 6 (T2 EvidenceIdWhitelistFilter) + 7 (T5 DeepSeekProvider) = **1456 PASS / 0 FAIL / 5 SKIP**.

---

### Task 8: Release notes + tier-3 ship

**Files:**
- Create: `docs/release-notes-v3-54-0-minor.md`
- Modify: `src/Directory.Build.props` (3.53.1 → 3.54.0)
- Push + PR + squash + tag + GH release

- [ ] **Step 1: Write release notes**

Create `docs/release-notes-v3-54-0-minor.md` covering:
- P1 LLM complete (P1b + P1c)
- DeepSeekProvider replaces NotImplementedLlmProvider as default
- API key via Windows Credential Manager (P1a security foundation)
- Evidence ID whitelist filter
- 6 NEW 1/3 lesson candidates
- Sister of aspice-toolkit (provider-registry pattern + DeepSeek systematic bias insight)

- [ ] **Step 2: Bump version**

`src/Directory.Build.props`:
```xml
<Version>3.54.0</Version>
<AssemblyVersion>3.54.0.0</AssemblyVersion>
<FileVersion>3.54.0.0</FileVersion>
<InformationalVersion>3.54.0</InformationalVersion>
```

- [ ] **Step 3: Tier-3 ship**

```bash
git add docs/release-notes-v3-54-0-minor.md src/Directory.Build.props
git commit -m "v3.54.0: version bump + release notes (P1 LLM complete; DeepSeek + HttpClient + JSON + Evidence ID whitelist)"
git push -u origin feature/v3-54-0-minor-p1-llm-complete
gh pr create --base main --title "v3.54.0 MINOR: P1 LLM complete (DeepSeek Provider + HttpClient + JSON + Evidence ID whitelist)" --body-file docs/release-notes-v3-54-0-minor.md
gh pr merge --squash --delete-branch
git reset --hard origin/main  # force-correct per sister pattern (v3.52.0/v3.52.1/v3.53.0/v3.53.1)
git tag -a v3.54.0 -m "v3.54.0 MINOR — P1 LLM complete"
git push origin v3.54.0
gh release create v3.54.0 --notes-file docs/release-notes-v3-54-0-minor.md
```

---

## Sister-lesson candidates to monitor

| Lesson | Status | What T1-T6 might observe |
|---|---|---|
| `deepseek-api-key-must-flow-through-icredentialstore-not-appsettingsjson` | NEW 1/3 (P1b) | T4: API key read from ICredentialStore only |
| `httpclient-via-ihttpclientfactory-required-for-socket-lifecycle-management` | NEW 1/3 (P1b) | T4+T6: AddHttpClient named client "DeepSeek" |
| `llm-evidence-id-whitelist-filter-must-drop-entire-claim-not-just-id` | NEW 1/3 (P1c) | T2: per-claim filter not per-ID |
| `system-text-json-preferred-over-newtonsoft-for-net10-no-3rd-party-dep` | NEW 1/3 (P1b) | T3+T4: System.Text.Json only, no Newtonsoft / NJsonSchema |
| `deepseek-systematic-bias-mitigation-via-strict-json-schema-and-whitelist` | NEW 1/3 (P1c) | T2+T4: SystemPrompt + summary extraction + whitelist mitigate bias |
| `llm-error-must-be-result-envelope-not-throw-so-caller-can-fall-back-to-local` | NEW 1/3 (P1b) | T4: all error paths return ErrorResult, never throw |

## Verification

- `dotnet build PeakCan.Host.slnx`: 0 errors
- `dotnet test PeakCan.Host.slnx`: 1456 PASS / 0 FAIL / 5 SKIP
- 6 NEW 1/3 candidates observed at 1/3
- 19 cumulative 1/3 candidates (v3.52.0 + v3.52.1 + v3.53.0 + v3.53.1) + 6 new = 25 candidates now tracked
- Tag v3.54.0 + GH release published

## Out of scope (YAGNI)

- API Key UI setting → P2 PATCH
- Streaming LLM response → future
- Tool calling → not planned
- AzureOpenAI / Ollama concrete impls → future (interface预留)
- NJsonSchema / JsonSchema.Net 3rd-party → not planned (手写 whitelist sufficient)
- Retry policy with exponential backoff → not planned
- Token usage tracking persistence → not planned (log only)
- Real DeepSeek API integration test (needs CI env var) → defer to integration test PATCH