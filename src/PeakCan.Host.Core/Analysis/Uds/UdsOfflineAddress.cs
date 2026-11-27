using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>
/// The request/response pair the offline analysis is scoped to.
/// A frame "matches" only when channel, addressing format and id all agree —
/// an id match on another channel is a different bus, not the same ECU.
/// </summary>
/// <param name="FunctionalId">
/// 功能寻址（广播）CAN ID，如 <c>0x7DF</c>。为 null 表示这份日志不按功能寻址解析。
/// <para>
/// <b>为什么必须单独建模：</b>功能广播是<b>测试仪发出的请求</b>，ECU 在各自的物理
/// 响应 ID 上回答。只认物理请求 ID 时，广播请求会被判成背景流量丢掉，而它引起的
/// 响应却因为 ID 匹配被收下——于是凭空多出几条「孤立响应」，报告还说不清来源。
/// 真实日志 <c>150635_log.asc</c> 里正是这样多出三条。
/// </para>
/// </param>
public sealed record UdsOfflineAddress(
    ushort Channel,
    uint RequestId,
    uint ResponseId,
    bool IsExtended,
    uint? FunctionalId = null)
{
    /// <summary>
    /// 是否为功能寻址帧。功能寻址用<b>与物理寻址相同的响应 ID</b>，
    /// 因此响应侧不需要额外配置。
    /// </summary>
    public bool IsFunctional(ReplayFrame f)
        => FunctionalId is { } fid
           && f.Channel == Channel && f.IsExtended == IsExtended && f.Id == fid;

    public bool Matches(ReplayFrame f)
        => IsFunctional(f)
           || (f.Channel == Channel && f.IsExtended == IsExtended
               && (f.Id == RequestId || f.Id == ResponseId));

    public UdsDirectionRole RoleOf(ReplayFrame f)
        => f.Id == RequestId || IsFunctional(f) ? UdsDirectionRole.Request : UdsDirectionRole.Response;

    /// <summary>Customer-facing text used by the offline log analysis page.</summary>
    public string Display
        => $"ch{Channel}  0x{RequestId:X3} → 0x{ResponseId:X3}{(IsExtended ? "  (29-bit)" : "")}"
           + (FunctionalId is { } fid ? $"  功能 0x{fid:X3}" : "");
}
