namespace PeakCan.Host.Core.Analysis.Nm;

public sealed record NmGapInput(
    string SourceId,
    IReadOnlyList<NmFrame> NmFrames,
    IReadOnlyList<PeakCan.Host.Core.Replay.ReplayFrame> AllFrames,
    NmProfile Profile,
    IReadOnlyList<NmEpisode> Episodes,
    NmWindow Window,
    NmSelectionDiagnostics Diagnostics);

public sealed record NmGapResult(
    IReadOnlyList<NmFinding> Findings,
    IReadOnlyList<NmEvent> Events,
    IReadOnlyList<string> Unknowns);

/// <summary>
/// 空档归因，严格按 spec §7.5 的优先级：先网络级、后单报文。
/// 只在有可验证证据时升级为 log gap；不得把"没有 NM 帧"说成睡眠。
/// </summary>
public static class NmGapAnalyzer
{
    public static NmGapResult Analyze(NmGapInput input)
    {
        var findings = new List<NmFinding>();
        var events = new List<NmEvent>();
        var unknowns = new List<string>();

        var timeoutMs = input.Profile.Timings.TimeoutMs ?? 2000;
        var cycleMs = input.Profile.Timings.CycleMs ?? 500;
        var timeoutSec = timeoutMs / 1000.0;
        var busSilenceThreshold = Math.Max(2 * cycleMs, 500) / 1000.0;

        // 1) 每个 episode 之后到下一个 episode 之间是"全部 NM 静默"。
        for (int i = 0; i + 1 < input.Episodes.Count; i++)
        {
            var gapStart = input.Episodes[i].End;
            var gapEnd = input.Episodes[i + 1].Start;
            if (gapEnd - gapStart < timeoutSec) continue;

            findings.Add(new NmFinding(
                "NM_ALL_NM_SILENT", "info",
                $"全部 NM 报文静默 {gapEnd - gapStart:F2}s（≥ 超时 {timeoutMs}ms）",
                new[] { gapStart, gapEnd }, IsInference: false,
                "正常释放/下电/记录停止；不对单个报文重复报 dropout"));
            events.Add(new NmEvent(gapStart, "all_nm_silent", null, null,
                $"NM silent until {gapEnd:F3}s", i, "measured"));
            unknowns.Add("all_nm_silent_reason_unknown（release / 下电 / 记录停止 / bus sleep 无法区分）");
        }

        // 2) 单报文静默但其它 NM 仍在发送：区分"持续掉线"与"单个间隔超时"。
        //    - 持续掉线：该报文到 episode 结束前再没出现 → NM_NODE_DROPOUT
        //    - 单间隔超时：中间断了又恢复 → NM_SENDER_SILENT_BEYOND_TIMEOUT
        var perMessage = input.NmFrames.GroupBy(f => f.Def.CanIdHex, StringComparer.Ordinal).ToArray();
        if (perMessage.Length > 1)
        {
            foreach (var group in perMessage)
            {
                var times = group.Select(f => f.Frame.Timestamp).OrderBy(t => t).ToArray();
                var lastEpisode = input.Episodes.Count > 0 ? input.Episodes[^1] : null;

                if (lastEpisode is not null
                    && lastEpisode.End - times[^1] > timeoutSec
                    && !OverlapsAllNmSilent(times[^1], lastEpisode.End, input.Episodes)
                    && OthersActiveBetween(times[^1], lastEpisode.End, group.Key, input.NmFrames))
                {
                    findings.Add(new NmFinding(
                        "NM_NODE_DROPOUT", "warn",
                        $"{group.Key} 从 {times[^1]:F2}s 起未再发送（episode 结束 {lastEpisode.End:F2}s），其它 NM 报文仍在发送",
                        new[] { times[^1], lastEpisode.End }, IsInference: true,
                        "检查该发送节点的复位/掉线/发送停止"));
                    events.Add(new NmEvent(times[^1], "sender_silent", null, group.Key,
                        $"{lastEpisode.End - times[^1]:F2}s", lastEpisode.Index, "inferred"));
                    unknowns.Add("dropout_receiver_side_or_node_internal_state_not_excluded（只看发送侧）");
                }

                for (int i = 1; i < times.Length; i++)
                {
                    var gap = times[i] - times[i - 1];
                    if (gap <= timeoutSec) continue;
                    if (OverlapsAllNmSilent(times[i - 1], times[i], input.Episodes)) continue;
                    if (!OthersActiveBetween(times[i - 1], times[i], group.Key, input.NmFrames)) continue;

                    findings.Add(new NmFinding(
                        "NM_SENDER_SILENT_BEYOND_TIMEOUT", "warn",
                        $"{group.Key} 静默 {gap:F2}s（> 超时 {timeoutMs}ms），其它 NM 报文仍在发送",
                        new[] { times[i - 1], times[i] }, IsInference: true,
                        "检查发送端是否停止发送/掉线"));
                    events.Add(new NmEvent(times[i - 1], "sender_silent", null, group.Key,
                        $"{gap:F2}s", EpisodeIndexOf(times[i - 1], input.Episodes), "inferred"));
                    unknowns.Add("sender_silent_receiver_side_not_excluded");
                }
            }
        }

        // 3) 整条总线无任何帧 → bus-wide silence。
        var allTimes = input.AllFrames.Select(f => f.Timestamp).OrderBy(t => t).ToArray();
        for (int i = 1; i < allTimes.Length; i++)
        {
            var gap = allTimes[i] - allTimes[i - 1];
            if (gap < busSilenceThreshold) continue;
            findings.Add(new NmFinding(
                "NM_BUS_WIDE_SILENCE", "warn",
                $"全网无帧 {gap:F2}s（阈值 {busSilenceThreshold * 1000:F0}ms）",
                new[] { allTimes[i - 1], allTimes[i] }, IsInference: false,
                "确认记录仪是否暂停/下电；不要据此判断节点掉线"));
            events.Add(new NmEvent(allTimes[i - 1], "bus_wide_silence", null, null,
                $"{gap:F2}s", 0, "measured"));
            unknowns.Add("recorder_gap_or_bus_sleep");
        }

        // 4) 只有可验证证据才报 log gap。
        if (input.Diagnostics.TimestampRegression)
        {
            findings.Add(new NmFinding(
                "NM_TIMESTAMP_REGRESSION", "warn",
                "时间戳非单调，已排序后分析",
                new[] { input.Window.Start }, IsInference: false,
                "检查记录文件完整性"));
            findings.Add(new NmFinding(
                "NM_LOG_GAP_SUSPECTED", "warn",
                "存在时间戳回退，可能存在记录仪丢帧",
                new[] { input.Window.Start }, IsInference: true,
                "对比记录仪日志/其它日志源"));
        }

        return new NmGapResult(findings, events, unknowns.Distinct().ToArray());
    }

    /// <summary>区间与任意相邻 episode 之间的全静默区间有交集时，不做单报文归因。</summary>
    private static bool OverlapsAllNmSilent(double from, double to, IReadOnlyList<NmEpisode> episodes)
    {
        for (int i = 0; i + 1 < episodes.Count; i++)
        {
            var gapStart = episodes[i].End;
            var gapEnd = episodes[i + 1].Start;
            if (gapEnd <= gapStart) continue;
            if (to > gapStart && from < gapEnd) return true;
        }
        return false;
    }

    /// <summary>区间内有其它 NM 报文在发送（否则是全网静默，不是单报文掉线）。</summary>
    private static bool OthersActiveBetween(
        double from, double to, string excludeCanId, IReadOnlyList<NmFrame> all)
        => all.Any(f => !string.Equals(f.Def.CanIdHex, excludeCanId, StringComparison.Ordinal)
                        && f.Frame.Timestamp > from && f.Frame.Timestamp <= to);

    private static int EpisodeIndexOf(double t, IReadOnlyList<NmEpisode> episodes)
    {
        for (int i = 0; i < episodes.Count; i++)
            if (t >= episodes[i].Start && t <= episodes[i].End) return i;
        return episodes.Count > 0 ? episodes.Count - 1 : 0;
    }
}
