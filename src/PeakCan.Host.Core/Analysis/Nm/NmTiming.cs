namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// profile 展平后的全局时序（spec §5.2）。**不含报文级覆盖**；
/// 分析阶段必须用 <see cref="NmTiming.Resolve"/> 得到的 <see cref="NmEffectiveTiming"/>。
/// </summary>
public sealed record NmGlobalTiming(
    int CycleMs,
    int TimeoutMs,
    int WaitBusSleepMs,
    int RepeatMessageWithBurstMs,
    int RepeatMessageWithoutBurstMs,
    int ImmediateCycleMs,
    int ImmediateCount,
    int StartNmTxMs,
    int WakeUpMs,
    double ToleranceRatio,
    // M1 容差是 max(ratio×cycle, absMs)：只有比例会丢下限，短周期下判定过严。
    int ToleranceAbsMs = NmTimingDefaults.ToleranceAbsMs)
{
    /// <summary>周期容差毫秒：max(ratio×cycle, 下限)。</summary>
    public int ToleranceMsFor(int cycleMs)
        => Math.Max((int)Math.Round(cycleMs * ToleranceRatio), ToleranceAbsMs);
}

/// <summary>
/// 单报文的生效时序：报文级覆盖已应用。所有分析阶段（含 Activity 段）必须用这个，
/// 否则会丢掉 DBC 报文级 <c>GenMsgCycleTime</c> / <c>NmAsrCanMsgCycleTime</c> 的优先级。
/// </summary>
public sealed record NmEffectiveTiming(
    int CycleMs,
    int TimeoutMs,
    int WaitBusSleepMs,
    int RepeatMessageWithBurstMs,
    int RepeatMessageWithoutBurstMs,
    int ImmediateCycleMs,
    int ImmediateCount,
    int StartNmTxMs,
    int WakeUpMs,
    double ToleranceRatio,
    int ToleranceAbsMs = NmTimingDefaults.ToleranceAbsMs)
{
    /// <summary>周期容差毫秒：max(ratio×cycle, 下限)。与 M1 的 Tolerance() 同口径。</summary>
    public int ToleranceMsFor(int cycleMs)
        => Math.Max((int)Math.Round(cycleMs * ToleranceRatio), ToleranceAbsMs);
}

/// <summary>M1 默认值 + 唯一默认值注入点（spec §5.2.1）。</summary>
public static class NmTimingDefaults
{
    public const int CycleMs = 500;
    public const int TimeoutMs = 2000;
    public const int WaitBusSleepMs = 2000;
    public const int RepeatMessageWithBurstMs = 1700;
    public const int RepeatMessageWithoutBurstMs = 1500;
    public const int ImmediateCycleMs = 20;
    public const int ImmediateCount = 10;
    public const int StartNmTxMs = 10;
    public const int WakeUpMs = 120;

    /// <summary>M1 的容差是 max(0.2×cycle, 30ms)；吉利 profile 显式配 0.1 才收紧。</summary>
    public const double ToleranceRatio = 0.2;

    /// <summary>M1 容差下限：短周期下只按比例会过严（如 100ms 周期 → 20ms &lt; 30ms）。</summary>
    public const int ToleranceAbsMs = 30;

    /// <summary>
    /// 唯一默认值注入点。优先级：<b>M1 项目层（DBC/sidecar）&gt; OEM 企标 &gt; M1 默认</b>
    /// （spec §6.2「项目 &gt; 企标 &gt; 基础」）。
    /// <para>
    /// <paramref name="m1"/> 是 <see cref="NmProfile.Timings"/>：它承载 DBC 全局 NM 属性
    /// 与补全对话框/sidecar 的用户输入，属于「项目覆盖」层，必须优先于企标。
    /// 忽略它会让 episode 切分阈值回归（DBC 写 1000ms 却被当成 2000ms）。
    /// </para>
    /// </summary>
    public static NmGlobalTiming FromProfile(NmTimings? m1, NmStandardTimings? standard = null)
    {
        // M1 的单一 RepeatMessageMs 同时代表含/不含快发两种观察窗。
        var m1Repeat = m1?.RepeatMessageMs;
        var withBurst = m1Repeat
            ?? standard?.RepeatMessageWithBurstMs
            ?? RepeatMessageWithBurstMs;
        var withoutBurst = m1Repeat
            ?? standard?.RepeatMessageWithoutBurstMs
            ?? RepeatMessageWithoutBurstMs;

        return new NmGlobalTiming(
            CycleMs: m1?.CycleMs ?? standard?.CycleMs ?? CycleMs,
            TimeoutMs: m1?.TimeoutMs ?? standard?.TimeoutMs ?? TimeoutMs,
            WaitBusSleepMs: m1?.WaitBusSleepMs ?? standard?.WaitBusSleepMs ?? WaitBusSleepMs,
            RepeatMessageWithBurstMs: withBurst,
            RepeatMessageWithoutBurstMs: withoutBurst,
            ImmediateCycleMs: m1?.ImmediateCycleMs ?? standard?.ImmediateCycleMs ?? ImmediateCycleMs,
            ImmediateCount: m1?.ImmediateCount ?? standard?.ImmediateCount ?? ImmediateCount,
            StartNmTxMs: standard?.StartNmTxMs ?? StartNmTxMs,
            WakeUpMs: standard?.WakeUpMs ?? WakeUpMs,
            ToleranceRatio: standard?.ToleranceRatio ?? ToleranceRatio,
            // 吉利 profile 只配 ratio（0.1），不配下限时仍保留 M1 的 30ms 下限。
            ToleranceAbsMs: standard?.ToleranceAbsMs ?? ToleranceAbsMs);
    }
}

/// <summary>全局 → 报文级生效时序。</summary>
public static class NmTiming
{
    /// <summary>
    /// 报文级字段优先，其次全局（与现有 NmAnalyzer.cs:255 的 <c>def.CycleMs ?? cycleMs</c> 同义）。
    /// </summary>
    public static NmEffectiveTiming Resolve(NmMessageDef def, NmGlobalTiming global)
        => new(
            CycleMs: def.CycleMs ?? global.CycleMs,
            TimeoutMs: def.TimeoutMs ?? global.TimeoutMs,
            WaitBusSleepMs: global.WaitBusSleepMs,
            RepeatMessageWithBurstMs: global.RepeatMessageWithBurstMs,
            RepeatMessageWithoutBurstMs: global.RepeatMessageWithoutBurstMs,
            ImmediateCycleMs: global.ImmediateCycleMs,
            ImmediateCount: global.ImmediateCount,
            StartNmTxMs: global.StartNmTxMs,
            WakeUpMs: global.WakeUpMs,
            ToleranceRatio: global.ToleranceRatio,
            ToleranceAbsMs: global.ToleranceAbsMs);
}
