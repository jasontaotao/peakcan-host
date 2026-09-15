using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core.Analysis;
using PeakCan.HIL.Core.Analysis.Chat;
using PeakCan.Host.Mobile.Core.Chat;
using PeakCan.Host.Mobile.Core.Chat.Tools;
using PeakCan.Host.Mobile.Core.Platform;

namespace PeakCan.Host.Mobile.Core.ViewModels;

/// <summary>
/// AI chat panel logic for the mobile trace viewer: message list +
/// send/clear + the multi-round tool-calling loop (port of the desktop
/// <c>ChatFlow</c>). The provider is resolved lazily from
/// <see cref="CurrentProvider"/> (set by the settings partial class in
/// <c>ChatViewModel.Settings.cs</c>); when none is configured the send is a
/// no-op that surfaces <see cref="ConnectionHint"/>.
/// </summary>
public sealed partial class ChatViewModel : ObservableObject
{
    /// <summary>Max provider rounds per user message (aligned with desktop).</summary>
    private const int ChatMaxRounds = 12;

    private readonly IMobileChatToolContext _context;
    private readonly IChatProviderFactory _providerFactory;
    private readonly ILogger _logger;

    /// <summary>Cross-round LLM message history (system prompt rebuilt per
    /// send; this list holds user/assistant/tool turns).</summary>
    private readonly List<ChatMessage> _chatHistory = new();

    private readonly IReadOnlyList<IChatTool> _chatTools;
    private readonly IReadOnlyList<ChatToolDefinition> _chatToolDefs;

    /// <summary>Chat lifecycle CancellationTokenSource. Cancelled on Dispose
    /// so in-flight chat HTTP requests are aborted when the page closes.</summary>
    private CancellationTokenSource? _chatCts;

    /// <summary>Marshals message-list mutations back to the UI thread. Null in
    /// unit tests (mutations run inline on the test thread).</summary>
    private readonly IUiDispatcher? _ui;

    /// <summary>Run <paramref name="action"/> on the UI thread.</summary>
    private void RunOnUi(Action action)
    {
        if (_ui is null) action();
        else _ui.Post(action);
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private string _chatInput = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearChatCommand))]
    private bool _isChatBusy;

    /// <summary>UI hint for the unconfigured case ("请先在设置中配置 API Key").</summary>
    [ObservableProperty]
    private string _connectionHint = "";

    public ObservableCollection<ChatMessageViewModel> ChatMessages { get; } = new();

    /// <summary>True when no messages yet (show welcome suggestions).</summary>
    public bool HasMessages => ChatMessages.Count > 0;

    /// <summary>Inverse of <see cref="HasMessages"/>, for XAML empty-state visibility.</summary>
    public bool HasNoMessages => ChatMessages.Count == 0;

    public ChatViewModel(
        IMobileChatToolContext context,
        IChatProviderFactory providerFactory,
        ILogger? logger = null,
        IReadOnlyList<IChatTool>? chatTools = null,
        ICredentialStore? credentialStore = null,
        IChatConfigStore? configStore = null,
        IChatConnectionTester? connectionTester = null,
        IUiDispatcher? uiDispatcher = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _logger = logger ?? NullLogger.Instance;
        _ui = uiDispatcher;
        _credentialStore = credentialStore;
        _configStore = configStore;
        _connectionTester = connectionTester;
        _chatTools = chatTools ?? BuildChatTools(_context, _logger);
        _chatToolDefs = _chatTools.Select(t => t.Definition).ToList();
        ChatMessages.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasMessages));
            OnPropertyChanged(nameof(HasNoMessages));
        };
    }

    /// <summary>Current provider set by the settings flow; null when no key is configured.</summary>
    internal IChatProvider? CurrentProvider { get; private set; }

    /// <summary>Credential key behind <see cref="CurrentProvider"/> (null when
    /// unconfigured); used to restore the active marker on key-list reloads.</summary>
    internal string? CurrentCredentialKey { get; private set; }

    /// <summary>Set/clear the active provider (called by the settings partial).
    /// <paramref name="credentialKey"/> records which saved key is active.</summary>
    internal void SetProvider(IChatProvider? provider, string? credentialKey = null)
    {
        CurrentProvider = provider;
        CurrentCredentialKey = provider is null ? null : credentialKey;
        if (provider is not null) ConnectionHint = "";
    }

    /// <summary>The chat tools exposed to the provider (unit-testable via ctor injection).</summary>
    internal IReadOnlyList<IChatTool> ChatTools => _chatTools;

    /// <summary>Construct the 8 mobile chat tools bound to the session context
    /// (production path; tests inject fakes via ctor instead).</summary>
    private static IReadOnlyList<IChatTool> BuildChatTools(IMobileChatToolContext context, ILogger logger)
    {
        return new IChatTool[]
        {
            new GetTraceInfoTool(context, logger),
            new GetDbcInfoTool(context, logger),
            new SearchSignalsTool(context, logger),
            new GetDbcSignalTool(context, logger),
            new GetDbcMessageTool(context, logger),
            new GetAnchorValuesTool(context, logger),
            new SearchSignalTraceTool(context, logger),
            new SeekToTimeTool(context, logger),
        };
    }

    /// <summary>Cancel any in-flight provider round. Called by ChatPage when
    /// the page closes so a popped page does not keep streaming tokens.</summary>
    public void Dispose()
    {
        _chatCts?.Cancel();
        _chatCts?.Dispose();
        _chatCts = null;
    }

    private bool CanSendChat() => !IsChatBusy && !string.IsNullOrWhiteSpace(ChatInput);

    [RelayCommand(CanExecute = nameof(CanSendChat))]
    private async Task SendMessageAsync()
    {
        if (CurrentProvider is null)
        {
            ConnectionHint = "请先在设置中配置 API Key";
            return;
        }
        ConnectionHint = "";
        var userText = ChatInput.Trim();
        ChatInput = "";

        _chatCts ??= new CancellationTokenSource();
        var ct = _chatCts.Token;
        IsChatBusy = true;
        try
        {
            // Android 上 HttpClient 走 Java 栈，主线程收发网络包会抛
            // NetworkOnMainThreadException —— 整个聊天循环放到线程池跑，
            // 消息列表变更经 IUiDispatcher 调度回 UI 线程。
            await Task.Run(() => RunChatLoopAsync(userText, ct)).ConfigureAwait(true);
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

    private bool CanClearChat() => !IsChatBusy;

    [RelayCommand(CanExecute = nameof(CanClearChat))]
    private void ClearChat()
    {
        _chatHistory.Clear();
        ChatMessages.Clear();
    }

    private async Task RunChatLoopAsync(string userText, CancellationToken ct)
    {
        _chatHistory.Add(new ChatMessage("user", userText, null, null));
        RunOnUi(() => ChatMessages.Add(new ChatMessageViewModel("user", userText)));

        var provider = CurrentProvider;
        if (provider is null)
        {
            RunOnUi(() => ChatMessages.Add(new ChatMessageViewModel("assistant", "[出错: 聊天 Provider 未配置]")));
            return;
        }

        for (int round = 0; round < ChatMaxRounds; round++)
        {
            var messages = new List<ChatMessage> { BuildSystemMessage() };
            messages.AddRange(_chatHistory);

            var aiBubble = new ChatMessageViewModel("assistant") { IsStreaming = true };
            RunOnUi(() => ChatMessages.Add(aiBubble));
            var content = new StringBuilder();
            var toolCalls = new List<ChatToolCall>();
            var errored = false;

            await foreach (var update in provider.ChatStreamingAsync(messages, _chatToolDefs, ct)
                              .ConfigureAwait(false))
            {
                switch (update)
                {
                    case ChatUpdate.PartialDelta d:
                        content.Append(d.Text);
                        RunOnUi(() => aiBubble.Content += d.Text);
                        break;
                    case ChatUpdate.ToolCallStart s:
                        _logger.LogDebug("Chat tool call started: {ToolName}", s.Name);
                        break;
                    case ChatUpdate.ToolCallArgDelta:
                        break;
                    case ChatUpdate.ToolCallRoundDone r:
                        toolCalls = r.ToolCalls.ToList();
                        break;
                    case ChatUpdate.Error e:
                        RunOnUi(() => aiBubble.Content += $"\n[错误: {e.Message}]");
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

            RunOnUi(() => aiBubble.IsStreaming = false);
            if (errored) return;

            if (toolCalls.Count == 0)
            {
                _chatHistory.Add(new ChatMessage("assistant", content.ToString(), null, null));
                return; // assistant replied with text - turn complete
            }

            // Assistant requested tools - record the assistant turn + execute sequentially.
            // content=null (not "") when empty, per OpenAI/DeepSeek spec.
            _chatHistory.Add(new ChatMessage(
                "assistant", content.Length == 0 ? null : content.ToString(), toolCalls, null));

            var toolLog = new ChatMessageViewModel("tool_log");
            var results = new string?[toolCalls.Count];
            for (int i = 0; i < toolCalls.Count; i++)
            {
                var tc = toolCalls[i];
                var tool = _chatTools.FirstOrDefault(t => t.Name == tc.FunctionName);
                results[i] = tool is null
                    ? $"{{\"error\":\"unknown tool: {tc.FunctionName}\"}}"
                    : await ExecuteToolOnUiAsync(tool, tc.FunctionArgs, ct).ConfigureAwait(false);
            }

            for (int i = 0; i < toolCalls.Count; i++)
            {
                var entry = new ToolCallEntry(toolCalls[i].FunctionName, results[i]!);
                RunOnUi(() => toolLog.Tools.Add(entry));
                _chatHistory.Add(new ChatMessage("tool", results[i], null, toolCalls[i].Id));
            }
            RunOnUi(() => ChatMessages.Add(toolLog));
            // loop continues - next round the assistant replies to the tool results
        }

        // MaxRounds exhausted. Write to both UI and history so the LLM sees
        // the hint on the next user turn.
        const string maxRoundsMsg = "[达到最大轮数上限，请发新消息继续]";
        var maxRoundsBubble = new ChatMessageViewModel("assistant", maxRoundsMsg);
        RunOnUi(() => ChatMessages.Add(maxRoundsBubble));
        _chatHistory.Add(new ChatMessage("assistant", maxRoundsMsg, null, null));
    }

    /// <summary>Executes a chat tool on the UI thread — tools touch session
    /// state (player/seek/DBC) that is owned by the UI thread.</summary>
    private Task<string> ExecuteToolOnUiAsync(IChatTool tool, string argsJson, CancellationToken ct)
    {
        if (_ui is null) return tool.ExecuteAsync(argsJson, ct);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(async () =>
        {
            try { tcs.SetResult(await tool.ExecuteAsync(argsJson, ct).ConfigureAwait(false)); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    private ChatMessage BuildSystemMessage()
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是一个汽车 CAN 总线故障诊断专家。");
        sb.AppendLine();
        sb.AppendLine("当前 trace 状态:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 锚点: {(_context.AnchorTimestamp is { } a
            ? a.ToString("F6", CultureInfo.InvariantCulture) + "s"
            : "未设")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- DBC: {(_context.Dbc is null ? "未加载" : _context.Dbc.SourceName)}");
        sb.AppendLine(_context.IsDurationKnown && _context.DurationSeconds is { } d
            ? $"- 时长: {d.ToString("F3", CultureInfo.InvariantCulture)}s"
            : "- 时长: 未知");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 当前播放时间: {FormatTs(_context.CurrentTimestamp)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- ID 过滤: {(_context.FilterText is { Length: > 0 } f ? f : "无")}");
        sb.AppendLine();
        sb.AppendLine("时间格式约定:");
        sb.AppendLine("- 时间戳统一用秒数（保留4位小数，如 158340.5101），不要换算成其他形式");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"可用工具（{_chatTools.Count} 个）: {string.Join(", ", _chatTools.Select(t => t.Name))}");
        sb.AppendLine();
        sb.AppendLine("分析原则:");
        sb.AppendLine("1. 信息不足时问用户，不编造");
        sb.AppendLine("2. 引用数据时给出具体数值");
        return new ChatMessage("system", sb.ToString(), null, null);
    }

    private static string FormatTs(double ts)
        => double.IsNaN(ts) ? "未播放" : ts.ToString("F4", CultureInfo.InvariantCulture) + "s";
}
