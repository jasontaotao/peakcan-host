namespace PeakCan.Host.Core.HIL.Contracts;

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
    /// Returns null if signal not found or never decoded.
    /// </summary>
    double? GetSignalValue(string signalName);

    /// <summary>
    /// Current timestamp in microseconds (matches CanFrame.Timestamp.TotalMicroseconds baseline).
    /// </summary>
    double CurrentTimestamp { get; }

    /// <summary>
    /// Send frame to bus. Returns failed Result on error (never throws).
    /// </summary>
    ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct);
}
