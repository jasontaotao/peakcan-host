using System.Net;
using System.Net.Http;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PeakCan.Host.App.Services.ChatProvider;
using PeakCan.Host.Core.Analysis;
using PeakCan.Host.Core.Analysis.Chat;

namespace PeakCan.Host.App.Tests.Services.ChatProvider;

public class DeepSeekChatProviderTests
{
    private static readonly IReadOnlyList<ChatMessage> EmptyMessages =
        new[] { new ChatMessage("user", "hi", null, null) };
    private static readonly IReadOnlyList<ChatToolDefinition> EmptyTools =
        Array.Empty<ChatToolDefinition>();

    private static DeepSeekOptions BuildOptions() => new()
    {
        ApiBase = "https://test.example",
        Model = "deepseek-v4-flash",
        TimeoutSeconds = 30,
    };

    /// <summary>Build a provider whose HttpClient is backed by a fake
    /// handler returning a fixed SSE body, and whose credential store
    /// returns a configured key.</summary>
    private static (DeepSeekChatProvider provider, FakeHandler handler) BuildProvider(
        HttpStatusCode status, string sseBody, string? apiKey = "test-key")
    {
        var handler = new FakeHandler(status, sseBody);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("DeepSeek").Returns(new HttpClient(handler));
        var store = Substitute.For<ICredentialStore>();
        store.GetAsync("deepseek-api-key", Arg.Any<CancellationToken>())
             .Returns(apiKey);
        var provider = new DeepSeekChatProvider(
            factory, store, Options.Create(BuildOptions()), NullLogger<DeepSeekChatProvider>.Instance);
        return (provider, handler);
    }

    private static async Task<List<ChatUpdate>> ConsumeAsync(
        DeepSeekChatProvider provider, CancellationToken ct = default)
    {
        var updates = new List<ChatUpdate>();
        await foreach (var u in provider.ChatStreamingAsync(EmptyMessages, EmptyTools, ct))
            updates.Add(u);
        return updates;
    }

    [Fact]
    public async Task Streams_Plain_Text_Deltas_Then_Done()
    {
        const string sse = """
            data: {"choices":[{"index":0,"delta":{"content":"Hello"}}]}

            data: {"choices":[{"index":0,"delta":{"content":" world"}}]}

            data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

            data: [DONE]

            """;
        var (provider, _) = BuildProvider(HttpStatusCode.OK, sse);
        var updates = await ConsumeAsync(provider);

        updates.Should().HaveCount(3);
        updates[0].Should().BeOfType<ChatUpdate.PartialDelta>()
            .Which.Text.Should().Be("Hello");
        updates[1].Should().BeOfType<ChatUpdate.PartialDelta>()
            .Which.Text.Should().Be(" world");
        updates[2].Should().BeOfType<ChatUpdate.Done>();
    }

    [Fact]
    public async Task Accumulates_Tool_Calls_And_Yields_RoundDone()
    {
        const string sse = """
            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"get_anchor_info","arguments":""}}]}}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{}"}}]}}]}

            data: {"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;
        var (provider, _) = BuildProvider(HttpStatusCode.OK, sse);
        var updates = await ConsumeAsync(provider);

        // ToolCallStart + ToolCallArgDelta + ToolCallRoundDone
        updates.Should().ContainItemsAssignableTo<ChatUpdate.ToolCallStart>();
        updates.OfType<ChatUpdate.ToolCallStart>().Single().Index.Should().Be(0);
        updates.OfType<ChatUpdate.ToolCallStart>().Single().Name.Should().Be("get_anchor_info");

        updates.OfType<ChatUpdate.ToolCallArgDelta>().Single().ArgsDelta.Should().Be("{}");

        var round = updates.OfType<ChatUpdate.ToolCallRoundDone>().Single();
        round.ToolCalls.Should().HaveCount(1);
        round.ToolCalls[0].Id.Should().Be("call_1");
        round.ToolCalls[0].FunctionName.Should().Be("get_anchor_info");
        round.ToolCalls[0].FunctionArgs.Should().Be("{}");
    }

    [Fact]
    public async Task Yields_Error_When_ApiKey_Missing()
    {
        var (provider, _) = BuildProvider(HttpStatusCode.OK, "data: [DONE]\n\n", apiKey: null);
        var updates = await ConsumeAsync(provider);
        updates.Should().ContainSingle().Which.Should().BeOfType<ChatUpdate.Error>()
            .Which.Message.Should().Be("API key not configured");
    }

    [Fact]
    public async Task Yields_Error_On_Http401()
    {
        var (provider, _) = BuildProvider(HttpStatusCode.Unauthorized, "");
        var updates = await ConsumeAsync(provider);
        updates.Should().ContainSingle().Which.Should().BeOfType<ChatUpdate.Error>()
            .Which.Message.Should().Contain("API key invalid or revoked");
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _sseBody;
        public FakeHandler(HttpStatusCode status, string sseBody) { _status = status; _sseBody = sseBody; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StringContent(_sseBody, Encoding.UTF8, "text/event-stream");
            return Task.FromResult(new HttpResponseMessage(_status) { Content = content });
        }
    }
}
