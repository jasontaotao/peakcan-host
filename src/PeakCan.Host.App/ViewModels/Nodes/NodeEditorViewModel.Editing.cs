using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.HIL.Core.J1939;
using PeakCan.Host.App.Services.Nodes;

namespace PeakCan.Host.App.ViewModels.Nodes;

/// <summary>
/// 消息表行 VM：只读摘要列（Identifier/Summary，后端解释收在本层——决策 4）
/// + 可编辑字段（详情编辑器经 <see cref="NodeMessageRowViewModel"/> 双向绑定，ApplyConfig 组装回写）。
/// 载荷多态经 <see cref="PayloadKindIndex"/> + 三个专属字段承载。
/// </summary>
public sealed partial class NodeMessageRowViewModel : ObservableObject
{
    public NodeMessage Model { get; }

    /// <summary>标识列（只读摘要；ApplyConfig 成功后行集重建刷新）。</summary>
    public string Identifier => NodeEditorViewModel.DescribeRef(Model.Ref);

    /// <summary>单行摘要（只读）。</summary>
    public string Summary => $"{Identifier} {IntervalMsText}ms {NodeEditorViewModel.DescribePayload(Model.Payload)}";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _pgnHex = string.Empty;

    [ObservableProperty]
    private string _priorityText = "6";

    /// <summary>目标地址 hex（空串 = null：PDU2 广播/无 DA）。</summary>
    [ObservableProperty]
    private string _daHex = string.Empty;

    /// <summary>发送模式：0 Single / 1 Bam / 2 RtsCts（消息必填，默认 0）。</summary>
    [ObservableProperty]
    private int _modeIndex;

    [ObservableProperty]
    private string _intervalMsText = string.Empty;

    [ObservableProperty]
    private bool _enabled = true;

    /// <summary>载荷来源：0 FixedHex / 1 DbcSignals / 2 Script。</summary>
    [ObservableProperty]
    private int _payloadKindIndex;

    [ObservableProperty]
    private string _payloadHexText = string.Empty;

    [ObservableProperty]
    private string _payloadDbcMessageName = string.Empty;

    [ObservableProperty]
    private string _payloadScriptRefText = string.Empty;

    /// <summary>从模型装载（Select 重建行集 / ApplyConfig 成功后刷新共用）。</summary>
    public NodeMessageRowViewModel(NodeMessage model)
    {
        Model = model;
        LoadFrom(model);
    }

    /// <summary>空白新行（AddMessage：PGN 空、Single、FixedHex、默认优先级 6 与 1 间隔占位）。</summary>
    public NodeMessageRowViewModel()
        : this(new NodeMessage(new J1939MessageRef(0, 6, TpMode.Single, null, null), 100, new FixedHexSource("AA"), true))
    {
        // LoadFrom 后覆写：新行要求空字段（否则用户须先理解默认 0x0000 PGN）
        PgnHex = string.Empty;
        IntervalMsText = string.Empty;
        PayloadHexText = string.Empty;
    }

    private void LoadFrom(NodeMessage m)
    {
        if (m.Ref is J1939MessageRef j)
        {
            PgnHex = j.Pgn.ToString("X", CultureInfo.InvariantCulture);
            PriorityText = j.Priority.ToString(CultureInfo.InvariantCulture);
            DaHex = j.Da?.ToString("X2", CultureInfo.InvariantCulture) ?? string.Empty;
            ModeIndex = j.Mode switch
            {
                TpMode.Bam => 1,
                TpMode.RtsCts => 2,
                _ => 0,   // Single 与 null 均按单帧（消息发送必选模式，默认 0）
            };
        }
        IntervalMsText = m.IntervalMs.ToString(CultureInfo.InvariantCulture);
        Enabled = m.Enabled;
        switch (m.Payload)
        {
            case FixedHexSource h: PayloadKindIndex = 0; PayloadHexText = h.Hex; break;
            case DbcSignalsSource d: PayloadKindIndex = 1; PayloadDbcMessageName = d.MessageName; break;
            case ScriptCallbackSource s: PayloadKindIndex = 2; PayloadScriptRefText = s.CallbackRef; break;
        }
    }
}

/// <summary>
/// 规则表行 VM：只读摘要列 + 可编辑字段（Trigger 引用 / 条件字节模式 / 动作多态 / 延迟）。
/// 动作多态经 <see cref="ActionKindIndex"/>（0 send / 1 setSignal / 2 start / 3 stop / 4 script）
/// 分派到四组字段（send 含嵌套载荷多态）。
/// </summary>
public sealed partial class ResponseRuleRowViewModel : ObservableObject
{
    public ResponseRule Model { get; }

    /// <summary>触发列（只读摘要）。</summary>
    public string Trigger => NodeEditorViewModel.DescribeRef(Model.Trigger);

    /// <summary>条件列（字节模式 [offset]&amp;mask==value；无条件为空串）。</summary>
    public string Condition => Model.Condition is { } c ? $"[{c.Offset}]&0x{c.Mask:X2}==0x{c.Value:X2}" : string.Empty;

    /// <summary>动作列（只读摘要）。</summary>
    public string Action => NodeEditorViewModel.DescribeAction(Model.Action);

    /// <summary>单行摘要（只读）。</summary>
    public string Summary => $"{Trigger} {Condition} → {Action} ({DelayMsText}ms)";

    [ObservableProperty]
    private bool _isSelected;

    // Trigger 引用字段（TriggerModeIndex：0=通配/1=Single/2=Bam/3=RtsCts——Matcher 宽容语义）
    [ObservableProperty]
    private string _triggerPgnHex = string.Empty;

    [ObservableProperty]
    private string _triggerPriorityText = "6";

    [ObservableProperty]
    private string _triggerDaHex = string.Empty;

    [ObservableProperty]
    private int _triggerModeIndex;

    // 条件字节模式（三字段全空 = 无条件）
    [ObservableProperty]
    private string _conditionOffsetText = string.Empty;

    [ObservableProperty]
    private string _conditionMaskHex = string.Empty;

    [ObservableProperty]
    private string _conditionValueHex = string.Empty;

    // 动作多态
    [ObservableProperty]
    private int _actionKindIndex;

    // send/start/stop 共用引用字段
    [ObservableProperty]
    private string _actionRefPgnHex = string.Empty;

    [ObservableProperty]
    private string _actionRefPriorityText = "6";

    [ObservableProperty]
    private string _actionRefDaHex = string.Empty;

    [ObservableProperty]
    private int _actionRefModeIndex;

    // send 专属：动作载荷多态（同消息载荷三字段）
    [ObservableProperty]
    private int _actionPayloadKindIndex;

    [ObservableProperty]
    private string _actionPayloadHexText = string.Empty;

    [ObservableProperty]
    private string _actionPayloadDbcMessageName = string.Empty;

    [ObservableProperty]
    private string _actionPayloadScriptRefText = string.Empty;

    // setSignal 专属
    [ObservableProperty]
    private string _actionSignalMessageName = string.Empty;

    [ObservableProperty]
    private string _actionSignalName = string.Empty;

    [ObservableProperty]
    private string _actionSignalValueText = "0";

    // script 专属
    [ObservableProperty]
    private string _actionScriptRefText = string.Empty;

    [ObservableProperty]
    private string _delayMsText = "0";

    public ResponseRuleRowViewModel(ResponseRule model)
    {
        Model = model;
        LoadFrom(model);
    }

    /// <summary>空白新规则（AddRule：Trigger 空、send 动作、无条件、无延迟）。</summary>
    public ResponseRuleRowViewModel()
        : this(new ResponseRule(new J1939MessageRef(0, 6, null, null, null), null, new SendMessageAction(
            new J1939MessageRef(0, 6, TpMode.Single, null, null), new FixedHexSource("AA")), 0))
    {
        TriggerPgnHex = string.Empty;
        ActionRefPgnHex = string.Empty;
        ActionPayloadHexText = string.Empty;
    }

    private void LoadFrom(ResponseRule r)
    {
        if (r.Trigger is J1939MessageRef t)
        {
            TriggerPgnHex = t.Pgn.ToString("X", CultureInfo.InvariantCulture);
            TriggerPriorityText = t.Priority.ToString(CultureInfo.InvariantCulture);
            TriggerDaHex = t.Da?.ToString("X2", CultureInfo.InvariantCulture) ?? string.Empty;
            TriggerModeIndex = t.Mode switch
            {
                TpMode.Single => 1,
                TpMode.Bam => 2,
                TpMode.RtsCts => 3,
                _ => 0,   // 通配
            };
        }
        if (r.Condition is { } c)
        {
            ConditionOffsetText = c.Offset.ToString(CultureInfo.InvariantCulture);
            ConditionMaskHex = c.Mask.ToString("X2", CultureInfo.InvariantCulture);
            ConditionValueHex = c.Value.ToString("X2", CultureInfo.InvariantCulture);
        }
        DelayMsText = r.DelayMs.ToString(CultureInfo.InvariantCulture);
        LoadAction(r.Action);
    }

    private void LoadAction(NodeAction action)
    {
        switch (action)
        {
            case SendMessageAction send:
                ActionKindIndex = 0;
                if (send.Ref is J1939MessageRef jr) LoadRefFields(jr);
                LoadPayload(send.Payload);
                break;
            case SetSignalAction set:
                ActionKindIndex = 1;
                ActionSignalMessageName = set.MessageName;
                ActionSignalName = set.SignalName;
                ActionSignalValueText = set.Value.ToString(CultureInfo.InvariantCulture);
                break;
            case StartMessageAction start:
                ActionKindIndex = 2;
                if (start.Ref is J1939MessageRef jr2) LoadRefFields(jr2);
                break;
            case StopMessageAction stop:
                ActionKindIndex = 3;
                if (stop.Ref is J1939MessageRef jr3) LoadRefFields(jr3);
                break;
            case ScriptAction script:
                ActionKindIndex = 4;
                ActionScriptRefText = script.ScriptRef;
                break;
        }
    }

    private void LoadRefFields(J1939MessageRef j)
    {
        ActionRefPgnHex = j.Pgn.ToString("X", CultureInfo.InvariantCulture);
        ActionRefPriorityText = j.Priority.ToString(CultureInfo.InvariantCulture);
        ActionRefDaHex = j.Da?.ToString("X2", CultureInfo.InvariantCulture) ?? string.Empty;
        ActionRefModeIndex = j.Mode switch
        {
            TpMode.Bam => 1,
            TpMode.RtsCts => 2,
            _ => 0,
        };
    }

    private void LoadPayload(NodePayloadSource payload)
    {
        switch (payload)
        {
            case FixedHexSource h: ActionPayloadKindIndex = 0; ActionPayloadHexText = h.Hex; break;
            case DbcSignalsSource d: ActionPayloadKindIndex = 1; ActionPayloadDbcMessageName = d.MessageName; break;
            case ScriptCallbackSource s: ActionPayloadKindIndex = 2; ActionPayloadScriptRefText = s.CallbackRef; break;
        }
    }
}