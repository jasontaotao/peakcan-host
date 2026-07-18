using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
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

        // v3.61.0 PATCH BUG-001: check UseStreaming BEFORE sending HTTP request.
        // Previously the non-streaming POST was always sent first, then
        // StreamAnalyzeAsync was called which sent a SECOND POST with stream=true,
        // wasting 2x DeepSeek tokens on every streaming invocation.
        if (_options.UseStreaming)
        {
            try
            {
                LlmPartialUpdate? lastFinal = null;
                await foreach (var update in StreamAnalyzeAsync(session, ct).ConfigureAwait(false))
                {
                    if (update is LlmPartialUpdate.FinalResult fr) lastFinal = fr;
                }
                if (lastFinal is null) return ErrorResult("DeepSeek streaming produced no FinalResult");
                return ((LlmPartialUpdate.FinalResult)lastFinal).Result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return ErrorResult("DeepSeek request cancelled by user");
            }
            catch (OperationCanceledException)
            {
                return ErrorResult($"DeepSeek request timed out after {_options.TimeoutSeconds}s");
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse DeepSeek streaming response as JSON");
                return ErrorResult("DeepSeek returned malformed JSON");
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "HTTP error calling DeepSeek streaming");
                return ErrorResult($"DeepSeek HTTP error: {ex.Message}");
            }
        }

        // 3. POST (non-streaming path only)
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
            var deepSeekResponse = JsonSerializer.Deserialize<DeepSeekResponse>(rawJson, _caseInsensitiveJson);

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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ErrorResult("DeepSeek request cancelled by user");
        }
        catch (OperationCanceledException)
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

    /// <summary>
    /// v3.59.0 MINOR: streaming SSE consumer. Reads <c>text/event-stream</c>
    /// chunk-by-chunk and emits:
    /// <list type="bullet">
    ///   <item><see cref="LlmPartialUpdate.PartialSummary"/> per non-empty <c>delta.content</c> fragment;</item>
    ///   <item><see cref="LlmPartialUpdate.FinalResult"/> once after EOF with
    ///         the whitelist-filtered <see cref="LlmAnalysisResult"/>.</item>
    /// </list>
    /// Errors (401/429/HTTP) surface as a single <see cref="LlmPartialUpdate.FinalResult"/>
    /// carrying the <see cref="LlmAnalysisResult.Error"/> envelope; <c>JsonException</c>
    /// per chunk is logged and skipped (resilient to single bad chunk).
    /// </summary>
    public async IAsyncEnumerable<LlmPartialUpdate> StreamAnalyzeAsync(
        AnalysisSession session,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);

        // 1. Read API key (same as AnalyzeAsync — never log value)
        LlmAnalysisResult? apiKeyError = null;
        string? apiKey = null;
        try
        {
            apiKey = await _credentialStore.GetAsync(ApiKeyCredentialKey, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read API key from credential store");
            apiKeyError = ErrorResult("Failed to read API key from credential store");
        }

        if (apiKeyError is not null)
        {
            yield return new LlmPartialUpdate.FinalResult(apiKeyError);
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

        // 4. Read SSE stream — with read-level timeout guard
        // v3.61.0 PATCH BUG-002: add silent-read timeout so the application
        // does not hang indefinitely if DeepSeek stalls mid-stream. The
        // linked token fires after _options.TimeoutSeconds of inactivity
        // on ReadLineAsync, independent of the HttpClient-level timeout.
        var summaryBuilder = new StringBuilder();
        var rawJsonBuilder = new StringBuilder();
        var chunkCount = 0;

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // CA2024: don't use reader.EndOfStream in async path. ReadLineAsync
        // returns null at EOF, so loop on ReadLineAsync result directly.
        string? line;
        while ((line = await ReadLineWithTimeoutAsync(reader, ct).ConfigureAwait(false)) is not null)
        {
            ct.ThrowIfCancellationRequested();

            if (line.Length == 0) continue;            // SSE event delimiter
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var payload = line.Substring("data: ".Length);
            if (payload == "[DONE]") break;

            rawJsonBuilder.AppendLine(payload);
            chunkCount++;

            DeepSeekStreamingChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<DeepSeekStreamingChunk>(payload, _caseInsensitiveJson);
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

    /// <summary>
    /// v3.61.0 PATCH BUG-002: read a line from the SSE stream with a
    /// silent-read timeout. Uses a linked CancellationTokenSource so that
    /// ReadLineAsync is cancelled after <see cref="_options"/>.<see cref="DeepSeekOptions.TimeoutSeconds"/>
    /// of inactivity. Prevents indefinite hang when DeepSeek stalls mid-stream.
    /// </summary>
    private async Task<string?> ReadLineWithTimeoutAsync(
        StreamReader reader, CancellationToken ct)
    {
        // Use the configured timeout as the silence threshold.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        try
        {
            return await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout (not user cancellation) — rethrow as a new one the
            // caller can distinguish via the message.
            throw new OperationCanceledException($"SSE stream read timed out (no data for {_options.TimeoutSeconds}s)");
        }
    }

    /// <summary>
    /// v3.61.0 PATCH OPT-008: serialized evidence payload for the LLM
    /// prompt. Uses System.Text.Json instead of manual StringBuilder +
    /// incomplete Escape() to guarantee valid JSON.
    /// </summary>
    private sealed record EvidenceEntry(string Id, string Type, string Description);

    /// <summary>
    /// v3.61.0 PATCH OPT-008: top-level payload structure for
    /// SerializeSessionForLLM. Kept minimal — only evidence list +
    /// candidate count — to stay within the DeepSeek token budget.
    /// </summary>
    private sealed record SessionPayload(EvidenceEntry[] Evidence, int CandidatesCount);

    private static readonly JsonSerializerOptions _indentedJson = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions _caseInsensitiveJson = new() { PropertyNameCaseInsensitive = true };

    private static string SerializeSessionForLLM(AnalysisSession session)
    {
        var payload = new SessionPayload(
            Evidence: session.Report.Evidence
                .Select(e => new EvidenceEntry(e.EvidenceId, e.Type, e.Description))
                .ToArray(),
            CandidatesCount: session.Report.Candidates.Count);
        return JsonSerializer.Serialize(payload, _indentedJson);
    }

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