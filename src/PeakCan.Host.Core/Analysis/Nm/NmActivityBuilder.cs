namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 报文级阶段推断（spec §7.3）：wakeup → active → released / silent。
/// 同一 episode 内各段首尾相接且非零长度；跨 episode 的静默空档单独留白，
/// 由整网行渲染，报文行不得跨越。M1 只做按报文的推断，不拆节点。
/// <para>
/// 前提：<c>episodes</c> 必须覆盖该报文的全部帧（<see cref="NmEpisodeBuilder"/>
/// 的产物满足）；落在 episode 之外的帧不会生成段（公众调用方自行保证切片完整）。
/// </para>
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

        // 单帧构不成任何非零长度的阶段：不伪造零长段，返回空活动区间。
        if (ordered.Length == 1) return Array.Empty<NmSegment>();

        var cycleSec = Math.Max(effectiveCycleMs, 1) / 1000.0;
        var wakeupWindowSec = (repeatMessageMs ?? effectiveCycleMs) / 1000.0;

        // 阶段属于 episode：两段唤醒之间的静默空档不能被画进同一条
        // wakeup/active 段，否则第二段唤醒永远不会出现，且"发送中"的起止
        // 时间会横跨整个睡眠期。按 episode 切片逐段生成。
        if (episodes is null || episodes.Count == 0)
        {
            var segments = new List<NmSegment>(3);
            AppendEpisodeSegments(
                segments, ordered, episode: null,
                cycleSec, wakeupWindowSec, effectiveTimeoutMs,
                def, allNmFrames, windowEnd, phaseLimit: windowEnd);
            return segments;
        }

        var all = new List<NmSegment>(episodes.Count * 2);
        var sorted = episodes.OrderBy(e => e.Start).ToArray();
        int cursor = 0;
        for (int i = 0; i < sorted.Length; i++)
        {
            var episode = sorted[i];
            while (cursor < ordered.Length && ordered[cursor].Frame.Timestamp < episode.Start)
                cursor++;
            var begin = cursor;
            while (cursor < ordered.Length && ordered[cursor].Frame.Timestamp <= episode.End)
                cursor++;
            if (cursor == begin) continue;

            // 尾段不得越过下一 episode 的起点：episode 用全局静默阈值切分，
            // 而尾段可能按报文级 timeout 外推；两者不一致时会在时间条上叠段。
            var phaseLimit = i + 1 < sorted.Length ? sorted[i + 1].Start : windowEnd;
            AppendEpisodeSegments(
                all, ordered[begin..cursor], episode,
                cycleSec, wakeupWindowSec, effectiveTimeoutMs,
                def, allNmFrames, windowEnd, phaseLimit);
        }

        return all;
    }

    /// <summary>
    /// 生成一个 episode 内的阶段段：wakeup → active → released/silent。
    /// <paramref name="episode"/> 为 null 时视为没有 episode 边界（兼容旧调用）。
    /// </summary>
    private static void AppendEpisodeSegments(
        List<NmSegment> segments,
        NmFrame[] ordered,
        NmEpisode? episode,
        double cycleSec,
        double wakeupWindowSec,
        int effectiveTimeoutMs,
        NmMessageDef def,
        IReadOnlyList<NmFrame> allNmFrames,
        double windowEnd,
        double phaseLimit)
    {
        if (ordered.Length == 0) return;

        var first = ordered[0].Frame.Timestamp;
        var last = ordered[^1].Frame.Timestamp;
        var wakeupEnd = ResolveWakeupEnd(ordered, first, last, cycleSec, wakeupWindowSec);

        if (wakeupEnd > first) segments.Add(new NmSegment(first, wakeupEnd, "wakeup"));
        if (last > wakeupEnd) segments.Add(new NmSegment(wakeupEnd, last, "active"));

        // 释放/全静默段：先按报文归属的 episode 结束；若本报文就是该 episode 最后
        // 停止的报文，episode 按 spec §7.3 要到"全部静默 ≥ timeout"才结束，
        // 因此观察窗延续到 min(窗口结束, 末帧 + 超时)。窗口在阈值前截断时结束时刻
        // 不可判明（BoundaryEnd），不伪造尾段。
        var phaseEnd = last;
        if (episode is { } ep)
        {
            if (ep.End > last)
            {
                phaseEnd = ep.End;
            }
            else if (!ep.BoundaryEnd)
            {
                phaseEnd = Math.Min(windowEnd, last + effectiveTimeoutMs / 1000.0);
            }
        }

        // 无论尾段来自 episode.End 还是 last+timeout，都不允许压到下一 episode/窗口之外。
        phaseEnd = Math.Min(phaseEnd, phaseLimit);
        if (phaseEnd > last)
        {
            var othersActive = OthersActiveBetween(last, phaseEnd, def, allNmFrames);
            segments.Add(new NmSegment(last, phaseEnd, othersActive ? "released" : "silent"));
        }
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

    private static bool OthersActiveBetween(
        double from, double to, NmMessageDef def, IReadOnlyList<NmFrame> allNmFrames)
        => allNmFrames.Any(f =>
            f.Frame.Timestamp > from && f.Frame.Timestamp <= to
            && !(string.Equals(f.Def.CanIdHex, def.CanIdHex, StringComparison.Ordinal)
                 && f.Def.IsExtended == def.IsExtended));
}
