namespace PeakCan.Host.Core.Analysis.Nm;

public sealed record NmGapInput(
    string SourceId,
    IReadOnlyList<NmFrame> NmFrames,
    IReadOnlyList<PeakCan.Host.Core.Replay.ReplayFrame> AllFrames,
    NmProfile Profile,
    IReadOnlyList<NmEpisode> Episodes,
    NmWindow Window,
    NmSelectionDiagnostics Diagnostics,
    // 统一时序（spec §5.2）：null 时回落到 Profile.Timings / M1 默认，
    // 兼容只关心 gap 逻辑的调用方与测试。
    NmGlobalTiming? Timing = null);

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

        // 统一时序优先（spec §5.2）：避免 episode 切分用 2200、静默判定用 2000 的漂移。
        var globalTiming = input.Timing
            ?? NmTimingDefaults.FromProfile(input.Profile.Timings, input.Profile.StandardTimings);
        var timeoutMs = globalTiming.TimeoutMs;
        var cycleMs = globalTiming.CycleMs;
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
                $"NM silent until {gapEnd:F3}s", input.Episodes[i].Index, "measured"));
            unknowns.Add("all_nm_silent_reason_unknown（release / 下电 / 记录停止 / bus sleep 无法区分）");
        }

        // 1b) 窗口首尾的纯静默：开头是记录边界，结尾是 NM 先停、录制仍在继续。
        //     尾部静默不在 episode 间隙里，必须单独报；首尾各只出现一次，不会重复。
        if (input.Episodes.Count > 0)
        {
            var firstEpisode = input.Episodes[0];
            var headSilence = firstEpisode.Start - input.Window.Start;
            if (headSilence >= timeoutSec)
            {
                findings.Add(new NmFinding(
                    "NM_TRACE_BOUNDARY", "info",
                    $"窗口开头到首个 NM 帧静默 {headSilence:F2}s（≥ 超时 {timeoutMs}ms），属记录边界",
                    new[] { input.Window.Start, firstEpisode.Start }, IsInference: false,
                    "记录边界，不是故障"));
            }

            var lastEpisode = input.Episodes[^1];
            var tailSilence = input.Window.End - lastEpisode.End;
            if (tailSilence >= timeoutSec)
            {
                findings.Add(new NmFinding(
                    "NM_ALL_NM_SILENT", "info",
                    $"全部 NM 报文静默 {tailSilence:F2}s（≥ 超时 {timeoutMs}ms，窗口尾部）",
                    new[] { lastEpisode.End, input.Window.End }, IsInference: false,
                    "正常释放/下电/记录停止；不对单个报文重复报 dropout"));
                events.Add(new NmEvent(lastEpisode.End, "all_nm_silent", null, null,
                    $"NM silent until window end {input.Window.End:F3}s",
                    lastEpisode.Index, "measured"));
                unknowns.Add("all_nm_silent_reason_unknown（release / 下电 / 记录停止 / bus sleep 无法区分）");
            }
        }

        // 2) 单报文静默但其它 NM 仍在发送：区分"持续掉线"与"单个间隔超时"。
        //    - 持续掉线：该报文到 episode 结束前再没出现 → NM_NODE_DROPOUT
        //    - 单间隔超时：中间断了又恢复 → NM_SENDER_SILENT_BEYOND_TIMEOUT
        // 分组键必须含扩展位：标准 0x500 与扩展 0x500 是两条不同报文，
        // 合并后会互相掩盖掉线，并串用对方的超时阈值。
        var perMessage = input.NmFrames
            .GroupBy(f => (f.Def.CanIdHex, f.Def.IsExtended))
            .ToArray();
        if (perMessage.Length > 1)
        {
            foreach (var group in perMessage)
            {
                var canId = group.Key.CanIdHex;
                var isExtended = group.Key.IsExtended;
                var display = Display(canId, isExtended);
                var times = group.Select(f => f.Frame.Timestamp).OrderBy(t => t).ToArray();
                var lastEpisode = input.Episodes.Count > 0 ? input.Episodes[^1] : null;
                // 单报文阈值用该报文的生效超时（def > profile > 2000），与周期统计同源；
                // 全网络静默仍用全局超时。
                var effectiveTimeoutMs = NmTiming
                    .Resolve(group.First().Def, globalTiming).TimeoutMs;
                var messageTimeoutSec = effectiveTimeoutMs / 1000.0;

                if (lastEpisode is not null
                    && lastEpisode.End - times[^1] > messageTimeoutSec
                    && !OverlapsAllNmSilent(times[^1], lastEpisode.End, input.Episodes)
                    && OthersActiveBetween(times[^1], lastEpisode.End, canId, isExtended, input.NmFrames))
                {
                    findings.Add(new NmFinding(
                        "NM_NODE_DROPOUT", "warn",
                        $"{display} 从 {times[^1]:F2}s 起未再发送（episode 结束 {lastEpisode.End:F2}s），其它 NM 报文仍在发送",
                        new[] { times[^1], lastEpisode.End }, IsInference: true,
                        "检查该发送节点的复位/掉线/发送停止"));
                    events.Add(new NmEvent(times[^1], "sender_silent", null, canId,
                        $"{lastEpisode.End - times[^1]:F2}s", lastEpisode.Index, "inferred",
                        isExtended));
                    unknowns.Add("dropout_receiver_side_or_node_internal_state_not_excluded（只看发送侧）");
                }

                for (int i = 1; i < times.Length; i++)
                {
                    var gap = times[i] - times[i - 1];
                    if (gap <= messageTimeoutSec) continue;
                    if (OverlapsAllNmSilent(times[i - 1], times[i], input.Episodes)) continue;
                    if (!OthersActiveBetween(times[i - 1], times[i], canId, isExtended, input.NmFrames)) continue;

                    findings.Add(new NmFinding(
                        "NM_SENDER_SILENT_BEYOND_TIMEOUT", "warn",
                        $"{display} 静默 {gap:F2}s（> 超时 {effectiveTimeoutMs}ms），其它 NM 报文仍在发送",
                        new[] { times[i - 1], times[i] }, IsInference: true,
                        "检查发送端是否停止发送/掉线"));
                    events.Add(new NmEvent(times[i - 1], "sender_silent", null, canId,
                        $"{gap:F2}s", NmEpisodeBuilder.IndexAt(times[i - 1], input.Episodes), "inferred",
                        isExtended));
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
                $"{gap:F2}s", NmEpisodeBuilder.IndexAt(allTimes[i - 1], input.Episodes), "measured"));
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

    /// <summary>区间内有其它 NM 报文在发送（否则是全网静默，不是单报文掉线）。
    /// 同数值 ID 的标准/扩展帧视为不同报文，互不折算对方的活动证据。</summary>
    private static bool OthersActiveBetween(
        double from, double to, string excludeCanId, bool excludeIsExtended, IReadOnlyList<NmFrame> all)
        => all.Any(f => !(string.Equals(f.Def.CanIdHex, excludeCanId, StringComparison.Ordinal)
                          && f.Def.IsExtended == excludeIsExtended)
                        && f.Frame.Timestamp > from && f.Frame.Timestamp <= to);

    /// <summary>事件/结论里的报文名：扩展帧带 <c>(ext)</c>，避免与同数值标准帧混淆。</summary>
    private static string Display(string canIdHex, bool isExtended)
        => isExtended ? canIdHex + "(ext)" : canIdHex;
}
