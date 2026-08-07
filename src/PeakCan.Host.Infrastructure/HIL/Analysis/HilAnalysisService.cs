using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PeakCan.HIL.Core.Analysis;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Analysis;

namespace PeakCan.Host.Infrastructure.HIL.Analysis;

/// <summary>
/// Sprint 14: Calls LLM API to analyze test failures.
/// Phase 1 重构: 不再直接调 HTTP, 改为依赖 <see cref="ILlmClient"/>。
/// 底层 HTTP 由 hil-core <c>OpenAiCompatibleClient</c> 承担。
/// </summary>
public sealed class HilAnalysisService : IHilAnalysisService, IDisposable
{
    private const string ApiKeyCredentialKey = "PeakCan/deepseek/default";

    private readonly ICredentialStore _credentialStore;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly LlmOptions _options;

    public HilAnalysisService(HttpClient httpClient, ICredentialStore credentialStore, IOptions<LlmOptions> options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(credentialStore);
        _credentialStore = credentialStore;
        _httpClient = httpClient;
        _ownsHttpClient = false;
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "peakcan-host/hil-analyze");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    public async Task<AnalysisResult?> AnalyzeAsync(TestSuiteResult result, CancellationToken ct = default)
    {
        var apiKey = await _credentialStore.GetAsync(ApiKeyCredentialKey, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(apiKey))
            return AnalysisResult.Unavailable("API key not configured");

        var prompt = HilPromptBuilder.Build(result);
        var messages = new List<LlmMessage>
        {
            new("system", "You are an automotive ECU diagnostic test failure analyst. Analyze the test failure and suggest root causes."),
            new("user", prompt),
        };

        // per-call options: non-streaming, slightly higher temperature for analysis
        var callOptions = _options with { Temperature = 0.3, ResponseFormat = null };

        var client = new OpenAiCompatibleClient(_httpClient, callOptions, apiKey);

        try
        {
            var response = await client.CompleteAsync(messages, callOptions, ct).ConfigureAwait(false);
            return AnalysisResult.Success(response.Content ?? "No analysis content");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AnalysisResult.Unavailable($"Request failed: {ex.Message}");
        }
    }
}
