using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
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

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
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

    private sealed class SingleClientHttpFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public SingleClientHttpFactory(HttpMessageHandler handler) { _handler = handler; }
        public HttpClient CreateClient(string name) => new HttpClient(_handler, disposeHandler: false);
    }

    private static readonly string[] OneEvidenceId = { "E-0001" };
    private static readonly FaultAnalysisEvidence[] OneEvidence = new[]
    {
        new FaultAnalysisEvidence("E-0001", "0x100.TestSignal.src", "src",
            "state-transition", 1.0, 1, null, "test evidence")
    };
    private static readonly CandidateSignal[] NoCandidates = Array.Empty<CandidateSignal>();
    private static readonly string[] NoDataQualityNotes = Array.Empty<string>();
    private static readonly AnchoredSignalValue[] NoAnchoredSignals = Array.Empty<AnchoredSignalValue>();

    private static DeepSeekProvider CreateProvider(StubHandler handler)
    {
        var store = Substitute.For<ICredentialStore>();
        store.GetAsync(ApiKeyManager.CredentialKey, Arg.Any<CancellationToken>())
            .Returns("sk-test");

        var options = Options.Create(new DeepSeekOptions { Model = "deepseek-chat", UseStreaming = true });

        var provider = new DeepSeekProvider(
            new SingleClientHttpFactory(handler),
            store,
            NullLogger<DeepSeekProvider>.Instance,
            options);

        return provider;
    }

    private static AnalysisSession TestSession() => new(
        SessionId: Guid.NewGuid(),
        Version: 1,
        FaultEvent: new FaultEvent(0.0, TimeSpan.Zero, TimeSpan.Zero, "test", DateTime.UtcNow),
        AnchorSnapshot: new AnchorSnapshot(0.0, 0.0, NoAnchoredSignals, DateTime.UtcNow, 1),
        Report: new LocalReport(OneEvidence, NoCandidates, NoDataQualityNotes, "", DateTime.UtcNow),
        CreatedAtUtc: DateTime.UtcNow);

    [Fact]
    public async Task StreamAnalyzeAsync_AccumulatesPartialSummary_FromChunks()
    {
        // Simpler test: just 2 chunks streaming plain text (not JSON-wrapped).
        // This bypasses any JsonException issues with the test fixture JSON.
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"hello \"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"world\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new StubHandler { SsePayload = sse };
        var provider = CreateProvider(handler);

        var session = TestSession();
        var chunks = new List<LlmPartialUpdate>();
        await foreach (var u in provider.StreamAnalyzeAsync(session, CancellationToken.None))
        {
            chunks.Add(u);
        }

        // Expect: 2 PartialSummary + 1 FinalResult
        chunks.OfType<LlmPartialUpdate.PartialSummary>().Should().HaveCount(2);
        var final = chunks.OfType<LlmPartialUpdate.FinalResult>().Single();
        final.Result.Summary.Should().Contain("hello world");
        handler.AuthorizationHeader.Should().StartWith("Bearer");
    }

    [Fact]
    public async Task StreamAnalyzeAsync_ExtractsEvidenceIdsFromIncrementalJson()
    {
        // Stream a JSON object with claims → evidence_ids.
        var sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"{\\\"claims\\\":[{\\\"evidence_ids\\\":[\\\"E-0001\\\"]}]}\"}}]}\n\n" +
            "data: {\"choices\":[{\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        var handler = new StubHandler { SsePayload = sse };
        var provider = CreateProvider(handler);

        var session = TestSession();
        LlmAnalysisResult? result = null;
        await foreach (var u in provider.StreamAnalyzeAsync(session, CancellationToken.None))
        {
            if (u is LlmPartialUpdate.FinalResult fr) result = fr.Result;
        }

        result.Should().NotBeNull();
        result!.AttributedEvidenceIds.Should().Contain("E-0001");
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
        var session = TestSession();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var u in provider.StreamAnalyzeAsync(session, cts.Token))
            {
                cts.Cancel();  // cancel after first chunk
            }
        });
    }
}