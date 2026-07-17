using System.Net;
using System.Net.Http;
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