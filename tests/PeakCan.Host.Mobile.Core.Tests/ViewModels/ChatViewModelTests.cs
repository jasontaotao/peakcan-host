using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.HIL.Core.Analysis.Chat;
using PeakCan.Host.Mobile.Core.Chat;
using PeakCan.Host.Mobile.Core.Services;
using PeakCan.Host.Mobile.Core.ViewModels;

namespace PeakCan.Host.Mobile.Core.Tests.ViewModels;

public class ChatViewModelTests
{
    private static ChatViewModel BuildVm(
        IChatProvider? provider = null,
        params IChatTool[] tools)
    {
        var context = Substitute.For<IMobileChatToolContext>();
        context.CurrentTimestamp.Returns(12.5);
        context.DurationSeconds.Returns(60.0);
        context.SourceName.Returns("test.asc");
        context.FilterText.Returns((string?)null);
        var factory = Substitute.For<IChatProviderFactory>();
        var vm = new ChatViewModel(context, factory, NullLogger.Instance, tools);
        if (provider is not null) vm.SetProvider(provider);
        return vm;
    }

    [Fact]
    public async Task Send_PlainTextReply_AddsUserAndAssistantBubbles()
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
        vm.IsChatBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Send_ToolCallRound_ExecutesToolAndRepliesNextRound()
    {
        var provider = new FakeChatProvider();
        provider.EnqueueRound(new ChatUpdate.ToolCallRoundDone(new[]
        {
            new ChatToolCall("call_1", "get_anchor_values", "{}"),
        }));
        provider.EnqueueRound(new ChatUpdate.PartialDelta("分析完成"), new ChatUpdate.Done());

        var tool = new FakeChatTool("get_anchor_values", """{"signals":[{"name":"EngineSpeed"}]}""");
        var vm = BuildVm(provider, tool);

        vm.ChatInput = "看看锚点";
        await vm.SendMessageCommand.ExecuteAsync(null);

        // user + assistant(round1, empty) + tool_log + assistant(round2)
        vm.ChatMessages.Should().HaveCount(4);
        vm.ChatMessages[0].IsUser.Should().BeTrue();
        vm.ChatMessages[1].IsAssistant.Should().BeTrue();
        vm.ChatMessages[2].IsToolLog.Should().BeTrue();
        vm.ChatMessages[2].Tools.Should().HaveCount(1);
        vm.ChatMessages[2].Tools[0].Name.Should().Be("get_anchor_values");
        vm.ChatMessages[2].Tools[0].Result.Should().Contain("EngineSpeed");
        vm.ChatMessages[3].IsAssistant.Should().BeTrue();
        vm.ChatMessages[3].Content.Should().Be("分析完成");

        tool.ExecuteCount.Should().Be(1);
    }

    [Fact]
    public async Task Send_ErrorUpdate_StopsLoopAndShowsMessage()
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

    [Fact]
    public async Task Send_NoProviderConfigured_ShowsConnectionHintAndNoBubbles()
    {
        var vm = BuildVm(provider: null);

        vm.ChatInput = "hi";
        await vm.SendMessageCommand.ExecuteAsync(null);

        vm.ChatMessages.Should().BeEmpty();
        vm.ConnectionHint.Should().Contain("API Key");
    }

    [Fact]
    public async Task ClearChat_ResetsMessages()
    {
        var provider = new FakeChatProvider();
        provider.EnqueueRound(new ChatUpdate.PartialDelta("Hello"), new ChatUpdate.Done());
        var vm = BuildVm(provider);

        vm.ChatInput = "hi";
        await vm.SendMessageCommand.ExecuteAsync(null);
        vm.ClearChatCommand.Execute(null);

        vm.ChatMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task Send_InputEmpty_DoesNothing()
    {
        var vm = BuildVm(new FakeChatProvider());
        vm.ChatInput = "   ";

        vm.SendMessageCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Send_UserText_HistoryCarriesToolResultsAcrossRounds()
    {
        var provider = new FakeChatProvider();
        provider.EnqueueRound(new ChatUpdate.ToolCallRoundDone(new[]
        {
            new ChatToolCall("call_1", "get_anchor_values", "{}"),
        }));
        provider.EnqueueRound(new ChatUpdate.PartialDelta("完成"), new ChatUpdate.Done());

        var tool = new FakeChatTool("get_anchor_values", """{"ok":true}""");
        var vm = BuildVm(provider, tool);

        vm.ChatInput = "看锚点";
        await vm.SendMessageCommand.ExecuteAsync(null);

        // Round 2 request must carry system + user + assistant(tool_calls) + tool result
        provider.ReceivedMessages.Should().HaveCount(2);
        var roles = provider.ReceivedMessages[1].Select(m => m.Role).ToList();
        roles.Should().ContainInOrder("system", "user", "assistant", "tool");
        provider.ReceivedMessages[1].Last(m => m.Role == "tool")!.Content.Should().Be("""{"ok":true}""");
        provider.ReceivedMessages[1].Last(m => m.Role == "assistant")!.ToolCalls.Should().HaveCount(1);
    }

    private sealed class FakeChatProvider : IChatProvider
    {
        private readonly Queue<List<ChatUpdate>> _rounds = new();
        public string DisplayName => "Fake";
        public List<IReadOnlyList<ChatMessage>> ReceivedMessages { get; } = new();

        public void EnqueueRound(params ChatUpdate[] updates) => _rounds.Enqueue(updates.ToList());

        public async IAsyncEnumerable<ChatUpdate> ChatStreamingAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ChatToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken ct)
        {
            ReceivedMessages.Add(messages);
            if (_rounds.Count == 0) yield break;
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
