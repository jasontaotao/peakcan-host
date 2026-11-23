namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 逐报文周期统计。规则见 spec §7.4：剔除跨 episode 边界、超过超时阈值、
/// missed_frame（1.5×cycle &lt; 间隔 ≤ timeout，见 §7.5.6）以及唤醒突发（§7.6）的间隔；
/// 有效样本 &lt; 5 时只报样本不足，不下偏差结论。
/// </summary>
public static class NmCycleAnalyzer
{
    private const int MinimumSamples = 5;

    /// <summary>
    /// 周期统计。<paramref name="toleranceMs"/> 为 null 时用 M1 默认
    /// <c>max(0.2×cycle, 30ms)</c>——保持无 profile 时行为不变。
    /// </summary>
    public static NmCycleStats Analyze(
        IReadOnlyList<double> timestamps,
        NmMessageDef def,
        IReadOnlyList<NmEpisode> episodes,
        int? toleranceMs = null)
    {
        var ordered = timestamps.OrderBy(t => t).ToArray();
        var timeoutSec = (def.TimeoutMs ?? int.MaxValue) / 1000.0;
        var cycleSec = def.CycleMs is { } cycle ? cycle / 1000.0 : (double?)null;

        // 唤醒突发（spec §7.6）属于 immediate 发送序列，不是报文周期；纳入统计
        // 会把突发整体误报成周期偏差。按 episode 取首帧起的短间隔前缀：即使窗口
        // 恰好从突发第一帧开始（视口裁窗）也能识别，不依赖"突发前那一帧"存在。
        var burstIntervals = MarkBurstIntervals(ordered, episodes, cycleSec);

        var intervals = new List<double>();
        for (int i = 1; i < ordered.Length; i++)
        {
            var delta = ordered[i] - ordered[i - 1];
            // 同一时间戳的重复帧不构成周期样本。
            if (delta <= 0) continue;
            // 静默超过超时阈值的间隔已被 episode 切分吃掉，不算周期样本。
            if (delta > timeoutSec) continue;
            // spec §7.5.6：1.5×cycle < 间隔 ≤ timeout 属于 missed_frame（漏发一次），
            // 不是周期本身的变化；纳入统计会把一次漏发误报成周期偏差。
            if (cycleSec is { } expectedCycle && delta > 1.5 * expectedCycle) continue;
            if (CrossesEpisodeBoundary(ordered[i - 1], ordered[i], episodes)) continue;
            // 突发序列内的短间隔不参与周期统计。
            if (burstIntervals[i]) continue;
            intervals.Add(delta);
        }

        if (intervals.Count < MinimumSamples)
        {
            // 样本不足：只报计数，中位数/相位/偏差一律留空，避免下结论。
            return new NmCycleStats(null, null, null, null, null, 0,
                IntervalCount: intervals.Count,
                ExpectedMs: def.CycleMs,
                ToleranceMs: toleranceMs ?? Tolerance(def.CycleMs),
                InsufficientSamples: true);
        }

        var sorted = intervals.OrderBy(x => x).ToArray();
        var median = Percentile(sorted, 0.5);
        var p95 = Percentile(sorted, 0.95);

        int deviations = 0;
        int longestConsecutive = 0;
        if (def.CycleMs is { } expected)
        {
            var effectiveToleranceMs = toleranceMs ?? Tolerance(expected)!.Value;
            var expectedSec = expected / 1000.0;
            var toleranceSec = effectiveToleranceMs / 1000.0;
            // 一次遍历同时统计总偏差间隔数与最长连续偏差间隔数。
            var run = 0;
            foreach (var interval in intervals)
            {
                // 偏差判据是严格大于容差：恰好等于容差不计。
                if (Math.Abs(interval - expectedSec) > toleranceSec)
                {
                    deviations++;
                    run++;
                    if (run > longestConsecutive) longestConsecutive = run;
                }
                else
                {
                    run = 0;
                }
            }
        }

        // 相位：首帧相对"包含首帧的那个 episode"起点的偏移。
        // 不能固定用 episodes[0]：报文首次出现在后续 episode 时，会把 episode 之间的
        // 空档也算进相位，数值会偏出几十秒。
        NmEpisode? firstEpisode = null;
        foreach (var episode in episodes)
        {
            if (episode.Start <= ordered[0] && ordered[0] <= episode.End)
            {
                firstEpisode = episode;
                break;
            }
        }
        var phase = firstEpisode is null
            ? (double?)null
            : (ordered[0] - firstEpisode.Start) * 1000.0;

        return new NmCycleStats(
            MedianMs: median * 1000.0,
            P95Ms: p95 * 1000.0,
            MinMs: sorted[0] * 1000.0,
            MaxMs: sorted[^1] * 1000.0,
            PhaseMs: phase,
            DeviationCount: deviations,
            IntervalCount: intervals.Count,
            ExpectedMs: def.CycleMs,
            ToleranceMs: toleranceMs ?? Tolerance(def.CycleMs),
            ConsecutiveDeviationCount: longestConsecutive);
    }

    /// <summary>
    /// 标记属于唤醒突发的间隔（spec §7.6）：≥2 个连续 &lt;0.5×cycle 的间隔。
    /// 返回数组下标 k 对应间隔 <c>(ordered[k-1], ordered[k]]</c>。
    /// <para>
    /// 两个锚点并用，覆盖两种切分结果：
    /// ① episode 首帧起的前缀——窗口/记录恰好从突发开始时没有“突发前那一帧”；
    /// ② episode 内部某个 ≥cycle 的静默之后的短序列——全局超时配得较大、或其它
    ///    NM 报文维持 episode 时，突发会落在 episode 中间。
    /// </para>
    /// <para>
    /// 若短序列一直持续到该报文在本 episode 的最后一帧，视为“实际周期远小于配置”
    /// 而不是突发：剔除整段会把真实的周期偏差降级成样本不足，因此保留统计事实。
    /// </para>
    /// <para>前提：<paramref name="episodes"/> 覆盖该报文的全部帧（<see cref="NmAnalyzer"/> 的调用满足）。</para>
    /// </summary>
    private static bool[] MarkBurstIntervals(
        double[] ordered, IReadOnlyList<NmEpisode> episodes, double? cycleSec)
    {
        var burst = new bool[ordered.Length];
        if (cycleSec is not { } cycle || cycle <= 0 || episodes.Count == 0) return burst;

        int cursor = 0;
        foreach (var episode in episodes.OrderBy(e => e.Start))
        {
            while (cursor < ordered.Length && ordered[cursor] < episode.Start) cursor++;
            var begin = cursor;
            while (cursor < ordered.Length && ordered[cursor] <= episode.End) cursor++;
            var end = cursor - 1;

            if (end - begin < 2) continue;

            // 锚点 ①：episode 首帧（可能正好是突发第一帧）。
            MarkShortRun(ordered, burst, begin + 1, end, cycle);

            // 锚点 ②：episode 内部静默 ≥ cycle 后重新出现的短序列。
            for (int i = begin + 1; i < end; i++)
            {
                if (ordered[i] - ordered[i - 1] >= cycle)
                    MarkShortRun(ordered, burst, i + 1, end, cycle);
            }
        }

        return burst;
    }

    /// <summary>
    /// 从 <paramref name="first"/> 开始数连续 &lt;0.5×cycle 的间隔；
    /// 仅在 ≥2 个且后面还有正常间隔时标记（避免把整段短周期当突发吞掉）。
    /// </summary>
    private static void MarkShortRun(
        double[] ordered, bool[] burst, int first, int end, double cycle)
    {
        int count = 0;
        for (int k = first; k <= end; k++)
        {
            if (ordered[k] - ordered[k - 1] >= 0.5 * cycle) break;
            count++;
        }

        var last = first + count - 1;
        if (count < 2 || last >= end) return;
        for (int k = first; k <= last; k++) burst[k] = true;
    }

    /// <summary>spec §7.4: deviation ratio > 5% or 3 consecutive deviating intervals.</summary>
    public static bool IsDeviationSignificant(NmCycleStats stats)
        => stats.IntervalCount > 0
           && (stats.DeviationCount * 20 > stats.IntervalCount
               || stats.ConsecutiveDeviationCount >= 3);

    private static bool CrossesEpisodeBoundary(double from, double to, IReadOnlyList<NmEpisode> episodes)
        => episodes.Any(e => from < e.Start && to >= e.Start || from <= e.End && to > e.End);

    private static int? Tolerance(int? cycleMs)
        => cycleMs is { } c ? Math.Max((int)Math.Round(c * 0.2), 30) : null;

    private static double Percentile(double[] sorted, double p)
    {
        var index = (int)Math.Round((sorted.Length - 1) * p);
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}
