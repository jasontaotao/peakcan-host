namespace PeakCan.HIL.Core.J1939;

/// <summary>
/// J1939TP 层参数（spec §5.4；默认值实现时以 J1939-21 §5.10 核对）。
/// <para><see cref="CtsMaxPackets"/> 是接收方本地策略：0 = 每个 CTS 放行全部剩余包
/// （我们发出的 CTS 携带 ≥1 的包数）；线上 CTS 包数=0 是对端的 hold 语义，发送方按 T4 等待。</para>
/// </summary>
public sealed record J1939TpOptions
{
    /// <summary>接收方等下一 TP.DT 上限（ms）。</summary>
    public int T1Ms { get; init; } = 750;

    /// <summary>发送方等 CTS / EOM_ACK 上限（ms）。</summary>
    public int T3Ms { get; init; } = 1250;

    /// <summary>CTS hold（包数=0）后等待下一 CTS 上限（ms）。</summary>
    public int T4Ms { get; init; } = 1050;

    /// <summary>BAM 的 TP.DT 帧间隔（ms，J1939-21 允许 50–200）。</summary>
    public int BamIntervalMs { get; init; } = 50;

    /// <summary>接收方每 CTS 放行包数；0 = 放行全部剩余。</summary>
    public byte CtsMaxPackets { get; init; } = 0;

    /// <summary>发送方 RTS 中宣告的"每 CTS 最大包数"；0xFF = 不限制。</summary>
    public byte RtsMaxPacketsPerCts { get; init; } = 0xFF;

    /// <summary>载荷长度防御上限（1785 = 255×7）。</summary>
    public int MaxPayloadBytes { get; init; } = 1785;

    /// <summary>并发接收会话上限（超限 LRU 驱逐，防 fuzz 打爆内存）。</summary>
    public int MaxConcurrentSessions { get; init; } = 32;

    /// <summary>收到指向本机地址的 RTS 时自动回 CTS（本地地址集合为空时无效果）。</summary>
    public bool AutoRespondToRts { get; init; } = true;

    /// <summary>离线模式（回放分析）：不启 watchdog、禁止一切主动发送、完整性判定移交调用方。</summary>
    public bool OfflineMode { get; init; } = false;

    /// <summary>回放分析专用的离线配置。</summary>
    public static J1939TpOptions Offline => new() { OfflineMode = true };
}
