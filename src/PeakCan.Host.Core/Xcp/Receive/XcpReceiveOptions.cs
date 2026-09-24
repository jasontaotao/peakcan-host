using A2lEditor.Core.Layout;
using PeakCan.Host.Core.Xcp.Scheduling;

namespace PeakCan.Host.Core.Xcp.Receive;

/// <summary>
/// XcpReceiveLoop 出站样本（T14 回调出站；T15 sink 接手后复用此类型）。
/// 携带 planner 自产条目（PID/ODT/Entry/对象名/段与地址全量溯源），值本身不缓存。
/// </summary>
/// <param name="Entry">反查命中的 planner 条目（只读）。</param>
/// <param name="Value">包侧 <see cref="ValueContract.Decode"/> 产出的物理值。</param>
public sealed record XcpDaqSample(PlannedDaqEntry Entry, double Value);

/// <summary>归因出口种类（spec §3 Receive：未知帧不静默丢）。</summary>
public enum XcpReceiveAttributionKind
{
    /// <summary>空帧（无法按首字节分类）或 DTO 载荷短于规划布局。</summary>
    MalformedFrame,

    /// <summary>首字节非响应 PID（0xFF/0xFE）也非 DTO 区（0x00–0xFB），如 EV 0xFD / SERV 0xFC。</summary>
    UnknownPid,

    /// <summary>DTO PID 区但 planner 方案中无此 ODT。</summary>
    UnmappedDto,

    /// <summary>包侧 Decode 抛 DecodeException；<see cref="XcpReceiveAttribution.MissingCause"/> 承接包侧归因。</summary>
    DecodeFailed,
}

/// <summary>接收层归因条目（T15 空窗归因的逐帧原料；空窗聚合归 T15）。</summary>
/// <param name="Kind">归因种类。</param>
/// <param name="FirstByte">触发帧的 PID 首字节（空帧恒 0x00）。</param>
/// <param name="Detail">人类可读归因细节。</param>
/// <param name="MissingCause">包侧 MissingCause（仅 DecodeFailed 承接；其余恒 null）。</param>
public sealed record XcpReceiveAttribution(
    XcpReceiveAttributionKind Kind,
    byte FirstByte,
    string Detail,
    A2lEditor.Core.Layout.MissingCause? MissingCause = null);

/// <summary>
/// XcpReceiveLoop 构造参数（spec §3 Receive：合同与索引解析期一次建好）。
/// <see cref="Map"/> 必须是 <see cref="AcquisitionPlanner"/> 的自产方案——[H2]
/// 占位三元组禁作 DAQ 配置；<see cref="Contracts"/> 为包侧解析期合同集。
/// </summary>
public sealed class XcpReceiveOptions
{
    /// <summary>planner 自产采集方案（反查唯一来源；构造后只读，可并发读）。</summary>
    public required PlannedAcquisitionMap Map { get; init; }

    /// <summary>包侧合同集（解析期一次建好；loop 构造期快照方案涉及的对象）。</summary>
    public required ContractSet Contracts { get; init; }

    /// <summary>解码成功出站（T14 回调出站；T15 换 sink，接口预留）。</summary>
    public Action<XcpDaqSample>? SampleDecoded { get; init; }

    /// <summary>归因出站（未知帧/解码失败；T15 聚合空窗）。</summary>
    public Action<XcpReceiveAttribution>? Attributed { get; init; }
}
