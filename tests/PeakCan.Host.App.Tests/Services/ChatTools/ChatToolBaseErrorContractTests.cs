using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.ChatTools;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

/// <summary>
/// spec §8.1：ChatToolBase 的所有失败路径必须返回
/// <c>{"error":..., "hint":...}</c>，让模型能按结构化提示自救。
/// 22 个工具都是 sealed，这里用测试内子类直接验证基类契约。
/// </summary>
public class ChatToolBaseErrorContractTests
{
    private sealed class ThrowingTool : ChatToolBase
    {
        private readonly Func<CancellationToken, Task<string>> _body;

        public ThrowingTool(Func<CancellationToken, Task<string>> body)
            : base(
                "throwing_tool",
                "test-only tool",
                """{"type":"object","properties":{},"additionalProperties":false}""",
                NullLogger<ThrowingTool>.Instance)
        {
            _body = body;
        }

        protected override Task<string> ExecuteCoreAsync(string argsJson, CancellationToken ct)
            => _body(ct);
    }

    private static async Task<JsonObject> RunAsync(ThrowingTool tool, CancellationToken ct = default)
    {
        var raw = await tool.ExecuteAsync("{}", ct);
        return JsonNode.Parse(raw)!.AsObject();
    }

    [Fact]
    public async Task Cancellation_ReturnsCancelledErrorWithRetryHint()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var tool = new ThrowingTool(_ => throw new OperationCanceledException(cts.Token));

        var json = await RunAsync(tool, cts.Token);

        json["error"]!.GetValue<string>().Should().Be("cancelled");
        json["hint"]!.GetValue<string>().Should().Be("请求已取消，可重试或缩小窗口");
    }

    [Fact]
    public async Task CancellationWithoutCallerToken_IsStillReturnedAsCancelled()
    {
        // token 未取消却抛 OCE（内部超时）也必须返回结构化取消，不能逃逸成未处理异常。
        var tool = new ThrowingTool(_ => throw new OperationCanceledException("internal timeout"));

        var json = await RunAsync(tool, CancellationToken.None);

        json["error"]!.GetValue<string>().Should().Be("cancelled");
        json["hint"]!.GetValue<string>().Should().Be("请求已取消，可重试或缩小窗口");
    }

    [Fact]
    public async Task ArgumentException_ReturnsParameterHint()
    {
        var tool = new ThrowingTool(_ =>
            throw new ArgumentException("channel must be within 0..65535, got 70000."));

        var json = await RunAsync(tool);

        json["error"]!.GetValue<string>().Should().Contain("channel");
        json["hint"]!.GetValue<string>().Should().Contain("参数");
        json["hint"]!.GetValue<string>().Should().Contain("取值");
    }

    [Fact]
    public async Task FormatException_ReturnsParameterHint()
    {
        // 参数 JSON 里的数值格式错误会抛 FormatException（例如 "abc" 当成整数）。
        var tool = new ThrowingTool(_ => throw new FormatException("The input string 'abc' was not in a correct format."));

        var json = await RunAsync(tool);

        json["error"]!.GetValue<string>().Should().Contain("abc");
        json["hint"]!.GetValue<string>().Should().Contain("参数");
    }

    [Fact]
    public async Task JsonException_ReturnsParameterHint()
    {
        // ParseArgs 解析非法 JSON 时抛的是 System.Text.Json.JsonException。
        var tool = new ThrowingTool(_ => throw new JsonException("invalid JSON"));

        var json = await RunAsync(tool);

        json["error"]!.GetValue<string>().Should().Contain("invalid JSON");
        json["hint"]!.GetValue<string>().Should().Contain("参数");
    }

    [Fact]
    public async Task GenericException_ReturnsDataSourceHintAndProfileSuggestion()
    {
        var tool = new ThrowingTool(_ => throw new InvalidOperationException("trace not loaded"));

        var json = await RunAsync(tool);

        json["error"]!.GetValue<string>().Should().Contain("trace not loaded");
        json["hint"]!.GetValue<string>().Should().Contain("数据源");
        json["hint"]!.GetValue<string>().Should().Contain("get_nm_profile");
    }
}
