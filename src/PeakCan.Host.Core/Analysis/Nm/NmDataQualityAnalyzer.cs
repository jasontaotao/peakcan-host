using System.Globalization;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 数据体检（spec §9）。跑规则前先做一致性检查，结果放报告顶部。
/// <para>
/// <b>不做成规则原语</b>：体检需要原始帧与 DBC（<see cref="NmRuleContext"/> 没有），
/// 且必须在规则求值之前运行——做成规则需要引入规则间调度，v1 不做。
/// </para>
/// <para>
/// <b>三态而非「问题列表」</b>：「通过」与「没查」必须可区分。问题列表装不下这个语义，
/// 会把「无 DBC 所以没查」误读成「查了没问题」。
/// </para>
/// </summary>
public static class NmDataQualityAnalyzer
{
    /// <summary>
    /// <paramref name="frames"/> 必须是选择器过滤后的帧集（通道/窗口/RTR/错误帧），
    /// 不能传 <see cref="NmAnalysisRequest.Frames"/>：否则会把其它通道或窗口外的
    /// 帧 ID 报成未知，且时间区间会越出报告自身的窗口。
    /// </summary>
    public static IReadOnlyList<NmDataQualityCheck> Analyze(
        NmAnalysisRequest request, IReadOnlyList<ReplayFrame> frames)
    {
        var checks = new List<NmDataQualityCheck>(4);
        checks.Add(CheckUnknownId(request, frames));
        return checks;
    }

    /// <summary>
    /// spec §9.1：报文 ID 是否在 DBC / profile 声明中。
    /// <para>
    /// 已知 ID 有两个来源：DBC 的报文集合（应用报文也算已知），
    /// 与 profile 显式声明的 NM 报文。只查后者会把 DBC 里的全部应用报文
    /// 误判成「未知 ID」——正常 trace 每次都会报。两者都没有才报 NotChecked。
    /// </para>
    /// </summary>
    private static NmDataQualityCheck CheckUnknownId(
        NmAnalysisRequest request, IReadOnlyList<ReplayFrame> frames)
    {
        var declaredNm = request.Profile.Messages
            .Where(m => m.IsNm)
            .Select(m => (m.CanIdHex, m.IsExtended))
            .ToHashSet();
        var dbcIds = request.Dbc?.MessagesById.Keys.ToHashSet();

        // spec §9.2：没有帧就没有可检查的对象——不能因为「循环没跑」而报通过。
        if (frames.Count == 0)
        {
            return NotChecked("窗口内没有可检查的帧，无法判定帧 ID 是否合法");
        }

        if (declaredNm.Count == 0 && dbcIds is null)
        {
            return NotChecked("既无 DBC 也无 profile 报文声明，无法判定帧 ID 是否合法");
        }

        var unknown = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var times = new List<double>();
        foreach (var frame in frames)
        {
            var hex = "0x" + frame.Id.ToString("X", CultureInfo.InvariantCulture);
            // DBC 的 Message 不带扩展帧标志（DBC 格式把扩展位编码在 ID 高 bit），
            // 因此 DBC 侧只能按 ID 数值比对：标准帧与扩展帧同数值会互相掩护。
            // 这是 DBC 格式的固有限制，不是本检查的取舍。
            var knownInDbc = dbcIds is not null && dbcIds.Contains(frame.Id);
            var knownInProfile = declaredNm.Contains((hex, frame.IsExtended));
            if (!knownInDbc && !knownInProfile)
            {
                unknown.Add(hex);
                times.Add(frame.Timestamp);
            }
        }

        if (unknown.Count == 0)
        {
            return new NmDataQualityCheck(
                "unknown_id", NmDataQualityStatus.Passed,
                $"全部 {frames.Count} 帧的 ID 均在已知范围内",
                Array.Empty<NmTimeRange>(), Array.Empty<string>());
        }

        return new NmDataQualityCheck(
            "unknown_id", NmDataQualityStatus.Failed,
            $"{times.Count} 帧的 CAN ID 不在 DBC 或 profile 声明中：{string.Join(", ", unknown)}",
            [new NmTimeRange(times.Min(), times.Max())],
            unknown.ToArray());
    }

    private static NmDataQualityCheck NotChecked(string detail)
        => new("unknown_id", NmDataQualityStatus.NotChecked, detail,
            Array.Empty<NmTimeRange>(), Array.Empty<string>());
}
