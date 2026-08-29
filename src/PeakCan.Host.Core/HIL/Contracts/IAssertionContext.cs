namespace PeakCan.HIL.Core.HIL.Contracts;

/// <summary>
/// Real-time assertion context. Implemented by Infrastructure layer, bridges ChannelRouter frame stream.
/// Subscription model: Subscribe returns IDisposable, Dispose cancels subscription.
/// 
/// IDisposable contract:
/// 1. Idempotent (multiple Dispose calls harmless).
/// 2. After Dispose returns, callback will NOT be invoked again.
/// 3. Dispose does not block a callback currently executing (uses volatile flag).
/// 4. Remaining queued frames are drained before consumer thread exits (5s timeout then forced cancel).
/// </summary>
public interface IAssertionContext
{
    /// <summary>
    /// Subscribe to decoded frame stream. Callback fires when frame is decoded
    /// (implementation guarantees frame and signals snapshot are consistent).
    /// Callback invoked on a dedicated consumer thread (NOT the sink thread).
    /// Returns IDisposable; Dispose cancels subscription.
    /// </summary>
    IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame);

    /// <summary>
    /// Get last-decoded value of a signal (global cache across all frames).
    /// Format: "MessageName.SignalName" (e.g. "BMS_Status.EngineRPM").
    /// Returns null if signal not found, never decoded, or age exceeds maxAgeMs.
    /// maxAgeMs=0 disables staleness check (always returns last value).
    /// </summary>
    double? GetSignalValue(string signalName, int maxAgeMs = 5000);

    /// <summary>
    /// Current timestamp in microseconds (matches CanFrame.Timestamp.TotalMicroseconds baseline).
    /// </summary>
    double CurrentTimestamp { get; }

    /// <summary>
    /// Send frame to bus. Returns failed Result on error (never throws).
    /// </summary>
    ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct);

    /// <summary>
    /// Get recent frames (decoded). Used by WaitForFrame to check if a frame
    /// already arrived before subscription (avoids race condition with fast ECUs).
    /// </summary>
    IReadOnlyList<DecodedFrame> GetRecentDecodedFrames();

    // ── Multi-channel overloads (DIM default = ignore channelName, forward to single-channel) ──

    /// <summary>按逻辑名路由发送（channelName null/空 = 默认/唯一通道）。</summary>
    ValueTask<Result<Unit>> SendFrameAsync(string? channelName, CanFrame frame, CancellationToken ct)
        => SendFrameAsync(frame, ct);

    /// <summary>按逻辑名订阅解码帧流。</summary>
    IDisposable SubscribeDecodedFrames(string? channelName, Action<DecodedFrame> onFrame)
        => SubscribeDecodedFrames(onFrame);

    /// <summary>按通道桶查最近帧。</summary>
    IReadOnlyList<DecodedFrame> GetRecentDecodedFrames(string? channelName)
        => GetRecentDecodedFrames();

    /// <summary>按逻辑通道取信号快照。channelName null/空 = 默认通道；未知名 -> 抛 KeyNotFoundException（与 GetRecentDecodedFrames(string?) 一致）。</summary>
    /// <remarks>
    /// DIM 默认：忽略 channelName 转发单通道版（与既有三兄弟 DIM 一致，单通道 context 语义正确）。
    /// 注意：非通道感知实现传非 null channelName 不抛异常——2026-08-28 review HIGH 修正（原"非 null 抛
    /// NotSupportedException"会被 ConsumerLoop 的 per-subscriber catch 吞掉 → 静默"No samples"假失败，比
    /// 静默错更糟）。多通道感知实现必须显式 override 按名路由（MultiChannelAssertionContext/SingleChannelContext）。
    /// </remarks>
    double? GetSignalValue(string? channelName, string signalName, int maxAgeMs = 5000)
        => GetSignalValue(signalName, maxAgeMs);
}
