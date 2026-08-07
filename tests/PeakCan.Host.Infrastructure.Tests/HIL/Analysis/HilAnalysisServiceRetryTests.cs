using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using PeakCan.HIL.Core.Analysis;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Analysis;
using PeakCan.Host.Infrastructure.HIL.Analysis;
using Polly;
using Polly.Extensions.Http;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Analysis;

/// <summary>
/// Sprint 19 Inc 8: Polly retry for HilAnalysisService HTTP calls.
/// Retry policy is applied to the HttpClient handler (PolicyHttpMessageHandler),
/// mirroring the AddHttpClient wiring in HeadlessHostBuilder/AppServicesFlow.
/// </summary>
public class HilAnalysisServiceRetryTests
{
    private sealed class SequenceMockHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public int InvocationCount;

        public SequenceMockHandler(params HttpStatusCode[] statuses)
        {
            foreach (var s in statuses)
            {
                _responses.Enqueue(new HttpResponseMessage(s)
                {
                    Content = new StringContent(s == HttpStatusCode.OK
                        ? JsonSerializer.Serialize(new
                        {
                            choices = new[] { new { message = new { content = "Retry analysis result" } } }
                        })
                        : "{}")
                });
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            InvocationCount++;
            ct.ThrowIfCancellationRequested();
            var response = _responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.InternalServerError);
            return Task.FromResult(response);
        }
    }

    private static (HilAnalysisService Service, SequenceMockHandler Handler, SimpleCredentialStore Store)
        CreateService(HttpStatusCode[] statuses)
    {
        var handler = new SequenceMockHandler(statuses);
        var retryPolicy = HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(r => (int)r.StatusCode == 429)
            .WaitAndRetryAsync(3, _ => TimeSpan.Zero);
        var policyHandler = new PolicyHttpMessageHandler(retryPolicy)
        {
            InnerHandler = handler
        };
        var httpClient = new HttpClient(policyHandler);
        var store = new SimpleCredentialStore();
        store.SetAsync("PeakCan/deepseek/default", "test-key").GetAwaiter().GetResult();
        return (new HilAnalysisService(httpClient, store, Options.Create(new LlmOptions { ApiBase = "https://api.deepseek.com", Model = "deepseek-v4-flash" })), handler, store);
    }

    private static TestSuiteResult CreateFailedResult()
        => new TestSuiteResult("Suite", 1, 0, 1, 0, 100, Array.Empty<string>(),
            new[] { new TestCaseResult("c", "Fail", false, "boom", 10, 1, 0, 1, 0, 0, Array.Empty<StepResult>()) });

    [Fact]
    public async Task AnalyzeService_RetryOn500_EventuallySucceeds()
    {
        var (service, handler, _) = CreateService(new[] { HttpStatusCode.InternalServerError, HttpStatusCode.OK });

        var result = await service.AnalyzeAsync(CreateFailedResult());

        Assert.NotNull(result);
        Assert.False(result.IsUnavailable);
        Assert.Contains("Retry analysis result", result.Content);
        Assert.Equal(2, handler.InvocationCount); // 1 initial + 1 retry
    }

    [Fact]
    public async Task AnalyzeService_Retry3Times_ThenFails()
    {
        var (service, handler, _) = CreateService(new[]
        {
            HttpStatusCode.InternalServerError,
            HttpStatusCode.InternalServerError,
            HttpStatusCode.InternalServerError,
            HttpStatusCode.InternalServerError, // 4th attempt (3 retries) also fails
        });

        var result = await service.AnalyzeAsync(CreateFailedResult());

        Assert.NotNull(result);
        Assert.True(result.IsUnavailable);
        Assert.Equal(4, handler.InvocationCount); // 1 initial + 3 retries
    }

    [Fact]
    public async Task AnalyzeService_OperationCancelled_DoesNotRetry()
    {
        var (service, handler, _) = CreateService(new[] { HttpStatusCode.InternalServerError });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Handler throws OperationCanceledException on first SendAsync (ct cancelled).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.AnalyzeAsync(CreateFailedResult(), cts.Token));

        Assert.Equal(0, handler.InvocationCount); // policy short-circuits before inner handler; no retry
    }

    [Fact]
    public async Task AnalyzeService_Success_NoRetry()
    {
        var (service, handler, _) = CreateService(new[] { HttpStatusCode.OK });

        var result = await service.AnalyzeAsync(CreateFailedResult());

        Assert.NotNull(result);
        Assert.False(result.IsUnavailable);
        Assert.Equal(1, handler.InvocationCount); // single call, no retry
    }

    // --- Phase 7 Unit A: endpoint/model/TrimEnd 用例 ---

    /// <summary>
    /// Captures the outgoing HttpRequestMessage for assertion.
    /// </summary>
    private sealed class CapturingMockHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        public string? LastRequestBody;
        private readonly HttpResponseMessage _response;

        public CapturingMockHandler(HttpResponseMessage response) => _response = response;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(ct);
            return _response;
        }
    }

    private static HttpResponseMessage OkResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = "analysis" } } }
        }))
    };

    [Fact]
    public async Task AnalyzeService_UsesOptionsModelAndEndpoint()
    {
        var handler = new CapturingMockHandler(OkResponse());
        var httpClient = new HttpClient(handler);
        var store = new SimpleCredentialStore();
        await store.SetAsync("PeakCan/deepseek/default", "test-key");
        var options = Options.Create(new LlmOptions
        {
            ApiBase = "https://custom.api.com",
            Model = "custom-model"
        });
        var service = new HilAnalysisService(httpClient, store, options);

        await service.AnalyzeAsync(CreateFailedResult());

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("https://custom.api.com/chat/completions", handler.LastRequest!.RequestUri!.ToString());
        Assert.Contains("\"model\":\"custom-model\"", handler.LastRequestBody);
        // L3/E1 invariants: HIL analysis is always non-streaming (UseStreaming is
        // inert for this service) and never uses json_object response_format.
        Assert.Contains("\"stream\":false", handler.LastRequestBody);
        Assert.DoesNotContain("response_format", handler.LastRequestBody);
    }

    [Fact]
    public async Task AnalyzeService_DefaultOptions_UsesDeepSeekDefaults()
    {
        var handler = new CapturingMockHandler(OkResponse());
        var httpClient = new HttpClient(handler);
        var store = new SimpleCredentialStore();
        await store.SetAsync("PeakCan/deepseek/default", "test-key");
        var service = new HilAnalysisService(httpClient, store, Options.Create(new LlmOptions { ApiBase = "https://api.deepseek.com", Model = "deepseek-v4-flash" }));

        await service.AnalyzeAsync(CreateFailedResult());

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("https://api.deepseek.com/chat/completions", handler.LastRequest!.RequestUri!.ToString());
        Assert.Contains("\"model\":\"deepseek-v4-flash\"", handler.LastRequestBody);
    }

    [Fact]
    public async Task AnalyzeService_ApiBaseTrailingSlash_NoDoubleSlash()
    {
        var handler = new CapturingMockHandler(OkResponse());
        var httpClient = new HttpClient(handler);
        var store = new SimpleCredentialStore();
        await store.SetAsync("PeakCan/deepseek/default", "test-key");
        var options = Options.Create(new LlmOptions
        {
            ApiBase = "https://api.deepseek.com/"
        });
        var service = new HilAnalysisService(httpClient, store, options);

        await service.AnalyzeAsync(CreateFailedResult());

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("https://api.deepseek.com/chat/completions", handler.LastRequest!.RequestUri!.ToString());
    }
}
