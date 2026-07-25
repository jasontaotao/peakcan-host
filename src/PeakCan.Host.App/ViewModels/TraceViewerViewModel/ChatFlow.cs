using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.Core.Analysis.Chat;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// AI Chat panel logic: message list + SendMessageCommand + multi-round
/// tool-calling loop. Sister of <c>AnalysisFlow</c> (one-shot report);
/// this is the conversational path.
/// </summary>
public sealed partial class TraceViewerViewModel
{
    /// <summary>Max provider rounds per user message (spec §5).</summary>
    private const int ChatMaxRounds = 8;

    // Injected via ctor (Step 5 DI). Nullable defaults keep the legacy
    // test ctor signature compiling; production DI passes real instances.
    private readonly IChatProvider? _chatProvider;
    private IReadOnlyList<IChatTool> _chatTools = Array.Empty<IChatTool>();
    private IReadOnlyList<ChatToolDefinition> _chatToolDefs = Array.Empty<ChatToolDefinition>();

    /// <summary>Cross-round LLM message history (system prompt rebuilt
    /// per send; this list holds user/assistant/tool turns).</summary>
    private readonly List<ChatMessage> _chatHistory = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private string _chatInput = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private bool _isChatBusy;

    public ObservableCollection<ChatMessageViewModel> ChatMessages { get; } = new();

    private Views.TraceViewerViewChatPanel? _chatPanelContent;
    /// <summary>Lazy-loaded UserControl backing the AI Chat tab (sister
    /// of <c>AIPanelContent</c>). DataContext bound to <c>this</c>.</summary>
    public Views.TraceViewerViewChatPanel? ChatPanelContent
    {
        get
        {
            if (_chatPanelContent is null)
            {
                _chatPanelContent = new Views.TraceViewerViewChatPanel { DataContext = this };
                // Production: no tools injected via ctor (DI cycle - tools need
                // IChatToolContext which is the VM). Build them lazily here, bound
                // to `this`. Tests inject fakes via ctor so _chatTools is non-empty.
                if (_chatTools.Count == 0) _chatTools = BuildChatTools();
                _chatToolDefs = _chatTools.Select(t => t.Definition).ToList();
            }
            return _chatPanelContent;
        }
    }

    private bool CanSendChat() => !IsChatBusy && !string.IsNullOrWhiteSpace(ChatInput);

    [RelayCommand(CanExecute = nameof(CanSendChat))]
    private async Task SendMessageAsync()
    {
        if (_chatProvider is null)
        {
            ErrorMessage = "聊天 Provider 未配置";
            return;
        }
        var userText = ChatInput.Trim();
        ChatInput = "";

        _analysisCts ??= new CancellationTokenSource();
        var ct = _analysisCts.Token;
        IsChatBusy = true;
        try
        {
            await RunChatLoopAsync(userText, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chat loop failed");
            ChatMessages.Add(new ChatMessageViewModel("assistant", $"[出错: {ex.Message}]"));
        }
        finally
        {
            IsChatBusy = false;
        }
    }

    [RelayCommand]
    private void ClearChat()
    {
        _chatHistory.Clear();
        ChatMessages.Clear();
    }

    [RelayCommand]
    private void ExportChat()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"peakcan-chat-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        var json = System.Text.Json.JsonSerializer.Serialize(
            _chatHistory,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        System.IO.File.WriteAllText(path, json);
        StatusMessage = $"聊天记录已导出: {path}";
    }

    private async Task RunChatLoopAsync(string userText, CancellationToken ct)
    {
        _chatHistory.Add(new ChatMessage("user", userText, null, null));
        ChatMessages.Add(new ChatMessageViewModel("user", userText));

        for (int round = 0; round < ChatMaxRounds; round++)
        {
            var messages = new List<ChatMessage> { BuildSystemMessage() };
            messages.AddRange(_chatHistory);

            var aiBubble = new ChatMessageViewModel("assistant") { IsStreaming = true };
            ChatMessages.Add(aiBubble);
            var content = new StringBuilder();
            var toolCalls = new List<ChatToolCall>();
            var errored = false;

            await foreach (var update in _chatProvider!.ChatStreamingAsync(messages, _chatToolDefs, ct)
                              .ConfigureAwait(true))
            {
                switch (update)
                {
                    case ChatUpdate.PartialDelta d:
                        content.Append(d.Text);
                        aiBubble.Content += d.Text;
                        break;
                    case ChatUpdate.ToolCallRoundDone r:
                        toolCalls = r.ToolCalls.ToList();
                        break;
                    case ChatUpdate.Error e:
                        aiBubble.Content += $"\n[错误: {e.Message}]";
                        _chatHistory.Add(new ChatMessage(
                            "assistant", content.ToString() + $"\n[错误: {e.Message}]", null, null));
                        errored = true;
                        break;
                    case ChatUpdate.Done:
                        break;
                }
                if (update is ChatUpdate.Error or ChatUpdate.Done) break;
            }

            aiBubble.IsStreaming = false;
            if (errored) return;

            if (toolCalls.Count == 0)
            {
                _chatHistory.Add(new ChatMessage("assistant", content.ToString(), null, null));
                return; // assistant replied with text - turn complete
            }

            // Assistant requested tools - record the assistant turn + execute.
            // Per OpenAI/DeepSeek spec: assistant message with tool_calls and no
            // text content must carry content=null (not "") so it serializes as
            // omitted, not "content":"" (strict backends reject the latter).
            _chatHistory.Add(new ChatMessage(
                "assistant", content.Length == 0 ? null : content.ToString(), toolCalls, null));

            var toolLog = new ChatMessageViewModel("tool_log");
            var results = new string?[toolCalls.Count];
            await Parallel.ForEachAsync(
                toolCalls.Select((tc, i) => (tc, i)),
                ct,
                async (item, ct2) =>
                {
                    var tool = _chatTools.FirstOrDefault(t => t.Name == item.tc.FunctionName);
                    results[item.i] = tool is null
                        ? $"{{\"error\":\"unknown tool: {item.tc.FunctionName}\"}}"
                        : await tool.ExecuteAsync(item.tc.FunctionArgs, ct2).ConfigureAwait(false);
                }).ConfigureAwait(true);

            for (int i = 0; i < toolCalls.Count; i++)
            {
                toolLog.Tools.Add(new ToolCallEntry(toolCalls[i].FunctionName, results[i]!));
                _chatHistory.Add(new ChatMessage("tool", results[i], null, toolCalls[i].Id));
            }
            ChatMessages.Add(toolLog);
            // loop continues - next round the assistant replies to the tool results
        }

        // MaxRounds exhausted
        ChatMessages.Add(new ChatMessageViewModel("assistant", "[达到最大轮数上限，请发新消息继续]"));
    }

    private ChatMessage BuildSystemMessage()
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是一个汽车 CAN 总线故障诊断专家。");
        sb.AppendLine();
        sb.AppendLine("当前 trace 状态:");
        sb.AppendLine($"- 绿锚: {FormatTs(_anchorTimestampSeconds)}");
        sb.AppendLine($"- 蓝锚: {FormatTs(_blueAnchorTimestampSeconds)}");
        var watchCount = WatchedSignals.Count(r => !r.IsPlaceholder);
        sb.AppendLine($"- watch list: {watchCount} 条信号");
        var dbc = _dbcService.Current;
        sb.AppendLine($"- DBC: {(dbc is null
            ? "未加载"
            : (string.IsNullOrEmpty(dbc.SourcePath) ? "已加载" : System.IO.Path.GetFileName(dbc.SourcePath)))}");
        sb.AppendLine();
        sb.AppendLine("可用工具: find_related_signals, propose_to_watch_list, get_anchor_info, get_dbc_signal, get_dbc_message, seek_to");
        sb.AppendLine();
        sb.AppendLine("分析原则:");
        sb.AppendLine("1. 信息不足时问用户，不编造");
        sb.AppendLine("2. 引用数据时给出具体数值（如 BatteryVoltage 从 12.5V 降到 11.0V）");
        sb.AppendLine("3. 发现关联信号时反问用户要不要加 watch list，给明确选择（是/否）");
        sb.AppendLine("4. propose_to_watch_list 后可同轮调 get_anchor_info 读新值");
        sb.AppendLine("5. 第一轮可直接调 get_anchor_info 读已有 watch list 数据");
        sb.AppendLine("6. 不确定时说不确定");
        return new ChatMessage("system", sb.ToString(), null, null);
    }

    private static string FormatTs(double ts) => double.IsNaN(ts) ? "未设" : $"{ts:F3}s";

    /// <summary>Construct the 6 chat tools bound to this VM as
    /// <see cref="IChatToolContext"/>. Production path (no DI - avoids the
    /// VM↔IChatTool cycle). Tests inject fakes via ctor instead.</summary>
    private IReadOnlyList<IChatTool> BuildChatTools()
    {
        var ctx = (IChatToolContext)this;
        return new IChatTool[]
        {
            new FindRelatedSignalsTool(ctx, Microsoft.Extensions.Logging.Abstractions.NullLogger<FindRelatedSignalsTool>.Instance),
            new ProposeToWatchListTool(ctx, Microsoft.Extensions.Logging.Abstractions.NullLogger<ProposeToWatchListTool>.Instance),
            new GetAnchorInfoTool(ctx, Microsoft.Extensions.Logging.Abstractions.NullLogger<GetAnchorInfoTool>.Instance),
            new GetDbcSignalTool(ctx, Microsoft.Extensions.Logging.Abstractions.NullLogger<GetDbcSignalTool>.Instance),
            new GetDbcMessageTool(ctx, Microsoft.Extensions.Logging.Abstractions.NullLogger<GetDbcMessageTool>.Instance),
            new SeekToTimeTool(ctx, Microsoft.Extensions.Logging.Abstractions.NullLogger<SeekToTimeTool>.Instance),
        };
    }
}
