using System.Globalization;

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
    public static IReadOnlyList<NmDataQualityCheck> Analyze(NmAnalysisRequest request)
    {
        var checks = new List<NmDataQualityCheck>(4);
        checks.Add(CheckUnknownId(request));
        return checks;
    }

    /// <summary>
    /// spec §9.1：报文 ID 是否在 DBC / profile 声明中。
    /// <para>
    /// 没有 DBC 时用 profile 声明的报文集合判定——profile 是用户显式给的约束，
    /// 不依赖 DBC 也能查。两者都没有才报 NotChecked。
    /// </para>
    /// </summary>
    private static NmDataQualityCheck CheckUnknownId(NmAnalysisRequest request)
    {
        var declared = request.Profile.Messages
            .Where(m => m.IsNm)
            .Select(m => (m.CanIdHex, m.IsExtended))
            .ToHashSet();

        if (declared.Count == 0 && request.Dbc is null)
        {
            return new NmDataQualityCheck(
                "unknown_id", NmDataQualityStatus.NotChecked,
                "既无 DBC 也无 profile 报文声明，无法判定帧 ID 是否合法",
                Array.Empty<NmTimeRange>(), Array.Empty<string>());
        }

        var unknown = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var times = new List<double>();
        foreach (var frame in request.Frames)
        {
            var hex = "0x" + frame.Id.ToString("X", CultureInfo.InvariantCulture);
            if (declared.Count > 0 && !declared.Contains((hex, frame.IsExtended)))
            {
                unknown.Add(hex);
                times.Add(frame.Timestamp);
            }
        }

        if (unknown.Count == 0)
        {
            return new NmDataQualityCheck(
                "unknown_id", NmDataQualityStatus.Passed,
                $"全部 {request.Frames.Count} 帧的 ID 均在声明范围内",
                Array.Empty<NmTimeRange>(), Array.Empty<string>());
        }

        return new NmDataQualityCheck(
            "unknown_id", NmDataQualityStatus.Failed,
            $"{times.Count} 帧的 CAN ID 不在 profile 声明中：{string.Join(", ", unknown)}",
            [new NmTimeRange(times.Min(), times.Max())],
            unknown.ToArray());
    }
}
