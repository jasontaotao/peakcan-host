using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.App.Services.Uds;
using PeakCan.HIL.Core.Analysis;
using PeakCan.HIL.Core.Analysis.Chat;
using PeakCan.Host.Core.Analysis;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// AI Chat panel logic: message list + SendMessageCommand + multi-round
/// tool-calling loop. Sister of <c>AnalysisFlow</c> (one-shot report);
/// this is the conversational path.
/// </summary>
public sealed partial class TraceViewerViewModel
{
    /// <summary>Max provider rounds per user message. v12: 8 -> 12 to
    /// accommodate 22-tool workflows (spec §6).</summary>
    private const int ChatMaxRounds = 12;

    // Injected via ctor (Step 5 DI). Nullable defaults keep the legacy
    // test ctor signature compiling; production DI passes real instances.
    private readonly IChatProvider? _chatProvider;
    private IReadOnlyList<IChatTool> _chatTools = Array.Empty<IChatTool>();
    private IReadOnlyList<ChatToolDefinition> _chatToolDefs = Array.Empty<ChatToolDefinition>();
    private static readonly System.Text.Json.JsonSerializerOptions s_exportJsonOptions = new() { WriteIndented = true };

    /// <summary>Cross-round LLM message history (system prompt rebuilt
    /// per send; this list holds user/assistant/tool turns).</summary>
    private readonly List<ChatMessage> _chatHistory = new();

    /// <summary>Chat lifecycle CancellationTokenSource (v3.62-era:
    /// previously shared with the removed AI Analysis flow). Cancelled on
    /// VM Dispose so in-flight chat HTTP requests are aborted when the
    /// window closes.</summary>
    private CancellationTokenSource? _chatCts;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private string _chatInput = "";

    /// <summary>
    /// 右侧面板 TabControl 中「AI 对话」页的索引
    /// （0 监视列表 / 1 采样表 / 2 AI 对话 / 3 网络管理）。
    /// NM 面板的「让 AI 解读/总结」需要把用户带到 AI 页，选中项由 VM 驱动；
    /// 若将来调整 Tab 顺序，此常量必须同步。
    /// </summary>
    public const int ChatPanelTabIndex = 2;

    /// <summary>右侧面板 TabControl 的当前选中索引（View 绑定）。</summary>
    [ObservableProperty]
    private int _rightPanelSelectedIndex;

    /// <summary>预填输入框后的引导文案（显示在输入区上方）；发送消息后清空。</summary>
    [ObservableProperty]
    private string _chatPrefillHint = "";

    /// <summary>每次从 NM 面板预填问题就递增：View 据此把键盘焦点移到输入框。
    /// 用计数而不是 bool，连续两次预填（中间不发送）也能再次触发聚焦。</summary>
    [ObservableProperty]
    private int _chatInputFocusTicket;

    /// <summary>输入区上方提示条的可见性（空文案时不占位）。</summary>
    public bool HasChatPrefillHint => ChatPrefillHint.Length > 0;

    partial void OnChatPrefillHintChanged(string value)
        => OnPropertyChanged(nameof(HasChatPrefillHint));

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
                // ChatSettingsFlow: 加载已保存的多厂商 API Key
                _ = LoadChatSavedKeysAsync();
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

    /// <summary>
    /// NM 面板「在 AI 中解读选中事件」的接线：只预填输入框，不自动发送。
    /// 单独抽出便于单测验证「生成提示」与「未发送」两件事。
    /// </summary>
    internal void PrefillChatInput(string text) => ChatInput = text;

    /// <summary>
    /// UDS 离线分析页的 Handoff 入口：只预填问题 + 切到 AI 对话页 + 请求聚焦，
    /// 不自动发送。系统提示里追加本次 Handoff 的证据边界规则。
    /// </summary>
    private UdsChatHandoffPayload? _udsHandoff;

    /// <summary>
    /// 覆盖草稿确认（spec §14.3）。null = 不弹窗、也不覆盖（测试/无 UI 环境的默认）。
    /// 生产由 <c>TraceViewerChatHandoffService</c> 注入 MessageBox 实现。
    /// </summary>
    internal Func<string, bool>? ConfirmReplaceDraft { get; set; }

    internal void AcceptChatHandoff(UdsChatHandoffPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        _udsHandoff = payload;

        // 用户可能正在写草稿：静默覆盖 = 丢输入。有不同草稿时先问；
        // 拒绝或无法确认时保留原文并说明原因。
        if (!string.IsNullOrWhiteSpace(ChatInput) && ChatInput != payload.Prompt)
        {
            if (ConfirmReplaceDraft?.Invoke(payload.Prompt) == true)
            {
                PrefillChatInput(payload.Prompt);
                ChatPrefillHint = payload.Hint;
            }
            else
            {
                ChatPrefillHint = "输入框已有未发送的草稿，未覆盖；清空后可重新点击「在 AI 中解读」";
            }
        }
        else
        {
            PrefillChatInput(payload.Prompt);
            ChatPrefillHint = payload.Hint;
        }

        RightPanelSelectedIndex = ChatPanelTabIndex;
        ChatInputFocusTicket++;
    }

    /// <summary>测试用：暴露私有 <see cref="BuildSystemMessage"/>（不引入公共 API）。</summary>
    internal string BuildSystemMessageForTest() => BuildSystemMessage().Content ?? string.Empty;

    private bool CanSendChat() => !IsChatBusy && !string.IsNullOrWhiteSpace(ChatInput);

    [RelayCommand(CanExecute = nameof(CanSendChat))]
    private async Task SendMessageAsync()
    {
        if (_chatProvider is null)
        {
            // 用户已经按了发送：提示条不能再停留在“按发送开始”的待办状态。
            ChatPrefillHint = "";
            ErrorMessage = "聊天 Provider 未配置";
            return;
        }
        var userText = ChatInput.Trim();
        ChatInput = "";
        // 预填引导只对“尚未发送”的那一次有效；发出去后提示必须消失。
        ChatPrefillHint = "";

        _chatCts ??= new CancellationTokenSource();
        var ct = _chatCts.Token;
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
            s_exportJsonOptions);
        System.IO.File.WriteAllText(path, json);
        StatusMessage = $"聊天记录已导出: {path}";
    }

    private async Task RunChatLoopAsync(string userText, CancellationToken ct)
    {
        _chatHistory.Add(new ChatMessage("user", userText, null, null));
        ChatMessages.Add(new ChatMessageViewModel("user", userText));

        // 多厂商: 优先使用设置面板构建的 Provider, 回退到 DI 注入的 _chatProvider
        var provider = SettingsChatProvider ?? _chatProvider;
        if (provider is null)
        {
            ChatMessages.Add(new ChatMessageViewModel("assistant", "[出错: 聊天 Provider 未配置]"));
            return;
        }

        for (int round = 0; round < ChatMaxRounds; round++)
        {
            var messages = new List<ChatMessage> { BuildSystemMessage() };
            messages.AddRange(_chatHistory);

            var aiBubble = new ChatMessageViewModel("assistant") { IsStreaming = true };
            ChatMessages.Add(aiBubble);
            var content = new StringBuilder();
            var toolCalls = new List<ChatToolCall>();
            var errored = false;

            await foreach (var update in provider.ChatStreamingAsync(messages, _chatToolDefs, ct)
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
        var wallClockOrigin = Sources.Count > 0 ? Sources[0].WallClockOrigin : null;
        sb.AppendLine(FormattableString.Invariant($"- 绿锚: {FormatTs(_anchorTimestampSeconds, wallClockOrigin)}"));
        sb.AppendLine(FormattableString.Invariant($"- 蓝锚: {FormatTs(_blueAnchorTimestampSeconds, wallClockOrigin)}"));
        var watchCount = WatchedSignals.Count(r => !r.IsPlaceholder);
        sb.AppendLine(FormattableString.Invariant($"- watch list: {watchCount} 条信号"));
        var dbc = _dbcService.Current;
        sb.AppendLine(FormattableString.Invariant($"- DBC: {(dbc is null
            ? "未加载"
            : (string.IsNullOrEmpty(dbc.SourcePath) ? "已加载" : System.IO.Path.GetFileName(dbc.SourcePath)))}"));
        // v12 Step 4: inject DBC node list so the AI knows which ECUs are present.
        if (dbc is not null && dbc.Nodes.Count > 0)
            sb.AppendLine(FormattableString.Invariant($"- DBC 节点: {string.Join(", ", dbc.Nodes.Select(n => n.Name))}"));
        // v12: inject current playback timestamp + chart viewport so the AI
        // knows what time range the user is currently looking at.
        var currentTs = _masterService?.CurrentTimestamp ?? 0.0;
        sb.AppendLine(FormattableString.Invariant($"- 当前播放时间戳: {FormatTraceTime(currentTs, wallClockOrigin)}"));
        var viewports = ChartViewModel.CaptureViewports();
        if (viewports.Count > 0)
        {
            var vp = viewports[0];
            sb.AppendLine(FormattableString.Invariant($"- chart 视口范围: {FormatTraceTime(vp.XMin, wallClockOrigin)} ~ {FormatTraceTime(vp.XMax, wallClockOrigin)}"));
        }
        if (AutoConfirm)
            sb.AppendLine("- 静默模式: 开启（直接执行合理操作，不需要逐步反问确认）");
        sb.AppendLine();
        sb.AppendLine("时间格式约定:");
        sb.AppendLine("- 图表 X 轴与工具返回的 *_label 字段统一使用秒数（保留4位小数，如 158340.5101）");
        sb.AppendLine("- 向用户提及任何时间戳时，必须引用 *_label 字段或本消息已格式化的时间字符串，禁止自行换算成 Xd hh:mm:ss 等其他形式");
        sb.AppendLine();
        sb.AppendLine("可用工具（22 个）：");
        sb.AppendLine("发现类: search_signals, get_signal_overview, anomaly_scan");
        sb.AppendLine("查询类: get_dbc_signal, get_dbc_message, find_related_signals");
        sb.AppendLine("操作类: propose_to_watch_list, remove_from_watch_list, seek_to");
        sb.AppendLine("分析类: search_signal_trace, get_anchor_info, analyze_timing_sequence");
        sb.AppendLine("上下文类: get_trace_info, get_dbc_info");
        sb.AppendLine("组织类: create_group, add_to_group, remove_from_group, set_group_notes, set_signal_alias");
        sb.AppendLine("网络管理: get_nm_profile, get_nm_overview, get_nm_events");
        sb.AppendLine();
        sb.AppendLine(NmPromptRules.BuildM1());
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

        // UDS 离线分析 Handoff：本次对话以该报告快照为唯一证据，
        // 不允许用 trace/DBC/NM 或工具补造证据。
        if (_udsHandoff is { } handoff)
        {
            sb.AppendLine();
            sb.AppendLine("UDS 离线分析 Handoff 规则:");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- ReportId: {handoff.ReportId}");
            sb.AppendLine("- 本次问题只以该报告为证据；其它 trace/DBC/NM 数据不是证据。");
            sb.AppendLine("- 首版不新增 UDS AI 工具；只用 Handoff 提供的事实，不调用无关工具补造证据。");
            sb.AppendLine("- 事务列表不等于总线全部流量；做“没有/只有”断言前先引用报告噪声计数。");
            sb.AppendLine("- 未加载 ODX 时不得把 DID/例程原始 hex 解释为异常。");
            sb.AppendLine("- 区分日志事实、假设与不可知，不把时间相关性写成因果。");
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

    /// <summary>Construct the 22 chat tools bound to this VM as
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
            // Network management (M1)
            new GetNmProfileTool(ctx, lf.CreateLogger<GetNmProfileTool>()),
            new GetNmOverviewTool(ctx, lf.CreateLogger<GetNmOverviewTool>()),
            new GetNmEventsTool(ctx, lf.CreateLogger<GetNmEventsTool>()),
        };
    }
}
