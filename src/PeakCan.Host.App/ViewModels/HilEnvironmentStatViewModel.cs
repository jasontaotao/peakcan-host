namespace PeakCan.Host.App.ViewModels;

/// <summary>M2 gap: 环境节点运行状态行（spec §5.5 NodeRunStats 的 UI 投影）。</summary>
public sealed class HilEnvironmentStatViewModel
{
    public string NodeName { get; init; } = "";
    public long FramesSent { get; init; }
    public long RulesMatched { get; init; }
    public long UdsResponses { get; init; }
}
