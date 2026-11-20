namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>已识别为 NM 的一帧（报文定义 + 原始帧）。</summary>
public sealed record NmFrame(NmMessageDef Def, PeakCan.Host.Core.Replay.ReplayFrame Frame);

/// <summary>
/// 按「全部 NM 报文静默 ≥ timeout」切 episode。
/// M1 的 Kind 固定为 "active"；wakeup/normal/release 阶段属于推断，通过事件表达。
/// </summary>
public static class NmEpisodeBuilder
{
    public static IReadOnlyList<NmEpisode> Build(
        IReadOnlyList<NmFrame> frames, int timeoutMs, double windowStart, double windowEnd)
    {
        if (frames.Count == 0) return Array.Empty<NmEpisode>();

        var timeout = timeoutMs / 1000.0;
        var episodes = new List<NmEpisode>();
        var ordered = frames.OrderBy(f => f.Frame.Timestamp).ToArray();

        var start = ordered[0].Frame.Timestamp;
        var last = start;
        var participants = new HashSet<string>(StringComparer.Ordinal) { ordered[0].Def.CanIdHex };
        var index = 0;

        for (int i = 1; i < ordered.Length; i++)
        {
            var t = ordered[i].Frame.Timestamp;
            // 静默 ≥ 阈值才切开：等于阈值也算网络已静默。
            if (t - last >= timeout)
            {
                episodes.Add(new NmEpisode(index++, start, last, "active", null,
                    participants.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
                    BoundaryStart: start <= windowStart,
                    BoundaryEnd: false));
                start = t;
                participants.Clear();
            }
            last = t;
            participants.Add(ordered[i].Def.CanIdHex);
        }

        episodes.Add(new NmEpisode(index, start, last, "active", null,
            participants.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            BoundaryStart: start <= windowStart,
            // 窗口结束时若静默已 ≥ timeout，按切分判据该 episode 已自然结束，不算截断。
            BoundaryEnd: windowEnd - last < timeout));
        return episodes;
    }

    /// <summary>
    /// 把事件时间映射到 episode 编号：落在 <c>[Start, End]</c> 内取该 episode；
    /// 落进 episode 之间的空档（或窗口首尾）时，取时间上最近的<strong>前一个</strong>
    /// episode；之前没有任何 episode 时返回 0。未提供 episodes 时恒为 0。
    /// </summary>
    public static int IndexAt(double t, IReadOnlyList<NmEpisode>? episodes)
    {
        if (episodes is null || episodes.Count == 0) return 0;

        int? containing = null;
        var previous = -1;
        foreach (var episode in episodes)
        {
            if (t >= episode.Start && t <= episode.End)
                containing ??= episode.Index;
            if (episode.End <= t && episode.Index > previous)
                previous = episode.Index;
        }

        return containing ?? (previous >= 0 ? previous : 0);
    }
}
