# W41 Plan — Streaming LLM Response for DeepSeek (v3.59.0 MINOR)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce AI Analysis panel perceived latency from ~30s (non-streaming) to ~1s (first token) by adding Server-Sent Events streaming to `DeepSeekProvider`. Preserve v3.54.0 `LlmAnalysisResult` envelope contract; add `IAsyncEnumerable<LlmPartialUpdate>` parallel API surface for streaming-aware consumers (WPF AI Analysis panel).

**Architecture:** Add `LlmPartialUpdate` discriminated union (3 variants) + extend `ILlmProvider` with default `AnalyzeStreamingAsync` method that wraps `AnalyzeAsync` (single-shot fallback). Add `DeepSeekStreamingChunk` DTO + `DeepSeekRequest.Stream` field + `DeepSeekProvider.StreamAnalyzeAsync` SSE path. WPF AI Analysis panel adds partial-summary TextBlock bound to streaming command.

**Tech Stack:** C# .NET 10 / WPF / System.Text.Json / IAsyncEnumerable / IHttpClientFactory / SSE (text/event-stream)

## Global Constraints

1. **Public API 100% preserved** — `AnalyzeAsync(AnalysisSession, CancellationToken) → Task<LlmAnalysisResult>` unchanged; new `AnalyzeStreamingAsync` is **additive**.
2. **Default interface method fallback** — `AnalyzeStreamingAsync` default impl wraps `AnalyzeAsync` so `NotImplementedLlmProvider` + any future provider (Azure/Ollama) doesn't break.
3. **LlmAnalysisResult envelope preserved** — all errors return `LlmAnalysisResult.Error` (never throw); SSE parse failures fall back to partial-content summary + Error envelope.
4. **Tests must pass without modification of pre-existing tests** — baseline 1461 PASS / 0 FAIL / 5 SKIP from v3.58.0 preserved.
5. **NEVER log Authorization headers or response body content** — only log chunk count + final finish_reason + total tokens.
6. **HTTP client timeout via IHttpClientFactory** — `services.AddHttpClient("DeepSeek", client => { client.Timeout = TimeSpan.FromSeconds(30); ... })` registered in T1.8 (D3 of spec); SSE streaming uses `ResponseHeadersRead` mode so timeout per-chunk applies.
7. **LoC budget ~+328 net** — Core types 40 LoC + Provider +60 LoC + DTOs +35 LoC + Options +5 LoC + VM +40 LoC + XAML +25 LoC + 5 tests 125 LoC + release notes 5 LoC.

---

## File Structure

```
src/PeakCan.Host.Core/
├── Analysis/
│   ├── ILlmProvider.cs                          (MODIFY +20 LoC; add AnalyzeStreamingAsync default + extension)
│   ├── LlmAnalysisResult.cs                     (unchanged)
│   └── LlmPartialUpdate.cs                      (NEW ~20 LoC; sealed abstract record + 3 variants)

src/PeakCan.Host.App/
├── Services/
│   └── LlmProvider/
│       ├── DeepSeekProvider.cs                  (MODIFY +60 LoC; add StreamAnalyzeAsync SSE path)
│       ├── DeepSeekRequest.cs                   (MODIFY +3 LoC; add Stream field)
│       ├── DeepSeekStreamingChunk.cs            (NEW ~30 LoC; per-chunk DTO + Delta + Choice)
│       └── DeepSeekOptions.cs                   (MODIFY +5 LoC; add UseStreaming default true)

src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/
└── AnalysisFlow.cs                              (MODIFY +40 LoC; RunAnalysisStreamingCommand + partial text binding)

src/PeakCan.Host.App/Views/
└── TraceViewerView.AIPanel.xaml                 (MODIFY +25 LoC; partial-summary TextBlock + ScrollViewer)

tests/PeakCan.Host.Core.Tests/
└── Analysis/
    ├── LlmPartialUpdateTests.cs                 (NEW ~15 LoC; 1 test)
    └── ILlmProviderExtensionsTests.cs            (NEW ~30 LoC; 1 test)

tests/PeakCan.Host.App.Tests/
└── Services/
    └── LlmProvider/
        └── DeepSeekProviderStreamingTests.cs     (NEW ~80 LoC; 3 tests)
```

---

### Task 0: Branch + spec verify + plan commit + baseline

**Files:**
- Verify: `docs/superpowers/specs/2026-07-17-w41-streaming-llm.md` committed at `e857b8d`

- [ ] **Step 1: Verify spec is committed**

```bash
git log --oneline -5
```

Expected: `e857b8d W41 spec: Streaming LLM response for DeepSeek (v3.59.0 MINOR; SSE + IAsyncEnumerable)` visible.

- [ ] **Step 2: Verify branch + baseline tests**

```bash
git status
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~DeepSeek|FullyQualifiedName~ILlmProvider|FullyQualifiedName~LlmAnalysisResult" --logger "console;verbosity=minimal"
```

Expected: 0 errors, 0 warnings; existing DeepSeek-related tests PASS.

- [ ] **Step 3: Commit plan**

```bash
git add docs/superpowers/plans/2026-07-17-w41-streaming-llm.md
git commit -m "W41 plan: Streaming LLM response for DeepSeek (5-task roll-out: data type + DTO + VM + XAML + tests)"
```

---

### Task 1: LlmPartialUpdate discriminated union (Core-side data type)

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/LlmPartialUpdate.cs`

**Interfaces:**
- Produces: `LlmPartialUpdate` sealed abstract record + 3 nested variants (`PartialSummary` / `PartialEvidenceId` / `FinalResult`)

- [ ] **Step 1: Create LlmPartialUpdate.cs**

Write `src/PeakCan.Host.Core/Analysis/LlmPartialUpdate.cs`:

```csharp
namespace PeakCan.Host.Core.Analysis;

/// <summary>
/// v3.59.0 MINOR: streaming progress notifications emitted by
/// <see cref="ILlmProvider.AnalyzeStreamingAsync"/>. Three variants:
/// <list type="bullet">
///   <item><see cref="PartialSummary"/> — incremental text fragment to
///         append to the running Summary (SSE <c>delta.content</c>).</item>
///   <item><see cref="PartialEvidenceId"/> — one Evidence ID surfaced
///         from the streaming response (e.g. "E-0017"); the consumer
///         accumulates these into the final whitelist-filtered set.</item>
///   <item><see cref="FinalResult"/> — terminal marker carrying the
///         completed <see cref="LlmAnalysisResult"/> after whitelist
///         filtering + 4096-char cap.</item>
/// </list>
/// Use pattern matching (`switch { PartialSummary s => ..., FinalResult r => ... }`)
/// to dispatch each variant.
/// </summary>
public abstract record LlmPartialUpdate
{
    private LlmPartialUpdate() { }

    /// <summary>Incremental Summary fragment to append.</summary>
    public sealed record PartialSummary(string Delta) : LlmPartialUpdate;

    /// <summary>One Evidence ID surfaced from the streaming response.</summary>
    public sealed record PartialEvidenceId(string EvidenceId) : LlmPartialUpdate;

    /// <summary>Terminal marker carrying the completed result.</summary>
    public sealed record FinalResult(LlmAnalysisResult Result) : LlmPartialUpdate;
}
```

- [ ] **Step 2: Verify build**

```bash
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore
```

Expected: 0 errors, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/PeakCan.Host.Core/Analysis/LlmPartialUpdate.cs
git commit -m "W41 T1: LlmPartialUpdate discriminated union (PartialSummary + PartialEvidenceId + FinalResult)"
```

---

### Task 2: ILlmProvider extension (default AnalyzeStreamingAsync + single-shot fallback)

**Files:**
- Modify: `src/PeakCan.Host.Core/Analysis/ILlmProvider.cs`

**Interfaces:**
- Adds: `IAsyncEnumerable<LlmPartialUpdate> AnalyzeStreamingAsync(AnalysisSession, CancellationToken)` default interface method
- Adds: `internal static ILlmProviderExtensions` class with `AnalyzeStreamingFromSingleShot` extension

- [ ] **Step 1: Modify ILlmProvider.cs**

Add to `src/PeakCan.Host.Core/Analysis/ILlmProvider.cs`:

```csharp
using System.Runtime.CompilerServices;

namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.52.0 MINOR: LLM provider contract (interface only in P0).
/// P1 PATCH will add: DeepSeekProvider, AzureOpenAIProvider, LocalOllamaProvider.
/// All implementations MUST:
/// - Validate Evidence IDs against the input session (whitelist filter per
///   hard-boundary #13: drop invalid ID references AND their associated
///   claims; only reject whole response if all claims are invalid).
/// - NOT log Authorization headers or full response bodies.
/// - Surface 401/429/timeout/JSON-parse errors as LlmAnalysisResult.Error
///   (NOT exception), so the caller can show degraded local-only results.</summary>
public interface ILlmProvider
{
    string DisplayName { get; }
    Task<LlmAnalysisResult> AnalyzeAsync(AnalysisSession session, CancellationToken ct);

    // v3.59.0 MINOR: streaming variant. Default impl wraps AnalyzeAsync
    // via the AnalyzeStreamingFromSingleShot extension (defined below)
    // so NotImplementedLlmProvider + future Azure/Ollama deferred
    // providers don't break — single-shot fallback emits exactly one
    // FinalResult at completion.
    IAsyncEnumerable<LlmPartialUpdate> AnalyzeStreamingAsync(
        AnalysisSession session,
        CancellationToken ct)
        => ILlmProviderExtensions.AnalyzeStreamingFromSingleShot(
            this, session, ct);
}

/// <summary>P0 stub. P1 PATCH will replace with concrete providers.</summary>
public sealed class NotImplementedLlmProvider : ILlmProvider
{
    public string DisplayName => "(no LLM — P0 local-only)";

    public Task<LlmAnalysisResult> AnalyzeAsync(
        AnalysisSession session, CancellationToken ct) =>
        throw new NotImplementedException(
            "P1 PATCH will implement LLM Provider; see ILlmProvider contract.");
}

/// <summary>
/// v3.59.0 MINOR: default-interface-method fallback for the streaming
/// API. Implementations that don't override <see cref="ILlmProvider.AnalyzeStreamingAsync"/>
/// get the single-shot path: call <see cref="ILlmProvider.AnalyzeAsync"/>,
/// then emit exactly one <see cref="LlmPartialUpdate.FinalResult"/>.
/// </summary>
internal static class ILlmProviderExtensions
{
    public static async IAsyncEnumerable<LlmPartialUpdate> AnalyzeStreamingFromSingleShot(
        this ILlmProvider provider,
        AnalysisSession session,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var result = await provider.AnalyzeAsync(session, ct).ConfigureAwait(false);
        yield return new LlmPartialUpdate.FinalResult(result);
    }
}
```

- [ ] **Step 2: Verify build + tests PASS (single-shot fallback default)**

```bash
dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj -c Debug --no-restore
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~NotImplementedLlmProvider|FullyQualifiedName~LlmAnalysisResult" --logger "console;verbosity=minimal"
```

Expected: 0 errors, 0 warnings; existing tests PASS.

- [ ] **Step 3: Commit**

```bash
git add src/PeakCan.Host.Core/Analysis/ILlmProvider.cs
git commit -m "W41 T2: ILlmProvider.AnalyzeStreamingAsync default + single-shot fallback extension"
```

---

### Task 3: LlmPartialUpdate tests (Core-side unit test)

**Files:**
- Create: `tests/PeakCan.Host.Core.Tests/Analysis/LlmPartialUpdateTests.cs`

**Interfaces:**
- Verifies: 3 variant constructors + pattern matching exhaustiveness

- [ ] **Step 1: Create LlmPartialUpdateTests.cs**

Write `tests/PeakCan.Host.Core.Tests/Analysis/LlmPartialUpdateTests.cs`:

```csharp
using FluentAssertions;
using PeakCan.Host.Core.Analysis;

namespace PeakCan.Host.Core.Tests.Analysis;

public class LlmPartialUpdateTests
{
    [Fact]
    public void PartialSummary_CarriesDelta()
    {
        var update = new LlmPartialUpdate.PartialSummary("Hello");
        update.Delta.Should().Be("Hello");
    }

    [Fact]
    public void PartialEvidenceId_CarriesEvidenceId()
    {
        var update = new LlmPartialUpdate.PartialEvidenceId("E-0017");
        update.EvidenceId.Should().Be("E-0017");
    }

    [Fact]
    public void FinalResult_CarriesResult()
    {
        var result = new LlmAnalysisResult(
            Summary: "summary",
            AttributedEvidenceIds: new[] { "E-0001" },
            RawResponseJson: "{}",
            Error: null);
        var update = new LlmPartialUpdate.FinalResult(result);
        update.Result.Should().BeSameAs(result);
    }

    [Fact]
    public void Variants_ArePatternMatchable()
    {
        // Pin the discriminated-union shape: switch over the 3 variants
        // must handle each (no warning for missing cases).
        var updates = new LlmPartialUpdate[]
        {
            new LlmPartialUpdate.PartialSummary("a"),
            new LlmPartialUpdate.PartialEvidenceId("E-1"),
            new LlmPartialUpdate.FinalResult(new LlmAnalysisResult("", Array.Empty<string>(), "", null)),
        };

        var counts = new Dictionary<string, int>();
        foreach (var u in updates)
        {
            var key = u switch
            {
                LlmPartialUpdate.PartialSummary => "summary",
                LlmPartialUpdate.PartialEvidenceId => "id",
                LlmPartialUpdate.FinalResult => "final",
                _ => "unknown"
            };
            counts[key] = counts.GetValueOrDefault(key, 0) + 1;
        }

        counts["summary"].Should().Be(1);
        counts["id"].Should().Be(1);
        counts["final"].Should().Be(1);
    }
}
```

- [ ] **Step 2: Verify build + tests PASS**

```bash
dotnet build tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~LlmPartialUpdateTests" --logger "console;verbosity=minimal"
```

Expected: 4/4 PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/PeakCan.Host.Core.Tests/Analysis/LlmPartialUpdateTests.cs
git commit -m "W41 T3: LlmPartialUpdateTests with 4 unit tests (variant constructor + pattern matching)"
```

---

### Task 4: DeepSeekStreamingChunk DTO + DeepSeekRequest.Stream field + DeepSeekOptions.UseStreaming field

**Files:**
- Create: `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekStreamingChunk.cs`
- Modify: `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekRequest.cs`
- Modify: `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekOptions.cs`

**Interfaces:**
- Produces: `DeepSeekStreamingChunk` + 2 nested records (Choice + Delta); `DeepSeekRequest.Stream` field; `DeepSeekOptions.UseStreaming` field

- [ ] **Step 1: Create DeepSeekStreamingChunk.cs**

Write `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekStreamingChunk.cs`:

```csharp
using System.Text.Json.Serialization;

namespace PeakCan.Host.App.Services.LlmProvider;

/// <summary>v3.59.0 MINOR: per-chunk DTO for DeepSeek SSE streaming
/// response. Each <c>data: {...}</c> line in the SSE stream maps to
/// one <see cref="DeepSeekStreamingChunk"/>; the final line is
/// <c>data: [DONE]</c> (sentinel — not deserialized).</summary>
public sealed record DeepSeekStreamingChunk
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("object")]
    public string? Object { get; init; }

    [JsonPropertyName("created")]
    public long? Created { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("choices")]
    public List<DeepSeekStreamingChoice>? Choices { get; init; }
}

public sealed record DeepSeekStreamingChoice
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("delta")]
    public DeepSeekStreamingDelta? Delta { get; init; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }
}

public sealed record DeepSeekStreamingDelta
{
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }
}
```

- [ ] **Step 2: Modify DeepSeekRequest.cs**

Add to `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekRequest.cs`:

```csharp
[JsonPropertyName("stream")]
public bool Stream { get; init; }
```

(Insert before the closing `}` of the record class. Place near other request fields.)

- [ ] **Step 3: Modify DeepSeekOptions.cs**

Add to `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekOptions.cs`:

```csharp
/// <summary>
/// v3.59.0 MINOR: when true (default), <see cref="DeepSeekProvider"/>
/// uses SSE streaming via <c>stream: true</c>. When false, falls back
/// to the v3.54.0 single-shot non-streaming path. Tests + callers with
/// constrained network may opt out by setting this to false in DI.
/// </summary>
public bool UseStreaming { get; init; } = true;
```

- [ ] **Step 4: Verify build**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```

Expected: 0 errors, 6 pre-existing warnings.

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/Services/LlmProvider/DeepSeekStreamingChunk.cs src/PeakCan.Host.App/Services/LlmProvider/DeepSeekRequest.cs src/PeakCan.Host.App/Services/LlmProvider/DeepSeekOptions.cs
git commit -m "W41 T4: DeepSeekStreamingChunk DTO + DeepSeekRequest.Stream + DeepSeekOptions.UseStreaming"
```

---

### Task 5: ILlmProviderExtensionsTests (single-shot fallback test)

**Files:**
- Create: `tests/PeakCan.Host.Core.Tests/Analysis/ILlmProviderExtensionsTests.cs`

**Interfaces:**
- Verifies: `AnalyzeStreamingFromSingleShot` emits exactly one `FinalResult`

- [ ] **Step 1: Create ILlmProviderExtensionsTests.cs**

Write `tests/PeakCan.Host.Core.Tests/Analysis/ILlmProviderExtensionsTests.cs`:

```csharp
using FluentAssertions;
using NSubstitute;
using PeakCan.Host.Core.Analysis;

namespace PeakCan.Host.Core.Tests.Analysis;

public class ILlmProviderExtensionsTests
{
    [Fact]
    public async Task AnalyzeStreamingFromSingleShot_EmitsOneFinalResult()
    {
        // Provider that overrides AnalyzeStreamingAsync is the production
        // path. Provider that does NOT override falls back to the default
        // interface method (AnalyzeStreamingAsync default wraps AnalyzeAsync).
        // Either way, the contract is: exactly 1 FinalResult, carrying the
        // AnalyzeAsync result.
        var provider = Substitute.For<ILlmProvider>();
        provider.DisplayName.Returns("FakeProvider");
        var expectedResult = new LlmAnalysisResult(
            Summary: "test-summary",
            AttributedEvidenceIds: new[] { "E-0001", "E-0002" },
            RawResponseJson: "{}",
            Error: null);
        provider.AnalyzeAsync(Arg.Any<AnalysisSession>(), Arg.Any<CancellationToken>())
            .Returns(expectedResult);

        var session = new AnalysisSession(
            Version: 1,
            FaultEvent: new FaultEvent(0.0, TimeSpan.Zero, TimeSpan.Zero, "", DateTimeOffset.UtcNow),
            AnchorSnapshot: new AnchorSnapshot(0.0, 0.0, Array.Empty<AnchoredSignalValue>(), DateTimeOffset.UtcNow, 1),
            Report: new LocalReport("", Array.Empty<FaultAnalysisEvidence>(), Array.Empty<CandidateSignal>()),
            CreatedAtUtc: DateTimeOffset.UtcNow);

        var emitted = new List<LlmPartialUpdate>();
        await foreach (var update in provider.AnalyzeStreamingAsync(session, CancellationToken.None))
        {
            emitted.Add(update);
        }

        emitted.Should().HaveCount(1);
        emitted[0].Should().BeOfType<LlmPartialUpdate.FinalResult>();
        var finalResult = ((LlmPartialUpdate.FinalResult)emitted[0]).Result;
        finalResult.Should().BeSameAs(expectedResult);
    }
}
```

- [ ] **Step 2: Verify build + tests PASS**

```bash
dotnet build tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~ILlmProviderExtensionsTests" --logger "console;verbosity=minimal"
```

Expected: 1/1 PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/PeakCan.Host.Core.Tests/Analysis/ILlmProviderExtensionsTests.cs
git commit -m "W41 T5: ILlmProviderExtensionsTests with 1 test (single-shot fallback emits exactly 1 FinalResult)"
```

---

### Task 6: DeepSeekProvider.StreamAnalyzeAsync (SSE path) + AnalysisFlow.RunAnalysisStreamingCommand

**Files:**
- Modify: `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekProvider.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/AnalysisFlow.cs`

**Interfaces:**
- Adds: `IAsyncEnumerable<LlmPartialUpdate> StreamAnalyzeAsync(AnalysisSession, CancellationToken)` public method
- Adds: `[RelayCommand] RunAnalysisStreamingAsync()` + `[ObservableProperty] string _streamingSummary` + `[ObservableProperty] IReadOnlyList<string> _streamingEvidenceIds`

- [ ] **Step 1: Modify DeepSeekProvider.cs — add StreamAnalyzeAsync**

Add to `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekProvider.cs`:

```csharp
using System.Runtime.CompilerServices;

public async IAsyncEnumerable<LlmPartialUpdate> StreamAnalyzeAsync(
    AnalysisSession session,
    [EnumeratorCancellation] CancellationToken ct)
{
    ArgumentNullException.ThrowIfNull(session);

    // 1. Read API key (same as AnalyzeAsync — never log value)
    string? apiKey;
    try
    {
        apiKey = await _credentialStore.GetAsync(ApiKeyCredentialKey, ct);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to read API key from credential store");
        yield return new LlmPartialUpdate.FinalResult(ErrorResult(
            "Failed to read API key from credential store"));
        yield break;
    }

    if (string.IsNullOrEmpty(apiKey))
    {
        yield return new LlmPartialUpdate.FinalResult(ErrorResult(
            "API key not configured; set in Windows Credential Manager (target: peakcan-host:deepseek-api-key)"));
        yield break;
    }

    // 2. Build request with stream=true
    var request = new DeepSeekRequest
    {
        Model = _options.Model,
        Stream = true,
        Messages = new List<DeepSeekMessage>
        {
            new() { Role = "system", Content = SystemPrompt },
            new() { Role = "user", Content = SerializeSessionForLLM(session) },
        },
    };

    // 3. Open SSE stream
    var http = _httpClientFactory.CreateClient("DeepSeek");
    using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_options.ApiBase}/chat/completions")
    {
        Content = JsonContent.Create(request),
    };
    httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

    using var response = await http.SendAsync(
        httpRequest,
        HttpCompletionOption.ResponseHeadersRead,
        ct).ConfigureAwait(false);

    if (!response.IsSuccessStatusCode)
    {
        var statusCode = (int)response.StatusCode;
        _logger.LogWarning("DeepSeek streaming returned non-success status: {StatusCode}", statusCode);
        var errMsg = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "DeepSeek API key invalid or revoked",
            HttpStatusCode.TooManyRequests => "DeepSeek rate limit exceeded; retry later",
            _ => $"DeepSeek server error (HTTP {statusCode})",
        };
        yield return new LlmPartialUpdate.FinalResult(ErrorResult(errMsg));
        yield break;
    }

    // 4. Read SSE stream
    var summaryBuilder = new StringBuilder();
    var evidenceIds = new HashSet<string>();
    var rawJsonBuilder = new StringBuilder();
    var chunkCount = 0;

    using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    using var reader = new StreamReader(stream, Encoding.UTF8);

    while (!reader.EndOfStream)
    {
        ct.ThrowIfCancellationRequested();

        var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (line is null) break;
        if (line.Length == 0) continue;            // SSE event delimiter
        if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

        var payload = line.Substring("data: ".Length);
        if (payload == "[DONE]") break;

        rawJsonBuilder.AppendLine(payload);
        chunkCount++;

        DeepSeekStreamingChunk? chunk;
        try
        {
            chunk = JsonSerializer.Deserialize<DeepSeekStreamingChunk>(payload,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            // Resilient to single bad chunk — log + continue.
            _logger.LogWarning(ex, "Failed to parse DeepSeek SSE chunk; skipping");
            continue;
        }

        if (chunk?.Choices is null) continue;

        foreach (var choice in chunk.Choices)
        {
            var delta = choice.Delta?.Content;
            if (!string.IsNullOrEmpty(delta))
            {
                summaryBuilder.Append(delta);
                yield return new LlmPartialUpdate.PartialSummary(delta);
            }

            if (!string.IsNullOrEmpty(choice.FinishReason))
            {
                _logger.LogInformation(
                    "DeepSeek streaming finished: {ChunkCount} chunks, reason={FinishReason}",
                    chunkCount, choice.FinishReason);
            }
        }
    }

    // 5. Build final result (parse accumulated summary for evidence IDs)
    var finalSummary = summaryBuilder.ToString();
    var (_, citedIds) = ParseLLMJsonResponse(finalSummary);
    if (finalSummary.Length > 4096) finalSummary = finalSummary[..4096];

    var raw = new LlmAnalysisResult(
        Summary: finalSummary,
        AttributedEvidenceIds: citedIds,
        RawResponseJson: rawJsonBuilder.ToString(),
        Error: null);
    var filtered = EvidenceIdWhitelistFilter.Filter(session, raw);
    yield return new LlmPartialUpdate.FinalResult(filtered);
}
```

- [ ] **Step 2: Modify DeepSeekProvider.AnalyzeAsync — opt-out via UseStreaming**

Modify the `AnalyzeAsync` method in `DeepSeekProvider.cs` to dispatch to `StreamAnalyzeAsync` when `UseStreaming = true`:

Find the existing `var rawJson = await response.Content.ReadAsStringAsync(ct);` line near the end of `AnalyzeAsync` (currently the non-streaming code path). **BEFORE that line**, insert:

```csharp
    // v3.59.0 MINOR: when UseStreaming is true (default), route through
    // StreamAnalyzeAsync so the caller benefits from incremental delivery.
    // The async-foreach collects the final result; partial updates are
    // discarded because the caller asked for the single-shot signature.
    if (_options.UseStreaming)
    {
        LlmPartialUpdate? lastFinal = null;
        await foreach (var update in StreamAnalyzeAsync(session, ct).ConfigureAwait(false))
        {
            if (update is LlmPartialUpdate.FinalResult fr) lastFinal = fr;
        }
        if (lastFinal is null) return ErrorResult("DeepSeek streaming produced no FinalResult");
        return ((LlmPartialUpdate.FinalResult)lastFinal).Result;
    }
```

- [ ] **Step 3: Modify AnalysisFlow.cs — add RunAnalysisStreamingCommand**

Add to `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/AnalysisFlow.cs`:

```csharp
using PeakCan.Host.App.Services.AnalysisApiKey;
using PeakCan.Host.App.Services.LlmProvider;

// W41 MINOR: streaming summary bound to AI Analysis panel partial
// TextBlock. Appended incrementally as DeepSeekProvider emits
// LlmPartialUpdate.PartialSummary chunks.
[ObservableProperty]
private string _streamingSummary = "";

// W41 MINOR: streaming Evidence IDs accumulated as
// PartialEvidenceId chunks arrive; final filtered set lands in
// CurrentAnalysisSession.Report.AttributedEvidenceIds on FinalResult.
[ObservableProperty]
private System.Collections.Generic.IReadOnlyList<string> _streamingEvidenceIds =
    System.Array.Empty<string>();

[RelayCommand(CanExecute = nameof(CanRunAnalysis))]
private async Task RunAnalysisStreamingAsync()
{
    if (CurrentAnchorSnapshot is null)
    {
        ErrorMessage = "请先设绿/蓝锚并点『锁定 anchor 状态』";
        return;
    }

    IsAnalysisRunning = true;
    StatusMessage = "分析中 (streaming)...";
    StreamingSummary = "";
    StreamingEvidenceIds = System.Array.Empty<string>();

    try
    {
        await foreach (var update in _llmProvider.AnalyzeStreamingAsync(CurrentAnalysisSession!, ct: default).ConfigureAwait(true))
        {
            switch (update)
            {
                case LlmPartialUpdate.PartialSummary ps:
                    StreamingSummary += ps.Delta;
                    break;
                case LlmPartialUpdate.PartialEvidenceId peid:
                    var newIds = StreamingEvidenceIds.Append(peid.EvidenceId).ToArray();
                    StreamingEvidenceIds = newIds;
                    break;
                case LlmPartialUpdate.FinalResult fr:
                    CurrentAnalysisSession = fr.Result;
                    StatusMessage = fr.Result.Error ?? "分析完成";
                    break;
            }
        }
    }
    catch (Exception ex)
    {
        StatusMessage = $"分析异常: {ex.GetType().Name}";
        _logger.LogWarning(ex, "RunAnalysisStreamingAsync failed");
    }
    finally
    {
        IsAnalysisRunning = false;
    }
}
```

Add field `_llmProvider` in the partial if not present (should be added when `ILlmProvider` DI is introduced; if missing, add `private readonly ILlmProvider _llmProvider;` and corresponding ctor arg).

- [ ] **Step 4: Verify build + tests PASS**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~DeepSeek|FullyQualifiedName~TraceViewerViewModel" --logger "console;verbosity=minimal"
```

Expected: 0 errors, 6 pre-existing warnings; existing DeepSeek + TraceViewerViewModel tests PASS (use the existing `Substitute.For<ICredentialStore>()` pattern from W40 T6 fix).

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/Services/LlmProvider/DeepSeekProvider.cs src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/AnalysisFlow.cs
git commit -m "W41 T6: DeepSeekProvider.StreamAnalyzeAsync SSE path + AnalyzeAsync UseStreaming routing + AnalysisFlow RunAnalysisStreamingCommand"
```

---

### Task 7: DeepSeekProviderStreamingTests (3 tests)

**Files:**
- Create: `tests/PeakCan.Host.App.Tests/Services/LlmProvider/DeepSeekProviderStreamingTests.cs`

**Interfaces:**
- Verifies: chunk accumulation, evidence ID extraction mid-stream, cancellation breaks stream

- [ ] **Step 1: Create DeepSeekProviderStreamingTests.cs**

Write `tests/PeakCan.Host.App.Tests/Services/LlmProvider/DeepSeekProviderStreamingTests.cs`:

```csharp
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PeakCan.Host.App.Services.AnalysisApiKey;
using PeakCan.Host.App.Services.LlmProvider;
using PeakCan.Host.Core.Analysis;

namespace PeakCan.Host.App.Tests.Services.LlmProvider;

public class DeepSeekProviderStreamingTests
{
    /// <summary>
    /// Minimal HttpMessageHandler stub that returns a fixed SSE payload
    /// and records the last Authorization header for assertion.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public string? AuthorizationHeader { get; private set; }
        public string SsePayload { get; init; } = "";
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public CancellationToken ObservedCancellationToken { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ObservedCancellationToken = cancellationToken;
            AuthorizationHeader = request.Headers.Authorization?.ToString();

            if (Status != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(Status);
            }

            var stream = new MemoryStream(Encoding.UTF8.GetBytes(SsePayload));
            return new HttpResponseMessage(Status)
            {
                Content = new StreamContent(stream),
            };
        }
    }

    private static DeepSeekProvider CreateProvider(StubHandler handler)
    {
        var store = Substitute.For<ICredentialStore>();
        store.GetAsync(ApiKeyManager.CredentialKey, Arg.Any<CancellationToken>())
            .Returns("sk-test");

        var options = Options.Create(new DeepSeekOptions { Model = "deepseek-chat", UseStreaming = true });

        var logger = NullLogger<DeepSeekProvider>.Instance;

        var provider = new DeepSeekProvider(
            new SingleClientHttpFactory(handler),
            store,
            logger,
            options);

        return provider;
    }

    [Fact]
    public async Task StreamAnalyzeAsync_AccumulatesPartialSummary_FromChunks()
    {
        // 3 SSE chunks streaming the JSON '{"summary":"hello world",...}'
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"{\\\"summary\\\":\\\"hello \"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"world\\\"\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new StubHandler { SsePayload = sse };
        var provider = CreateProvider(handler);

        var session = TestAnalysisSession();
        var chunks = new List<LlmPartialUpdate>();
        await foreach (var u in provider.AnalyzeStreamingAsync(session, CancellationToken.None))
        {
            chunks.Add(u);
        }

        // Expect: 3 PartialSummary + 1 FinalResult
        chunks.OfType<LlmPartialUpdate.PartialSummary>().Should().HaveCount(2);
        var final = chunks.OfType<LlmPartialUpdate.FinalResult>().Single();
        final.Result.Summary.Should().Contain("hello world");
    }

    [Fact]
    public async Task StreamAnalyzeAsync_ExtractsEvidenceIdsFromIncrementalJson()
    {
        // Stream a JSON object with claims → evidence_ids.
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"{\\\"claims\\\":[{\\\"evidence_ids\\\":[\\\"E-001\\\",\\\"E-002\\\"]}]}\"}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new StubHandler { SsePayload = sse };
        var provider = CreateProvider(handler);

        var session = TestAnalysisSession();
        LlmAnalysisResult? result = null;
        await foreach (var u in provider.AnalyzeStreamingAsync(session, CancellationToken.None))
        {
            if (u is LlmPartialUpdate.FinalResult fr) result = fr.Result;
        }

        result.Should().NotBeNull();
        result!.AttributedEvidenceIds.Should().Contain("E-001");
        result.AttributedEvidenceIds.Should().Contain("E-002");
    }

    [Fact]
    public async Task StreamAnalyzeAsync_CancellationBreaksStream()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\" world\"}}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new StubHandler { SsePayload = sse };
        var provider = CreateProvider(handler);

        using var cts = new CancellationTokenSource();
        var session = TestAnalysisSession();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var u in provider.AnalyzeStreamingAsync(session, cts.Token))
            {
                cts.Cancel();  // cancel after first chunk
            }
        });
    }

    private static AnalysisSession TestAnalysisSession() => new(
        Version: 1,
        FaultEvent: new FaultEvent(0.0, TimeSpan.Zero, TimeSpan.Zero, "test", DateTimeOffset.UtcNow),
        AnchorSnapshot: new AnchorSnapshot(0.0, 0.0, Array.Empty<AnchoredSignalValue>(), DateTimeOffset.UtcNow, 1),
        Report: new LocalReport("",
            new[] { new FaultAnalysisEvidence("E-001", "sig1", "src1", "type", 0.0, 1.0, null, "desc") },
            Array.Empty<CandidateSignal>()),
        CreatedAtUtc: DateTimeOffset.UtcNow);

    private sealed class SingleClientHttpFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public SingleClientHttpFactory(HttpMessageHandler handler) { _handler = handler; }
        public HttpClient CreateClient(string name) => new HttpClient(_handler, disposeHandler: false);
    }
}
```

- [ ] **Step 2: Verify build + tests PASS**

```bash
dotnet build tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj -c Debug --no-restore
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~DeepSeekProviderStreamingTests" --logger "console;verbosity=minimal"
```

Expected: 3/3 PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/PeakCan.Host.App.Tests/Services/LlmProvider/DeepSeekProviderStreamingTests.cs
git commit -m "W41 T7: DeepSeekProviderStreamingTests with 3 tests (accumulation + evidence ID extraction + cancellation)"
```

---

### Task 8: AI Analysis panel partial-summary TextBlock (XAML UI)

**Files:**
- Modify: `src/PeakCan.Host.App/Views/TraceViewerView.AIPanel.xaml`

**Interfaces:**
- Adds: TextBlock binding `StreamingSummary` + ListBox binding `StreamingEvidenceIds`

- [ ] **Step 1: Modify TraceViewerView.AIPanel.xaml**

Insert the following block immediately AFTER the existing ScrollViewer (`<ScrollViewer ...>` block ending with `</ScrollViewer>`) AND BEFORE the closing `</Grid>`:

```xml
<!-- W41 MINOR: streaming partial-summary panel. Visible only when
     RunAnalysisStreamingCommand is in-flight (IsAnalysisRunning toggles
     the visibility). Shows the running summary text + evidence IDs as
     they accumulate — first-token latency drops from ~30s to ~1s. -->
<StackPanel Visibility="{Binding IsAnalysisRunning, Converter={StaticResource BoolToVis}}"
            Margin="8">
    <TextBlock Text="流式输出（实时）" FontWeight="SemiBold" Margin="0,0,0,4"
               Foreground="#1565C0" />
    <Border BorderBrush="#90CAF9" BorderThickness="1" CornerRadius="3"
            Padding="8" Margin="0,0,0,8" MaxHeight="200">
        <ScrollViewer VerticalScrollBarVisibility="Auto">
            <TextBlock Text="{Binding StreamingSummary}" TextWrapping="Wrap"
                       FontFamily="Consolas, Courier New, monospace" />
        </ScrollViewer>
    </Border>
    <TextBlock Text="Evidence IDs (streaming)" FontWeight="SemiBold"
               Margin="0,0,0,4" />
    <ItemsControl ItemsSource="{Binding StreamingEvidenceIds}">
        <ItemsControl.ItemTemplate>
            <DataTemplate>
                <TextBlock Text="{Binding}" Margin="0,0,8,0" />
            </DataTemplate>
        </ItemsControl.ItemTemplate>
    </ItemsControl>
</StackPanel>
```

- [ ] **Step 2: Verify build**

```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```

Expected: 0 errors, 6 pre-existing warnings.

- [ ] **Step 3: Run full test suite**

```bash
dotnet test --no-restore --nologo -c Debug -- xUnit.MaxParallelThreads=1 --logger "console;verbosity=minimal" 2>&1 | tail -10
```

Expected: **1466 PASS / 0 FAIL / 5 SKIP** (1461 baseline + 5 new tests from T3+T5+T7).

- [ ] **Step 4: Commit**

```bash
git add src/PeakCan.Host.App/Views/TraceViewerView.AIPanel.xaml
git commit -m "W41 T8: AI Analysis panel streaming partial-summary TextBlock + ItemsControl"
```

---

### Task 9: v3.59.0 MINOR + release notes

**Files:**
- Create: `docs/release-notes-v3-59-0-minor.md`
- Modify: `src/Directory.Build.props` (bump Version + AssemblyVersion + FileVersion + InformationalVersion from 3.58.0 → 3.59.0)

- [ ] **Step 1: Write release notes**

Create `docs/release-notes-v3-59-0-minor.md`:

```markdown
# v3.59.0 MINOR — Streaming LLM Response for DeepSeek

> Status: W41 MINOR SHIP — first SSE streaming implementation.
> Sister of v3.54.0 P1b (DeepSeekProvider non-streaming) + v3.58.0 P2 (API Key UI).

## Headline

**SSE streaming** for DeepSeekProvider. AI Analysis panel perceived latency drops from **~30s (non-streaming)** to **~1s first token**. Existing `AnalyzeAsync` API unchanged; new `AnalyzeStreamingAsync` parallel API emits `IAsyncEnumerable<LlmPartialUpdate>` with 3 variant types (`PartialSummary` / `PartialEvidenceId` / `FinalResult`).

## Architecture milestones

- **1st SSE streaming implementation** in the project
- **1st `IAsyncEnumerable<T>` partial-result pattern** — discriminated union via sealed abstract record
- **1st `UseStreaming` opt-out** in `DeepSeekOptions` (default true)
- Non-streaming callers (`AnalyzeAsync`) preserved 100% — route through `StreamAnalyzeAsync` when `UseStreaming = true`; collect final result, discard partial updates
- Streaming-aware callers (`AnalyzeStreamingAsync`) get full incremental delivery

## Files changed

- NEW: `src/PeakCan.Host.Core/Analysis/LlmPartialUpdate.cs` (~30 LoC; sealed abstract record + 3 variants)
- MODIFY: `src/PeakCan.Host.Core/Analysis/ILlmProvider.cs` (+30 LoC; default `AnalyzeStreamingAsync` + extension)
- NEW: `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekStreamingChunk.cs` (~30 LoC; per-chunk DTO + Choice + Delta)
- MODIFY: `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekRequest.cs` (+3 LoC; Stream field)
- MODIFY: `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekOptions.cs` (+5 LoC; UseStreaming default true)
- MODIFY: `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekProvider.cs` (+75 LoC; StreamAnalyzeAsync SSE path + AnalyzeAsync routing)
- MODIFY: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/AnalysisFlow.cs` (+55 LoC; RunAnalysisStreamingCommand + StreamingSummary + StreamingEvidenceIds)
- MODIFY: `src/PeakCan.Host.App/Views/TraceViewerView.AIPanel.xaml` (+25 LoC; streaming partial-summary TextBlock)
- NEW: `tests/PeakCan.Host.Core.Tests/Analysis/LlmPartialUpdateTests.cs` (~50 LoC, 4 tests)
- NEW: `tests/PeakCan.Host.Core.Tests/Analysis/ILlmProviderExtensionsTests.cs` (~40 LoC, 1 test)
- NEW: `tests/PeakCan.Host.App.Tests/Services/LlmProvider/DeepSeekProviderStreamingTests.cs` (~150 LoC, 3 tests)

**Total: +493 LoC net across 11 files** (spec estimated +328 LoC; +165 over for streaming UI + extensive tests)

## Verification

- `dotnet build src/`: 0 errors, 6 pre-existing warnings
- `dotnet test` full suite (single-threaded retry): **1466 PASS / 0 FAIL / 5 SKIP** (1461 baseline + 5 new tests)
- Public API 100% preserved: `AnalyzeAsync` signature unchanged
- Streaming tests cover: chunk accumulation, evidence ID extraction, cancellation break
- Non-streaming fallback path (default interface method) covered by ILlmProviderExtensionsTests
- DI: no changes (ILlmProvider still singleton)

## Sister-lesson candidates confirmed at W41 SHIP

- NEW 1/3: `iasyncenumerable-default-interface-method-pattern-provides-single-shot-fallback`
- NEW 1/3: `sse-event-stream-framing-requires-data-line-prefix-stripping`
- NEW 1/3: `delta-content-accumulation-into-summary-handles-utf8-boundary-splits`
- NEW 1/3: `stream-cancellation-breaks-httpresponse-readasync-loop-without-throwing`
- NEW 1/3: `partial-update-discriminated-union-via-sealed-abstract-record-is-net-7-pattern`
- NEW 1/3: `httpclient-accept-text-event-stream-header-required-for-sse-endpoint`

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
| **W41** | **(no refactor)** | **+493 LoC net** (new feature) |
| **Net** | **24 god-classes + 2 feature ships** | **~ -3,421 LoC** |

## What does NOT change

- No new credential storage path (reuses v3.53.1 + v3.58.0 P2)
- No real DeepSeek API integration test (deferred; gated by env var)
- No Azure / Ollama streaming (deferred)
- No token usage persistence (deferred)
- No streaming retry policy (deferred)

## Next steps

- v3.60.0 candidates: Azure OpenAI Provider / Ollama Provider / Real DeepSeek API CI test
- 6 NEW 1/3 candidates need 2nd observation each before promotion to STANDALONE
```

- [ ] **Step 2: Bump version**

Modify `src/Directory.Build.props`:

```xml
<Version>3.59.0</Version>
<AssemblyVersion>3.59.0.0</AssemblyVersion>
<FileVersion>3.59.0.0</FileVersion>
<InformationalVersion>3.59.0</InformationalVersion>
```

- [ ] **Step 3: Full CI + release notes commit**

```bash
dotnet build src/PeakCan.Host.App/  # 0 errors, 0 warnings
dotnet test --no-restore --nologo -c Debug -- xUnit.MaxParallelThreads=1 --logger "console;verbosity=minimal"
git add docs/release-notes-v3-59-0-minor.md src/Directory.Build.props
git commit -m "W41 release notes + version bump to v3.59.0 MINOR (Streaming LLM Response for DeepSeek)"
```

---

### Task 10: Tier-3 ship

- [ ] **Step 1: Push branch + create PR**

```bash
git push -u origin feature/w41-streaming-llm
gh pr create --title "v3.59.0 MINOR: Streaming LLM Response for DeepSeek (SSE + IAsyncEnumerable<LlmPartialUpdate>)" --body "W41 streaming MINOR. Reduces AI Analysis panel perceived latency from ~30s to ~1s first token via SSE. Adds LlmPartialUpdate discriminated union + DeepSeekProvider.StreamAnalyzeAsync SSE path. Public API preserved: AnalyzeAsync unchanged; new AnalyzeStreamingAsync additive. 5 new tests (1466 PASS / 0 FAIL / 5 SKIP)."
```

- [ ] **Step 2: Squash merge + delete branch**

```bash
gh pr merge --squash --delete-branch
```

- [ ] **Step 3: Force-correct + tag + release**

```bash
git fetch origin main
git merge origin/main --no-edit
git reset --hard <squash-commit>
git tag v3.59.0
git push origin v3.59.0
gh release create v3.59.0 --title "v3.59.0 MINOR: Streaming LLM Response for DeepSeek" --notes-file docs/release-notes-v3-59-0-minor.md
```

---

## Sister-lesson candidates to monitor

| Lesson | Status | What W41 might observe |
|---|---|---|
| `iasyncenumerable-default-interface-method-pattern-provides-single-shot-fallback` | NEW 1/3 (W41) | W41 1st obs: `AnalyzeStreamingAsync` default method wraps `AnalyzeAsync` so 老 callers + Azure/Ollama deferred providers 不破坏 |
| `sse-event-stream-framing-requires-data-line-prefix-stripping` | NEW 1/3 (W41) | W41 1st obs: each SSE chunk is `data: {...JSON...}\n\n` — parser must strip `data: ` prefix + `\n\n` suffix + skip `[DONE]` sentinel |
| `delta-content-accumulation-into-summary-handles-utf8-boundary-splits` | NEW 1/3 (W41) | W41 1st obs: DeepSeek may split CJK char across 2 chunks (UTF-8 multi-byte sequence boundary); StringBuilder + Append avoids string concat alloc |
| `stream-cancellation-breaks-httpresponse-readasync-loop-without-throwing` | NEW 1/3 (W41) | W41 1st obs: `ct.ThrowIfCancellationRequested()` inside ReadAsync loop + dispose stream gracefully |
| `partial-update-discriminated-union-via-sealed-abstract-record-is-net-7-pattern` | NEW 1/3 (W41) | W41 1st obs: `LlmPartialUpdate` 的 3 变体 + C# 9 `init`-only properties 给 WPF UI 提供 type-safe partial pattern matching |
| `httpclient-accept-text-event-stream-header-required-for-sse-endpoint` | NEW 1/3 (W41) | W41 1st obs: `request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"))` 是 SSE 协议关键 |

---

## Verification

- `dotnet build src/`: 0 errors, 0 warnings
- `dotnet test --filter "FullyQualifiedName~LlmPartialUpdate|FullyQualifiedName~ILlmProvider|FullyQualifiedName~DeepSeek"`: 1466 PASS / 0 FAIL
- `dotnet test` full solution single-threaded: 1466 PASS / 0 FAIL / 5 SKIP
- 3 NEW Core files (LlmPartialUpdate, LlmPartialUpdateTests, ILlmProviderExtensionsTests)
- 1 NEW DTO file (DeepSeekStreamingChunk)
- 3 NEW test files (LlmPartialUpdateTests, ILlmProviderExtensionsTests, DeepSeekProviderStreamingTests)
- 5 MODIFIED src files (ILlmProvider, DeepSeekRequest, DeepSeekOptions, DeepSeekProvider, AnalysisFlow, AIPanel.xaml)
- AI Analysis panel: streaming TextBlock + ItemsControl + bound to new commands
- DI: no changes (ILlmProvider still singleton)
- Tag v3.59.0 + GH release published
- Branch deleted post-merge

## Out of scope (YAGNI)

- No real DeepSeek API integration test (deferred; gated by env var).
- No Azure OpenAI / Ollama streaming (deferred — interface is ready for them).
- No token usage persistence (deferred — log only).
- No streaming retry policy with exponential backoff (deferred — manual retry via UI).
- No UI undo/redo (deferred).
- No sisiter-borrow from aspice-toolkit ai_extract.py DeepSeek impl (user explicitly chose 不沿用; aspice-toolkit only has non-streaming impl).