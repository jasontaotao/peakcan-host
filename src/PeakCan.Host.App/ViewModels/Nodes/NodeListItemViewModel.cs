using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Services.Nodes;

namespace PeakCan.Host.App.ViewModels.Nodes;

/// <summary>节点行（后端无关列：名称/身份/状态/启停；身份文本由后端身份子类解释——决策 4）。</summary>
public sealed partial class NodeListItemViewModel : ObservableObject
{
    private readonly NodeHostService _host;

    /// <summary>节点名（来自 <see cref="Config"/>）。</summary>
    public string Name => Config.Name;

    /// <summary>可选分组标签（如 gbt27930）。</summary>
    public string? Tag => Config.Tag;

    /// <summary>节点配置（编辑区经 <see cref="NodeSetupViewModel.OnSelectedNodeChanged"/> 取用）。</summary>
    public NodeConfig Config { get; }

    /// <summary>是否运行中（由 Activity Started/Stopped 事件驱动刷新——决策 3 首选路径）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartStopLabel))]
    private bool _isRunning;

    /// <summary>身份列文本（J1939 后端显示 SA；后端无关列契约——决策 4）。</summary>
    public string IdentityDisplay => Config.Identity switch
    {
        J1939NodeIdentity j => $"SA 0x{j.Sa:X2}",
        _ => "?",
    };

    /// <summary>启停按钮标签（运行中显示停止，未运行显示启动）。</summary>
    public string StartStopLabel => IsRunning ? "■" : "▶";

    /// <summary>启停命令（StartNode/StopNode 幂等；SA 冲突等失败经 Activity Error 路径呈现）。</summary>
    public IRelayCommand StartStopCommand { get; }

    public NodeListItemViewModel(NodeHostService host, SimulatedNode node)
    {
        _host = host;
        Config = node.Config;
        _isRunning = node.IsRunning;
        StartStopCommand = new RelayCommand(Toggle);
    }

    private void Toggle()
    {
        if (IsRunning)
            _host.StopNode(Config.Name);
        else
            _host.StartNode(Config.Name);
    }
}
