using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.HIL.Core;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Nodes;

namespace PeakCan.Host.App.ViewModels.Nodes;

/// <summary>
/// 编辑区宿主：节点身份表单 + 消息表 + 规则表（可编辑行）+ 多态详情编辑
/// （spec §10.2 决策 1：行内只显示摘要，选中行后在下方详情编辑器编辑；plan:5645 的 deferred 项）。
/// 提交语义（用户决策）：ApplyConfig 组装新 <see cref="NodeConfig"/>（不可变 record）→
/// <see cref="NodeHostService.UpdateNode"/> 生效；持久化仍走"另存为…"（NodeSetupViewModel.Save）。
/// 运行中节点只读（plan 修订 12），命令与组装均受 <see cref="EditorEnabled"/> 门保护。
/// </summary>
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
    public bool EditorEnabled => Config is not null && !_configRunning;

    private bool _configRunning;

    /// <summary>节点名称（身份表单；也是持久化文件名）。</summary>
    [ObservableProperty]
    private string _nodeName = string.Empty;

    /// <summary>节点 J1939 源地址（hex，可带 0x 前缀；默认 0x11 与 NewNode 一致）。</summary>
    [ObservableProperty]
    private string _nodeSaHex = "11";

    /// <summary>选中的消息行（与规则行互斥）。</summary>
    [ObservableProperty]
    private NodeMessageRowViewModel? _selectedMessage;

    /// <summary>选中的规则行（与消息行互斥）。</summary>
    [ObservableProperty]
    private ResponseRuleRowViewModel? _selectedRule;

    /// <summary>ApplyConfig 结果文本（生效/拒绝原因直显）。</summary>
    [ObservableProperty]
    private string _applyStatus = string.Empty;

    private NodeHostService? _host;
    private DbcService? _dbcService;
    private NodeConfigLibrary? _library;

    /// <summary>ApplyConfig 成功后触发（NodeSetupViewModel 订阅 → RefreshFromHost + 按新名重选）。</summary>
    public event Action<NodeConfig>? ConfigApplied;

    /// <summary>
    /// 绑定宿主服务。原空实现（plan:5661 注释"DbcService 留给后续 DbcSignals 编辑器"）——
    /// 本期消费 host（UpdateNode 生效）+ library（字典信息未用，保留注入位）。
    /// </summary>
    public void Bind(NodeHostService host, DbcService dbcService, NodeConfigLibrary library)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _dbcService = dbcService ?? throw new ArgumentNullException(nameof(dbcService));
        _library = library ?? throw new ArgumentNullException(nameof(library));
    }

    partial void OnSelectedMessageChanged(NodeMessageRowViewModel? value)
    {
        if (value is not null) SelectedRule = null;   // 互斥：选消息清规则
    }

    partial void OnSelectedRuleChanged(ResponseRuleRowViewModel? value)
    {
        if (value is not null) SelectedMessage = null;   // 互斥：选规则清消息
    }

    /// <summary>选中节点切换：重建行集并装载身份表单；运行中的节点只读（修订 12）。</summary>
    public void Select(NodeConfig? config, bool running)
    {
        Config = config;
        _configRunning = running;
        OnPropertyChanged(nameof(EditorEnabled));
        Messages.Clear();
        Rules.Clear();
        SelectedMessage = null;
        SelectedRule = null;
        ApplyStatus = string.Empty;
        if (config is null)
        {
            NodeName = string.Empty;
            NodeSaHex = "11";
            return;
        }

        NodeName = config.Name;
        if (config.Identity is J1939NodeIdentity j)
            NodeSaHex = j.Sa.ToString("X2");
        foreach (var m in config.Messages)
            Messages.Add(new NodeMessageRowViewModel(m));
        foreach (var r in config.Rules)
            Rules.Add(new ResponseRuleRowViewModel(r));
    }

    /// <summary>
    /// 选中节点运行状态翻转（由 <see cref="NodeSetupViewModel.AppendActivity"/> 在 Started/Stopped
    /// 活动且命中选中行时推送）：只刷新只读门（修订 12 的"实时"半边），不重建行集。
    /// </summary>
    public void SetRunning(bool running)
    {
        _configRunning = running;
        OnPropertyChanged(nameof(EditorEnabled));
    }

    /// <summary>消息表"标识"列文本（后端解释自己的 MessageRef 子类——决策 4）。</summary>
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

    // ---- 编辑命令 ----

    [RelayCommand]
    private void AddMessage() => Messages.Add(new NodeMessageRowViewModel());

    [RelayCommand]
    private void DeleteMessage()
    {
        if (SelectedMessage is null) return;
        Messages.Remove(SelectedMessage);
        SelectedMessage = null;   // 选中收敛（同 NodeSetupViewModel 删除后收敛原则）
    }

    [RelayCommand]
    private void AddRule() => Rules.Add(new ResponseRuleRowViewModel());

    [RelayCommand]
    private void DeleteRule()
    {
        if (SelectedRule is null) return;
        Rules.Remove(SelectedRule);
        SelectedRule = null;
    }

    /// <summary>
    /// 组装新配置（纯校验，不经 host——测试与 ApplyConfig 共用；成功返回新 record）。
    /// </summary>
    public NodeConfig? AssembleConfig(out string? error)
        => NodeConfigAssembler.TryAssemble(NodeName, NodeSaHex, Messages, Rules, Config, out var config, out error) ? config : null;

    /// <summary>
    /// 提交：组装 → <see cref="NodeHostService.UpdateNode"/> 生效 → 行集重建 + 事件通知。
    /// 失败（运行中 / 组装校验）直显 <see cref="ApplyStatus"/>，不产生活动日志（host 拒绝经
    /// 硬 Result 返回；UI 门已挡住运行中编辑，此为双保险）。
    /// </summary>
    [RelayCommand]
    private void ApplyConfig()
    {
        if (_host is null || Config is null)
        {
            ApplyStatus = "未选中节点";
            return;
        }
        if (_configRunning)
        {
            ApplyStatus = "节点运行中，请先停止后再应用";
            return;
        }

        var config = AssembleConfig(out var error);
        if (config is null)
        {
            ApplyStatus = error ?? "配置无效";
            return;
        }

        var currentName = Config.Name;   // 装载旧名（改名场景 key 随新名切换）
        var result = _host.UpdateNode(currentName, config);
        if (!result.IsSuccess)
        {
            ApplyStatus = result.Error?.Message ?? "配置应用失败";
            return;
        }

        // 生效：以新配置重建行集（选中保持当前行索引；实现取同名或首行，够用）
        Select(config, running: false);
        ApplyStatus = $"已应用（{config.Messages.Count} 条报文 / {config.Rules.Count} 条规则）";
        ConfigApplied?.Invoke(config);
    }
}