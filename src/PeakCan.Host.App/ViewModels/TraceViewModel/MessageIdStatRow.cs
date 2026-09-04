using CommunityToolkit.Mvvm.ComponentModel;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// 2026-08-31 P4：统计面板行（spec §5.7）。由 <see cref="TraceViewModel.RefreshStats"/>
/// 从 <see cref="TraceViewModel.GetMessageIdStats"/> 结果重建；<see cref="DbcName"/>
/// 刷新时经 DBC 解析，无则空。<see cref="IdHex"/> 为 <c>0x</c>-前缀裸 ID，供
/// <see cref="TraceViewModel.SetFilterToIdCommand"/> 回写过滤字段。
/// </summary>
public sealed partial class MessageIdStatRow : ObservableObject
{
    /// <summary>消息 ID 显示（0x 前缀裸 ID）。</summary>
    public string IdHex { get; init; } = "";

    /// <summary>DBC 消息名（刷新时解析；无则 null/空）。</summary>
    [ObservableProperty]
    private string? _dbcName;

    /// <summary>帧计数。</summary>
    public long Count { get; init; }

    /// <summary>占总帧数百分比（0..100）。</summary>
    public double Percent { get; init; }
}
