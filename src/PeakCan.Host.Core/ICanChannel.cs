namespace PeakCan.HIL.Core;

/// <summary>
/// One CAN channel (one PCAN-USB handle). Owns the connect/disconnect lifecycle
/// and exposes an asynchronous read path via <see cref="FrameReceived"/>.
/// <para>
/// The <see cref="FrameReceived"/> event is raised on a background read loop
/// (the SDK's <c>PCANBasic.Read</c> polling thread or its event-driven
/// <c>SetRcvEvent</c> equivalent) — consumers must marshal back to the UI
/// thread themselves. Subscribers should not throw; the
/// <c>ChannelRouter</c> isolates per-sink exceptions.
/// </para>
/// <para>
/// A channel is single-use: after <see cref="IAsyncDisposable.DisposeAsync"/>
/// the internal <c>CancellationTokenSource</c> is gone and any further
/// <see cref="ConnectAsync"/> call will throw <see cref="ObjectDisposedException"/>.
/// </para>
/// </summary>
public interface ICanChannel : IAsyncDisposable
{
    /// <summary>Stable channel handle (PCAN_USBBUS1 etc.).</summary>
    ChannelId Id { get; }

    /// <summary>True after a successful <see cref="ConnectAsync"/> until <see cref="DisconnectAsync"/>.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// Initialize the channel at <paramref name="baud"/>. Sets
    /// <see cref="IsConnected"/> to true on success; on failure returns a
    /// <see cref="Result{T}"/> with the mapped PEAK error code.
    /// </summary>
    Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default);

    /// <summary>Stop the read loop and call <c>PCANBasic.Uninitialize</c>. Idempotent.</summary>
    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Transmit one <paramref name="frame"/>. Returns a failed <see cref="Result{T}"/>
    /// when not connected or when PEAK reports an error.
    /// </summary>
    ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default);

    /// <summary>
    /// Fired by the read loop for every received frame. Subscribers must be
    /// thread-safe and must not block (the read loop continues immediately).
    /// </summary>
    event Action<CanFrame>? FrameReceived;

    /// <summary>
    /// Fired when the read loop encounters an exception from the underlying
    /// SDK read calls, or when it gives up after
    /// <c>MaxConsecutiveReadFailures</c> consecutive failures.
    /// <para>
    /// v3.16.9.4 PATCH: surfaces bus-off / driver-unload / hardware faults
    /// to the UI layer. Previously the read loop only logged the exception
    /// via <c>ILogger</c> (Serilog file in production); the user saw a
    /// "connected but no frames" state with no explanation. Subscribers
    /// (typically <c>AppShellViewModel</c>) update <c>StatusMessage</c> so
    /// the operator sees the fault.
    /// </para>
    /// <para>
    /// Raised on the read-loop thread. Subscribers must marshal to the UI
    /// thread themselves (matches the contract on <see cref="FrameReceived"/>).
    /// </para>
    /// </summary>
    event Action<ReadLoopError>? ReadLoopError;
}

/// <summary>
/// Payload for <see cref="ICanChannel.ReadLoopError"/>. Distinguishes
/// recoverable per-iteration failures (Classic/Fd exception) from the
/// fatal give-up state (<see cref="Kind"/> = <see cref="ReadLoopErrorKind.LoopGivingUp"/>).
/// </summary>
/// <param name="Handle">PCAN handle where the failure occurred.</param>
/// <param name="Kind">Where in the read loop the error happened.</param>
/// <param name="Exception">Underlying exception (null for LoopGivingUp, where
/// the loop has already abandoned; the cumulative failure count is logged).</param>
public readonly record struct ReadLoopError(
    ushort Handle,
    ReadLoopErrorKind Kind,
    Exception? Exception);

/// <summary>
/// Where the read loop failed. <see cref="ClassicReadException"/> and
/// <see cref="FdReadException"/> are per-iteration recoverable errors;
/// <see cref="LoopGivingUp"/> fires once when the loop exits after the
/// consecutive-failure cap is hit.
/// </summary>
public enum ReadLoopErrorKind
{
    /// <summary>PCANBasic.Read threw on this iteration.</summary>
    ClassicReadException,
    /// <summary>PCANBasic.ReadFD threw on this iteration.</summary>
    FdReadException,
    /// <summary>Read loop exited after MaxConsecutiveReadFailures reached.</summary>
    LoopGivingUp,
}

