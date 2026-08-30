using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.HIL.Core;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Nodes;

namespace PeakCan.Host.App.ViewModels.Nodes;

/// <summary>编辑区：消息表 + 规则表 + 摘要列（payload/动作细节用行摘要，多态编辑器后续迭代可加 ContentControl）。</summary>
public sealed partial class NodeEditorViewModel : ObservableObject
{
    /// <summary>周期报文行集（选中节点切换时整体重建）。</summary>
    public ObservableCollection<NodeMessageRowViewModel> Messages { get; } = new();

    /// <summary>响应规则行集（选中节点切换时整体重建）。</summary>
    public ObservableCollection<ResponseRuleRowViewModel> Rules { get; } = new();

    /// <summary>当前编辑的节点配置（null = 未选中）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorEnabled))]
    private NodeConfig? _config;

    /// <summary>编辑器可用性：选中节点且未运行（运行中节点只读——修订 12）。</summary>
    public bool EditorEnabled => Config is not null && !(_configRunning);

    private bool _configRunning;

    /// <summary>
    /// 绑定宿主服务。DbcService 留给后续 DbcSignals 编辑器（决策 2 的 DBC 绑定；本期 FixedHex 编辑足够模板闭环）。
    /// </summary>
    public void Bind(NodeHostService host, DbcService dbcService, NodeConfigLibrary library)
    {
    }

    /// <summary>选中节点切换：重建行集；运行中的节点只读（修订 12）。</summary>
    public void Select(NodeConfig? config, bool running)
    {
        Config = config;
        _configRunning = running;
        OnPropertyChanged(nameof(EditorEnabled));
        Messages.Clear();
        Rules.Clear();
        if (config is null)
            return;

        foreach (var m in config.Messages)
            Messages.Add(new NodeMessageRowViewModel(m));
        foreach (var r in config.Rules)
            Rules.Add(new ResponseRuleRowViewModel(r));
    }

    /// <summary>
    /// 选中节点运行状态翻转（由 <see cref="NodeSetupViewModel.AppendActivity"/> 在 Started/Stopped
    /// 活动且命中选中行时推送）：只刷新只读门（修订 12 的"实时"半边），不重建行集。
    /// 评审修复——原实现仅在 <see cref="Select"/>（选择变更）时计算，选中节点经行 ▶ 启停后
    /// <see cref="EditorEnabled"/> 失真（运行中仍可编辑 / 停止后仍禁用，直到重新选择）。
    /// </summary>
    public void SetRunning(bool running)
    {
        _configRunning = running;
        OnPropertyChanged(nameof(EditorEnabled));
    }

    /// <summary>消息表"标识"列文本（后端解释自己的 MessageRef 子类——决策 4）。</summary>
    /// <remarks>
    /// TP 模式经 <see cref="string.ToUpperInvariant"/> 转大写——计划原稿 <c>{mode}</c> 输出
    /// 枚举拼写（"Bam"），与钉死的测试断言 "PGN 0x0200 BAM"（行为契约，测试先行）矛盾；
    /// 以测试为准，用 Invariant 形式规避区域设置陷阱。
    /// </remarks>
    public static string DescribeRef(MessageRef refr) => refr switch
    {
        J1939MessageRef j => j.Mode is { } mode
            ? $"PGN 0x{j.Pgn:X4} {mode.ToString().ToUpperInvariant()}"
            : $"PGN 0x{j.Pgn:X4}",
        CanMessageRef c => $"ID 0x{c.Id:X}{(c.IsExtended ? " ext" : "")}",
        _ => "?",
    };

    /// <summary>消息表"来源"列文本（载荷来源联合的多态摘要）。</summary>
    public static string DescribePayload(NodePayloadSource payload) => payload switch
    {
        FixedHexSource hex => $"Fixed {hex.Hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length}B",
        DbcSignalsSource dbc => $"DBC({dbc.MessageName})",
        ScriptCallbackSource script => $"Script({script.CallbackRef})",
        _ => "?",
    };

    /// <summary>规则表"动作"列文本（动作联合的多态摘要；send/start/stop 复用 <see cref="DescribeRef"/>）。</summary>
    public static string DescribeAction(NodeAction action) => action switch
    {
        SendMessageAction a => $"send {DescribeRef(a.Ref)}",
        SetSignalAction a => $"set {a.MessageName}.{a.SignalName}={a.Value}",
        StartMessageAction a => $"start {DescribeRef(a.Ref)}",
        StopMessageAction a => $"stop {DescribeRef(a.Ref)}",
        ScriptAction a => $"script {a.ScriptRef}",
        _ => "?",
    };
}

/// <summary>消息表行 VM（只读投影：标识/周期/来源/摘要；编辑能力后续迭代再加）。</summary>
public sealed class NodeMessageRowViewModel
{
    /// <summary>标识列（<see cref="NodeEditorViewModel.DescribeRef"/> 解释后端引用）。</summary>
    public string Identifier => NodeEditorViewModel.DescribeRef(Model.Ref);

    /// <summary>周期（毫秒）。</summary>
    public int IntervalMs => Model.IntervalMs;

    /// <summary>来源列（<see cref="NodeEditorViewModel.DescribePayload"/> 解释载荷来源）。</summary>
    public string Source => NodeEditorViewModel.DescribePayload(Model.Payload);

    /// <summary>单行摘要（标识 + 周期 + 来源）。</summary>
    public string Summary => $"{Identifier} {IntervalMs}ms {Source}";

    /// <summary>底层配置行（后续编辑器迭代使用）。</summary>
    public NodeMessage Model { get; }

    public NodeMessageRowViewModel(NodeMessage model) => Model = model;
}

/// <summary>规则表行 VM（只读投影：触发/条件/动作/延迟/摘要）。</summary>
public sealed class ResponseRuleRowViewModel
{
    /// <summary>触发列（<see cref="NodeEditorViewModel.DescribeRef"/> 解释触发引用）。</summary>
    public string Trigger => NodeEditorViewModel.DescribeRef(Model.Trigger);

    /// <summary>条件列（字节模式 <c>[offset]&amp;mask==value</c>；无条件为空串）。</summary>
    public string Condition => Model.Condition is { } c ? $"[{c.Offset}]&0x{c.Mask:X2}==0x{c.Value:X2}" : "";

    /// <summary>动作列（<see cref="NodeEditorViewModel.DescribeAction"/> 解释动作）。</summary>
    public string Action => NodeEditorViewModel.DescribeAction(Model.Action);

    /// <summary>响应延迟（毫秒）。</summary>
    public int DelayMs => Model.DelayMs;

    /// <summary>单行摘要（触发 条件 → 动作 (延迟)）。</summary>
    public string Summary => $"{Trigger} {Condition} → {Action} ({DelayMs}ms)";

    /// <summary>底层规则行（后续编辑器迭代使用）。</summary>
    public ResponseRule Model { get; }

    public ResponseRuleRowViewModel(ResponseRule model) => Model = model;
}
