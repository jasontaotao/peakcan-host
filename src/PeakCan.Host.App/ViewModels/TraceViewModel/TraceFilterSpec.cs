using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using PeakCan.HIL.Core.HIL.Environment;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// Trace 页视图层过滤的不可变规范（spec §5.1/§5.2）。任一字段为 null/false =
/// 该维度不过滤；各条件 AND 组合，最后 <see cref="Exclude"/> 对整体取反。
/// <para>
/// <see cref="Matches"/> 是纯谓词实例方法（零 I/O、零共享状态）——过滤器
/// （<c>EntriesView.Filter</c>）与高亮规则求值共用同一套判定逻辑。
/// </para>
/// <para>
/// ID 匹配语义：<see cref="CanId.Raw"/> 由 ctor 保证 ≤0x1FFFFFFF、从不携带
/// bit31（IDE 位），故 <see cref="IdAllowList"/> 匹配侧无掩码；DBC 消息名的
/// <c>&amp; 0x7FFF_FFFF</c> 掩码在 <see cref="TraceFilterParser"/> 解析侧完成。
/// </para>
/// </summary>
public sealed record TraceFilterSpec
{
    /// <summary>ID allow-list；null=不过滤。值为裸 ID（不含 IDE 位）。</summary>
    public IReadOnlySet<uint>? IdAllowList { get; init; }

    /// <summary>J1939 PGN 列表；null=不过滤。值为 18-bit PGN（≤0x3FFFF）。仅扩展帧可匹配。</summary>
    public IReadOnlySet<uint>? PgnList { get; init; }

    /// <summary>J1939 源地址；null=不过滤。仅扩展帧可匹配。</summary>
    public byte? Sa { get; init; }

    /// <summary>J1939 目标地址；null=不过滤。仅扩展帧且 PDU1 可匹配。</summary>
    public byte? Da { get; init; }

    /// <summary>通道；null=全部通道。</summary>
    public ChannelId? Channel { get; init; }

    /// <summary>仅错误帧。</summary>
    public bool ErrorsOnly { get; init; }

    /// <summary>对上述合取结果整体取反（黑名单语义）。</summary>
    public bool Exclude { get; init; }

    /// <summary>payload 字节模式；null=不过滤。复用既有 <see cref="BytePattern"/>。</summary>
    public BytePattern? Payload { get; init; }

    /// <summary>全 null/false 的空规范（显示全部）。</summary>
    public static TraceFilterSpec Empty { get; } = new();

    /// <summary>全部条件为 null/false。</summary>
    public bool IsEmpty =>
        IdAllowList is null
        && PgnList is null
        && Sa is null
        && Da is null
        && Channel is null
        && !ErrorsOnly
        && !Exclude
        && Payload is null;

    /// <summary>
    /// 纯谓词：<paramref name="entry"/> 是否通过本规范（spec §5.2）。
    /// 各条件 AND，最后 <see cref="Exclude"/> 对整体取反。
    /// </summary>
    public bool Matches(TraceEntry entry)
    {
        bool result = MatchesCore(entry);
        return Exclude ? !result : result;
    }

    private bool MatchesCore(TraceEntry entry)
    {
        // 1. IdAllowList（无掩码，见类注释）。
        if (IdAllowList is not null && !IdAllowList.Contains(entry.Id.Raw))
            return false;

        // 2-4. J1939 条件（PGN/SA/DA）仅扩展帧可匹配。
        if (PgnList is not null || Sa is not null || Da is not null)
        {
            if (!entry.Id.IsExtended)
                return false; // 标准帧设任一 J1939 条件 → 不匹配（spec §5.2 钉死）。
            var j1939 = new J1939Id(entry.Id.Raw);
            if (PgnList is not null && !PgnList.Contains(j1939.Pgn))
                return false;
            if (Sa is { } sa && j1939.SourceAddress != sa)
                return false;
            if (Da is { } da)
            {
                // 仅 PDU1 有 DA；PDU2（DestinationAddress=null）设 Da 条件 → 不匹配。
                if (j1939.DestinationAddress is not { } entryDa || entryDa != da)
                    return false;
            }
        }

        // 5. Channel。
        if (Channel is { } ch && entry.Channel != ch)
            return false;

        // 6. ErrorsOnly。
        if (ErrorsOnly && !entry.IsError)
            return false;

        // 7. Payload（帧短于 offset → 不匹配，非错误；负 offset（防御，parser 已拒）
        //    同样恒不匹配——谓词在 view.Filter 委托内执行，抛异常会杀死 TraceService）。
        if (Payload is { } p
            && (p.Offset < 0
                || entry.Data.Length <= p.Offset
                || (entry.Data[p.Offset] & p.Mask) != p.Value))
            return false;

        return true;
    }
}

