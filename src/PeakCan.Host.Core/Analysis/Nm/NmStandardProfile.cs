namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 企标 profile 文件模型（spec §5.4）。YAML 反序列化目标，展平后产出
/// <see cref="NmProfile"/> + <see cref="NmGlobalTiming"/>。
/// </summary>
public sealed record NmStandardProfile
{
    public required string StandardId { get; init; }
    public string? Vendor { get; init; }
    public string Version { get; init; } = "1.0";
    public string? Extends { get; init; }
    public NmStandardSource? Source { get; init; }
    public NmStandardOverrides? Overrides { get; init; }
    public IReadOnlyList<NmStandardMessage>? Messages { get; init; }
    public IReadOnlyList<NmStandardNode>? Nodes { get; init; }
    public IReadOnlyList<NmRuleDef>? Rules { get; init; }

    // 便利访问器：绝大多数调用方只关心「这个 profile 声明的时序/布局/开关」，
    // 不需要知道它们存在 overrides 下。展平逻辑（S1 后续）仍读 Overrides。
    public NmStandardTimings? Timings => Overrides?.Timings;
    public NmStandardLayout? Layout => Overrides?.Layout;
    public NmStandardFeatures? Features => Overrides?.Features;
}

public sealed record NmStandardSource(string? Doc = null, string? Revision = null);

/// <summary>
/// 企标覆盖段：只写与父 profile 不同的字段。
/// <para>
/// 三态语义（spec §6.2）：<b>字段缺失 = 继承父值</b>，<b>set 块内的值 = 替换</b>，
/// <b>remove 列表 = 显式删除父值</b>。不能用「可空属性为 null」表达删除——
/// YAML 反序列化后「缺失」和「显式 null」都是 null，无法区分。
/// </para>
/// </summary>
public sealed record NmStandardOverrides
{
    public NmStandardTimings? Timings { get; init; }
    public NmStandardLayout? Layout { get; init; }
    public NmStandardFeatures? Features { get; init; }

    /// <summary>
    /// 要显式删除的父级项。支持的键：
    /// <c>bit_roles[byte.bit]</c>（如 <c>bit_roles[1.6]</c> 删除 base 的 PNI 位）、
    /// <c>rules[id]</c>、<c>messages[id]</c>、<c>nodes[name]</c>。
    /// </summary>
    public IReadOnlyList<string>? Remove { get; init; }
}

public sealed record NmStandardTimings
{
    public int? CycleMs { get; init; }
    public int? TimeoutMs { get; init; }
    public int? WaitBusSleepMs { get; init; }
    public int? RepeatMessageWithBurstMs { get; init; }
    public int? RepeatMessageWithoutBurstMs { get; init; }
    public int? ImmediateCycleMs { get; init; }
    public int? ImmediateCount { get; init; }
    public int? StartNmTxMs { get; init; }

    /// <summary>
    /// T_WakeUp。显式 alias：属性名 <c>WakeUpMs</c> 在 underscoring 约定下会映射成
    /// <c>wake_up_ms</c>，而规范里的键统一写作 <c>wakeup_ms</c>。
    /// </summary>
    [YamlDotNet.Serialization.YamlMember(Alias = "wakeup_ms")]
    public int? WakeUpMs { get; init; }
    public double? ToleranceRatio { get; init; }
    /// <summary>容差下限毫秒；不配时沿用 M1 的 30ms。</summary>
    public int? ToleranceAbsMs { get; init; }
}

public sealed record NmStandardLayout
{
    public byte? NidByte { get; init; }
    public byte? CbvByte { get; init; }
    public byte? UserDataOffset { get; init; }
    public int? UserDataLength { get; init; }
    public IReadOnlyList<NmBitRole>? BitRoles { get; init; }
}

public sealed record NmStandardFeatures
{
    public bool? ActiveWakeupBitEnabled { get; init; }
    public bool? RepeatMsgIndEnabled { get; init; }
    public bool? PnEnabled { get; init; }
}

public sealed record NmStandardMessage(
    string Name, string Id, bool Extended = false, string? Sender = null, byte Dlc = 8);

public sealed record NmStandardNode(string Name, byte? StationAddress = null);

/// <summary>
/// CBV/内容字段的一位语义。profile 声明哪一位存在，代码不假设。
/// </summary>
public sealed record NmBitRole(byte Byte, int Bit, string Name)
{
    /// <summary>人类可读语义，如「0=被动唤醒, 1=主动唤醒」。</summary>
    public string? Meaning { get; init; }

    /// <summary>位的取值约束；默认无约束（如 NMSts 的 0/1 都合法）。</summary>
    public NmBitConstraint Constraint { get; init; } = NmBitConstraint.None;
}

/// <summary>位的取值约束。</summary>
public enum NmBitConstraint
{
    /// <summary>无约束：0 和 1 都有合法语义。</summary>
    None = 0,

    /// <summary>预留位：必须为 0，违反报 <c>NM_RESERVED_BIT_SET</c>。</summary>
    MustBeZero,

    /// <summary>已定义但本项目禁用（如 RMR）：必须为 0，违反报 <c>NM_RMR_SET_WHILE_DISABLED</c>。</summary>
    MustBeZeroWhileDisabled,
}

/// <summary>一条企标规则（spec §5.3 / §7.2）。S1 只定义模型，S3/S4 实现原语。</summary>
public sealed record NmRuleDef(
    string Id,
    string Requirement,
    string Phase,
    NmRuleWhen When,
    NmRuleExpect Expect,
    string FindingCode)
{
    public string? SourceRef { get; init; }
    public NmObservability Observability { get; init; } = NmObservability.Observed;
}

/// <summary>
/// 规则条件。字段是闭集原语参数的并集——结构化而非字符串字典，
/// 类型错误在 YAML 加载期暴露（spec §5.3）。
/// </summary>
public sealed record NmRuleWhen(
    string Kind,
    byte? Byte = null,
    int? Bit = null,
    int? Length = null,
    string? Role = null,
    /// <summary>first_frame_latency 的前置状态：start_nm_tx | wakeup。</summary>
    string? LatencyKind = null,
    int? MinSilenceMs = null,
    int? CycleMs = null,
    double? Pct = null,
    int? MinFrames = null);

/// <summary>规则期望。同上，闭集原语参数的并集。</summary>
public sealed record NmRuleExpect(
    string Kind,
    string? Value = null,
    byte? Byte = null,
    int? Bit = null,
    int? Count = null,
    int? IntervalMs = null,
    double? Pct = null,
    int? MinMs = null,
    int? MaxMs = null,
    /// <summary>repeat_message_duration 的锚点：nmsts | burst_end。</summary>
    string? Anchor = null);

/// <summary>
/// 原语闭集（spec §7.2）。不在集合内的 kind 在加载期报错——
/// 静默进 Unmodeled 会让用户以为条款已被校验。
/// </summary>
public static class NmRulePrimitives
{
    public static readonly IReadOnlySet<string> WhenKinds =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "layout_check", "nid_id_mapping", "bit_rule", "cycle_tolerance",
            "burst_shape", "first_frame_latency", "repeat_message_duration",
            "nmsts_transition", "normal_resume_shape", "stop_sending", "max_gap_observe",
        };

    public static readonly IReadOnlySet<string> ExpectKinds =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "within_pct", "equals", "is_zero", "matches_id_low_byte",
            "count_equals", "interval_within_pct", "duration_within_ms",
            "present", "absent", "observe",
        };
}

/// <summary>规则的可观测性分级（spec §3）。</summary>
public enum NmObservability
{
    /// <summary>直接观测：可产 finding。</summary>
    Observed = 0,

    /// <summary>部分观测：可产 finding 但必须标推断。</summary>
    Partial = 1,

    /// <summary>不可观测：只能进 Unmodeled，禁止产 finding。</summary>
    Unobservable = 2,
}

/// <summary>profile 加载/校验失败。不静默降级——坏 profile 不允许进入分析。</summary>
public sealed class NmProfileValidationException : Exception
{
    public NmProfileValidationException(string message) : base(message) { }

    public NmProfileValidationException(string message, Exception inner) : base(message, inner) { }
}
