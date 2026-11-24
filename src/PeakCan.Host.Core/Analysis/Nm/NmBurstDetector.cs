namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 快发段检测结果（spec §7.4）。「是否为候选」与「是否合规」是两个独立答案：
/// <see cref="IsCandidate"/> 由宽松阈值检出，<see cref="IsCompliant"/> 只在严格判定成立时为 true。
/// </summary>
/// <remarks>
/// <para>10 帧 = 9 个间隔：<see cref="IntervalCount"/> = <see cref="FrameCount"/> − 1。
/// 跨度取首末帧时刻之差（真实 trace §14.3：181.497ms），禁止用「帧数 × 周期」推算。</para>
/// <para><see cref="Detector"/> 取值：<c>immediate</c> | <c>cycle_ratio_fallback</c> | <c>none</c>。</para>
/// <para><see cref="IsIndeterminate"/> 为 true 表示段尾触到 episode/窗口边界，实测时长只是下限；
/// 此时既不得判合规，也不得判不合规。</para>
/// </remarks>
public sealed record BurstDetection(
    int FrameCount,
    int IntervalCount,
    double Start,
    double End,
    bool IsCandidate,
    bool IsCompliant,
    string Detector,
    bool IsIndeterminate);

/// <summary>
/// 快发检测的唯一实现（spec §7.4 / §7.5.3.1）。所有调用方（活动段、主动唤醒候选、
/// 快发形态合规）必须复用本类，不得各自写阈值：
/// 检测用宽松的 2.0×ImmediateCycleMs 抓候选，合规用严格的 1±ToleranceRatio 判形态。
/// </summary>
public static class NmBurstDetector
{
    /// <summary>检测阶段的宽松倍数：只为抓到候选，不作合规结论（spec §7.3 阶段 1）。</summary>
    private const double CandidateFactor = 2.0;

    /// <summary>
    /// 回退路径（无 ImmediateCycleMs）沿用的 M1 阈值：0.5×cycle。
    /// 比较方向也是 M1 口径——严格 <，即恰为 0.5×cycle 时算作断点。
    /// </summary>
    private const double CycleRatioFactor = 0.5;

    /// <summary>浮点边界比较余量（秒）。</summary>
    private const double BoundaryEpsilon = 1e-9;

    /// <summary>整段报文内检测；不做 episode 边界截断判定。</summary>
    public static BurstDetection Detect(IReadOnlyList<double> timestamps, NmEffectiveTiming timing)
        => DetectInEpisode(timestamps, double.NegativeInfinity, double.PositiveInfinity, timing);

    /// <summary>
    /// 在 episode 内检测快发段。段尾落在 <paramref name="episodeEnd"/> 上时，
    /// 后续帧可能被窗口切掉，返回 <see cref="BurstDetection.IsIndeterminate"/>。
    /// </summary>
    /// <param name="anchorAtStart">
    /// true = 只认「从 episode 首帧起算」的连续短间隔（唤醒段终点的语义，spec §7.3）；
    /// false = 取整段内最长的一段（规则求值的语义）。
    /// <para>
    /// 两者不能合并：报文在 episode 中途又出现一段快发时，「最长段」会把唤醒终点
    /// 跳到中段，画出「先正常发送、后唤醒」的时间条。
    /// </para>
    /// </param>
    /// <param name="preferImmediateCycle">
    /// true = 优先用 <c>ImmediateCycleMs</c> 判短间隔（企标已声明快发周期）；
    /// false = 强制走 M1 的 <c>0.5×CycleMs</c> 回退口径。
    /// <para>
    /// <b>不能靠「ImmediateCycleMs 是否为 0」推断</b>：它有默认值 20，未声明时也是 20。
    /// Activity 段在未选企标时必须用 M1 口径，否则无 profile 的行为会变（spec §7.5.5）。
    /// </para>
    /// </param>
    public static BurstDetection DetectInEpisode(
        IReadOnlyList<double> timestamps,
        double episodeStart,
        double episodeEnd,
        NmEffectiveTiming timing,
        bool anchorAtStart = false,
        bool preferImmediateCycle = true)
    {
        var none = new BurstDetection(0, 0, 0, 0, false, false, "none", false);

        // 两个阈值都缺失：快发判据无法确定，明确返回 indeterminate，不猜。
        if (timing.ImmediateCycleMs <= 0 && timing.CycleMs <= 0)
            return none with { IsIndeterminate = true };

        // 只分析落在本 episode 内的帧；边界为闭区间。
        //
        // **必须去重**：重复时间戳是同一时刻的重复帧，会产生 delta=0 的假短间隔，
        // 把「3 帧」数成「4 帧」、把合规快发判成不合规。
        // 这与 M1 的 ResolveWakeupEnd 不同——那里不去重是 M1 自己的缺陷
        // （提交 98acb20a 修的就是这一类），§7.5.5 的「行为不变」指结论口径，
        // 不包含把已知缺陷一并复刻。
        var ordered = timestamps
            .Where(t => t >= episodeStart && t <= episodeEnd)
            .Distinct()
            .OrderBy(t => t)
            .ToArray();
        if (ordered.Length < 2) return none;

        // 检测阈值与检测器名：优先用 ImmediateCycleMs，缺失或被禁用时回退到周期比例。
        double detectThreshold;
        string detector;
        var canJudgeCompliance = false;

        if (preferImmediateCycle && timing.ImmediateCycleMs > 0)
        {
            detectThreshold = timing.ImmediateCycleMs * CandidateFactor / 1000.0;
            detector = "immediate";
            canJudgeCompliance = true;
        }
        else
        {
            detectThreshold = timing.CycleMs * CycleRatioFactor / 1000.0;
            detector = "cycle_ratio_fallback";
        }

        // 找连续短间隔。anchorAtStart 时只认从首帧起算的那一段，否则取最长的一段。
        // 起止用「间隔两端」的帧时刻。
        var bestStart = -1;
        var bestCount = 0;
        var runStart = -1;
        var runCount = 0;

        for (var i = 1; i < ordered.Length; i++)
        {
            var delta = ordered[i] - ordered[i - 1];

            // 去重后 delta 恒 > 0，不再需要跳过重复帧。

            // 阈值比较方向按路径区分：immediate 走 spec §7.4 的 ≤；
            // 回退路径沿用 M1 的严格 <（NmActivityBuilder 在 delta >= 0.5×cycle 时断开）。
            var isShortInterval = canJudgeCompliance
                ? delta <= detectThreshold
                : delta < detectThreshold;

            if (isShortInterval)
            {
                if (runStart < 0) runStart = i - 1;
                runCount++;
                if (runCount > bestCount)
                {
                    bestCount = runCount;
                    bestStart = runStart;
                }
            }
            else
            {
                // 锚定首帧时，第一个长间隔就是这段快发的终点，后面再有快发也不算。
                if (anchorAtStart) break;
                runStart = -1;
                runCount = 0;
            }
        }

        // 候选要求 ≥2 个间隔（spec §7.3 阶段 1：连续 ≥3 帧）。
        if (bestCount < 2 || bestStart < 0) return none;

        // 锚定首帧时，快发必须从首帧起算；中途出现的长段不能当唤醒终点。
        if (anchorAtStart && bestStart != 0) return none;

        var start = ordered[bestStart];
        var end = ordered[bestStart + bestCount];
        var frameCount = bestCount + 1;

        // 合规判定只在 immediate 检测器下做：回退路径证明不了帧数语义（N_ImmediateNM_TIMES）。
        var isCompliant = false;
        if (canJudgeCompliance && frameCount == timing.ImmediateCount)
        {
            var lo = timing.ImmediateCycleMs * (1 - timing.ToleranceRatio) / 1000.0;
            var hi = timing.ImmediateCycleMs * (1 + timing.ToleranceRatio) / 1000.0;
            isCompliant = true;
            for (var i = bestStart + 1; i <= bestStart + bestCount; i++)
            {
                var delta = ordered[i] - ordered[i - 1];
                if (delta < lo || delta > hi) { isCompliant = false; break; }
            }
        }

        // 段尾触到 episode/窗口边界：后面可能还有快发帧被切断，时长只是下限。
        var truncated = end >= episodeEnd - BoundaryEpsilon;

        return new BurstDetection(
            frameCount, bestCount, start, end,
            IsCandidate: true,
            IsCompliant: isCompliant && !truncated,
            Detector: detector,
            IsIndeterminate: truncated);
    }
}
