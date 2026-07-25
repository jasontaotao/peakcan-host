using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Input;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.AnalysisApiKey;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Analysis;
using PeakCan.Host.Core.Analysis.Chat;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.Tests.ViewModels;

public class ChatFlowTests
{
    private static TraceViewerViewModel BuildVm(IChatProvider? provider, params IChatTool[] tools)
    {
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(Array.Empty<TraceSource>());
        var dbcService = Substitute.For<DbcService>(Substitute.For<ILogger<DbcService>>());
        var sessionLibrary = new TraceSessionLibrary(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"chatflow-{Guid.NewGuid():N}.tmtrace"),
            NullLogger<TraceSessionLibrary>.Instance);
        var apiKeyManager = new ApiKeyManager(
            Substitute.For<ICredentialStore>(),
            Substitute.For<ILogger<ApiKeyManager>>());
        return new TraceViewerViewModel(
            registry, dbcService, NullLogger<TraceViewerViewModel>.Instance, sessionLibrary,
            chatProvider: provider, chatTools: tools, apiKeyManager: apiKeyManager);
    }

    [Fact]
    public async Task PlainText_Reply_Adds_User_And_Assistant_Bubbles()
    {
        var provider = new FakeChatProvider();
        provider.EnqueueRound(new ChatUpdate.PartialDelta("Hello"), new ChatUpdate.Done());
        var vm = BuildVm(provider);

        vm.ChatInput = "hi";
        await vm.SendMessageCommand.ExecuteAsync(null);

        vm.ChatMessages.Should().HaveCount(2);
        vm.ChatMessages[0].IsUser.Should().BeTrue();
        vm.ChatMessages[0].Content.Should().Be("hi");
        vm.ChatMessages[1].IsAssistant.Should().BeTrue();
        vm.ChatMessages[1].Content.Should().Be("Hello");
        vm.ChatMessages[1].IsStreaming.Should().BeFalse();
    }

    [Fact]
    public async Task ToolCall_Round_Executes_Tool_And_Replies_Next_Round()
    {
        var provider = new FakeChatProvider();
        // Round 1: assistant requests one tool call (no text content)
        provider.EnqueueRound(new ChatUpdate.ToolCallRoundDone(new[]
        {
            new ChatToolCall("call_1", "get_anchor_info", "{}"),
        }));
        // Round 2: assistant replies with text + done
        provider.EnqueueRound(new ChatUpdate.PartialDelta("分析完成"), new ChatUpdate.Done());

        var tool = new FakeChatTool("get_anchor_info", """{"green_ts":12.0}""");
        var vm = BuildVm(provider, tool);

        vm.ChatInput = "看看锚点";
        await vm.SendMessageCommand.ExecuteAsync(null);

        // user + assistant(round1, empty) + tool_log + assistant(round2)
        vm.ChatMessages.Should().HaveCount(4);
        vm.ChatMessages[0].IsUser.Should().BeTrue();
        vm.ChatMessages[1].IsAssistant.Should().BeTrue();
        vm.ChatMessages[2].IsToolLog.Should().BeTrue();
        vm.ChatMessages[2].Tools.Should().HaveCount(1);
        vm.ChatMessages[2].Tools[0].Name.Should().Be("get_anchor_info");
        vm.ChatMessages[2].Tools[0].Result.Should().Contain("green_ts");
        vm.ChatMessages[3].IsAssistant.Should().BeTrue();
        vm.ChatMessages[3].Content.Should().Be("分析完成");

        tool.ExecuteCount.Should().Be(1);
    }

    [Fact]
    public async Task Error_Update_Stops_Loop_And_Shows_Message()
    {
        var provider = new FakeChatProvider();
        provider.EnqueueRound(
            new ChatUpdate.PartialDelta("partial"),
            new ChatUpdate.Error("API key invalid"));
        var vm = BuildVm(provider);

        vm.ChatInput = "hi";
        await vm.SendMessageCommand.ExecuteAsync(null);

        vm.ChatMessages.Should().HaveCount(2);
        vm.ChatMessages[1].IsAssistant.Should().BeTrue();
        vm.ChatMessages[1].Content.Should().Contain("partial");
        vm.ChatMessages[1].Content.Should().Contain("API key invalid");
    }

    private sealed class FakeChatProvider : IChatProvider
    {
        private readonly Queue<List<ChatUpdate>> _rounds = new();
        public string DisplayName => "Fake";

        public void EnqueueRound(params ChatUpdate[] updates) => _rounds.Enqueue(updates.ToList());

        public async IAsyncEnumerable<ChatUpdate> ChatStreamingAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ChatToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken ct)
        {
            var round = _rounds.Dequeue();
            foreach (var u in round)
            {
                await Task.Yield();
                yield return u;
            }
        }
    }

    private sealed class FakeChatTool : IChatTool
    {
        public string Name { get; }
        public ChatToolDefinition Definition { get; }
        public string Result { get; }
        public int ExecuteCount { get; private set; }

        public FakeChatTool(string name, string result)
        {
            Name = name;
            Result = result;
            Definition = new ChatToolDefinition(name, "fake", JsonNode.Parse("{}")!);
        }

        public Task<string> ExecuteAsync(string argsJson, CancellationToken ct)
        {
            ExecuteCount++;
            return Task.FromResult(Result);
        }
    }
}
