namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// T_REPEAT_MESSAGE 的来源（spec §7.5.3 / §7.5.5）。三态而非 bool：
/// M1 的单一值与企标的双值<b>语义不同</b>，不能用同一个开关表达。
/// </summary>
public enum NmRepeatWindowSource
{
    /// <summary>
    /// 无人声明 → 唤醒观察窗用周期（M1 最初的兜底口径）。
    /// <para>
    /// 不能用「值是否等于默认值」反推：<see cref="NmTimingDefaults"/> 在无人声明时
    /// 也会填 1700/1500，用户也可能恰好显式配了 1700。
    /// </para>
    /// </summary>
    None = 0,

    /// <summary>
    /// M1 的单一 <c>RepeatMessageMs</c>（DBC 全局属性或补全对话框输入）。
    /// <para>
    /// <b>沿用 M1 语义</b>：唤醒段 = 观测到的快发段末帧；无快发时用该值当观察窗。
    /// 这是 spec §7.5.5「无 profile 时 M1 行为不变」所保护的行为——
    /// 把 M1 配置也套上企标的「选窗」语义，会让既有项目的唤醒段突然变长。
    /// </para>
    /// </summary>
    M1Single = 1,

    /// <summary>
    /// 企标的双值 <c>repeat_message_with_burst_ms</c> / <c>repeat_message_without_burst_ms</c>。
    /// <para>
    /// <b>企标语义</b>（spec §7.5.3）：快发只用来<b>选窗</b>，唤醒段是整个
    /// Repeat Message State 的推断时长（1700 或 1500）。§7.5 的错误表正是在纠正
    /// 「有快发时只认突发段，把剩下 2.14s 的 RMS 画成发送中」。
    /// </para>
    /// </summary>
    StandardDual = 2,
}

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
    int ToleranceAbsMs = NmTimingDefaults.ToleranceAbsMs,
    /// <summary>
    /// T_REPEAT_MESSAGE 的来源；<see cref="NmRepeatWindowSource.None"/> = 无人声明。
    /// 见 <see cref="NmEffectiveTiming.RepeatWindowSource"/>。
    /// </summary>
    NmRepeatWindowSource RepeatWindowSource = NmRepeatWindowSource.None,
    /// <summary>
    /// 是否有任何一层**显式声明**了快发周期 <c>ImmediateCycleMs</c>。
    /// 见 <see cref="NmEffectiveTiming.ImmediateCycleDeclared"/>。
    /// </summary>
    bool ImmediateCycleDeclared = false)
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
    int ToleranceAbsMs = NmTimingDefaults.ToleranceAbsMs,
    /// <summary>
    /// T_REPEAT_MESSAGE 的来源，决定 Activity 段唤醒窗的**语义**
    /// （spec §7.5.3 / §7.5.5）。三态见 <see cref="NmRepeatWindowSource"/>：
    /// <list type="bullet">
    /// <item><see cref="NmRepeatWindowSource.None"/> → 用周期当观察窗（M1 最初口径）。</item>
    /// <item><see cref="NmRepeatWindowSource.M1Single"/> → 唤醒段 = 快发段末帧（M1 语义）。</item>
    /// <item><see cref="NmRepeatWindowSource.StandardDual"/> → 快发只选窗，唤醒段 = 1700/1500 整段。</item>
    /// </list>
    /// <para>
    /// <b>不能用「值是否等于默认值」反推</b>：无人声明时默认值也是 1700/1500。
    /// 必须由解析路径显式带上来（见 <see cref="NmTimingDefaults.FromProfile"/>）。
    /// </para>
    /// </summary>
    NmRepeatWindowSource RepeatWindowSource = NmRepeatWindowSource.None,
    /// <summary>
    /// 是否有任何一层**显式声明**了快发周期 <c>ImmediateCycleMs</c>。
    /// <para>
    /// spec §7.5.3.1 的回退表以「<c>ImmediateCycleMs</c> 是否配置」为分界：
    /// 已配置 → 用 <c>≤ 2.0×ImmediateCycleMs</c> 判快发；未配置 → 用 M1 的
    /// <c>&lt; 0.5×CycleMs</c>。
    /// </para>
    /// <para>
    /// <b>不能用「值是否为 0」判断</b>：默认值就是 20，未声明时也是 20。
    /// 判错会让无 profile 的 Activity 段把 50ms 间隔当成正常周期（M1 视为快发），
    /// 唤醒段被拉到末帧——spec §7.5.5 的 M1 回归门会立刻红。
    /// </para>
    /// </summary>
    bool ImmediateCycleDeclared = false)
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
            ToleranceAbsMs: standard?.ToleranceAbsMs ?? ToleranceAbsMs,
            // 三态：M1 单一值优先，其次企标双值，都没有则为 None。
            // M1 与企标的语义不同（见 NmRepeatWindowSource），不能合并成一个 bool。
            RepeatWindowSource: m1Repeat is not null
                ? NmRepeatWindowSource.M1Single
                : standard?.RepeatMessageWithBurstMs is not null
                  || standard?.RepeatMessageWithoutBurstMs is not null
                    ? NmRepeatWindowSource.StandardDual
                    : NmRepeatWindowSource.None,
            ImmediateCycleDeclared: m1?.ImmediateCycleMs is not null
                || standard?.ImmediateCycleMs is not null);
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
            ToleranceAbsMs: global.ToleranceAbsMs,
            RepeatWindowSource: global.RepeatWindowSource,
            ImmediateCycleDeclared: global.ImmediateCycleDeclared);
}
