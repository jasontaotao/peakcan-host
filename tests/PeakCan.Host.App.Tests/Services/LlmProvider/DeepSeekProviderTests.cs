using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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
    private static readonly string[] E0001Single = { "E-0001" };

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
        HttpMessageHandler handler,
        ICredentialStore? credStore = null,
        DeepSeekOptions? options = null)
    {
        var resolvedOptions = options ?? DefaultOptions();
        var services = new ServiceCollection();
        services.AddHttpClient("DeepSeek")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(resolvedOptions.TimeoutSeconds));
        var sp = services.BuildServiceProvider();
        var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
        return new DeepSeekProvider(
            httpFactory,
            credStore ?? Substitute.For<ICredentialStore>(),
            NullLogger<DeepSeekProvider>.Instance,
            Options.Create(resolvedOptions));
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
                            claims = new[] { new { evidence_ids = E0001Single, text = "valid claim" } }
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
        result.AttributedEvidenceIds.Should().BeEquivalentTo(E0001Single);
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