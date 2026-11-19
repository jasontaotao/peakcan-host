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
            BoundaryEnd: windowEnd - last <= timeout));
        return episodes;
    }
}
