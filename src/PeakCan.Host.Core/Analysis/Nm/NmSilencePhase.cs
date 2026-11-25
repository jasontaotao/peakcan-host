namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 全网 NM 静默空档内的相位窗口（2026-11-25 spec）。
/// <para>
/// 相位是**按 NM 超时边界与总线活动推断的窗口**，不是实测的节点状态：
/// 内部定时器与状态机仍不可观测。消费方必须把它当 <c>inferred</c>。
/// </para>
/// </summary>
public enum NmSilencePhaseKind
{
    /// <summary>NM 停发后的首个 TimeoutMs 窗口（正常释放下为 RSS）。</summary>
    ReleaseWait = 0,

    /// <summary>随后 WaitBusSleepMs 窗口（正常释放下为预睡眠）。</summary>
    PrepareSleep = 1,

    /// <summary>再之后的剩余静默（正常释放下为睡眠）。</summary>
    Sleep = 2,

    /// <summary>无法判定（无帧 / 未归属 / 与行为矛盾 / 默认时序）。</summary>
    Unknown = 3,
}

/// <summary>相位判定依据。展示层可据此给出不同的证据行。</summary>
public enum NmSilenceBasis
{
    /// <summary>NM 发送者仍有应用报文 → 该发送者仍处于网络模式。</summary>
    NmSenderStillActive = 0,

    /// <summary>发送者应用报文在超时后仅剩清缓存尾巴，之后总线静止。</summary>
    NmSenderStoppedAfterDrain = 1,

    /// <summary>只有其它发送者在发；本网络发送者状态不可判定。</summary>
    OtherSenderActive = 2,

    /// <summary>区间内没有任何选中帧。</summary>
    NoBusActivity = 3,

    /// <summary>发送者无法归属。</summary>
    Unattributed = 4,

    /// <summary>停发前最后一帧处于 Repeat Message State，不是合规释放。</summary>
    NmStsRepeatMessage = 5,

    /// <summary>超时后仍有帧，与预睡眠/睡眠的「总线静止」要求矛盾。</summary>
    AppTxAfterTimeout = 6,

    /// <summary>WaitBusSleep 未显式配置，不推断睡眠边界。</summary>
    WaitBusSleepDefaulted = 7,

    /// <summary>
    /// 窗口短于清缓存宽限，观察不到发送者是否在宽限后停发。
    /// 有帧时不得升级为 PrepareSleep（HIGH：短窗口连续流量曾被误判）。
    /// </summary>
    WindowTooShort = 8,
}

/// <summary>停发前最后一帧的 NMSts 证据（仅作佐证，不改变 inferred）。</summary>
public enum NmStsEvidence
{
    /// <summary>profile 未声明 NmSts 位角色。</summary>
    NotAvailable = 0,

    /// <summary>全部可读末帧均为 Ready Sleep（NMSts=1）。</summary>
    ReadySleepAtLastFrame = 1,

    /// <summary>任一可读末帧为 Repeat Message State（NMSts=0）。</summary>
    RepeatMessageAtLastFrame = 2,

    /// <summary>位角色存在，但末帧 DLC 不足读不出。</summary>
    UnreadableAtLastFrame = 3,

    /// <summary>多个报文的读数不一致。</summary>
    Mixed = 4,
}

/// <summary>相位内的发送者活动摘要。</summary>
public sealed record NmSenderActivity(string Sender, bool IsNmSender, int FrameCount);

/// <summary>
/// 一个静默相位窗口。所有相位都是 <c>inferred</c>——边界来自配置时序与观测到的活动，
/// 不是节点内部状态的实测值。
/// </summary>
public sealed record NmSilencePhase(
    double Start,
    double End,
    NmSilencePhaseKind Kind,
    NmSilenceBasis Basis,
    int NonNmFrameCount,
    int NmSenderAppFrameCount,
    int OtherSenderFrameCount,
    int UnattributedFrameCount,
    double? BusMaxGapSec,
    NmStsEvidence NmSts,
    bool IsWindowTruncated,
    int AfterEpisodeIndex,
    string Detail)
{
    /// <summary>证据等级固定为 inferred；相位没有 measured 形态。</summary>
    public string EvidenceKind => "inferred";

    /// <summary>与 <see cref="EvidenceKind"/> 同义的布尔形态，供导出/UI 直接消费。</summary>
    public bool IsInference => true;

    /// <summary>Top 发送者（按帧数降序，最多 8 个）。</summary>
    public IReadOnlyList<NmSenderActivity> Senders { get; init; } = Array.Empty<NmSenderActivity>();

    /// <summary><see cref="Senders"/> 是否被截断。</summary>
    public bool SendersTruncated { get; init; }
}

/// <summary>相位的稳定 snake_case 名称（导出/AI 契约）；中文文案只在 UI 层。</summary>
public static class NmSilencePhaseText
{
    public static string PhaseName(NmSilencePhaseKind kind) => kind switch
    {
        NmSilencePhaseKind.ReleaseWait => "release_wait",
        NmSilencePhaseKind.PrepareSleep => "prepare_sleep",
        NmSilencePhaseKind.Sleep => "sleep",
        _ => "unknown",
    };

    public static string BasisName(NmSilenceBasis basis) => basis switch
    {
        NmSilenceBasis.NmSenderStillActive => "nm_sender_still_active",
        NmSilenceBasis.NmSenderStoppedAfterDrain => "nm_sender_stopped_after_drain",
        NmSilenceBasis.OtherSenderActive => "other_sender_active",
        NmSilenceBasis.NoBusActivity => "no_bus_activity",
        NmSilenceBasis.Unattributed => "unattributed",
        NmSilenceBasis.NmStsRepeatMessage => "nmsts_repeat_message",
        NmSilenceBasis.AppTxAfterTimeout => "app_tx_after_timeout",
        NmSilenceBasis.WaitBusSleepDefaulted => "wait_bus_sleep_defaulted",
        NmSilenceBasis.WindowTooShort => "window_too_short",
        _ => "unknown",
    };
}
