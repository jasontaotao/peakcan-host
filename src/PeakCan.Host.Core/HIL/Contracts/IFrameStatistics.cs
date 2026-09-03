namespace PeakCan.HIL.Core.HIL.Contracts;

/// <summary>
/// 帧统计基础设施（Phase B）。按 CAN ID 统计 FrameReceived 事件，
/// 时间基准为 collector 内部单调时钟（<c>System.Environment.TickCount64</c>，单位 ms），
/// 不依赖帧的 <c>Timestamp</c> 字段——硬件/回放模式下语义一致。
/// 窗口边界由调用方打点派生（前向语义），而非"最近 window"回看。
/// </summary>
public interface IFrameStatistics
{
    /// <summary>单调时钟基准（collector 内部打点）。窗口边界由此派生。</summary>
    long Now { get; }

    /// <summary>[since, now] 区间内指定 CAN ID 的帧计数。</summary>
    int CountSince(CanId id, long since, long now);

    /// <summary>[since, now] 区间内指定 CAN ID 的帧计数（按通道路由）。</summary>
    int CountSince(CanId id, long since, long now, string? channelName = null)
        => CountSince(id, since, now);

    /// <summary>[since, now] 区间内的帧间隔统计；样本不足时 SampleCount 如实反映。</summary>
    FrameIntervalStats GetIntervalStats(CanId id, long since, long now);

    /// <summary>[since, now] 区间内的帧间隔统计（按通道路由）。</summary>
    FrameIntervalStats GetIntervalStats(CanId id, long since, long now, string? channelName = null)
        => GetIntervalStats(id, since, now);
}

public sealed record FrameIntervalStats(
    int SampleCount,
    double MinMs,
    double MaxMs,
    double MeanMs,
    double JitterMs); // max - min
