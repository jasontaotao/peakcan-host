using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PeakCan.Host.Core.Analysis;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Analysis;

namespace PeakCan.Host.Infrastructure.HIL.Analysis;

/// <summary>
/// Sprint 14: Calls DeepSeek API to analyze test failures.
/// Phase 7 Unit A: endpoint and model read from IOptions&lt;DeepSeekOptions&gt;
/// (injected via AddHttpClient&lt;&gt;); HTTP timeout from TimeoutSeconds × 5.
/// stream=false (non-streaming); UseStreaming does not affect this service.
/// </summary>
public sealed class HilAnalysisService : IHilAnalysisService, IDisposable
{
    private readonly ICredentialStore _credentialStore;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly DeepSeekOptions _options;

    public HilAnalysisService(HttpClient httpClient, ICredentialStore credentialStore, IOptions<DeepSeekOptions> options)
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
        var apiKey = await _credentialStore.GetAsync("deepseek-api-key", ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(apiKey))
            return AnalysisResult.Unavailable("API key not configured");

        var prompt = HilPromptBuilder.Build(result);
        var requestBody = new
        {
            model = _options.Model,
            messages = new[]
            {
                new { role = "system", content = "You are an automotive ECU diagnostic test failure analyst. Analyze the test failure and suggest root causes." },
                new { role = "user", content = prompt }
            },
            stream = false,
            temperature = 0.3
        };

        var json = JsonSerializer.Serialize(requestBody);
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_options.ApiBase.TrimEnd('/')}/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var response = await _httpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return AnalysisResult.Unavailable($"HTTP error: {(int)response.StatusCode}");
            }

            var responseJson = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;
            var content = root.GetProperty("choices")[0]
                              .GetProperty("message")
                              .GetProperty("content")
                              .GetString();
            return AnalysisResult.Success(content ?? "No analysis content");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AnalysisResult.Unavailable($"Request failed: {ex.Message}");
        }
    }
}
