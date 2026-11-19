namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 逐报文周期统计。规则见 spec §7.4：剔除跨 episode 边界与超过超时阈值的间隔；
/// 有效样本 &lt; 5 时只报样本不足，不下偏差结论。
/// </summary>
public static class NmCycleAnalyzer
{
    private const int MinimumSamples = 5;

    public static NmCycleStats Analyze(
        IReadOnlyList<double> timestamps,
        NmMessageDef def,
        IReadOnlyList<NmEpisode> episodes)
    {
        var ordered = timestamps.OrderBy(t => t).ToArray();
        var timeoutSec = (def.TimeoutMs ?? int.MaxValue) / 1000.0;

        var intervals = new List<double>();
        for (int i = 1; i < ordered.Length; i++)
        {
            var delta = ordered[i] - ordered[i - 1];
            // 同一时间戳的重复帧不构成周期样本。
            if (delta <= 0) continue;
            // 静默超过超时阈值的间隔已被 episode 切分吃掉，不算周期样本。
            if (delta > timeoutSec) continue;
            if (CrossesEpisodeBoundary(ordered[i - 1], ordered[i], episodes)) continue;
            intervals.Add(delta);
        }

        if (intervals.Count < MinimumSamples)
        {
            // 样本不足：只报计数，中位数/相位/偏差一律留空，避免下结论。
            return new NmCycleStats(null, null, null, null, null, 0,
                IntervalCount: intervals.Count,
                ExpectedMs: def.CycleMs,
                ToleranceMs: Tolerance(def.CycleMs));
        }

        var sorted = intervals.OrderBy(x => x).ToArray();
        var median = Percentile(sorted, 0.5);
        var p95 = Percentile(sorted, 0.95);

        int deviations = 0;
        if (def.CycleMs is { } expected)
        {
            var toleranceMs = Tolerance(expected)!.Value;
            var expectedSec = expected / 1000.0;
            var toleranceSec = toleranceMs / 1000.0;
            // 偏差判据是严格大于容差：恰好等于容差不计。
            deviations = intervals.Count(x => Math.Abs(x - expectedSec) > toleranceSec);
        }

        // 相位：首个报文相对首个 episode 起点的偏移。
        var phase = episodes.Count > 0
            ? (ordered[0] - episodes[0].Start) * 1000.0
            : 0.0;

        return new NmCycleStats(
            MedianMs: median * 1000.0,
            P95Ms: p95 * 1000.0,
            MinMs: sorted[0] * 1000.0,
            MaxMs: sorted[^1] * 1000.0,
            PhaseMs: phase,
            DeviationCount: deviations,
            IntervalCount: intervals.Count,
            ExpectedMs: def.CycleMs,
            ToleranceMs: Tolerance(def.CycleMs));
    }

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
