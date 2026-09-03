using System.Net;
using System.Text;
using FluentAssertions;
using PeakCan.Tools.PromptCacheProbe;

namespace PromptCacheProbe.Tests;

public class ProbeRunnerTests
{
    private const string TestSystemPrompt = "你是一个汽车 CAN 总线故障诊断专家。当前 trace 状态...（固定前缀）";

    private static readonly ProbeTurn[] Turns =
    {
        new("第一条用户消息", "第一条助手回复"),
        new("第二条用户消息", "第二条助手回复"),
        new("第三条用户消息", "第三条助手回复"),
    };

    [Fact]
    public void BuildGrowingMessages_RoundPrefix_Contains_Previous_Round()
    {
        // Arrange / Act — 第 k 轮消息 = 第 k-1 轮消息 + 新的一对 user/assistant
        var r1 = ProbeRunner.BuildGrowingMessages(Turns, TestSystemPrompt, round: 1);
        var r2 = ProbeRunner.BuildGrowingMessages(Turns, TestSystemPrompt, round: 2);
        var r3 = ProbeRunner.BuildGrowingMessages(Turns, TestSystemPrompt, round: 3);

        // Assert — 前缀必须字节级稳定（缓存命中的前提）
        r2.Take(r1.Count).Should().Equal(r1);
        r3.Take(r2.Count).Should().Equal(r2);
        r1.Should().HaveCount(3); // system + user1 + assistant1
        r2.Should().HaveCount(5);
        r3.Should().HaveCount(7);
        r1[0].Should().Be(new ProbeMessage("system", TestSystemPrompt));
    }

    [Fact]
    public async Task Sends_Correct_Request_Shape()
    {
        // Arrange — 捕获请求体的 fake handler
        HttpRequestMessage? captured = null;
        string? requestBody = null;
        var handler = new RecordingHandler(r =>
        {
            captured = r;
            requestBody = r.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(FixedResponseJson, Encoding.UTF8, "application/json"),
            };
        });
        var runner = new ProbeRunner(
            new HttpClient(handler), "https://api.deepseek.com/v1", "deepseek-chat", "sk-test", TestSystemPrompt);

        // Act
        var results = await runner.SendGrowingAsync(Turns, CancellationToken.None);

        // Assert — URL、Bearer、stream=false、request body 结构
        captured!.RequestUri!.ToString().Should().Be("https://api.deepseek.com/v1/chat/completions");
        captured.Headers.Authorization!.Scheme.Should().Be("Bearer");
        captured.Headers.Authorization.Parameter.Should().Be("sk-test");
        requestBody.Should().Contain("\"stream\":false");
        requestBody.Should().Contain("\"model\":\"deepseek-chat\"");
        results.Should().HaveCount(3);
    }

    [Fact]
    public async Task Parses_Usage_Per_Round()
    {
        // Arrange — 固定响应: 每轮 100 prompt tokens, 90 hit / 10 miss
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(FixedResponseJson, Encoding.UTF8, "application/json"),
        });
        var runner = new ProbeRunner(
            new HttpClient(handler), "https://api.deepseek.com/v1", "deepseek-chat", "sk-test", TestSystemPrompt);

        // Act
        var results = await runner.SendGrowingAsync(Turns, CancellationToken.None);

        // Assert
        foreach (var round in results)
        {
            round.Usage.PromptTokens.Should().Be(100);
            round.Usage.PromptCacheHitTokens.Should().Be(90);
            round.Usage.PromptCacheMissTokens.Should().Be(10);
            round.Usage.HitRatio.Should().BeApproximately(0.9, 1e-9);
        }
        results[0].MessageCount.Should().Be(3);
        results[2].MessageCount.Should().Be(7);
    }

    [Fact]
    public async Task Non_Success_Status_Throws_With_Server_Error_Body()
    {
        // Arrange — 4xx 响应 (如余额不足) 应在异常里带上服务端错误正文
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.PaymentRequired)
        {
            Content = new StringContent(
                """{"error":{"message":"Insufficient Balance","type":"insufficient_quota"}}""",
                Encoding.UTF8, "application/json"),
        });
        var runner = new ProbeRunner(
            new HttpClient(handler), "https://api.deepseek.com/v1", "deepseek-chat", "sk-test", TestSystemPrompt);

        // Act
        var act = () => runner.SendGrowingAsync(Turns, CancellationToken.None);

        // Assert — 异常消息包含状态码 + 错误正文
        var ex = await act.Should().ThrowAsync<HttpRequestException>();
        ex.Which.Message.Should().Contain("402");
        ex.Which.Message.Should().Contain("Insufficient Balance");
    }

    [Fact]
    public async Task Provider_Without_Cache_Fields_Defaults_To_Zero_Hit()
    {
        // Arrange — GLM/Kimi 等厂商可能不返回 cache 字段
        const string noCacheJson = """
            {
              "id": "x", "object": "chat.completion", "created": 1, "model": "mv",
              "choices": [{"index": 0, "message": {"role": "assistant", "content": "ok"}, "finish_reason": "stop"}],
              "usage": {"prompt_tokens": 64, "completion_tokens": 8, "total_tokens": 72}
            }
            """;
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(noCacheJson, Encoding.UTF8, "application/json"),
        });
        var runner = new ProbeRunner(
            new HttpClient(handler), "https://api.deepseek.com/v1", "deepseek-chat", "sk-test", TestSystemPrompt);

        // Act
        var results = await runner.SendGrowingAsync(Turns, CancellationToken.None);

        // Assert
        results[0].Usage.PromptCacheHitTokens.Should().Be(0);
        results[0].Usage.PromptCacheMissTokens.Should().Be(0);
        results[0].Usage.HitRatio.Should().Be(0);
    }

    private const string FixedResponseJson = """
        {
          "id": "chatcmpl-1", "object": "chat.completion", "created": 1, "model": "deepseek-chat",
          "choices": [{ "index": 0, "message": { "role": "assistant", "content": "ok" }, "finish_reason": "stop" }],
          "usage": {
            "prompt_tokens": 100,
            "completion_tokens": 20,
            "total_tokens": 120,
            "prompt_cache_hit_tokens": 90,
            "prompt_cache_miss_tokens": 10
          }
        }
        """;

    /// <summary>Records the request and returns the canned response.</summary>
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }
}