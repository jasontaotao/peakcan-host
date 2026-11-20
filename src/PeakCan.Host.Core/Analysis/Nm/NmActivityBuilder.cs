namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 报文级阶段推断（spec §7.3）：wakeup → active → released / silent。
/// 段必须是非零长度且首尾相接；M1 只做按报文的推断，不拆节点。
/// </summary>
public static class NmActivityBuilder
{
    public static IReadOnlyList<NmSegment> Build(
        IReadOnlyList<NmFrame> messageFrames,
        NmMessageDef def,
        IReadOnlyList<NmEpisode> episodes,
        int effectiveCycleMs,
        int? repeatMessageMs,
        IReadOnlyList<NmFrame> allNmFrames,
        double windowEnd,
        int effectiveTimeoutMs)
    {
        if (messageFrames.Count == 0) return Array.Empty<NmSegment>();

        var ordered = messageFrames.OrderBy(f => f.Frame.Timestamp).ToArray();
        var first = ordered[0].Frame.Timestamp;
        var last = ordered[^1].Frame.Timestamp;

        // 单帧构不成任何非零长度的阶段：不伪造零长段，返回空活动区间。
        if (ordered.Length == 1) return Array.Empty<NmSegment>();

        var cycleSec = Math.Max(effectiveCycleMs, 1) / 1000.0;
        var wakeupWindowSec = (repeatMessageMs ?? effectiveCycleMs) / 1000.0;
        var wakeupEnd = ResolveWakeupEnd(ordered, first, last, cycleSec, wakeupWindowSec);

        var segments = new List<NmSegment>(3);
        if (wakeupEnd > first) segments.Add(new NmSegment(first, wakeupEnd, "wakeup"));
        if (last > wakeupEnd) segments.Add(new NmSegment(wakeupEnd, last, "active"));

        // 释放/全静默段：先按报文归属的 episode 结束；若本报文就是该 episode 最后
        // 停止的报文，episode 按 spec §7.3 要到"全部静默 ≥ timeout"才结束，
        // 因此观察窗延续到 min(窗口结束, 末帧 + 超时)。窗口在阈值前截断时结束时刻
        // 不可判明（BoundaryEnd），不伪造尾段。
        var episode = ResolveEpisode(last, episodes);
        var episodeEnd = episode?.End ?? last;
        var phaseEnd = last;
        if (episodeEnd > last)
        {
            phaseEnd = episodeEnd;
        }
        else if (episode is { BoundaryEnd: false })
        {
            phaseEnd = Math.Min(windowEnd, last + effectiveTimeoutMs / 1000.0);
        }

        if (phaseEnd > last)
        {
            var othersActive = OthersActiveBetween(last, phaseEnd, def, allNmFrames);
            segments.Add(new NmSegment(last, phaseEnd, othersActive ? "released" : "silent"));
        }

        return segments;
    }

    private static double ResolveWakeupEnd(
        NmFrame[] ordered, double first, double last, double cycleSec, double wakeupWindowSec)
    {
        // 首帧起连续 ≥2 个 <0.5×cycle 的间隔视为观察到的唤醒突发，阶段到突发末帧。
        var shortRun = 0;
        for (int i = 1; i < ordered.Length; i++)
        {
            if (ordered[i].Frame.Timestamp - ordered[i - 1].Frame.Timestamp >= 0.5 * cycleSec) break;
            shortRun++;
        }
        if (shortRun >= 2)
        {
            var burstEnd = ordered[shortRun].Frame.Timestamp;
            if (burstEnd > first) return burstEnd;
        }

        // 无突发：用 RepeatMessageMs（缺省用周期）作为观察窗，且不晚于末帧。
        return Math.Min(first + wakeupWindowSec, last);
    }

    private static NmEpisode? ResolveEpisode(double last, IReadOnlyList<NmEpisode> episodes)
    {
        foreach (var episode in episodes)
        {
            if (episode.Start <= last && last <= episode.End) return episode;
        }
        return null;
    }

    private static bool OthersActiveBetween(
        double from, double to, NmMessageDef def, IReadOnlyList<NmFrame> allNmFrames)
        => allNmFrames.Any(f =>
            f.Frame.Timestamp > from && f.Frame.Timestamp <= to
            && !(string.Equals(f.Def.CanIdHex, def.CanIdHex, StringComparison.Ordinal)
                 && f.Def.IsExtended == def.IsExtended));
}
