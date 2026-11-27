using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>一个非诊断 CAN ID 的发送规律。</summary>
public enum UdsBusTrafficPattern
{
    /// <summary>间隔稳定，典型周期报文。</summary>
    Periodic,

    /// <summary>间隔忽长忽短，事件触发。</summary>
    Sporadic,

    /// <summary>帧数太少，判断不了规律。</summary>
    SingleShot,
}

/// <summary>
/// 一个 CAN ID 的一段连续活动。
/// <para>
/// <b>为什么要有它：</b>只报「首帧时间–末帧时间」会把中间的大段静默掩盖掉。
/// 一个 ID 在 0.54s 和 25.41s 各发一帧，区间写法看起来像「整段都在活动」，
/// 实际中间 24.9 秒什么都没发。
/// </para>
/// </summary>
/// <param name="Start">该段首帧时间。</param>
/// <param name="End">该段末帧时间。</param>
/// <param name="Count">该段内的帧数。</param>
public sealed record UdsTrafficBurst(double Start, double End, int Count)
{
    public double DurationSeconds => End - Start;
}

/// <summary>
/// 背景流量的一个 CAN ID。
/// <para>
/// <b>为什么不叫「噪声」：</b>这些帧本身完全正常，是总线上其它 ECU 的广播。
/// 把它们叫噪声会让用户以为有问题，而真正的问题帧（地址匹配但拼不成 PDU）
/// 被混在同一个列表里淹掉。
/// </para>
/// </summary>
public sealed record UdsBusTrafficId(
    uint Id,
    bool IsExtended,
    int Count,
    double FirstTimestamp,
    double LastTimestamp,
    UdsBusTrafficPattern Pattern,
    double AverageIntervalMs)
{
    /// <summary>
    /// 该 ID 的连续活动段。段与段之间的空隙就是静默。
    /// 空列表表示未做分段（旧构造点）。
    /// </summary>
    public IReadOnlyList<UdsTrafficBurst> Bursts { get; init; } = Array.Empty<UdsTrafficBurst>();

    /// <summary>
    /// 该 ID 最长的静默秒数。0 = 全程连续。
    /// <b>这是「中间有没有断」的直接答案</b>，首末区间给不出来。
    /// </summary>
    public double MaxSilenceSeconds { get; init; }

    /// <summary>
    /// 该 ID 是否在中途停过。为 true 时 UI 必须显示分段，不能只显示首末时间。
    /// </summary>
    public bool HasGaps => Bursts.Count > 1;
}

/// <summary>
/// 总线级静默窗口：这段时间里<b>所有</b>其它 ECU 的报文都停了。
/// <para>
/// 刷写期间总线被诊断报文独占是正常现象，但用户需要看到这个窗口存在——
/// 否则会以为「日志没记全」或者「ECU 掉线了」。
/// </para>
/// </summary>
public sealed record UdsSilenceWindow(double Start, double End)
{
    public double DurationSeconds => End - Start;
}

/// <summary>
/// 非诊断报文的整体画像。
/// <para>
/// 替换原来的「Top-20 个 ID × 帧数」：那个列表说明不了任何事——
/// 20 个周期广播各列一行，用户看不出总线忙不忙、有没有异常流量。
/// </para>
/// </summary>
public sealed record UdsBusTrafficProfile(
    IReadOnlyList<UdsBusTrafficId> Ids,
    int TotalFrames,
    int DistinctIds,
    double WindowSeconds,
    double FramesPerSecond,
    int PeriodicCount,
    int SporadicCount,
    int SingleShotCount)
{
    public static UdsBusTrafficProfile Empty { get; } = new(
        Array.Empty<UdsBusTrafficId>(), 0, 0, 0, 0, 0, 0, 0);

    /// <summary>偶发流量更值得关注——「只在某个时刻出现」常与故障相关。</summary>
    public bool HasSporadic => SporadicCount > 0;

    /// <summary>总线级静默窗口（所有背景流量同时消失的时段）。</summary>
    public IReadOnlyList<UdsSilenceWindow> SilenceWindows { get; init; }
        = Array.Empty<UdsSilenceWindow>();

    /// <summary>静默总时长。用户一眼能看出「总线安静了多久」。</summary>
    public double TotalSilenceSeconds => SilenceWindows.Sum(w => w.DurationSeconds);

    /// <summary>是否有总线级静默。</summary>
    public bool HasSilence => SilenceWindows.Count > 0;
}

/// <summary>
/// 给非诊断报文做画像：多少 ID、各自什么规律、总线多忙。
/// <para>
/// 判周期用「间隔中位数与离散度」，不用逐帧相等——真实总线会丢帧，
/// 严格判等会把所有正常报文标成偶发。
/// </para>
/// </summary>
public sealed class UdsBusTrafficProfiler
{
    /// <summary>判定周期所需的最小帧数。少于这个数看不出规律。</summary>
    private const int MinFramesForPattern = 4;

    /// <summary>
    /// 周期判据：间隔的变异系数（标准差 / 中位数）低于这个值算稳定。
    /// 0.35 能容忍丢一帧（间隔翻倍后中位数不变，标准差上升但不至于越界）。
    /// </summary>
    private const double PeriodicCoefficientOfVariation = 0.35;

    /// <summary>
    /// 静默判据：间隔超过总线典型间隔的这个倍数即为一段静默。
    /// 取 5 倍——周期报文偶尔丢两三帧不算「停发」，连续丢 5 帧才是。
    /// </summary>
    private const double BurstGapFactor = 5.0;

    /// <summary>静默阈值下限，避免极快总线上的微小抖动被当成静默。</summary>
    private const double MinSilenceSeconds = 0.2;

    public UdsBusTrafficProfile Profile(
        IReadOnlyList<ReplayFrame> frames,
        IReadOnlyList<UdsFrameClassification> classes)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(classes);
        if (frames.Count != classes.Count)
            throw new ArgumentException("frames and classes must be parallel", nameof(classes));

        var groups = new Dictionary<(uint Id, bool IsExtended), List<double>>();
        for (var i = 0; i < frames.Count; i++)
        {
            if (classes[i].Class != UdsFrameClass.NonDiagnostic) continue;
            var f = frames[i];
            var key = (f.Id, f.IsExtended);
            if (!groups.TryGetValue(key, out var list))
                groups[key] = list = new List<double>();
            list.Add(f.Timestamp);
        }

        if (groups.Count == 0) return UdsBusTrafficProfile.Empty;

        var ids = new List<UdsBusTrafficId>(groups.Count);
        var periodic = 0;
        var sporadic = 0;
        var singleShot = 0;

        // 先算总线的典型帧间隔，静默阈值由它推导——写死阈值在慢总线上会误报。
        var allTimes = groups.Values.SelectMany(t => t).OrderBy(t => t).ToArray();
        var busInterval = TypicalInterval(allTimes);
        var silenceThreshold = Math.Max(busInterval * BurstGapFactor, MinSilenceSeconds);

        foreach (var ((id, isExtended), times) in groups)
        {
            times.Sort();
            var (pattern, avgInterval) = ClassifyPattern(times);
            switch (pattern)
            {
                case UdsBusTrafficPattern.Periodic: periodic++; break;
                case UdsBusTrafficPattern.Sporadic: sporadic++; break;
                default: singleShot++; break;
            }

            var bursts = SplitBursts(times, silenceThreshold);
            var maxSilence = bursts.Count < 2
                ? 0
                : bursts.Zip(bursts.Skip(1), (a, b) => b.Start - a.End).Max();

            ids.Add(new(id, isExtended, times.Count, times[0], times[^1], pattern, avgInterval)
            {
                Bursts = bursts,
                MaxSilenceSeconds = maxSilence,
            });
        }

        var ordered = ids
            .OrderByDescending(x => x.HasGaps)
            .ThenByDescending(x => x.Pattern == UdsBusTrafficPattern.Sporadic)
            .ThenByDescending(x => x.Count)
            .ThenBy(x => x.Id)
            .ToArray();

        var total = ids.Sum(x => x.Count);
        var windowStart = ids.Min(x => x.FirstTimestamp);
        var windowEnd = ids.Max(x => x.LastTimestamp);
        var window = Math.Max(windowEnd - windowStart, 0);
        var rate = window > 0 ? total / window : 0;

        return new(ordered, total, ids.Count, window, rate, periodic, sporadic, singleShot)
        {
            SilenceWindows = FindBusSilence(allTimes, silenceThreshold),
        };
    }

    /// <summary>
    /// 把某个 ID 的时间点切成连续活动段。
    /// 间隔超过阈值即为新的一段——这正是「中间停过」的证据。
    /// </summary>
    private static IReadOnlyList<UdsTrafficBurst> SplitBursts(
        List<double> sortedTimes, double gapThreshold)
    {
        var bursts = new List<UdsTrafficBurst>();
        var start = sortedTimes[0];
        var last = sortedTimes[0];
        var count = 1;

        for (var i = 1; i < sortedTimes.Count; i++)
        {
            var t = sortedTimes[i];
            if (t - last > gapThreshold)
            {
                bursts.Add(new(start, last, count));
                start = t;
                count = 0;
            }
            last = t;
            count++;
        }

        bursts.Add(new(start, last, count));
        return bursts;
    }

    /// <summary>
    /// 总线级静默：<b>所有</b>背景流量同时消失的时段。
    /// 只有部分 ID 停发不算——那时总线仍在工作。
    /// </summary>
    private static IReadOnlyList<UdsSilenceWindow> FindBusSilence(
        double[] allSortedTimes, double gapThreshold)
    {
        if (allSortedTimes.Length < 2) return Array.Empty<UdsSilenceWindow>();

        var windows = new List<UdsSilenceWindow>();
        for (var i = 1; i < allSortedTimes.Length; i++)
        {
            var gap = allSortedTimes[i] - allSortedTimes[i - 1];
            if (gap > gapThreshold)
                windows.Add(new(allSortedTimes[i - 1], allSortedTimes[i]));
        }
        return windows;
    }

    /// <summary>
    /// 总线典型帧间隔。
    /// <para>
    /// <b>不能用全体间隔的中位数：</b>一个只有两帧、相隔 25 秒的稀疏 ID 会把
    /// 中位数拉到 25 秒，阈值随之变成 125 秒，于是这个 ID 永远不会被判为
    /// 「中间停过」——正是要检测的情况。
    /// </para>
    /// <para>
    /// 改用<b>下四分位</b>：取最密的那批间隔，代表总线的真实节奏。
    /// 稀疏 ID 的孤立长间隔不会拉高它。
    /// </para>
    /// </summary>
    private static double TypicalInterval(double[] sortedTimes)
    {
        if (sortedTimes.Length < 2) return 0;
        var intervals = new double[sortedTimes.Length - 1];
        for (var i = 1; i < sortedTimes.Length; i++)
            intervals[i - 1] = sortedTimes[i] - sortedTimes[i - 1];
        Array.Sort(intervals);

        // 只有两帧时没有「多数间隔」可言：不能拿那唯一的间隔当基准
        // （否则 25 秒的间隔会推出 125 秒的阈值，这个 ID 永远不被判为断档）。
        // 返回 0，由调用方回落到 MinSilenceSeconds。
        if (intervals.Length == 1) return 0;

        return intervals[intervals.Length / 4];
    }

    private static (UdsBusTrafficPattern Pattern, double AverageIntervalMs) ClassifyPattern(
        List<double> sortedTimes)
    {
        if (sortedTimes.Count < MinFramesForPattern)
            return (UdsBusTrafficPattern.SingleShot, 0);

        var intervals = new double[sortedTimes.Count - 1];
        for (var i = 1; i < sortedTimes.Count; i++)
            intervals[i - 1] = sortedTimes[i] - sortedTimes[i - 1];

        var median = Median(intervals);
        if (median <= 0) return (UdsBusTrafficPattern.SingleShot, 0);

        var mean = intervals.Average();
        var variance = intervals.Sum(x => (x - mean) * (x - mean)) / intervals.Length;
        var cv = Math.Sqrt(variance) / median;

        var pattern = cv <= PeriodicCoefficientOfVariation
            ? UdsBusTrafficPattern.Periodic
            : UdsBusTrafficPattern.Sporadic;

        return (pattern, mean * 1000);
    }

    private static double Median(double[] values)
    {
        var copy = (double[])values.Clone();
        Array.Sort(copy);
        var mid = copy.Length / 2;
        return copy.Length % 2 == 0 ? (copy[mid - 1] + copy[mid]) / 2 : copy[mid];
    }
}
