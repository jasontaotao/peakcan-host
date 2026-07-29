using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.Core.Analysis;
using PeakCan.Host.Core.Analysis.Chat;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// AI Chat panel logic: message list + SendMessageCommand + multi-round
/// tool-calling loop. Sister of <c>AnalysisFlow</c> (one-shot report);
/// this is the conversational path.
/// </summary>
public sealed partial class TraceViewerViewModel
{
    /// <summary>Max provider rounds per user message. v12: 8 -> 12 to
    /// accommodate 19-tool workflows (spec §6).</summary>
    private const int ChatMaxRounds = 12;

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

    /// <summary>v12: when true, system prompt tells the AI to skip
    /// step-by-step confirmation and execute reasonable operations
    /// directly. For engineers running the same diagnostic flow
    /// repeatedly.</summary>
    [ObservableProperty]
    private bool _autoConfirm;

    public ObservableCollection<ChatMessageViewModel> ChatMessages { get; } = new();

    /// <summary>v12: true when no messages yet (show welcome suggestions).</summary>
    public bool HasMessages => ChatMessages.Count > 0;

    /// <summary>v12: inverse of HasMessages, for XAML empty-state visibility.</summary>
    public bool HasNoMessages => ChatMessages.Count == 0;

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
                ChatMessages.CollectionChanged += (_, _) =>
                {
                    OnPropertyChanged(nameof(HasMessages));
                    OnPropertyChanged(nameof(HasNoMessages));
                };
            }
            return _chatPanelContent;
        }
    }

    /// <summary>v12: Fill ChatInput with a suggestion text and send.</summary>
    [RelayCommand]
    private void SendSuggestion(string suggestion)
    {
        ChatInput = suggestion;
        if (SendMessageCommand.CanExecute(null))
            SendMessageCommand.Execute(null);
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
                    case ChatUpdate.ToolCallStart s:
                        // Live tool-call display: the provider emits this when a
                        // tool call begins. Currently no UI surface consumes it;
                        // logged at debug so silent drops are visible in dev.
                        _logger.LogDebug("Chat tool call started: {ToolName}", s.Name);
                        break;
                    case ChatUpdate.ToolCallArgDelta:
                        // Incremental argument fragments for an in-progress tool
                        // call. Not consumed by the UI; intentionally ignored.
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
                    default:
                        _logger.LogWarning("Unexpected chat update type: {Type}", update.GetType().Name);
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
            // Sequential execution (v12 C2 fix): same-round tools may have
            // data dependencies (e.g. propose_to_watch_list -> get_anchor_info
            // in the same round). Parallel execution would let get_anchor_info
            // read the watch list before propose_to_watch_list finishes.
            for (int i = 0; i < toolCalls.Count; i++)
            {
                var tc = toolCalls[i];
                var tool = _chatTools.FirstOrDefault(t => t.Name == tc.FunctionName);
                results[i] = tool is null
                    ? $"{{\"error\":\"unknown tool: {tc.FunctionName}\"}}"
                    : await tool.ExecuteAsync(tc.FunctionArgs, ct).ConfigureAwait(true);
            }

            for (int i = 0; i < toolCalls.Count; i++)
            {
                toolLog.Tools.Add(new ToolCallEntry(toolCalls[i].FunctionName, results[i]!));
                _chatHistory.Add(new ChatMessage("tool", results[i], null, toolCalls[i].Id));
            }
            ChatMessages.Add(toolLog);
            // loop continues - next round the assistant replies to the tool results
        }

        // MaxRounds exhausted. Write to both UI and history so the LLM sees
        // the hint on the next user turn and exports include it.
        const string maxRoundsMsg = "[达到最大轮数上限，请发新消息继续]";
        var maxRoundsBubble = new ChatMessageViewModel("assistant", maxRoundsMsg);
        ChatMessages.Add(maxRoundsBubble);
        _chatHistory.Add(new ChatMessage("assistant", maxRoundsMsg, null, null));
    }

    private ChatMessage BuildSystemMessage()
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是一个汽车 CAN 总线故障诊断专家。");
        sb.AppendLine();
        sb.AppendLine("当前 trace 状态:");
        // 统一时间格式：WallClockOrigin 从首个 source 取，与图表 X 轴一致。
        var wallClockOrigin = Sources.FirstOrDefault()?.WallClockOrigin;
        sb.AppendLine($"- 绿锚: {FormatTs(_anchorTimestampSeconds, wallClockOrigin)}");
        sb.AppendLine($"- 蓝锚: {FormatTs(_blueAnchorTimestampSeconds, wallClockOrigin)}");
        var watchCount = WatchedSignals.Count(r => !r.IsPlaceholder);
        sb.AppendLine($"- watch list: {watchCount} 条信号");
        var dbc = _dbcService.Current;
        sb.AppendLine($"- DBC: {(dbc is null
            ? "未加载"
            : (string.IsNullOrEmpty(dbc.SourcePath) ? "已加载" : System.IO.Path.GetFileName(dbc.SourcePath)))}");
        // v12 Step 4: inject DBC node list so the AI knows which ECUs are present.
        if (dbc is not null && dbc.Nodes.Count > 0)
            sb.AppendLine($"- DBC 节点: {string.Join(", ", dbc.Nodes.Select(n => n.Name))}");
        // v12: inject current playback timestamp + chart viewport so the AI
        // knows what time range the user is currently looking at.
        var currentTs = _masterService?.CurrentTimestamp ?? 0.0;
        sb.AppendLine($"- 当前播放时间戳: {FormatTraceTime(currentTs, wallClockOrigin)}");
        var viewports = ChartViewModel.CaptureViewports();
        if (viewports.Count > 0)
        {
            var vp = viewports[0];
            sb.AppendLine($"- chart 视口范围: {FormatTraceTime(vp.XMin, wallClockOrigin)} ~ {FormatTraceTime(vp.XMax, wallClockOrigin)}");
        }
        if (AutoConfirm)
            sb.AppendLine("- 静默模式: 开启（直接执行合理操作，不需要逐步反问确认）");
        sb.AppendLine();
        sb.AppendLine("时间格式约定:");
        sb.AppendLine("- 图表 X 轴与工具返回的 *_label 字段统一使用秒数（保留4位小数，如 158340.5101）");
        sb.AppendLine("- 向用户提及任何时间戳时，必须引用 *_label 字段或本消息已格式化的时间字符串，禁止自行换算成 Xd hh:mm:ss 等其他形式");
        sb.AppendLine();
        sb.AppendLine("可用工具（19 个）：");
        sb.AppendLine("发现类: search_signals, get_signal_overview, anomaly_scan");
        sb.AppendLine("查询类: get_dbc_signal, get_dbc_message, find_related_signals");
        sb.AppendLine("操作类: propose_to_watch_list, remove_from_watch_list, seek_to");
        sb.AppendLine("分析类: search_signal_trace, get_anchor_info, analyze_timing_sequence");
        sb.AppendLine("上下文类: get_trace_info, get_dbc_info");
        sb.AppendLine("组织类: create_group, add_to_group, remove_from_group, set_group_notes, set_signal_alias");
        sb.AppendLine();
        sb.AppendLine("分析原则:");
        sb.AppendLine("1. 信息不足时问用户，不编造");
        sb.AppendLine("2. 引用数据时给出具体数值（如 BatteryVoltage 从 12.5V 降到 11.0V）");
        if (AutoConfirm)
        {
            sb.AppendLine("3. 静默模式已开启：发现关联信号直接加入 watch list，不反问确认");
            sb.AppendLine("4. propose_to_watch_list 后可同轮调 get_anchor_info 读新值");
            sb.AppendLine("5. 第一轮可直接调 get_anchor_info 读已有 watch list 数据");
            sb.AppendLine("6. 不确定时说不确定");
        }
        else
        {
            sb.AppendLine("3. 发现关联信号时反问用户要不要加 watch list，给明确选择（是/否）");
            sb.AppendLine("4. propose_to_watch_list 后可同轮调 get_anchor_info 读新值");
            sb.AppendLine("5. 第一轮可直接调 get_anchor_info 读已有 watch list 数据");
            sb.AppendLine("6. 不确定时说不确定");
        }
        return new ChatMessage("system", sb.ToString(), null, null);
    }

    private static string FormatTs(double ts, DateTime? wallClockOrigin)
        => double.IsNaN(ts) ? "未设" : FormatTraceTime(ts, wallClockOrigin);

    /// <summary>Format trace time via the shared <see cref="TraceTimeFormatter"/>
    /// so AI Chat timestamps always match the chart X-axis (including the
    /// WallClockOrigin -> MM/dd HH:mm:ss branch that the old static 3-branch
    /// version was missing).</summary>
    private static string FormatTraceTime(double seconds, DateTime? wallClockOrigin)
        => TraceTimeFormatter.Format(seconds, wallClockOrigin);

    /// <summary>Construct the 19 chat tools bound to this VM as
    /// <see cref="IChatToolContext"/>. Production path (no DI - avoids the
    /// VM↔IChatTool cycle). Tests inject fakes via ctor instead.</summary>
    private IReadOnlyList<IChatTool> BuildChatTools()
    {
        var ctx = (IChatToolContext)this;
        var lf = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        return new IChatTool[]
        {
            // Discovery
            new SearchSignalsTool(ctx, lf.CreateLogger<SearchSignalsTool>()),
            new GetSignalOverviewTool(ctx, lf.CreateLogger<GetSignalOverviewTool>()),
            new AnomalyScanTool(ctx, lf.CreateLogger<AnomalyScanTool>()),
            // Query (existing + improved)
            new GetDbcSignalTool(ctx, lf.CreateLogger<GetDbcSignalTool>()),
            new GetDbcMessageTool(ctx, lf.CreateLogger<GetDbcMessageTool>()),
            new FindRelatedSignalsTool(ctx, lf.CreateLogger<FindRelatedSignalsTool>()),
            // Operation (existing + new)
            new ProposeToWatchListTool(ctx, lf.CreateLogger<ProposeToWatchListTool>()),
            new RemoveFromWatchListTool(ctx, lf.CreateLogger<RemoveFromWatchListTool>()),
            new SeekToTimeTool(ctx, lf.CreateLogger<SeekToTimeTool>()),
            // Analysis
            new SearchSignalTraceTool(ctx, lf.CreateLogger<SearchSignalTraceTool>()),
            new GetAnchorInfoTool(ctx, lf.CreateLogger<GetAnchorInfoTool>()),
            new AnalyzeTimingSequenceTool(ctx, lf.CreateLogger<AnalyzeTimingSequenceTool>()),
            // Context
            new GetTraceInfoTool(ctx, lf.CreateLogger<GetTraceInfoTool>()),
            new GetDbcInfoTool(ctx, lf.CreateLogger<GetDbcInfoTool>()),
            // Organization
            new CreateGroupTool(ctx, lf.CreateLogger<CreateGroupTool>()),
            new AddToGroupTool(ctx, lf.CreateLogger<AddToGroupTool>()),
            new RemoveFromGroupTool(ctx, lf.CreateLogger<RemoveFromGroupTool>()),
            new SetGroupNotesTool(ctx, lf.CreateLogger<SetGroupNotesTool>()),
            new SetSignalAliasTool(ctx, lf.CreateLogger<SetSignalAliasTool>()),
        };
    }
}
